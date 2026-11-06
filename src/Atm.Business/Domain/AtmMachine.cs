using Atm.Business.Common;

namespace Atm.Business.Domain;

public sealed record CashLoadItem(int Slot, int Count);

public sealed record CashUnloadItem(int Slot, int? Count);

public sealed record ReservedNotes(Guid CassetteId, int CassetteSlot, int Denomination, int Quantity, int Sequence);

public sealed record DispensedNote(int CassetteSlot, int Denomination, int Count);

public sealed record DepositNoteInput(Currency Currency, int Denomination, int Count);

public sealed record DepositPlacement(int CassetteSlot, CassetteType CassetteType, Currency Currency, int Denomination, int Count);

public sealed record ReturnedNotes(Currency Currency, int Denomination, int Count, string Reason);

public sealed record DepositOutcome(IReadOnlyList<DepositPlacement> Placements, IReadOnlyList<ReturnedNotes> Returned);

public sealed record CountedNotes(Currency Currency, int Denomination, int Count);

public sealed record CountedCassette(int Slot, int? Count, IReadOnlyList<CountedNotes>? Notes);

public sealed record ReconciliationCount(
    Guid CassetteId,
    int CassetteSlot,
    CassetteType CassetteType,
    Currency Currency,
    int Denomination,
    int SystemCount,
    int CountedCount)
{
    public int Difference => CountedCount - SystemCount;
}

/// <summary>
/// Aggregate root: an ATM and its 5 cassettes. All business rules that span cassettes live here, and every
/// balance change goes through <see cref="ApplyMovement"/>, which also writes the ledger record.
/// </summary>
public class AtmMachine
{
    public const int CassetteCount = 5;
    public const int MaxForeignRecyclingCassettes = 2;
    public const int MinForeignRecyclingCassettes = 1;
    public const decimal LowCashThresholdRatio = 0.10m;
    public static readonly int LowCashThreshold = (int)(Cassette.Capacity * LowCashThresholdRatio);

    public const string ReturnReasonCassetteFull = "CASSETTE_FULL";

    private readonly List<Cassette> _cassettes = new();
    private readonly List<CassetteMovement> _pendingMovements = new();
    private readonly List<IDomainEvent> _domainEvents = new();

    private AtmMachine()
    {
    }

    public Guid Id { get; private set; }
    public string Code { get; private set; } = null!;
    public string? Location { get; private set; }
    public AtmState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyCollection<Cassette> Cassettes => _cassettes;

    /// <summary>Ledger records produced since the last save. Persisted by the data layer in the same transaction.</summary>
    public IReadOnlyList<CassetteMovement> PendingMovements => _pendingMovements;

    /// <summary>Domain events produced since the last save. Written to the outbox in the same transaction.</summary>
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    public Cassette? DepositCassette => _cassettes.SingleOrDefault(c => c.Type == CassetteType.Deposit);

    public IEnumerable<Cassette> RecyclingCassettes => _cassettes.Where(c => c.IsRecycling);

    public static AtmMachine Create(string code, string? location, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 50)
        {
            throw new ValidationException(ErrorCodes.ValidationFailed, "ATM code is required and must be at most 50 characters.");
        }

