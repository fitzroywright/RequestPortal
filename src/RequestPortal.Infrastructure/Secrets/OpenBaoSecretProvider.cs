using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using RequestPortal.Common.Secrets;

namespace RequestPortal.Infrastructure.Secrets;

public sealed class OpenBaoSecretProvider : ISecretProvider
{
    private readonly HttpClient httpClient;
    private readonly IConfiguration configuration;

    public OpenBaoSecretProvider(HttpClient httpClient, IConfiguration configuration)
    {
        this.httpClient = httpClient;
        this.configuration = configuration;
    }

    public async Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        string? address = configuration["OpenBao:Address"];
        string? token = Environment.GetEnvironmentVariable(configuration["OpenBao:TokenEnvironmentVariable"] ?? "OPENBAO_TOKEN");
        string mount = configuration["OpenBao:Mount"] ?? "secret";
        string basePath = (configuration["OpenBao:BasePath"] ?? "requestportal").Trim('/');

        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        string key = name.Replace(':', '/').Trim('/');
        using HttpRequestMessage request = new(HttpMethod.Get, $"{address.TrimEnd('/')}/v1/{mount}/data/{basePath}/{key}");
        request.Headers.Add("X-Vault-Token", token);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("data", out JsonElement outer) ||
            !outer.TryGetProperty("data", out JsonElement inner))
        {
            return null;
        }

        if (inner.TryGetProperty("value", out JsonElement value))
        {
            return value.GetString();
        }

        foreach (JsonProperty property in inner.EnumerateObject())
        {
            return property.Value.GetString();
        }

        return null;
    }
}
