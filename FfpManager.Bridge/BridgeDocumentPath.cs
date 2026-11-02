namespace FfpManager.Bridge;

public static class BridgeDocumentPath
{
    public static string SafeFileName(string value)
    {
        string name = Path.GetFileName(value.Replace('\\', '/'));
        foreach (char invalid in "<>:\"/\\|?*".Concat(Enumerable.Range(0, 32).Select(i => (char)i)))
            name = name.Replace(invalid, '_');
        name = name.TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(name)) return "document.bin";
        string stem = name.Split('.')[0];
        if (System.Text.RegularExpressions.Regex.IsMatch(stem, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            name = "_" + name;
        // Leave room for the stable application/document identity prefix on CIFS.
        return name.Length <= 160 ? name : name[..160];
    }

    public static string ForManager(BridgeOptions options, string localPath)
    {
        string root = options.SupportingDocumentManagerRoot.TrimEnd('\\');
        if (!root.StartsWith(@"\\", StringComparison.Ordinal) || root.Contains('/') || root.Split('\\').Any(part => part is "." or ".."))
            throw new InvalidOperationException("Bridge:SupportingDocumentManagerRoot must be an approved absolute UNC directory.");
        string[] components = root[2..].Split('\\');
        if (components.Length < 2 || components.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Bridge:SupportingDocumentManagerRoot must include a server and share.");
        string name = Path.GetFileName(localPath);
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\'))
            throw new InvalidOperationException("Supporting document file name is invalid for FFP Manager.");
        string result = root + @"\" + name;
        if (result.Length > 1024) throw new InvalidOperationException("Supporting document path exceeds the FFP SQL field length.");
        return result;
    }
}
