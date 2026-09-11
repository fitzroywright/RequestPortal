using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RequestPortal.Common.Diagnostics;
using RequestPortal.Infrastructure.Security;

namespace RequestPortal.Infrastructure.Diagnostics;

public sealed class ActiveDirectoryStartupDiagnostic : IStartupDiagnostic
{
    private readonly ActiveDirectoryOptions options;
    private readonly IConfiguration configuration;

    public ActiveDirectoryStartupDiagnostic(
        IOptions<ActiveDirectoryOptions> options,
        IConfiguration configuration)
    {
        this.options = options.Value;
        this.configuration = configuration;
    }

    public string Name => "FFP Active Directory";

    public async Task<DiagnosticCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Server))
        {
            if (configuration.GetValue("Demo:Enabled", false))
            {
                return new DiagnosticCheckResult(
                    Name,
                    true,
                    "Directory authentication is intentionally bypassed for the demo staff persona. Production FFP staff authentication remains AD/LDAPS based.");
            }

            return new DiagnosticCheckResult(Name, false, "Directory server is not configured.");
        }

        try
        {
            using TcpClient client = new();
            await client.ConnectAsync(options.Server, options.Port, cancellationToken);
            return new DiagnosticCheckResult(
                Name,
                true,
                $"Directory endpoint {options.Server}:{options.Port} is reachable.");
        }
        catch (Exception exception)
        {
            return new DiagnosticCheckResult(Name, false, exception.Message);
        }
    }
}
