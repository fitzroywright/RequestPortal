using System.Net;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using RequestPortal.Application.Documents;

namespace RequestPortal.Infrastructure.Documents;

public sealed class OpenXmlDocumentPreviewRenderer : IDocumentPreviewRenderer
{
    public Task<string?> RenderHtmlAsync(
        string fileName,
        string contentType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        string? html = extension switch
        {
            ".docx" => RenderWord(content),
            ".xlsx" => RenderExcel(content),
            ".pptx" => RenderPowerPoint(content),
            _ => null
        };

        return Task.FromResult(html);
    }

    private static string RenderWord(Stream content)
    {
        using WordprocessingDocument document = WordprocessingDocument.Open(content, false);
        var body = document.MainDocumentPart?.Document.Body;
        if (body is null)
        {
            return "<p>No previewable content was found.</p>";
        }

        StringBuilder html = new();
        html.Append("<article class=\"office-preview word-preview\">");

        foreach (var child in body.ChildElements)
        {
            if (child is DocumentFormat.OpenXml.Wordprocessing.Paragraph paragraph)
            {
                string text = paragraph.InnerText;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    html.Append("<p>")
                        .Append(WebUtility.HtmlEncode(text))
                        .Append("</p>");
                }
            }
            else if (child is DocumentFormat.OpenXml.Wordprocessing.Table table)
            {
                html.Append("<div class=\"preview-table-wrap\"><table class=\"preview-table\">");
                foreach (var row in table.Elements<DocumentFormat.OpenXml.Wordprocessing.TableRow>())
                {
                    html.Append("<tr>");
                    foreach (var cell in row.Elements<DocumentFormat.OpenXml.Wordprocessing.TableCell>())
                    {
                        html.Append("<td>")
                            .Append(WebUtility.HtmlEncode(cell.InnerText))
                            .Append("</td>");
                    }
                    html.Append("</tr>");
                }
                html.Append("</table></div>");
            }
        }

        html.Append("</article>");
        return html.ToString();
    }

    private static string RenderExcel(Stream content)
    {
        using SpreadsheetDocument document = SpreadsheetDocument.Open(content, false);
        WorkbookPart? workbookPart = document.WorkbookPart;
        if (workbookPart?.Workbook.Sheets is null)
        {
            return "<p>No previewable spreadsheet content was found.</p>";
        }

        SharedStringItem[] sharedStrings = workbookPart.SharedStringTablePart?
            .SharedStringTable
            .Elements<SharedStringItem>()
            .ToArray() ?? Array.Empty<SharedStringItem>();

        StringBuilder html = new();
        html.Append("<article class=\"office-preview excel-preview\">");

        foreach (Sheet sheet in workbookPart.Workbook.Sheets.Elements<Sheet>())
        {
            if (sheet.Id?.Value is null)
            {
                continue;
            }

            WorksheetPart worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id.Value);
            html.Append("<section class=\"sheet-preview\"><h2>")
                .Append(WebUtility.HtmlEncode(sheet.Name?.Value ?? "Sheet"))
                .Append("</h2><div class=\"preview-table-wrap\"><table class=\"preview-table\">");

            foreach (Row row in worksheetPart.Worksheet.Descendants<Row>().Take(200))
            {
                html.Append("<tr>");
                foreach (Cell cell in row.Elements<Cell>().Take(50))
                {
                    html.Append("<td>")
                        .Append(WebUtility.HtmlEncode(ReadCell(cell, sharedStrings)))
                        .Append("</td>");
                }
                html.Append("</tr>");
            }

            html.Append("</table></div></section>");
        }

        html.Append("</article>");
        return html.ToString();
    }

    private static string ReadCell(Cell cell, IReadOnlyList<SharedStringItem> sharedStrings)
    {
        string value = cell.CellValue?.Text ?? cell.InlineString?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(value, out int index) &&
            index >= 0 &&
            index < sharedStrings.Count)
        {
            return sharedStrings[index].InnerText;
        }

        return value;
    }

    private static string RenderPowerPoint(Stream content)
    {
        using PresentationDocument document = PresentationDocument.Open(content, false);
        PresentationPart? presentationPart = document.PresentationPart;
        var slideIds = presentationPart?.Presentation.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>().ToArray();
        if (presentationPart is null || slideIds is null || slideIds.Length == 0)
        {
            return "<p>No previewable presentation content was found.</p>";
        }

        StringBuilder html = new();
        html.Append("<article class=\"office-preview powerpoint-preview\">");

        int slideNumber = 1;
        foreach (var slideId in slideIds)
        {
            if (slideId.RelationshipId?.Value is null)
            {
                continue;
            }

            SlidePart slidePart = (SlidePart)presentationPart.GetPartById(slideId.RelationshipId.Value);
            string[] lines = slidePart.Slide
                .Descendants<DocumentFormat.OpenXml.Drawing.Text>()
                .Select(text => text.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();

            html.Append("<section class=\"slide-preview\"><div class=\"slide-number\">Slide ")
                .Append(slideNumber++)
                .Append("</div><div class=\"slide-canvas\">");

            foreach (string line in lines)
            {
                html.Append("<p>")
                    .Append(WebUtility.HtmlEncode(line))
                    .Append("</p>");
            }

            if (lines.Length == 0)
            {
                html.Append("<p class=\"muted\">No text content on this slide.</p>");
            }

            html.Append("</div></section>");
        }

        html.Append("</article>");
        return html.ToString();
    }
}
