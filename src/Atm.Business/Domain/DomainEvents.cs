using Atm.Business.Common;

namespace Atm.Business.Domain;

public interface IDomainEvent
{
    string EventType { get; }
    DateTimeOffset OccurredAt { get; }
}

/// <summary>Raised when a recycling cassette's available notes drop below 10% of its capacity.</summary>
public sealed record LowCashAlert(
    Guid AtmId,
    string AtmCode,
    Guid CassetteId,
    int CassetteSlot,
    Currency Currency,
    int Denomination,
    int Count,
    int Reserved,
    int Available,
    int Capacity,
    int Threshold,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public string EventType => "LowCashAlert";
}
