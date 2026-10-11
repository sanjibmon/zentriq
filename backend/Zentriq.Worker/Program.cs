using Zentriq.Infrastructure;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Logging;
using Zentriq.Worker;
using Zentriq.Worker.Handlers;

var paths = new ZentriqPaths();
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
builder.Services.AddSingleton<IHostPinger, TcpHostPinger>();

builder.Services.AddHostedService<JobRunner>();
builder.Services.AddHostedService<Housekeeping>();

builder.Build().Run();
