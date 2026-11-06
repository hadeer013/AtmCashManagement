using System.Security.Cryptography;
using System.Text;
using Atm.Business.Common;
using Microsoft.Extensions.Options;

namespace Atm.Business.Security;

public sealed record ProtectedCard(string Hash, string Last4)
{
    public string Masked => $"**** **** **** {Last4}";
}

public interface ICardHasher
{
    ProtectedCard Protect(string? cardNumber);
}

/// <summary>
/// Card numbers are never stored. We keep HMAC-SHA256(secret, PAN) — a keyed hash, because a plain SHA-256 of a
/// 16-digit number with a known BIN can be brute-forced — plus the last 4 digits for display.
/// </summary>
public sealed class HmacCardHasher : ICardHasher
{
    private readonly byte[] _key;

    public HmacCardHasher(IOptions<CardHashingOptions> options)
    {
        if (string.IsNullOrWhiteSpace(options.Value.Secret))
        {
            throw new InvalidOperationException("CardHashing:Secret must be configured.");
        }

        _key = Encoding.UTF8.GetBytes(options.Value.Secret);
    }

    public ProtectedCard Protect(string? cardNumber)
    {
        var digits = Normalize(cardNumber);
        var hash = Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(digits)));
        return new ProtectedCard(hash, digits[^4..]);
    }

    private static string Normalize(string? cardNumber)
    {
        var digits = (cardNumber ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty);
        if (digits.Length is < 12 or > 19 || !digits.All(char.IsAsciiDigit))
        {
            throw new ValidationException(ErrorCodes.InvalidCardNumber, "Card number must contain 12 to 19 digits.");
        }

        return digits;
    }
}

public interface IBusinessCalendar
{
    DateOnly GetBusinessDate(DateTimeOffset instant);
}

public sealed class BusinessCalendar : IBusinessCalendar
{
    private readonly TimeZoneInfo _timeZone;

    public BusinessCalendar(IOptions<AtmOptions> options)
    {
        try
        {
            _timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.BusinessTimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            // Turkey has used a fixed UTC+3 offset since 2016; used when tzdata is not available.
            _timeZone = TimeZoneInfo.CreateCustomTimeZone("UTC+03", TimeSpan.FromHours(3), "UTC+03", "UTC+03");
        }
    }

    public DateOnly GetBusinessDate(DateTimeOffset instant)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, _timeZone).DateTime);
}
