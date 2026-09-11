using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using UglyToad.PdfPig;

namespace LocalDocket.Extract;

internal static class OfficeExtractors
{
    public static string Pdf(string path, int cap, Dictionary<string, string> meta)
    {
        using var doc = PdfDocument.Open(path);
        meta["pdf.pages"] = doc.NumberOfPages.ToString();
        var info = doc.Information;
        if (!string.IsNullOrWhiteSpace(info.Title)) meta["pdf.title"] = info.Title!;
        if (!string.IsNullOrWhiteSpace(info.Author)) meta["pdf.author"] = info.Author!;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(info.Title)) sb.AppendLine("Title: " + info.Title);
        foreach (var page in doc.GetPages())
        {
            sb.AppendLine(page.Text);
            if (sb.Length > cap) break;
        }
        return Cap(sb.ToString(), cap);
    }

    public static string Docx(string path, int cap, Dictionary<string, string> meta)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var props = doc.PackageProperties;
        if (!string.IsNullOrWhiteSpace(props.Title)) meta["doc.title"] = props.Title!;
        if (!string.IsNullOrWhiteSpace(props.Creator)) meta["doc.author"] = props.Creator!;
        var body = doc.MainDocumentPart?.Document.Body;
        if (body == null) return "";
        var sb = new StringBuilder();
        foreach (var p in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
        {
            var t = p.InnerText.Trim();
            if (t.Length == 0) continue;
            sb.AppendLine(t);
            if (sb.Length > cap) break;
        }
        return Cap(sb.ToString(), cap);
    }

    public static string Xlsx(string path, int cap, Dictionary<string, string> meta)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart;
        if (wb == null) return "";
        var sst = wb.SharedStringTablePart?.SharedStringTable;
        var sb = new StringBuilder();
        var sheets = wb.Workbook.Sheets?.Elements<Sheet>().ToList() ?? new();
        meta["xlsx.sheets"] = string.Join(",", sheets.Select(s => s.Name?.Value));
        sb.AppendLine("Sheets: " + meta["xlsx.sheets"]);
        foreach (var sheet in sheets.Take(4))
        {
            if (sheet.Id?.Value == null) continue;
            var wsp = (WorksheetPart)wb.GetPartById(sheet.Id.Value);
            sb.AppendLine($"--- {sheet.Name} ---");
            int rows = 0;
            foreach (var row in wsp.Worksheet.Descendants<Row>())
            {
                var cells = row.Elements<Cell>().Select(c => CellText(c, sst)).Where(s => s.Length > 0).ToList();
                if (cells.Count > 0) { sb.AppendLine(string.Join(" | ", cells)); rows++; }
                if (rows >= 12 || sb.Length > cap) break;
            }
            if (sb.Length > cap) break;
        }
        return Cap(sb.ToString(), cap);
    }

    static string CellText(Cell c, SharedStringTable? sst)
    {
        var v = c.CellValue?.InnerText ?? "";
        if (c.DataType?.Value == CellValues.SharedString && sst != null && int.TryParse(v, out var idx))
            return sst.ElementAt(idx).InnerText;
        return v;
    }

    public static string Pptx(string path, int cap, Dictionary<string, string> meta)
    {
        using var doc = PresentationDocument.Open(path, false);
        var pres = doc.PresentationPart;
        if (pres == null) return "";
        var sb = new StringBuilder();
        int n = 0;
        foreach (var slidePart in pres.SlideParts)
        {
            n++;
            var text = string.Join(" ", slidePart.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>().Select(t => t.Text));
            if (text.Trim().Length > 0) sb.AppendLine($"[slide {n}] {text}");
            if (sb.Length > cap) break;
        }
        meta["pptx.slides"] = pres.SlideParts.Count().ToString();
        return Cap(sb.ToString(), cap);
    }

    static string Cap(string s, int cap) => s.Length > cap ? s[..cap] : s;
}
