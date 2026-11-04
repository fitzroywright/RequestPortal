using System.Text.RegularExpressions;

namespace RequestPortal.Application.Management;

public static class JamaicaParishes
{
    public const string Unknown = "Unknown / not provided";
    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(new[] {
        "Hanover", "Westmoreland", "St. James", "Trelawny", "St. Elizabeth", "Manchester", "Clarendon",
        "St. Catherine", "Kingston", "St. Andrew", "St. Thomas", "Portland", "St. Mary", "St. Ann"
    });
    private static readonly IReadOnlyDictionary<string, string> CanonicalNames =
        Names.ToDictionary(name => name.Replace(".", ""), name => name, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Unknown;
        string key = Regex.Replace(value.Trim().Replace('.', ' '), @"\s+", " ");
        if (key.EndsWith(" Parish", StringComparison.OrdinalIgnoreCase)) key = key[..^7].Trim();
        if (key.StartsWith("Saint ", StringComparison.OrdinalIgnoreCase)) key = "St " + key[6..];
        return CanonicalNames.GetValueOrDefault(key, Unknown);
    }
}
