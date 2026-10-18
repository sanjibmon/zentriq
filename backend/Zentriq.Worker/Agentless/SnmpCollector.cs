using System.Net;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using Zentriq.Inventory;

namespace Zentriq.Worker.Agentless;

/// <summary>
/// Agentless inventory of network devices, printers and UPS units over SNMP v1, v2c or v3 (UDP 161).
/// Read-only GET and bounded GETBULK / GETNEXT walks of standard MIBs (SNMPv2-MIB, IF-MIB, IP-MIB, ENTITY-MIB, BRIDGE-MIB,
/// Q-BRIDGE-MIB, LLDP-MIB, POWER-ETHERNET-MIB, Printer-MIB, UPS-MIB, HOST-RESOURCES-MIB) and Cisco CDP/VTP MIBs.
/// SNMPv3 with authPriv (SHA-2 + AES) is recommended; v1/v2c send the community in clear text and should be limited to
/// management networks. A v2c community that gets no answer is retried with v1 (older printers and switches).
/// </summary>
public static class SnmpCollector
{
    private const int DefaultRowsPerColumn = 600;

    private static IEnumerable<(string Column, int Max)> Columns() =>
        SnmpMapper.WalkColumns.Append(SnmpMapper.UpsIdentModel).Select(c => (c, DefaultRowsPerColumn))
            .Concat(NetworkSnmp.WalkColumns.Select(kv => (kv.Key, kv.Value)))
            .GroupBy(c => c.Item1).Select(g => (g.Key, g.Max(x => x.Item2)));

    public static InventoryDocument? Collect(IPAddress ip, ScanCredential cred, TimeSpan timeout, string version)
    {
        var endpoint = new IPEndPoint(ip, cred.Int("port", 161));
        var ms = (int)timeout.TotalMilliseconds;
        var values = new Dictionary<string, string>();
        var vars = SnmpMapper.SystemOids.Concat(NetworkSnmp.GetOids).Distinct().Select(o => new Variable(new ObjectIdentifier(o))).ToList();
        string used;

        if (cred.Kind == "snmp_v2c")
        {
            var community = new OctetString(cred.Secret.Community ?? "public");
            var code = VersionCode.V2;
            IList<Variable>? got = null;
            try { got = Messenger.Get(code, endpoint, community, vars, ms); }
            catch (Lextm.SharpSnmpLib.Messaging.TimeoutException) { }
            catch (ErrorException) { }
            if (got is null || got.All(x => x.Data.TypeCode is SnmpType.NoSuchObject or SnmpType.NoSuchInstance))
            {
                // SNMPv1 agents: one unknown OID fails the whole request, so ask for the system group only.
                code = VersionCode.V1;
                try { got = Messenger.Get(code, endpoint, community, SnmpMapper.SystemOids.Take(6).Select(o => new Variable(new ObjectIdentifier(o))).ToList(), ms); }
                catch (Exception ex) when (ex is Lextm.SharpSnmpLib.Messaging.TimeoutException or ErrorException) { return null; }
            }
            Add(values, got);
            if (values.Count == 0) return null;
            used = code == VersionCode.V1 ? "v1" : "v2c";
            foreach (var (col, max) in Columns())
            {
                var list = new List<Variable>();
                try
                {
                    if (code == VersionCode.V1) Messenger.Walk(code, endpoint, community, new ObjectIdentifier(col), list, ms, WalkMode.WithinSubtree);
                    else Messenger.BulkWalk(code, endpoint, community, OctetString.Empty, new ObjectIdentifier(col), list, ms, 25, WalkMode.WithinSubtree, null, null);
                }
                catch (Exception ex) when (ex is Lextm.SharpSnmpLib.Messaging.TimeoutException or ErrorException) { }
                Add(values, list.Take(max));
            }
        }
        else
        {
            var user = new OctetString(cred.Username ?? "");
            var priv = Privacy(cred);
            var discovery = Messenger.GetNextDiscovery(SnmpType.GetRequestPdu);
            ReportMessage report;
            try { report = discovery.GetResponse(ms, endpoint); }
            catch (Lextm.SharpSnmpLib.Messaging.TimeoutException) { return null; }
            var request = new GetRequestMessage(VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId, user, OctetString.Empty, vars, priv, Messenger.MaxMessageSize, report);
            var reply = request.GetResponse(ms, endpoint);
            if (reply.Pdu().ErrorStatus.ToInt32() != 0) throw new InvalidOperationException($"SNMP error status {reply.Pdu().ErrorStatus.ToInt32()}.");
            Add(values, reply.Pdu().Variables);
            used = "v3";
            foreach (var (col, max) in Columns())
            {
                var list = new List<Variable>();
                try { Messenger.BulkWalk(VersionCode.V3, endpoint, user, OctetString.Empty, new ObjectIdentifier(col), list, ms, 25, WalkMode.WithinSubtree, priv, report); }
                catch (Exception ex) when (ex is Lextm.SharpSnmpLib.Messaging.TimeoutException or ErrorException) { }
                Add(values, list.Take(max));
            }
        }
        if (!values.ContainsKey(SnmpMapper.SysDescr) && !values.ContainsKey(SnmpMapper.SysName)) return null;
        var doc = SnmpMapper.Map(values, ip.ToString(), version);
        if (doc.NetworkDevice is not null) doc.NetworkDevice.SnmpVersion = used;
        return doc;
    }

    private static IPrivacyProvider Privacy(ScanCredential cred)
    {
        var authPass = new OctetString(cred.Secret.AuthPassword ?? "");
        IAuthenticationProvider auth = (cred.Str("authProtocol") ?? "sha1").ToLowerInvariant() switch
        {
            "md5" => new MD5AuthenticationProvider(authPass),
            "sha256" => new SHA256AuthenticationProvider(authPass),
            _ => new SHA1AuthenticationProvider(authPass),
        };
        var privPass = new OctetString(cred.Secret.PrivPassword ?? "");
        return (cred.Str("privProtocol") ?? "aes").ToLowerInvariant() switch
        {
            "none" => new DefaultPrivacyProvider(auth),
            "aes256" => new AES256PrivacyProvider(privPass, auth),
            _ => new AESPrivacyProvider(privPass, auth),
        };
    }

    /// <summary>Values as text; binary octet strings (MAC addresses, chassis ids, packed IPs) as hex.</summary>
    public static void Add(Dictionary<string, string> values, IEnumerable<Variable> vars)
    {
        foreach (var v in vars)
        {
            if (v.Data.TypeCode is SnmpType.NoSuchObject or SnmpType.NoSuchInstance or SnmpType.EndOfMibView) continue;
            var id = v.Id.ToString();
            values[id] = v.Data switch
            {
                OctetString os when NetworkSnmp.HexColumns.Any(h => id.StartsWith(h, StringComparison.Ordinal)) || !Printable(os.GetRaw()) => os.ToHexString(),
                TimeTicks tt => tt.ToUInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => v.Data.ToString() ?? "",
            };
        }
    }

    private static bool Printable(byte[] raw)
    {
        if (raw.Length == 0) return true;
        var n = raw.Length > 0 && raw[^1] == 0 ? raw.Length - 1 : raw.Length; // a trailing NUL is common
        for (var i = 0; i < n; i++)
        {
            var b = raw[i];
            if (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D) return false;
        }
        return true;
    }

    public static bool IsAuthFailure(Exception ex) =>
        ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("unknown user", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("decryption", StringComparison.OrdinalIgnoreCase);
}
