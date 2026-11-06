using Atm.Business.Abstractions;
using Atm.Business.Common;
using Atm.Business.Contracts;
using Atm.Business.Domain;
using Microsoft.EntityFrameworkCore;

namespace Atm.Business.Services;

public sealed class ReportService(IAtmDbContext db, TimeProvider time)
{
    public async Task<OperationsReport> GetOperationsReportAsync(Guid? atmId, CancellationToken cancellationToken)
    {
        var query = db.Atms.AsNoTracking().Include(a => a.Cassettes).ThenInclude(c => c.Balances).AsQueryable();
        if (atmId is not null)
        {
            query = query.Where(a => a.Id == atmId);
        }

        var atms = await query.OrderBy(a => a.Code).ToListAsync(cancellationToken);
        if (atmId is not null && atms.Count == 0)
        {
            throw new NotFoundException(ErrorCodes.AtmNotFound, $"ATM {atmId} was not found.");
        }

        var rateEntities = await db.ExchangeRates.AsNoTracking().OrderBy(r => r.Currency).ToListAsync(cancellationToken);
        var rates = rateEntities.ToDictionary(r => r.Currency, r => r.RateToTry);

        var atmReports = atms.Select(atm => BuildAtmReport(atm, rates)).ToList();
        var networkTotals = BuildTotals(
            atms.SelectMany(a => a.Cassettes).SelectMany(c => c.Balances),
            rates);

        return new OperationsReport(
            time.GetUtcNow(),
            atmReports,
            networkTotals,
            networkTotals.Sum(t => t.AmountTry),
            rateEntities.Select(r => new ExchangeRateResponse(r.Currency, r.RateToTry, r.UpdatedAt)).ToList());
    }

    private static AtmReport BuildAtmReport(AtmMachine atm, IReadOnlyDictionary<Currency, decimal> rates)
    {
        var cassettes = atm.Cassettes.OrderBy(c => c.Slot).Select(c => new CassetteReport(
            c.Slot,
            c.Type,
            c.Currency,
            c.Denomination,
            Cassette.Capacity,
            c.TotalCount,
            c.TotalReserved,
            c.Available,
            c.IsRecycling && c.Available < AtmMachine.LowCashThreshold,
            c.ToNotes())).ToList();

        var balances = atm.Cassettes.SelectMany(c => c.Balances).ToList();
        var denominations = balances
            .GroupBy(b => (b.Currency, b.Denomination))
            .Where(g => g.Sum(b => b.Count) > 0)
            .OrderBy(g => g.Key.Currency)
            .ThenByDescending(g => g.Key.Denomination)
            .Select(g => new DenominationSummary(
                g.Key.Currency,
                g.Key.Denomination,
                g.Sum(b => b.Count),
                g.Sum(b => b.Reserved),
                g.Sum(b => b.Available),
                g.Sum(b => (long)b.Count) * g.Key.Denomination))
            .ToList();

        var totals = BuildTotals(balances, rates);
        return new AtmReport(atm.Id, atm.Code, atm.Location, atm.State, cassettes, denominations, totals, totals.Sum(t => t.AmountTry));
    }

    private static List<CurrencyTotal> BuildTotals(IEnumerable<CassetteBalance> balances, IReadOnlyDictionary<Currency, decimal> rates)
        => balances
            .GroupBy(b => b.Currency)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var amount = g.Sum(b => (long)b.Count * b.Denomination);
                var rate = rates.GetValueOrDefault(g.Key, g.Key == Currency.TRY ? 1m : 0m);
                return new CurrencyTotal(
                    g.Key,
                    g.Sum(b => b.Count),
                    amount,
                    g.Sum(b => (long)b.Reserved * b.Denomination),
                    g.Sum(b => (long)b.Available * b.Denomination),
                    rate,
                    decimal.Round(amount * rate, 2));
            })
            .ToList();
}

