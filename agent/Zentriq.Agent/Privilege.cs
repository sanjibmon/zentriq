using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using Zentriq.Inventory;

namespace Zentriq.Agent;

/// <summary>
/// Endpoint privilege management on Windows: the members of the local Administrators group (read, removed in enforce
/// mode, added for time-limited rights), and running an approved application as administrator for a standard user.
/// </summary>
internal static class Privilege
{
    // ---- local Administrators group -------------------------------------------------------------------------------

    public sealed class Member { public string Name = ""; public string? Sid; public string Kind = "unknown"; }

    public static string AdministratorsName()
    {
        var full = new SecurityIdentifier(SecurityPackRules.AdministratorsSid).Translate(typeof(NTAccount)).Value;
        return full.Substring(full.IndexOf('\\') + 1);
    }

    public static List<Member> Members()
    {
        var list = new List<Member>();
        var rc = NetLocalGroupGetMembers(null, AdministratorsName(), 2, out var buf, -1, out var read, out _, IntPtr.Zero);
        if (rc != 0) throw new Win32Exception(rc, "The members of the Administrators group could not be read");
        try
        {
            var size = Marshal.SizeOf(typeof(LocalGroupMembersInfo2));
            for (var i = 0; i < read; i++)
            {
                var m = (LocalGroupMembersInfo2)Marshal.PtrToStructure(new IntPtr(buf.ToInt64() + i * size), typeof(LocalGroupMembersInfo2))!;
                string? sid = null;
                try { sid = new SecurityIdentifier(m.Sid).Value; } catch (ArgumentException) { }
                list.Add(new Member
                {
                    Name = string.IsNullOrEmpty(m.DomainAndName) ? sid ?? "?" : m.DomainAndName!, Sid = sid,
                    Kind = m.SidUsage switch { 1 => "user", 2 => "group", 4 => "group", 5 => "well-known", 6 => "deleted", _ => "unknown" },
                });
            }
        }
        finally { NetApiBufferFree(buf); }
        return list;
    }

    public static void Remove(string sid)
    {
        var s = new SecurityIdentifier(sid);
        var bytes = new byte[s.BinaryLength];
        s.GetBinaryForm(bytes, 0);
        var p = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, p, bytes.Length);
            var info = new[] { new LocalGroupMembersInfo0 { Sid = p } };
            var rc = NetLocalGroupDelMembers(null, AdministratorsName(), 0, info, 1);
            if (rc != 0 && rc != 1377 /* not a member */) throw new Win32Exception(rc);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    public static void Add(string account)
    {
        var info = new[] { new LocalGroupMembersInfo3 { DomainAndName = account } };
        var rc = NetLocalGroupAddMembers(null, AdministratorsName(), 3, info, 1);
        if (rc != 0 && rc != 1378 /* already a member */) throw new Win32Exception(rc);
    }

