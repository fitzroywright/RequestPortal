using System;
using System.Collections.Generic;
using System.Linq;

namespace RequestPortal.Application.Requests;

public static class ProfileIntakeChoices
{
    public static IReadOnlyList<string> Programs { get; } = Array.AsReadOnly(new[]
    {
        "None", "PATH", "Poor Relief Dole", "Rehabilitation Programme", "Solidarity Program", "CARE"
    });

    public static string NormalizePrograms(IReadOnlyList<string> values)
    {
        if (values.Count == 0) throw new ArgumentException("Choose a programme or None.");
        List<string> selected = values.Select(value => Programs.FirstOrDefault(option =>
            string.Equals(option, value?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Assistance programmes contain an invalid selection."))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (selected.Contains("None") && selected.Count > 1)
            throw new ArgumentException("None cannot be selected with another programme.");
        return string.Join("|", Programs.Where(selected.Contains));
    }

    public static List<string> Parse(string? value, char separator) =>
        string.IsNullOrWhiteSpace(value) ? new List<string>() :
            value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
