namespace Zentriq.Infrastructure.Vulnerabilities;

/// <summary>
/// Prioritisation, following common risk-based vulnerability management practice: severity from CVSS, raised when
/// the CVE is known to be exploited (CISA KEV) or likely to be exploited soon (FIRST EPSS).
///   P1  on the CISA KEV list, or CVSS ≥ 9.0 with EPSS ≥ 10 %
///   P2  CVSS ≥ 9.0, or CVSS ≥ 7.0 with EPSS ≥ 10 %
///   P3  CVSS ≥ 7.0, or CVSS ≥ 4.0 with EPSS ≥ 10 %
///   P4  everything else
/// Risk score 0–100 = CVSS × 7 + EPSS × 20 + 10 when on KEV (capped at 100).
/// </summary>
public static class VulnScoring
{
    public const decimal EpssLikely = 0.10m;

    public static string SeverityFor(decimal? cvss) => cvss switch
    {
        null => "none",
        >= 9.0m => "critical",
        >= 7.0m => "high",
        >= 4.0m => "medium",
        > 0m => "low",
        _ => "none",
    };

    public static string Priority(decimal? cvss, decimal? epss, bool kev)
    {
        var s = cvss ?? 0;
        var likely = (epss ?? 0) >= EpssLikely;
        if (kev || (s >= 9.0m && likely)) return "P1";
        if (s >= 9.0m || (s >= 7.0m && likely)) return "P2";
        if (s >= 7.0m || (s >= 4.0m && likely)) return "P3";
        return "P4";
    }

    public static int Risk(decimal? cvss, decimal? epss, bool kev) =>
        (int)Math.Min(100, Math.Round((cvss ?? 0) * 7 + Math.Min(1, epss ?? 0) * 20 + (kev ? 10 : 0)));
}
