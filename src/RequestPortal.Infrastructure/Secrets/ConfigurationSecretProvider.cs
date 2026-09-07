using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using RequestPortal.Common.Secrets;

namespace RequestPortal.Infrastructure.Secrets;

public sealed class ConfigurationSecretProvider : ISecretProvider
{
    private readonly IConfiguration configuration;

    public ConfigurationSecretProvider(IConfiguration configuration)
    {
        this.configuration = configuration;
    }

    public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        string? value = configuration[$"Secrets:{name}"];
        return Task.FromResult(value);
    }
}
