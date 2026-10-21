#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Zentriq.Inventory
{
    /// <summary>
    /// Short codes shared by the server, the Zentriq Agent and the agent's Windows Installer custom actions (this file is
    /// compiled into all three; keep it free of other dependencies).
    /// <list type="bullet">
    /// <item><b>Enrollment code</b>: 20 characters (Crockford base 32: 0-9 and A-Z without I, L, O, U). The first 6 identify the
    /// code, the other 14 are the secret (70 bits). The server stores only SHA-256 of the whole code. The server address is
    /// given separately; the agent learns the server's key pins from the server itself, proven with the code
    /// (<see cref="EnrollProof"/>), so a man in the middle cannot pose as the server.</item>
    /// <item><b>Uninstall code</b>: 16 characters, valid for one hour. 6 characters carry the expiry time, 10 an HMAC of it
    /// with the device's own uninstall key (which the server keeps and the agent stores readable by SYSTEM only).</item>
    /// </list>
    /// </summary>
    public static class AgentCodes
    {
        public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        public const int EnrollmentLength = 20;
        public const int EnrollmentIdLength = 6;
        public const int UninstallLength = 16;
        public static readonly TimeSpan UninstallValidity = TimeSpan.FromHours(1);
        private static readonly DateTime Epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static string Random(int length)
        {
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder(length);
            foreach (var b in bytes) sb.Append(Alphabet[b & 31]);
            return sb.ToString();
        }

        public static string NewEnrollmentCode() => Random(EnrollmentLength);

        /// <summary>Upper case, without spaces and dashes; O→0 and I/L→1 (easily confused when typed). Null when not a code of <paramref name="length"/> characters.</summary>
        public static string? Normalize(string? code, int length)
        {
            if (code is null) return null;
            var sb = new StringBuilder();
            foreach (var ch in code.Trim().Trim('"').ToUpperInvariant())
            {
                if (ch == '-' || ch == ' ' || ch == '\t') continue;
                var c = ch == 'O' ? '0' : ch == 'I' || ch == 'L' ? '1' : ch;
                if (Alphabet.IndexOf(c) < 0) return null;
                sb.Append(c);
            }
            return sb.Length == length ? sb.ToString() : null;
        }

        public static string? NormalizeEnrollment(string? code) => Normalize(code, EnrollmentLength);
        public static bool IsShortEnrollmentCode(string? code) => NormalizeEnrollment(code) is not null;
        public static string EnrollmentId(string normalizedCode) => normalizedCode.Substring(0, EnrollmentIdLength);

        /// <summary>"ABCDE-FGHJK-MNPQR-STVWX" for display (the dashes are optional when typed).</summary>
        public static string Group(string code, int size = 5)
        {
            var parts = new List<string>();
            for (var i = 0; i < code.Length; i += size) parts.Add(code.Substring(i, Math.Min(size, code.Length - i)));
            return string.Join("-", parts);
        }

        public static byte[] Sha256(string s)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes(s));
        }

        /// <summary>
        /// The server's proof that it knows the enrollment code, bound to the key pins it serves: HMAC-SHA256 keyed with
        /// SHA-256(code) over the agent's nonce and the pins. The agent accepts the server only if the key it actually saw
        /// in the TLS handshake is one of the proven pins.
        /// </summary>
        public static string EnrollProof(byte[] codeHash, string nonce, IEnumerable<string> pins)
        {
            var msg = "zentriq-enroll-v2\n" + nonce + "\n" + string.Join(",", pins.OrderBy(p => p, StringComparer.Ordinal));
            using (var h = new HMACSHA256(codeHash)) return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(msg)));
        }

        // ---- uninstall codes -----------------------------------------------------------------------------------

        public static byte[] NewUninstallKey()
        {
            var k = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(k);
            return k;
        }

        /// <summary>A code that lets this device's agent be uninstalled until <paramref name="expiresUtc"/> (at most one hour ahead).</summary>
        public static string UninstallCode(byte[] key, DateTime expiresUtc)
        {
            var minutes = (long)Math.Ceiling((expiresUtc.ToUniversalTime() - Epoch).TotalMinutes);
            var head = Encode((ulong)minutes, 6);
            return head + Mac(key, minutes);
        }

        /// <summary>Checks a typed uninstall code. <paramref name="error"/> says why it was refused.</summary>
        public static bool VerifyUninstallCode(byte[] key, string? code, DateTime nowUtc, out string error)
        {
            var c = Normalize(code, UninstallLength);
            if (c is null) { error = "The uninstall code must have 16 letters and digits (for example ABCD-EFGH-JKMN-PQRS)."; return false; }
            var minutes = (long)Decode(c.Substring(0, 6));
            var expires = Epoch.AddMinutes(minutes);
            var expected = Mac(key, minutes);
            if (!FixedEquals(expected, c.Substring(6))) { error = "The uninstall code is not valid for this device. Get a new code for this device from the Zentriq console."; return false; }
            if (nowUtc.ToUniversalTime() > expires) { error = $"The uninstall code expired at {expires:yyyy-MM-dd HH:mm} UTC. Codes are valid for one hour; get a new one from the Zentriq console."; return false; }
            if (expires > nowUtc.ToUniversalTime() + UninstallValidity + TimeSpan.FromMinutes(5)) { error = "The uninstall code is not valid yet (check this device's clock)."; return false; }
            error = "";
            return true;
        }

        // ---- one uninstall code for all agents ------------------------------------------------------------------

        /// <summary>A random 16-character code that removes the agent on any device until it expires (one hour).</summary>
        public static string NewUninstallCodeForAll() => Random(UninstallLength);

        /// <summary>What the agents store (readable by SYSTEM only): SHA-256 of the code, hex.</summary>
        public static string UninstallCodeHash(string normalizedCode)
        {
            var h = Sha256("zentriq-uninstall-all\n" + normalizedCode);
            var sb = new StringBuilder(64);
            foreach (var b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>Checks a typed code against the code for all agents the device received from the server.</summary>
        public static bool VerifyUninstallCodeForAll(string? storedHash, DateTime? expiresUtc, string? code, DateTime nowUtc, out string error)
        {
            var c = Normalize(code, UninstallLength);
            if (c is null) { error = "The uninstall code must have 16 letters and digits (for example ABCD-EFGH-JKMN-PQRS)."; return false; }
            if (string.IsNullOrEmpty(storedHash) || expiresUtc is null) { error = "This device has not received an uninstall code yet. Create one in the Zentriq console (Agents › Uninstall code) and try again once the device is online."; return false; }
            if (!FixedEquals(storedHash!.ToLowerInvariant(), UninstallCodeHash(c))) { error = "The uninstall code is not valid. Use the current code from the Zentriq console (Agents › Uninstall code)."; return false; }
            if (nowUtc.ToUniversalTime() > expiresUtc.Value.ToUniversalTime()) { error = $"The uninstall code expired at {expiresUtc.Value.ToUniversalTime():yyyy-MM-dd HH:mm} UTC. Codes are valid for one hour; create a new one in the Zentriq console."; return false; }
            error = "";
            return true;
        }

        private static string Mac(byte[] key, long minutes)
        {
            using (var h = new HMACSHA256(key))
            {
                var mac = h.ComputeHash(Encoding.UTF8.GetBytes("zentriq-uninstall\n" + minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                ulong v = 0;
                for (var i = 0; i < 7; i++) v = (v << 8) | mac[i];
                return Encode(v >> 6, 10); // 50 bits
            }
        }

        private static string Encode(ulong value, int length)
        {
            var chars = new char[length];
            for (var i = length - 1; i >= 0; i--) { chars[i] = Alphabet[(int)(value & 31)]; value >>= 5; }
            return new string(chars);
        }

        private static ulong Decode(string s)
        {
            ulong v = 0;
            foreach (var ch in s) v = (v << 5) | (uint)Alphabet.IndexOf(ch);
            return v;
        }

        private static bool FixedEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
