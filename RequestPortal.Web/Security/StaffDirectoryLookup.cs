using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Secrets;
using Common.Security.Models;
using Microsoft.Extensions.Options;

namespace RequestPortal.Web.Security;

public sealed class StaffDirectoryOptions
{
    public const string SectionName = "Authentication:StaffDirectory";
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecretName { get; set; } = "Authentication:StaffDirectory:ClientSecret";
    public string TenantId { get; set; } = "";
    // Operator attests that Keycloak's OIDC sub is the UserRepresentation.id, with no pairwise/custom subject mapper.
    public bool KeycloakSubjectMappingVerified { get; set; }
}

public sealed record DirectoryStaffIdentity(string Id, string UserName, string DisplayName, string? Email,
    bool Enabled, ExternalIdentityKey Key, AuthenticationProviderKind Kind);

public interface IStaffDirectoryLookup
{
    string? UnavailableReason { get; }
    string ConfigurationKey { get; }
    Task<IReadOnlyList<DirectoryStaffIdentity>> SearchAsync(string query, CancellationToken cancellationToken);
    Task<DirectoryStaffIdentity?> FindAsync(string id, CancellationToken cancellationToken);
}

/// <summary>Read-only provider lookup. Directory data is never treated as an authenticated sign-in.</summary>
public sealed class HttpStaffDirectoryLookup(HttpClient http, IOptions<StaffDirectoryOptions> directoryOptions,
    IOptions<StaffOidcOptions> staffOptions, ISecretProvider secrets) : IStaffDirectoryLookup
{
    private readonly StaffDirectoryOptions options = directoryOptions.Value;
    private readonly StaffOidcOptions staff = staffOptions.Value;
    private string Issuer => (string.IsNullOrWhiteSpace(staff.Issuer) ? staff.Authority : staff.Issuer).TrimEnd('/');
    public string ConfigurationKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
        options.Enabled, options.Provider, options.ClientId, options.ClientSecretName, options.TenantId, options.KeycloakSubjectMappingVerified,
        StaffSignInEnabled=staff.Enabled, staff.ProviderId, staff.Authority, Issuer, staff.AllowedTenantIds }))));

    public string? UnavailableReason
    {
        get {
            if (!options.Enabled) return "Directory pre-provisioning is not configured. An operator must enable the approved read-only directory connection.";
            if (!staff.Enabled || string.IsNullOrWhiteSpace(staff.ProviderId) || string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.ClientSecretName))
                return "Directory pre-provisioning requires staff sign-in and a read-only directory client.";
            if (!Https(Issuer) || !Https(staff.Authority)) return "The staff identity provider must use HTTPS.";
            if (options.Provider == "Keycloak") {
                var marker = Issuer.LastIndexOf("/realms/", StringComparison.Ordinal);
                if (staff.AllowedTenantIds.Length != 0 || !options.KeycloakSubjectMappingVerified || marker < 8 ||
                    Issuer != staff.Authority.TrimEnd('/') || Issuer[(marker+8)..].Any(c => c is '/' or '?' or '#'))
                    return "Keycloak pre-provisioning requires the configured realm issuer and a verified user-ID to OIDC-sub mapping.";
            }
            else if (options.Provider == "Entra") {
                if (!Guid.TryParse(options.TenantId, out var tenant) || !staff.AllowedTenantIds.Any(x => Guid.TryParse(x,out var allowed) && allowed == tenant) ||
                    !EntraAuthority(staff.Authority, tenant) || !EntraAuthority(Issuer, tenant))
                    return "Entra pre-provisioning requires an explicitly allowed tenant matching the staff sign-in authority and issuer.";
            }
            else return "Choose an approved Keycloak or Entra directory connection.";
            return null;
        }
    }

    public async Task<IReadOnlyList<DirectoryStaffIdentity>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        RequireConfigured();
        query = query.Trim();
        if (query.Length is < 3 or > 100 || query.Any(char.IsControl)) throw new InvalidOperationException("Enter 3–100 characters to search the directory.");
        string url;
        if (options.Provider == "Keycloak") url = UsersUrl + "?max=21&briefRepresentation=true&search=" + Uri.EscapeDataString(query);
        else {
            string literal = query.Replace("'", "''", StringComparison.Ordinal);
            string filter = $"startswith(displayName,'{literal}') or startswith(userPrincipalName,'{literal}') or startswith(mail,'{literal}')";
            url = UsersUrl + "?$select=" + GraphFields + "&$top=21&$filter=" + Uri.EscapeDataString(filter);
        }
        using var json = await GetAsync(url, cancellationToken);
        if (json is null) return [];
        var rows = options.Provider == "Keycloak" ? json.RootElement : json.RootElement.GetProperty("value");
        return rows.EnumerateArray().Select(Parse).Take(21).ToArray();
    }

    public async Task<DirectoryStaffIdentity?> FindAsync(string id, CancellationToken cancellationToken)
    {
        RequireConfigured();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 500 || id is "." or ".." || id.Any(c => char.IsControl(c) || c is '/' or '\\'))
            throw new InvalidOperationException("Invalid directory selection. Search again.");
        if (options.Provider == "Entra" && !Guid.TryParse(id,out _)) throw new InvalidOperationException("Invalid Entra identity.");
        using var json = await GetAsync(UsersUrl + "/" + Uri.EscapeDataString(id) + (options.Provider == "Entra" ? "?$select=" + GraphFields : ""), cancellationToken);
        return json is null ? null : Parse(json.RootElement);
    }

    private const string GraphFields = "id,displayName,userPrincipalName,mail,accountEnabled";
    private string UsersUrl => options.Provider == "Entra" ? "https://graph.microsoft.com/v1.0/users"
        : Issuer[..Issuer.LastIndexOf("/realms/",StringComparison.Ordinal)] + "/admin" + Issuer[Issuer.LastIndexOf("/realms/",StringComparison.Ordinal)..] + "/users";
    private void RequireConfigured() { if (UnavailableReason is { } reason) throw new InvalidOperationException(reason); }
    private static bool Https(string value) => Uri.TryCreate(value,UriKind.Absolute,out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    private static bool EntraAuthority(string value, Guid tenant) => Https(value) && Uri.TryCreate(value,UriKind.Absolute,out var uri) && uri.Host == "login.microsoftonline.com" && uri.IsDefaultPort &&
        (uri.AbsolutePath.TrimEnd('/').Equals("/"+tenant.ToString("D"),StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.TrimEnd('/').Equals("/"+tenant.ToString("D")+"/v2.0",StringComparison.OrdinalIgnoreCase));

    private async Task<JsonDocument?> GetAsync(string url, CancellationToken cancellationToken)
    {
        try {
            string secret;
            try { secret = await secrets.GetRequiredAsync(options.ClientSecretName,cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new InvalidOperationException("The directory client credential is unavailable. An operator must check its Common.Secrets reference."); }
            string tokenUrl = options.Provider == "Entra"
                ? $"https://login.microsoftonline.com/{Guid.Parse(options.TenantId):D}/oauth2/v2.0/token" : Issuer + "/protocol/openid-connect/token";
            var fields = new Dictionary<string,string> { ["grant_type"]="client_credentials", ["client_id"]=options.ClientId, ["client_secret"]=secret };
            if (options.Provider == "Entra") fields["scope"]="https://graph.microsoft.com/.default";
            using var response = await http.PostAsync(tokenUrl,new FormUrlEncodedContent(fields),cancellationToken);
            EnsureSuccess(response);
            using var tokenJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            string token = tokenJson.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("The directory did not supply an access token.");
            using var request = new HttpRequestMessage(HttpMethod.Get,url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",token);
            using var result = await http.SendAsync(request,cancellationToken);
            if (result.StatusCode == HttpStatusCode.NotFound) return null;
            EnsureSuccess(result);
            return JsonDocument.Parse(await result.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (HttpRequestException) { throw new InvalidOperationException("The directory connection is unavailable. No staff account was changed."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new InvalidOperationException("The directory lookup timed out. Try again."); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException) { throw new InvalidOperationException("The directory returned an invalid response. No staff account was changed."); }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Directory lookup failed (HTTP {(int)response.StatusCode}). Check the read-only client and its directory permissions.");
    }
    private DirectoryStaffIdentity Parse(JsonElement row)
    {
        static string? Text(JsonElement row,string field) => row.TryGetProperty(field,out var value) && value.ValueKind==JsonValueKind.String ? value.GetString() : null;
        bool entra = options.Provider == "Entra";
        string id = Text(row,"id") ?? throw new InvalidOperationException("The directory returned no stable user identity.");
        if (entra) id = Guid.TryParse(id,out var guid) ? guid.ToString("D") : throw new InvalidOperationException("The directory returned an invalid Entra identity.");
        string username = Text(row,entra?"userPrincipalName":"username") ?? "";
        string displayName = entra ? Text(row,"displayName") ?? username : string.Join(" ",new[]{Text(row,"firstName"),Text(row,"lastName")}.Where(x=>!string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(displayName)) displayName=username;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(id) || username.Length>500 || displayName.Length>200)
            throw new InvalidOperationException("The directory identity is incomplete or exceeds the supported field lengths.");
        string? email = Text(row,entra?"mail":"email");
        if (email is not null && (email.Length>256 || !System.Net.Mail.MailAddress.TryCreate(email,out var address) || address.Address!=email)) email=null;
        bool enabled = row.TryGetProperty(entra?"accountEnabled":"enabled",out var active) && active.ValueKind == JsonValueKind.True;
        return new(id,username,displayName,email,enabled,new ExternalIdentityKey(staff.ProviderId,entra?Guid.Parse(options.TenantId).ToString("D"):Issuer,id),
            entra?AuthenticationProviderKind.EntraId:AuthenticationProviderKind.External);
    }
}
