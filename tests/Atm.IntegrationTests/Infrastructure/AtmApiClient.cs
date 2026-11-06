using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atm.Business.Common;
using Atm.Business.Contracts;

namespace Atm.IntegrationTests.Infrastructure;

/// <summary>Thin typed wrapper over the HTTP API used by the integration tests.</summary>
public sealed class AtmApiClient(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = AtmJson.Create();

    public HttpClient Http => http;

    public static string NewCard() => "4" + string.Concat(Enumerable.Range(0, 15).Select(_ => Random.Shared.Next(10)));

    public static List<CassetteConfigurationDto> DefaultConfig(
        (Currency, int) slot2 = default,
        (Currency, int) slot3 = default,
        (Currency, int) slot4 = default,
        (Currency, int) slot5 = default) =>
    [
        new(1, CassetteType.Deposit, null, null),
        Recycling(2, slot2 == default ? (Currency.TRY, 100) : slot2),
        Recycling(3, slot3 == default ? (Currency.TRY, 50) : slot3),
        Recycling(4, slot4 == default ? (Currency.TRY, 20) : slot4),
        Recycling(5, slot5 == default ? (Currency.USD, 100) : slot5)
    ];

    private static CassetteConfigurationDto Recycling(int slot, (Currency Currency, int Denomination) c)
        => new(slot, CassetteType.Recycling, c.Currency, c.Denomination);

    public async Task<AtmResponse> CreateAtmAsync(string? code = null)
    {
        var response = await http.PostAsJsonAsync("/api/atms", new CreateAtmRequest(code ?? $"ATM-{Guid.NewGuid():N}"[..20], "Test"), Json);
        return await ReadAsync<AtmResponse>(response, HttpStatusCode.Created);
    }

    public async Task<AtmResponse> ConfigureAsync(Guid atmId, List<CassetteConfigurationDto>? config = null)
        => await ReadAsync<AtmResponse>(await http.PutAsJsonAsync($"/api/atms/{atmId}/cassettes", new ConfigureCassettesRequest(config ?? DefaultConfig()), Json));

    public async Task<AtmResponse> LoadAsync(Guid atmId, params (int Slot, int Count)[] items)
        => await ReadAsync<AtmResponse>(await http.PostAsJsonAsync(
            $"/api/atms/{atmId}/cash-loads",
            new LoadCashRequest(items.Select(i => new CashLoadItemDto(i.Slot, i.Count)).ToList()), Json));

    public Task<HttpResponseMessage> SetStateRawAsync(Guid atmId, AtmState state)
        => http.PostAsJsonAsync($"/api/atms/{atmId}/state", new ChangeStateRequest(state), Json);

    public async Task<AtmResponse> SetStateAsync(Guid atmId, AtmState state)
        => await ReadAsync<AtmResponse>(await SetStateRawAsync(atmId, state));

    public async Task<Guid> CreateInServiceAtmAsync(List<CassetteConfigurationDto>? config, params (int Slot, int Count)[] loads)
    {
        var atm = await CreateAtmAsync();
        await ConfigureAsync(atm.Id, config);
        await LoadAsync(atm.Id, loads);
        await SetStateAsync(atm.Id, AtmState.InService);
        return atm.Id;
    }

    public Task<HttpResponseMessage> QuoteRawAsync(Guid atmId, string card, Currency currency, int amount)
        => http.PostAsJsonAsync($"/api/atms/{atmId}/withdrawal-quotes", new CreateQuoteRequest(card, currency, amount), Json);

    public async Task<QuoteResponse> QuoteAsync(Guid atmId, string card, Currency currency, int amount)
        => await ReadAsync<QuoteResponse>(await QuoteRawAsync(atmId, card, currency, amount), HttpStatusCode.Created);

    public Task<HttpResponseMessage> ConfirmRawAsync(Guid atmId, Guid quoteId, string? idempotencyKey, QuoteOptionType? option = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/atms/{atmId}/withdrawal-quotes/{quoteId}/confirm")
        {
            Content = JsonContent.Create(new ConfirmQuoteRequest(option), options: Json)
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return http.SendAsync(request);
    }

    public Task<HttpResponseMessage> CancelRawAsync(Guid atmId, Guid quoteId)
        => http.PostAsync($"/api/atms/{atmId}/withdrawal-quotes/{quoteId}/cancel", null);

    public async Task<DepositResponse> DepositAsync(Guid atmId, int unrecognized, params (Currency Currency, int Denomination, int Count)[] notes)
        => await ReadAsync<DepositResponse>(
            await http.PostAsJsonAsync(
                $"/api/atms/{atmId}/deposits",
                new DepositRequest(null, notes.Select(n => new DepositNoteDto(n.Currency, n.Denomination, n.Count)).ToList(), unrecognized),
                Json),
            HttpStatusCode.Created);

    public Task<HttpResponseMessage> ReconcileRawAsync(Guid atmId, string user, List<CountedCassetteDto> cassettes)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/atms/{atmId}/reconciliations")
        {
            Content = JsonContent.Create(new CreateReconciliationRequest(cassettes), options: Json)
        };
        request.Headers.Add("X-User", user);
        return http.SendAsync(request);
    }

    public Task<HttpResponseMessage> DecideRawAsync(Guid reconciliationId, string decision, string? user)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/reconciliations/{reconciliationId}/{decision}")
        {
            Content = JsonContent.Create(new ReconciliationDecisionRequest(null), options: Json)
        };
        if (user is not null)
        {
            request.Headers.Add("X-User", user);
        }

        return http.SendAsync(request);
    }

    public async Task<AtmResponse> GetAtmAsync(Guid atmId) => await ReadAsync<AtmResponse>(await http.GetAsync($"/api/atms/{atmId}"));

    public async Task<OperationsReport> ReportAsync(Guid atmId)
        => await ReadAsync<OperationsReport>(await http.GetAsync($"/api/reports/operations?atmId={atmId}"));

    public async Task<ConsistencyReport> ConsistencyAsync(Guid atmId)
        => await ReadAsync<ConsistencyReport>(await http.GetAsync($"/api/atms/{atmId}/consistency-check"));

    public async Task<QuoteResponse> GetQuoteAsync(Guid atmId, Guid quoteId)
        => await ReadAsync<QuoteResponse>(await http.GetAsync($"/api/atms/{atmId}/withdrawal-quotes/{quoteId}"));

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected} but got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    /// <summary>Asserts a ProblemDetails error response and returns its body.</summary>
    public static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string errorCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status} but got {(int)response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(errorCode, json.GetProperty("errorCode").GetString());
        Assert.Equal((int)status, json.GetProperty("status").GetInt32());
        return json;
    }

    public static int Count(AtmResponse atm, int slot) => atm.Cassettes.Single(c => c.Slot == slot).Count;

    public static int Reserved(AtmResponse atm, int slot) => atm.Cassettes.Single(c => c.Slot == slot).Reserved;
}
