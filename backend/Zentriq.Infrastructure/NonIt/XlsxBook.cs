using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Zentriq.Infrastructure.NonIt;

/// <summary>A worksheet to write: headings, rows (string, number, DateOnly or null), widths and drop-down lists.</summary>
public sealed class XlsxSheet(string name)
{
    public string Name { get; } = name;
    public List<string> Headings { get; } = [];
    public List<object?[]> Rows { get; } = [];
    public List<double> Widths { get; } = [];
    /// <summary>Column index → list source (e.g. "Lists!$A$2:$A$80"); applied to rows 2 … 5000.</summary>
    public Dictionary<int, string> DropDowns { get; } = [];
    /// <summary>Headings of required columns are written in the accent fill.</summary>
    public HashSet<int> Required { get; } = [];
    public bool Hidden { get; set; }
    public bool FreezeHeader { get; set; } = true;
}

/// <summary>Minimal multi-sheet .xlsx writer (bold frozen headings, dates as dates, drop-down validation, hidden sheets).</summary>
public static class XlsxBook
{
    public static byte[] Write(params XlsxSheet[] sheets)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var ct = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");
            for (var i = 1; i <= sheets.Length; i++) ct.Append($"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
            ct.Append("</Types>");
            Entry(zip, "[Content_Types].xml", ct.ToString());
            Entry(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            var rels = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
            var wb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>""");
            for (var i = 1; i <= sheets.Length; i++)
            {
                rels.Append($"""<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i}.xml"/>""");
                wb.Append($"""<sheet name="{SecurityElement.Escape(SheetName(sheets[i - 1].Name))}" sheetId="{i}" r:id="rId{i}"{(sheets[i - 1].Hidden ? " state=\"hidden\"" : "")}/>""");
            }
            rels.Append($"""<Relationship Id="rId{sheets.Length + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            wb.Append("</sheets></workbook>");
            Entry(zip, "xl/_rels/workbook.xml.rels", rels.ToString());
            Entry(zip, "xl/workbook.xml", wb.ToString());
            // Styles: 0 default, 1 heading, 2 date, 3 number, 4 required heading, 5 wrapped text.
            Entry(zip, "xl/styles.xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="yyyy-mm-dd"/></numFmts><fonts count="3"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="Calibri"/></font></fonts><fills count="4"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFE0F1F3"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FF0A7385"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="6"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="4" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="0" fontId="2" fillId="3" borderId="0" xfId="0" applyFont="1" applyFill="1"/><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment wrapText="1" vertical="top"/></xf></cellXfs></styleSheet>""");
            for (var i = 0; i < sheets.Length; i++) Entry(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i]));
        }
        return ms.ToArray();
    }

    private static string SheetXml(XlsxSheet s)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">""");
        if (s.FreezeHeader && s.Headings.Count > 0)
            sb.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        if (s.Widths.Count > 0)
        {
            sb.Append("<cols>");
            for (var i = 0; i < s.Widths.Count; i++) sb.Append($"""<col min="{i + 1}" max="{i + 1}" width="{s.Widths[i].ToString(CultureInfo.InvariantCulture)}" customWidth="1"/>""");
            sb.Append("</cols>");
        }
        sb.Append("<sheetData>");
        var r = 1;
        if (s.Headings.Count > 0)
        {
            sb.Append($"""<row r="{r}">""");
            for (var c = 0; c < s.Headings.Count; c++) sb.Append(Cell(r, c, s.Headings[c], s.Required.Contains(c) ? 4 : 1));
            sb.Append("</row>");
            r++;
        }
        foreach (var row in s.Rows)
        {
            sb.Append($"""<row r="{r}">""");
            for (var c = 0; c < row.Length; c++) if (row[c] is not null) sb.Append(Cell(r, c, row[c]!, null));
            sb.Append("</row>");
            r++;
        }
        sb.Append("</sheetData>");
        if (s.Headings.Count > 0 && s.Rows.Count > 0)
            sb.Append($"""<autoFilter ref="A1:{Col(s.Headings.Count - 1)}{s.Rows.Count + 1}"/>""");
        if (s.DropDowns.Count > 0)
        {
            sb.Append($"""<dataValidations count="{s.DropDowns.Count}">""");
            foreach (var (col, source) in s.DropDowns)
                sb.Append($"""<dataValidation type="list" allowBlank="1" showErrorMessage="1" errorStyle="warning" sqref="{Col(col)}2:{Col(col)}5000"><formula1>{SecurityElement.Escape(source)}</formula1></dataValidation>""");
            sb.Append("</dataValidations>");
        }
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    private static string Cell(int r, int c, object v, int? style)
    {
        var refText = $"{Col(c)}{r}";
        switch (v)
        {
            case DateOnly d:
                return $"""<c r="{refText}" s="2"><v>{(d.ToDateTime(TimeOnly.MinValue) - new DateTime(1899, 12, 30)).TotalDays.ToString(CultureInfo.InvariantCulture)}</v></c>""";
            case decimal or double or int or long:
                return $"""<c r="{refText}"{(style is { } st ? $" s=\"{st}\"" : v is decimal ? " s=\"3\"" : "")}><v>{Convert.ToString(v, CultureInfo.InvariantCulture)}</v></c>""";
            default:
                var text = Clean(v.ToString() ?? "");
                return $"""<c r="{refText}" t="inlineStr"{(style is { } s2 ? $" s=\"{s2}\"" : text.Contains('\n') ? " s=\"5\"" : "")}><is><t xml:space="preserve">{SecurityElement.Escape(text)}</t></is></c>""";
        }
    }

    /// <summary>Strips characters XML cannot hold (inline strings are text: Excel never runs them as formulas).</summary>
    private static string Clean(string s) => new(s.Where(ch => ch == '\t' || ch == '\n' || ch == '\r' || ch >= ' ').ToArray());

    public static string Col(int i)
    {
        var s = "";
        i++;
        while (i > 0) { var m = (i - 1) % 26; s = (char)('A' + m) + s; i = (i - 1) / 26; }
        return s;
    }

    private static string SheetName(string n)
    {
        var t = new string(n.Where(c => c is not ('\\' or '/' or '?' or '*' or '[' or ']' or ':')).ToArray());
        return t.Length == 0 ? "Sheet" : t.Length > 31 ? t[..31] : t;
    }

    private static void Entry(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
