using System.Net;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using Zentriq.Inventory;

namespace Zentriq.Worker.Agentless;

/// <summary>
/// Agentless inventory of network devices, printers and UPS units over SNMP v2c or v3 (UDP 161).
/// Read-only GET and bounded GETBULK walks of standard MIBs (SNMPv2-MIB, IF-MIB, IP-MIB, ENTITY-MIB,
/// Printer-MIB, UPS-MIB). SNMPv3 with authPriv (SHA-1/SHA-256 + AES) is recommended; v2c sends the
/// community in clear text and should be limited to management networks.
/// </summary>
public static class SnmpCollector
{
    private const int MaxRowsPerColumn = 600;

    public static InventoryDocument? Collect(IPAddress ip, ScanCredential cred, TimeSpan timeout, string version)
    {
        var endpoint = new IPEndPoint(ip, cred.Int("port", 161));
        var ms = (int)timeout.TotalMilliseconds;
        var values = new Dictionary<string, string>();
        var vars = SnmpMapper.SystemOids.Select(o => new Variable(new ObjectIdentifier(o))).ToList();

        if (cred.Kind == "snmp_v2c")
        {
            var community = new OctetString(cred.Secret.Community ?? "public");
            IList<Variable> got;
            try { got = Messenger.Get(VersionCode.V2, endpoint, community, vars, ms); }
            catch (Lextm.SharpSnmpLib.Messaging.TimeoutException) { return null; }
            Add(values, got);
            if (values.Count == 0) return null;
            foreach (var col in SnmpMapper.WalkColumns.Append(SnmpMapper.UpsIdentModel))
            {
                var list = new List<Variable>();
                try { Messenger.BulkWalk(VersionCode.V2, endpoint, community, OctetString.Empty, new ObjectIdentifier(col), list, ms, 25, WalkMode.WithinSubtree, null, null); }
                catch (Exception ex) when (ex is Lextm.SharpSnmpLib.Messaging.TimeoutException or ErrorException) { }
                Add(values, list.Take(MaxRowsPerColumn));
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
            foreach (var col in SnmpMapper.WalkColumns.Append(SnmpMapper.UpsIdentModel))
            {
                var list = new List<Variable>();
                try { Messenger.BulkWalk(VersionCode.V3, endpoint, user, OctetString.Empty, new ObjectIdentifier(col), list, ms, 25, WalkMode.WithinSubtree, priv, report); }
                catch (Exception ex) when (ex is Lextm.SharpSnmpLib.Messaging.TimeoutException or ErrorException) { }
                Add(values, list.Take(MaxRowsPerColumn));
            }
        }
        if (!values.ContainsKey(SnmpMapper.SysDescr) && !values.ContainsKey(SnmpMapper.SysName)) return null;
        return SnmpMapper.Map(values, ip.ToString(), version);
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

    private static void Add(Dictionary<string, string> values, IEnumerable<Variable> vars)
    {
        foreach (var v in vars)
        {
            if (v.Data.TypeCode is SnmpType.NoSuchObject or SnmpType.NoSuchInstance or SnmpType.EndOfMibView) continue;
            values[v.Id.ToString()] = v.Data switch
            {
                OctetString os when v.Id.ToString().StartsWith(SnmpMapper.IfPhysAddress, StringComparison.Ordinal) => os.ToHexString(),
                TimeTicks tt => tt.ToUInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => v.Data.ToString() ?? "",
            };
        }
    }

    public static bool IsAuthFailure(Exception ex) =>
        ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("unknown user", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("decryption", StringComparison.OrdinalIgnoreCase);
}
