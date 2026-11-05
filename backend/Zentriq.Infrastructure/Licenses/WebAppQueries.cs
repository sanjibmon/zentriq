namespace Zentriq.Infrastructure.Licenses;

/// <summary>Shared SQL of Web Apps &amp; Accounts.</summary>
public static class WebAppQueries
{
    /// <summary>
    /// Every account with what the directory and the devices say about its user: left the organisation (Asset Lifecycle
    /// people), disabled in Microsoft Entra ID, last Entra sign-in, last use seen on a device. Needs <c>@days</c> (inactivity).
    /// </summary>
    public const string AccountsCte = """
        acc AS (
            SELECT w.*, app.name AS app_name,
                   lp.name AS person_name, (lp.status = 'left') AS person_left,
                   cu.account_enabled AS directory_enabled, cu.last_sign_in AS directory_sign_in, cu.department AS directory_department,
                   (SELECT max(u.last_seen) FROM zentriq.webapp_usage u
                     WHERE u.app_id = w.app_id AND u.user_name <> ''
                       AND lower(split_part(u.user_name, '\', 2)) = lower(split_part(w.user_name, '@', 1))) AS last_used
              FROM zentriq.webapp_accounts w
              JOIN zentriq.webapps app ON app.id = w.app_id
              LEFT JOIN LATERAL (SELECT p.name, p.status FROM zentriq.lifecycle_people p
                                  WHERE p.email IS NOT NULL AND lower(p.email) IN (lower(w.user_name), lower(COALESCE(w.email, '')))
                                  ORDER BY p.status = 'left' DESC LIMIT 1) lp ON true
              LEFT JOIN LATERAL (SELECT c.account_enabled, c.last_sign_in, c.department FROM zentriq.sam_cloud_users c
                                  WHERE lower(c.upn) = lower(w.user_name) OR (c.mail IS NOT NULL AND lower(c.mail) IN (lower(w.user_name), lower(COALESCE(w.email, ''))))
                                  ORDER BY c.synced_at DESC LIMIT 1) cu ON true),
        accx AS (
            SELECT acc.*,
                   GREATEST(acc.last_login, acc.directory_sign_in, acc.last_used) AS last_activity,
                   (acc.status = 'active' AND (COALESCE(acc.person_left, false) OR acc.directory_enabled = false)) AS orphaned,
                   CASE WHEN COALESCE(acc.person_left, false) THEN acc.person_name || ' has left the organisation (Asset Lifecycle › People).'
                        WHEN acc.directory_enabled = false THEN 'The user is disabled in Microsoft Entra ID but the account is still active.' END AS orphan_reason,
                   (acc.status = 'active' AND acc.created_at < now() - make_interval(days => @days)
                        AND COALESCE(GREATEST(acc.last_login, acc.directory_sign_in, acc.last_used), acc.created_at) < now() - make_interval(days => @days)) AS inactive
              FROM acc)
        """;

    /// <summary>Active accounts of people who left or are disabled in the directory: account id, app id, app, user, role, reason.</summary>
    public const string OrphanedSql = """
        WITH acc AS (
            SELECT w.id, w.app_id, app.name AS app_name, COALESCE(w.display_name, w.user_name) AS who, w.role,
                   CASE WHEN EXISTS (SELECT 1 FROM zentriq.lifecycle_people p WHERE p.status = 'left' AND p.email IS NOT NULL
                                      AND lower(p.email) IN (lower(w.user_name), lower(COALESCE(w.email, ''))))
                        THEN 'The person has left the organisation (Asset Lifecycle › People).'
                        WHEN EXISTS (SELECT 1 FROM zentriq.sam_cloud_users c WHERE c.account_enabled = false
                                      AND (lower(c.upn) = lower(w.user_name) OR (c.mail IS NOT NULL AND lower(c.mail) IN (lower(w.user_name), lower(COALESCE(w.email, ''))))))
                        THEN 'The user is disabled in Microsoft Entra ID but the account is still active.' END AS reason
              FROM zentriq.webapp_accounts w JOIN zentriq.webapps app ON app.id = w.app_id
             WHERE w.status = 'active')
        SELECT id, app_id, app_name, who, role, reason FROM acc WHERE reason IS NOT NULL
        """;

    /// <summary>Per app: active accounts inactive for @days, and what they cost.</summary>
    public const string InactiveByAppSql = $$"""
        WITH {{AccountsCte}}
        SELECT app_id, min(app_name), count(*) FILTER (WHERE inactive), sum(COALESCE(cost, (SELECT cost_per_user FROM zentriq.webapps WHERE id = app_id))) FILTER (WHERE inactive)
          FROM accx GROUP BY app_id
        """;
}
