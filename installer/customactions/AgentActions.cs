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
