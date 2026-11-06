using System.Net;
using Atm.Business.Common;
using Atm.Business.Contracts;
using Atm.IntegrationTests.Infrastructure;
using static Atm.IntegrationTests.Infrastructure.AtmApiClient;

namespace Atm.IntegrationTests;

[Collection(ApiCollection.Name)]
public class EndToEndScenarioTests(AtmApiFactory factory)
{
    private readonly AtmApiClient _api = new(factory.CreateClient());

    [Fact]
    public async Task Full_scenario_create_configure_load_serve_withdraw_deposit_reconcile_report()
    {
        // create ATM → configure cassettes → load cash → put in service
        var atm = await _api.CreateAtmAsync();
        Assert.Equal(AtmState.Unconfigured, atm.State);
        atm = await _api.ConfigureAsync(atm.Id);
        Assert.Equal(AtmState.Configured, atm.State);
        atm = await _api.LoadAsync(atm.Id, (2, 50), (3, 40), (4, 60), (5, 30));
        Assert.Equal(AtmState.Loaded, atm.State);
        atm = await _api.SetStateAsync(atm.Id, AtmState.InService);
        Assert.Equal(AtmState.InService, atm.State);

        // request quote → confirm
        var card = NewCard();
        var quote = await _api.QuoteAsync(atm.Id, card, Currency.TRY, 370);
        Assert.True(quote.ExactMatch);
        var exact = Assert.Single(quote.Options);
        Assert.Equal(QuoteOptionType.Exact, exact.Type);
        Assert.Equal([(100, 3), (50, 1), (20, 1)], exact.Notes.Select(n => (n.Denomination, n.Count)));
        Assert.Equal(20_000 - 370, quote.RemainingDailyLimit);

        var withdrawal = await ReadAsync<WithdrawalResponse>(await _api.ConfirmRawAsync(atm.Id, quote.QuoteId, Guid.NewGuid().ToString()));
        Assert.Equal(370, withdrawal.Amount);
        Assert.Equal(5, withdrawal.NoteCount);

        // deposit
        var deposit = await _api.DepositAsync(atm.Id, 2, (Currency.TRY, 100, 4), (Currency.EUR, 50, 3));
        Assert.Contains(deposit.Accepted, a => a is { CassetteSlot: 2, Count: 4 });
        Assert.Contains(deposit.Accepted, a => a is { CassetteSlot: 1, CassetteType: CassetteType.Deposit, Currency: Currency.EUR, Count: 3 });
        Assert.Equal(2, deposit.Returned.UnrecognizedCount);

        // switch to maintenance → reconcile (one TRY 50 note short: 50 TRY ≤ 1,000 → applied immediately)
        await _api.SetStateAsync(atm.Id, AtmState.Maintenance);
        var current = await _api.GetAtmAsync(atm.Id);
        Assert.Equal(50 - 3 + 4, Count(current, 2));
        var reconciliationResponse = await _api.ReconcileRawAsync(atm.Id, "alice",
        [
            new CountedCassetteDto(1, null, [new CountedNotesDto(Currency.EUR, 50, 3)]),
            new CountedCassetteDto(2, Count(current, 2), null),
            new CountedCassetteDto(3, Count(current, 3) - 1, null),
            new CountedCassetteDto(4, Count(current, 4), null),
            new CountedCassetteDto(5, Count(current, 5), null)
        ]);
        var reconciliation = await ReadAsync<ReconciliationResponse>(reconciliationResponse, HttpStatusCode.Created);
        Assert.Equal(ReconciliationStatus.Applied, reconciliation.Status);
        Assert.Equal(50m, reconciliation.TotalAbsoluteDifferenceTry);

        // report
        var report = await _api.ReportAsync(atm.Id);
        var atmReport = Assert.Single(report.Atms);
        var tryTotal = atmReport.Totals.Single(t => t.Currency == Currency.TRY);
        Assert.Equal(51 * 100 + 38 * 50 + 59 * 20, tryTotal.Amount);
        Assert.Equal(30 * 100, atmReport.Totals.Single(t => t.Currency == Currency.USD).Amount);
        Assert.Equal(150, atmReport.Totals.Single(t => t.Currency == Currency.EUR).Amount);
        Assert.True(atmReport.TotalTry > tryTotal.Amount);
        Assert.All(atmReport.Cassettes, c => Assert.Equal(c.Count - c.Reserved, c.Available));

        // the ledger reproduces every balance
        var consistency = await _api.ConsistencyAsync(atm.Id);
        Assert.True(consistency.IsConsistent);
        Assert.Empty(consistency.Differences);
    }

    [Fact]
    public async Task Health_and_swagger_are_available()
    {
        var health = await _api.Http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Contains("Healthy", await health.Content.ReadAsStringAsync());

        var swagger = await _api.Http.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, swagger.StatusCode);
    }
}
