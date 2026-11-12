using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zentriq.Infrastructure.Data;

namespace Zentriq.Api.Infrastructure;

/// <summary>
/// Data scope (build 136): for a user whose roles are all limited to locations, departments or device groups, the
/// endpoint's database access is switched to that user's scoped connections, so PostgreSQL row-level security shows
/// only their devices and what belongs to them. Applies to the data source, the EF context and the stores an endpoint
/// receives as parameters. Unrestricted users, agents and anonymous calls are not touched.
/// </summary>
public sealed class DataScopeFilter : IEndpointFilter
{
    /// <summary>Stores that read device data through a data source given to their constructor.</summary>
    private static readonly HashSet<Type> ScopedStores =
    [
        typeof(Zentriq.Infrastructure.Vulnerabilities.VulnStore),
        typeof(Zentriq.Infrastructure.Security.SecurityStore),
        typeof(Zentriq.Infrastructure.Cloud.CloudStore),
    ];

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var user = ctx.HttpContext.User;
        if (user.HasClaim(ZentriqClaims.Scoped, "1") && Guid.TryParse(user.FindFirstValue(ZentriqClaims.UserId), out var uid))
        {
            var sp = ctx.HttpContext.RequestServices;
            var source = sp.GetRequiredService<ScopedDataSources>().For(uid);
            for (var i = 0; i < ctx.Arguments.Count; i++)
            {
                switch (ctx.Arguments[i])
                {
                    case NpgsqlDataSource:
                        ctx.Arguments[i] = source;
                        break;
                    case ZentriqDbContext:
                        var db = new ZentriqDbContext(new DbContextOptionsBuilder<ZentriqDbContext>().UseNpgsql(source).Options);
                        ctx.HttpContext.Response.RegisterForDisposeAsync(db);
                        ctx.Arguments[i] = db;
                        break;
                    case { } store when ScopedStores.Contains(store.GetType()):
                        ctx.Arguments[i] = ActivatorUtilities.CreateInstance(sp, store.GetType(), source);
                        break;
                }
            }
        }
        return await next(ctx);
    }

    /// <summary>The data source for the signed-in user outside endpoint parameters (null: not scoped).</summary>
    public static NpgsqlDataSource? ScopedSource(HttpContext http) =>
        http.User.HasClaim(ZentriqClaims.Scoped, "1") && Guid.TryParse(http.User.FindFirstValue(ZentriqClaims.UserId), out var uid)
            ? http.RequestServices.GetRequiredService<ScopedDataSources>().For(uid)
            : null;
}