    /// <summary>The built-in Administrator account (RID 500) is disabled (it is the way back in on a computer without a domain).</summary>
    public static bool BuiltInAdministratorDisabled()
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT SID, Disabled FROM Win32_UserAccount WHERE LocalAccount = True");
            foreach (System.Management.ManagementObject o in s.Get())
                using (o)
                    if (o["SID"] is string sid && sid.EndsWith("-500", StringComparison.Ordinal)) return o["Disabled"] is bool d && d;
        }
        catch (Exception) { }
        return true;
    }

    public static bool DomainJoined()
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT PartOfDomain FROM Win32_ComputerSystem");
            foreach (System.Management.ManagementObject o in s.Get()) using (o) return o["PartOfDomain"] is bool b && b;
        }
        catch (Exception) { }
        return false;
    }

    // ---- file facts -----------------------------------------------------------------------------------------------

    public static string Sha256(Stream s)
    {
        using var sha = SHA256.Create();
        s.Position = 0;
        return string.Concat(sha.ComputeHash(s).Select(b => b.ToString("x2")));
    }

    /// <summary>The signer (CN and O of the certificate) when the file carries a valid Authenticode signature.</summary>
    public static (string? Cn, string? O) Signer(string path)
    {
        if (!Verify(path)) return (null, null);
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return (cert.GetNameInfo(X509NameType.SimpleName, false), Part(cert.Subject, "O"));
        }
        catch (Exception) { return (null, null); }
    }

    private static string? Part(string subject, string key)
    {
        foreach (var p in subject.Split(','))
        {
            var kv = p.Trim();
            if (kv.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return kv.Substring(key.Length + 1).Trim('"');
        }
        return null;
    }

    private static bool Verify(string path)
    {
        var file = new WinTrustFileInfo { StructSize = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)), FilePath = path };
        var fileMem = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
        try
        {
            Marshal.StructureToPtr(file, fileMem, false);
            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf(typeof(WinTrustData)), UiChoice = 2 /* none */, RevocationChecks = 0, UnionChoice = 1 /* file */,
                FileInfo = fileMem, StateAction = 0, ProvFlags = 0x1000 /* cache only URL retrieval: no network */,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
        }
        catch (Exception) { return false; }
        finally { Marshal.FreeHGlobal(fileMem); }
    }

    // ---- elevation: the user's own identity with the Administrators group added (S4U logon) ------------------------

    /// <summary>
    /// Starts <paramref name="commandLine"/> on the desktop of <paramref name="session"/> as the signed-in user, with
    /// administrator rights: a service-for-user logon of the same account with the Administrators group and the session's
    /// logon SID added (so it may use the desktop), elevated. Returns the process id.
    /// </summary>
    public static int StartElevated(int session, string user, string commandLine, string? directory)
    {
        if (!WTSQueryUserToken((uint)session, out var userToken)) throw new Win32Exception(Marshal.GetLastWin32Error(), "The signed-in user's session was not found");
        IntPtr lsa = IntPtr.Zero, logonToken = IntPtr.Zero, primary = IntPtr.Zero, env = IntPtr.Zero, auth = IntPtr.Zero, groups = IntPtr.Zero, adminSid = IntPtr.Zero;
        var logonSid = IntPtr.Zero;
        try
        {
            logonSid = LogonSid(userToken);
            var slash = user.IndexOf('\\');
            var domain = slash > 0 ? user.Substring(0, slash) : Environment.MachineName;
            var name = slash > 0 ? user.Substring(slash + 1) : user;
            var local = string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

            var lsaName = LsaString("Zentriq");
            var rc = LsaRegisterLogonProcess(ref lsaName, out lsa, out _);
            FreeLsaString(lsaName);
            if (rc != 0) throw new Win32Exception(LsaNtStatusToWinError(rc), "Could not register with the Local Security Authority");
            var pkgName = LsaString(local ? "MICROSOFT_AUTHENTICATION_PACKAGE_V1_0" : "Kerberos");
            rc = LsaLookupAuthenticationPackage(lsa, ref pkgName, out var package);
            FreeLsaString(pkgName);
            if (rc != 0) throw new Win32Exception(LsaNtStatusToWinError(rc));

            auth = S4uLogon(local ? name : name, local ? Environment.MachineName : null, out var authLen);

            var admins = new SecurityIdentifier(SecurityPackRules.AdministratorsSid);
            var adminBytes = new byte[admins.BinaryLength];
            admins.GetBinaryForm(adminBytes, 0);
            adminSid = Marshal.AllocHGlobal(adminBytes.Length);
            Marshal.Copy(adminBytes, 0, adminSid, adminBytes.Length);
            var count = logonSid != IntPtr.Zero ? 2 : 1;
            var entry = IntPtr.Size * 2;                                   // SID_AND_ATTRIBUTES: pointer + DWORD (padded)
            var header = IntPtr.Size;                                      // DWORD GroupCount, padded to pointer alignment
            groups = Marshal.AllocHGlobal(header + entry * count);
            Marshal.WriteInt32(groups, count);
            Marshal.WriteIntPtr(groups, header, adminSid);
            Marshal.WriteInt32(groups, header + IntPtr.Size, 0x7);         // enabled, enabled by default, mandatory
            if (logonSid != IntPtr.Zero)
            {
                Marshal.WriteIntPtr(groups, header + entry, logonSid);
                Marshal.WriteInt32(groups, header + entry + IntPtr.Size, unchecked((int)0xC0000007));   // logon ID + enabled
            }

            var source = new TokenSource { Name = Encoding.ASCII.GetBytes("Zentriq\0") };
            AllocateLocallyUniqueId(out source.Id);
            var origin = LsaString("Zentriq elevation");
            Exception? last = null;
            foreach (var type in new[] { 4 /* Batch */, 3 /* Network */ })
            {
                rc = LsaLogonUser(lsa, ref origin, type, package, auth, authLen, groups, ref source, out var profile, out _, out _, out logonToken, out _, out var sub);
                if (profile != IntPtr.Zero) LsaFreeReturnBuffer(profile);
                if (rc == 0) break;
                last = new Win32Exception(LsaNtStatusToWinError(rc), $"Logon of {user} for elevation failed (type {type}, status 0x{rc:X8}, sub 0x{sub:X8})");
                logonToken = IntPtr.Zero;
            }
            FreeLsaString(origin);
            if (logonToken == IntPtr.Zero) throw last ?? new InvalidOperationException("The logon for the elevation failed.");

            // A filtered (limited) token: take its elevated linked token.
            var token = logonToken;
            if (TokenInt(token, 18 /* TokenElevationType */) == 3 /* limited */ && GetTokenInformation(token, 19 /* TokenLinkedToken */, out IntPtr linked, IntPtr.Size, out _)) token = linked;
            if (TokenInt(token, 20 /* TokenElevation */) == 0) throw new InvalidOperationException("Windows did not give the logon administrator rights.");
            if (!DuplicateTokenEx(token, 0x02000000 /* MAXIMUM_ALLOWED */, IntPtr.Zero, 2, 1 /* primary */, out primary)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (token != logonToken) CloseHandle(token);
            var sid = session;
            if (!SetTokenInformation(primary, 12 /* TokenSessionId */, ref sid, 4)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not place the program in the user's session");
            if (!CreateEnvironmentBlock(out env, userToken, false)) env = IntPtr.Zero;
            var si = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Desktop = @"winsta0\default", Flags = 0x1 /* STARTF_USESHOWWINDOW */, ShowWindow = 1 };
            if (!CreateProcessAsUser(primary, null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, 0x00000400 | 0x00000010 /* unicode env, new console */,
                    env, directory, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The program could not be started");
            CloseHandle(pi.Thread);
            CloseHandle(pi.Process);
            return pi.ProcessId;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            if (logonToken != IntPtr.Zero) CloseHandle(logonToken);
            if (lsa != IntPtr.Zero) LsaDeregisterLogonProcess(lsa);
            if (auth != IntPtr.Zero) Marshal.FreeHGlobal(auth);
            if (groups != IntPtr.Zero) Marshal.FreeHGlobal(groups);
            if (adminSid != IntPtr.Zero) Marshal.FreeHGlobal(adminSid);
            if (logonSid != IntPtr.Zero) Marshal.FreeHGlobal(logonSid);
            CloseHandle(userToken);
        }
    }

    /// <summary>MSV1_0_S4U_LOGON / KERB_S4U_LOGON (same layout): message type 12, flags, user, domain or realm.</summary>
    private static IntPtr S4uLogon(string user, string? domain, out int length)
    {
        var u = Encoding.Unicode.GetBytes(user);
        var d = domain is null ? Array.Empty<byte>() : Encoding.Unicode.GetBytes(domain);
        var us = IntPtr.Size == 8 ? 16 : 8;                  // UNICODE_STRING
        var bufOff = IntPtr.Size == 8 ? 8 : 4;
        var head = 8 + 2 * us;
        length = head + u.Length + 2 + d.Length + 2;
        var p = Marshal.AllocHGlobal(length);
        for (var i = 0; i < length; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, 12);
        Marshal.WriteInt32(p, 4, 0);
        var uPtr = new IntPtr(p.ToInt64() + head);
        Marshal.Copy(u, 0, uPtr, u.Length);
        Marshal.WriteInt16(p, 8, (short)u.Length);
        Marshal.WriteInt16(p, 10, (short)(u.Length + 2));
        Marshal.WriteIntPtr(p, 8 + bufOff, uPtr);
        if (d.Length > 0)
        {
            var dPtr = new IntPtr(uPtr.ToInt64() + u.Length + 2);
            Marshal.Copy(d, 0, dPtr, d.Length);
            Marshal.WriteInt16(p, 8 + us, (short)d.Length);
            Marshal.WriteInt16(p, 8 + us + 2, (short)(d.Length + 2));
            Marshal.WriteIntPtr(p, 8 + us + bufOff, dPtr);
        }
        return p;
    }

    /// <summary>A copy of the logon SID of the user's session (the desktop allows it).</summary>
    private static IntPtr LogonSid(IntPtr token)
    {
        GetTokenInformation(token, 2 /* TokenGroups */, IntPtr.Zero, 0, out var size);
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, 2, buf, size, out _)) return IntPtr.Zero;
            var count = Marshal.ReadInt32(buf);
            var entry = IntPtr.Size * 2;
            for (var i = 0; i < count; i++)
            {
                var sidPtr = Marshal.ReadIntPtr(buf, IntPtr.Size + i * entry);
                var attrs = (uint)Marshal.ReadInt32(buf, IntPtr.Size + i * entry + IntPtr.Size);
                if ((attrs & 0xC0000000) != 0xC0000000) continue;
                var len = GetLengthSid(sidPtr);
                var copy = Marshal.AllocHGlobal(len);
                CopySid(len, copy, sidPtr);
                return copy;
            }
            return IntPtr.Zero;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static int TokenInt(IntPtr token, int cls) => GetTokenInformation(token, cls, out int value, 4, out _) ? value : -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaStringStruct { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    private static LsaStringStruct LsaString(string s) => new LsaStringStruct { Length = (ushort)s.Length, MaximumLength = (ushort)(s.Length + 1), Buffer = Marshal.StringToHGlobalAnsi(s) };
    private static void FreeLsaString(LsaStringStruct s) => Marshal.FreeHGlobal(s.Buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenSource { [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Name; public Luid Id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct QuotaLimits { public IntPtr PagedPool, NonPagedPool, MinWorkingSet, MaxWorkingSet, Pagefile; public long TimeLimit; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved; public string? Desktop; public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public short ShowWindow, Reserved2;
        public IntPtr Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupMembersInfo2 { public IntPtr Sid; public int SidUsage; public string? DomainAndName; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LocalGroupMembersInfo0 { public IntPtr Sid; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupMembersInfo3 { public string DomainAndName; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo { public uint StructSize; public string FilePath; public IntPtr File; public IntPtr KnownSubject; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize; public IntPtr PolicyCallbackData, SipClientData; public uint UiChoice, RevocationChecks, UnionChoice; public IntPtr FileInfo;
        public uint StateAction; public IntPtr StateData; public string? UrlReference; public uint ProvFlags, UiContext; public IntPtr SignatureSettings;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetLocalGroupGetMembers(string? server, string group, int level, out IntPtr buf, int prefMax, out int read, out int total, IntPtr resume);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetLocalGroupDelMembers(string? server, string group, int level, LocalGroupMembersInfo0[] members, int count);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetLocalGroupAddMembers(string? server, string group, int level, LocalGroupMembersInfo3[] members, int count);
    [DllImport("netapi32.dll")] private static extern int NetApiBufferFree(IntPtr buf);
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)] private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);
    [DllImport("secur32.dll")] private static extern int LsaRegisterLogonProcess(ref LsaStringStruct name, out IntPtr handle, out ulong mode);
    [DllImport("secur32.dll")] private static extern int LsaLookupAuthenticationPackage(IntPtr handle, ref LsaStringStruct name, out uint package);
    [DllImport("secur32.dll")] private static extern int LsaLogonUser(IntPtr handle, ref LsaStringStruct origin, int logonType, uint package, IntPtr auth, int authLength,
        IntPtr localGroups, ref TokenSource source, out IntPtr profile, out uint profileLength, out Luid logonId, out IntPtr token, out QuotaLimits quotas, out int subStatus);
    [DllImport("secur32.dll")] private static extern int LsaDeregisterLogonProcess(IntPtr handle);
    [DllImport("secur32.dll")] private static extern int LsaFreeReturnBuffer(IntPtr buffer);
    [DllImport("advapi32.dll")] private static extern int LsaNtStatusToWinError(int status);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AllocateLocallyUniqueId(out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int length, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int length, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, out IntPtr info, int length, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, int cls, ref int info, int length);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonation, int type, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcessAsUser(IntPtr token, string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation info);
    [DllImport("advapi32.dll")] private static extern int GetLengthSid(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CopySid(int length, IntPtr destination, IntPtr source);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out IntPtr token);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
