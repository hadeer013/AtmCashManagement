using Atm.Business.Common;
using Atm.Business.Domain;
using Microsoft.EntityFrameworkCore;

namespace Atm.Business.Abstractions;

/// <summary>
/// Persistence port used by the business layer. Implemented by the data access layer on SQL Server.
/// The business layer depends on EF Core's provider-agnostic abstractions (DbSet / LINQ) but never on SQL Server.
/// </summary>
public interface IAtmDbContext
{
    DbSet<AtmMachine> Atms { get; }
    DbSet<Cassette> Cassettes { get; }
    DbSet<CassetteBalance> CassetteBalances { get; }
    DbSet<CassetteMovement> Movements { get; }
    DbSet<WithdrawalQuote> Quotes { get; }
    DbSet<Deposit> Deposits { get; }
    DbSet<Reconciliation> Reconciliations { get; }
    DbSet<ExchangeRate> ExchangeRates { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }
    DbSet<IdempotencyRecord> IdempotencyRecords { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<IAtmTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes an exclusive (update) lock on the ATM row for the rest of the current transaction. Every operation
    /// that changes cassette balances takes this lock first, which serialises them per ATM.
    /// </summary>
    Task LockAtmAsync(Guid atmId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes an exclusive lock on (card, currency) for the rest of the current transaction, so two quotes for the
    /// same card at different ATMs cannot both pass the daily-limit check.
    /// </summary>
    Task LockCardLimitAsync(string cardHash, Currency currency, CancellationToken cancellationToken = default);

    bool IsUniqueConstraintViolation(Exception exception);

    void ClearChangeTracker();
}

public interface IAtmTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);
}
