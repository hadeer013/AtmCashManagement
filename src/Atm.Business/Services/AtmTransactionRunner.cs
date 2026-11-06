using Atm.Business.Abstractions;
using Atm.Business.Common;
using Atm.Business.Domain;
using Microsoft.EntityFrameworkCore;

namespace Atm.Business.Services;

/// <summary>
/// Unit of work for every operation that changes an ATM's cash:
///   BEGIN TRAN → lock ATM row (UPDLOCK) → load aggregate → run use case → SaveChanges (balances + ledger + outbox) → COMMIT.
/// Because every writer takes the same row lock first, operations on one ATM are serialised and two concurrent
/// quotes can never see (and reserve) the same available notes. Different ATMs do not block each other.
/// </summary>
public sealed class AtmTransactionRunner(IAtmDbContext db)
{
    public async Task<T> ExecuteAsync<T>(Guid atmId, Func<AtmMachine, Task<T>> action, CancellationToken cancellationToken)
    {
        await using var transaction = await db.BeginTransactionAsync(cancellationToken);
        await db.LockAtmAsync(atmId, cancellationToken);
        var atm = await LoadAtmAsync(atmId, tracking: true, cancellationToken);

        var result = await action(atm);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task ExecuteAsync(Guid atmId, Func<AtmMachine, Task> action, CancellationToken cancellationToken)
        => ExecuteAsync<bool>(atmId, async atm =>
        {
            await action(atm);
            return true;
        }, cancellationToken);

    public async Task<AtmMachine> LoadAtmAsync(Guid atmId, bool tracking, CancellationToken cancellationToken)
    {
        IQueryable<AtmMachine> query = db.Atms.Include(a => a.Cassettes).ThenInclude(c => c.Balances);
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query.FirstOrDefaultAsync(a => a.Id == atmId, cancellationToken)
               ?? throw new NotFoundException(ErrorCodes.AtmNotFound, $"ATM {atmId} was not found.");
    }
}
