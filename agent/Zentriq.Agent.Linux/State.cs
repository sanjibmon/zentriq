using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zentriq.Inventory;

namespace Zentriq.AgentLinux;

/// <summary>Persistent agent state in /var/lib/zentriq-agent/state.json (root only). Same fields as the Windows agent.</summary>
internal sealed class AgentState
{
    [JsonPropertyName("enrollmentCode")] public string? EnrollmentCode { get; set; }
    [JsonPropertyName("enrollmentServer")] public string? EnrollmentServer { get; set; }
    [JsonPropertyName("serverUrl")] public string? ServerUrl { get; set; }
    [JsonPropertyName("serverUrls")] public List<string> ServerUrls { get; set; } = [];
    [JsonPropertyName("agentId")] public string? AgentId { get; set; }
    [JsonPropertyName("certThumbprint")] public string? CertThumbprint { get; set; }
    [JsonPropertyName("serverPins")] public List<string> ServerPins { get; set; } = [];
    [JsonPropertyName("proxyUrl")] public string? ProxyUrl { get; set; }
    [JsonPropertyName("checkinMinutes")] public int CheckinMinutes { get; set; } = 30;
    [JsonPropertyName("inventoryMinutes")] public int InventoryMinutes { get; set; } = 180;
    [JsonPropertyName("installCheckinMinutes")] public int? InstallCheckinMinutes { get; set; }
    [JsonPropertyName("installInventoryMinutes")] public int? InstallInventoryMinutes { get; set; }
    [JsonPropertyName("offlineSinceUtc")] public DateTime? OfflineSinceUtc { get; set; }
    [JsonPropertyName("lastInventoryUtc")] public DateTime? LastInventoryUtc { get; set; }
    [JsonPropertyName("lastCheckinUtc")] public DateTime? LastCheckinUtc { get; set; }
    /// <summary>pending | enrolled | revoked</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "pending";
    [JsonPropertyName("lastError")] public string? LastError { get; set; }

    [JsonIgnore] public bool Enrolled => Status == "enrolled" && !string.IsNullOrEmpty(AgentId) && !string.IsNullOrEmpty(CertThumbprint);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static AgentState Load()
    {
        lock (Gate)
        {
            try
            {
                var s = File.Exists(Paths.StateFile) ? JsonSerializer.Deserialize<AgentState>(File.ReadAllText(Paths.StateFile)) ?? new() : new();
                s.CheckinMinutes = Math.Clamp(s.CheckinMinutes, 2, 1440);
                s.InventoryMinutes = Math.Clamp(s.InventoryMinutes, 30, 10080);
                return s;
            }
            catch (JsonException) { return new AgentState(); }
        }
    }

    public void Save()
    {
        lock (Gate)
        {
            Paths.EnsureData();
            Paths.WriteSecret(Paths.StateFile, JsonSerializer.SerializeToUtf8Bytes(this, Json));
        }
    }
}

/// <summary>
/// Agent identity: an RSA-2048 key made on the device and the client certificate the server's agent CA issued for it,
/// kept together in /var/lib/zentriq-agent/agent.pfx (root only). The key never leaves the device.
/// </summary>
internal sealed class PendingKey : IDisposable
{
    public RSA Rsa { get; } = RSA.Create(2048);

