using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Zentriq.LicenseServer.Services;

/// <summary>Daily log files in data\logs (kept 30 days), next to the Windows event log.</summary>
public sealed class FileLoggerProvider(string dir) : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private DateTime _cleaned;

    public ILogger CreateLogger(string category) => _loggers.GetOrAdd(category, c => new FileLogger(this, c));
    public void Dispose() { }

    internal void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, $"server-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine, Encoding.UTF8);
                if (_cleaned.Date != DateTime.Today)
                {
                    _cleaned = DateTime.Today;
                    foreach (var f in Directory.GetFiles(dir, "server-*.log").Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-30))) File.Delete(f);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information && (level >= LogLevel.Warning || !category.StartsWith("Microsoft.", StringComparison.Ordinal));
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format)
        {
            if (!IsEnabled(level)) return;
            var name = category[(category.LastIndexOf('.') + 1)..];
            owner.Write($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level.ToString().ToUpperInvariant()[..4]} {name}: {format(state, ex)}{(ex is null ? "" : " | " + ex)}");
        }
    }
}

/// <summary>Daily database backup (pg_dump, custom format) into data\backups; the newest 14 are kept.</summary>
public sealed class DailyBackup(ServerData data, ILogger<DailyBackup> log) : BackgroundService
{
    public const int Keep = 14;
    public static string PgDump => Path.Combine(AppContext.BaseDirectory, "..", "pgsql", "bin", OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!File.Exists(PgDump)) return;   // not an installed server
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            var last = Directory.Exists(data.Backups) ? Directory.GetFiles(data.Backups, "*.dump").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max() : DateTime.MinValue;
            if (DateTime.UtcNow - last > TimeSpan.FromHours(23))
            {
                try { var f = Run(data); log.LogInformation("Database backup written: {File}", f); }
                catch (Exception ex) { log.LogError("Database backup failed: {Message}", ex.Message); }
            }
            try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch (OperationCanceledException) { return; }
        }
    }

    public static string Run(ServerData data)
    {
        Directory.CreateDirectory(data.Backups);
        var s = data.State;
        var file = Path.Combine(data.Backups, $"{ServerData.DatabaseName}-{DateTime.Now:yyyyMMdd-HHmmss}.dump");
        var psi = new ProcessStartInfo(PgDump, $"-w -h 127.0.0.1 -p {s.PgPort} -U {ServerData.DatabaseUser} -Fc -f \"{file}\" {ServerData.DatabaseName}")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        psi.Environment["PGPASSWORD"] = data.GetSecret("database") ?? "";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(600_000) || p.ExitCode != 0) { try { File.Delete(file); } catch (IOException) { } throw new InvalidOperationException("pg_dump: " + err.Trim()); }
        foreach (var old in Directory.GetFiles(data.Backups, "*.dump").OrderByDescending(File.GetLastWriteTimeUtc).Skip(Keep)) File.Delete(old);
        return file;
    }
}

/// <summary>
/// Applies new address settings by restarting the service: the process ends and Windows starts it again (the
/// service's recovery actions, set by the installer). Outside a service the change applies at the next start.
/// </summary>
public static class Restarter
{
    public static bool CanRestart => Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();

    public static void Schedule(ILogger log)
    {
        if (!CanRestart) { log.LogWarning("Restart the license server to apply the new address settings."); return; }
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);   // let the response reach the browser
            log.LogWarning("Restarting to apply the new address settings (Windows starts the service again in a few seconds).");
            await Task.Delay(300);
            Process.GetCurrentProcess().Kill();
        });
    }
}
