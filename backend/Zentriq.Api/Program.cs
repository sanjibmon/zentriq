using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Zentriq.Api.Endpoints;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Logging;
using Zentriq.Infrastructure.Secrets;

var paths = new ZentriqPaths();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});

builder.Configuration
    .AddJsonFile(paths.ConfigFile, optional: true, reloadOnChange: false)
    .AddEnvironmentVariables("ZENTRIQ_");

builder.Host.UseWindowsService(o => o.ServiceName = "ZentriqApi");
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddZentriqFile(paths.Logs, "api");

var options = builder.Configuration.GetSection(ZentriqOptions.Section).Get<ZentriqOptions>() ?? new ZentriqOptions();
var httpOnly = builder.Environment.IsDevelopment() || Environment.GetEnvironmentVariable("ZENTRIQ_HTTP_ONLY") == "1";

builder.Services.AddZentriqInfrastructure(options, paths, "Zentriq.Api");

builder.Services.AddSingleton<AgentServer>();

// ---- Kestrel / HTTPS -------------------------------------------------------------------
builder.WebHost.ConfigureKestrel((ctx, k) =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
    if (httpOnly)
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("ZENTRIQ_HTTP_PORT"), out var p) ? p : 5080;
        k.ListenLocalhost(port);
        return;
    }
    var cert = HttpsCertificate.Load(options, paths);
    k.ListenAnyIP(options.Https.Port, l => l.UseHttps(cert));
    if (options.Https.AgentPort > 0)
    {
        // Agents: mutual TLS. Any client certificate is accepted at the TLS layer; AgentEndpoints validates
        // it against the installation's agent CA and the agent registry (enrollment itself has no certificate).
        k.ListenAnyIP(options.Https.AgentPort, l => l.UseHttps(new Microsoft.AspNetCore.Server.Kestrel.Https.HttpsConnectionAdapterOptions
        {
            ServerCertificate = cert,
            ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.AllowCertificate,
            ClientCertificateValidation = (_, _, _) => true,
            CheckCertificateRevocation = false,
        }));
    }
    if (options.Https.LoopbackHealthPort > 0)
        k.Listen(System.Net.IPAddress.Loopback, options.Https.LoopbackHealthPort); // health probes only (see HealthEndpoints)
});

// ---- Data protection (cookies, MFA secrets) --------------------------------------------
var dp = builder.Services.AddDataProtection()
    .SetApplicationName("Zentriq")
    .PersistKeysToFileSystem(new DirectoryInfo(paths.DataProtectionKeys));
if (OperatingSystem.IsWindows()) dp.ProtectKeysWithDpapi(protectToLocalMachine: true);

// ---- Authentication & authorization ----------------------------------------------------
builder.Services.AddAuthentication(AuthSchemes.Smart)
    .AddPolicyScheme(AuthSchemes.Smart, "Session or API token", o => o.ForwardDefaultSelector = AuthSchemes.Select)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenAuthenticationHandler.Scheme, _ => { })
    .AddCookie(o =>
    {
        o.Cookie.Name = "zentriq.session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = httpOnly ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        o.SlidingExpiration = true;
        o.Events = SessionEvents.Create();
    });
builder.Services.AddAuthorization(o =>
{
    foreach (var p in Permissions.All)
        o.AddPolicy(p, policy => policy.RequireAuthenticatedUser().RequireClaim(ZentriqClaims.Permission, p));
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("agent-enroll", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("password-reset", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = c =>
{
    c.ProblemDetails.Extensions["traceId"] = c.HttpContext.TraceIdentifier;
});
builder.Services.AddOpenApi("v1");
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<RequestContext>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

var app = builder.Build();

// The dual-stack listener reports IPv4 clients as IPv4-mapped IPv6 (::ffff:a.b.c.d); record them as plain IPv4.
app.Use((ctx, next) =>
{
    if (ctx.Connection.RemoteIpAddress is { IsIPv4MappedToIPv6: true } mapped) ctx.Connection.RemoteIpAddress = mapped.MapToIPv4();
    return next(ctx);
});

app.UseExceptionHandler(e => e.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = ApiErrors.Map(ex);
    if (status >= 500) app.Logger.LogError(ex, "Unhandled error for {Path}", ctx.Request.Path);
    await Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["traceId"] = ctx.TraceIdentifier }).ExecuteAsync(ctx);
}));
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<AgentPortMiddleware>();
if (!httpOnly) app.UseHsts();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<CsrfHeaderMiddleware>();
app.UseAuthorization();

app.MapHealthEndpoints();
var api = app.MapGroup("/api/v1");
api.MapSetupEndpoints();
api.MapAuthEndpoints();
api.MapAccountEndpoints(options);
api.MapAssetEndpoints();
api.MapAssetActionEndpoints();
api.MapActivityAndUninstallEndpoints();
api.MapDiscoveryEndpoints();
api.MapJobEndpoints();
api.MapDashboardEndpoints();
api.MapOverviewEndpoints();
api.MapLicensingEndpoints();
api.MapUserEndpoints();
api.MapAuditEndpoints();
api.MapSystemEndpoints();
api.MapOrganizationEndpoints();
api.MapCredentialEndpoints();
api.MapDirectoryEndpoints();
api.MapInventoryEndpoints();
api.MapAgentAdminEndpoints();
api.MapReportEndpoints();
api.MapVulnerabilityEndpoints();
api.MapSecurityEndpoints();
api.MapIntegrationEndpoints();
api.MapRemoteEndpoints();
api.MapDepartmentEndpoints();
app.MapAgentEndpoints();
api.MapPendingModuleEndpoints();
app.MapOpenApi("/api/openapi/{documentName}.json").RequireAuthorization(Permissions.SettingsManage);

// Unknown /api paths are 404 JSON; everything else is the single-page app.
app.MapFallback("/api/{**rest}", () => Results.Problem(statusCode: 404, title: "Not found"));
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
