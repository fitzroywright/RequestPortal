using PhoneNumbers;

namespace RequestPortal.Common.Contacts;

/// <summary>Shared intake and SQL-boundary validation. Never guesses missing digits or truncates input.</summary>
public static class ApplicantTelephone
{
    // New user input uses numbering-plan validation. Normalize remains the legacy integration boundary.
    public static string? NormalizeNew(string? value)
    {
        if(value?.Length>100)throw new ArgumentException("Telephone input cannot exceed 100 characters.");
        if(!string.IsNullOrWhiteSpace(value))
        {
            string digits=new(value.Where(char.IsAsciiDigit).ToArray());
            bool explicitCountry=value.TrimStart().StartsWith('+') || digits.StartsWith("00") || digits.StartsWith("011") || (digits.Length==11 && digits.StartsWith('1'));
            if(!explicitCountry && (digits.Length!=10 || !(digits.StartsWith("876") || digits.StartsWith("658"))))
                throw new ArgumentException("Use a Jamaican 876/658 number, or include the international country code.");
        }
        string? canonical=Normalize(value);if(canonical is null)return null;
        var util=PhoneNumberUtil.GetInstance();
        if(!util.IsValidNumber(util.Parse(canonical,"JM")))
            throw new ArgumentException("Enter a valid telephone number; Jamaica uses area code 876 or 658.");
        return canonical;
    }
    public static string? Display(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return value;
        try {var util=PhoneNumberUtil.GetInstance();var parsed=util.Parse(value,"JM");return util.IsValidNumber(parsed)?util.Format(parsed,PhoneNumberFormat.INTERNATIONAL):value;}
        catch(NumberParseException) {return value;}
    }
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
