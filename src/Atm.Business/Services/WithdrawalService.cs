using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atm.Business.Abstractions;
using Atm.Business.Algorithms;
using Atm.Business.Common;
using Atm.Business.Contracts;
using Atm.Business.Domain;
using Atm.Business.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Atm.Business.Services;

/// <summary>Two-step withdrawal: quote (select + reserve notes, check limit) and confirm/cancel.</summary>
public sealed class WithdrawalService(
    IAtmDbContext db,
    AtmTransactionRunner runner,
    QuoteReservationManager reservations,
    ICardHasher cardHasher,
    IBusinessCalendar calendar,
    TimeProvider time,
    IOptions<AtmOptions> options)
{
    private const int MaxIdempotencyKeyLength = 100;

    public async Task<QuoteResponse> CreateQuoteAsync(Guid atmId, CreateQuoteRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ValidationException(ErrorCodes.ValidationFailed, "Request body is required.");
        }

        if (!Enum.IsDefined(request.Currency))
        {
            throw new ValidationException(ErrorCodes.InvalidCurrency, "Currency must be TRY, USD or EUR.");
        }

        if (request.Amount <= 0)
        {
            throw new ValidationException(ErrorCodes.InvalidAmount, "Amount must be a positive whole number.");
        }

        var card = cardHasher.Protect(request.CardNumber);
        var now = time.GetUtcNow();
        var businessDate = calendar.GetBusinessDate(now);
        var dailyLimit = options.Value.GetDailyLimit(request.Currency);

        return await runner.ExecuteAsync(atmId, async atm =>
        {
            atm.EnsureState("request withdrawal quote", AtmState.InService);

            // Lazily release quotes that lapsed but have not been swept yet, so their notes are available again.
            await reservations.ExpireLapsedAsync(atm, now, cancellationToken);

            if (!atm.SupportsCurrency(request.Currency))
            {
                throw new BusinessRuleException(
                    ErrorCodes.CurrencyNotAvailable,
                    $"ATM {atm.Code} does not dispense {request.Currency}.");
            }

            // Lock order is always ATM → card, so concurrent quotes cannot deadlock.
            await db.LockCardLimitAsync(card.Hash, request.Currency, cancellationToken);
            var used = await GetUsedLimitAsync(card.Hash, request.Currency, businessDate, now, cancellationToken);
            var remaining = Math.Max(0, dailyLimit - used);
            if (request.Amount > remaining)
            {
                throw new BusinessRuleException(
                    ErrorCodes.DailyLimitExceeded,
                    $"The requested amount exceeds the remaining daily limit of {remaining} {request.Currency}.",
                    new Dictionary<string, object?>
                    {
                        ["dailyLimit"] = dailyLimit,
                        ["usedOrReserved"] = used,
                        ["remaining"] = remaining
                    });
            }

            var selection = NoteSelector.Select(atm.GetAvailableNotes(request.Currency), request.Amount, maxUpperAmount: remaining);
            if (!selection.HasAnyOption)
            {
                throw new BusinessRuleException(
                    ErrorCodes.NoPayableAmount,
                    $"ATM {atm.Code} cannot pay any amount in {request.Currency} at the moment.");
            }

            var quoteOptions = ToOptions(selection);
            var quote = WithdrawalQuote.Create(
                atm.Id, card.Hash, card.Last4, request.Currency, request.Amount, businessDate, quoteOptions,
                now, options.Value.QuoteTimeToLive);

            var reserved = atm.ReserveNotes(
                request.Currency,
                WithdrawalQuote.RequiredReservation(quoteOptions),
                new OperationContext(now, null, ReferenceTypes.Quote, quote.Id));
            quote.AttachReservations(reserved);
            db.Quotes.Add(quote);

            return ToResponse(quote, now, dailyLimit, remaining - quote.LimitHoldAmount);
        }, cancellationToken);
    }

    public async Task<QuoteResponse> GetQuoteAsync(Guid atmId, Guid quoteId, CancellationToken cancellationToken)
    {
        var quote = await db.Quotes.AsNoTracking()
                        .FirstOrDefaultAsync(q => q.Id == quoteId && q.AtmId == atmId, cancellationToken)
                    ?? throw QuoteNotFound(quoteId);
        return ToResponse(quote, time.GetUtcNow(), null, null);
    }

    public async Task<ConfirmResult> ConfirmAsync(
        Guid atmId,
        Guid quoteId,
        string? idempotencyKey,
        ConfirmQuoteRequest? request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ValidationException(ErrorCodes.IdempotencyKeyRequired, "The Idempotency-Key header is required to confirm a withdrawal.");
        }

        var key = idempotencyKey.Trim();
        if (key.Length > MaxIdempotencyKeyLength)
        {
            throw new ValidationException(ErrorCodes.ValidationFailed, $"Idempotency-Key must be at most {MaxIdempotencyKeyLength} characters.");
        }

        var requestHash = HashRequest(atmId, quoteId, request?.Option);
        var now = time.GetUtcNow();

        try
        {
            var outcome = await runner.ExecuteAsync(atmId, async atm =>
            {
                // Checked first and under the ATM lock: a retry returns the stored result even if the quote
                // has since expired, and two simultaneous retries cannot both debit the cash.
                var existing = await db.IdempotencyRecords.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Key == key, cancellationToken);
                if (existing is not null)
                {
                    return ConfirmOutcome.FromReplay(Replay(existing, requestHash));
                }

                var quote = await db.Quotes.Include(q => q.Reservations)
                                .FirstOrDefaultAsync(q => q.Id == quoteId && q.AtmId == atmId, cancellationToken)
                            ?? throw QuoteNotFound(quoteId);

                EnsureConfirmable(quote);
                if (quote.IsExpiredAt(now))
                {
                    // Release now and commit, then report the expiry to the caller.
                    QuoteReservationManager.Expire(atm, quote, now);
                    return ConfirmOutcome.FromFailure(QuoteExpired(quote));
                }

                atm.EnsureState("confirm withdrawal", AtmState.InService);
                var option = ResolveOption(quote, request?.Option);

                var dispensed = atm.Dispense(
                    quote.Currency,
                    quote.GetReservedNotes(),
                    option.Notes,
                    new OperationContext(now, null, ReferenceTypes.Quote, quote.Id));
                quote.MarkConfirmed(option, dispensed, now);

                var response = ToWithdrawalResponse(quote);
                db.IdempotencyRecords.Add(IdempotencyRecord.Create(
                    key, quote.Id, requestHash, JsonSerializer.Serialize(response, AtmJson.Options), now));
                return ConfirmOutcome.FromSuccess(response);
            }, cancellationToken);

            if (outcome.Failure is not null)
            {
                throw outcome.Failure;
            }

            return outcome.Result!;
        }
        catch (Exception ex) when (db.IsUniqueConstraintViolation(ex))
        {
            // The same key was used concurrently on another ATM; the other request won.
            db.ClearChangeTracker();
            var existing = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return Replay(existing, requestHash);
        }
    }

    public async Task<QuoteResponse> CancelAsync(Guid atmId, Guid quoteId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var (response, failure) = await runner.ExecuteAsync<(QuoteResponse?, AtmException?)>(atmId, async atm =>
        {
            var quote = await db.Quotes.Include(q => q.Reservations)
                            .FirstOrDefaultAsync(q => q.Id == quoteId && q.AtmId == atmId, cancellationToken)
                        ?? throw QuoteNotFound(quoteId);

            switch (quote.Status)
            {
                case QuoteStatus.Cancelled:
                    return (ToResponse(quote, now, null, null), (AtmException?)null); // idempotent
                case QuoteStatus.Confirmed:
                    throw new ConflictException(ErrorCodes.QuoteAlreadyConfirmed, $"Quote {quote.Id} is already confirmed and cannot be cancelled.");
                case QuoteStatus.Expired:
                    throw QuoteExpired(quote);
            }

            if (quote.IsExpiredAt(now))
            {
                QuoteReservationManager.Expire(atm, quote, now);
                return (null, QuoteExpired(quote));
            }

            QuoteReservationManager.Cancel(atm, quote, "CANCELLED_BY_CUSTOMER", now);
            return (ToResponse(quote, now, null, null), null);
        }, cancellationToken);

        if (failure is not null)
        {
            throw failure;
        }

        return response!;
    }

    /// <summary>Used by the background expiry worker.</summary>
    public async Task<int> ExpireLapsedQuotesAsync(Guid atmId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return await runner.ExecuteAsync(atmId, atm => reservations.ExpireLapsedAsync(atm, now, cancellationToken), cancellationToken);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<int> GetUsedLimitAsync(string cardHash, Currency currency, DateOnly businessDate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var forCard = db.Quotes.Where(q => q.CardHash == cardHash && q.Currency == currency && q.BusinessDate == businessDate);

        var confirmed = await forCard
            .Where(q => q.Status == QuoteStatus.Confirmed)
            .SumAsync(q => q.ConfirmedAmount, cancellationToken) ?? 0;

        // Reserved but not yet confirmed amounts count towards the limit too.
        var held = await forCard
            .Where(q => q.Status == QuoteStatus.Active && q.ExpiresAt > now)
            .SumAsync(q => (int?)q.LimitHoldAmount, cancellationToken) ?? 0;

        return confirmed + held;
    }

    private static List<QuoteOption> ToOptions(SelectionResult selection)
    {
        static QuoteOption Option(QuoteOptionType type, Breakdown b) => new(type, b.Amount, b.Notes.ToList());

        if (selection.Exact is not null)
        {
            return [Option(QuoteOptionType.Exact, selection.Exact)];
        }

        var result = new List<QuoteOption>();
        if (selection.Lower is not null)
        {
            result.Add(Option(QuoteOptionType.Lower, selection.Lower));
        }

        if (selection.Upper is not null)
        {
            result.Add(Option(QuoteOptionType.Upper, selection.Upper));
        }

        return result;
    }

    private static void EnsureConfirmable(WithdrawalQuote quote)
    {
        switch (quote.Status)
        {
            case QuoteStatus.Confirmed:
                throw new ConflictException(ErrorCodes.QuoteAlreadyConfirmed, $"Quote {quote.Id} has already been confirmed.");
            case QuoteStatus.Cancelled:
                throw new ConflictException(ErrorCodes.QuoteCancelled, $"Quote {quote.Id} was cancelled and cannot be confirmed.");
            case QuoteStatus.Expired:
                throw QuoteExpired(quote);
        }
    }

    private static QuoteOption ResolveOption(WithdrawalQuote quote, QuoteOptionType? requested)
    {
        if (requested is null)
        {
            if (quote.Options.Count == 1)
            {
                return quote.Options[0];
            }

            throw new ValidationException(
                ErrorCodes.QuoteOptionRequired,
                "The quote offers more than one alternative; specify 'option' (LOWER or UPPER).",
                new Dictionary<string, object?> { ["availableOptions"] = quote.Options.Select(o => o.Type.ToApiName()).ToArray() });
        }

        return quote.Options.FirstOrDefault(o => o.Type == requested)
               ?? throw new BusinessRuleException(
                   ErrorCodes.QuoteOptionNotAvailable,
                   $"Option {requested.Value.ToApiName()} is not part of this quote.",
                   new Dictionary<string, object?> { ["availableOptions"] = quote.Options.Select(o => o.Type.ToApiName()).ToArray() });
    }

    private static ConfirmResult Replay(IdempotencyRecord record, string requestHash)
    {
        if (record.RequestHash != requestHash)
        {
            throw new BusinessRuleException(
                ErrorCodes.IdempotencyKeyReused,
                "This Idempotency-Key was already used for a different request.");
        }

        var response = JsonSerializer.Deserialize<WithdrawalResponse>(record.ResponseJson, AtmJson.Options)
                       ?? throw new InvalidOperationException("Stored idempotent response could not be read.");
        return new ConfirmResult(response, Replayed: true);
    }

    private static string HashRequest(Guid atmId, Guid quoteId, QuoteOptionType? option)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"confirm|{atmId:N}|{quoteId:N}|{option?.ToString() ?? "-"}")));

    private static NotFoundException QuoteNotFound(Guid quoteId)
        => new(ErrorCodes.QuoteNotFound, $"Quote {quoteId} was not found for this ATM.");

    private static ConflictException QuoteExpired(WithdrawalQuote quote)
        => new(ErrorCodes.QuoteExpired, $"Quote {quote.Id} expired at {quote.ExpiresAt:O}; request a new quote.");

    private static QuoteResponse ToResponse(WithdrawalQuote quote, DateTimeOffset now, int? dailyLimit, int? remaining)
    {
        var exact = quote.Options.Any(o => o.Type == QuoteOptionType.Exact);
        var message = exact
            ? "The requested amount can be paid exactly."
            : "The requested amount cannot be paid exactly. " +
              string.Join(" ", quote.Options.Select(o => $"{o.Type.ToApiName()}: {o.Amount} {quote.Currency}."));
        return new QuoteResponse(
            quote.Id,
            quote.AtmId,
            quote.EffectiveStatus(now),
            $"**** **** **** {quote.CardLast4}",
            quote.Currency,
            quote.RequestedAmount,
            exact,
            message,
            quote.Options.Select(o => o.ToResponse()).ToList(),
            quote.CreatedAt,
            quote.ExpiresAt,
            dailyLimit,
            remaining,
            quote.ConfirmedOption,
            quote.ConfirmedAmount);
    }

    private static WithdrawalResponse ToWithdrawalResponse(WithdrawalQuote quote) => new(
        quote.Id,
        quote.Id,
        quote.AtmId,
        quote.Currency,
        quote.ConfirmedOption!.Value,
        quote.ConfirmedAmount!.Value,
        quote.DispensedNotes.Sum(n => n.Count),
        quote.DispensedNotes.Select(n => new DispensedNoteResponse(n.CassetteSlot, n.Denomination, n.Count)).ToList(),
        quote.ConfirmedAt!.Value);

    private sealed record ConfirmOutcome(ConfirmResult? Result, AtmException? Failure)
    {
        public static ConfirmOutcome FromSuccess(WithdrawalResponse response) => new(new ConfirmResult(response, false), null);
        public static ConfirmOutcome FromReplay(ConfirmResult replay) => new(replay, null);
        public static ConfirmOutcome FromFailure(AtmException failure) => new(null, failure);
    }
}
