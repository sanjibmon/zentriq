using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WixToolset.Dtf.WindowsInstaller;

namespace Zentriq.CustomActions
{
    /// <summary>
    /// Zentriq installer custom actions. Secrets (passwords) are only ever placed in hidden properties
    /// and in the stdin of zentriqctl; they are never written to the MSI log or to disk by these actions.
    /// </summary>
    public static class CustomActions
    {
        private const int TotalTicks = 140000;
        private const string PgMajor = "17";

        // ---------------------------------------------------------------------------------------
        // Prerequisite check (UI: before the "System check" page; execute sequence: silent installs)
        // ---------------------------------------------------------------------------------------
        [CustomAction]
        public static ActionResult CheckPrerequisites(Session session)
        {
            try
            {
                ApplyAnswerFileNetworkSettings(session);
                var report = new List<string>();
                var block = false;
                Action<string> ok = s => report.Add("[ OK ]  " + s);
                Action<string> warn = s => report.Add("[WARN]  " + s);
                Action<string> fail = s => { report.Add("[FAIL]  " + s); block = true; };

                // Operating system (registry: Environment.OSVersion is not reliable for .NET Framework).
                using (var nt = Hklm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    var product = nt?.GetValue("ProductName") as string ?? "Windows";
                    var type = nt?.GetValue("InstallationType") as string ?? "";
                    int build;
                    int.TryParse(nt?.GetValue("CurrentBuildNumber") as string, out build);
                    if (build < 17763) fail(string.Format("{0} (build {1}) is not supported. Windows Server 2019 or later is required.", product, build));
                    else if (type.IndexOf("Server", StringComparison.OrdinalIgnoreCase) < 0) warn(string.Format("{0} (build {1}) is a client edition; Windows Server 2019/2022/2025 is the supported platform.", product, build));
                    else ok(string.Format("{0} (build {1})", product, build));
                }
                if (!Environment.Is64BitOperatingSystem) fail("A 64-bit operating system is required.");

                int memMb;
                int.TryParse(session["PhysicalMemory"], out memMb);
                if (memMb > 0 && memMb < 3500) fail(string.Format("{0:N0} MB of memory. At least 4 GB is required (8 GB recommended).", memMb));
                else if (memMb > 0 && memMb < 7500) warn(string.Format("{0:N0} MB of memory. 8 GB or more is recommended.", memMb));
                else if (memMb > 0) ok(string.Format("{0:N0} MB of memory", memMb));

                foreach (var dir in new[] { session["INSTALLFOLDER"], session["DATAFOLDER"] }.Where(d => !string.IsNullOrEmpty(d)).Select(d => Path.GetPathRoot(d)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var free = new DriveInfo(dir).AvailableFreeSpace / (1024L * 1024 * 1024);
                        if (free < 2) fail(string.Format("Only {0} GB free on {1}. At least 2 GB is required.", free, dir));
                        else if (free < 10) warn(string.Format("{0} GB free on {1}. 10 GB or more is recommended for data and backups.", free, dir));
                        else ok(string.Format("{0} GB free on {1}", free, dir));
                    }
                    catch (Exception) { warn("Could not determine free space on " + dir); }
                }

                var existing = !string.IsNullOrEmpty(session["ZQ_EXISTING"]);
                if (existing) ok("Existing Zentriq data found in " + session["ZQ_EXISTING"] + ". It will be preserved and upgraded; settings are reused.");

                // PostgreSQL
                var pgService = session["PGSERVICENAME"];
                var pgServiceExists = ServiceExists(pgService);
                var installer = FindPostgresInstaller(session);
                if (!string.IsNullOrEmpty(installer)) session["PGINSTALLER"] = installer;
                var others = ExistingPostgres();
                if (pgServiceExists) ok("PostgreSQL service " + pgService + " is installed.");
                else if (!string.IsNullOrEmpty(installer)) ok("PostgreSQL " + PgMajor + " installer included: " + Path.GetFileName(installer));
                else
                {
                    warn("The PostgreSQL installer is not included with this MSI. Use Zentriq-Setup-x64.exe, or choose an existing PostgreSQL server.");
                    if (session["PGMODE"] == "bundled" && !existing) session["PGMODE"] = "existing";
                }
                if (others.Count > 0) report.Add("        Other PostgreSQL installations: " + string.Join("; ", others));

                if (!pgServiceExists && session["PGMODE"] == "bundled" && !VcRuntimeInstalled() && session["ZQ_FROM_BUNDLE"] != "1")
                    warn("Microsoft Visual C++ 2015-2022 (x64) runtime is missing; PostgreSQL needs it. Zentriq-Setup-x64.exe installs it automatically.");

                // Ports (skipped when Zentriq already owns them)
                int https;
                if (!existing && int.TryParse(session["HTTPSPORT"], out https) && PortInUse(https))
                    warn(string.Format("TCP port {0} is in use by another program. Choose a different HTTPS port on the HTTPS page.", https));
                int pgPort;
                if (!existing && !pgServiceExists && session["PGMODE"] == "bundled" && int.TryParse(session["PGPORT"], out pgPort) && PortInUse(pgPort))
                    warn(string.Format("TCP port {0} is in use. Choose a different PostgreSQL port on the Database page.", pgPort));

                if (session["AdminUser"] != "1" && session["Privileged"] != "1")
                    warn("Setup will ask for administrator rights when installation starts.");

                if (string.IsNullOrEmpty(session["HTTPSHOSTNAME"])) session["HTTPSHOSTNAME"] = DefaultHostName();
                SetUrl(session);

                session["ZQ_PREREQ_TEXT"] = string.Join("\r\n", report);
                session["ZQ_PREREQ_BLOCK"] = block ? "1" : "";
                session.Log("Zentriq prerequisite check:\r\n" + session["ZQ_PREREQ_TEXT"]);
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("CheckPrerequisites error: " + ex);
                session["ZQ_PREREQ_TEXT"] = "The system check could not complete: " + ex.Message;
                return ActionResult.Success; // never block the UI on an unexpected check error
            }
        }

