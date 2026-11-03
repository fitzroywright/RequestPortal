using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Secrets;
using Microsoft.Extensions.Options;
using Npgsql;

namespace RequestPortal.Web.Security;

/// <summary>Offline, non-destructive commissioning. Does not start the website, run migrations or provision accounts.</summary>
internal static class StaffDirectoryCommissioningCommand
{
    public static async Task<int> RunAsync(WebApplicationBuilder builder, bool verify)
    {
        try
        {
            if (!builder.Environment.IsProduction()) throw new InvalidOperationException();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
            var token = timeout.Token;
            // A diagnostic process must not consume/delete the service's bootstrap credential file.
            var overrides = new Dictionary<string, string?>
            {
                ["CommonSecrets:OpenBao:DeleteSecretIdFileAfterRead"] = "false"
            };
            foreach (var provider in builder.Configuration.GetSection("CommonSecrets:Providers").GetChildren())
                overrides[$"CommonSecrets:Providers:{provider.Key}:Settings:DeleteSecretIdFileAfterRead"] = "false";
            var configuration = new ConfigurationBuilder().AddConfiguration(builder.Configuration).AddInMemoryCollection(overrides).Build();
            var staff = configuration.GetSection(StaffOidcOptions.SectionName).Get<StaffOidcOptions>() ?? new();
            var directory = configuration.GetSection(StaffDirectoryOptions.SectionName).Get<StaffDirectoryOptions>() ?? new();
            await using var secrets = CommonSecretsBootstrapRuntime.Create(configuration);
            var connectionString = await secrets.Provider.GetRequiredAsync("ConnectionStrings:RequestPortal", token);
            var connectionOptions = new NpgsqlConnectionStringBuilder(connectionString);
            if (connectionOptions.Database?.Equals("FFPJM", StringComparison.OrdinalIgnoreCase) != false)
                throw new InvalidOperationException();
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            await using (var command = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                await command.ExecuteNonQueryAsync(token);
            var bindings = new List<string>();
            await using (var command = new NpgsqlCommand("""
                SELECT provider_id,scope_id,subject_id,account_id FROM common_security_identities
                WHERE application_id='RequestPortal.Staff' ORDER BY provider_id,scope_id,subject_id,account_id
                """, connection, transaction))
            await using (var reader = await command.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token))
                    bindings.Add(JsonSerializer.Serialize(new[] { reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3) }));
            await transaction.RollbackAsync(token);
            int? searchMatches = null;
            bool individualLookupVerified = false;
            if (verify)
            {
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
                var lookup = new HttpStaffDirectoryLookup(http, Options.Create(directory), Options.Create(staff), secrets.Provider);
                if (lookup.UnavailableReason is not null) throw new InvalidOperationException();
                var results = await lookup.SearchAsync("adm", token);
                searchMatches = results.Count;
                if (results.FirstOrDefault() is { } sample)
                {
                    var found = await lookup.FindAsync(sample.Id, token);
                    if (found?.Key != sample.Key) throw new InvalidOperationException();
                    individualLookupVerified = true;
                }
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "ok", compatibility = "issuer-sub-and-tenant-object-read-only-v1",
                staffEnabled = staff.Enabled, staffClientId = staff.ClientId, staffProviderId = staff.ProviderId,
                authority = staff.Authority, issuer = string.IsNullOrWhiteSpace(staff.Issuer) ? staff.Authority.TrimEnd('/') : staff.Issuer,
                allowedTenantIds = staff.AllowedTenantIds,
                directoryEnabled = directory.Enabled, directoryProvider = directory.Provider, directoryTenantId = directory.TenantId,
                directoryClientId = directory.ClientId, directorySecretReference = directory.ClientSecretName,
                staffBindingCount = bindings.Count,
                staffBindingFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', bindings)))),
                searchMatches, individualLookupVerified
            }));
            return 0;
        }
        catch (Exception exception)
        {
            // Exception/provider bodies can include credential or connection details. Never emit them.
            Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", errorType = exception.GetType().Name }));
            return 1;
        }
    }
}
