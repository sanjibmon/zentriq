using System.Globalization;
using System.Text;

namespace Zentriq.Infrastructure.Reports;

/// <summary>
/// Writes a report as a PDF (landscape A4) without third-party libraries: title, subtitle, an optional bar chart for
/// summary reports, and the table with the header repeated on every page and "Page n of N" in the footer.
/// Uses the standard Helvetica fonts (WinAnsi encoding); characters outside Latin-1 are replaced.
/// </summary>
public static class PdfWriter
{
    private const double PageW = 841.89, PageH = 595.28, Margin = 36;
    private const double RowH = 15, HeaderH = 18, FontSize = 8, HeaderFontSize = 8;
    public const int MaxPdfRows = 20_000;

    private static readonly (double R, double G, double B)[] Palette =
    [
        (0.149, 0.443, 0.835), (0.906, 0.396, 0.161), (0.110, 0.651, 0.471), (0.894, 0.643, 0.090),
        (0.831, 0.341, 0.533), (0.459, 0.341, 0.808), (0.090, 0.592, 0.651), (0.600, 0.400, 0.200),
    ];

    public static byte[] Write(string title, string? subtitle, ReportResult result, string? chart = null)
    {
        var rows = result.Rows.Take(MaxPdfRows).ToList();
        var cols = result.Columns;
        var widths = ColumnWidths(cols, rows);
        var pages = new List<StringBuilder>();
        StringBuilder? page = null;
        double y = 0;

        void NewPage()
        {
            page = new StringBuilder();
            pages.Add(page);
            y = PageH - Margin;
            if (pages.Count == 1)
            {
                Text(page, Margin, y - 16, 16, bold: true, title);
                y -= 24;
                if (!string.IsNullOrWhiteSpace(subtitle))
                {
                    Fill(page, 0.35, 0.35, 0.35);
                    Text(page, Margin, y - 10, 9, bold: false, subtitle!);
                    Fill(page, 0, 0, 0);
                    y -= 18;
                }
            }
            else
            {
                Fill(page, 0.35, 0.35, 0.35);
                Text(page, Margin, y - 9, 8, bold: false, title);
                Fill(page, 0, 0, 0);
                y -= 16;
            }
        }

        void TableHeader()
        {
            Fill(page!, 0.92, 0.94, 0.96);
            Rect(page!, Margin, y - HeaderH, PageW - 2 * Margin, HeaderH, fill: true);
            Fill(page!, 0.10, 0.12, 0.16);
            var x = Margin + 4;
            for (var i = 0; i < cols.Count; i++)
            {
                var label = Fit(cols[i].Label, widths[i] - 8, HeaderFontSize, bold: true);
                if (cols[i].Type == "number") Text(page!, x + widths[i] - 8 - Measure(label, HeaderFontSize, true), y - 12.5, HeaderFontSize, true, label);
                else Text(page!, x, y - 12.5, HeaderFontSize, true, label);
                x += widths[i];
            }
            Fill(page!, 0, 0, 0);
            y -= HeaderH;
        }

        NewPage();

        // Summary reports: a bar chart of the count (or the first calculation) per group, top 15.
        var grouped = cols.Any(c => c.Key == "count");
        if (grouped && rows.Count > 0 && chart != "table")
        {
            var valueIndex = cols.ToList().FindIndex(c => c.Key == "count");
            var labelCount = cols.TakeWhile(c => c.Key != "count").Count();
            var data = rows.Take(15).Select(r => (Label: string.Join(" / ", Enumerable.Range(0, labelCount).Select(i => ReportEngine.Text(r[i], cols[i].Type)).Select(s => s.Length == 0 ? "(empty)" : s)),
                                                 Value: Convert.ToDouble(r[valueIndex] ?? 0, CultureInfo.InvariantCulture))).ToList();
            var max = Math.Max(1, data.Max(d => d.Value));
            const double labelW = 220, barMax = 400;
            Text(page!, Margin, y - 10, 9, true, rows.Count > 15 ? $"Top 15 of {rows.Count} groups" : "Overview");
            y -= 18;
            for (var i = 0; i < data.Count; i++)
            {
                var (r, g, b) = Palette[i % Palette.Length];
                Text(page!, Margin, y - 9, 8, false, Fit(data[i].Label, labelW - 8, 8, false));
                Fill(page!, r, g, b);
                Rect(page!, Margin + labelW, y - 10, Math.Max(1.5, data[i].Value / max * barMax), 9, fill: true);
                Fill(page!, 0.2, 0.2, 0.2);
                Text(page!, Margin + labelW + Math.Max(1.5, data[i].Value / max * barMax) + 4, y - 9, 8, false, Num(data[i].Value));
                Fill(page!, 0, 0, 0);
                y -= 13;
            }
            y -= 12;
        }

        TableHeader();
        var stripe = false;
        foreach (var row in rows)
        {
            if (y - RowH < Margin + 20)
            {
                NewPage();
                TableHeader();
                stripe = false;
            }
            if (stripe)
            {
                Fill(page!, 0.975, 0.98, 0.985);
                Rect(page!, Margin, y - RowH, PageW - 2 * Margin, RowH, fill: true);
                Fill(page!, 0, 0, 0);
            }
            stripe = !stripe;
            var x = Margin + 4;
            for (var i = 0; i < cols.Count; i++)
            {
                var text = Fit(ReportEngine.Text(row[i], cols[i].Type), widths[i] - 8, FontSize, false);
                if (cols[i].Type == "number") Text(page!, x + widths[i] - 8 - Measure(text, FontSize, false), y - 10.5, FontSize, false, text);
                else Text(page!, x, y - 10.5, FontSize, false, text);
                x += widths[i];
            }
            y -= RowH;
        }
        if (rows.Count == 0) Text(page!, Margin + 4, y - 12, 9, false, "No rows match this report.");
        else if (result.Rows.Count > rows.Count || result.Truncated)
        {
            if (y - RowH < Margin + 20) { NewPage(); }
            Fill(page!, 0.4, 0.4, 0.4);
            Text(page!, Margin + 4, y - 14, 8, false, $"First {rows.Count:N0} of {Math.Max(result.Total, result.Rows.Count):N0} rows. Use Excel or CSV for the complete data.");
            Fill(page!, 0, 0, 0);
        }

        // Footer on every page.
        var generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        for (var p = 0; p < pages.Count; p++)
        {
            Fill(pages[p], 0.45, 0.45, 0.45);
            Text(pages[p], Margin, 20, 7.5, false, $"Zentriq · {title} · generated {generated}");
            var label = $"Page {p + 1} of {pages.Count}";
            Text(pages[p], PageW - Margin - Measure(label, 7.5, false), 20, 7.5, false, label);
        }
        return Assemble(pages);
    }