        // ---------------------------------------------------------------------------------------
        // Wizard page validation (sets ZQ_VALIDATION_ERROR; the dialog shows it and stays put)
        // ---------------------------------------------------------------------------------------
        [CustomAction]
        public static ActionResult ValidateDatabase(Session session)
        {
            session["ZQ_VALIDATION_ERROR"] = DatabaseError(session) ?? "";
            return ActionResult.Success;
        }

        [CustomAction]
        public static ActionResult ValidateHttps(Session session)
        {
            session["ZQ_VALIDATION_ERROR"] = HttpsError(session) ?? "";
            SetUrl(session);
            return ActionResult.Success;
        }

        [CustomAction]
        public static ActionResult ValidateAdmin(Session session)
        {
            session["ZQ_VALIDATION_ERROR"] = AdminError(session) ?? "";
            return ActionResult.Success;
        }

        private static string DatabaseError(Session s)
        {
            int port;
            if (!int.TryParse(s["PGPORT"], out port) || port < 1 || port > 65535) return "Enter a PostgreSQL port between 1 and 65535.";
            if (s["PGMODE"] == "existing")
            {
                if (string.IsNullOrWhiteSpace(s["PGHOST"])) return "Enter the PostgreSQL server host name.";
                if (string.IsNullOrWhiteSpace(s["PGSUPERUSER"])) return "Enter a PostgreSQL user that can create roles and databases.";
                if (string.IsNullOrEmpty(s["PGSUPERPASSWORD"])) return "Enter the password of the PostgreSQL user.";
                if (!CanConnect(s["PGHOST"].Trim(), port, 5000)) return string.Format("Cannot reach PostgreSQL at {0}:{1}. Check the host, port and firewall.", s["PGHOST"].Trim(), port);
                return null;
            }
            if (ServiceExists(s["PGSERVICENAME"])) return null; // reuse
            if (string.IsNullOrEmpty(s["PGINSTALLER"]) || !File.Exists(s["PGINSTALLER"]))
                return "The PostgreSQL installer is not available. Run Zentriq-Setup-x64.exe, or choose 'Use an existing PostgreSQL server'.";
            if (!VcRuntimeInstalled() && s["ZQ_FROM_BUNDLE"] != "1")
                return "PostgreSQL requires the Microsoft Visual C++ 2015-2022 x64 runtime. Run Zentriq-Setup-x64.exe (it installs the runtime) or install vc_redist.x64.exe first.";
            if (PortInUse(port)) return string.Format("TCP port {0} is already in use. Choose another port for PostgreSQL.", port);
            var dataDir = s["PGDATADIR"];
            if (string.IsNullOrWhiteSpace(dataDir) || !Path.IsPathRooted(dataDir)) return "Enter a full path for the PostgreSQL data folder (for example D:\\PostgreSQL\\17\\data).";
            if (Directory.Exists(dataDir) && Directory.EnumerateFileSystemEntries(dataDir).Any())
                return "The PostgreSQL data folder " + dataDir + " is not empty. Choose an empty or new folder.";
            return null;
        }

