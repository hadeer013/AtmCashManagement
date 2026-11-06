using Atm.Business.Abstractions;
using Atm.Business.Common;
using Atm.Business.Contracts;
using Atm.Business.Domain;
using Microsoft.EntityFrameworkCore;

namespace Atm.Business.Services;

/// <summary>ATM setup and lifecycle: create, configure cassettes, load/unload cash, change state.</summary>
public sealed class AtmService(
    IAtmDbContext db,
    AtmTransactionRunner runner,
    QuoteReservationManager reservations,
    TimeProvider time)
{
    public async Task<AtmResponse> CreateAsync(CreateAtmRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ValidationException(ErrorCodes.ValidationFailed, "Request body is required.");
        }

        var atm = AtmMachine.Create(request.Code, request.Location, time.GetUtcNow());
        if (await db.Atms.AnyAsync(a => a.Code == atm.Code, cancellationToken))
        {
            throw new ConflictException(ErrorCodes.AtmCodeAlreadyExists, $"An ATM with code '{atm.Code}' already exists.");
        }

        db.Atms.Add(atm);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (db.IsUniqueConstraintViolation(ex))
        {
            throw new ConflictException(ErrorCodes.AtmCodeAlreadyExists, $"An ATM with code '{atm.Code}' already exists.");
        }

        return atm.ToResponse();
    }

    public async Task<AtmResponse> GetAsync(Guid atmId, CancellationToken cancellationToken)
        => (await runner.LoadAtmAsync(atmId, tracking: false, cancellationToken)).ToResponse();

    public async Task<List<AtmResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var atms = await db.Atms.AsNoTracking()
            .Include(a => a.Cassettes).ThenInclude(c => c.Balances)
            .OrderBy(a => a.Code)
            .ToListAsync(cancellationToken);
        return atms.Select(a => a.ToResponse()).ToList();
    }

    public async Task<AtmResponse> ConfigureCassettesAsync(Guid atmId, ConfigureCassettesRequest? request, string? user, CancellationToken cancellationToken)
    {
        var configurations = request?.Cassettes?
            .Select(c => new CassetteConfiguration(c.Slot, c.Type, c.Currency, c.Denomination))
            .ToList();

        await runner.ExecuteAsync(atmId, async atm =>
        {
            await EnsureNoPendingReconciliationAsync(atm.Id, cancellationToken);
            atm.ConfigureCassettes(configurations!, Context(user, "CONFIGURATION"));
        }, cancellationToken);

        return await GetAsync(atmId, cancellationToken);
    }

    public async Task<AtmResponse> LoadCashAsync(Guid atmId, LoadCashRequest? request, string? user, CancellationToken cancellationToken)
    {
        var items = request?.Items?.Select(i => new CashLoadItem(i.Slot, i.Count)).ToList();
        await runner.ExecuteAsync(atmId, async atm =>
        {
            await EnsureNoPendingReconciliationAsync(atm.Id, cancellationToken);
            atm.LoadCash(items, Context(user, ReferenceTypes.Load, Guid.NewGuid()));
        }, cancellationToken);

        return await GetAsync(atmId, cancellationToken);
    }

    public async Task<AtmResponse> UnloadCashAsync(Guid atmId, UnloadCashRequest? request, string? user, CancellationToken cancellationToken)
    {
        var items = request?.Items?.Select(i => new CashUnloadItem(i.Slot, i.Count)).ToList();
        await runner.ExecuteAsync(atmId, async atm =>
        {
            await EnsureNoPendingReconciliationAsync(atm.Id, cancellationToken);
            atm.UnloadCash(items, Context(user, ReferenceTypes.Unload, Guid.NewGuid()));
        }, cancellationToken);

        return await GetAsync(atmId, cancellationToken);
    }

    public async Task<AtmResponse> ChangeStateAsync(Guid atmId, ChangeStateRequest? request, string? user, CancellationToken cancellationToken)
    {
        if (request is null || !Enum.IsDefined(request.TargetState))
        {
            throw new ValidationException(ErrorCodes.ValidationFailed, "A valid targetState is required.");
        }

        await runner.ExecuteAsync(atmId, async atm =>
        {
            var now = time.GetUtcNow();
            if (request.TargetState == AtmState.Maintenance && atm.State == AtmState.InService)
            {
                // Notes must not stay reserved for customers while the cash is being handled.
                await reservations.CancelAllActiveAsync(atm, now, "ATM_SWITCHED_TO_MAINTENANCE", cancellationToken);
            }

            var pending = request.TargetState == AtmState.InService
                          && await HasPendingReconciliationAsync(atm.Id, cancellationToken);
            atm.TransitionTo(request.TargetState, pending, new OperationContext(now, user, ReferenceTypes.StateChange));
        }, cancellationToken);

        return await GetAsync(atmId, cancellationToken);
    }

    public async Task<List<MovementResponse>> GetMovementsAsync(
        Guid atmId,
        int? slot,
        MovementType? type,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        if (!await db.Atms.AnyAsync(a => a.Id == atmId, cancellationToken))
        {
            throw new NotFoundException(ErrorCodes.AtmNotFound, $"ATM {atmId} was not found.");
        }

        var query = db.Movements.AsNoTracking().Where(m => m.AtmId == atmId);
        if (slot is not null)
        {
            query = query.Where(m => m.CassetteSlot == slot);
        }

        if (type is not null)
        {
            query = query.Where(m => m.Type == type);
        }

        var movements = await query
            .OrderByDescending(m => m.Id)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);
        return movements.Select(m => m.ToResponse()).ToList();
    }

    private Task<bool> HasPendingReconciliationAsync(Guid atmId, CancellationToken cancellationToken)
        => db.Reconciliations.AnyAsync(r => r.AtmId == atmId && r.Status == ReconciliationStatus.AwaitingApproval, cancellationToken);

    private async Task EnsureNoPendingReconciliationAsync(Guid atmId, CancellationToken cancellationToken)
    {
        if (await HasPendingReconciliationAsync(atmId, cancellationToken))
        {
            throw new ConflictException(
                ErrorCodes.ReconciliationPending,
                "A reconciliation is awaiting approval; cash and configuration cannot change until it is approved or rejected.");
        }
    }

    private OperationContext Context(string? user, string referenceType, Guid? referenceId = null)
        => new(time.GetUtcNow(), user, referenceType, referenceId);
}
