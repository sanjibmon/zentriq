using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Zentriq.Infrastructure.Assistant;

/// <summary>One section of a Zentriq document (a heading and the text under it).</summary>
public sealed record DocSection(string File, string DocTitle, string Heading, string Text)
{
    /// <summary>The console's help link for this section, e.g. "agents-and-agentless.md#deploying".</summary>
    public string Anchor => File + "#" + AssistantKnowledge.Slug(Heading);
}

public sealed record DocHit(DocSection Section, double Score);

/// <summary>
/// Zentriq Assistant (build 145): the customer documentation compiled into the server, split into sections and searched
/// with BM25. Works with no AI model and no internet; with a model, the best sections are its context.
/// </summary>
public sealed partial class AssistantKnowledge
{
    private readonly List<DocSection> _sections = [];
    private readonly List<Dictionary<string, int>> _tf = [];
    private readonly List<int> _len = [];
    private readonly Dictionary<string, int> _df = new(StringComparer.Ordinal);
    private readonly double _avgLen;

    public IReadOnlyList<DocSection> Sections => _sections;

    public AssistantKnowledge() : this(LoadEmbedded()) { }

    public AssistantKnowledge(IEnumerable<(string File, string Markdown)> documents)
    {
        foreach (var (file, md) in documents)
            foreach (var s in Split(file, md)) Add(s);
        _avgLen = _len.Count == 0 ? 1 : _len.Average();
    }

