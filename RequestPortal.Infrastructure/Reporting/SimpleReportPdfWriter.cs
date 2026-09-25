using System.Text;

namespace RequestPortal.Infrastructure.Reporting;

internal static class SimpleReportPdfWriter
{
    public static byte[] Create(string title, IEnumerable<string> content)
    {
        List<string> lines = Wrap(title, 72)
            .Concat([string.Empty])
            .Concat(content.SelectMany(line => Wrap(line, 88)))
            .ToList();

        const int linesPerPage = 48;
        List<List<string>> pages = lines.Chunk(linesPerPage).Select(chunk => chunk.ToList()).ToList();
        if (pages.Count == 0) pages.Add([title]);

        using MemoryStream output = new();
        using StreamWriter writer = new(output, new UTF8Encoding(false), 1024, leaveOpen: true) { NewLine = "\n" };
        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        List<long> offsets = [0];
        int pageCount = pages.Count;
        int fontObject = 3 + pageCount * 2;
        int objectCount = fontObject;

        WriteObject(writer, output, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");
        string kids = string.Join(' ', Enumerable.Range(0, pageCount).Select(i => $"{3 + i * 2} 0 R"));
        WriteObject(writer, output, offsets, 2, $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");

        for (int i = 0; i < pageCount; i++)
        {
            int pageObject = 3 + i * 2;
            int contentObject = pageObject + 1;
            WriteObject(writer, output, offsets, pageObject,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {fontObject} 0 R >> >> /Contents {contentObject} 0 R >>");

            string pageStream = BuildPageStream(pages[i]);
            byte[] streamBytes = Encoding.ASCII.GetBytes(pageStream);
            writer.Flush();
            offsets.Add(output.Position);
            writer.WriteLine($"{contentObject} 0 obj");
            writer.WriteLine($"<< /Length {streamBytes.Length} >>");
            writer.WriteLine("stream");
            writer.Flush();
            output.Write(streamBytes, 0, streamBytes.Length);
            writer.WriteLine();
            writer.WriteLine("endstream");
            writer.WriteLine("endobj");
            writer.Flush();
        }

        WriteObject(writer, output, offsets, fontObject, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        long xref = output.Position;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {objectCount + 1}");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= objectCount; i++) writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Size {objectCount + 1} /Root 1 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xref);
        writer.WriteLine("%%EOF");
        writer.Flush();
        return output.ToArray();
    }

    private static void WriteObject(StreamWriter writer, MemoryStream output, List<long> offsets, int id, string body)
    {
        writer.Flush();
        offsets.Add(output.Position);
        writer.WriteLine($"{id} 0 obj");
        writer.WriteLine(body);
        writer.WriteLine("endobj");
        writer.Flush();
    }

    private static string BuildPageStream(IEnumerable<string> lines)
    {
        StringBuilder stream = new();
        stream.AppendLine("BT");
        stream.AppendLine("/F1 10 Tf");
        stream.AppendLine("42 748 Td");
        bool first = true;
        foreach (string line in lines)
        {
            if (!first) stream.AppendLine("0 -14 Td");
            stream.Append('(').Append(Escape(line)).AppendLine(") Tj");
            first = false;
        }
        stream.AppendLine("ET");
        return stream.ToString();
    }

    private static IEnumerable<string> Wrap(string? text, int width)
    {
        foreach (string source in (text ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
        {
            string remaining = source.Trim();
            if (remaining.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            while (remaining.Length > width)
            {
                int cut = remaining.LastIndexOf(' ', width);
                if (cut <= 0) cut = width;
                yield return remaining[..cut].Trim();
                remaining = remaining[cut..].TrimStart();
            }

            yield return remaining;
        }
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("(", "\\(", StringComparison.Ordinal)
        .Replace(")", "\\)", StringComparison.Ordinal)
        .Select(ch => ch is >= ' ' and <= '~' ? ch : '?')
        .Aggregate(new StringBuilder(), (builder, ch) => builder.Append(ch))
        .ToString();
}
