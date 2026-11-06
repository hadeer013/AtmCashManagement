namespace Atm.Business.Common;

/// <summary>Who did what, when and why — stamped onto every ledger movement.</summary>
public sealed record OperationContext(
    DateTimeOffset Timestamp,
    string? PerformedBy,
    string ReferenceType,
    Guid? ReferenceId = null);

public static class ReferenceTypes
{
    public const string Load = "LOAD";
    public const string Unload = "UNLOAD";
    public const string Quote = "WITHDRAWAL_QUOTE";
    public const string Deposit = "DEPOSIT";
    public const string Reconciliation = "RECONCILIATION";
    public const string StateChange = "STATE_CHANGE";
}
