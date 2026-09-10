using System;
using System.Threading;
using System.Threading.Tasks;
using Common.Storage;
using RequestPortal.Common.Diagnostics;

namespace RequestPortal.Infrastructure.Diagnostics;

public sealed class CommonStorageStartupDiagnostic : IStartupDiagnostic
{
    private readonly IFileStorage storage;

    public CommonStorageStartupDiagnostic(IFileStorage storage)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public string Name => "Common.Storage";

    public async Task<DiagnosticCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            StorageHealth health = await storage.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            if (!health.Available)
            {
                return new DiagnosticCheckResult(
                    Name,
                    false,
                    health.Error ?? $"{health.Provider} storage is unavailable.");
            }

            if (!health.Writable)
            {
                return new DiagnosticCheckResult(
                    Name,
                    false,
                    health.Error ?? $"{health.Provider} storage is available but not writable.");
            }

            return new DiagnosticCheckResult(
                Name,
                true,
                $"{health.Provider} storage is available and writable through Common.Storage.");
        }
        catch (Exception exception)
        {
            return new DiagnosticCheckResult(Name, false, exception.Message);
        }
    }
}