    public string CreateCsr(string hostname)
    {
        var req = new CertificateRequest("CN=" + Sanitize(hostname) + ", O=Zentriq Agent", Rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(req.CreateSigningRequest());
    }

    public X509Certificate2 Install(byte[] certificateDer)
    {
        using var issued = X509CertificateLoader.LoadCertificate(certificateDer);
        using var withKey = issued.CopyWithPrivateKey(Rsa);
        var pfx = withKey.Export(X509ContentType.Pkcs12);
        try
        {
            Paths.WriteSecret(Paths.Certificate, pfx);
            return X509CertificateLoader.LoadPkcs12(pfx, null);
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    private static string Sanitize(string s)
    {
        var chars = s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '.').ToArray();
        return chars.Length == 0 ? "agent" : new string(chars);
    }

    public void Dispose() => Rsa.Dispose();
}

internal static class CertificateStore
{
    public static X509Certificate2? Find(string? thumbprint)
    {
        if (string.IsNullOrEmpty(thumbprint) || !File.Exists(Paths.Certificate)) return null;
        try
        {
            var c = X509CertificateLoader.LoadPkcs12FromFile(Paths.Certificate, null);
            if (c.HasPrivateKey && string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase)) return c;
            c.Dispose();
        }
        catch (CryptographicException ex) { Log.Warn("The agent certificate file could not be read: " + ex.Message); }
        return null;
    }

    public static void Remove()
    {
        try { if (File.Exists(Paths.Certificate)) File.Delete(Paths.Certificate); } catch (IOException) { }
    }
}

/// <summary>
/// Uninstall and tamper protection (from the server): the device's uninstall key and the current code for all agents
/// (hash and expiry), in /var/lib/zentriq-agent/protection.json (root only). While it is on, removing the package needs
/// a one-hour code from the console, and "systemctl stop" is refused (systemd RefuseManualStop); "zentriq-agent --stop
/// &lt;code&gt;" stops the agent. Root can still defeat anything on its own machine; every attempt seen is reported.
/// </summary>
internal sealed class Protection
{
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("allCodeHash")] public string? AllCodeHash { get; set; }
    [JsonPropertyName("allCodeExpires")] public DateTime? AllCodeExpires { get; set; }
    [JsonPropertyName("pausedUntil")] public DateTime? PausedUntil { get; set; }
    [JsonPropertyName("stoppedByCode")] public DateTime? StoppedByCode { get; set; }
    [JsonPropertyName("failures")] public List<DateTime> Failures { get; set; } = [];

    [JsonIgnore] public byte[]? KeyBytes => Key is null ? null : Convert.FromBase64String(Key);
    [JsonIgnore] public bool Enabled => KeyBytes is { Length: 32 };
    [JsonIgnore] public bool Paused => PausedUntil is { } p && p > DateTime.UtcNow;
    /// <summary>Protection wanted now: on, not paused and not stopped with a code.</summary>
    [JsonIgnore] public bool Active => Enabled && !Paused;

    public static Protection Load()
    {
        try { return File.Exists(Paths.Protection) ? JsonSerializer.Deserialize<Protection>(File.ReadAllText(Paths.Protection)) ?? new() : new(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new(); }
    }

    public void Save() => Paths.WriteSecret(Paths.Protection, JsonSerializer.SerializeToUtf8Bytes(this));

    public string? CodeForAllHash() => !Enabled ? "" : AllCodeExpires is { } e && e > DateTime.UtcNow ? AllCodeHash ?? "" : "";

    /// <summary>Checks a code: the device's own code (from its key) or the code for all agents. Five wrong codes in 15 minutes lock it.</summary>
    public bool Verify(string? code, out string error)
    {
        Failures.RemoveAll(f => f < DateTime.UtcNow.AddMinutes(-15));
        if (Failures.Count >= 5) { error = "Too many wrong codes; try again in 15 minutes."; return false; }
        var key = KeyBytes;
        if (key is null) { error = ""; return true; }
        if (AgentCodes.VerifyUninstallCode(key, code, DateTime.UtcNow, out var e1)) { error = ""; return true; }
        if (AgentCodes.VerifyUninstallCodeForAll(AllCodeHash, AllCodeExpires, code, DateTime.UtcNow, out var e2)) { error = ""; return true; }
        Failures.Add(DateTime.UtcNow);
        Save();
        error = AgentCodes.Normalize(code, AgentCodes.UninstallLength) is null ? e1 : e2.Length > 0 ? e2 : e1;
        Events.Record("tamper", "A wrong code was entered on the device (zentriq-agent).");
        return false;
    }

    /// <summary>Stores or removes the key from the server; applies the systemd stop protection.</summary>
    public static void Apply(bool on, string? keyBase64, UninstallCodeInfo? code)
    {
        var p = Load();
        var changed = false;
        if (!on)
        {
            if (p.Key is not null) { p.Key = null; p.AllCodeHash = null; p.AllCodeExpires = null; p.PausedUntil = null; changed = true; Log.Info("Uninstall and tamper protection turned off by the server."); }
        }
        else
        {
            if (!string.IsNullOrEmpty(keyBase64) && keyBase64 != p.Key)
            {
                if (p.Key is null) Log.Info("Uninstall and tamper protection is on: removing or stopping the agent needs a one-hour code from the Zentriq console.");
                p.Key = keyBase64; changed = true;
            }
            var hash = code?.Hash?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(hash) || hash.Length != 64)
            {
                if (p.AllCodeHash is not null) { p.AllCodeHash = null; p.AllCodeExpires = null; changed = true; }
            }
            else if (p.AllCodeHash != hash || p.AllCodeExpires != code!.ExpiresAt.UtcDateTime)
            {
                p.AllCodeHash = hash; p.AllCodeExpires = code!.ExpiresAt.UtcDateTime; changed = true;
                Log.Info($"New uninstall code from the console received (valid until {code.ExpiresAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC).");
            }
        }
        if (changed) p.Save();
        StopGuard.Sync(p);
    }

