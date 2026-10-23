using System.Globalization;

namespace EnvironmentBuilder.Core.Environments;

// Exact integer parsing for bounded format fields. Inline JSON numbers keep their original lexemes.
public static class ContractInteger
{
    public static bool TryRead(string token, out long value)
    {
        value = 0;
        if (token.Length == 0) { return false; }
        bool negative = token.StartsWith('-');
        string unsigned = negative ? token[1..] : token;
        int exponentAt = unsigned.IndexOfAny(['e', 'E']);
        string mantissa = exponentAt < 0 ? unsigned : unsigned[..exponentAt];
        int point = mantissa.IndexOf('.');
        if (mantissa.Length == 0 || point == 0 || point == mantissa.Length - 1 ||
            (point >= 0 && mantissa.LastIndexOf('.') != point) || (mantissa.Length > 1 && mantissa[0] == '0' && mantissa[1] != '.')) { return false; }
        for (int i = 0; i < mantissa.Length; i++)
        { if (i != point && mantissa[i] is < '0' or > '9') { return false; } }
        if (exponentAt >= 0)
        {
            string exponentToken = unsigned[(exponentAt + 1)..];
            if (exponentToken.StartsWith('+') || exponentToken.StartsWith('-')) { exponentToken = exponentToken[1..]; }
            if (exponentToken.Length == 0 || exponentToken.Any(c => c is < '0' or > '9')) { return false; }
        }
        int fractionalDigits = point < 0 ? 0 : mantissa.Length - point - 1;
        string digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) { return true; }
        int exponent = 0;
        if (exponentAt >= 0 && !int.TryParse(unsigned.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) { return false; }
        long scale = (long)exponent - fractionalDigits;
        if (scale < 0)
        {
            if (-scale >= digits.Length) { return false; }
            int end = digits.Length + (int)scale;
            if (digits.AsSpan(end).ContainsAnyExcept('0')) { return false; }
            digits = digits[..end];
        }
        else
        {
            if (digits.Length + scale > 19) { return false; }
            digits += new string('0', (int)scale);
        }
        return long.TryParse(negative ? "-" + digits : digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }
}