        return new AtmMachine
        {
            Id = Guid.NewGuid(),
            Code = code.Trim(),
            Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim(),
            State = AtmState.Unconfigured,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void ClearPendingChanges()
    {
        _pendingMovements.Clear();
        _domainEvents.Clear();
    }

    // ------------------------------------------------------------------ lifecycle

    public void EnsureState(string operation, params AtmState[] allowed)
    {
        if (allowed.Contains(State))
        {
            return;
        }

        throw new ConflictException(
            ErrorCodes.OperationNotAllowedInState,
            $"Operation '{operation}' is not allowed while the ATM is {State.ToApiName()}. " +
            $"Allowed state(s): {string.Join(", ", allowed.Select(s => s.ToApiName()))}.",
            new Dictionary<string, object?>
            {
                ["currentState"] = State.ToApiName(),
                ["allowedStates"] = allowed.Select(s => s.ToApiName()).ToArray()
            });
    }

    /// <summary>
    /// Explicit transitions: LOADED → IN_SERVICE, MAINTENANCE → IN_SERVICE, IN_SERVICE → MAINTENANCE.
    /// UNCONFIGURED → CONFIGURED and CONFIGURED → LOADED happen implicitly via configuration and cash loading.
    /// </summary>
    public void TransitionTo(AtmState target, bool hasPendingReconciliation, OperationContext context)
    {
        switch (State, target)
        {
            case (AtmState.Loaded, AtmState.InService):
            case (AtmState.Maintenance, AtmState.InService):
                if (hasPendingReconciliation)
                {
                    throw new ConflictException(
                        ErrorCodes.ReconciliationPending,
                        "The ATM cannot return to service while a reconciliation is awaiting approval.");
                }

                State = AtmState.InService;
                RaiseLowCashForAllCassettes(context);
                break;

            case (AtmState.InService, AtmState.Maintenance):
                State = AtmState.Maintenance;
                break;

            default:
                throw new ConflictException(
                    ErrorCodes.InvalidStateTransition,
                    $"Cannot transition from {State.ToApiName()} to {target.ToApiName()}. {TransitionHint()}",
                    new Dictionary<string, object?>
                    {
                        ["currentState"] = State.ToApiName(),
                        ["targetState"] = target.ToApiName()
                    });
        }

        Touch(context.Timestamp);
    }

    private string TransitionHint() => State switch
    {
        AtmState.Unconfigured => "Configure the cassettes first (moves the ATM to CONFIGURED).",
        AtmState.Configured => "Load cash first (moves the ATM to LOADED).",
        AtmState.Loaded => "Allowed transition: IN_SERVICE.",
        AtmState.InService => "Allowed transition: MAINTENANCE.",
        AtmState.Maintenance => "Allowed transition: IN_SERVICE.",
        _ => string.Empty
    };

    // ------------------------------------------------------------------ configuration

    public static void ValidateConfiguration(IReadOnlyList<CassetteConfiguration>? configurations)
    {
        static ValidationException Invalid(string message)
            => new(ErrorCodes.InvalidCassetteConfiguration, message);

        if (configurations is null || configurations.Count != CassetteCount)
        {
            throw Invalid($"Exactly {CassetteCount} cassettes must be configured.");
        }

        if (configurations.Any(c => c.Slot is < 1 or > CassetteCount)
            || configurations.Select(c => c.Slot).Distinct().Count() != CassetteCount)
        {
            throw Invalid($"Cassette slots must be unique and between 1 and {CassetteCount}.");
        }

        if (configurations.Any(c => !Enum.IsDefined(c.Type)))
        {
            throw Invalid("Unknown cassette type.");
        }

        var deposit = configurations.Where(c => c.Type == CassetteType.Deposit).ToList();
        if (deposit.Count != 1)
        {
            throw Invalid("Exactly one cassette must be the deposit-only cassette.");
        }

        if (deposit[0].Currency is not null || deposit[0].Denomination is not null)
        {
            throw Invalid("The deposit cassette accepts any currency and denomination; do not specify currency or denomination for it.");
        }

        var recycling = configurations.Where(c => c.Type == CassetteType.Recycling).ToList();
        foreach (var cassette in recycling)
        {
            if (cassette.Currency is null || !Enum.IsDefined(cassette.Currency.Value))
            {
                throw Invalid($"Recycling cassette {cassette.Slot} must have a supported currency (TRY, USD, EUR).");
            }

            if (cassette.Denomination is null || !Denominations.IsValid(cassette.Denomination.Value))
            {
                throw Invalid($"Recycling cassette {cassette.Slot} must have a denomination of 20, 50, 100 or 500.");
            }
        }

        var foreign = recycling.Count(c => c.Currency!.Value.IsForeign());
        if (foreign is < MinForeignRecyclingCassettes or > MaxForeignRecyclingCassettes)
        {
            throw Invalid(
                $"Between {MinForeignRecyclingCassettes} and {MaxForeignRecyclingCassettes} recycling cassettes must be configured " +
                $"for a foreign currency (USD or EUR); {foreign} configured.");
        }
    }

    public void ConfigureCassettes(IReadOnlyList<CassetteConfiguration> configurations, OperationContext context)
    {
        EnsureState("configure cassettes", AtmState.Unconfigured, AtmState.Configured, AtmState.Maintenance);
        ValidateConfiguration(configurations);

        foreach (var configuration in configurations)
        {
            var existing = _cassettes.FirstOrDefault(c => c.Slot == configuration.Slot);
            if (existing is not null && !existing.HasConfiguration(configuration) && !existing.IsEmpty)
            {
                throw new ConflictException(
                    ErrorCodes.CassetteNotEmpty,
                    $"Cassette {existing.Slot} still contains {existing.TotalCount} note(s); unload it before changing its configuration.",
                    new Dictionary<string, object?> { ["slot"] = existing.Slot, ["count"] = existing.TotalCount });
            }
        }

        foreach (var configuration in configurations.OrderBy(c => c.Slot))
        {
            var existing = _cassettes.FirstOrDefault(c => c.Slot == configuration.Slot);
            if (existing is null)
            {
                _cassettes.Add(Cassette.Create(Id, configuration));
            }
            else if (!existing.HasConfiguration(configuration))
            {
                existing.Reconfigure(configuration);
            }
        }

        if (State == AtmState.Unconfigured)
        {
            State = AtmState.Configured;
        }

        Touch(context.Timestamp);
    }

    // ------------------------------------------------------------------ loading / unloading

    public void LoadCash(IReadOnlyList<CashLoadItem>? items, OperationContext context)
    {
        // LOADED is allowed too, so cash can be topped up before the ATM first goes into service.
        EnsureState("load cash", AtmState.Configured, AtmState.Loaded, AtmState.Maintenance);
        if (items is null || items.Count == 0)
        {
            throw new ValidationException(ErrorCodes.ValidationFailed, "At least one cassette load item is required.");
        }

        var grouped = items.GroupBy(i => i.Slot).Select(g => (Slot: g.Key, Count: g.Sum(i => i.Count), Items: g.ToList())).ToList();
        foreach (var (slot, count, group) in grouped)
        {
            if (group.Any(i => i.Count <= 0))
            {
                throw new ValidationException(ErrorCodes.InvalidNoteCount, $"Load count for cassette {slot} must be positive.");
            }

            var cassette = GetCassette(slot);
            if (!cassette.IsRecycling)
            {
                throw new BusinessRuleException(
                    ErrorCodes.DepositCassetteNotLoadable,
                    $"Cassette {slot} is the deposit-only cassette; cash cannot be loaded into it.");
            }

            if (count > cassette.FreeSpace)
            {
                throw new BusinessRuleException(
                    ErrorCodes.CassetteCapacityExceeded,
                    $"Cassette {slot} can take {cassette.FreeSpace} more note(s); {count} requested.",
                    new Dictionary<string, object?> { ["slot"] = slot, ["freeSpace"] = cassette.FreeSpace, ["requested"] = count });
            }
        }

        var snapshot = SnapshotAvailable();
        foreach (var (slot, count, _) in grouped)
        {
            var cassette = GetCassette(slot);
            ApplyMovement(cassette, cassette.Currency!.Value, cassette.Denomination!.Value, MovementType.Load, count, context);
        }

        if (State == AtmState.Configured)
        {
            State = AtmState.Loaded;
        }

        DetectLowCash(snapshot, context);
    }

    public void UnloadCash(IReadOnlyList<CashUnloadItem>? items, OperationContext context)
    {
        EnsureState("unload cash", AtmState.Loaded, AtmState.Maintenance);
        if (items is null || items.Count == 0)
        {
            throw new ValidationException(ErrorCodes.ValidationFailed, "At least one cassette unload item is required.");
        }

        if (items.Select(i => i.Slot).Distinct().Count() != items.Count)
        {
            throw new ValidationException(ErrorCodes.ValidationFailed, "Each cassette may appear only once in an unload request.");
        }

        var snapshot = SnapshotAvailable();
        foreach (var item in items)
        {
            var cassette = GetCassette(item.Slot);
            if (!cassette.IsRecycling)
            {
                if (item.Count is not null)
                {
                    throw new ValidationException(
                        ErrorCodes.ValidationFailed,
                        "The deposit cassette holds mixed notes and can only be emptied completely (omit 'count').");
                }

                foreach (var balance in cassette.Balances.Where(b => b.Count > 0).ToList())
                {
                    ApplyMovement(cassette, balance.Currency, balance.Denomination, MovementType.Unload, balance.Count, context);
                }

                continue;
            }

            var quantity = item.Count ?? cassette.TotalCount;
            if (item.Count is <= 0)
            {
                throw new ValidationException(ErrorCodes.InvalidNoteCount, $"Unload count for cassette {item.Slot} must be positive.");
            }

            if (quantity > cassette.Available)
            {
                throw new BusinessRuleException(
                    ErrorCodes.InsufficientCassetteBalance,
                    $"Cassette {item.Slot} holds {cassette.Available} unreserved note(s); cannot unload {quantity}.");
            }

            if (quantity > 0)
            {
                ApplyMovement(cassette, cassette.Currency!.Value, cassette.Denomination!.Value, MovementType.Unload, quantity, context);
            }
        }

        DetectLowCash(snapshot, context);
    }

    // ------------------------------------------------------------------ withdrawals

    public bool SupportsCurrency(Currency currency) => RecyclingCassettes.Any(c => c.Currency == currency);

    /// <summary>Notes that can still be offered in a new quote (physical minus reserved), per denomination.</summary>
    public IReadOnlyDictionary<int, int> GetAvailableNotes(Currency currency)
        => RecyclingCassettes
            .Where(c => c.Currency == currency)
            .GroupBy(c => c.Denomination!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Available));

