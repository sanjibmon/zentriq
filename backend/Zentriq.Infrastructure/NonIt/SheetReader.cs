using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Zentriq.Infrastructure.NonIt;

/// <summary>
/// Reads the rows of an uploaded spreadsheet: .xlsx (Office Open XML: shared and inline strings, numbers, booleans,
/// dates as serial numbers) or .csv (comma, semicolon or tab separated, quoted fields). No third-party dependency.
/// Every cell comes back as text; numbers use the invariant culture so the importer can parse dates and amounts.
/// </summary>
public static class SheetReader
{
    public const int MaxRows = 20_000;
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pr = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static List<string[]> Read(byte[] content, string? preferredSheet = null)
    {
        if (content.Length >= 4 && content[0] == 0x50 && content[1] == 0x4B) return ReadXlsx(content, preferredSheet);
        if (content.Length >= 8 && content[0] == 0xD0 && content[1] == 0xCF)
            throw new InvalidDataException("This is an old Excel 97-2003 (.xls) file. Save it as Excel Workbook (.xlsx) or CSV and upload again.");
        return ReadCsv(DecodeText(content));
    }

    private static string DecodeText(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(b); }
    }

    public static List<string[]> ReadCsv(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        var sep = new[] { ',', ';', '\t' }.OrderByDescending(c => firstLine.Count(x => x == c)).First();
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
                continue;
            }
            if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == sep) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                rows.Add(row.ToArray()); row.Clear();
                if (rows.Count > MaxRows + 1) throw new InvalidDataException($"The file has more than {MaxRows:N0} rows. Split it into smaller files.");
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    private static List<string[]> ReadXlsx(byte[] content, string? preferredSheet)
    {
        using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        XDocument Load(string path)
        {
            var e = zip.GetEntry(path) ?? throw new InvalidDataException($"The workbook has no {path}.");
            if (e.Length > 200 * 1024 * 1024) throw new InvalidDataException("The workbook is too large.");
            using var s = e.Open();
            return XDocument.Load(s, LoadOptions.None);
        }

        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is not null)
            foreach (var si in Load("xl/sharedStrings.xml").Root!.Elements(S + "si"))
                shared.Add(string.Concat(si.Descendants(S + "t").Select(t => t.Value)));

        // Find the sheet: the preferred name (e.g. "Assets"), else the first one.
        var wb = Load("xl/workbook.xml");
        var sheets = wb.Root!.Element(S + "sheets")?.Elements(S + "sheet").ToList() ?? [];
        if (sheets.Count == 0) throw new InvalidDataException("The workbook has no sheets.");
        var sheet = sheets.FirstOrDefault(x => string.Equals((string?)x.Attribute("name"), preferredSheet, StringComparison.OrdinalIgnoreCase)) ?? sheets[0];
        var rid = (string?)sheet.Attribute(R + "id");
        var target = "worksheets/sheet1.xml";
        if (rid is not null && zip.GetEntry("xl/_rels/workbook.xml.rels") is not null)
            target = (string?)Load("xl/_rels/workbook.xml.rels").Root!.Elements(Pr + "Relationship").FirstOrDefault(x => (string?)x.Attribute("Id") == rid)?.Attribute("Target") ?? target;
        var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

        var rows = new List<string[]>();
        var data = Load(path).Root!.Element(S + "sheetData");
        if (data is null) return rows;
        var expected = 1;
        foreach (var r in data.Elements(S + "row"))
        {
            var rowNum = int.TryParse((string?)r.Attribute("r"), out var rn) ? rn : expected;
            while (expected < rowNum) { rows.Add([]); expected++; }   // keep spreadsheet row numbers for error messages
            expected = rowNum + 1;
            var cells = new SortedDictionary<int, string>();
            var next = 0;
            foreach (var c in r.Elements(S + "c"))
            {
                var col = (string?)c.Attribute("r") is { } refText ? ColumnIndex(refText) : next;
                next = col + 1;
                var type = (string?)c.Attribute("t");
                var v = c.Element(S + "v")?.Value;
                cells[col] = type switch
                {
                    "s" => int.TryParse(v, out var si) && si >= 0 && si < shared.Count ? shared[si] : "",
                    "inlineStr" => string.Concat(c.Descendants(S + "t").Select(t => t.Value)),
                    "b" => v == "1" ? "TRUE" : "FALSE",
                    _ => v ?? "",
                };
            }
            var arr = new string[cells.Count == 0 ? 0 : cells.Keys.Max() + 1];
            for (var i = 0; i < arr.Length; i++) arr[i] = cells.TryGetValue(i, out var x) ? x : "";
            rows.Add(arr);
            if (rows.Count > MaxRows + 1) throw new InvalidDataException($"The sheet has more than {MaxRows:N0} rows. Split it into smaller files.");
        }
        return rows;
    }

    /// <summary>"C12" → 2.</summary>
    public static int ColumnIndex(string cellRef)
    {
        var n = 0;
        foreach (var ch in cellRef)
        {
            if (!char.IsLetter(ch)) break;
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return Math.Max(0, n - 1);
    }

    /// <summary>A date typed as text (2026-03-31, 31/03/2026, 31-03-2026, 31 Mar 2026) or an Excel serial number.</summary>
    public static DateOnly? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 1 and < 2958466 && !s.Contains('-') && !s.Contains('/'))
            return DateOnly.FromDateTime(new DateTime(1899, 12, 30).AddDays(Math.Floor(serial)));
        string[] formats = ["yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d MMM yyyy", "dd MMM yyyy", "d-MMM-yyyy", "dd-MMM-yyyy", "MMM d, yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss"];
        return DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d) ? DateOnly.FromDateTime(d) : null;
    }

    /// <summary>An amount typed with thousands separators or a currency symbol (₹ 1,25,000.50 → 125000.50).</summary>
    public static decimal? ParseNumber(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = new string(s.Where(c => char.IsDigit(c) || c is '.' or '-' or 'E' or 'e' or '+').ToArray());
        return decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
