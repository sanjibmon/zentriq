using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace Zentriq.Infrastructure.Reports;

public sealed class ReportFilter
{
    [JsonPropertyName("column")] public string Column { get; set; } = "";
    [JsonPropertyName("op")] public string Op { get; set; } = "eq";
    [JsonPropertyName("value")] public string? Value { get; set; }
}

public sealed class ReportSort
{
    [JsonPropertyName("column")] public string Column { get; set; } = "";
    [JsonPropertyName("dir")] public string Dir { get; set; } = "asc";
}

/// <summary>A calculation per group: count_distinct, sum, avg, min or max of a column.</summary>
public sealed class ReportAggregate
{
    [JsonPropertyName("fn")] public string Fn { get; set; } = "sum";
    [JsonPropertyName("column")] public string Column { get; set; } = "";

    public string Key => $"agg_{Fn}_{Column}";
}

/// <summary>What a saved report stores: only column keys, filters, grouping and sorting — never SQL.</summary>
public sealed class ReportSpec
{
    [JsonPropertyName("columns")] public List<string> Columns { get; set; } = [];
    [JsonPropertyName("filters")] public List<ReportFilter> Filters { get; set; } = [];
    [JsonPropertyName("groupBy")] public List<string>? GroupBy { get; set; }
    [JsonPropertyName("sort")] public List<ReportSort>? Sort { get; set; }
    /// <summary>Summary reports: calculations per group besides the row count.</summary>
    [JsonPropertyName("aggregates")] public List<ReportAggregate>? Aggregates { get; set; }
    /// <summary>Summary reports: date columns in the grouping are bucketed by day, week, month, quarter or year.</summary>
    [JsonPropertyName("dateBucket")] public string? DateBucket { get; set; }
    /// <summary>How the console draws a summary (donut, pie, bar, column, line); kept with the report for dashboards and PDF.</summary>
    [JsonPropertyName("chart")] public string? Chart { get; set; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static ReportSpec Parse(string json) => JsonSerializer.Deserialize<ReportSpec>(json, Json) ?? new ReportSpec();
    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

public sealed record ReportResultColumn(string Key, string Label, string Type);
public sealed record ReportResult(IReadOnlyList<ReportResultColumn> Columns, IReadOnlyList<object?[]> Rows, long Total, bool Truncated);

public sealed class ReportValidationException(string message) : Exception(message);

/// <summary>Builds parameterised SQL from a <see cref="ReportSpec"/> over a <see cref="ReportCatalog"/> dataset and runs it.</summary>
public static class ReportEngine
{
    public static readonly string[] Operators = ["eq", "neq", "contains", "not_contains", "starts", "gt", "gte", "lt", "lte", "empty", "not_empty", "last_days", "older_days", "in"];
    public static readonly string[] AggregateFunctions = ["count_distinct", "sum", "avg", "min", "max"];
    public static readonly string[] DateBuckets = ["day", "week", "month", "quarter", "year"];
    public static readonly string[] Charts = ["donut", "pie", "bar", "column", "line", "table"];
    public const int MaxColumns = 40;
    public const int MaxFilters = 25;
    public const int MaxAggregates = 6;

    public static string AggregateLabel(ReportAggregate a, ReportColumn c) => a.Fn switch
    {
        "count_distinct" => $"Distinct {c.Label}",
        "sum" => $"Total {c.Label}",
        "avg" => $"Average {c.Label}",
        "min" => $"Lowest {c.Label}",
        "max" => $"Highest {c.Label}",
        _ => c.Label,
    };

    /// <summary>Checks the spec against the dataset; throws <see cref="ReportValidationException"/> with a readable message.</summary>
    public static void Validate(ReportDataset ds, ReportSpec spec)
    {
        var grouped = spec.GroupBy is { Count: > 0 };
        if (!grouped && spec.Columns.Count == 0) throw new ReportValidationException("Choose at least one column.");
        if (spec.Columns.Count > MaxColumns) throw new ReportValidationException($"A report can have up to {MaxColumns} columns.");
        if (spec.Filters.Count > MaxFilters) throw new ReportValidationException($"A report can have up to {MaxFilters} filters.");
        foreach (var c in spec.Columns.Concat(spec.GroupBy ?? []))
            if (ds.Column(c) is null) throw new ReportValidationException($"Unknown column '{c}' for {ds.Name}.");
        if (grouped && spec.GroupBy!.Count > 3) throw new ReportValidationException("Group by up to three columns.");
        if (spec.DateBucket is not null && !DateBuckets.Contains(spec.DateBucket)) throw new ReportValidationException($"Unknown date grouping '{spec.DateBucket}'.");
        if (spec.Chart is not null && !Charts.Contains(spec.Chart)) throw new ReportValidationException($"Unknown chart type '{spec.Chart}'.");
        var aggregates = spec.Aggregates ?? [];
        if (aggregates.Count > 0 && !grouped) throw new ReportValidationException("Calculations (total, average, …) need a summary report: group by at least one column.");
        if (aggregates.Count > MaxAggregates) throw new ReportValidationException($"Up to {MaxAggregates} calculations.");
        foreach (var a in aggregates)
        {
            var col = ds.Column(a.Column) ?? throw new ReportValidationException($"Unknown column '{a.Column}' for a calculation.");
            if (!AggregateFunctions.Contains(a.Fn)) throw new ReportValidationException($"Unknown calculation '{a.Fn}'.");
            if (a.Fn is "sum" or "avg" && col.Type != ReportColumnType.Number) throw new ReportValidationException($"{col.Label} is not a number: it can be counted, not added up or averaged.");
            if (a.Fn is "min" or "max" && col.Type is not (ReportColumnType.Number or ReportColumnType.Date)) throw new ReportValidationException($"Lowest/highest needs a number or date column ({col.Label} is neither).");
        }
        if (aggregates.Select(a => a.Key).Distinct().Count() != aggregates.Count) throw new ReportValidationException("The same calculation is listed twice.");
        foreach (var f in spec.Filters)
        {
            var col = ds.Column(f.Column) ?? throw new ReportValidationException($"Unknown filter column '{f.Column}'.");
            if (!Operators.Contains(f.Op)) throw new ReportValidationException($"Unknown filter operator '{f.Op}'.");
            if (f.Op is "empty" or "not_empty") continue;
            if (string.IsNullOrWhiteSpace(f.Value)) throw new ReportValidationException($"Enter a value for the filter on {col.Label}.");
            if (f.Op is "last_days" or "older_days")
            {
                if (col.Type != ReportColumnType.Date) throw new ReportValidationException($"'{f.Op}' only applies to date columns ({col.Label} is not a date).");
                if (!int.TryParse(f.Value, out var d) || d < 0 || d > 36500) throw new ReportValidationException($"Enter a number of days for {col.Label}.");
            }
            else if (f.Op is "gt" or "gte" or "lt" or "lte")
            {
                if (col.Type == ReportColumnType.Number && !decimal.TryParse(f.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                    throw new ReportValidationException($"Enter a number for {col.Label}.");
                if (col.Type == ReportColumnType.Date && !DateTimeOffset.TryParse(f.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
                    throw new ReportValidationException($"Enter a date (yyyy-mm-dd) for {col.Label}.");
            }
        }
        foreach (var s in spec.Sort ?? [])
        {
            var ok = s.Column == "count" ? grouped
                : grouped ? spec.GroupBy!.Contains(s.Column) || aggregates.Any(a => a.Key == s.Column)
                : ds.Column(s.Column) is not null;
            if (!ok) throw new ReportValidationException($"Cannot sort by '{s.Column}'.");
        }
    }

    /// <summary>Builds the SQL; column keys are validated identifiers, every value is a parameter.</summary>
    public static (string Sql, string CountSql, List<NpgsqlParameter> Params, List<ReportResultColumn> Columns) Build(ReportDataset ds, ReportSpec spec, int limit)
    {
        Validate(ds, spec);
        var grouped = spec.GroupBy is { Count: > 0 };
        var aggs = grouped ? spec.Aggregates ?? [] : [];
        var needed = spec.Columns.Concat(spec.GroupBy ?? []).Concat(spec.Filters.Select(f => f.Column)).Concat((spec.Sort ?? []).Select(s => s.Column))
            .Concat(aggs.Select(a => a.Column)).Where(c => c != "count" && ds.Column(c) is not null).Distinct().ToList();
        var inner = $"SELECT {string.Join(", ", needed.Select(k => $"{ds.Column(k)!.Expr} AS \"{k}\""))} FROM {ds.From}";

        var ps = new List<NpgsqlParameter>();
        var where = new List<string>();
        foreach (var f in spec.Filters)
        {
            var col = ds.Column(f.Column)!;
            var c = $"t.\"{col.Key}\"";
            string P(object v, NpgsqlDbType t) { var n = "p" + ps.Count; ps.Add(new NpgsqlParameter(n, t) { Value = v }); return "@" + n; }
            var text = $"({c})::text";
            where.Add(f.Op switch
            {
                "empty" => $"({c} IS NULL OR {text} = '')",
                "not_empty" => $"({c} IS NOT NULL AND {text} <> '')",
                "contains" => $"{text} ILIKE {P("%" + Escape(f.Value!) + "%", NpgsqlDbType.Text)}",
                "not_contains" => $"({c} IS NULL OR {text} NOT ILIKE {P("%" + Escape(f.Value!) + "%", NpgsqlDbType.Text)})",
                "starts" => $"{text} ILIKE {P(Escape(f.Value!) + "%", NpgsqlDbType.Text)}",
                "in" => $"lower({text}) = ANY({P(f.Value!.Split(',').Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text)})",
                "last_days" => $"{c} >= now() - make_interval(days => {P(int.Parse(f.Value!, CultureInfo.InvariantCulture), NpgsqlDbType.Integer)})",
                "older_days" => $"({c} IS NULL OR {c} < now() - make_interval(days => {P(int.Parse(f.Value!, CultureInfo.InvariantCulture), NpgsqlDbType.Integer)}))",
                _ => Compare(col, c, f, P),
            });
        }

        string select, groupClause = "", order;
        var cols = new List<ReportResultColumn>();
        if (grouped)
        {
            // Date columns in the grouping can be bucketed (2026-10, 2026-Q4, …); the label sorts chronologically.
            string GroupExpr(string g) => ds.Column(g)!.Type == ReportColumnType.Date && spec.DateBucket is { } b
                ? $"to_char(date_trunc('{b}', t.\"{g}\"), '{BucketFormat(b)}')"
                : $"t.\"{g}\"";
            select = string.Join(", ", spec.GroupBy!.Select(g => $"{GroupExpr(g)} AS \"{g}\"")) + ", count(*) AS \"count\"";
            foreach (var a in aggs)
            {
                var c = $"t.\"{a.Column}\"";
                select += a.Fn switch
                {
                    "count_distinct" => $", count(DISTINCT {c}) AS \"{a.Key}\"",
                    "sum" => $", sum(({c})::numeric) AS \"{a.Key}\"",
                    "avg" => $", round(avg(({c})::numeric), 2) AS \"{a.Key}\"",
                    "min" => $", min({c}) AS \"{a.Key}\"",
                    _ => $", max({c}) AS \"{a.Key}\"",
                };
            }
            groupClause = " GROUP BY " + string.Join(", ", spec.GroupBy!.Select(GroupExpr));
            cols.AddRange(spec.GroupBy!.Select(g => ds.Column(g)!.Type == ReportColumnType.Date && spec.DateBucket is { } b
                ? new ReportResultColumn(g, $"{ds.Column(g)!.Label} ({b})", "text")
                : Col(ds.Column(g)!)));
            cols.Add(new ReportResultColumn("count", "Count", "number"));
            foreach (var a in aggs)
            {
                var col = ds.Column(a.Column)!;
                var type = a.Fn is "min" or "max" && col.Type == ReportColumnType.Date ? "date" : "number";
                cols.Add(new ReportResultColumn(a.Key, AggregateLabel(a, col), type));
            }
        }
        else
        {
            select = string.Join(", ", spec.Columns.Select(k => $"t.\"{k}\""));
            cols.AddRange(spec.Columns.Select(k => Col(ds.Column(k)!)));
        }
        // Summary reports sort by output columns (group labels, count, calculations); detail reports by source columns.
        var sorts = (spec.Sort ?? []).Select(s => $"{(grouped ? $"\"{s.Column}\"" : $"t.\"{s.Column}\"")} {(s.Dir == "desc" ? "DESC" : "ASC")} NULLS LAST").ToList();
        if (sorts.Count == 0) sorts.Add(grouped ? "\"count\" DESC" : "1");
        order = " ORDER BY " + string.Join(", ", sorts);
        var whereSql = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
        var body = $"FROM ({inner}) t{whereSql}{groupClause}";
        var sql = $"SELECT {select} {body}{order} LIMIT {limit + 1}";
        var countSql = grouped ? $"SELECT count(*) FROM (SELECT 1 {body}) g" : $"SELECT count(*) {body}";
        return (sql, countSql, ps, cols);
    }

    private static string Compare(ReportColumn col, string c, ReportFilter f, Func<object, NpgsqlDbType, string> p)
    {
        var op = f.Op switch { "eq" => "=", "neq" => "<>", "gt" => ">", "gte" => ">=", "lt" => "<", "lte" => "<=", _ => "=" };
        switch (col.Type)
        {
            case ReportColumnType.Number when decimal.TryParse(f.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var n):
                return f.Op == "neq" ? $"({c} IS NULL OR {c}::numeric <> {p(n, NpgsqlDbType.Numeric)})" : $"{c}::numeric {op} {p(n, NpgsqlDbType.Numeric)}";
            case ReportColumnType.Date when DateTimeOffset.TryParse(f.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d):
                // Date equality compares the calendar day.
                return f.Op is "eq" or "neq" ? $"({c})::date {op} {p(d.UtcDateTime.Date, NpgsqlDbType.Date)}" : $"{c} {op} {p(d.ToUniversalTime(), NpgsqlDbType.TimestampTz)}";
            default:
                var v = p(f.Value!, NpgsqlDbType.Text);
                return f.Op switch
                {
                    "eq" => $"lower(({c})::text) = lower({v})",
                    "neq" => $"({c} IS NULL OR lower(({c})::text) <> lower({v}))",
                    _ => $"({c})::text {op} {v}",
                };
        }
    }

    private static string BucketFormat(string bucket) => bucket switch
    {
        "day" => "YYYY-MM-DD",
        "week" => "IYYY-\"W\"IW",
        "month" => "YYYY-MM",
        "quarter" => "YYYY-\"Q\"Q",
        _ => "YYYY",
    };

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    private static ReportResultColumn Col(ReportColumn c) => new(c.Key, c.Label, c.Type switch { ReportColumnType.Number => "number", ReportColumnType.Date => "date", ReportColumnType.YesNo => "yesno", _ => "text" });

    public static async Task<ReportResult> RunAsync(NpgsqlDataSource ds, string dataset, ReportSpec spec, int limit, CancellationToken ct)
    {
        var d = ReportCatalog.Find(dataset) ?? throw new ReportValidationException($"Unknown dataset '{dataset}'.");
        var (sql, countSql, ps, cols) = Build(d, spec, limit);
        await using var conn = await ds.OpenConnectionAsync(ct);
        var rows = new List<object?[]>();
        await using (var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 120 })
        {
            foreach (var p in ps) cmd.Parameters.Add(p.Clone());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var row = new object?[cols.Count];
                for (var i = 0; i < cols.Count; i++)
                {
                    var v = r.IsDBNull(i) ? null : r.GetValue(i);
                    row[i] = v switch
                    {
                        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
                        DateOnly dd => new DateTimeOffset(dd.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                        decimal m => m,
                        long or int or short => Convert.ToInt64(v, CultureInfo.InvariantCulture),
                        null => null,
                        _ when v is DateTimeOffset => v,
                        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
                    };
                }
                rows.Add(row);
            }
        }
        var truncated = rows.Count > limit;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        long total = rows.Count;
        if (truncated)
        {
            await using var cc = new NpgsqlCommand(countSql, conn) { CommandTimeout = 120 };
            foreach (var p in ps) cc.Parameters.Add(p.Clone());
            total = Convert.ToInt64(await cc.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        return new ReportResult(cols, rows, total, truncated);
    }

    // ---- export ----------------------------------------------------------------------------------

    public static string Text(object? v, string type) => v switch
    {
        null => "",
        DateTimeOffset d => type == "date" ? d.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" : d.ToString("o", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    public static byte[] ToCsv(ReportResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", result.Columns.Select(c => Csv(c.Label))));
        foreach (var row in result.Rows)
            sb.AppendLine(string.Join(",", row.Select((v, i) => Csv(Text(v, result.Columns[i].Type)))));
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static string Csv(string s)
    {
        // Neutralise spreadsheet formula injection, then quote.
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0]) && !decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