    /// <summary>
    /// Order in which cassettes holding the same currency/denomination are used for dispensing:
    /// the cassette with the higher fill ratio first (ties broken by the lower slot number).
    /// </summary>
    public IReadOnlyList<Cassette> DispenseOrder(Currency currency, int denomination)
        => RecyclingCassettes
            .Where(c => c.Accepts(currency, denomination) && c.Available > 0)
            .OrderByDescending(c => c.AvailableFillRatio)
            .ThenBy(c => c.Slot)
            .ToList();

    public IReadOnlyList<ReservedNotes> ReserveNotes(
        Currency currency,
        IReadOnlyDictionary<int, int> notesPerDenomination,
        OperationContext context)
    {
        EnsureState("reserve notes", AtmState.InService);
        var snapshot = SnapshotAvailable();
        var reserved = new List<ReservedNotes>();
        var sequence = 0;

        foreach (var (denomination, quantity) in notesPerDenomination.Where(n => n.Value > 0).OrderByDescending(n => n.Key))
        {
            var remaining = quantity;
            foreach (var cassette in DispenseOrder(currency, denomination))
            {
                var take = Math.Min(cassette.Available, remaining);
                if (take <= 0)
                {
                    continue;
                }

                ApplyMovement(cassette, currency, denomination, MovementType.Reservation, take, context);
                reserved.Add(new ReservedNotes(cassette.Id, cassette.Slot, denomination, take, sequence++));
                remaining -= take;
                if (remaining == 0)
                {
                    break;
                }
            }

            if (remaining > 0)
            {
                throw new InvalidOperationException(
                    $"Not enough available {currency} {denomination} notes to reserve {quantity}.");
            }
        }

        DetectLowCash(snapshot, context);
        return reserved;
    }

