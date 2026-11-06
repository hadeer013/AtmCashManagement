using Atm.Business.Common;

namespace Atm.Business.Domain;

/// <summary>
/// Immutable ledger record. Every change to a cassette balance produces exactly one movement, so
/// balances can be rebuilt from the ledger:
///   Count    = LOAD - UNLOAD - WITHDRAWAL + DEPOSIT + RECONCILIATION_ADJUSTMENT (signed)
///   Reserved = RESERVATION - RESERVATION_RELEASE
/// </summary>
public class CassetteMovement
{
    private CassetteMovement()
    {
    }

    public long Id { get; private set; }
    public Guid AtmId { get; private set; }
    public Guid CassetteId { get; private set; }
    public int CassetteSlot { get; private set; }
    public Currency Currency { get; private set; }
    public int Denomination { get; private set; }
    public MovementType Type { get; private set; }

    /// <summary>Number of notes. Always positive, except RECONCILIATION_ADJUSTMENT, which is signed.</summary>
    public int Quantity { get; private set; }

    public int CountAfter { get; private set; }
    public int ReservedAfter { get; private set; }
    public string ReferenceType { get; private set; } = null!;
    public Guid? ReferenceId { get; private set; }
    public string? PerformedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static (int CountDelta, int ReservedDelta) EffectOf(MovementType type, int quantity) => type switch
    {
        MovementType.Load => (quantity, 0),
        MovementType.Unload => (-quantity, 0),
        MovementType.Withdrawal => (-quantity, 0),
        MovementType.Deposit => (quantity, 0),
        MovementType.Reservation => (0, quantity),
        MovementType.ReservationRelease => (0, -quantity),
        MovementType.ReconciliationAdjustment => (quantity, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    internal static CassetteMovement Create(
        Guid atmId,
        Cassette cassette,
        CassetteBalance balance,
        MovementType type,
        int quantity,
        OperationContext context) => new()
    {
        AtmId = atmId,
        CassetteId = cassette.Id,
        CassetteSlot = cassette.Slot,
        Currency = balance.Currency,
        Denomination = balance.Denomination,
        Type = type,
        Quantity = quantity,
        CountAfter = balance.Count,
        ReservedAfter = balance.Reserved,
        ReferenceType = context.ReferenceType,
        ReferenceId = context.ReferenceId,
        PerformedBy = context.PerformedBy,
        CreatedAt = context.Timestamp
    };
}
