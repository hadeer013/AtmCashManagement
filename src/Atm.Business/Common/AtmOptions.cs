using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atm.Business.Common;

public sealed class AtmOptions
{
    public const string SectionName = "Atm";

    public int QuoteTimeToLiveSeconds { get; set; } = 60;

    /// <summary>Daily withdrawal limit per card, per currency.</summary>
    public Dictionary<Currency, int> DailyLimits { get; set; } = new()
    {
        [Currency.TRY] = 20_000,
        [Currency.USD] = 1_000,
        [Currency.EUR] = 1_000
    };

    /// <summary>Reconciliations whose absolute TRY difference exceeds this need a second user's approval.</summary>
    public decimal ReconciliationApprovalThresholdTry { get; set; } = 1_000m;

    /// <summary>Time zone that defines a "day" for daily limits (IANA or Windows id).</summary>
    public string BusinessTimeZone { get; set; } = "Europe/Istanbul";

    public TimeSpan QuoteTimeToLive => TimeSpan.FromSeconds(QuoteTimeToLiveSeconds);

    public int GetDailyLimit(Currency currency)
        => DailyLimits.TryGetValue(currency, out var limit)
            ? limit
            : throw new InvalidOperationException($"No daily limit configured for {currency}.");
}

public sealed class CardHashingOptions
{
    public const string SectionName = "CardHashing";

    /// <summary>Secret key (pepper) for HMAC-SHA256 of card numbers. Supply via environment/secret store.</summary>
    public string Secret { get; set; } = string.Empty;
}

public static class AtmJson
{
    /// <summary>Serializer settings shared by the API and by JSON columns / cached idempotent responses.</summary>
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        return options;
    }

    public static void Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false));
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    }
}
