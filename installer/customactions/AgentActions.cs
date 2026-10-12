using System;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using WixToolset.Dtf.WindowsInstaller;

namespace Zentriq.CustomActions
{
    /// <summary>
    /// Zentriq Agent MSI: stores the enrollment code without it ever appearing in the MSI log. A plain
    /// RegistryValue would be logged by WriteRegistryValues (RegAddValue), so the value is passed as hidden
    /// CustomActionData and written by this deferred action into a key readable only by SYSTEM and Administrators.
    /// </summary>
    public static class AgentActions
    {
        private const string KeyPath = @"SOFTWARE\Zentriq\Agent";

        [CustomAction]
        public static ActionResult PrepareAgentEnrollment(Session session)
        {
            var code = (session["ENROLLMENTCODE"] ?? "").Trim().Trim('"');
            if (code.Length == 0) return ActionResult.Success;
            if (!code.StartsWith("ZQ1.", StringComparison.Ordinal))
            {
                session.Log("The enrollment code does not start with ZQ1. and was ignored.");
                return ActionResult.Success;
            }
            session["ZqAgentEnroll"] = code; // hidden (HideTarget): never logged
            return ActionResult.Success;
        }

        /// <summary>Wizard "Connect to Zentriq" page: checks the code format and that the proxy is a plain http:// proxy.</summary>
        [CustomAction]
        public static ActionResult ValidateAgentInput(Session session)
        {
            string error = null;
            var code = (session["ENROLLMENTCODE"] ?? "").Trim().Trim('"');
            var proxy = (session["PROXYURL"] ?? "").Trim();
            if (code.Length > 0 && !code.StartsWith("ZQ1.", StringComparison.Ordinal))
                error = "The enrollment code must start with ZQ1. Copy it from the Zentriq Web Console (Agents > Enrollment codes > Show code).";
            else if (proxy.Length > 0)
            {
                Uri u;
                var candidate = proxy.Contains("://") ? proxy : "http://" + proxy;
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttp)
                    error = "The proxy must be an http:// address such as http://proxy.example.com:8080. Leave it empty if this device connects to the Zentriq server directly (the server address is already inside the enrollment code).";
                else if (code.Length > 0 && code.IndexOf(u.Host, StringComparison.OrdinalIgnoreCase) >= 0)
                    error = "This looks like the Zentriq server address, not a proxy. Leave the proxy empty: the server address is already inside the enrollment code.";
            }
            session["ZQ_VALIDATION_ERROR"] = error ?? "";
            return ActionResult.Success;
        }

        [CustomAction]
        public static ActionResult WriteAgentEnrollment(Session session)
        {
            try
            {
                var code = session["CustomActionData"];
                if (string.IsNullOrEmpty(code)) return ActionResult.Success;
                var sec = new RegistrySecurity();
                sec.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                    sec.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(sid, null), RegistryRights.FullControl,
                        InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = hklm.CreateSubKey(KeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree, sec))
                {
                    key.SetAccessControl(sec); // also when the key already existed
                    key.SetValue("EnrollmentCode", code, RegistryValueKind.String);
                }
                session.Log("Zentriq Agent enrollment code stored (key restricted to SYSTEM and Administrators).");
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("Could not store the enrollment code: " + ex.GetType().Name + ": " + ex.Message);
                return ActionResult.Failure;
            }
        }
    }
}
