using Atm.Business.Abstractions;
using Atm.Business.Common;
using Atm.Business.Domain;
using Microsoft.EntityFrameworkCore;

namespace Atm.Business.Services;

/// <summary>Releases reservations of quotes that expired or must be cancelled. Must run inside the ATM lock.</summary>
public sealed class QuoteReservationManager(IAtmDbContext db)
{
    public const string SystemUser = "system";

    public async Task<int> ExpireLapsedAsync(AtmMachine atm, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lapsed = await db.Quotes
            .Include(q => q.Reservations)
            .Where(q => q.AtmId == atm.Id && q.Status == QuoteStatus.Active && q.ExpiresAt <= now)
            .ToListAsync(cancellationToken);

        foreach (var quote in lapsed)
        {
            Expire(atm, quote, now);
        }

        return lapsed.Count;
    }

    public async Task<int> CancelAllActiveAsync(AtmMachine atm, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        var active = await db.Quotes
            .Include(q => q.Reservations)
            .Where(q => q.AtmId == atm.Id && q.Status == QuoteStatus.Active)
            .ToListAsync(cancellationToken);

        foreach (var quote in active)
        {
            if (quote.IsExpiredAt(now))
            {
                Expire(atm, quote, now);
            }
            else
            {
                Cancel(atm, quote, reason, now);
            }
        }

        return active.Count;
    }

    public static void Expire(AtmMachine atm, WithdrawalQuote quote, DateTimeOffset now)
    {
        atm.ReleaseReservation(quote.Currency, quote.GetReservedNotes(), Context(quote, now));
        quote.MarkExpired(now);
    }

    public static void Cancel(AtmMachine atm, WithdrawalQuote quote, string reason, DateTimeOffset now)
    {
        atm.ReleaseReservation(quote.Currency, quote.GetReservedNotes(), Context(quote, now));
        quote.MarkCancelled(reason, now);
    }

    private static OperationContext Context(WithdrawalQuote quote, DateTimeOffset now)
        => new(now, SystemUser, ReferenceTypes.Quote, quote.Id);
}