    public void ReleaseReservation(Currency currency, IEnumerable<ReservedNotes> reserved, OperationContext context)
    {
        foreach (var line in reserved)
        {
            var cassette = GetCassette(line.CassetteId);
            ApplyMovement(cassette, currency, line.Denomination, MovementType.ReservationRelease, line.Quantity, context);
        }

        Touch(context.Timestamp);
    }

    /// <summary>
    /// Converts a reservation into a withdrawal: every reserved line is released and the chosen notes are
    /// debited from the same cassettes they were reserved in.
    /// </summary>
    public IReadOnlyList<DispensedNote> Dispense(
        Currency currency,
        IReadOnlyList<ReservedNotes> reserved,
        IReadOnlyList<NoteCount> notesToDispense,
        OperationContext context)
    {
        EnsureState("dispense cash", AtmState.InService);

        foreach (var note in notesToDispense)
        {
            var reservedForDenomination = reserved.Where(r => r.Denomination == note.Denomination).Sum(r => r.Quantity);
            if (note.Count > reservedForDenomination)
            {
                throw new InvalidOperationException(
                    $"Cannot dispense {note.Count} x {note.Denomination}; only {reservedForDenomination} reserved.");
            }
        }

        var snapshot = SnapshotAvailable();
        ReleaseReservation(currency, reserved, context);

        var dispensed = new List<DispensedNote>();
        foreach (var note in notesToDispense.Where(n => n.Count > 0))
        {
            var remaining = note.Count;
            foreach (var line in reserved.Where(r => r.Denomination == note.Denomination).OrderBy(r => r.Sequence))
            {
                var take = Math.Min(line.Quantity, remaining);
                if (take <= 0)
                {
                    continue;
                }

                var cassette = GetCassette(line.CassetteId);
                ApplyMovement(cassette, currency, note.Denomination, MovementType.Withdrawal, take, context);
                dispensed.Add(new DispensedNote(cassette.Slot, note.Denomination, take));
                remaining -= take;
                if (remaining == 0)
                {
                    break;
                }
            }
        }

        DetectLowCash(snapshot, context);
        return dispensed;
    }

