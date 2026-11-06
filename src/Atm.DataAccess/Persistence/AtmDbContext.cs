using System.Data;
using System.Text.Json;
using Atm.Business.Abstractions;
using Atm.Business.Common;
using Atm.Business.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Atm.DataAccess.Persistence;

public sealed class AtmDbContext(DbContextOptions<AtmDbContext> options) : DbContext(options), IAtmDbContext
{
    public DbSet<AtmMachine> Atms => Set<AtmMachine>();
    public DbSet<Cassette> Cassettes => Set<Cassette>();
    public DbSet<CassetteBalance> CassetteBalances => Set<CassetteBalance>();
    public DbSet<CassetteMovement> Movements => Set<CassetteMovement>();
    public DbSet<WithdrawalQuote> Quotes => Set<WithdrawalQuote>();
    public DbSet<Deposit> Deposits => Set<Deposit>();
    public DbSet<Reconciliation> Reconciliations => Set<Reconciliation>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(AtmDbContext).Assembly);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareForSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareForSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Moves ledger movements and domain events raised by ATM aggregates into their tables, so they are written in
    /// the same database transaction as the balance changes that produced them (transactional outbox).
    /// </summary>
    private void PrepareForSave()
    {
        foreach (var entry in ChangeTracker.Entries<CassetteMovement>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException("Cassette movements are immutable and cannot be modified or deleted.");
            }
        }

        foreach (var atm in ChangeTracker.Entries<AtmMachine>().Select(e => e.Entity).ToList())
        {
            if (atm.PendingMovements.Count > 0)
            {
                Movements.AddRange(atm.PendingMovements);
            }

            foreach (var domainEvent in atm.DomainEvents)
            {
                OutboxMessages.Add(OutboxMessage.Create(
                    domainEvent.EventType,
                    JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), AtmJson.Options),
                    domainEvent.OccurredAt));
            }

            atm.ClearPendingChanges();
        }
    }

    public async Task<IAtmTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => new EfTransaction(await Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken));

    public async Task LockAtmAsync(Guid atmId, CancellationToken cancellationToken = default)
    {
        EnsureInTransaction();
        // UPDLOCK + HOLDLOCK: the lock is held until the transaction ends; competing writers for the same ATM wait here.
        await Database.ExecuteSqlInterpolatedAsync(
            $"SELECT Id FROM dbo.Atms WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE Id = {atmId}",
            cancellationToken);
    }

    public async Task LockCardLimitAsync(string cardHash, Currency currency, CancellationToken cancellationToken = default)
    {
        EnsureInTransaction();
        var resource = $"card-limit:{currency}:{cardHash}";
        var result = new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.Output };
        await Database.ExecuteSqlRawAsync(
            "EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000",
            [result, new SqlParameter("@resource", resource)],
            cancellationToken);

        if (result.Value is int code && code < 0)
        {
            throw new TimeoutException($"Could not acquire the daily-limit lock (sp_getapplock returned {code}).");
        }
    }

    public bool IsUniqueConstraintViolation(Exception exception)
        => exception is DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } };

    public void ClearChangeTracker() => ChangeTracker.Clear();

    private void EnsureInTransaction()
    {
        if (Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Locks can only be taken inside a transaction.");
        }
    }

    private sealed class EfTransaction(IDbContextTransaction transaction) : IAtmTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) => transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
