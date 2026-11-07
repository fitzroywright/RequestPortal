using RequestPortal.Application.Requests;

namespace RequestPortal.Application.Integration;

/// <summary>Destination values for Quick Intake only; omitted old wire fields remain unknown.</summary>
public static class QuickIntakeApplicationFields
{
    public static bool AppliesTo(ExternalApplicationSubmission submission) =>
        string.Equals(submission.DocumentSource?.Trim(), "Request Portal - Quick Intake", StringComparison.OrdinalIgnoreCase);

    public static int? MaritalStatus(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "SINGLE" => 0, "MARRIED" => 1, "DIVORCED" => 2, "WIDOWED" => 3, _ => null
    };

    public static string? Dependents(bool? children) => children switch
    {
        true => "One or More", false => "None", _ => null
    };

    public static string? Programme(ExternalApplicationSubmission submission) => AppliesTo(submission)
        ? Selections(submission.CurrentAssistancePrograms, "CurrentAssistancePrograms", programmes: true) : submission.ProgramCategory;

    public static string? Circumstance(ExternalApplicationSubmission submission) => AppliesTo(submission)
        ? Selections(submission.VictimCircumstances, "VictimCircumstances", programmes: false) : null;

    private static string? Selections(IReadOnlyList<string>? values, string field, bool programmes)
    {
        if (values is null || values.Count == 0) return null;
        var selected = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())
            .Select(v => v.Equals("None", StringComparison.OrdinalIgnoreCase) ? "None of the Above" : v).ToArray();
        if (selected.Length == 0) return null;
        string normalized;
        try
        {
            // Reuse the actual capture choices, their legacy aliases, deduplication and option order.
            normalized = programmes ? ProfileIntakeChoices.NormalizePrograms(selected)
                : QuickIntakeVictimCircumstances.Normalize(selected);
        }
        catch (ArgumentException)
        {
            throw new ExternalApplicationValidationException(field, "Selections contain unsupported or incompatible choices.");
        }
        string joined = string.Join(", ", normalized.Split(programmes ? '|' : ';', StringSplitOptions.TrimEntries));
        if (joined.Length > 255 || joined.Any(char.IsControl))
            throw new ExternalApplicationValidationException(field, "Selections must fit the 255-character destination field without control characters.");
        return joined;
    }

    public static void Validate(ExternalApplicationSubmission submission)
    {
        if (!AppliesTo(submission)) return;
        // FFPJM Income is decimal(18,10): eight integer digits. The source is JMD to two decimals.
        if (submission.WeeklyIncome is { } income &&
            (income < 0 || income > 99999999.99m || decimal.Round(income, 2) != income))
            throw new ExternalApplicationValidationException("WeeklyIncome", "Weekly income must be non-negative, have at most two decimal places and fit the destination income field.");
        _ = Programme(submission);
        _ = Circumstance(submission);
    }
}
