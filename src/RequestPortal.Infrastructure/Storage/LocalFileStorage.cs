using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using RequestPortal.Common.Storage;

namespace RequestPortal.Infrastructure.Storage;

public sealed class LocalFileStorage : IFileStorage
{
    private readonly Common.Storage.IFileStorage storage;

    public LocalFileStorage(IConfiguration configuration)
    {
        string configuredPath = configuration["Storage:Local:RootPath"] ?? "App_Data/storage";
        storage = new Common.Storage.LocalFileStorage(configuredPath);
    }

    public async Task<StoredFile> StoreAsync(
        string storageKey,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        string originalFileName = Path.GetFileName(storageKey);
        Common.Storage.StoredFile stored = await storage.StoreAsync(
            new Common.Storage.StorageWriteRequest(
                storageKey,
                content,
                contentType,
                originalFileName,
                "RequestPortal",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["application"] = "RequestPortal"
                }),
            cancellationToken);

        return new StoredFile(stored.StorageKey, stored.Length, stored.ContentType, stored.StoredUtc);
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
        => storage.OpenReadAsync(storageKey, cancellationToken);

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
        => storage.DeleteAsync(storageKey, cancellationToken);
}
