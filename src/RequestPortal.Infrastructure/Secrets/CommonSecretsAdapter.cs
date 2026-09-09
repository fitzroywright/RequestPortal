using RequestPortal.Common.Secrets;
using CommonSecretProvider = Common.Secrets.ISecretProvider;

namespace RequestPortal.Infrastructure.Secrets;

public sealed class CommonSecretsAdapter : ISecretProvider
{
    private readonly CommonSecretProvider provider;

    public CommonSecretsAdapter(CommonSecretProvider provider)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
        => provider.GetAsync(name, cancellationToken);
}