    // ------------------------------------------------------------------ deposits

    /// <summary>
    /// Routes deposited notes: matching recycling cassette(s) first (least-filled first), overflow to the
    /// deposit cassette, and anything that still does not fit is returned to the customer.
    /// </summary>
    public DepositOutcome AcceptDeposit(IReadOnlyList<DepositNoteInput> notes, OperationContext context)
    {
        EnsureState("deposit", AtmState.InService);
        foreach (var note in notes)
        {
            if (!Enum.IsDefined(note.Currency))
            {
                throw new ValidationException(ErrorCodes.InvalidCurrency, $"Currency '{note.Currency}' is not supported.");
            }

            Denominations.EnsureValid(note.Denomination);
            if (note.Count <= 0)
            {
                throw new ValidationException(ErrorCodes.InvalidNoteCount, "Every deposited note line must have a positive count.");
            }
        }

        var depositCassette = DepositCassette
            ?? throw new InvalidOperationException("ATM has no deposit cassette.");

        var snapshot = SnapshotAvailable();
        var placements = new List<DepositPlacement>();
        var returned = new List<ReturnedNotes>();

        var merged = notes
            .GroupBy(n => (n.Currency, n.Denomination))
            .Select(g => (g.Key.Currency, g.Key.Denomination, Count: g.Sum(n => n.Count)));

        foreach (var (currency, denomination, count) in merged)
        {
            var remaining = count;

            var recyclingTargets = RecyclingCassettes
                .Where(c => c.Accepts(currency, denomination))
                .OrderBy(c => c.PhysicalFillRatio)
                .ThenBy(c => c.Slot)
                .ToList();

            foreach (var cassette in recyclingTargets)
            {
                remaining -= Place(cassette, currency, denomination, remaining, placements, context);
                if (remaining == 0)
                {
                    break;
                }
            }

            if (remaining > 0)
            {
                remaining -= Place(depositCassette, currency, denomination, remaining, placements, context);
            }

            if (remaining > 0)
            {
                returned.Add(new ReturnedNotes(currency, denomination, remaining, ReturnReasonCassetteFull));
            }
        }

        DetectLowCash(snapshot, context);
        return new DepositOutcome(placements, returned);
    }

    private int Place(
        Cassette cassette,
        Currency currency,
        int denomination,
        int quantity,
        List<DepositPlacement> placements,
        OperationContext context)
    {
        var take = Math.Min(cassette.FreeSpace, quantity);
        if (take <= 0)
        {
            return 0;
        }

        ApplyMovement(cassette, currency, denomination, MovementType.Deposit, take, context);
        placements.Add(new DepositPlacement(cassette.Slot, cassette.Type, currency, denomination, take));
        return take;
    }

