using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using RequestPortal.Common.Security;

namespace RequestPortal.Infrastructure.Security;

public sealed class ActiveDirectoryStaffAuthenticator : IStaffDirectoryAuthenticator
{
    private readonly ActiveDirectoryOptions options;

    public ActiveDirectoryStaffAuthenticator(IOptions<ActiveDirectoryOptions> options)
    {
        this.options = options.Value;
    }

    public Task<StaffAuthenticationResult> AuthenticateAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Authenticate(userName, password, cancellationToken),
            cancellationToken);
    }

    private StaffAuthenticationResult Authenticate(
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            return new StaffAuthenticationResult(false, null, "INVALID_CREDENTIALS");
        }

        if (string.IsNullOrWhiteSpace(options.Server) || string.IsNullOrWhiteSpace(options.SearchBase))
        {
            return new StaffAuthenticationResult(false, null, "DIRECTORY_NOT_CONFIGURED");
        }

        string normalizedUserName = NormalizeUserName(userName);
        string bindIdentity = normalizedUserName.Contains('@', StringComparison.Ordinal)
            ? normalizedUserName
            : $"{normalizedUserName}@{options.Domain}";

        LdapDirectoryIdentifier identifier = new(options.Server, options.Port, true, false);

        using LdapConnection connection = new(identifier)
        {
            AuthType = AuthType.Negotiate,
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };

        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = options.RequireLdaps;

        try
        {
            NetworkCredential credential = new(bindIdentity, password);
            connection.Bind(credential);

            cancellationToken.ThrowIfCancellationRequested();

            string ldapFilter = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={EscapeFilterValue(normalizedUserName)}))";
            SearchRequest request = new(
                options.SearchBase,
                ldapFilter,
                SearchScope.Subtree,
                "sAMAccountName",
                "displayName",
                "mail",
                "memberOf");

            SearchResponse response = (SearchResponse)connection.SendRequest(request);

            if (response.Entries.Count != 1)
            {
                return new StaffAuthenticationResult(false, null, "DIRECTORY_IDENTITY_NOT_FOUND");
            }

            SearchResultEntry entry = response.Entries[0];
            string displayName = GetSingleAttribute(entry, "displayName") ?? normalizedUserName;
            string emailAddress = GetSingleAttribute(entry, "mail") ?? bindIdentity;
            IReadOnlyCollection<string> groups = GetMultiValueAttribute(entry, "memberOf")
                .Select(GetCommonNameFromDistinguishedName)
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .ToArray();

            StaffDirectoryIdentity identity = new(
                normalizedUserName,
                displayName,
                emailAddress,
                groups);

            return new StaffAuthenticationResult(true, identity);
        }
        catch (LdapException)
        {
            return new StaffAuthenticationResult(false, null, "INVALID_CREDENTIALS_OR_DIRECTORY_UNAVAILABLE");
        }
    }

    private static string NormalizeUserName(string userName)
    {
        string trimmed = userName.Trim();

        int slashIndex = trimmed.LastIndexOf('\\');
        if (slashIndex >= 0 && slashIndex < trimmed.Length - 1)
        {
            trimmed = trimmed[(slashIndex + 1)..];
        }

        int atIndex = trimmed.IndexOf('@');
        if (atIndex > 0)
        {
            trimmed = trimmed[..atIndex];
        }

        return trimmed;
    }

    private static string EscapeFilterValue(string value)
    {
        return value
            .Replace("\\", "\\5c", StringComparison.Ordinal)
            .Replace("*", "\\2a", StringComparison.Ordinal)
            .Replace("(", "\\28", StringComparison.Ordinal)
            .Replace(")", "\\29", StringComparison.Ordinal)
            .Replace("\0", "\\00", StringComparison.Ordinal);
    }

    private static string? GetSingleAttribute(SearchResultEntry entry, string name)
    {
        if (!entry.Attributes.Contains(name) || entry.Attributes[name].Count == 0)
        {
            return null;
        }

        return entry.Attributes[name][0]?.ToString();
    }

    private static IReadOnlyCollection<string> GetMultiValueAttribute(SearchResultEntry entry, string name)
    {
        if (!entry.Attributes.Contains(name))
        {
            return Array.Empty<string>();
        }

        List<string> values = new();
        foreach (object? value in entry.Attributes[name])
        {
            if (value is not null)
            {
                values.Add(value.ToString() ?? string.Empty);
            }
        }

        return values;
    }

    private static string GetCommonNameFromDistinguishedName(string distinguishedName)
    {
        const string prefix = "CN=";
        int commaIndex = distinguishedName.IndexOf(',');
        string firstPart = commaIndex >= 0 ? distinguishedName[..commaIndex] : distinguishedName;

        return firstPart.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? firstPart[prefix.Length..]
            : firstPart;
    }
}
