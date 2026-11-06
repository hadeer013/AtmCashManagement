using System.Net;
using System.Net.Http.Json;
using Atm.Business.Common;
using Atm.Business.Contracts;
using Atm.DataAccess.BackgroundJobs;
using Atm.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Atm.IntegrationTests.Infrastructure.AtmApiClient;

namespace Atm.IntegrationTests;

/// <summary>Lifecycle (B2), deposits (B7), ledger (B8), reconciliation (B9), alerts (B10) and the error model.</summary>
[Collection(ApiCollection.Name)]
public class OperationsTests(AtmApiFactory factory)
{
    private readonly AtmApiClient _api = new(factory.CreateClient());

    private static List<CountedCassetteDto> Counts(AtmResponse atm, Func<int, int, int>? adjust = null)
        => atm.Cassettes.OrderBy(c => c.Slot).Select(c => c.Type == CassetteType.Deposit
                ? new CountedCassetteDto(c.Slot, null, c.Notes.Select(n => new CountedNotesDto(n.Currency, n.Denomination, n.Count)).ToList())
                : new CountedCassetteDto(c.Slot, adjust?.Invoke(c.Slot, c.Count) ?? c.Count, null))
            .ToList();

    [Fact]
    public async Task Invalid_transitions_and_wrong_state_operations_are_rejected_with_meaningful_errors()
    {
        var atm = await _api.CreateAtmAsync();
        await AssertProblemAsync(await _api.SetStateRawAsync(atm.Id, AtmState.InService), HttpStatusCode.Conflict, ErrorCodes.InvalidStateTransition);

        await _api.ConfigureAsync(atm.Id);
        await _api.LoadAsync(atm.Id, (2, 10));
        var problem = await AssertProblemAsync(await _api.QuoteRawAsync(atm.Id, NewCard(), Currency.TRY, 100), HttpStatusCode.Conflict, ErrorCodes.OperationNotAllowedInState);
        Assert.Equal("LOADED", problem.GetProperty("currentState").GetString());

        await AssertProblemAsync(await _api.SetStateRawAsync(atm.Id, AtmState.Maintenance), HttpStatusCode.Conflict, ErrorCodes.InvalidStateTransition);
        await _api.SetStateAsync(atm.Id, AtmState.InService);

        var load = await _api.Http.PostAsJsonAsync($"/api/atms/{atm.Id}/cash-loads", new LoadCashRequest([new CashLoadItemDto(2, 5)]), Json);
        await AssertProblemAsync(load, HttpStatusCode.Conflict, ErrorCodes.OperationNotAllowedInState);
    }

    [Fact]
    public async Task Switching_to_maintenance_cancels_active_quotes_and_releases_notes()
    {
        var atmId = await _api.CreateInServiceAtmAsync(null, (2, 10));
        var quote = await _api.QuoteAsync(atmId, NewCard(), Currency.TRY, 500);

        var atm = await _api.SetStateAsync(atmId, AtmState.Maintenance);

        Assert.Equal(0, Reserved(atm, 2));
        Assert.Equal(QuoteStatus.Cancelled, (await _api.GetQuoteAsync(atmId, quote.QuoteId)).Status);
        await AssertProblemAsync(await _api.ConfirmRawAsync(atmId, quote.QuoteId, Guid.NewGuid().ToString()), HttpStatusCode.Conflict, ErrorCodes.QuoteCancelled);
    }

