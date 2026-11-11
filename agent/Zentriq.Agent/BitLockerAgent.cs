using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using Zentriq.Inventory;

namespace Zentriq.Agent;

/// <summary>
/// BitLocker through the Win32_EncryptableVolume WMI provider: the status of each volume, its recovery passwords (sent to
/// the server, which keeps them encrypted), turning encryption on for the system drive (TPM plus a recovery password)
/// and for fixed data drives (recovery password, unlocked automatically), and replacing a recovery password.
/// </summary>
internal static class BitLockerAgent
{
    private const string Ns = @"root\cimv2\Security\MicrosoftVolumeEncryption";

    private static readonly Dictionary<string, int> MethodCodes = new(StringComparer.OrdinalIgnoreCase) { ["Aes128"] = 3, ["Aes256"] = 4, ["XtsAes128"] = 6, ["XtsAes256"] = 7 };
    private static string MethodName(int code) => code switch { 1 => "AES-128 (diffuser)", 2 => "AES-256 (diffuser)", 3 => "AES-CBC 128", 4 => "AES-CBC 256", 5 => "Hardware", 6 => "XTS-AES 128", 7 => "XTS-AES 256", _ => "" };

    public static string Tpm()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\cimv2\Security\MicrosoftTpm", "SELECT * FROM Win32_Tpm");
            foreach (ManagementObject o in s.Get())
                using (o)
                    return o["IsEnabled_InitialValue"] is bool e && e && o["IsActivated_InitialValue"] is bool a && a ? "ready" : "not_ready";
            return "absent";
        }
        catch (ManagementException) { return "absent"; }
    }

    private static uint Call(ManagementObject v, string method, Dictionary<string, object?>? args, out ManagementBaseObject? result)
    {
        var inParams = v.GetMethodParameters(method);
        if (args is not null) foreach (var kv in args) inParams[kv.Key] = kv.Value;
        result = v.InvokeMethod(method, inParams, null);
        return result?["ReturnValue"] is uint rv ? rv : 0;
    }

    private static string Hr(uint code) => "0x" + code.ToString("X8");

    /// <summary>Applies the policy (enforce: encrypt, replace recovery passwords) and returns the volumes and recovery passwords.</summary>
    public static (List<BitLockerVolume> Volumes, List<RecoveryKey> Keys, string Tpm, List<string> Log) Apply(BitLockerPolicy policy)
    {
        var volumes = new List<BitLockerVolume>();
        var keys = new List<RecoveryKey>();
        var log = new List<string>();
        var tpm = Tpm();
        List<ManagementObject> all;
        try
        {
            using var s = new ManagementObjectSearcher(Ns, "SELECT * FROM Win32_EncryptableVolume");
            all = s.Get().Cast<ManagementObject>().ToList();
        }
        catch (ManagementException ex) { throw new InvalidOperationException("BitLocker is not available on this device (" + ex.Message.Trim() + ").", ex); }

        var osProtected = false;
        foreach (var v in all.OrderBy(v => v["VolumeType"] is uint t ? t : 9))
        {
            using (v)
            {
                var letter = v["DriveLetter"] as string;
                var type = v["VolumeType"] is uint vt ? vt : 9u;
                if (string.IsNullOrEmpty(letter) || type > 1) continue;           // OS and fixed data volumes with a drive letter
                var r = new BitLockerVolume { Volume = letter!, Kind = type == 0 ? "os" : "fixed" };
                try
                {
                    if (policy.Mode == "enforce" && (type == 0 || policy.FixedDrives)) Enforce(v, policy, type == 0, tpm, osProtected, r, log);
                    if (policy.Mode != "off") Rotate(v, policy, r, log);
                    Read(v, r, keys);
                    if (type == 0) osProtected = r.Protection == "on" || r.Status == "encrypting";
                }
                catch (Exception ex) { r.Error = ex.Message; }
                volumes.Add(r);
            }
        }
        return (volumes, keys, tpm, log);
    }

    private static void Read(ManagementObject v, BitLockerVolume r, List<RecoveryKey> keys)
    {
        r.Protection = v["ProtectionStatus"] is uint ps ? ps switch { 1 => "on", 0 => "off", _ => "unknown" } : "unknown";
        if (Call(v, "GetConversionStatus", null, out var cs) == 0 && cs is not null)
        {
            var c = cs["ConversionStatus"] is uint x ? x : 99;
            r.Status = c switch { 0 => "decrypted", 1 => "encrypted", 2 => "encrypting", 3 => "decrypting", 4 => "paused", 5 => "paused", _ => "unknown" };
            if (cs["EncryptionPercentage"] is uint pct) r.Percent = pct;
        }
        if (Call(v, "GetEncryptionMethod", null, out var em) == 0 && em?["EncryptionMethod"] is uint m) r.Method = MethodName((int)m);
        if (Call(v, "GetKeyProtectors", new Dictionary<string, object?> { ["KeyProtectorType"] = 0u }, out var kp) == 0 && kp?["VolumeKeyProtectorID"] is string[] ids)
        {
            foreach (var id in ids)
            {
                var kind = Call(v, "GetKeyProtectorType", new Dictionary<string, object?> { ["VolumeKeyProtectorID"] = id }, out var kt) == 0 && kt?["KeyProtectorType"] is uint k ? k : 0u;
                r.Protectors.Add(kind switch { 1 => "TPM", 2 => "Startup key", 3 => "Recovery password", 4 => "TPM and PIN", 5 => "TPM and startup key", 6 => "TPM, PIN and startup key", 7 => "Public key", 8 => "Password", 9 => "TPM certificate", 10 => "Active Directory account", _ => "Other" });
                if (kind != 3) continue;
                var clean = id.Trim('{', '}').ToUpperInvariant();
                r.RecoveryIds.Add(clean);
                if (Call(v, "GetKeyProtectorNumericalPassword", new Dictionary<string, object?> { ["VolumeKeyProtectorID"] = id }, out var np) == 0 && np?["NumericalPassword"] is string pw)
                    keys.Add(new RecoveryKey { Volume = r.Volume, ProtectorId = clean, Password = pw });
            }
        }
    }

    private static string? AddRecoveryPassword(ManagementObject v)
    {
        var rc = Call(v, "ProtectKeyWithNumericalPassword", new Dictionary<string, object?> { ["FriendlyName"] = "Zentriq recovery password" }, out var res);
        if (rc != 0) throw new InvalidOperationException("Adding a recovery password failed (" + Hr(rc) + ").");
        return res?["VolumeKeyProtectorID"] as string;
    }

    private static bool HasProtector(ManagementObject v, uint type) =>
        Call(v, "GetKeyProtectors", new Dictionary<string, object?> { ["KeyProtectorType"] = type }, out var kp) == 0 && kp?["VolumeKeyProtectorID"] is string[] ids && ids.Length > 0;

    private static void Enforce(ManagementObject v, BitLockerPolicy policy, bool os, string tpm, bool osProtected, BitLockerVolume r, List<string> log)
    {
        var conv = Call(v, "GetConversionStatus", null, out var cs) == 0 && cs?["ConversionStatus"] is uint c ? c : 99u;
        var protection = v["ProtectionStatus"] is uint ps ? ps : 2u;
        if (conv == 1 || conv == 2)
        {
            // A protected volume keeps its encryption; it only needs a recovery password Zentriq can keep.
            if (!HasProtector(v, 3)) { AddRecoveryPassword(v); log.Add($"{r.Volume}: recovery password added"); }
            if (protection == 0 && conv == 1 && Call(v, "EnableKeyProtectors", null, out _) == 0) log.Add($"{r.Volume}: protection resumed");
            return;
        }
        if (conv != 0) return;   // decrypting or paused: leave it to the administrator
        if (os)
        {
            if (tpm != "ready") throw new InvalidOperationException(tpm == "absent" ? "No TPM: the system drive is encrypted only with a TPM." : "The TPM is not ready (turn it on and take ownership in the firmware settings).");
            if (!HasProtector(v, 1))
            {
                var rc = Call(v, "ProtectKeyWithTPM", new Dictionary<string, object?> { ["FriendlyName"] = "Zentriq TPM" }, out _);
                if (rc != 0) throw new InvalidOperationException("Adding the TPM protector failed (" + Hr(rc) + ").");
            }
        }
        else
        {
            if (!osProtected) throw new InvalidOperationException("Encrypted only after the system drive (it is unlocked automatically with it).");
            if (!HasProtector(v, 2))
            {
                var rc = Call(v, "ProtectKeyWithExternalKey", new Dictionary<string, object?> { ["FriendlyName"] = "Zentriq automatic unlock" }, out var ext);
                if (rc != 0) throw new InvalidOperationException("Adding the automatic-unlock key failed (" + Hr(rc) + ").");
                if (ext?["VolumeKeyProtectorID"] is string id) Call(v, "EnableAutoUnlock", new Dictionary<string, object?> { ["VolumeKeyProtectorID"] = id }, out _);
            }
        }
        // Protectors only once the drive can be encrypted (a system drive without a TPM is left untouched).
        if (!HasProtector(v, 3)) { AddRecoveryPassword(v); log.Add($"{r.Volume}: recovery password added"); }
        var method = MethodCodes.TryGetValue(policy.Method, out var mc) ? mc : 7;
        var enc = Call(v, "Encrypt", new Dictionary<string, object?> { ["EncryptionMethod"] = (uint)method, ["EncryptionFlags"] = policy.UsedSpaceOnly ? 1u : 0u }, out _);
        if (enc != 0) throw new InvalidOperationException("Starting the encryption failed (" + Hr(enc) + ").");
        log.Add($"{r.Volume}: encryption started ({MethodName(method)}{(policy.UsedSpaceOnly ? ", used space only" : "")})");
    }

    /// <summary>Replaces recovery passwords that were shown in the console (or that IT asked to replace).</summary>
    private static void Rotate(ManagementObject v, BitLockerPolicy policy, BitLockerVolume r, List<string> log)
    {
        if (policy.Rotate.Count == 0) return;
        if (Call(v, "GetKeyProtectors", new Dictionary<string, object?> { ["KeyProtectorType"] = 3u }, out var kp) != 0 || kp?["VolumeKeyProtectorID"] is not string[] ids) return;
        foreach (var id in ids)
        {
            var clean = id.Trim('{', '}').ToUpperInvariant();
            if (!policy.Rotate.Any(x => string.Equals(x.Trim('{', '}'), clean, StringComparison.OrdinalIgnoreCase))) continue;
            AddRecoveryPassword(v);
            var rc = Call(v, "DeleteKeyProtector", new Dictionary<string, object?> { ["VolumeKeyProtectorID"] = id }, out _);
            if (rc != 0) throw new InvalidOperationException("Removing the old recovery password failed (" + Hr(rc) + ").");
            log.Add($"{r.Volume}: recovery password {clean} replaced");
        }
    }
}