    // ------------------------------------------------------------------ reconciliation

    public IReadOnlyList<ReconciliationCount> CalculateReconciliation(IReadOnlyList<CountedCassette>? counted)
    {
        EnsureState("reconcile", AtmState.Configured, AtmState.Maintenance);
        if (counted is null || counted.Count == 0)
        {
            throw new ValidationException(ErrorCodes.ReconciliationIncomplete, "Counted figures are required for every cassette.");
        }

        if (counted.Select(c => c.Slot).Distinct().Count() != counted.Count)
        {
            throw new ValidationException(ErrorCodes.ValidationFailed, "Each cassette may appear only once in a reconciliation.");
        }

        var missing = _cassettes.Select(c => c.Slot).Except(counted.Select(c => c.Slot)).OrderBy(s => s).ToList();
        if (missing.Count > 0)
        {
            throw new ValidationException(
                ErrorCodes.ReconciliationIncomplete,
                $"Counted figures are missing for cassette(s): {string.Join(", ", missing)}.",
                new Dictionary<string, object?> { ["missingSlots"] = missing });
        }

        var lines = new List<ReconciliationCount>();
        foreach (var entry in counted.OrderBy(c => c.Slot))
        {
            var cassette = GetCassette(entry.Slot);
            if (cassette.TotalReserved > 0)
            {
                throw new ConflictException(ErrorCodes.ReconciliationStale, $"Cassette {cassette.Slot} has reserved notes.");
            }

            if (cassette.IsRecycling)
            {
                var count = entry.Count
                    ?? (entry.Notes is { Count: 1 } single
                        && single[0].Currency == cassette.Currency
                        && single[0].Denomination == cassette.Denomination
                            ? single[0].Count
                            : throw new ValidationException(
                                ErrorCodes.ValidationFailed,
                                $"Provide 'count' for recycling cassette {cassette.Slot}."));

                EnsureCountWithinCapacity(cassette.Slot, count);
                lines.Add(new ReconciliationCount(
                    cassette.Id, cassette.Slot, cassette.Type, cassette.Currency!.Value, cassette.Denomination!.Value,
                    cassette.TotalCount, count));
                continue;
            }

            if (entry.Notes is null && entry.Count is not (null or 0))
            {
                throw new ValidationException(
                    ErrorCodes.ValidationFailed,
                    "The deposit cassette holds mixed notes; provide 'notes' (currency, denomination, count) instead of 'count'.");
            }

            var countedNotes = entry.Notes ?? [];
            foreach (var note in countedNotes)
            {
                if (!Enum.IsDefined(note.Currency))
                {
                    throw new ValidationException(ErrorCodes.InvalidCurrency, $"Currency '{note.Currency}' is not supported.");
                }

                Denominations.EnsureValid(note.Denomination);
                if (note.Count < 0)
                {
                    throw new ValidationException(ErrorCodes.InvalidNoteCount, "Counted note counts cannot be negative.");
                }
            }

            var countedByKey = countedNotes
                .GroupBy(n => (n.Currency, n.Denomination))
                .ToDictionary(g => g.Key, g => g.Sum(n => n.Count));
            EnsureCountWithinCapacity(cassette.Slot, countedByKey.Values.Sum());

            var keys = cassette.Balances.Select(b => (b.Currency, b.Denomination))
                .Union(countedByKey.Keys)
                .OrderBy(k => k.Currency)
                .ThenBy(k => k.Denomination);

            foreach (var key in keys)
            {
                lines.Add(new ReconciliationCount(
                    cassette.Id, cassette.Slot, cassette.Type, key.Currency, key.Denomination,
                    cassette.CountOf(key.Currency, key.Denomination),
                    countedByKey.GetValueOrDefault(key)));
            }
        }

        return lines;
    }