    [Fact]
    public async Task Invalid_input_returns_problem_details()
    {
        var atmId = await _api.CreateInServiceAtmAsync(null, (2, 10));

        var badCurrency = await _api.Http.PostAsync(
            $"/api/atms/{atmId}/withdrawal-quotes",
            new StringContent("""{"cardNumber":"4111111111111111","currency":"GBP","amount":100}""", System.Text.Encoding.UTF8, "application/json"));
        await AssertProblemAsync(badCurrency, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        await AssertProblemAsync(await _api.QuoteRawAsync(atmId, NewCard(), Currency.TRY, -5), HttpStatusCode.BadRequest, ErrorCodes.InvalidAmount);
        await AssertProblemAsync(await _api.QuoteRawAsync(atmId, "12", Currency.TRY, 100), HttpStatusCode.BadRequest, ErrorCodes.InvalidCardNumber);
        await AssertProblemAsync(await _api.QuoteRawAsync(atmId, NewCard(), Currency.EUR, 100), HttpStatusCode.UnprocessableEntity, ErrorCodes.CurrencyNotAvailable);
        await AssertProblemAsync(await _api.Http.GetAsync($"/api/atms/{Guid.NewGuid()}"), HttpStatusCode.NotFound, ErrorCodes.AtmNotFound);

        var badConfig = await _api.Http.PutAsJsonAsync(
            $"/api/atms/{(await _api.CreateAtmAsync()).Id}/cassettes",
            new ConfigureCassettesRequest(DefaultConfig(slot5: (Currency.TRY, 500))), Json);
        await AssertProblemAsync(badConfig, HttpStatusCode.BadRequest, ErrorCodes.InvalidCassetteConfiguration);
    }

    [Fact]
    public async Task Deposit_overflow_goes_to_deposit_cassette_then_back_to_customer()
    {
        var atmId = await _api.CreateInServiceAtmAsync(null, (2, 98));
        await _api.DepositAsync(atmId, 0, (Currency.EUR, 20, 95)); // fills the deposit cassette to 95/100

        var deposit = await _api.DepositAsync(atmId, 3, (Currency.TRY, 100, 10));

        Assert.Contains(deposit.Accepted, a => a is { CassetteSlot: 2, CassetteType: CassetteType.Recycling, Count: 2 });
        Assert.Contains(deposit.Accepted, a => a is { CassetteSlot: 1, CassetteType: CassetteType.Deposit, Count: 5 });
        var returned = Assert.Single(deposit.Returned.Notes);
        Assert.Equal((Currency.TRY, 100, 3, "CASSETTE_FULL"), (returned.Currency, returned.Denomination, returned.Count, returned.Reason));
        Assert.Equal(3, deposit.Returned.UnrecognizedCount);
        Assert.Equal(6, deposit.Returned.TotalReturnedNotes);
        Assert.Equal(700, deposit.AcceptedTotals.Single(t => t.Currency == Currency.TRY).Amount);

        var atm = await _api.GetAtmAsync(atmId);
        Assert.Equal((100, 100), (Count(atm, 1), Count(atm, 2)));
    }

    [Fact]
    public async Task Reconciliation_over_threshold_needs_a_different_user_before_returning_to_service()
    {
        var atmId = await _api.CreateInServiceAtmAsync(null, (2, 50), (5, 10));
        await _api.SetStateAsync(atmId, AtmState.Maintenance);
        var before = await _api.GetAtmAsync(atmId);

        // 12 x TRY 100 short = TRY 1,200 > 1,000.
        var created = await ReadAsync<ReconciliationResponse>(
            await _api.ReconcileRawAsync(atmId, "alice", Counts(before, (slot, count) => slot == 2 ? count - 12 : count)),
            HttpStatusCode.Created);
        Assert.Equal(ReconciliationStatus.AwaitingApproval, created.Status);
        Assert.Equal(1200m, created.TotalAbsoluteDifferenceTry);
        Assert.Equal(50, Count(await _api.GetAtmAsync(atmId), 2)); // nothing applied yet

        await AssertProblemAsync(await _api.SetStateRawAsync(atmId, AtmState.InService), HttpStatusCode.Conflict, ErrorCodes.ReconciliationPending);
        await AssertProblemAsync(await _api.DecideRawAsync(created.Id, "approve", null), HttpStatusCode.BadRequest, ErrorCodes.UserHeaderRequired);
        await AssertProblemAsync(await _api.DecideRawAsync(created.Id, "approve", "alice"), HttpStatusCode.Forbidden, ErrorCodes.SameUserApproval);

        var approved = await ReadAsync<ReconciliationResponse>(await _api.DecideRawAsync(created.Id, "approve", "bob"));
        Assert.Equal(ReconciliationStatus.Approved, approved.Status);
        Assert.Equal("bob", approved.DecidedBy);
        Assert.Equal(38, Count(await _api.GetAtmAsync(atmId), 2));

        await _api.SetStateAsync(atmId, AtmState.InService);
        var adjustments = await ReadAsync<List<MovementResponse>>(
            await _api.Http.GetAsync($"/api/atms/{atmId}/movements?type=RECONCILIATION_ADJUSTMENT"));
        var adjustment = Assert.Single(adjustments);
        Assert.Equal((-12, "bob"), (adjustment.Quantity, adjustment.PerformedBy));
        Assert.True((await _api.ConsistencyAsync(atmId)).IsConsistent);
    }

    [Fact]
    public async Task Foreign_currency_differences_are_valued_in_try_with_the_rate_table()
    {
        var atmId = await _api.CreateInServiceAtmAsync(null, (5, 20));
        await _api.SetStateAsync(atmId, AtmState.Maintenance);
        var before = await _api.GetAtmAsync(atmId);

        // One USD 100 note missing: 100 x 41.50 = TRY 4,150 → needs approval.
        var created = await ReadAsync<ReconciliationResponse>(
            await _api.ReconcileRawAsync(atmId, "alice", Counts(before, (slot, count) => slot == 5 ? count - 1 : count)),
            HttpStatusCode.Created);

        Assert.Equal(ReconciliationStatus.AwaitingApproval, created.Status);
        Assert.Equal(4150m, created.TotalAbsoluteDifferenceTry);

        var rejected = await ReadAsync<ReconciliationResponse>(await _api.DecideRawAsync(created.Id, "reject", "bob"));
        Assert.Equal(ReconciliationStatus.Rejected, rejected.Status);
        Assert.Equal(20, Count(await _api.GetAtmAsync(atmId), 5));
    }

    [Fact]
    public async Task Low_cash_alert_is_written_to_the_outbox_and_processed_by_the_background_service()
    {
        var atmId = await _api.CreateInServiceAtmAsync(null, (2, 12), (3, 50), (4, 50), (5, 50));
        await _api.QuoteAsync(atmId, NewCard(), Currency.TRY, 300); // TRY 100 available: 12 → 9 (< 10%)

        var pending = await factory.WithDbAsync(db => db.OutboxMessages.AsNoTracking()
            .Where(m => m.ProcessedAt == null && m.Payload.Contains(atmId.ToString()))
            .ToListAsync());
        var alert = Assert.Single(pending);
        Assert.Equal("LowCashAlert", alert.Type);
        Assert.Contains("\"cassetteSlot\":2", alert.Payload);

        await factory.Services.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(CancellationToken.None);

        var processed = await factory.WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == alert.Id));
        Assert.NotNull(processed.ProcessedAt);
        Assert.Equal(1, processed.Attempts);

