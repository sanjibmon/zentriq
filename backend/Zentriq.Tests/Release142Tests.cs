using Zentriq.Inventory;

namespace Zentriq.Tests;

/// <summary>Build 142: software metering records each run of a program (started, closed).</summary>
public sealed class Release142Tests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"zq-usage-{Guid.NewGuid():N}.json");
    private static readonly DateTime T0 = new(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);

    public void Dispose() { try { File.Delete(_file); } catch (IOException) { } }

    private ExperienceRecorder New() => new(_file, _ => ("Word", "Microsoft", "16.0"));

    private static RunningProgram P(string path, int pid, DateTimeOffset? started = null) => new(path, "CORP\\anita", pid, 1000 + pid, started);

    private static List<UsageSession> Runs(ExperienceRecorder r, DateTime at) => r.Report(at).Usage.Sessions;

    [Fact]
    public void A_run_lasts_from_the_first_to_the_last_sample_with_the_program()
    {
        var r = New();
        var word = @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE";
        for (var m = 0; m <= 3; m++) r.Sample(T0.AddMinutes(m), [P(word, 10)], true, null, null);
        r.Sample(T0.AddMinutes(4), [], true, null, null);
        var run = Assert.Single(Runs(r, T0.AddMinutes(5)));
        Assert.Equal(new DateTimeOffset(T0), run.Start);
        Assert.Equal(new DateTimeOffset(T0.AddMinutes(3)), run.End);
        Assert.Equal("WINWORD.EXE", run.Exe);
        Assert.Equal("Word", run.Product);
        Assert.Equal("CORP\\anita", run.User);
    }

    [Fact]
    public void The_process_start_time_gives_the_real_start_and_a_running_program_has_no_end()
    {
        var r = New();
        var started = new DateTimeOffset(T0.AddMinutes(-7));
        r.Sample(T0, [P("C:\\Apps\\excel.exe", 11, started)], true, null, null);
        r.Sample(T0.AddMinutes(1), [P("C:\\Apps\\excel.exe", 11, started)], true, null, null);
        var run = Assert.Single(Runs(r, T0.AddMinutes(1)));
        Assert.Equal(started, run.Start);
        Assert.Null(run.End);
        Assert.Equal(new DateTimeOffset(T0.AddMinutes(1)), run.LastSeen);
    }

    [Fact]
    public void Several_processes_are_one_run_and_closing_and_reopening_is_a_new_run()
    {
        var r = New();
        const string chrome = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
        r.Sample(T0, [P(chrome, 1), P(chrome, 2), P(chrome, 3)], true, null, null);
        r.Sample(T0.AddMinutes(1), [P(chrome, 1), P(chrome, 4)], true, null, null);
        r.Sample(T0.AddMinutes(2), [], true, null, null);
        r.Sample(T0.AddMinutes(10), [P(chrome, 9)], true, null, null);
        var runs = Runs(r, T0.AddMinutes(10)).OrderBy(x => x.Start).ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal(new DateTimeOffset(T0.AddMinutes(1)), runs[0].End);
        Assert.Equal(new DateTimeOffset(T0.AddMinutes(10)), runs[1].Start);
        Assert.Null(runs[1].End);
    }

    [Fact]
    public void The_same_process_after_an_agent_stop_continues_its_run()
    {
        var started = new DateTimeOffset(T0.AddMinutes(-1));
        var r = New();
        r.Sample(T0, [P("C:\\Apps\\cad.exe", 5, started)], true, null, null);
        r.Save(force: true);
        // The agent was stopped for 20 minutes; the program kept running (same process).
        var again = New();
        again.Sample(T0.AddMinutes(20), [P("C:\\Apps\\cad.exe", 5, started)], true, null, null);
        var run = Assert.Single(Runs(again, T0.AddMinutes(20)));
        Assert.Equal(started, run.Start);
        Assert.Null(run.End);
        // A new process after a long gap is a new run, and starts no earlier than the previous one ended.
        again.Sample(T0.AddMinutes(21), [], true, null, null);
        again.Sample(T0.AddMinutes(40), [P("C:\\Apps\\cad.exe", 6, started)], true, null, null);
        var runs = Runs(again, T0.AddMinutes(40)).OrderBy(x => x.Start).ToList();
        Assert.Equal(2, runs.Count);
        Assert.True(runs[1].Start >= runs[0].End);
    }

    [Fact]
    public void Turning_metering_off_forgets_the_runs()
    {
        var r = New();
        r.Sample(T0, [P("C:\\Apps\\a.exe", 1)], true, null, null);
        r.ClearUsage();
        Assert.Empty(Runs(r, T0));
    }
}
