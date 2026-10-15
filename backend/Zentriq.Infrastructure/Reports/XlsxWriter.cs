using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Zentriq.Infrastructure.Reports;

/// <summary>
/// Minimal Office Open XML (.xlsx) writer: one worksheet, bold frozen header row, auto filter, numbers as numbers and
/// dates as Excel dates. No third-party dependency; opens in Excel, LibreOffice and Google Sheets.
/// </summary>
public static class XlsxWriter
{
    public static byte[] Write(string sheetName, ReportResult result, string? title = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Entry(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                <Default Extension="xml" ContentType="application/xml"/>
                <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                </Types>
                """);
            Entry(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Entry(zip, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
            var name = SecurityElement.Escape(SheetName(sheetName));
            Entry(zip, "xl/workbook.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                <sheets><sheet name="{name}" sheetId="1" r:id="rId1"/></sheets>
                <definedNames><definedName name="_xlnm._FilterDatabase" localSheetId="0" hidden="1">'{name.Replace("'", "''")}'!$A$1:${ColName(Math.Max(1, result.Columns.Count) - 1)}${result.Rows.Count + 1}</definedName></definedNames>
                </workbook>
                """);
            // Styles: 0 default, 1 bold header, 2 date-time, 3 number with up to 2 decimals.
            Entry(zip, "xl/styles.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                <numFmts count="1"><numFmt numFmtId="164" formatCode="yyyy-mm-dd hh:mm"/></numFmts>
                <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>
                <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFE8F1F3"/><bgColor indexed="64"/></patternFill></fill></fills>
                <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
                <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                <cellXfs count="4">
                <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
                <xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/>
                <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
                <xf numFmtId="4" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
                </cellXfs>
                </styleSheet>
                """);

            var sb = new StringBuilder();
            sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
            sb.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
            sb.Append("<cols>");
            for (var i = 0; i < result.Columns.Count; i++)
            {
                var width = Math.Min(60, Math.Max(10, Math.Max(result.Columns[i].Label.Length + 2,
                    result.Rows.Take(200).Select(r => ReportEngine.Text(r[i], result.Columns[i].Type).Length + 2).DefaultIfEmpty(10).Max())));
                sb.Append(CultureInfo.InvariantCulture, $"""<col min="{i + 1}" max="{i + 1}" width="{width}" customWidth="1"/>""");
            }
            sb.Append("</cols><sheetData>");
            sb.Append("""<row r="1">""");
            for (var i = 0; i < result.Columns.Count; i++) InlineString(sb, ColName(i) + "1", result.Columns[i].Label, 1);
            sb.Append("</row>");
            for (var r = 0; r < result.Rows.Count; r++)
            {
                var rn = r + 2;
                sb.Append(CultureInfo.InvariantCulture, $"""<row r="{rn}">""");
                for (var i = 0; i < result.Columns.Count; i++)
                {
                    var v = result.Rows[r][i];
                    var cell = ColName(i) + rn.ToString(CultureInfo.InvariantCulture);
                    switch (v)
                    {
                        case null: break;
                        case DateTimeOffset d:
                            sb.Append(CultureInfo.InvariantCulture, $"""<c r="{cell}" s="2"><v>{d.UtcDateTime.ToOADate()}</v></c>""");
                            break;
                        case decimal m:
                            sb.Append(CultureInfo.InvariantCulture, $"""<c r="{cell}" s="3"><v>{m}</v></c>""");
                            break;
                        case long l:
                            sb.Append(CultureInfo.InvariantCulture, $"""<c r="{cell}"><v>{l}</v></c>""");
                            break;
                        default:
                            InlineString(sb, cell, v.ToString() ?? "", 0);
                            break;
                    }
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData>");
            if (result.Columns.Count > 0)
                sb.Append(CultureInfo.InvariantCulture, $"""<autoFilter ref="A1:{ColName(result.Columns.Count - 1)}{result.Rows.Count + 1}"/>""");
            sb.Append("</worksheet>");
            Entry(zip, "xl/worksheets/sheet1.xml", sb.ToString());
        }
        return ms.ToArray();
    }

    private static void InlineString(StringBuilder sb, string cell, string text, int style)
    {
        var clean = new string(text.Where(ch => ch == '\t' || ch == '\n' || ch == '\r' || ch >= 0x20).ToArray());
        if (clean.Length > 32000) clean = clean[..32000];
        sb.Append($"""<c r="{cell}" t="inlineStr"{(style > 0 ? $" s=\"{style}\"" : "")}><is><t xml:space="preserve">{SecurityElement.Escape(clean)}</t></is></c>""");
    }

    public static string ColName(int index)
    {
        var s = "";
        index++;
        while (index > 0) { var m = (index - 1) % 26; s = (char)('A' + m) + s; index = (index - 1) / 26; }
        return s;
    }

    private static string SheetName(string s)
    {
        var clean = new string(s.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray()).Trim();
        if (clean.Length == 0) clean = "Report";
        return clean.Length > 31 ? clean[..31] : clean;
    }

    private static void Entry(ZipArchive zip, string path, string content)
    {
        var e = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content.Trim());
    }
}
