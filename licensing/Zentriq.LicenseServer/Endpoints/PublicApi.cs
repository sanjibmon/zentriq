using System.Text.Json;
using System.Text.Json.Serialization;
using Zentriq.Domain.Licensing;
using Zentriq.LicenseServer.Services;

namespace Zentriq.LicenseServer.Endpoints;

/// <summary>
/// The protocol Zentriq installations speak (Zentriq.Domain.Licensing.LicenseServerProtocol). Rate limited per client
/// address. Errors are RFC 7807 problems whose "title" the product shows to its administrator.
/// </summary>
public static class PublicApi
{
    public const string RatePolicy = "public";

    /// <summary>Offline activation (browser page or API): the license key plus the activation request exported by the air-gapped server.</summary>
    public sealed record OfflineBody(
        [property: JsonPropertyName("licenseKey")] string? LicenseKey,
        [property: JsonPropertyName("request")] JsonElement Request);

    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web);

    internal static string? Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    private static string? Bearer(HttpContext http)
    {
        var h = http.Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
    }

    public static void MapPublicApi(this WebApplication app)
    {
        var g = app.MapGroup("/").RequireRateLimiting(RatePolicy).DisableAntiforgery();

        g.MapPost(LicenseServerProtocol.ActivatePath, async (OnlineActivateRequest body, LicenseService svc, HttpContext http, CancellationToken ct) =>
        {
            var r = await svc.ActivateAsync(body.LicenseKey, body.Request, LicenseFormats.Online, Ip(http), ct);
            return r.Ok
                ? Results.Ok(new OnlineActivateResponse(r.Value.Document, r.Value.Token, r.Message))
                : Results.Problem(statusCode: r.Status, title: r.Message);
        });

        g.MapPost(LicenseServerProtocol.CheckinPath, async (OnlineCheckinRequest body, LicenseService svc, HttpContext http, CancellationToken ct) =>
        {
            var r = await svc.CheckinAsync(Bearer(http), body, Ip(http), ct);
            return r.Ok ? Results.Ok(r.Value) : Results.Problem(statusCode: r.Status, title: r.Message);
        });

        g.MapPost(LicenseServerProtocol.DeactivatePath, async (OnlineDeactivateRequest body, LicenseService svc, HttpContext http, CancellationToken ct) =>
        {
            var r = await svc.DeactivateAsync(Bearer(http), body, Ip(http), ct);
            return r.Ok ? Results.Ok(new { message = r.Message }) : Results.Problem(statusCode: r.Status, title: r.Message);
        });

        g.MapPost(LicenseServerProtocol.OfflineActivatePath, async (OfflineBody body, LicenseService svc, HttpContext http, CancellationToken ct) =>
        {
            ActivationRequest? request;
            try { request = body.Request.ValueKind == JsonValueKind.Object ? body.Request.Deserialize<ActivationRequest>(RequestJson) : null; }
            catch (JsonException) { request = null; }
            if (request is null) return Results.Problem(statusCode: 400, title: "Attach the activation request file (zentriq-activation-request-….json) from Licensing › Offline activation.");
            var r = await svc.ActivateAsync(body.LicenseKey, request, LicenseFormats.Offline, Ip(http), ct);
            if (!r.Ok) return Results.Problem(statusCode: r.Status, title: r.Message);
            return Results.Ok(new
            {
                license = r.Value.Document,
                fileName = $"zentriq-license-{r.Value.Payload.LicenseId}.zlic",
                message = r.Message,
                customer = r.Value.Payload.Customer,
                edition = r.Value.Payload.Edition,
            });
        });

        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).DisableRateLimiting();
    }
}