    public static object? Reported()
    {
        if (!Shell.IsRoot) return null;
        var p = Load();
        if (!p.Enabled) return new Dictionary<string, object?> { ["state"] = "off" };
        if (p.Paused) return new Dictionary<string, object?> { ["state"] = "paused", ["until"] = p.PausedUntil!.Value.ToString("o") };
        return new Dictionary<string, object?> { ["state"] = StopGuard.Applied ? "on" : "pending" };
    }
}

/// <summary>Refuses "systemctl stop / restart zentriq-agent" while protection is active (systemd drop-in, RefuseManualStop).</summary>
internal static class StopGuard
{
    public static bool Applied => File.Exists(Paths.StopDropIn);

    public static void Sync(Protection p) => Set(p.Active);

    public static void Set(bool on)
    {
        if (!Shell.Systemd || !Shell.IsRoot) return;
        try
        {
            if (on == Applied) return;
            if (on)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Paths.StopDropIn)!);
                File.WriteAllText(Paths.StopDropIn, "# Zentriq tamper protection: stop the agent with 'zentriq-agent --stop <code>' (code from the Zentriq console).\n[Unit]\nRefuseManualStop=yes\n");
            }
            else File.Delete(Paths.StopDropIn);
            Shell.Run("systemctl", ["daemon-reload"], TimeSpan.FromSeconds(60));
            Log.Info(on ? "Tamper protection on: 'systemctl stop zentriq-agent' is refused." : "Tamper protection off for the agent service.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("Could not change the stop protection: " + ex.Message); }
    }
}

/// <summary>Protection events (wrong codes, stops with a code) waiting to be reported at the next check-in.</summary>
internal static class Events
{
    private static readonly object Gate = new();

    public static void Record(string kind, string text)
    {
        if (kind == "tamper") Log.Warn("Tamper protection: " + text); else Log.Info("Tamper protection: " + text);
        lock (Gate)
            try
            {
                var list = Load();
                list.Add(new Dictionary<string, object?> { ["kind"] = kind, ["at"] = DateTime.UtcNow.ToString("o"), ["text"] = text });
                Paths.WriteSecret(Paths.Events, JsonSerializer.SerializeToUtf8Bytes(list.Skip(Math.Max(0, list.Count - 20)).ToList()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (kind == "tamper") AgentService.RequestServerWake();
    }

    public static List<Dictionary<string, object?>> Load()
    {
        try
        {
            if (!File.Exists(Paths.Events)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(Paths.Events));
            return doc.RootElement.EnumerateArray().Select(e => new Dictionary<string, object?>
            {
                ["kind"] = e.GetProperty("kind").GetString(), ["at"] = e.GetProperty("at").GetString(), ["text"] = e.GetProperty("text").GetString(),
            }).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException) { return []; }
    }

    public static void Clear(int count)
    {
        lock (Gate)
            try
            {
                var rest = Load().Skip(count).ToList();
                if (rest.Count == 0) File.Delete(Paths.Events); else Paths.WriteSecret(Paths.Events, JsonSerializer.SerializeToUtf8Bytes(rest));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
