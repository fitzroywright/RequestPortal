using System;
using System.Collections.Generic;
using System.Linq;

namespace RequestPortal.Application.Requests;

public static class ProfileIntakeChoices
{
    public static IReadOnlyList<string> Programs { get; } = Array.AsReadOnly(new[]
    {
        "PATH", "Poor Relief Dole", "Rehabilitation Programme", "Social Pension", "CARE", "None of the Above"
    });

    public static string NormalizePrograms(IReadOnlyList<string> values)
    {
        if (values.Count == 0) throw new ArgumentException("Choose a programme or None of the Above.");
        List<string> selected = values.Select(value => Programs.FirstOrDefault(option =>
            string.Equals(option, value?.Trim() == "None" ? "None of the Above" : string.Equals(value?.Trim(), "Solidarity Program", StringComparison.OrdinalIgnoreCase) ? "Social Pension" : value?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Assistance programmes contain an invalid selection."))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (selected.Contains("None of the Above") && selected.Count > 1)
            throw new ArgumentException("None of the Above cannot be selected with another programme.");
        return string.Join("|", Programs.Where(selected.Contains));
    }

    public static List<string> Parse(string? value, char separator) =>
        string.IsNullOrWhiteSpace(value) ? new List<string>() :
            value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(item => string.Equals(item, "None", StringComparison.OrdinalIgnoreCase) ? "None of the Above" : string.Equals(item, "Solidarity Program", StringComparison.OrdinalIgnoreCase) ? "Social Pension" : item).ToList();
}
