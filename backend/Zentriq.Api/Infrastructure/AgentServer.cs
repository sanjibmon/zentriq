using System.Security.Cryptography.X509Certificates;
using Zentriq.Infrastructure.Agents;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Api.Infrastructure;

/// <summary>Agent-side server identity: the agent CA (issues client certificates) and the SPKI pin of the HTTPS certificate.</summary>
public sealed class AgentServer(ZentriqOptions options, ZentriqPaths paths, Func<AppSecrets> secrets, ILogger<AgentServer> logger)
{
    private readonly Lazy<X509Certificate2?> _ca = new(() =>
    {
        var pwd = secrets().AgentCaPfxPassword;
        if (string.IsNullOrEmpty(pwd) || !File.Exists(paths.AgentCaFile))
        {
            logger.LogWarning("Agent CA is not provisioned; agent enrollment is unavailable until 'zentriqctl repair' runs.");
            return null;
        }
        return AgentCertificateAuthority.Load(paths.AgentCaFile, pwd);
    });

    public X509Certificate2? Ca => _ca.Value;
    public bool Enabled => options.Https.AgentPort > 0;
    public int Port => options.Https.AgentPort;

    private readonly Lazy<string?> _pin = new(() =>
    {
        try
        {
            using var cert = HttpsCertificate.Load(options, paths);
            return AgentCertificateAuthority.SpkiSha256(cert);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the HTTPS certificate to compute the agent pin.");
            return null;
        }
    });

    /// <summary>SHA-256 SPKI pin of the HTTPS certificate served on the agent port.</summary>
    public string? ServerPin => _pin.Value;

    public string DefaultUrl => !string.IsNullOrWhiteSpace(options.Https.AgentPublicUrl)
        ? options.Https.AgentPublicUrl!.TrimEnd('/')
        : $"https://{options.Https.HostName}:{options.Https.AgentPort}";

    /// <summary>
    /// Every address agents can try when the one they use stops answering (the server got a new IP address, or the
    /// name does not resolve on the device): the configured URL, the server's names and its IPv4 addresses. Agents
    /// trust only the pinned server key, so a wrong address can never pose as the server.
    /// </summary>
    public string[] AlternateUrls()
    {
        var list = new List<string> { DefaultUrl };
        foreach (var n in Zentriq.Infrastructure.Certificates.ServerCertificates.Names(options.Https.HostName))
            if (n != "localhost") list.Add($"https://{n}:{Port}");
        foreach (var a in Zentriq.Infrastructure.Certificates.ServerCertificates.Addresses(options.Https.HostName))
            if (!System.Net.IPAddress.IsLoopback(a)) list.Add($"https://{a}:{Port}");
        return list.Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
    }
}

/// <summary>The agent port serves only /api/agent and /downloads; agent APIs are not served on the console port.</summary>
public sealed class AgentPortMiddleware(RequestDelegate next, AgentServer agent)
{
    public Task InvokeAsync(HttpContext ctx)
    {
        var onAgentPort = agent.Enabled && ctx.Connection.LocalPort == agent.Port;
        var agentPath = ctx.Request.Path.StartsWithSegments("/api/agent");
        if (onAgentPort && !agentPath && !ctx.Request.Path.StartsWithSegments("/downloads"))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        if (agentPath && !onAgentPort && !string.Equals(Environment.GetEnvironmentVariable("ZENTRIQ_HTTP_ONLY"), "1", StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        return next(ctx);
    }
}
