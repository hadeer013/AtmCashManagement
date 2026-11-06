namespace Atm.Business.Common;

public enum Currency
{
    TRY,
    USD,
    EUR
}

public enum AtmState
{
    Unconfigured,
    Configured,
    Loaded,
    InService,
    Maintenance
}

public enum CassetteType
{
    Deposit,
    Recycling
}

public enum MovementType
{
    Load,
    Unload,
    Withdrawal,
    Deposit,
    Reservation,
    ReservationRelease,
    ReconciliationAdjustment
}

public enum QuoteStatus
{
    Active,
    Confirmed,
    Cancelled,
    Expired
}

public enum QuoteOptionType
{
    Exact,
    Lower,
    Upper
}

public enum ReconciliationStatus
{
    Applied,
    AwaitingApproval,
    Approved,
    Rejected
}