        var report = await _api.ReportAsync(atmId);
        var slot2 = report.Atms.Single().Cassettes.Single(c => c.Slot == 2);
        Assert.Equal((12, 3, 9, true), (slot2.Count, slot2.Reserved, slot2.Available, slot2.LowCash));
    }

    [Fact]
    public async Task Ledger_rows_are_immutable_and_the_consistency_check_detects_tampering()
    {
        var atmId = await _api.CreateInServiceAtmAsync(null, (2, 10));

        var update = await Assert.ThrowsAsync<SqlException>(() => factory.WithDbAsync(db =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.CassetteMovements SET Quantity = 99 WHERE AtmId = {atmId}")));
        Assert.Contains("immutable", update.Message);
        await Assert.ThrowsAsync<SqlException>(() => factory.WithDbAsync(db =>
            db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM dbo.CassetteMovements WHERE AtmId = {atmId}")));

        // Tamper with a stored balance directly: the ledger no longer agrees and the check reports it.
        await factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE b SET b.[Count] = 7 FROM dbo.CassetteBalances b JOIN dbo.Cassettes c ON c.Id = b.CassetteId WHERE c.AtmId = {atmId} AND c.Slot = 2"));

        var report = await _api.ConsistencyAsync(atmId);
        Assert.False(report.IsConsistent);
        var difference = Assert.Single(report.Differences);
        Assert.Equal((2, 7, 10), (difference.CassetteSlot, difference.StoredCount, difference.LedgerCount));
    }
}
