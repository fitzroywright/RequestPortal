using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace RequestPortal.Infrastructure.Documents;

public interface IDocumentUploadSecurity
{
    Task ValidateAsync(string fileName, string contentType, long sizeBytes, Stream content, CancellationToken cancellationToken = default);
}

public sealed class DocumentUploadSecurity : IDocumentUploadSecurity
{
    private static readonly HashSet<string> allowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".docx", ".xlsx"
    };

    private readonly long maxBytes;

    public DocumentUploadSecurity(IConfiguration configuration)
    {
        maxBytes = configuration.GetValue<long?>("Uploads:MaxBytes") ?? 25L * 1024L * 1024L;
    }

    public async Task ValidateAsync(string fileName, string contentType, long sizeBytes, Stream content, CancellationToken cancellationToken = default)
    {
        if (sizeBytes <= 0 || sizeBytes > maxBytes)
        {
            throw new InvalidOperationException($"Uploaded file must be between 1 byte and {maxBytes} bytes.");
        }

        string extension = Path.GetExtension(Path.GetFileName(fileName));
        if (!allowedExtensions.Contains(extension))
        {
            throw new InvalidOperationException("Unsupported file type. Allowed: PDF, PNG, JPEG, DOCX and XLSX.");
        }

        if (!content.CanSeek)
        {
            throw new InvalidOperationException("Upload stream must support validation before storage.");
        }

        long originalPosition = content.Position;
        byte[] header = new byte[8];
        int read = await content.ReadAsync(header.AsMemory(0, header.Length), cancellationToken);
        content.Position = originalPosition;

        bool valid = extension.ToLowerInvariant() switch
        {
            ".pdf" => read >= 4 && header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46,
            ".png" => read >= 8 && header.SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".jpg" or ".jpeg" => read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".docx" or ".xlsx" => read >= 4 && header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04,
            _ => false
        };

        if (!valid)
        {
            throw new InvalidOperationException("The file content does not match its extension.");
        }
    }
}
