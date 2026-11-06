using Atm.Business.Common;

namespace Atm.Business.Contracts;

public sealed record CreateAtmRequest(string Code, string? Location);

public sealed record CassetteConfigurationDto(int Slot, CassetteType Type, Currency? Currency, int? Denomination);

public sealed record ConfigureCassettesRequest(List<CassetteConfigurationDto> Cassettes);

public sealed record CashLoadItemDto(int Slot, int Count);

public sealed record LoadCashRequest(List<CashLoadItemDto> Items);

/// <param name="Slot">Cassette slot.</param>
/// <param name="Count">Notes to remove; omit to empty the cassette completely (required for the deposit cassette).</param>
public sealed record CashUnloadItemDto(int Slot, int? Count);

public sealed record UnloadCashRequest(List<CashUnloadItemDto> Items);

public sealed record ChangeStateRequest(AtmState TargetState);

public sealed record CreateQuoteRequest(string CardNumber, Currency Currency, int Amount);

/// <param name="Option">EXACT, LOWER or UPPER. May be omitted when the quote has a single option.</param>
public sealed record ConfirmQuoteRequest(QuoteOptionType? Option);

public sealed record DepositNoteDto(Currency Currency, int Denomination, int Count);

public sealed record DepositRequest(string? CardNumber, List<DepositNoteDto> Notes, int UnrecognizedCount);

public sealed record CountedNotesDto(Currency Currency, int Denomination, int Count);

/// <param name="Slot">Cassette slot.</param>
/// <param name="Count">Counted notes for a recycling cassette.</param>
/// <param name="Notes">Counted notes per currency/denomination for the deposit cassette.</param>
public sealed record CountedCassetteDto(int Slot, int? Count, List<CountedNotesDto>? Notes);

public sealed record CreateReconciliationRequest(List<CountedCassetteDto> Cassettes);

public sealed record ReconciliationDecisionRequest(string? Comment);

public sealed record UpdateExchangeRateRequest(decimal RateToTry);
