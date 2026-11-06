using System.Text.Json;
using Npgsql;

namespace Zentriq.Infrastructure.Deployment;

/// <summary>Devices chosen for a job: devices, device groups, departments or all devices with the Zentriq Agent.</summary>
public sealed record TargetSpec(Guid[]? AssetIds, Guid[]? GroupIds, Guid[]? DepartmentIds, bool? All)
{
    public bool IsEmpty => All != true && (AssetIds?.Length ?? 0) == 0 && (GroupIds?.Length ?? 0) == 0 && (DepartmentIds?.Length ?? 0) == 0;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    public string ToJson() => JsonSerializer.Serialize(new { assetIds = AssetIds ?? [], groupIds = GroupIds ?? [], departmentIds = DepartmentIds ?? [], all = All == true }, Web);
    public static TargetSpec FromJson(string? json)
    {
        try { return JsonSerializer.Deserialize<TargetSpec>(json ?? "{}", Web) ?? new TargetSpec(null, null, null, null); }
        catch (JsonException) { return new TargetSpec(null, null, null, null); }
    }
}

/// <summary>A device of a job and its agent (the newest active registration), its platform family and whether the agent can do the job.</summary>
public sealed record DeviceTarget(Guid AssetId, string Name, Guid? AgentId, string Family, bool Capable);

public static class DeviceTargets
{
    /// <summary>The active devices of <paramref name="spec"/> (at most <paramref name="limit"/> + 1, so callers can refuse too many).</summary>
    public static async Task<List<DeviceTarget>> ResolveAsync(NpgsqlDataSource ds, TargetSpec spec, string capability, CancellationToken ct, int limit = 10000)
    {
        await using var cmd = ds.CreateCommand($"""
            SELECT a.id, COALESCE(a.hostname, host(a.primary_ip), a.id::text), r.id, COALESCE(r.platform, a.os_family), COALESCE(@cap = ANY(r.capabilities), false)
              FROM zentriq.assets a
              LEFT JOIN LATERAL (SELECT x.id, x.platform, x.capabilities FROM zentriq.agent_registrations x WHERE x.asset_id = a.id AND x.status = 'active'
                                  ORDER BY x.last_checkin_at DESC NULLS LAST LIMIT 1) r ON true
             WHERE a.status = 'active' AND (
                   @all OR a.id = ANY(@as) OR a.department_id = ANY(@dep)
                   OR EXISTS (SELECT 1 FROM zentriq.device_groups g WHERE g.id = ANY(@grp) AND {AppControlStore.MemberSql}))
               AND (NOT @all OR r.id IS NOT NULL)
             ORDER BY 2
             LIMIT @lim
            """);
        cmd.Parameters.AddWithValue("cap", capability);
        cmd.Parameters.AddWithValue("all", spec.All == true);
        cmd.Parameters.AddWithValue("as", spec.AssetIds ?? []);
        cmd.Parameters.AddWithValue("dep", spec.DepartmentIds ?? []);
        cmd.Parameters.AddWithValue("grp", spec.GroupIds ?? []);
        cmd.Parameters.AddWithValue("lim", limit + 1);
        var list = new List<DeviceTarget>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new DeviceTarget(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2), AppControlStore.Family(r.IsDBNull(3) ? null : r.GetString(3)), r.GetBoolean(4)));
        return list;
    }

    /// <summary>Why a device cannot run the job (null: it can).</summary>
    public static string? WhyNot(DeviceTarget t, string? platform, string what) =>
        t.AgentId is null ? "No Zentriq Agent on this device."
        : platform is not null && t.Family != platform ? $"This is for {(platform == "linux" ? "Linux" : "Windows")}; the device runs {(t.Family == "linux" ? "Linux" : "Windows")}."
        : !t.Capable ? $"The agent on this device is too old for {what}; update the agent first." : null;
}