        private static string HttpsError(Session s)
        {
            int port;
            if (!int.TryParse(s["HTTPSPORT"], out port) || port < 1 || port > 65535) return "Enter an HTTPS port between 1 and 65535.";
            var host = (s["HTTPSHOSTNAME"] ?? "").Trim();
            if (!Regex.IsMatch(host, @"^[A-Za-z0-9]([A-Za-z0-9\-\.]{0,251}[A-Za-z0-9])?$")) return "Enter a valid DNS host name for the console (for example zentriq.corp.example.com).";
            if (string.IsNullOrEmpty(s["ZQ_EXISTING"]) && PortInUse(port)) return string.Format("TCP port {0} is already in use by another program. Choose another HTTPS port.", port);
            if (s["CERTMODE"] == "existing")
            {
                var tp = (s["CERTTHUMBPRINT"] ?? "").Replace(" ", "").Replace("\u200e", "").ToUpperInvariant();
                if (!Regex.IsMatch(tp, "^[0-9A-F]{40}$")) return "Enter the 40-character SHA-1 thumbprint of a certificate in Local Computer\\Personal.";
                s["CERTTHUMBPRINT"] = tp;
                var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
                try
                {
                    store.Open(OpenFlags.ReadOnly);
                    var found = store.Certificates.Find(X509FindType.FindByThumbprint, tp, false);
                    if (found.Count == 0) return "No certificate with thumbprint " + tp + " was found in Local Computer\\Personal.";
                    var c = found[0];
                    if (!c.HasPrivateKey) return "The certificate " + tp + " has no private key.";
                    if (c.NotAfter < DateTime.Now.AddDays(1)) return "The certificate " + tp + " has expired or expires within a day.";
                }
                finally { store.Close(); }
            }
            else s["CERTTHUMBPRINT"] = "";
            return null;
        }