/// <summary>Rebuilds balances from the immutable ledger and compares them with the stored balances.</summary>
public sealed class ConsistencyService(IAtmDbContext db, TimeProvider time)
{
    public async Task<ConsistencyReport> CheckAsync(Guid? atmId, CancellationToken cancellationToken)
    {
        if (atmId is not null && !await db.Atms.AnyAsync(a => a.Id == atmId, cancellationToken))
        {
            throw new NotFoundException(ErrorCodes.AtmNotFound, $"ATM {atmId} was not found.");
        }

        var movements = db.Movements.AsNoTracking();
        if (atmId is not null)
        {
            movements = movements.Where(m => m.AtmId == atmId);
        }

        var ledger = await movements
            .GroupBy(m => new { m.AtmId, m.CassetteId, m.CassetteSlot, m.Currency, m.Denomination })
            .Select(g => new
            {
                g.Key.AtmId,
                g.Key.CassetteId,
                g.Key.CassetteSlot,
                g.Key.Currency,
                g.Key.Denomination,
                Count = g.Sum(m =>
                    m.Type == MovementType.Load || m.Type == MovementType.Deposit || m.Type == MovementType.ReconciliationAdjustment
                        ? m.Quantity
                        : m.Type == MovementType.Unload || m.Type == MovementType.Withdrawal
                            ? -m.Quantity
                            : 0),
                Reserved = g.Sum(m =>
                    m.Type == MovementType.Reservation
                        ? m.Quantity
                        : m.Type == MovementType.ReservationRelease
                            ? -m.Quantity
                            : 0)
            })
            .ToListAsync(cancellationToken);

        var cassettes = db.Cassettes.AsNoTracking();
        if (atmId is not null)
        {
            cassettes = cassettes.Where(c => c.AtmId == atmId);
        }

        var stored = await cassettes
            .SelectMany(c => c.Balances, (c, b) => new
            {
                c.AtmId,
                CassetteId = c.Id,
                CassetteSlot = c.Slot,
                b.Currency,
                b.Denomination,
                b.Count,
                b.Reserved
            })
            .ToListAsync(cancellationToken);

        var ledgerByKey = ledger.ToDictionary(l => (l.CassetteId, l.Currency, l.Denomination));
        var storedByKey = stored.ToDictionary(s => (s.CassetteId, s.Currency, s.Denomination));

        var differences = new List<BalanceDifference>();
        foreach (var key in ledgerByKey.Keys.Union(storedByKey.Keys))
        {
            ledgerByKey.TryGetValue(key, out var l);
            storedByKey.TryGetValue(key, out var s);
            var storedCount = s?.Count ?? 0;
            var storedReserved = s?.Reserved ?? 0;
            var ledgerCount = l?.Count ?? 0;
            var ledgerReserved = l?.Reserved ?? 0;
            if (storedCount != ledgerCount || storedReserved != ledgerReserved)
            {
                differences.Add(new BalanceDifference(
                    s?.AtmId ?? l!.AtmId,
                    key.CassetteId,
                    s?.CassetteSlot ?? l!.CassetteSlot,
                    key.Currency,
                    key.Denomination,
                    storedCount,
                    ledgerCount,
                    storedReserved,
                    ledgerReserved));
            }
        }

        return new ConsistencyReport(
            time.GetUtcNow(),
            differences.Count == 0,
            storedByKey.Count,
            differences.OrderBy(d => d.AtmId).ThenBy(d => d.CassetteSlot).ToList());
    }
}

public sealed class ExchangeRateService(IAtmDbContext db, TimeProvider time)
{
    public async Task<List<ExchangeRateResponse>> ListAsync(CancellationToken cancellationToken)
        => (await db.ExchangeRates.AsNoTracking().OrderBy(r => r.Currency).ToListAsync(cancellationToken))
            .Select(r => new ExchangeRateResponse(r.Currency, r.RateToTry, r.UpdatedAt))
            .ToList();

    public async Task<ExchangeRateResponse> UpdateAsync(Currency currency, UpdateExchangeRateRequest? request, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(currency))
        {
            throw new ValidationException(ErrorCodes.InvalidCurrency, "Currency must be TRY, USD or EUR.");
        }

        if (currency == Currency.TRY)
        {
            throw new BusinessRuleException(ErrorCodes.ExchangeRateReadOnly, "TRY is the base currency; its rate is always 1.");
        }

        if (request is null || request.RateToTry <= 0)
        {
            throw new ValidationException(ErrorCodes.ValidationFailed, "rateToTry must be a positive number.");
        }

        var now = time.GetUtcNow();
        var rate = await db.ExchangeRates.FirstOrDefaultAsync(r => r.Currency == currency, cancellationToken);
        if (rate is null)
        {
            rate = new ExchangeRate(currency, request.RateToTry, now);
            db.ExchangeRates.Add(rate);
        }
        else
        {
            rate.Update(request.RateToTry, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new ExchangeRateResponse(rate.Currency, rate.RateToTry, rate.UpdatedAt);
    }
}

public sealed class OutboxQueryService(IAtmDbContext db)
{
    public async Task<List<OutboxMessageResponse>> ListAsync(bool pendingOnly, int take, CancellationToken cancellationToken)
    {
        var query = db.OutboxMessages.AsNoTracking();
        if (pendingOnly)
        {
            query = query.Where(m => m.ProcessedAt == null);
        }

        return await query
            .OrderByDescending(m => m.OccurredAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(m => new OutboxMessageResponse(m.Id, m.Type, m.Payload, m.OccurredAt, m.ProcessedAt, m.Attempts, m.LastError))
            .ToListAsync(cancellationToken);
    }
}
