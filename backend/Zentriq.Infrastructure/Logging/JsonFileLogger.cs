using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zentriq.Application.Security;

namespace Zentriq.Infrastructure.Logging;

/// <summary>
/// Structured (JSON-lines) file logger with daily files and size-capped retention. Every message
/// and exception passes through <see cref="Redactor"/> so credentials and keys never reach disk.
/// </summary>
public sealed class JsonFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly string _prefix;
    private readonly int _retainDays;
    private readonly BlockingCollection<string> _queue = new(10_000);
    private readonly Thread _writer;

    public JsonFileLoggerProvider(string directory, string prefix, int retainDays = 30)
    {
        _directory = directory; _prefix = prefix; _retainDays = retainDays;
        Directory.CreateDirectory(directory);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "zentriq-file-logger" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Enqueue(string line) => _queue.TryAdd(line);

    private void WriteLoop()
    {
        string? currentPath = null; StreamWriter? w = null;
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                var path = Path.Combine(_directory, $"{_prefix}-{DateTime.UtcNow:yyyyMMdd}.log");
                if (path != currentPath)
                {
                    w?.Dispose();
                    w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8) { AutoFlush = true };
                    currentPath = path;
                    Prune();
                }
                w!.WriteLine(line);
            }
        }
        catch { /* logging must never crash the service */ }
        finally { w?.Dispose(); }
    }

    private void Prune()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_directory, $"{_prefix}-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-_retainDays)) File.Delete(f);
        }
        catch { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(3));
    }

    private sealed class FileLogger(JsonFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var props = new Dictionary<string, object?>
            {
                ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
                ["level"] = logLevel.ToString(),
                ["category"] = category,
                ["msg"] = Redactor.Redact(formatter(state, exception)),
            };
            if (eventId.Id != 0) props["eventId"] = eventId.Id;
            if (state is IEnumerable<KeyValuePair<string, object?>> kv)
                foreach (var (k, v) in kv)
                    if (k != "{OriginalFormat}" && !props.ContainsKey(k)) props[k] = v is string s ? Redactor.Redact(s) : v?.ToString();
            if (exception is not null) props["exception"] = Redactor.Redact(exception.ToString());
            owner.Enqueue(JsonSerializer.Serialize(props));
        }
    }
}

public static class JsonFileLoggerExtensions
{
    public static ILoggingBuilder AddZentriqFile(this ILoggingBuilder builder, string directory, string prefix)
    {
        builder.Services.AddSingleton<ILoggerProvider>(_ => new JsonFileLoggerProvider(directory, prefix));
        return builder;
    }
}
