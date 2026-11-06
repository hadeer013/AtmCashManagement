using Atm.Business.Common;

namespace Atm.Business.Domain;

public class Deposit
{
    private Deposit()
    {
    }

    public Guid Id { get; private set; }
    public Guid AtmId { get; private set; }
    public string? CardHash { get; private set; }
    public string? CardLast4 { get; private set; }
    public int UnrecognizedCount { get; private set; }
    public List<DepositPlacement> Placements { get; private set; } = new();
    public List<ReturnedNotes> ReturnedNotes { get; private set; } = new();
    public DateTimeOffset CreatedAt { get; private set; }

    public static Deposit Create(Guid id, Guid atmId, string? cardHash, string? cardLast4, int unrecognizedCount, DepositOutcome outcome, DateTimeOffset now)
        => new()
        {
            Id = id,
            AtmId = atmId,
            CardHash = cardHash,
            CardLast4 = cardLast4,
            UnrecognizedCount = unrecognizedCount,
            Placements = outcome.Placements.ToList(),
            ReturnedNotes = outcome.Returned.ToList(),
            CreatedAt = now
        };
}

public sealed record ReconciliationLine(
    Guid CassetteId,
    int CassetteSlot,
    CassetteType CassetteType,
    Currency Currency,
    int Denomination,
    int SystemCount,
    int CountedCount,
    int Difference,
    decimal RateToTry,
    decimal DifferenceValueTry);

public class Reconciliation
{
    private Reconciliation()
    {
    }

    public Guid Id { get; private set; }
    public Guid AtmId { get; private set; }
    public ReconciliationStatus Status { get; private set; }
    public string CreatedBy { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public string? DecidedBy { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public string? Comment { get; private set; }

    /// <summary>Sum of |difference| x denomination x rate. Absolute values, so a surplus cannot hide a shortage.</summary>
    public decimal TotalAbsoluteDifferenceTry { get; private set; }

    public decimal ApprovalThresholdTry { get; private set; }
    public List<ReconciliationLine> Lines { get; private set; } = new();

    public bool RequiresApproval => TotalAbsoluteDifferenceTry > ApprovalThresholdTry;

    public static Reconciliation Create(
        Guid atmId,
        string createdBy,
        IReadOnlyList<ReconciliationLine> lines,
        decimal approvalThresholdTry,
        DateTimeOffset now)
    {
        var reconciliation = new Reconciliation
        {
            Id = Guid.NewGuid(),
            AtmId = atmId,
            CreatedBy = createdBy,
            CreatedAt = now,
            Lines = lines.ToList(),
            ApprovalThresholdTry = approvalThresholdTry,
            TotalAbsoluteDifferenceTry = lines.Sum(l => Math.Abs(l.DifferenceValueTry))
        };

        reconciliation.Status = reconciliation.RequiresApproval
            ? ReconciliationStatus.AwaitingApproval
            : ReconciliationStatus.Applied;
        return reconciliation;
    }

    public IReadOnlyList<ReconciliationCount> ToCounts()
        => Lines.Select(l => new ReconciliationCount(
                l.CassetteId, l.CassetteSlot, l.CassetteType, l.Currency, l.Denomination, l.SystemCount, l.CountedCount))
            .ToList();

    public void Approve(string user, string? comment, DateTimeOffset now) => Decide(ReconciliationStatus.Approved, user, comment, now);

    public void Reject(string user, string? comment, DateTimeOffset now) => Decide(ReconciliationStatus.Rejected, user, comment, now);

    private void Decide(ReconciliationStatus status, string user, string? comment, DateTimeOffset now)
    {
        if (Status != ReconciliationStatus.AwaitingApproval)
        {
            throw new ConflictException(
                ErrorCodes.ReconciliationNotAwaitingApproval,
                $"Reconciliation {Id} is {Status.ToApiName()} and cannot be decided.");
        }

        if (string.Equals(user, CreatedBy, StringComparison.OrdinalIgnoreCase))
        {
            throw new ForbiddenException(
                ErrorCodes.SameUserApproval,
                "Four-eyes principle: a reconciliation must be approved or rejected by a different user than the one who submitted it.");
        }

        Status = status;
        DecidedBy = user;
        DecidedAt = now;
        Comment = comment;
    }
}

public class ExchangeRate
{
    private ExchangeRate()
    {
    }

    public ExchangeRate(Currency currency, decimal rateToTry, DateTimeOffset updatedAt)
    {
        Currency = currency;
        RateToTry = rateToTry;
        UpdatedAt = updatedAt;
    }

    public Currency Currency { get; private set; }

    /// <summary>Value of one unit of <see cref="Currency"/> in TRY.</summary>
    public decimal RateToTry { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Update(decimal rateToTry, DateTimeOffset now)
    {
        RateToTry = rateToTry;
        UpdatedAt = now;
    }
}

public class OutboxMessage
{
    private OutboxMessage()
    {
    }

    public Guid Id { get; private set; }
    public string Type { get; private set; } = null!;
    public string Payload { get; private set; } = null!;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }

    public static OutboxMessage Create(string type, string payload, DateTimeOffset occurredAt)
        => new() { Id = Guid.NewGuid(), Type = type, Payload = payload, OccurredAt = occurredAt };

    public void MarkProcessed(DateTimeOffset now)
    {
        Attempts++;
        ProcessedAt = now;
        LastError = null;
    }

    public void MarkFailed(string error)
    {
        Attempts++;
        LastError = error.Length > 2000 ? error[..2000] : error;
    }
}

/// <summary>Stored result of a confirm request, keyed by the client's Idempotency-Key.</summary>
public class IdempotencyRecord
{
    private IdempotencyRecord()
    {
    }

    public string Key { get; private set; } = null!;
    public Guid QuoteId { get; private set; }
    public string RequestHash { get; private set; } = null!;
    public string ResponseJson { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public static IdempotencyRecord Create(string key, Guid quoteId, string requestHash, string responseJson, DateTimeOffset now)
        => new() { Key = key, QuoteId = quoteId, RequestHash = requestHash, ResponseJson = responseJson, CreatedAt = now };
}