    private void Add(DocSection s)
    {
        var tokens = Tokens(s.DocTitle + " " + s.Heading + " " + s.Heading + " " + s.Text).ToList();
        if (tokens.Count == 0) return;
        var tf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tokens) tf[t] = tf.GetValueOrDefault(t) + 1;
        foreach (var t in tf.Keys) _df[t] = _df.GetValueOrDefault(t) + 1;
        _sections.Add(s); _tf.Add(tf); _len.Add(tokens.Count);
    }

    public IReadOnlyList<DocHit> Search(string query, int take = 5)
    {
        var q = Tokens(query).Distinct().ToList();
        if (q.Count == 0 || _sections.Count == 0) return [];
        const double k1 = 1.4, b = 0.75;
        var n = _sections.Count;
        var hits = new List<DocHit>();
        for (var i = 0; i < n; i++)
        {
            double score = 0;
            foreach (var t in q)
            {
                if (!_tf[i].TryGetValue(t, out var f)) continue;
                var df = _df[t];
                var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                score += idf * (f * (k1 + 1)) / (f + k1 * (1 - b + b * _len[i] / _avgLen));
            }
            if (score > 0) hits.Add(new DocHit(_sections[i], score));
        }
        return hits.OrderByDescending(h => h.Score).Take(take).ToList();
    }

    // ---- documents -----------------------------------------------------------------------------------------------

    public static IEnumerable<(string File, string Markdown)> LoadEmbedded()
    {
        var asm = typeof(AssistantKnowledge).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("Zentriq.Docs.", StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal)).Order())
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s, Encoding.UTF8);
            yield return (name["Zentriq.Docs.".Length..], r.ReadToEnd());
        }
    }

    /// <summary>Sections at headings of level 2 and 3; long sections are cut into parts of about 2,500 characters.</summary>
    public static IEnumerable<DocSection> Split(string file, string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var title = lines.FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim() ?? file;
        var heading = title;
        var sb = new StringBuilder();
        var inCode = false;
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) inCode = !inCode;
            var m = inCode ? null : HeadingRx().Match(line);
            if (m is { Success: true } && m.Groups[1].Length is 2 or 3)
            {
                foreach (var s in Flush(file, title, heading, sb)) yield return s;
                heading = m.Groups[2].Value.Trim();
                continue;
            }
            if (!inCode && line.StartsWith("# ", StringComparison.Ordinal)) continue;
            sb.Append(line).Append('\n');
        }
        foreach (var s in Flush(file, title, heading, sb)) yield return s;
    }

    private static IEnumerable<DocSection> Flush(string file, string title, string heading, StringBuilder sb)
    {
        var text = sb.ToString().Trim();
        sb.Clear();
        if (text.Length < 40) yield break;
        const int max = 2500;
        if (text.Length <= max) { yield return new DocSection(file, title, heading, text); yield break; }
        // Paragraphs; a paragraph longer than a part (a big table) is cut at its lines.
        var parts = text.Split("\n\n").SelectMany(p => p.Length <= max ? new[] { p } : p.Split('\n')).ToList();
        var cur = new StringBuilder();
        var part = 1;
        foreach (var p in parts)
        {
            if (cur.Length > 0 && cur.Length + p.Length > max)
            {
                yield return new DocSection(file, title, part == 1 ? heading : $"{heading} ({part})", cur.ToString().Trim());
                cur.Clear(); part++;
            }
            cur.Append(p).Append("\n\n");
        }
        if (cur.Length > 0) yield return new DocSection(file, title, part == 1 ? heading : $"{heading} ({part})", cur.ToString().Trim());
    }

    public static string Slug(string heading) =>
        Regex.Replace(Regex.Replace(heading.ToLowerInvariant(), @"[^\p{L}\p{N}\s-]", ""), @"\s+", "-").Trim('-');

    // ---- tokens --------------------------------------------------------------------------------------------------

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a","an","the","and","or","of","to","in","on","for","is","are","be","it","its","this","that","with","as","at","by","from",
        "how","what","why","when","where","which","who","do","does","did","can","could","should","would","i","we","you","my","our",
        "your","me","us","not","no","yes","if","then","than","so","there","here","have","has","had","was","were","will","into",
        "about","any","all","some","get","got","use","using","please","help","zentriq",
    };

    /// <summary>Words that mean the same thing in questions and in the documentation.</summary>
    private static readonly Dictionary<string, string> Same = new(StringComparer.Ordinal)
    {
        ["login"] = "signin", ["logon"] = "signin", ["log"] = "log", ["signin"] = "signin", ["sign"] = "sign",
        ["pc"] = "device", ["computer"] = "device", ["machine"] = "device", ["laptop"] = "device", ["endpoint"] = "device",
        ["licence"] = "license", ["licences"] = "license", ["patches"] = "patch", ["patching"] = "patch",
        ["mfa"] = "mfa", ["2fa"] = "mfa", ["otp"] = "mfa", ["ad"] = "activedirectory", ["ldap"] = "ldap",
        ["cert"] = "certificate", ["ssl"] = "certificate", ["tls"] = "certificate", ["av"] = "antivirus", ["defender"] = "antivirus",
        ["mac"] = "macos", ["osx"] = "macos", ["ubuntu"] = "linux", ["rhel"] = "linux", ["debian"] = "linux",
        ["error"] = "error", ["failed"] = "fail", ["failure"] = "fail", ["fails"] = "fail", ["failing"] = "fail",
        ["offline"] = "offline", ["online"] = "online", ["install"] = "install", ["installer"] = "install", ["installation"] = "install",
        ["setup"] = "install", ["upgrade"] = "upgrade", ["update"] = "upgrade", ["updates"] = "upgrade",
    };

    public static IEnumerable<string> Tokens(string text)
    {
        foreach (Match m in WordRx().Matches(text.ToLowerInvariant().Replace("sign-in", "signin").Replace("active directory", "activedirectory")))
        {
            var w = m.Value.Trim('-', '.', '_');
            if (w.Length < 2 || Stop.Contains(w)) continue;
            if (Same.TryGetValue(w, out var same)) { yield return same; continue; }
            yield return Stem(w);
        }
    }

    private static string Stem(string w)
    {
        if (w.Length > 5 && w.EndsWith("ing", StringComparison.Ordinal)) return w[..^3];
        if (w.Length > 4 && w.EndsWith("ed", StringComparison.Ordinal)) return w[..^2];
        if (w.Length > 4 && w.EndsWith("ies", StringComparison.Ordinal)) return w[..^3] + "y";
        if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal)) return w[..^1];
        return w;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")] private static partial Regex HeadingRx();
    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}._-]*")] private static partial Regex WordRx();
}