        private static string AdminError(Session s)
        {
            if (string.IsNullOrWhiteSpace(s["ADMINORG"])) return "Enter your organization name.";
            if (string.IsNullOrWhiteSpace(s["ADMINNAME"])) return "Enter the administrator's display name.";
            var email = (s["ADMINEMAIL"] ?? "").Trim();
            if (!Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$") || email.Length > 254) return "Enter a valid e-mail address for the administrator sign-in.";
            var pw = s["ADMINPASSWORD"] ?? "";
            var policy = PasswordPolicyError(pw, email);
            if (policy != null) return policy;
            if (pw != (s["ADMINPASSWORD2"] ?? "")) return "The passwords do not match.";
            return null;
        }

        /// <summary>Mirror of Zentriq.Application.Security.PasswordPolicy (server enforces it again).</summary>
        internal static string PasswordPolicyError(string password, string email)
        {
            if (string.IsNullOrEmpty(password) || password.Length < 12) return "The password must be at least 12 characters.";
            if (password.Length > 256) return "The password is too long.";
            var classes = (password.Any(char.IsUpper) ? 1 : 0) + (password.Any(char.IsLower) ? 1 : 0) + (password.Any(char.IsDigit) ? 1 : 0) + (password.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
            if (classes < 3) return "The password must contain at least three of: upper-case letters, lower-case letters, digits, symbols.";
            var local = email != null && email.Contains("@") ? email.Split('@')[0] : null;
            if (local != null && local.Length >= 3 && password.IndexOf(local, StringComparison.OrdinalIgnoreCase) >= 0) return "The password must not contain the account name.";
            return null;
        }

        // ---------------------------------------------------------------------------------------
        // Execute sequence: prepare and run provisioning
        // ---------------------------------------------------------------------------------------
        [CustomAction]
        public static ActionResult PrepareProvision(Session session)
        {
            try
            {
                var mode = !string.IsNullOrEmpty(session["WIX_UPGRADE_DETECTED"]) ? "upgrade"
                         : !string.IsNullOrEmpty(session["Installed"]) ? "repair" : "install";
                var answerFile = session["ZQ_ANSWERFILE"];
                var existing = !string.IsNullOrEmpty(session["ZQ_EXISTING"]);

                if (!string.IsNullOrEmpty(answerFile) && !File.Exists(answerFile))
                    return Fail(session, "The answer file '" + answerFile + "' (ZQ_ANSWERFILE) was not found.");

                // Silent installs never ran the wizard pages: validate here, before anything changes.
                if (string.IsNullOrEmpty(answerFile) && mode == "install" && !existing)
                {
                    var err = DatabaseError(session) ?? HttpsError(session) ?? AdminError(session);
                    if (err != null) return Fail(session, err + " (Set the corresponding installer properties or use ZQ_ANSWERFILE; see docs/installation.md.)");
                }

                var cad = new CustomActionData();
                cad["Exe"] = Path.Combine(session.GetTargetPath("ToolsFolder"), "zentriqctl.exe");
                cad["Mode"] = mode;
                cad["DataDir"] = (session["DATAFOLDER"] ?? "").TrimEnd('\\');
                cad["PgInstaller"] = session["PGINSTALLER"] ?? "";
                if (!string.IsNullOrEmpty(answerFile)) cad["AnswerFile"] = answerFile;
                else cad["Json"] = BuildAnswerJson(session, mode);
                session["ZqProvision"] = cad.ToString();

                using (var r = new Record(3, TotalTicks, 0, 0)) session.Message(InstallMessage.Progress, r);
                session.Log("Zentriq provisioning prepared (mode " + mode + (string.IsNullOrEmpty(answerFile) ? "" : ", answer file " + answerFile) + ").");
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                return Fail(session, "Could not prepare Zentriq configuration: " + ex.Message);
            }
        }

        [CustomAction]
        public static ActionResult Provision(Session session)
        {
            var cad = session.CustomActionData;
            var exe = cad["Exe"];
            var args = new StringBuilder("provision --input - --mode ").Append(cad["Mode"]);
            if (!string.IsNullOrEmpty(cad["DataDir"])) args.Append(" --data-dir ").Append(Quote(cad["DataDir"]));
            if (!string.IsNullOrEmpty(cad["PgInstaller"])) args.Append(" --pg-installer ").Append(Quote(cad["PgInstaller"]));

            string stdin = cad.ContainsKey("AnswerFile") ? File.ReadAllText(cad["AnswerFile"]) : cad["Json"];
            var psi = new ProcessStartInfo(exe, args.ToString())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            session.Log("Running: \"" + exe + "\" " + args);

            using (var start = new Record("ZqProvision", "Configuring Zentriq", "[1]")) session.Message(InstallMessage.ActionStart, start);
            var stderr = new StringBuilder();
            int reported = 0;
            using (var p = Process.Start(psi))
            {
                p.ErrorDataReceived += (o, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
                p.BeginErrorReadLine();
                p.StandardInput.Write(stdin);
                p.StandardInput.Close();
                stdin = null;

                var watchdog = Stopwatch.StartNew();
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    session.Log("zentriqctl: " + line);
                    if (line.StartsWith("PROGRESS ", StringComparison.Ordinal))
                    {
                        var parts = line.Substring(9).Split(new[] { ' ' }, 2);
                        int pct;
                        if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out pct))
                        {
                            var target = Math.Min(TotalTicks, TotalTicks / 100 * pct);
                            if (target > reported)
                            {
                                using (var r = new Record(2, target - reported, 0, 0)) session.Message(InstallMessage.Progress, r);
                                reported = target;
                            }
                        }
                    }
                    else if (line.StartsWith("STEP ", StringComparison.Ordinal))
                    {
                        using (var r = new Record(1)) { r[1] = "Step " + line.Substring(5); session.Message(InstallMessage.ActionData, r); }
                    }
                    if (watchdog.Elapsed > TimeSpan.FromMinutes(90))
                    {
                        try { p.Kill(); } catch (Exception) { }
                        stderr.AppendLine("ERROR Provisioning did not finish within 90 minutes.");
                        break;
                    }
                }
                p.WaitForExit();
                if (p.HasExited && p.ExitCode == 0)
                {
                    if (reported < TotalTicks) using (var r = new Record(2, TotalTicks - reported, 0, 0)) session.Message(InstallMessage.Progress, r);
                    session.Log("Zentriq provisioning succeeded.");
                    return ActionResult.Success;
                }
                session.Log("zentriqctl exited with code " + (p.HasExited ? p.ExitCode.ToString(CultureInfo.InvariantCulture) : "n/a") + "\r\n" + stderr);
            }

            var lines = stderr.ToString().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            Func<string, string> pick = prefix => lines.Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).Select(l => l.Substring(prefix.Length)).FirstOrDefault();
            var msg = new StringBuilder("Zentriq could not be configured.\r\n\r\n");
            msg.AppendLine(pick("ERROR ") ?? "zentriqctl failed without an error message.");
            var remedy = pick("REMEDY ");
            if (remedy != null) msg.AppendLine().Append("What to do: ").AppendLine(remedy);
            var log = pick("LOG ");
            msg.AppendLine().Append("Details: ").AppendLine(log ?? (cad["DataDir"] + "\\Logs"));
            msg.Append("Setup will now undo the program installation. Your data and the PostgreSQL server are kept, so running setup again continues where it stopped.");
            using (var r = new Record(1))
            {
                r.FormatString = "[1]";
                r[1] = msg.ToString();
                session.Message(InstallMessage.Error | (InstallMessage)MessageButtons.OK | (InstallMessage)MessageIcon.Error, r);
            }
            return ActionResult.Failure;
        }

