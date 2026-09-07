using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using RequestPortal.Common.Storage;

namespace RequestPortal.Infrastructure.Storage;

public sealed class LocalFileStorage : IFileStorage
{
    private readonly string rootPath;

    public LocalFileStorage(IConfiguration configuration)
    {
        string configuredPath = configuration["Storage:Local:RootPath"] ?? "App_Data/storage";
        rootPath = Path.GetFullPath(configuredPath);
        Directory.CreateDirectory(rootPath);
    }

    public async Task<StoredFile> StoreAsync(
        string storageKey,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        string fullPath = ResolvePath(storageKey);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using FileStream destination = new(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);

        await content.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);

        FileInfo info = new(fullPath);
        return new StoredFile(storageKey, info.Length, contentType, DateTimeOffset.UtcNow);
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        string fullPath = ResolvePath(storageKey);
        Stream stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);

        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        string fullPath = ResolvePath(storageKey);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Task.CompletedTask;
    }

    private string ResolvePath(string storageKey)
    {
        string relative = storageKey.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(rootPath, relative));

        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid storage key.");
        }

        return fullPath;
    }
}
