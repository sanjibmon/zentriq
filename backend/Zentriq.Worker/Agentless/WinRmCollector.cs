using System.Security;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Zentriq.Inventory;

namespace Zentriq.Worker.Agentless;

/// <summary>
/// Agentless Windows inventory over WinRM (WS-Management, ports 5985/5986) using the Windows CIM client,
/// with DCOM (135 + dynamic RPC ports) as a fallback. Read-only queries only; registry data is read with
/// the StdRegProv WMI provider (no Remote Registry service needed).
/// </summary>
public static class WinRmCollector
{
    public static Zentriq.Inventory.InventoryDocument Collect(string target, ScanCredential cred, bool useDcom, bool https, int port, TimeSpan timeout, string version)
    {
        using var session = CreateSession(target, cred, useDcom, https, port, timeout);
        var wmi = new CimWmiSource(session);
        return WindowsCollector.Collect(wmi, new CimRegistrySource(session), "winrm", version, includeServices: true);
    }

    public static CimSession CreateSession(string target, ScanCredential cred, bool useDcom, bool https, int port, TimeSpan timeout)
    {
        var (domain, user) = cred.SplitUser();
        if (domain == ".") domain = null; // .\user = local account on the target
        using var secure = new SecureString();
        foreach (var c in cred.Secret.Password ?? "") secure.AppendChar(c);
        secure.MakeReadOnly();
        var credential = new CimCredential(PasswordAuthenticationMechanism.Default, domain ?? "", user, secure);

        CimSessionOptions options;
        if (useDcom)
        {
            var d = new DComSessionOptions { Timeout = timeout };
            d.AddDestinationCredentials(credential);
            options = d;
        }
        else
        {
            var w = new WSManSessionOptions { Timeout = timeout, DestinationPort = (uint)port, UseSsl = https };
            if (https && cred.Bool("skipCertificateCheck")) { w.CertCACheck = false; w.CertCNCheck = false; w.CertRevocationCheck = false; }
            w.AddDestinationCredentials(credential);
            options = w;
        }
        return CimSession.Create(target, options);
    }

    /// <summary>
    /// Starts, stops or restarts one Windows service with the Win32_Service methods (the same thing services.msc does
    /// remotely). Returns the final state. The service name has been validated against the device's inventory.
    /// </summary>
    public static (bool Ok, string? State, string Message) ControlService(CimSession session, string name, string operation, TimeSpan wait)
    {
        CimInstance? Find()
        {
            foreach (var i in session.QueryInstances(@"root\cimv2", "WQL", $"SELECT Name, State, StartMode FROM Win32_Service WHERE Name = '{name}'")) return i;
            return null;
        }
        string? State(CimInstance? i) => i?.CimInstanceProperties["State"]?.Value as string;
        string? Current() { using var i = Find(); return State(i); }
        string? WaitFor(string desired)
        {
            var until = DateTime.UtcNow + wait;
            string? st;
            do
            {
                using var i = Find();
                st = State(i);
                if (string.Equals(st, desired, StringComparison.OrdinalIgnoreCase)) return st;
                Thread.Sleep(1500);
            } while (DateTime.UtcNow < until);
            return st;
        }
        uint Call(string method)
        {
            using var i = Find() ?? throw new InvalidOperationException($"Service '{name}' does not exist on the device.");
            if (method == "StartService" && i.CimInstanceProperties["StartMode"]?.Value is string mode && mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                return 4; // reported as "disabled" below
            return Convert.ToUInt32(session.InvokeMethod(i, method, null).ReturnValue.Value);
        }

        using (var first = Find())
            if (first is null) return (false, null, $"Service '{name}' does not exist on the device.");

        if (operation is "stop" or "restart")
        {
            var rc = Call("StopService");
            if (rc != 0 && rc != 5) return (false, Current(), $"Stop failed: {Explain(rc)}.");
            var st = WaitFor("Stopped");
            if (!string.Equals(st, "Stopped", StringComparison.OrdinalIgnoreCase)) return (false, st, $"The service did not stop within {wait.TotalSeconds:0} seconds (state: {st}).");
            if (operation == "stop") return (true, st, "Service stopped.");
        }
        var start = Call("StartService");
        if (start == 4) return (false, "Stopped", "The service is disabled; change its startup type first.");
        if (start != 0 && start != 10) return (false, Current(), $"Start failed: {Explain(start)}.");
        var final = WaitFor("Running");
        return string.Equals(final, "Running", StringComparison.OrdinalIgnoreCase)
            ? (true, final, operation == "restart" ? "Service restarted." : "Service started.")
            : (false, final, $"The service did not reach Running within {wait.TotalSeconds:0} seconds (state: {final}).");
    }

    private static string Explain(uint rc) => rc switch
    {
        2 => "access denied (the account is not an administrator on the device)",
        3 => "other services depend on it and are running",
        5 => "the service cannot accept this control now",
        6 => "the service is not running",
        7 => "the service did not respond in time",
        8 => "unknown failure",
        10 => "already running",
        14 => "the service is disabled",
        15 => "the service is marked for deletion",
        _ => $"Win32_Service return code {rc}",
    };

    private sealed class CimWmiSource(CimSession session) : IWmiSource
    {
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string wmiNamespace, string wql)
        {
            var list = new List<IReadOnlyDictionary<string, object?>>();
            foreach (var inst in session.QueryInstances(wmiNamespace, "WQL", wql))
            {
                using (inst)
                {
                    var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in inst.CimInstanceProperties) d[p.Name] = p.Value;
                    list.Add(d);
                }
            }
            return list;
        }
    }

    private sealed class CimRegistrySource(CimSession session) : IRegistrySource
    {
        private const uint Hklm = 0x80000002;

        private CimMethodResult Invoke(string method, string path, string? valueName = null)
        {
            var p = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("hDefKey", Hklm, CimType.UInt32, CimFlags.In),
                CimMethodParameter.Create("sSubKeyName", path, CimType.String, CimFlags.In),
            };
            if (valueName is not null) p.Add(CimMethodParameter.Create("sValueName", valueName, CimType.String, CimFlags.In));
            return session.InvokeMethod(@"root\cimv2", "StdRegProv", method, p);
        }

        public IReadOnlyList<string> SubKeys(string path)
        {
            var r = Invoke("EnumKey", path);
            return Convert.ToUInt32(r.ReturnValue.Value) == 0 && r.OutParameters["sNames"]?.Value is string[] names ? names : [];
        }

        public string? GetString(string path, string name)
        {
            var r = Invoke("GetStringValue", path, name);
            return Convert.ToUInt32(r.ReturnValue.Value) == 0 ? r.OutParameters["sValue"]?.Value as string : null;
        }

        public int? GetDword(string path, string name)
        {
            var r = Invoke("GetDWORDValue", path, name);
            return Convert.ToUInt32(r.ReturnValue.Value) == 0 && r.OutParameters["uValue"]?.Value is { } v ? unchecked((int)Convert.ToUInt32(v)) : null;
        }
    }

    /// <summary>True when the exception means the credentials were rejected (vs. network/protocol problems).</summary>
    public static bool IsAccessDenied(Exception ex) =>
        ex is CimException ce && (ce.NativeErrorCode == NativeErrorCode.AccessDenied ||
                                  ce.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
                                  ce.Message.Contains("logon failure", StringComparison.OrdinalIgnoreCase));
}
