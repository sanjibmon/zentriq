using System.Security.Claims;
using Zentriq.Application.Licensing;
using Zentriq.Infrastructure.Audit;
using Zentriq.Infrastructure.Licensing;

namespace Zentriq.Api.Infrastructure;

/// <summary>Per-request helper: the signed-in user, auditing and license checks.</summary>
public sealed class RequestContext(IHttpContextAccessor accessor, AuditWriter audit, LicensingService licensing)
{
    private HttpContext Http => accessor.HttpContext ?? throw new InvalidOperationException("No HTTP context.");
    public ClaimsPrincipal Principal => Http.User;
    public Guid? UserId => Guid.TryParse(Principal.FindFirstValue(ZentriqClaims.UserId), out var id) ? id : null;
    public string UserName => Principal.FindFirstValue(ClaimTypes.Email) ?? "anonymous";
    public string? SourceIp => Http.Connection.RemoteIpAddress?.ToString();
    public bool Has(string permission) => Principal.HasClaim(ZentriqClaims.Permission, permission);
    public AuditActor Actor => new(UserId, UserName, SourceIp);

    public Task AuditAsync(string action, string? target, string outcome = "Success", object? detail = null, CancellationToken ct = default) =>
        audit.WriteAsync(Actor, action, target, outcome, detail, ct: ct);

    private LicenseStatus? _status;
    public async Task<LicenseStatus> LicenseAsync(CancellationToken ct = default) => _status ??= await licensing.GetStatusAsync(ct);
}

/// <summary>Endpoint filter: blocks feature APIs when the trial/license does not cover the module.</summary>
public sealed class RequireModuleFilter(string module) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var rc = ctx.HttpContext.RequestServices.GetRequiredService<RequestContext>();
        var status = await rc.LicenseAsync(ctx.HttpContext.RequestAborted);
        if (!status.ModuleEnabled(module))
        {
            await rc.AuditAsync("license.blocked", ctx.HttpContext.Request.Path, "Denied", new { module, mode = status.Mode.ToString() });
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "This feature is not available under the current license.",
                detail: status.Summary,
                type: "https://zentriq.local/problems/license-required",
                extensions: new Dictionary<string, object?> { ["licenseMode"] = status.Mode.ToString(), ["module"] = module });
        }
        return await next(ctx);
    }
}

public static class EndpointExtensions
{
    public static TBuilder RequireModule<TBuilder>(this TBuilder builder, string module) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new RequireModuleFilter(module));
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public static class Paging
{
    public static (int Page, int PageSize) Normalize(int? page, int? pageSize) =>
        (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 25, 1, 200));
}

public static class Validation
{
    public static IResult Problem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
