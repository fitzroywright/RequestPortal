using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Common.Storage;

public sealed record StoredFile(
    string StorageKey,
    long Length,
    string ContentType,
    DateTimeOffset StoredUtc);

public interface IFileStorage
{
    Task<StoredFile> StoreAsync(
        string storageKey,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);

    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);
}
