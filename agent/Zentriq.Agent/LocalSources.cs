using System;
using System.Collections.Generic;
using System.Management;
using Microsoft.Win32;
using Zentriq.Inventory;

namespace Zentriq.Agent;

/// <summary>Local WMI (read-only queries).</summary>
internal sealed class LocalWmiSource : IWmiSource
{
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string wmiNamespace, string wql)
    {
        var list = new List<IReadOnlyDictionary<string, object?>>();
        var scope = new ManagementScope(@"\\.\" + wmiNamespace);
        scope.Options.Timeout = TimeSpan.FromSeconds(60);
        var opts = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(120), ReturnImmediately = true, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql), opts);
        using var results = searcher.Get();
        foreach (ManagementBaseObject mo in results)
        {
            using (mo)
            {
                var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in mo.Properties) d[p.Name] = p.Value;
                list.Add(d);
            }
        }
        return list;
    }
}

/// <summary>HKLM, 64-bit view (32-bit software is read through WOW6432Node paths).</summary>
internal sealed class LocalRegistrySource : IRegistrySource
{
    private readonly RegistryKey _hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public IReadOnlyList<string> SubKeys(string path)
    {
        using var k = _hklm.OpenSubKey(path, false);
        return k?.GetSubKeyNames() ?? new string[0];
    }

    public string? GetString(string path, string name)
    {
        using var k = _hklm.OpenSubKey(path, false);
        var v = k?.GetValue(name);
        return v switch
        {
            null => null,
            string s => s,
            string[] arr => string.Join(", ", arr),
            _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    public int? GetDword(string path, string name)
    {
        using var k = _hklm.OpenSubKey(path, false);
        return k?.GetValue(name) is int i ? i : (int?)null;
    }
}
