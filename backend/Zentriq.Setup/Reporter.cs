using System.Text;
using Zentriq.Application.Security;

namespace Zentriq.Setup;

/// <summary>
/// Console + file output for zentriqctl. Lines starting with "PROGRESS " and "STEP " are parsed by
/// the MSI custom action to drive the installer progress bar. Everything is redacted.
/// </summary>
public sealed class Reporter : IDisposable
{
    private readonly StreamWriter? _file;
    public string? LogPath { get; }

    public Reporter(string? logDirectory, string name)
    {
        if (logDirectory is null) return;
        try
        {
            Directory.CreateDirectory(logDirectory);
            LogPath = Path.Combine(logDirectory, $"{name}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
            _file = new StreamWriter(LogPath, append: true, Encoding.UTF8) { AutoFlush = true };
        }
        catch { /* fall back to console only */ }
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);
    public void Step(int n, int total, string name) => Write("STEP", $"{n}/{total} {name}");
    public void Progress(int pct, string message) => Write("PROGRESS", $"{pct} {message}");

    private void Write(string level, string message)
    {
        var line = $"{DateTime.UtcNow:O} {level} {Redactor.Redact(message)}";
        if (level is "STEP" or "PROGRESS") Console.Out.WriteLine($"{level} {Redactor.Redact(message)}");
        else Console.Out.WriteLine(line);
        _file?.WriteLine(line);
    }

    public void Dispose() => _file?.Dispose();
}

public sealed class SetupException(string message, string? remedy = null) : Exception(message)
{
    public string? Remedy { get; } = remedy;
}
