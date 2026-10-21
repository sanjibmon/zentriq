using System;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using WixToolset.Dtf.WindowsInstaller;
using Zentriq.Inventory;

namespace Zentriq.CustomActions
{
    /// <summary>
    /// Zentriq Agent MSI: stores the enrollment details without the code ever appearing in the MSI log (a plain
    /// RegistryValue would be logged by WriteRegistryValues, so the values are passed as hidden CustomActionData and written
    /// by a deferred action into a key readable only by SYSTEM and Administrators), and enforces uninstall protection.
    /// </summary>
    public static class AgentActions
    {
        private const string KeyPath = @"SOFTWARE\Zentriq\Agent";
        private const string ProtectionPath = KeyPath + @"\Protection";
        private const char Sep = '\u001f';

        private static bool IsLegacyCode(string code) => code.StartsWith("ZQ1.", StringComparison.Ordinal);

        /// <summary>"server", "server:8444" or "https://server:8444" → https://server:8444; null when not valid.</summary>
        internal static string NormalizeServer(string value)
        {
            var v = (value ?? "").Trim().Trim('"').TrimEnd('/');
            if (v.Length == 0) return null;
            if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return null;
            if (!v.Contains("://")) v = "https://" + v;
            Uri u;
            if (!Uri.TryCreate(v, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(u.Host)) return null;
            var port = u.IsDefaultPort && value.IndexOf(":443", StringComparison.Ordinal) < 0 ? 8444 : u.Port;
            return "https://" + u.Host + ":" + port;
        }

        private static int? Minutes(string value, int min, int max)
        {
            int m;
            return int.TryParse((value ?? "").Trim(), out m) && m >= min && m <= max ? m : (int?)null;
        }

        [CustomAction]
        public static ActionResult PrepareAgentEnrollment(Session session)
        {
            var code = (session["ENROLLMENTCODE"] ?? "").Trim().Trim('"');
            if (code.Length == 0) return ActionResult.Success;
            var shortCode = AgentCodes.NormalizeEnrollment(code);
            if (shortCode == null && !IsLegacyCode(code))
            {
                session.Log("The enrollment code is not a 20-character Zentriq enrollment code and was ignored.");
                return ActionResult.Success;
            }
            var server = NormalizeServer(session["SERVERURL"] ?? "") ?? "";
            if (shortCode != null && server.Length == 0) session.Log("SERVERURL is missing: the agent cannot enroll until it is given (ZentriqAgent.exe --enroll <code> --server <address>).");
            var ci = Minutes(session["CHECKININTERVAL"], 2, 1440);
            var inv = Minutes(session["INVENTORYINTERVAL"], 30, 10080);
            // Hidden (HideTarget): never logged.
            session["ZqAgentEnroll"] = (shortCode ?? code) + Sep + server + Sep + (ci?.ToString() ?? "") + Sep + (inv?.ToString() ?? "");
            return ActionResult.Success;
        }

        /// <summary>Wizard "Connect to Zentriq" page: server address, code format, intervals and an http:// proxy.</summary>
        [CustomAction]
        public static ActionResult ValidateAgentInput(Session session)
        {
            string error = null;
            var code = (session["ENROLLMENTCODE"] ?? "").Trim().Trim('"');
            var server = (session["SERVERURL"] ?? "").Trim();
            var proxy = (session["PROXYURL"] ?? "").Trim();
            if (code.Length > 0 && AgentCodes.NormalizeEnrollment(code) == null && !IsLegacyCode(code))
                error = "The enrollment code has 20 letters and digits. Copy it from the Zentriq Web Console (Agents > Enrollment codes > Show code).";
            else if (code.Length > 0 && !IsLegacyCode(code) && NormalizeServer(server) == null)
                error = "Enter the Zentriq server address, for example zentriq.example.com or https://zentriq.example.com:8444 (HTTPS only).";
            else if (proxy.Length > 0)
            {
                Uri u;
                var candidate = proxy.Contains("://") ? proxy : "http://" + proxy;
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttp)
                    error = "The proxy must be an http:// address such as http://proxy.example.com:8080. Leave it empty if this device connects to the Zentriq server directly.";
                else if (server.Length > 0 && server.IndexOf(u.Host, StringComparison.OrdinalIgnoreCase) >= 0)
                    error = "This looks like the Zentriq server address, not a proxy. Leave the proxy empty.";
            }
            if (error == null && code.Length > 0 && !IsLegacyCode(code))
            {
                session["SERVERURL"] = NormalizeServer(server);
                var ci = Minutes(session["CHECKININTERVAL"], 2, 1440);
                var inv = Minutes(session["INVENTORYINTERVAL"], 30, 10080);
                if (ci.HasValue && inv.HasValue && inv.Value < ci.Value) error = "Inventory cannot be sent more often than the agent checks its connection. Choose a longer inventory interval.";
            }
            session["ZQ_VALIDATION_ERROR"] = error ?? "";
            return ActionResult.Success;
        }

