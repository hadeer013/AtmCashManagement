using Atm.Business.Abstractions;
using Atm.Business.Common;
using Atm.Business.Contracts;
using Atm.Business.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Atm.Business.Services;

/// <summary>
/// Manual count reconciliation with four-eyes approval. Differences up to the threshold are applied at once; above it
/// the reconciliation waits for a different user, and no adjustment touches the balances until it is approved.
/// </summary>
public sealed class ReconciliationService(
    IAtmDbContext db,
    AtmTransactionRunner runner,
    TimeProvider time,
    IOptions<AtmOptions> options)
{
    public async Task<ReconciliationResponse> CreateAsync(
        Guid atmId,
        string? user,
        CreateReconciliationRequest? request,
        CancellationToken cancellationToken)
    {
        var performedBy = RequireUser(user);
        var counted = request?.Cassettes?
            .Select(c => new CountedCassette(
                c.Slot,
                c.Count,
                c.Notes?.Select(n => new CountedNotes(n.Currency, n.Denomination, n.Count)).ToList()))
            .ToList();
        var now = time.GetUtcNow();

        return await runner.ExecuteAsync(atmId, async atm =>
        {
            if (await db.Reconciliations.AnyAsync(r => r.AtmId == atm.Id && r.Status == ReconciliationStatus.AwaitingApproval, cancellationToken))
            {
                throw new ConflictException(
                    ErrorCodes.ReconciliationPending,
                    "Another reconciliation for this ATM is awaiting approval; approve or reject it first.");
            }

            var counts = atm.CalculateReconciliation(counted);
            var rates = await db.ExchangeRates.AsNoTracking().ToDictionaryAsync(r => r.Currency, r => r.RateToTry, cancellationToken);

            var lines = counts.Select(c =>
            {
                if (!rates.TryGetValue(c.Currency, out var rate))
                {
                    throw new BusinessRuleException(ErrorCodes.ExchangeRateMissing, $"No exchange rate is configured for {c.Currency}.");
                }

                var valueTry = decimal.Round(c.Difference * c.Denomination * rate, 2);
                return new ReconciliationLine(
                    c.CassetteId, c.CassetteSlot, c.CassetteType, c.Currency, c.Denomination,
                    c.SystemCount, c.CountedCount, c.Difference, rate, valueTry);
            }).ToList();

            var reconciliation = Reconciliation.Create(atm.Id, performedBy, lines, options.Value.ReconciliationApprovalThresholdTry, now);
            if (reconciliation.Status == ReconciliationStatus.Applied)
            {
                atm.ApplyReconciliation(counts, new OperationContext(now, performedBy, ReferenceTypes.Reconciliation, reconciliation.Id));
            }

            db.Reconciliations.Add(reconciliation);
            return reconciliation.ToResponse();
        }, cancellationToken);
    }

    public async Task<ReconciliationResponse> ApproveAsync(Guid reconciliationId, string? user, ReconciliationDecisionRequest? request, CancellationToken cancellationToken)
    {
        var approver = RequireUser(user);
        var atmId = await GetAtmIdAsync(reconciliationId, cancellationToken);
        var now = time.GetUtcNow();

        return await runner.ExecuteAsync(atmId, async atm =>
        {
            var reconciliation = await db.Reconciliations.FirstAsync(r => r.Id == reconciliationId, cancellationToken);
            reconciliation.Approve(approver, request?.Comment, now);
            atm.ApplyReconciliation(
                reconciliation.ToCounts(),
                new OperationContext(now, approver, ReferenceTypes.Reconciliation, reconciliation.Id));
            return reconciliation.ToResponse();
        }, cancellationToken);
    }

    public async Task<ReconciliationResponse> RejectAsync(Guid reconciliationId, string? user, ReconciliationDecisionRequest? request, CancellationToken cancellationToken)
    {
        var rejecter = RequireUser(user);
        var atmId = await GetAtmIdAsync(reconciliationId, cancellationToken);
        var now = time.GetUtcNow();

        return await runner.ExecuteAsync(atmId, async _ =>
        {
            var reconciliation = await db.Reconciliations.FirstAsync(r => r.Id == reconciliationId, cancellationToken);
            reconciliation.Reject(rejecter, request?.Comment, now);
            return reconciliation.ToResponse();
        }, cancellationToken);
    }

    public async Task<ReconciliationResponse> GetAsync(Guid reconciliationId, CancellationToken cancellationToken)
    {
        var reconciliation = await db.Reconciliations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reconciliationId, cancellationToken)
                             ?? throw NotFound(reconciliationId);
        return reconciliation.ToResponse();
    }

    public async Task<List<ReconciliationResponse>> ListForAtmAsync(Guid atmId, CancellationToken cancellationToken)
    {
        var list = await db.Reconciliations.AsNoTracking()
            .Where(r => r.AtmId == atmId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
        return list.Select(r => r.ToResponse()).ToList();
    }

    private async Task<Guid> GetAtmIdAsync(Guid reconciliationId, CancellationToken cancellationToken)
    {
        var atmId = await db.Reconciliations.Where(r => r.Id == reconciliationId).Select(r => (Guid?)r.AtmId)
            .FirstOrDefaultAsync(cancellationToken);
        return atmId ?? throw NotFound(reconciliationId);
    }

    private static string RequireUser(string? user)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            throw new ValidationException(ErrorCodes.UserHeaderRequired, "The X-User header is required for reconciliation operations.");
        }

        var trimmed = user.Trim();
        return trimmed.Length <= 100
            ? trimmed
            : throw new ValidationException(ErrorCodes.ValidationFailed, "X-User must be at most 100 characters.");
    }

    private static NotFoundException NotFound(Guid id)
        => new(ErrorCodes.ReconciliationNotFound, $"Reconciliation {id} was not found.");
}
