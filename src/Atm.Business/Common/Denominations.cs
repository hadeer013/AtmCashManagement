using System.Text.Json;

namespace Atm.Business.Common;

public static class Denominations
{
    /// <summary>The only banknote denominations supported for every currency (no coins).</summary>
    public static readonly IReadOnlyList<int> All = [20, 50, 100, 500];

    public static bool IsValid(int denomination) => denomination is 20 or 50 or 100 or 500;

    public static void EnsureValid(int denomination)
    {
        if (!IsValid(denomination))
        {
            throw new ValidationException(ErrorCodes.InvalidDenomination,
                $"Denomination {denomination} is not supported. Supported denominations: {string.Join(", ", All)}.");
        }
    }
}

public static class CurrencyExtensions
{
    public static bool IsForeign(this Currency currency) => currency != Currency.TRY;

    public static bool IsDefined(this Currency currency) => Enum.IsDefined(currency);
}

public static class EnumNames
{
    /// <summary>Formats an enum the same way the API serialises it (e.g. InService → IN_SERVICE).</summary>
    public static string ToApiName<T>(this T value) where T : struct, Enum
        => JsonNamingPolicy.SnakeCaseUpper.ConvertName(value.ToString());
}