        [CustomAction]
        public static ActionResult UninstallCleanup(Session session)
        {
            try
            {
                var exe = session.CustomActionData["Exe"];
                if (!File.Exists(exe)) return ActionResult.Success;
                var psi = new ProcessStartInfo(exe, "uninstall-cleanup") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(120000);
                    session.Log("zentriqctl uninstall-cleanup: " + output);
                }
            }
            catch (Exception ex) { session.Log("Uninstall cleanup warning: " + ex.Message); }
            return ActionResult.Success; // never block uninstall
        }

        [CustomAction]
        public static ActionResult OpenConsole(Session session)
        {
            try
            {
                var url = session["ZQ_URL"];
                if (!string.IsNullOrEmpty(url) && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex) { session.Log("Could not open the console: " + ex.Message); }
            return ActionResult.Success;
        }

        // ---------------------------------------------------------------------------------------
        // helpers
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// The firewall rule, registry settings and console URL are MSI resources, so an answer file's HTTPS
        /// port and host name are copied into the MSI properties (silent installs). Passwords are not read here.
        /// </summary>
        private static void ApplyAnswerFileNetworkSettings(Session session)
        {
            var file = session["ZQ_ANSWERFILE"];
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;
            string json;
            try { json = File.ReadAllText(file); } catch (Exception) { return; }
            var https = Regex.Match(json, "\"https\"\\s*:\\s*\\{(?<body>[^}]*)\\}", RegexOptions.IgnoreCase);
            if (!https.Success) return;
            var port = Regex.Match(https.Groups["body"].Value, "\"port\"\\s*:\\s*(?<v>\\d{1,5})", RegexOptions.IgnoreCase);
            if (port.Success) session["HTTPSPORT"] = port.Groups["v"].Value;
            var host = Regex.Match(https.Groups["body"].Value, "\"hostName\"\\s*:\\s*\"(?<v>[A-Za-z0-9\\.\\-]{1,253})\"", RegexOptions.IgnoreCase);
            if (host.Success) session["HTTPSHOSTNAME"] = host.Groups["v"].Value;
            session.Log("Answer file network settings: HTTPS port " + session["HTTPSPORT"] + ", host " + session["HTTPSHOSTNAME"]);
        }
        private static ActionResult Fail(Session session, string message)
        {
            session.Log("ERROR " + message);
            using (var r = new Record(1))
            {
                r.FormatString = "[1]";
                r[1] = message;
                session.Message(InstallMessage.Error | (InstallMessage)MessageButtons.OK | (InstallMessage)MessageIcon.Error, r);
            }
            return ActionResult.Failure;
        }

        private static void SetUrl(Session s)
        {
            var host = string.IsNullOrEmpty(s["HTTPSHOSTNAME"]) ? "localhost" : s["HTTPSHOSTNAME"].Trim();
            var port = s["HTTPSPORT"];
            s["ZQ_URL"] = "https://" + host + (port == "443" || string.IsNullOrEmpty(port) ? "" : ":" + port) + "/";
        }

        internal static string BuildAnswerJson(Session s, string mode)
        {
            var j = new Json();
            j.Begin();
            j.Str("mode", mode);
            j.Str("installDir", s.GetTargetPath("ToolsFolder"));
            j.Str("dataDir", (s["DATAFOLDER"] ?? "").TrimEnd('\\'));
            j.Obj("postgres");
            j.Str("mode", s["PGMODE"] == "existing" ? "existing" : "bundled");
            j.Str("installerPath", s["PGINSTALLER"]);
            j.Str("installDir", (s["PGINSTALLDIR"] ?? "").TrimEnd('\\'));
            j.Str("dataDir", (s["PGDATADIR"] ?? "").TrimEnd('\\'));
            j.Str("serviceName", s["PGSERVICENAME"]);
            j.Str("host", s["PGMODE"] == "existing" ? (s["PGHOST"] ?? "").Trim() : "localhost");
            j.Num("port", int.Parse(s["PGPORT"], CultureInfo.InvariantCulture));
            j.Str("superUser", s["PGMODE"] == "existing" ? (s["PGSUPERUSER"] ?? "").Trim() : "postgres");
            if (s["PGMODE"] == "existing") j.Str("superPassword", s["PGSUPERPASSWORD"]);
            j.Str("database", "zentriq");
            j.Str("sslMode", s["PGMODE"] == "existing" ? "Prefer" : "Disable");
            j.End();
            j.Obj("https");
            j.Num("port", int.Parse(s["HTTPSPORT"], CultureInfo.InvariantCulture));
            j.Str("hostName", (s["HTTPSHOSTNAME"] ?? "").Trim());
            if (s["CERTMODE"] == "existing") j.Str("certificateThumbprint", s["CERTTHUMBPRINT"]);
            j.End();
            if (!string.IsNullOrEmpty(s["ADMINEMAIL"]))
            {
                j.Obj("admin");
                j.Str("organization", (s["ADMINORG"] ?? "").Trim());
                j.Str("email", (s["ADMINEMAIL"] ?? "").Trim());
                j.Str("displayName", (s["ADMINNAME"] ?? "").Trim());
                j.Str("password", s["ADMINPASSWORD"]);
                j.End();
            }
            j.Finish();
            return j.ToString();
        }

        private static RegistryKey Hklm(string path) =>
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(path);

        private static bool VcRuntimeInstalled()
        {
            using (var k = Hklm(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"))
            {
                var v = k?.GetValue("Installed");
                return v is int i && i == 1;
            }
        }

        private static bool ServiceExists(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            try { return ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, name, StringComparison.OrdinalIgnoreCase)); }
            catch (Exception) { return false; }
        }

        private static List<string> ExistingPostgres()
        {
            var list = new List<string>();
            using (var root = Hklm(@"SOFTWARE\PostgreSQL\Installations"))
            {
                if (root == null) return list;
                foreach (var name in root.GetSubKeyNames())
                    using (var k = root.OpenSubKey(name))
                        list.Add(string.Format("{0} {1} ({2})", k?.GetValue("Branding") ?? "PostgreSQL", k?.GetValue("Version") ?? "", k?.GetValue("Service ID") ?? name));
            }
            return list;
        }

        /// <summary>prereqs\postgresql-*-windows-x64.exe next to the MSI (Burn caches its payload there).</summary>
        private static string FindPostgresInstaller(Session session)
        {
            var explicitPath = session["PGINSTALLER"];
            if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath)) return explicitPath;
            var msi = session["OriginalDatabase"];
            if (string.IsNullOrEmpty(msi)) return null;
            var dir = Path.Combine(Path.GetDirectoryName(msi) ?? "", "prereqs");
            if (!Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir, "postgresql-" + PgMajor + ".*-windows-x64.exe").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        private static bool PortInUse(int port)
        {
            try
            {
                return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);
            }
            catch (Exception) { return false; }
        }

        private static bool CanConnect(string host, int port, int timeoutMs)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var t = c.ConnectAsync(host, port);
                    return t.Wait(timeoutMs) && c.Connected;
                }
            }
            catch (Exception) { return false; }
        }

        private static string DefaultHostName()
        {
            try
            {
                var p = IPGlobalProperties.GetIPGlobalProperties();
                var host = p.HostName;
                if (!string.IsNullOrEmpty(p.DomainName)) host += "." + p.DomainName;
                return host.ToLowerInvariant();
            }
            catch (Exception) { return Dns.GetHostName().ToLowerInvariant(); }
        }

        private static string Quote(string s) => "\"" + s.TrimEnd('\\') + "\"";
    }

    /// <summary>Minimal JSON writer (.NET Framework has no System.Text.Json in-box).</summary>
    internal sealed class Json
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private readonly Stack<bool> _first = new Stack<bool>();

        public void Begin() { _sb.Append('{'); _first.Push(true); }
        public void Obj(string name) { Key(name); _sb.Append('{'); _first.Push(true); }
        public void End() { _sb.Append('}'); _first.Pop(); }
        public void Finish() => End();
        public void Str(string name, string value) { if (value == null) return; Key(name); Escape(value); }
        public void Num(string name, int value) { Key(name); _sb.Append(value.ToString(CultureInfo.InvariantCulture)); }
        public override string ToString() => _sb.ToString();

        private void Key(string name)
        {
            if (!_first.Pop()) _sb.Append(',');
            _first.Push(false);
            Escape(name);
            _sb.Append(':');
        }

        private void Escape(string s)
        {
            _sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }
    }
}
