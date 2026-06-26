namespace Stempeluhr.Api.Models;

public static class NfcCardIdNormalizer
{
    public static string? Normalize(string? cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId))
        {
            return null;
        }

        var normalized = new string(cardId
            .Where(char.IsAsciiHexDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
