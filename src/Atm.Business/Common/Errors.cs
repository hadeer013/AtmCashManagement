namespace Atm.Business.Common;

/// <summary>Machine-readable error codes returned in ProblemDetails.errorCode.</summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string InvalidAmount = "INVALID_AMOUNT";
    public const string InvalidDenomination = "INVALID_DENOMINATION";
    public const string InvalidCurrency = "INVALID_CURRENCY";
    public const string InvalidNoteCount = "INVALID_NOTE_COUNT";
    public const string InvalidCardNumber = "INVALID_CARD_NUMBER";
    public const string UserHeaderRequired = "USER_HEADER_REQUIRED";

    public const string AtmNotFound = "ATM_NOT_FOUND";
    public const string AtmCodeAlreadyExists = "ATM_CODE_ALREADY_EXISTS";
    public const string InvalidStateTransition = "INVALID_STATE_TRANSITION";
    public const string OperationNotAllowedInState = "OPERATION_NOT_ALLOWED_IN_STATE";

    public const string InvalidCassetteConfiguration = "INVALID_CASSETTE_CONFIGURATION";
    public const string CassetteNotFound = "CASSETTE_NOT_FOUND";
    public const string CassetteNotEmpty = "CASSETTE_NOT_EMPTY";
    public const string CassetteCapacityExceeded = "CASSETTE_CAPACITY_EXCEEDED";
    public const string DepositCassetteNotLoadable = "DEPOSIT_CASSETTE_NOT_LOADABLE";
    public const string InsufficientCassetteBalance = "INSUFFICIENT_CASSETTE_BALANCE";

    public const string CurrencyNotAvailable = "CURRENCY_NOT_AVAILABLE";
    public const string NoPayableAmount = "NO_PAYABLE_AMOUNT";
    public const string DailyLimitExceeded = "DAILY_LIMIT_EXCEEDED";
    public const string QuoteNotFound = "QUOTE_NOT_FOUND";
    public const string QuoteExpired = "QUOTE_EXPIRED";
    public const string QuoteCancelled = "QUOTE_CANCELLED";
    public const string QuoteAlreadyConfirmed = "QUOTE_ALREADY_CONFIRMED";
    public const string QuoteOptionRequired = "QUOTE_OPTION_REQUIRED";
    public const string QuoteOptionNotAvailable = "QUOTE_OPTION_NOT_AVAILABLE";
    public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";

    public const string EmptyDeposit = "EMPTY_DEPOSIT";

    public const string ReconciliationNotFound = "RECONCILIATION_NOT_FOUND";
    public const string ReconciliationPending = "RECONCILIATION_PENDING";
    public const string ReconciliationIncomplete = "RECONCILIATION_INCOMPLETE";
    public const string ReconciliationNotAwaitingApproval = "RECONCILIATION_NOT_AWAITING_APPROVAL";
    public const string ReconciliationStale = "RECONCILIATION_STALE";
    public const string SameUserApproval = "SAME_USER_APPROVAL";
    public const string ExchangeRateMissing = "EXCHANGE_RATE_MISSING";
    public const string ExchangeRateReadOnly = "EXCHANGE_RATE_READ_ONLY";
}

/// <summary>Base type for all expected business errors. The API maps each subtype to an HTTP status.</summary>
public abstract class AtmException : Exception
{
    protected AtmException(string code, string message, IDictionary<string, object?>? extensions = null)
        : base(message)
    {
        Code = code;
        Extensions = extensions ?? new Dictionary<string, object?>();
    }

    public string Code { get; }

    /// <summary>Extra structured data added to the ProblemDetails response.</summary>
    public IDictionary<string, object?> Extensions { get; }
}

/// <summary>Malformed or invalid input (HTTP 400).</summary>
public sealed class ValidationException(string code, string message, IDictionary<string, object?>? extensions = null)
    : AtmException(code, message, extensions);

/// <summary>Resource does not exist (HTTP 404).</summary>
public sealed class NotFoundException(string code, string message, IDictionary<string, object?>? extensions = null)
    : AtmException(code, message, extensions);

/// <summary>Request conflicts with the current state of a resource (HTTP 409).</summary>
public sealed class ConflictException(string code, string message, IDictionary<string, object?>? extensions = null)
    : AtmException(code, message, extensions);

/// <summary>Request is well-formed but violates a business rule (HTTP 422).</summary>
public sealed class BusinessRuleException(string code, string message, IDictionary<string, object?>? extensions = null)
    : AtmException(code, message, extensions);

/// <summary>Caller is not permitted to perform this action (HTTP 403).</summary>
public sealed class ForbiddenException(string code, string message, IDictionary<string, object?>? extensions = null)
    : AtmException(code, message, extensions);
