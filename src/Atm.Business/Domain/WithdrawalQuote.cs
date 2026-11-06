using Atm.Business.Common;

namespace Atm.Business.Domain;

public sealed record QuoteOption(QuoteOptionType Type, int Amount, List<NoteCount> Notes)
{
    public int NoteCount => Notes.Sum(n => n.Count);
}

/// <summary>
/// Step 1 of a withdrawal. Holds the offered option(s) and the physical notes reserved for them.
/// When two alternatives are offered, the notes reserved per denomination are the maximum of both, so either
/// alternative can be confirmed without touching notes that another quote could have been offered.
/// </summary>
public class WithdrawalQuote
{
    private WithdrawalQuote()
    {
    }

    public Guid Id { get; private set; }
    public Guid AtmId { get; private set; }
    public string CardHash { get; private set; } = null!;
    public string CardLast4 { get; private set; } = null!;
    public Currency Currency { get; private set; }
    public int RequestedAmount { get; private set; }

    /// <summary>Calendar day (business time zone) the daily limit is counted against.</summary>
    public DateOnly BusinessDate { get; private set; }

    /// <summary>Amount counted against the daily limit while the quote is active (largest offered amount).</summary>
    public int LimitHoldAmount { get; private set; }

    public QuoteStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public List<QuoteOption> Options { get; private set; } = new();
    public List<QuoteReservationLine> Reservations { get; private set; } = new();

    public QuoteOptionType? ConfirmedOption { get; private set; }
    public int? ConfirmedAmount { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public List<DispensedNote> DispensedNotes { get; private set; } = new();

    public DateTimeOffset? ClosedAt { get; private set; }
    public string? CloseReason { get; private set; }

    public static WithdrawalQuote Create(
        Guid atmId,
        string cardHash,
        string cardLast4,
        Currency currency,
        int requestedAmount,
        DateOnly businessDate,
        IReadOnlyList<QuoteOption> options,
        DateTimeOffset now,
        TimeSpan timeToLive)
    {
        if (options.Count == 0)
        {
            throw new InvalidOperationException("A quote needs at least one option.");
        }

        return new WithdrawalQuote
        {
            Id = Guid.NewGuid(),
            AtmId = atmId,
            CardHash = cardHash,
            CardLast4 = cardLast4,
            Currency = currency,
            RequestedAmount = requestedAmount,
            BusinessDate = businessDate,
            LimitHoldAmount = options.Max(o => o.Amount),
            Status = QuoteStatus.Active,
            CreatedAt = now,
            ExpiresAt = now + timeToLive,
            Options = options.ToList()
        };
    }

    /// <summary>Per-denomination notes that must be reserved so every offered option can be honoured.</summary>
    public static IReadOnlyDictionary<int, int> RequiredReservation(IEnumerable<QuoteOption> options)
        => options
            .SelectMany(o => o.Notes)
            .GroupBy(n => n.Denomination)
            .ToDictionary(g => g.Key, g => g.Max(n => n.Count));

    public bool IsExpiredAt(DateTimeOffset now) => Status == QuoteStatus.Active && now >= ExpiresAt;

    /// <summary>Status as seen by a client, treating a lapsed-but-not-yet-swept quote as expired.</summary>
    public QuoteStatus EffectiveStatus(DateTimeOffset now) => IsExpiredAt(now) ? QuoteStatus.Expired : Status;

    public IReadOnlyList<ReservedNotes> GetReservedNotes()
        => Reservations
            .OrderBy(r => r.Sequence)
            .Select(r => new ReservedNotes(r.CassetteId, r.CassetteSlot, r.Denomination, r.Quantity, r.Sequence))
            .ToList();

    public void AttachReservations(IEnumerable<ReservedNotes> reserved)
    {
        Reservations = reserved.Select(r => QuoteReservationLine.Create(Id, r)).ToList();
    }

    public void MarkConfirmed(QuoteOption option, IReadOnlyList<DispensedNote> dispensed, DateTimeOffset now)
    {
        EnsureActive();
        Status = QuoteStatus.Confirmed;
        ConfirmedOption = option.Type;
        ConfirmedAmount = option.Amount;
        ConfirmedAt = now;
        DispensedNotes = dispensed.ToList();
        ClosedAt = now;
    }

    public void MarkCancelled(string reason, DateTimeOffset now)
    {
        EnsureActive();
        Status = QuoteStatus.Cancelled;
        CloseReason = reason;
        ClosedAt = now;
    }

    public void MarkExpired(DateTimeOffset now)
    {
        EnsureActive();
        Status = QuoteStatus.Expired;
        CloseReason = "EXPIRED";
        ClosedAt = now;
    }

    private void EnsureActive()
    {
        if (Status != QuoteStatus.Active)
        {
            throw new InvalidOperationException($"Quote {Id} is {Status}, not active.");
        }
    }
}

public class QuoteReservationLine
{
    private QuoteReservationLine()
    {
    }

    public Guid Id { get; private set; }
    public Guid QuoteId { get; private set; }
    public Guid CassetteId { get; private set; }
    public int CassetteSlot { get; private set; }
    public int Denomination { get; private set; }
    public int Quantity { get; private set; }
    public int Sequence { get; private set; }

    internal static QuoteReservationLine Create(Guid quoteId, ReservedNotes reserved) => new()
    {
        Id = Guid.NewGuid(),
        QuoteId = quoteId,
        CassetteId = reserved.CassetteId,
        CassetteSlot = reserved.CassetteSlot,
        Denomination = reserved.Denomination,
        Quantity = reserved.Quantity,
        Sequence = reserved.Sequence
    };
}
