using Atm.Business.Common;
using Atm.Business.Domain;

namespace Atm.Business.Contracts;

public sealed record CassetteNotesResponse(Currency Currency, int Denomination, int Count, int Reserved, int Available, long Amount);

public sealed record CassetteResponse(
    Guid Id,
    int Slot,
    CassetteType Type,
    Currency? Currency,
    int? Denomination,
    int Capacity,
    int Count,
    int Reserved,
    int Available,
    decimal FillRatio,
    List<CassetteNotesResponse> Notes);

public sealed record AtmResponse(
    Guid Id,
    string Code,
    string? Location,
    AtmState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    List<CassetteResponse> Cassettes);

public sealed record NoteCountResponse(int Denomination, int Count);

public sealed record QuoteOptionResponse(QuoteOptionType Type, int Amount, int NoteCount, List<NoteCountResponse> Notes);

public sealed record QuoteResponse(
    Guid QuoteId,
    Guid AtmId,
    QuoteStatus Status,
    string Card,
    Currency Currency,
    int RequestedAmount,
    bool ExactMatch,
    string Message,
    List<QuoteOptionResponse> Options,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int? DailyLimit,
    int? RemainingDailyLimit,
    QuoteOptionType? ConfirmedOption,
    int? ConfirmedAmount);

public sealed record DispensedNoteResponse(int CassetteSlot, int Denomination, int Count);

public sealed record WithdrawalResponse(
    Guid WithdrawalId,
    Guid QuoteId,
    Guid AtmId,
    Currency Currency,
    QuoteOptionType Option,
    int Amount,
    int NoteCount,
    List<DispensedNoteResponse> Notes,
    DateTimeOffset ConfirmedAt);

public sealed record ConfirmResult(WithdrawalResponse Response, bool Replayed);

public sealed record DepositPlacementResponse(int CassetteSlot, CassetteType CassetteType, Currency Currency, int Denomination, int Count);

public sealed record ReturnedNotesResponse(Currency Currency, int Denomination, int Count, string Reason);

public sealed record DepositReturnedResponse(int UnrecognizedCount, List<ReturnedNotesResponse> Notes, int TotalReturnedNotes);

public sealed record CurrencyAmountResponse(Currency Currency, int NoteCount, long Amount);

public sealed record DepositResponse(
    Guid DepositId,
    Guid AtmId,
    DateTimeOffset CreatedAt,
    List<DepositPlacementResponse> Accepted,
    List<CurrencyAmountResponse> AcceptedTotals,
    DepositReturnedResponse Returned);

public sealed record ReconciliationLineResponse(
    int CassetteSlot,
    CassetteType CassetteType,
    Currency Currency,
    int Denomination,
    int SystemCount,
    int CountedCount,
    int Difference,
    decimal RateToTry,
    decimal DifferenceValueTry);

public sealed record ReconciliationResponse(
    Guid Id,
    Guid AtmId,
    ReconciliationStatus Status,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? Comment,
    decimal TotalAbsoluteDifferenceTry,
    decimal ApprovalThresholdTry,
    bool RequiresApproval,
    List<ReconciliationLineResponse> Lines);

public sealed record MovementResponse(
    long Id,
    Guid CassetteId,
    int CassetteSlot,
    MovementType Type,
    Currency Currency,
    int Denomination,
    int Quantity,
    int CountAfter,
    int ReservedAfter,
    string ReferenceType,
    Guid? ReferenceId,
    string? PerformedBy,
    DateTimeOffset CreatedAt);

public sealed record CassetteReport(
    int Slot,
    CassetteType Type,
    Currency? Currency,
    int? Denomination,
    int Capacity,
    int Count,
    int Reserved,
    int Available,
    bool LowCash,
    List<CassetteNotesResponse> Notes);

public sealed record DenominationSummary(Currency Currency, int Denomination, int Count, int Reserved, int Available, long Amount);

public sealed record CurrencyTotal(
    Currency Currency,
    int NoteCount,
    long Amount,
    long ReservedAmount,
    long AvailableAmount,
    decimal RateToTry,
    decimal AmountTry);

public sealed record AtmReport(
    Guid AtmId,
    string Code,
    string? Location,
    AtmState State,
    List<CassetteReport> Cassettes,
    List<DenominationSummary> Denominations,
    List<CurrencyTotal> Totals,
    decimal TotalTry);

public sealed record ExchangeRateResponse(Currency Currency, decimal RateToTry, DateTimeOffset UpdatedAt);

public sealed record OperationsReport(
    DateTimeOffset GeneratedAt,
    List<AtmReport> Atms,
    List<CurrencyTotal> NetworkTotals,
    decimal NetworkTotalTry,
    List<ExchangeRateResponse> ExchangeRates);

public sealed record BalanceDifference(
    Guid AtmId,
    Guid CassetteId,
    int CassetteSlot,
    Currency Currency,
    int Denomination,
    int StoredCount,
    int LedgerCount,
    int StoredReserved,
    int LedgerReserved);

public sealed record ConsistencyReport(
    DateTimeOffset CheckedAt,
    bool IsConsistent,
    int CheckedBalances,
    List<BalanceDifference> Differences);

public sealed record OutboxMessageResponse(
    Guid Id,
    string Type,
    string Payload,
    DateTimeOffset OccurredAt,
    DateTimeOffset? ProcessedAt,
    int Attempts,
    string? LastError);

internal static class Mapping
{
    public static AtmResponse ToResponse(this AtmMachine atm) => new(
        atm.Id,
        atm.Code,
        atm.Location,
        atm.State,
        atm.CreatedAt,
        atm.UpdatedAt,
        atm.Cassettes.OrderBy(c => c.Slot).Select(c => new CassetteResponse(
            c.Id,
            c.Slot,
            c.Type,
            c.Currency,
            c.Denomination,
            Cassette.Capacity,
            c.TotalCount,
            c.TotalReserved,
            c.Available,
            c.PhysicalFillRatio,
            c.ToNotes())).ToList());

    public static List<CassetteNotesResponse> ToNotes(this Cassette cassette)
        => cassette.Balances
            .Where(b => b.Count > 0 || b.Reserved > 0)
            .OrderBy(b => b.Currency)
            .ThenByDescending(b => b.Denomination)
            .Select(b => new CassetteNotesResponse(b.Currency, b.Denomination, b.Count, b.Reserved, b.Available, (long)b.Count * b.Denomination))
            .ToList();

    public static QuoteOptionResponse ToResponse(this QuoteOption option) => new(
        option.Type,
        option.Amount,
        option.NoteCount,
        option.Notes.Select(n => new NoteCountResponse(n.Denomination, n.Count)).ToList());

    public static ReconciliationResponse ToResponse(this Reconciliation r) => new(
        r.Id,
        r.AtmId,
        r.Status,
        r.CreatedBy,
        r.CreatedAt,
        r.DecidedBy,
        r.DecidedAt,
        r.Comment,
        r.TotalAbsoluteDifferenceTry,
        r.ApprovalThresholdTry,
        r.RequiresApproval,
        r.Lines.Select(l => new ReconciliationLineResponse(
            l.CassetteSlot, l.CassetteType, l.Currency, l.Denomination, l.SystemCount, l.CountedCount, l.Difference,
            l.RateToTry, l.DifferenceValueTry)).ToList());

    public static MovementResponse ToResponse(this CassetteMovement m) => new(
        m.Id, m.CassetteId, m.CassetteSlot, m.Type, m.Currency, m.Denomination, m.Quantity, m.CountAfter, m.ReservedAfter,
        m.ReferenceType, m.ReferenceId, m.PerformedBy, m.CreatedAt);
}
