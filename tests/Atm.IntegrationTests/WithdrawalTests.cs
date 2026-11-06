using System.Net;
using System.Text.Json;
using Atm.Business.Common;
using Atm.Business.Contracts;
using Atm.DataAccess.BackgroundJobs;
using Atm.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Atm.IntegrationTests.Infrastructure.AtmApiClient;

namespace Atm.IntegrationTests;

/// <summary>B4 quote/confirm rules and B5 daily limits.</summary>
[Collection(ApiCollection.Name)]
public class WithdrawalTests(AtmApiFactory factory)
{
    private readonly AtmApiClient _api = new(factory.CreateClient());

    [Fact]
    public async Task Alternatives_are_offered_and_the_chosen_one_is_dispensed()
    {
        // B3 #2 end-to-end: 50x1, 20x2, request 80 → LOWER 70 / UPPER 90.
        var atmId = await _api.CreateInServiceAtmAsync(null, (3, 1), (4, 2));
        var quote = await _api.QuoteAsync(atmId, NewCard(), Currency.TRY, 80);

        Assert.False(quote.ExactMatch);
        Assert.Equal([(QuoteOptionType.Lower, 70), (QuoteOptionType.Upper, 90)], quote.Options.Select(o => (o.Type, o.Amount)));
        var reserved = await _api.GetAtmAsync(atmId);
        Assert.Equal((1, 2), (Reserved(reserved, 3), Reserved(reserved, 4)));

        await AssertProblemAsync(await _api.ConfirmRawAsync(atmId, quote.QuoteId, "k-" + Guid.NewGuid()), HttpStatusCode.BadRequest, ErrorCodes.QuoteOptionRequired);

        var withdrawal = await ReadAsync<WithdrawalResponse>(
            await _api.ConfirmRawAsync(atmId, quote.QuoteId, "k-" + Guid.NewGuid(), QuoteOptionType.Upper));
        Assert.Equal(90, withdrawal.Amount);
        var atm = await _api.GetAtmAsync(atmId);
        Assert.Equal((0, 0), (Count(atm, 3), Count(atm, 4)));
    }

    [Fact]
    public async Task Reserved_notes_are_not_offered_in_another_quote()
    {
        var atmId = await _api.CreateInServiceAtmAsync(null, (2, 3));
        await _api.QuoteAsync(atmId, NewCard(), Currency.TRY, 200);

        var second = await _api.QuoteAsync(atmId, NewCard(), Currency.TRY, 200);

        Assert.False(second.ExactMatch);
        var lower = Assert.Single(second.Options);
        Assert.Equal((QuoteOptionType.Lower, 100), (lower.Type, lower.Amount));
    }

    [Fact]
    public async Task Expired_quote_cannot_be_confirmed_and_its_notes_are_released()
    {
        var atmId = await _api.CreateInServiceAtmAsync(null, (2, 10));
        var quote = await _api.QuoteAsync(atmId, NewCard(), Currency.TRY, 300);
        Assert.Equal(quote.CreatedAt.AddSeconds(60), quote.ExpiresAt);

        factory.Clock.Advance(TimeSpan.FromSeconds(61));

        await AssertProblemAsync(await _api.ConfirmRawAsync(atmId, quote.QuoteId, Guid.NewGuid().ToString()), HttpStatusCode.Conflict, ErrorCodes.QuoteExpired);
        var atm = await _api.GetAtmAsync(atmId);
        Assert.Equal((10, 0), (Count(atm, 2), Reserved(atm, 2)));
        Assert.Equal(QuoteStatus.Expired, (await _api.GetQuoteAsync(atmId, quote.QuoteId)).Status);
    }

    [Fact]
    public async Task Expiry_worker_releases_lapsed_reservations_automatically()
    {
        var atmId = await _api.CreateInServiceAtmAsync(null, (2, 10));
        var quote = await _api.QuoteAsync(atmId, NewCard(), Currency.TRY, 500);
        Assert.Equal(5, Reserved(await _api.GetAtmAsync(atmId), 2));

        factory.Clock.Advance(TimeSpan.FromSeconds(60));
        var released = await factory.Services.GetRequiredService<QuoteExpiryWorker>().SweepAsync(CancellationToken.None);

        Assert.True(released >= 1);
        Assert.Equal(0, Reserved(await _api.GetAtmAsync(atmId), 2));
        Assert.Equal(QuoteStatus.Expired, (await _api.GetQuoteAsync(atmId, quote.QuoteId)).Status);
        Assert.True((await _api.ConsistencyAsync(atmId)).IsConsistent);
    }

    [Fact]
    public async Task Cancelled_quote_cannot_be_confirmed_and_cancel_is_idempotent()
    {
        var atmId = await _api.CreateInServiceAtmAsync(null, (2, 10));
        var quote = await _api.QuoteAsync(atmId, NewCard(), Currency.TRY, 200);

        var cancelled = await ReadAsync<QuoteResponse>(await _api.CancelRawAsync(atmId, quote.QuoteId));
        var again = await ReadAsync<QuoteResponse>(await _api.CancelRawAsync(atmId, quote.QuoteId));

        Assert.Equal(QuoteStatus.Cancelled, cancelled.Status);
        Assert.Equal(QuoteStatus.Cancelled, again.Status);
        await AssertProblemAsync(await _api.ConfirmRawAsync(atmId, quote.QuoteId, Guid.NewGuid().ToString()), HttpStatusCode.Conflict, ErrorCodes.QuoteCancelled);
        Assert.Equal(0, Reserved(await _api.GetAtmAsync(atmId), 2));
    }

