using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zentriq.LicenseServer.Services;

/// <summary>
/// The data folder of an installed license server (C:\ProgramData\Zentriq\LicenseServer): database cluster, signing
/// key, protected secrets, certificates, logs, backups and the setup state. Present only when the server runs with
/// <c>--data &lt;folder&gt;</c> (the Windows service the installer creates); without it the server is configured
/// entirely by settings (development and tests).
/// </summary>
public sealed class ServerData
{
    public const int DefaultHttpPort = 5080;
    public const int DefaultPgPort = 5439;
    public const string DatabaseName = "zentriq_license";
    public const string DatabaseUser = "zls";

    public string Root { get; }
    public string Pg => Path.Combine(Root, "pg");
    public string Keys => Path.Combine(Root, "keys");
    public string Protected => Path.Combine(Root, "protected");
    public string Certs => Path.Combine(Root, "certs");
    public string Logs => Path.Combine(Root, "logs");
    public string Backups => Path.Combine(Root, "backups");
    public string ConfigFile => Path.Combine(Root, "config.json");

    private readonly object _gate = new();
    private SetupState _state;

    public ServerData(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        _state = Load();
    }

    public SetupState State { get { lock (_gate) return _state; } }

    /// <summary>Changes the setup state and writes it (atomically) to config.json.</summary>
    public SetupState Update(Func<SetupState, SetupState> change)
    {
        lock (_gate)
        {
            _state = change(_state);
            var tmp = ConfigFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, Json));
            File.Move(tmp, ConfigFile, overwrite: true);
            return _state;
        }
    }

    private SetupState Load()
    {
        try { return File.Exists(ConfigFile) ? JsonSerializer.Deserialize<SetupState>(File.ReadAllText(ConfigFile), Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // ---- protected secrets (database password, signing key passphrase, certificate passwords) -------------------

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Zentriq License Server");

    /// <summary>
    /// Kept in protected\&lt;name&gt;.bin, encrypted with Windows DPAPI for this computer and readable only through the
    /// folder's permissions (administrators, SYSTEM and the license server service). Elsewhere (development) plain
    /// text with owner-only permissions.
    /// </summary>
    public void SetSecret(string name, string value)
    {
        Directory.CreateDirectory(Protected);
        var bytes = Encoding.UTF8.GetBytes(value);
        var file = Path.Combine(Protected, name + ".bin");
        if (OperatingSystem.IsWindows())
            File.WriteAllBytes(file, ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine));
        else
        {
            if (File.Exists(file)) File.Delete(file);
            using var fs = new FileStream(file, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            fs.Write(bytes);
        }
    }

    public string? GetSecret(string name)
    {
        var file = Path.Combine(Protected, name + ".bin");
        if (!File.Exists(file)) return null;
        var bytes = File.ReadAllBytes(file);
        return Encoding.UTF8.GetString(OperatingSystem.IsWindows() ? ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.LocalMachine) : bytes);
    }

    public string ConnectionString(SetupState s) =>
        $"Host=127.0.0.1;Port={s.PgPort};Database={DatabaseName};Username={DatabaseUser};Password={GetSecret("database") ?? throw new InvalidOperationException("The database password is missing: run the installer again (repair).")}";

    public static string NewSecret(int bytes = 24) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).Replace('+', 'x').Replace('/', 'y').TrimEnd('=');
}

/// <summary>data\config.json: what the setup wizard decided.</summary>
public sealed record SetupState
{
    public int HttpPort { get; init; } = ServerData.DefaultHttpPort;
    public int PgPort { get; init; } = ServerData.DefaultPgPort;
    public string? KeyId { get; init; }
    public string? KeyFile { get; init; }
    public string? KeyFingerprint { get; init; }
    public bool KeyBackedUp { get; init; }
    public AccessSettings Access { get; init; } = new();
    public bool SetupComplete { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? InstalledVersion { get; init; }

    public string PublicUrl => Access.IsPublic ? $"https://{Access.Hostname}" : $"http://localhost:{HttpPort}";
}

/// <summary>Who can reach the server: this computer only (http://localhost:5080), or the internet over HTTPS.</summary>
public sealed record AccessSettings
{
    public const string Local = "local", Public = "public", AutoCertificate = "auto", PfxCertificate = "pfx";
    public string Mode { get; init; } = Local;
    public string? Hostname { get; init; }
    /// <summary>auto: Let's Encrypt certificate obtained and renewed by the server; pfx: certificate file supplied by you.</summary>
    public string? Certificate { get; init; }
    public string? Email { get; init; }
    public DateTimeOffset? PfxExpires { get; init; }
    [JsonIgnore] public bool IsPublic => Mode == Public && !string.IsNullOrWhiteSpace(Hostname);
}
