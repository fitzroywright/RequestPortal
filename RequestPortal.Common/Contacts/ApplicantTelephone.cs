using PhoneNumbers;

namespace RequestPortal.Common.Contacts;

/// <summary>Shared intake and SQL-boundary validation. Never guesses missing digits or truncates input.</summary>
public static class ApplicantTelephone
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Any(c => !char.IsAsciiDigit(c) && c is not ('+' or '(' or ')' or '-' or '.' or ' ')))
            throw new ArgumentException("Telephone contains unsupported characters or an extension.");
        string number = new(value.Where(c => char.IsAsciiDigit(c) || c == '+').ToArray());
        if (number.Length == 0 || number.Count(c => c == '+') > 1 || number.LastIndexOf('+') > 0)
            throw new ArgumentException("Telephone requires digits and at most one leading plus.");
        // International access prefixes are accepted only without an existing country-code plus.
        if (number.StartsWith("011", StringComparison.Ordinal)) number = "+" + number[3..];
        else if (number.StartsWith("00", StringComparison.Ordinal)) number = "+" + number[2..];
        if (number.StartsWith("+0", StringComparison.Ordinal))
            throw new ArgumentException("Telephone country calling code cannot start with zero.");
        var util = PhoneNumberUtil.GetInstance();
        try
        {
            var parsed = util.Parse(number, "JM");
            if (parsed.HasExtension || !util.IsPossibleNumber(parsed))
                throw new ArgumentException("Telephone has an unsupported extension or impossible number length.");
            number = util.Format(parsed, PhoneNumberFormat.E164);
        }
        catch (NumberParseException)
        {
            throw new ArgumentException("Telephone could not be parsed. Include a valid country code.");
        }
        // The production SQL column includes the plus in its 15-character limit.
        if (number.Length > 15)
            throw new ArgumentException("Telephone exceeds the destination's 15-character limit including the plus.");
        return number;
    }
}
