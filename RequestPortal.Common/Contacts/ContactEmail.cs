using System.Globalization;
using System.Net.Mail;

namespace RequestPortal.Common.Contacts;

public static class ContactEmail
{
    public static string? Normalize(string? value)
    {
        if(value is null) return null;
        // Check before trimming: CR/LF at either edge is still a header-injection attempt.
        if(value.Any(char.IsControl)) throw new ArgumentException("Email must not contain control characters or line breaks.");
        value=value.Trim();if(value.Length==0)return null;
        if(value.Length>320 || !MailAddress.TryCreate(value,out var parsed) || parsed.DisplayName.Length!=0 || parsed.Address!=value)
            throw new ArgumentException("Enter a valid email address, for example name@example.com.");
        string domain;
        try {domain=new IdnMapping().GetAscii(parsed.Host).ToLowerInvariant();}
        catch(ArgumentException) {throw new ArgumentException("Enter a valid email domain.");}
        if((!parsed.User.StartsWith('"') && (parsed.User.Contains("..") || parsed.User.StartsWith('.') || parsed.User.EndsWith('.'))) || parsed.User.Length>64 || domain.Length>253 || Uri.CheckHostName(domain)!=UriHostNameType.Dns ||
           domain.Split('.').Any(part=>part.Length is <1 or >63 || part.StartsWith('-') || part.EndsWith('-')))
            throw new ArgumentException("Enter a valid email address and domain.");
        string normalized=parsed.User+"@"+domain;
        if(normalized.Length>320)throw new ArgumentException("Email cannot exceed 320 characters.");
        return normalized;
    }
    public static string? Mailto(string? value)
    {
        try {var normalized=Normalize(value);return normalized is null?null:"mailto:"+Uri.EscapeDataString(normalized);}
        catch(ArgumentException) {return null;}
    }
}
