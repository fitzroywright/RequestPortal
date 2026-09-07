using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Common.Secrets;

public interface ISecretProvider
{
    Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default);
}

public sealed class CompositeSecretProvider : ISecretProvider
{
    private readonly IReadOnlyList<ISecretProvider> providers;

    public CompositeSecretProvider(IEnumerable<ISecretProvider> providers)
    {
        this.providers = providers.ToArray();
    }

    public async Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        foreach (ISecretProvider provider in providers)
        {
            string? value = await provider.GetSecretAsync(name, cancellationToken);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