        [CustomAction]
        public static ActionResult WriteAgentEnrollment(Session session)
        {
            try
            {
                var data = session["CustomActionData"];
                if (string.IsNullOrEmpty(data)) return ActionResult.Success;
                var parts = data.Split(Sep);
                var sec = new RegistrySecurity();
                sec.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                    sec.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(sid, null), RegistryRights.FullControl,
                        InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = hklm.CreateSubKey(KeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree, sec))
                {
                    key.SetAccessControl(sec); // also when the key already existed
                    key.SetValue("EnrollmentCode", parts[0], RegistryValueKind.String);
                    if (parts.Length > 1 && parts[1].Length > 0) key.SetValue("EnrollmentServer", parts[1], RegistryValueKind.String);
                    int m;
                    if (parts.Length > 2 && int.TryParse(parts[2], out m)) key.SetValue("InstallCheckinMinutes", m, RegistryValueKind.DWord);
                    if (parts.Length > 3 && int.TryParse(parts[3], out m)) key.SetValue("InstallInventoryMinutes", m, RegistryValueKind.DWord);
                }
                session.Log("Zentriq Agent enrollment details stored (key restricted to SYSTEM and Administrators).");
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("Could not store the enrollment code: " + ex.GetType().Name + ": " + ex.Message);
                return ActionResult.Failure;
            }
        }

        // ---- uninstall protection ---------------------------------------------------------------------------------

        private const string NeedCode =
            "Removing the Zentriq Agent needs an uninstall code from your IT team (Zentriq console > Agents > Uninstall code, valid for one hour).\n\n" +
            "Run as administrator:\n  \"C:\\Program Files\\Zentriq Agent\\ZentriqAgent.exe\" --uninstall <code>\n" +
            "or use Apps > Zentriq Agent > Modify > Remove and enter the code.";

        /// <summary>
        /// Uninstall (not an upgrade): when the agent is protected and no code was given, stops with an explanation. The
        /// code itself is checked by <see cref="CheckUninstallCode"/> (as SYSTEM, which alone can read the device's key).
        /// </summary>
        [CustomAction]
        public static ActionResult UninstallGate(Session session)
        {
            var code = (session["UNINSTALLCODE"] ?? "").Trim();
            if (Protected() && code.Length == 0)
            {
                session.Log("Uninstall refused: the Zentriq Agent is protected and no UNINSTALLCODE was given.");
                using (var r = new Record(1)) { r.FormatString = "[1]"; r[1] = NeedCode; session.Message(InstallMessage.Error | (InstallMessage)MessageButtons.OK | (InstallMessage)MessageIcon.Error, r); }
                return ActionResult.Failure;
            }
            session["ZqUninstallCheck"] = code; // hidden (HideTarget)
            return ActionResult.Success;
        }

        [CustomAction]
        public static ActionResult CheckUninstallCode(Session session)
        {
            byte[] key;
            string allHash = null, allExpires = null;
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hklm.OpenSubKey(ProtectionPath, false))
                {
                    key = k == null ? null : k.GetValue("UninstallKey") as byte[];
                    allHash = k == null ? null : k.GetValue("AllCodeHash") as string;
                    allExpires = k == null ? null : k.GetValue("AllCodeExpires") as string;
                }
            }
            catch (Exception ex)
            {
                session.Log("Uninstall protection key not readable: " + ex.Message);
                key = null;
            }
            if (key == null || key.Length != 32)
            {
                session.Log("The Zentriq Agent is not protected on this device; no uninstall code needed.");
                return ActionResult.Success;
            }
            var typed = session["CustomActionData"];
            DateTime expires;
            DateTime? until = DateTime.TryParse(allExpires, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out expires) ? expires : (DateTime?)null;
            string error;
            // The code for all agents (Agents › Uninstall code); codes made for this one device by earlier versions also work.
            if (AgentCodes.VerifyUninstallCodeForAll(allHash, until, typed, DateTime.UtcNow, out error)
                || AgentCodes.VerifyUninstallCode(key, typed, DateTime.UtcNow, out _))
            {
                session.Log("Uninstall code accepted.");
                return ActionResult.Success;
            }
            session.Log("Uninstall refused: " + error);
            try { using (var r = new Record(1)) { r.FormatString = "[1]"; r[1] = "The Zentriq Agent was not removed.\n\n" + error; session.Message(InstallMessage.Error | (InstallMessage)MessageButtons.OK | (InstallMessage)MessageIcon.Error, r); } }
            catch (Exception) { }
            return ActionResult.Failure;
        }

        private static bool Protected()
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hklm.OpenSubKey(KeyPath, false))
                    return k != null && k.GetValue("UninstallProtected") is int v && v == 1;
            }
            catch (Exception) { return true; } // cannot tell: ask for the code (the deferred check decides)
        }
    }
}
