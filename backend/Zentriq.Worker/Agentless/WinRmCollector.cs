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
        var (domain, user) = cred.SplitUser();
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

        using var session = CimSession.Create(target, options);
        var wmi = new CimWmiSource(session);
        return WindowsCollector.Collect(wmi, new CimRegistrySource(session), "winrm", version, includeServices: true);
    }

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
