using System;
using System.IO;
using System.Text;

namespace Zentriq.Agent;

/// <summary>Plain-text log under %ProgramData%\Zentriq\Agent\Logs (rotated at 5 MB, 5 files). Never logs secrets.</summary>
internal static class Log
{
    private static readonly object Gate = new object();
    public static bool Console { get; set; }
    public static string Directory => Path.Combine(AgentPaths.Root, "Logs");
    private static string File => Path.Combine(Directory, "agent.log");

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : message + ": " + ex.GetType().Name + ": " + ex.Message);

    private static void Write(string level, string message)
    {
        var line = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'") + " " + level + " " + message;
        if (Console) System.Console.WriteLine(line);
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var fi = new FileInfo(File);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                {
                    for (var i = 4; i >= 1; i--)
                    {
                        var src = File + "." + i;
                        if (System.IO.File.Exists(src)) { var dst = File + "." + (i + 1); if (System.IO.File.Exists(dst)) System.IO.File.Delete(dst); System.IO.File.Move(src, dst); }
                    }
                    System.IO.File.Move(File, File + ".1");
                }
                System.IO.File.AppendAllText(File, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

internal static class AgentPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Zentriq", "Agent");
    public static string Queue => Path.Combine(Root, "Queue");
    public static string QueuedInventory => Path.Combine(Queue, "inventory.json.gz");
}
