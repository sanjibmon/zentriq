using Zentriq.Infrastructure;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Logging;
using Zentriq.Worker;
using Zentriq.Worker.Handlers;

var paths = new ZentriqPaths();

// "--remote-host": the ZentriqRemote service (supervises the bundled remote support server). Same executable,
// separate service and account, nothing else runs in that process.
if (args.Contains("--remote-host"))
{
    var rb = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
    rb.Services.AddWindowsService(o => o.ServiceName = "ZentriqRemote");
    rb.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));
    rb.Logging.ClearProviders();
    rb.Logging.AddConsole();
    rb.Logging.AddZentriqFile(paths.Logs, "remote");
    rb.Services.AddSingleton(paths);
    rb.Services.AddHostedService<Zentriq.Worker.Remote.RemoteHost>();
    rb.Build().Run();
    return;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration.AddJsonFile(paths.ConfigFile, optional: true).AddEnvironmentVariables("ZENTRIQ_");
builder.Services.AddWindowsService(o => o.ServiceName = "ZentriqWorker");
builder.Services.Configure<HostOptions>(o =>
{
    o.ShutdownTimeout = TimeSpan.FromSeconds(30);
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost; // let SCM recovery restart us
});
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddZentriqFile(paths.Logs, "worker");

var options = builder.Configuration.GetSection(ZentriqOptions.Section).Get<ZentriqOptions>() ?? new ZentriqOptions();
builder.Services.AddZentriqInfrastructure(options, paths, "Zentriq.Worker");
builder.Services.AddSingleton(new WorkerIdentity($"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}"));

builder.Services.AddScoped<IJobHandler, SelfTestHandler>();
builder.Services.AddScoped<IJobHandler, DiscoveryScanHandler>();
builder.Services.AddScoped<IJobHandler, UsageSnapshotHandler>();
builder.Services.AddScoped<IJobHandler, DirectorySyncHandler>();
builder.Services.AddScoped<IJobHandler, AssetActionHandler>();
builder.Services.AddScoped<IJobHandler, ReportDeliveryHandler>();
builder.Services.AddScoped<IJobHandler, VulnSyncHandler>();
builder.Services.AddScoped<IJobHandler, VulnEvaluateHandler>();
builder.Services.AddScoped<IJobHandler, VulnImportHandler>();
builder.Services.AddScoped<IJobHandler, SecurityEvaluateHandler>();
builder.Services.AddSingleton<IHostPinger, TcpHostPinger>();
builder.Services.AddSingleton<Zentriq.Worker.Agentless.AgentlessInventory>();

builder.Services.AddHostedService<JobRunner>();
builder.Services.AddHostedService<Housekeeping>();
builder.Services.AddHostedService<Zentriq.Worker.DuplicateCleanup>();
builder.Services.AddHostedService<Zentriq.Worker.LifecycleAutomation>();
builder.Services.AddHostedService<Zentriq.Worker.Integrations.IntegrationService>();
builder.Services.AddHostedService<Zentriq.Worker.Remote.RemoteSupportSync>();

builder.Build().Run();
