namespace Zentriq.Api.Infrastructure;

/// <summary>Cache rules for the web console's files.</summary>
public static class ConsoleCache
{
    public static void Apply(HttpContext http)
    {
        var path = http.Request.Path.Value ?? "";
        var h = http.Response.Headers;
        if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
            h.CacheControl = "public, max-age=31536000, immutable";      // file names contain a content hash
        else if (path.StartsWith("/downloads/", StringComparison.OrdinalIgnoreCase))
            h.CacheControl = "no-cache";                                  // agent packages: always the current one
        else
        {
            // index.html (also served for every console route) and other top-level files: always check with the server.
            h.CacheControl = "no-cache, no-store, must-revalidate";
            h.Pragma = "no-cache";
            h.Expires = "0";
        }
    }
}
