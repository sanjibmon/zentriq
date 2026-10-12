using System.Net;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Infrastructure.Mail;

/// <summary>SMTP settings stored in system_settings['smtp']. The password is encrypted (never returned by the API).</summary>
public sealed class SmtpSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("host")] public string? Host { get; set; }
    [JsonPropertyName("port")] public int Port { get; set; } = 587;
    /// <summary>"starttls" (587, recommended) or "none" (25, internal relay only).</summary>
    [JsonPropertyName("security")] public string Security { get; set; } = "starttls";
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("passwordProtected")] public string? PasswordProtected { get; set; }
    [JsonPropertyName("fromAddress")] public string? FromAddress { get; set; }
    [JsonPropertyName("fromName")] public string? FromName { get; set; }
    /// <summary>Console address placed in e-mailed links, e.g. https://zentriq.corp.example.com.</summary>
    [JsonPropertyName("consoleUrl")] public string? ConsoleUrl { get; set; }
}

public sealed class MailException(string message) : Exception(message);

public sealed class MailService(NpgsqlDataSource ds, CredentialProtector protector)
{
    /// <summary>Fixed associated-data id for the SMTP password (binds the ciphertext to this purpose).</summary>
    public static readonly Guid PasswordAad = new("6f1d2c3b-58a4-4f0e-9a51-5d7c0e3a9b21");

    public async Task<SmtpSettings> LoadAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = 'smtp'");
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrEmpty(json) ? new SmtpSettings() : JsonSerializer.Deserialize<SmtpSettings>(json) ?? new SmtpSettings();
    }

    public async Task SaveAsync(SmtpSettings s, Guid? userId, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.system_settings (key, value, updated_at, updated_by) VALUES ('smtp', @v, now(), @u)
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now(), updated_by = EXCLUDED.updated_by
            """);
        cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(s) });
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public string ProtectPassword(string password) => protector.Protect(PasswordAad, new CredentialSecret { Password = password });

    public static bool IsUsable(SmtpSettings s) => s.Enabled && !string.IsNullOrWhiteSpace(s.Host) && !string.IsNullOrWhiteSpace(s.FromAddress);

    public async Task SendAsync(SmtpSettings s, string to, string subject, string textBody, string? htmlBody, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.FromAddress)) throw new MailException("E-mail (SMTP) is not configured.");
        using var client = new SmtpClient(s.Host.Trim(), s.Port)
        {
            EnableSsl = s.Security == "starttls",
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 20000,
        };
        if (!string.IsNullOrWhiteSpace(s.Username))
        {
            var password = s.PasswordProtected is { Length: > 0 } p ? protector.Unprotect(PasswordAad, p).Password ?? "" : "";
            client.Credentials = new NetworkCredential(s.Username.Trim(), password);
        }
        using var msg = new MailMessage
        {
            From = new MailAddress(s.FromAddress.Trim(), string.IsNullOrWhiteSpace(s.FromName) ? "Zentriq" : s.FromName.Trim()),
            Subject = subject,
            Body = textBody,
            IsBodyHtml = false,
        };
        msg.To.Add(new MailAddress(to));
        if (htmlBody is not null)
            msg.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(htmlBody, System.Text.Encoding.UTF8, "text/html"));
        try { await client.SendMailAsync(msg, ct); }
        catch (SmtpException ex)
        {
            throw new MailException($"The mail server {s.Host}:{s.Port} refused the message: {ex.StatusCode} {ex.Message}{(ex.InnerException is { } ie ? " " + ie.Message : "")}");
        }
    }
}