    [Fact]
    public async Task Repeated_confirm_with_same_key_returns_same_result_and_debits_once()
    {
        var atmId = await _api.CreateInServiceAtmAsync(null, (2, 10));
        var quote = await _api.QuoteAsync(atmId, NewCard(), Currency.TRY, 300);
        var key = Guid.NewGuid().ToString();

        var first = await _api.ConfirmRawAsync(atmId, quote.QuoteId, key);
        factory.Clock.Advance(TimeSpan.FromMinutes(5)); // even after the quote's validity window
        var second = await _api.ConfirmRawAsync(atmId, quote.QuoteId, key);

        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(7, Count(await _api.GetAtmAsync(atmId), 2));

        // A new key on an already-confirmed quote is rejected rather than debiting again.
        await AssertProblemAsync(await _api.ConfirmRawAsync(atmId, quote.QuoteId, Guid.NewGuid().ToString()), HttpStatusCode.Conflict, ErrorCodes.QuoteAlreadyConfirmed);
        Assert.Equal(7, Count(await _api.GetAtmAsync(atmId), 2));
    }

    [Fact]
    public async Task Confirm_requires_an_idempotency_key_and_a_key_cannot_be_reused_for_another_quote()
    {
        var atmId = await _api.CreateInServiceAtmAsync(null, (2, 10));
        var q1 = await _api.QuoteAsync(atmId, NewCard(), Currency.TRY, 100);
        var q2 = await _api.QuoteAsync(atmId, NewCard(), Currency.TRY, 100);

        await AssertProblemAsync(await _api.ConfirmRawAsync(atmId, q1.QuoteId, null), HttpStatusCode.BadRequest, ErrorCodes.IdempotencyKeyRequired);

        var key = Guid.NewGuid().ToString();
        await ReadAsync<WithdrawalResponse>(await _api.ConfirmRawAsync(atmId, q1.QuoteId, key));
        await AssertProblemAsync(await _api.ConfirmRawAsync(atmId, q2.QuoteId, key), HttpStatusCode.UnprocessableEntity, ErrorCodes.IdempotencyKeyReused);
    }

    [Fact]
    public async Task Daily_limit_counts_reserved_amounts_and_is_tracked_per_currency()
    {
        var config = DefaultConfig(slot2: (Currency.TRY, 500), slot5: (Currency.USD, 100));
        var atmId = await _api.CreateInServiceAtmAsync(config, (2, 60), (5, 20));
        var card = NewCard();

        var first = await _api.QuoteAsync(atmId, card, Currency.TRY, 15_000);
        Assert.Equal(5_000, first.RemainingDailyLimit);

        var problem = await AssertProblemAsync(await _api.QuoteRawAsync(atmId, card, Currency.TRY, 6_000), HttpStatusCode.UnprocessableEntity, ErrorCodes.DailyLimitExceeded);
        Assert.Equal(5_000, problem.GetProperty("remaining").GetInt32());

        // USD is tracked separately; another card is unaffected.
        await _api.QuoteAsync(atmId, card, Currency.USD, 1_000);
        await _api.QuoteAsync(atmId, NewCard(), Currency.TRY, 6_000);

        // Releasing the reservation frees the limit again.
        await ReadAsync<QuoteResponse>(await _api.CancelRawAsync(atmId, first.QuoteId));
        await _api.QuoteAsync(atmId, card, Currency.TRY, 6_000);
    }

    [Fact]
    public async Task Upper_alternative_is_not_offered_when_it_exceeds_the_daily_limit()
    {
        var config = DefaultConfig(slot4: (Currency.EUR, 20), slot5: (Currency.EUR, 50));
        var atmId = await _api.CreateInServiceAtmAsync(config, (4, 50), (5, 20));
        var card = NewCard();

        // EUR 970 confirmed → EUR 30 of the EUR 1,000 daily limit remains.
        var first = await _api.QuoteAsync(atmId, card, Currency.EUR, 970);
        await ReadAsync<WithdrawalResponse>(await _api.ConfirmRawAsync(atmId, first.QuoteId, Guid.NewGuid().ToString()));

        // EUR 25 requested: LOWER 20 is offered; the smallest payable amount above (40) exceeds the remaining 30.
        var quote = await _api.QuoteAsync(atmId, card, Currency.EUR, 25);

        var option = Assert.Single(quote.Options);
        Assert.Equal((QuoteOptionType.Lower, 20), (option.Type, option.Amount));

        // A different card with its full limit gets both alternatives for the same request.
        var other = await _api.QuoteAsync(atmId, NewCard(), Currency.EUR, 25);
        Assert.Equal([(QuoteOptionType.Lower, 20), (QuoteOptionType.Upper, 40)], other.Options.Select(o => (o.Type, o.Amount)));
    }

    [Fact]
    public async Task Card_numbers_are_not_stored_in_plain_text()
    {
        var atmId = await _api.CreateInServiceAtmAsync(null, (2, 10));
        var card = NewCard();
        var quote = await _api.QuoteAsync(atmId, card, Currency.TRY, 100);

        Assert.Equal($"**** **** **** {card[^4..]}", quote.Card);
        var stored = await factory.WithDbAsync(db => db.Quotes.AsNoTracking().SingleAsync(q => q.Id == quote.QuoteId));
        Assert.NotEqual(card, stored.CardHash);
        Assert.DoesNotContain(card, stored.CardHash);
        Assert.Equal(64, stored.CardHash.Length);
        Assert.DoesNotContain(card, JsonSerializer.Serialize(stored, AtmApiClient.Json));
    }
}