    /// <summary>Applies counted figures as RECONCILIATION_ADJUSTMENT movements (counted − system).</summary>
    public void ApplyReconciliation(IReadOnlyList<ReconciliationCount> lines, OperationContext context)
    {
        EnsureState("apply reconciliation", AtmState.Configured, AtmState.Maintenance);

        foreach (var line in lines)
        {
            var cassette = GetCassette(line.CassetteId);
            if (cassette.CountOf(line.Currency, line.Denomination) != line.SystemCount)
            {
                throw new ConflictException(
                    ErrorCodes.ReconciliationStale,
                    $"The balance of cassette {line.CassetteSlot} changed after the count was recorded; recount and submit again.");
            }
        }

        var snapshot = SnapshotAvailable();
        foreach (var line in lines.Where(l => l.Difference != 0))
        {
            var cassette = GetCassette(line.CassetteId);
            ApplyMovement(cassette, line.Currency, line.Denomination, MovementType.ReconciliationAdjustment, line.Difference, context);
        }

        DetectLowCash(snapshot, context);
        Touch(context.Timestamp);
    }

    private static void EnsureCountWithinCapacity(int slot, int count)
    {
        if (count is < 0 or > Cassette.Capacity)
        {
            throw new ValidationException(
                ErrorCodes.InvalidNoteCount,
                $"Counted notes for cassette {slot} must be between 0 and {Cassette.Capacity}.");
        }
    }

    // ------------------------------------------------------------------ internals

    public Cassette GetCassette(int slot)
        => _cassettes.FirstOrDefault(c => c.Slot == slot)
           ?? throw new NotFoundException(ErrorCodes.CassetteNotFound, $"Cassette {slot} does not exist on ATM {Code}.");

    private Cassette GetCassette(Guid cassetteId)
        => _cassettes.FirstOrDefault(c => c.Id == cassetteId)
           ?? throw new InvalidOperationException($"Cassette {cassetteId} does not belong to ATM {Code}.");

    /// <summary>The single choke point for balance changes: updates the balance and records the ledger movement.</summary>
    private void ApplyMovement(Cassette cassette, Currency currency, int denomination, MovementType type, int quantity, OperationContext context)
    {
        if (type == MovementType.ReconciliationAdjustment ? quantity == 0 : quantity <= 0)
        {
            throw new InvalidOperationException($"Invalid movement quantity {quantity} for {type}.");
        }

        var (countDelta, reservedDelta) = CassetteMovement.EffectOf(type, quantity);
        if (cassette.TotalCount + countDelta > Cassette.Capacity)
        {
            throw new InvalidOperationException($"Cassette {cassette.Slot} would exceed its capacity of {Cassette.Capacity}.");
        }

        var balance = cassette.GetOrCreateBalance(currency, denomination);
        balance.Apply(countDelta, reservedDelta);
        _pendingMovements.Add(CassetteMovement.Create(Id, cassette, balance, type, quantity, context));
        Touch(context.Timestamp);
    }

    private Dictionary<Cassette, int> SnapshotAvailable()
        => RecyclingCassettes.ToDictionary(c => c, c => c.Available);

    /// <summary>Raises a low-cash alert only when a cassette crosses the threshold downwards, to avoid alert storms.</summary>
    private void DetectLowCash(Dictionary<Cassette, int> before, OperationContext context)
    {
        foreach (var cassette in RecyclingCassettes)
        {
            if (before.TryGetValue(cassette, out var previous)
                && previous >= LowCashThreshold
                && cassette.Available < LowCashThreshold)
            {
                RaiseLowCash(cassette, context);
            }
        }
    }

    private void RaiseLowCashForAllCassettes(OperationContext context)
    {
        foreach (var cassette in RecyclingCassettes.Where(c => c.Available < LowCashThreshold))
        {
            RaiseLowCash(cassette, context);
        }
    }

    private void RaiseLowCash(Cassette cassette, OperationContext context)
        => _domainEvents.Add(new LowCashAlert(
            Id, Code, cassette.Id, cassette.Slot, cassette.Currency!.Value, cassette.Denomination!.Value,
            cassette.TotalCount, cassette.TotalReserved, cassette.Available, Cassette.Capacity, LowCashThreshold,
            context.Timestamp));

    private void Touch(DateTimeOffset now) => UpdatedAt = now;
}
