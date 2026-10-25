using System.Text;
using Zentriq.Application.Security;

namespace Zentriq.Setup;

/// <summary>
/// Console + file output for zentriqctl. Lines starting with "PROGRESS " and "STEP " are parsed by
/// the MSI custom action to drive the installer progress bar. Everything is redacted.
/// </summary>
public sealed class Reporter : IDisposable
{
    private StreamWriter? _file;
    private readonly string? _directory;
    private readonly string _name;
    private string? _path;
    private bool _opened;
    /// <summary>The log file (created when first asked for or written to); null when it could not be created.</summary>
    public string? LogPath { get { Open(); return _path; } }
    /// <summary>Whether anything was written (the log file is created at the first line).</summary>
    public bool Wrote { get; private set; }

    public Reporter(string? logDirectory, string name)
    {
        _directory = logDirectory;
        _name = name;
    }

    private void Open()
    {
        if (_opened || _directory is null) return;
        _opened = true;
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, $"{_name}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
            _file = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
            _path = path;
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
        Open();
        Wrote = true;
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
