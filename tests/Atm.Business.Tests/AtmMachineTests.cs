using Atm.Business.Common;
using Atm.Business.Domain;
using Atm.Business.Security;
using Microsoft.Extensions.Options;

namespace Atm.Business.Tests;

public class AtmMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
    private static OperationContext Ctx(string type = "TEST") => new(Now, "tester", type, Guid.NewGuid());

    private static List<CassetteConfiguration> StandardConfig(
        (Currency Currency, int Denomination) slot2,
        (Currency Currency, int Denomination) slot3,
        (Currency Currency, int Denomination) slot4,
        (Currency Currency, int Denomination) slot5) =>
    [
        new(1, CassetteType.Deposit, null, null),
        new(2, CassetteType.Recycling, slot2.Currency, slot2.Denomination),
        new(3, CassetteType.Recycling, slot3.Currency, slot3.Denomination),
        new(4, CassetteType.Recycling, slot4.Currency, slot4.Denomination),
        new(5, CassetteType.Recycling, slot5.Currency, slot5.Denomination)
    ];

    private static List<CassetteConfiguration> DefaultConfig()
        => StandardConfig((Currency.TRY, 100), (Currency.TRY, 50), (Currency.TRY, 20), (Currency.USD, 100));

    private static AtmMachine InService(List<CassetteConfiguration>? config, params (int Slot, int Count)[] loads)
    {
        var atm = AtmMachine.Create("ATM-T", null, Now);
        atm.ConfigureCassettes(config ?? DefaultConfig(), Ctx());
        atm.LoadCash(loads.Select(l => new CashLoadItem(l.Slot, l.Count)).ToList(), Ctx());
        atm.TransitionTo(AtmState.InService, hasPendingReconciliation: false, Ctx());
        return atm;
    }

    // ------------------------------------------------------------------ configuration (A3–A6)

    [Fact]
    public void Valid_configuration_moves_atm_to_configured()
    {
        var atm = AtmMachine.Create("ATM-1", "Kadikoy", Now);

        atm.ConfigureCassettes(DefaultConfig(), Ctx());

        Assert.Equal(AtmState.Configured, atm.State);
        Assert.Equal(5, atm.Cassettes.Count);
        Assert.Single(atm.Cassettes, c => c.Type == CassetteType.Deposit);
    }

    [Fact]
    public void Configuration_without_foreign_currency_is_rejected()
    {
        var config = StandardConfig((Currency.TRY, 100), (Currency.TRY, 50), (Currency.TRY, 20), (Currency.TRY, 500));

        var ex = Assert.Throws<ValidationException>(() => AtmMachine.ValidateConfiguration(config));
        Assert.Equal(ErrorCodes.InvalidCassetteConfiguration, ex.Code);
    }

    [Fact]
    public void Configuration_with_three_foreign_cassettes_is_rejected()
    {
        var config = StandardConfig((Currency.TRY, 100), (Currency.USD, 50), (Currency.EUR, 20), (Currency.USD, 100));

        Assert.Throws<ValidationException>(() => AtmMachine.ValidateConfiguration(config));
    }

    [Fact]
    public void Configuration_with_usd_and_eur_is_allowed()
    {
        var config = StandardConfig((Currency.TRY, 100), (Currency.TRY, 50), (Currency.EUR, 50), (Currency.USD, 100));

        AtmMachine.ValidateConfiguration(config);
    }

    [Fact]
    public void Configuration_needs_exactly_one_deposit_cassette()
    {
        var config = DefaultConfig();
        config[1] = new CassetteConfiguration(2, CassetteType.Deposit, null, null);

        Assert.Throws<ValidationException>(() => AtmMachine.ValidateConfiguration(config));
    }

    [Fact]
    public void Configuration_rejects_unsupported_denomination()
    {
        var config = DefaultConfig();
        config[1] = new CassetteConfiguration(2, CassetteType.Recycling, Currency.TRY, 200);

        Assert.Throws<ValidationException>(() => AtmMachine.ValidateConfiguration(config));
    }

    [Fact]
    public void Non_empty_cassette_cannot_be_reconfigured()
    {
        var atm = InService(null, (2, 50));
        atm.TransitionTo(AtmState.Maintenance, false, Ctx());
        var config = StandardConfig((Currency.TRY, 500), (Currency.TRY, 50), (Currency.TRY, 20), (Currency.USD, 100));

        var ex = Assert.Throws<ConflictException>(() => atm.ConfigureCassettes(config, Ctx()));
        Assert.Equal(ErrorCodes.CassetteNotEmpty, ex.Code);
    }

    // ------------------------------------------------------------------ loading (A3, A4, A7)

    [Fact]
    public void Cash_cannot_be_loaded_into_the_deposit_cassette()
    {
        var atm = AtmMachine.Create("ATM-1", null, Now);
        atm.ConfigureCassettes(DefaultConfig(), Ctx());

        var ex = Assert.Throws<BusinessRuleException>(() => atm.LoadCash([new CashLoadItem(1, 10)], Ctx()));
        Assert.Equal(ErrorCodes.DepositCassetteNotLoadable, ex.Code);
    }

    [Fact]
    public void Cassette_capacity_of_100_notes_is_enforced()
    {
        var atm = AtmMachine.Create("ATM-1", null, Now);
        atm.ConfigureCassettes(DefaultConfig(), Ctx());
        atm.LoadCash([new CashLoadItem(2, 60)], Ctx());

        var ex = Assert.Throws<BusinessRuleException>(() => atm.LoadCash([new CashLoadItem(2, 41)], Ctx()));
        Assert.Equal(ErrorCodes.CassetteCapacityExceeded, ex.Code);
    }

    // ------------------------------------------------------------------ lifecycle (B2)

    [Fact]
    public void Unconfigured_atm_cannot_go_into_service()
    {
        var atm = AtmMachine.Create("ATM-1", null, Now);

        var ex = Assert.Throws<ConflictException>(() => atm.TransitionTo(AtmState.InService, false, Ctx()));
        Assert.Equal(ErrorCodes.InvalidStateTransition, ex.Code);
    }

    [Fact]
    public void Full_lifecycle_follows_the_state_machine()
    {
        var atm = AtmMachine.Create("ATM-1", null, Now);
        Assert.Equal(AtmState.Unconfigured, atm.State);

        atm.ConfigureCassettes(DefaultConfig(), Ctx());
        Assert.Equal(AtmState.Configured, atm.State);

        atm.LoadCash([new CashLoadItem(2, 10)], Ctx());
        Assert.Equal(AtmState.Loaded, atm.State);

        atm.TransitionTo(AtmState.InService, false, Ctx());
        Assert.Equal(AtmState.InService, atm.State);

        atm.TransitionTo(AtmState.Maintenance, false, Ctx());
        Assert.Equal(AtmState.Maintenance, atm.State);

        atm.TransitionTo(AtmState.InService, false, Ctx());
        Assert.Equal(AtmState.InService, atm.State);
    }

    [Fact]
    public void Cash_cannot_be_loaded_while_in_service()
    {
        var atm = InService(null, (2, 10));

        var ex = Assert.Throws<ConflictException>(() => atm.LoadCash([new CashLoadItem(2, 10)], Ctx()));
        Assert.Equal(ErrorCodes.OperationNotAllowedInState, ex.Code);
    }

    [Fact]
    public void Deposit_is_rejected_in_maintenance()
    {
        var atm = InService(null, (2, 10));
        atm.TransitionTo(AtmState.Maintenance, false, Ctx());

        var ex = Assert.Throws<ConflictException>(() => atm.AcceptDeposit([new DepositNoteInput(Currency.TRY, 100, 1)], Ctx()));
        Assert.Equal(ErrorCodes.OperationNotAllowedInState, ex.Code);
    }

    [Fact]
    public void Pending_reconciliation_blocks_return_to_service()
    {
        var atm = InService(null, (2, 10));
        atm.TransitionTo(AtmState.Maintenance, false, Ctx());

        var ex = Assert.Throws<ConflictException>(() => atm.TransitionTo(AtmState.InService, hasPendingReconciliation: true, Ctx()));
        Assert.Equal(ErrorCodes.ReconciliationPending, ex.Code);
    }

    // ------------------------------------------------------------------ reservation / dispensing (B2, B4)

    [Fact]
    public void Same_denomination_in_two_cassettes_dispenses_from_higher_fill_ratio_first()
    {
        var config = StandardConfig((Currency.TRY, 100), (Currency.TRY, 100), (Currency.TRY, 20), (Currency.USD, 100));
        var atm = InService(config, (2, 30), (3, 80));

        var reserved = atm.ReserveNotes(Currency.TRY, new Dictionary<int, int> { [100] = 85 }, Ctx());

        Assert.Equal(3, reserved[0].CassetteSlot);
        Assert.Equal(80, reserved[0].Quantity);
        Assert.Equal(2, reserved[1].CassetteSlot);
        Assert.Equal(5, reserved[1].Quantity);
    }

    [Fact]
    public void Reserved_notes_are_no_longer_available_for_other_quotes()
    {
        var atm = InService(null, (2, 10));

        atm.ReserveNotes(Currency.TRY, new Dictionary<int, int> { [100] = 7 }, Ctx());

        Assert.Equal(3, atm.GetAvailableNotes(Currency.TRY)[100]);
        Assert.Throws<InvalidOperationException>(() =>
            atm.ReserveNotes(Currency.TRY, new Dictionary<int, int> { [100] = 4 }, Ctx()));
    }

    [Fact]
    public void Dispense_debits_the_reserved_cassettes_and_releases_the_rest()
    {
        var atm = InService(null, (2, 10), (4, 10));
        var reserved = atm.ReserveNotes(Currency.TRY, new Dictionary<int, int> { [100] = 1, [20] = 2 }, Ctx());

        var dispensed = atm.Dispense(Currency.TRY, reserved, [new NoteCount(100, 1), new NoteCount(20, 1)], Ctx());

        Assert.Equal(2, dispensed.Count);
        var slot2 = atm.GetCassette(2);
        var slot4 = atm.GetCassette(4);
        Assert.Equal((9, 0), (slot2.TotalCount, slot2.TotalReserved));
        Assert.Equal((9, 0), (slot4.TotalCount, slot4.TotalReserved));
    }

    [Fact]
    public void Quote_reserves_the_maximum_of_both_alternatives_per_denomination()
    {
        var options = new List<QuoteOption>
        {
            new(QuoteOptionType.Lower, 70, [new NoteCount(50, 1), new NoteCount(20, 1)]),
            new(QuoteOptionType.Upper, 90, [new NoteCount(50, 1), new NoteCount(20, 2)])
        };

        var required = WithdrawalQuote.RequiredReservation(options);

        Assert.Equal(1, required[50]);
        Assert.Equal(2, required[20]);
    }

    // ------------------------------------------------------------------ deposits (A11, B7)

    [Fact]
    public void Deposit_routes_matching_notes_to_recycling_and_others_to_deposit_cassette()
    {
        var atm = InService(null, (2, 10));

        var outcome = atm.AcceptDeposit(
            [new DepositNoteInput(Currency.TRY, 100, 5), new DepositNoteInput(Currency.EUR, 50, 3), new DepositNoteInput(Currency.TRY, 500, 1)],
            Ctx());

        Assert.Contains(outcome.Placements, p => p is { CassetteSlot: 2, Currency: Currency.TRY, Denomination: 100, Count: 5 });
        Assert.Contains(outcome.Placements, p => p is { CassetteSlot: 1, Currency: Currency.EUR, Denomination: 50, Count: 3 });
        Assert.Contains(outcome.Placements, p => p is { CassetteSlot: 1, Currency: Currency.TRY, Denomination: 500, Count: 1 });
        Assert.Empty(outcome.Returned);
        Assert.Equal(15, atm.GetCassette(2).TotalCount);
    }

    [Fact]
    public void Deposit_overflows_to_deposit_cassette_and_returns_what_does_not_fit()
    {
        var atm = InService(null, (2, 95));
        atm.AcceptDeposit([new DepositNoteInput(Currency.USD, 20, 97)], Ctx()); // deposit cassette now 97/100

        var outcome = atm.AcceptDeposit([new DepositNoteInput(Currency.TRY, 100, 10)], Ctx());

        Assert.Contains(outcome.Placements, p => p is { CassetteSlot: 2, Count: 5 });
        Assert.Contains(outcome.Placements, p => p is { CassetteSlot: 1, Count: 3 });
        var returned = Assert.Single(outcome.Returned);
        Assert.Equal(2, returned.Count);
        Assert.Equal(AtmMachine.ReturnReasonCassetteFull, returned.Reason);
        Assert.Equal(100, atm.GetCassette(2).TotalCount);
        Assert.Equal(100, atm.GetCassette(1).TotalCount);
    }

    // ------------------------------------------------------------------ ledger (B8) and alerts (B10)

    [Fact]
    public void Every_balance_change_produces_a_movement_and_balances_can_be_replayed()
    {
        var atm = InService(null, (2, 50), (4, 30));
        var reserved = atm.ReserveNotes(Currency.TRY, new Dictionary<int, int> { [100] = 2, [20] = 3 }, Ctx());
        atm.Dispense(Currency.TRY, reserved, [new NoteCount(100, 2), new NoteCount(20, 1)], Ctx());
        atm.AcceptDeposit([new DepositNoteInput(Currency.TRY, 100, 4), new DepositNoteInput(Currency.EUR, 20, 2)], Ctx());

        var replayed = atm.PendingMovements
            .GroupBy(m => (m.CassetteId, m.Currency, m.Denomination))
            .ToDictionary(
                g => g.Key,
                g => (Count: g.Sum(m => CassetteMovement.EffectOf(m.Type, m.Quantity).CountDelta),
                      Reserved: g.Sum(m => CassetteMovement.EffectOf(m.Type, m.Quantity).ReservedDelta)));

        foreach (var cassette in atm.Cassettes)
        {
            foreach (var balance in cassette.Balances)
            {
                Assert.Equal((balance.Count, balance.Reserved), replayed[(cassette.Id, balance.Currency, balance.Denomination)]);
            }
        }

        Assert.Contains(atm.PendingMovements, m => m.Type == MovementType.Withdrawal);
        Assert.Contains(atm.PendingMovements, m => m.Type == MovementType.Reservation);
        Assert.Contains(atm.PendingMovements, m => m.Type == MovementType.ReservationRelease);
    }

    [Fact]
    public void Low_cash_alert_is_raised_once_when_available_drops_below_ten_percent()
    {
        var atm = InService(null, (2, 12));
        atm.ClearPendingChanges();

        atm.ReserveNotes(Currency.TRY, new Dictionary<int, int> { [100] = 3 }, Ctx()); // 12 -> 9 available
        atm.ReserveNotes(Currency.TRY, new Dictionary<int, int> { [100] = 1 }, Ctx()); // stays below

        var alert = Assert.IsType<LowCashAlert>(Assert.Single(atm.DomainEvents));
        Assert.Equal(2, alert.CassetteSlot);
        Assert.Equal(9, alert.Available);
    }

    // ------------------------------------------------------------------ reconciliation (A8, B9)

    [Fact]
    public void Reconciliation_records_differences_as_adjustments()
    {
        var atm = InService(null, (2, 50), (4, 20));
        atm.AcceptDeposit([new DepositNoteInput(Currency.EUR, 50, 2)], Ctx());
        atm.TransitionTo(AtmState.Maintenance, false, Ctx());
        atm.ClearPendingChanges();

        var counts = atm.CalculateReconciliation(
        [
            new CountedCassette(1, null, [new CountedNotes(Currency.EUR, 50, 1), new CountedNotes(Currency.USD, 20, 1)]),
            new CountedCassette(2, 48, null),
            new CountedCassette(3, 0, null),
            new CountedCassette(4, 20, null),
            new CountedCassette(5, 0, null)
        ]);
        atm.ApplyReconciliation(counts, Ctx());

        Assert.Equal(48, atm.GetCassette(2).TotalCount);
        Assert.Equal(1, atm.GetCassette(1).CountOf(Currency.EUR, 50));
        Assert.Equal(1, atm.GetCassette(1).CountOf(Currency.USD, 20));
        Assert.Equal(3, atm.PendingMovements.Count(m => m.Type == MovementType.ReconciliationAdjustment));
        Assert.Contains(atm.PendingMovements, m => m is { CassetteSlot: 2, Quantity: -2 });
    }

    [Fact]
    public void Reconciliation_requires_every_cassette()
    {
        var atm = InService(null, (2, 10));
        atm.TransitionTo(AtmState.Maintenance, false, Ctx());

        var ex = Assert.Throws<ValidationException>(() => atm.CalculateReconciliation([new CountedCassette(2, 10, null)]));
        Assert.Equal(ErrorCodes.ReconciliationIncomplete, ex.Code);
    }

    [Theory]
    [InlineData(1000, ReconciliationStatus.Applied)]
    [InlineData(1000.01, ReconciliationStatus.AwaitingApproval)]
    public void Reconciliation_above_threshold_awaits_approval(decimal differenceTry, ReconciliationStatus expected)
    {
        var line = new ReconciliationLine(Guid.NewGuid(), 2, CassetteType.Recycling, Currency.TRY, 100, 10, 0, -10, 1m, -differenceTry);

        var reconciliation = Reconciliation.Create(Guid.NewGuid(), "alice", [line], 1000m, Now);

        Assert.Equal(expected, reconciliation.Status);
    }

    [Fact]
    public void Absolute_differences_are_summed_so_surplus_cannot_hide_shortage()
    {
        var shortage = new ReconciliationLine(Guid.NewGuid(), 2, CassetteType.Recycling, Currency.TRY, 100, 10, 4, -6, 1m, -600m);
        var surplus = new ReconciliationLine(Guid.NewGuid(), 3, CassetteType.Recycling, Currency.TRY, 50, 0, 12, 12, 1m, 600m);

        var reconciliation = Reconciliation.Create(Guid.NewGuid(), "alice", [shortage, surplus], 1000m, Now);

        Assert.Equal(1200m, reconciliation.TotalAbsoluteDifferenceTry);
        Assert.Equal(ReconciliationStatus.AwaitingApproval, reconciliation.Status);
    }

    [Fact]
    public void Four_eyes_submitter_cannot_approve_own_reconciliation()
    {
        var line = new ReconciliationLine(Guid.NewGuid(), 2, CassetteType.Recycling, Currency.TRY, 100, 20, 0, -20, 1m, -2000m);
        var reconciliation = Reconciliation.Create(Guid.NewGuid(), "alice", [line], 1000m, Now);

        var ex = Assert.Throws<ForbiddenException>(() => reconciliation.Approve("ALICE", null, Now));
        Assert.Equal(ErrorCodes.SameUserApproval, ex.Code);

        reconciliation.Approve("bob", "ok", Now);
        Assert.Equal(ReconciliationStatus.Approved, reconciliation.Status);
    }

    // ------------------------------------------------------------------ card protection (B5)

    [Fact]
    public void Card_numbers_are_hashed_with_a_secret_and_never_kept_in_plain_text()
    {
        var hasher = new HmacCardHasher(Options.Create(new CardHashingOptions { Secret = "unit-test-secret-123456" }));
        var other = new HmacCardHasher(Options.Create(new CardHashingOptions { Secret = "another-secret-abcdefg" }));

        var a = hasher.Protect("4111 1111 1111 1111");
        var b = hasher.Protect("4111-1111-1111-1111");

        Assert.Equal(a.Hash, b.Hash);
        Assert.DoesNotContain("4111111111111111", a.Hash);
        Assert.Equal(64, a.Hash.Length);
        Assert.Equal("1111", a.Last4);
        Assert.NotEqual(a.Hash, other.Protect("4111111111111111").Hash);
        Assert.Throws<ValidationException>(() => hasher.Protect("12ab"));
    }
}
