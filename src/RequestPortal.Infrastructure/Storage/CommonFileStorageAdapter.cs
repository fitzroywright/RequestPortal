using RequestPortal.Common.Storage;
using CommonFileStorage = Common.Storage.IFileStorage;
using CommonStorageWriteRequest = Common.Storage.StorageWriteRequest;

namespace RequestPortal.Infrastructure.Storage;

public sealed class CommonFileStorageAdapter : IFileStorage
{
    private readonly CommonFileStorage storage;

    public CommonFileStorageAdapter(CommonFileStorage storage)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public async Task<StoredFile> StoreAsync(
        string storageKey,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        ArgumentNullException.ThrowIfNull(content);

        Common.Storage.StoredFile stored = await storage.StoreAsync(
            new CommonStorageWriteRequest(
                storageKey,
                content,
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                Path.GetFileName(storageKey),
                "RequestPortal"),
            cancellationToken);

        return new StoredFile(
            stored.StorageKey,
            stored.Length,
            stored.ContentType,
            stored.StoredUtc);
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
        => storage.OpenReadAsync(storageKey, cancellationToken);

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
        => storage.DeleteAsync(storageKey, cancellationToken);
}
