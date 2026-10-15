using System;
using System.Collections.Generic;
using System.Linq;

namespace RequestPortal.Application.Requests;

public static class QuickIntakeVictimCircumstances
{
    public const string None = "None of the Above";
    public static IReadOnlyList<string> Options { get; } = Array.AsReadOnly(new[]
    {
        "House Fire", "Flooding", "Natural Disaster",
        "Displacement due to civil unrest or safety", "Domestic Abuse", None
    });

    public static string Normalize(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            throw new ArgumentException("Select at least one victim circumstance or None of the Above.");
        List<string> selected = values.Select(value =>
            Options.FirstOrDefault(option => string.Equals(option, value?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("Victim circumstances contain an invalid selection."))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (selected.Contains(None) && selected.Count > 1)
            throw new ArgumentException("None of the Above cannot be selected with another victim circumstance.");
        return string.Join("; ", Options.Where(selected.Contains));
    }
}
