using System.Net;
using Atm.Business.Common;
using Atm.Business.Contracts;
using Atm.IntegrationTests.Infrastructure;
using static Atm.IntegrationTests.Infrastructure.AtmApiClient;

namespace Atm.IntegrationTests;

/// <summary>B6: parallel requests against a real SQL Server must never reserve or debit the same physical note twice.</summary>
[Collection(ApiCollection.Name)]
public class ConcurrencyTests(AtmApiFactory factory)
{
    private readonly AtmApiClient _api = new(factory.CreateClient());

    [Fact]
    public async Task Parallel_quotes_at_the_same_atm_never_reserve_the_same_note_twice()
    {
        // Only 10 TRY 100 notes in the machine; 30 customers ask for TRY 100 at the same moment.
        var atmId = await _api.CreateInServiceAtmAsync(null, (2, 10));
        const int requests = 30;

        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, requests).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            return await _api.QuoteRawAsync(atmId, NewCard(), Currency.TRY, 100);
        })).ToList();
        start.Set();
        var responses = await Task.WhenAll(tasks);

        var succeeded = responses.Where(r => r.StatusCode == HttpStatusCode.Created).ToList();
        var rejected = responses.Where(r => r.StatusCode == HttpStatusCode.UnprocessableEntity).ToList();
        Assert.Equal(10, succeeded.Count);
        Assert.Equal(requests - 10, rejected.Count);
        foreach (var response in rejected)
        {
            await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ErrorCodes.NoPayableAmount);
        }

        var quotes = await Task.WhenAll(succeeded.Select(r => ReadAsync<QuoteResponse>(r, HttpStatusCode.Created)));
        Assert.All(quotes, q => Assert.Equal(100, Assert.Single(q.Options).Amount));

        var atm = await _api.GetAtmAsync(atmId);
        Assert.Equal(10, Count(atm, 2));
        Assert.Equal(10, Reserved(atm, 2));
        Assert.True((await _api.ConsistencyAsync(atmId)).IsConsistent);
    }

    [Fact]
    public async Task Parallel_quotes_with_mixed_amounts_never_over_reserve()
    {
        var atmId = await _api.CreateInServiceAtmAsync(null, (2, 15), (3, 15), (4, 20));
        var amounts = new[] { 370, 160, 500, 90, 1000, 240, 60, 130, 700, 220, 450, 80 };

        var responses = await Task.WhenAll(amounts.SelectMany(a => new[] { a, a })
            .Select(amount => Task.Run(() => _api.QuoteRawAsync(atmId, NewCard(), Currency.TRY, amount))));

        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.UnprocessableEntity }));
        var atm = await _api.GetAtmAsync(atmId);
        foreach (var cassette in atm.Cassettes.Where(c => c.Type == CassetteType.Recycling))
        {
            Assert.InRange(cassette.Reserved, 0, cassette.Count);
        }

        Assert.True((await _api.ConsistencyAsync(atmId)).IsConsistent);
    }

    [Fact]
    public async Task Parallel_confirms_with_the_same_idempotency_key_debit_the_cash_once()
    {
        var atmId = await _api.CreateInServiceAtmAsync(null, (2, 20));
        var quote = await _api.QuoteAsync(atmId, NewCard(), Currency.TRY, 500);
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => _api.ConfirmRawAsync(atmId, quote.QuoteId, key))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
        Assert.Single(responses, r => r.Headers.GetValues("Idempotent-Replayed").Single() == "false");

        var atm = await _api.GetAtmAsync(atmId);
        Assert.Equal(15, Count(atm, 2));
        Assert.Equal(0, Reserved(atm, 2));
    }

    [Fact]
    public async Task Same_card_at_two_atms_in_parallel_cannot_exceed_the_daily_limit()
    {
        var config = DefaultConfig(slot2: (Currency.TRY, 500));
        var atmA = await _api.CreateInServiceAtmAsync(config, (2, 40));
        var atmB = await _api.CreateInServiceAtmAsync(config, (2, 40));
        var card = NewCard();

        var responses = await Task.WhenAll(
            Task.Run(() => _api.QuoteRawAsync(atmA, card, Currency.TRY, 15_000)),
            Task.Run(() => _api.QuoteRawAsync(atmB, card, Currency.TRY, 15_000)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        await AssertProblemAsync(
            responses.Single(r => r.StatusCode != HttpStatusCode.Created),
            HttpStatusCode.UnprocessableEntity,
            ErrorCodes.DailyLimitExceeded);
    }
}