    // ---- layout helpers ------------------------------------------------------------------------------

    private static double[] ColumnWidths(IReadOnlyList<ReportResultColumn> cols, List<object?[]> rows)
    {
        var available = PageW - 2 * Margin;
        var want = new double[cols.Count];
        for (var i = 0; i < cols.Count; i++)
        {
            var sample = rows.Take(200).Select(r => Measure(ReportEngine.Text(r[i], cols[i].Type), FontSize, false)).DefaultIfEmpty(0).Max();
            want[i] = Math.Clamp(Math.Max(sample, Measure(cols[i].Label, HeaderFontSize, true)) + 10, 40, 260);
        }
        var total = want.Sum();
        if (total <= available) return want.Select(w => w * available / total).ToArray();
        // Too wide: scale down, keep a minimum width, then fit exactly.
        var scaled = want.Select(w => Math.Max(36, w * available / total)).ToArray();
        var sum = scaled.Sum();
        return scaled.Select(w => w * available / sum).ToArray();
    }

    private static string Fit(string text, double width, double size, bool bold)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        if (Measure(text, size, bold) <= width) return text;
        var lo = 0; var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Measure(text[..mid] + "…", size, bold) <= width) lo = mid; else hi = mid - 1;
        }
        return lo == 0 ? "" : text[..lo].TrimEnd() + "…";
    }

    private static string Num(double v) => Math.Abs(v - Math.Round(v)) < 0.005 ? Math.Round(v).ToString("N0", CultureInfo.InvariantCulture) : v.ToString("N2", CultureInfo.InvariantCulture);

    // Helvetica advance widths (1/1000 em) for ASCII 32–126 (Adobe AFM).
    private static readonly short[] Helvetica =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
        278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667,
        611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833,
        556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    public static double Measure(string text, double size, bool bold)
    {
        double w = 0;
        foreach (var ch in text) w += ch is >= ' ' and <= '~' ? Helvetica[ch - 32] : 556;
        return w * size / 1000 * (bold ? 1.07 : 1.0);
    }

    // ---- PDF primitives ---------------------------------------------------------------------------------

    private static void Text(StringBuilder sb, double x, double y, double size, bool bold, string text) =>
        sb.Append(CultureInfo.InvariantCulture, $"BT /{(bold ? "F2" : "F1")} {size:0.##} Tf {x:0.##} {y:0.##} Td ({Escape(text)}) Tj ET\n");

    private static void Fill(StringBuilder sb, double r, double g, double b) =>
        sb.Append(CultureInfo.InvariantCulture, $"{r:0.###} {g:0.###} {b:0.###} rg\n");

    private static void Rect(StringBuilder sb, double x, double y, double w, double h, bool fill) =>
        sb.Append(CultureInfo.InvariantCulture, $"{x:0.##} {y:0.##} {w:0.##} {h:0.##} re {(fill ? "f" : "S")}\n");

    /// <summary>PDF string literal in WinAnsi: Latin-1 as is, common typographic characters mapped, others '?'.</summary>
    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            var c = ch switch
            {
                '…' => (char)0x85, '–' => (char)0x96, '—' => (char)0x97, '‘' => (char)0x91, '’' => (char)0x92,
                '“' => (char)0x93, '”' => (char)0x94, '•' => (char)0x95, '€' => (char)0x80, '·' => (char)0xB7,
                '≥' => '\0', '≤' => '\0',
                _ => ch,
            };
            if (ch == '≥') { sb.Append(">="); continue; }
            if (ch == '≤') { sb.Append("<="); continue; }
            if (c is '\\' or '(' or ')') sb.Append('\\').Append(c);
            else if (c < ' ') sb.Append(' ');
            else if (c > 0xFF) sb.Append('?');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static byte[] Assemble(List<StringBuilder> pages)
    {
        var latin1 = Encoding.Latin1;
        using var ms = new MemoryStream();
        var offsets = new List<long>();
        void Raw(string s) { var b = latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Obj(int n, string body) { offsets.Add(ms.Position); Raw($"{n} 0 obj\n{body}\nendobj\n"); }

        Raw("%PDF-1.4\n%âãÏÓ\n");
        // 1 catalog, 2 pages, 3 Helvetica, 4 Helvetica-Bold, then per page: page object and content stream.
        var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{5 + i * 2} 0 R"));
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, $"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");
        Obj(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Obj(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        for (var i = 0; i < pages.Count; i++)
        {
            var pageObj = 5 + i * 2;
            Obj(pageObj, string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageW:0.##} {PageH:0.##}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {pageObj + 1} 0 R >>"));
            var content = latin1.GetBytes(pages[i].ToString());
            offsets.Add(ms.Position);
            Raw($"{pageObj + 1} 0 obj\n<< /Length {content.Length} >>\nstream\n");
            ms.Write(content, 0, content.Length);
            Raw("\nendstream\nendobj\n");
        }
        var xref = ms.Position;
        var count = offsets.Count + 1;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {count}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Raw(sb.ToString());
        return ms.ToArray();
    }
}
