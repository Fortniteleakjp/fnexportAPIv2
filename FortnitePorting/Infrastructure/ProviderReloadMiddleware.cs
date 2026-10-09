namespace FortnitePorting.Services;

public sealed class ProviderReloadMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path == "/" || path.StartsWithSegments("/swagger") || path.StartsWithSegments("/api/v1/build") ||
            path.StartsWithSegments("/api/v1/changes") || path.StartsWithSegments("/api/v1/versions") || ReadsArchivedBuild(context))
        {
            await next(context);
            return;
        }

        var gate = ProviderReloadGate.Instance;
        if (!gate.TryEnter())
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "30";
            await context.Response.WriteAsJsonAsync(new
            {
                status = gate.State,
                message = "The API is reloading the latest Fortnite build. Retry shortly.",
                statusEndpoint = "/api/v1/build"
            });
            return;
        }
        try { await next(context); }
        finally { gate.Exit(); }
    }

    private static bool ReadsArchivedBuild(HttpContext context)
    {
        var requested = context.Request.Query[VersionParameterFilter.VersionParameter].ToString();
        if (string.IsNullOrWhiteSpace(requested)) return false;
        try
        {
            var diffs = context.RequestServices.GetService<BuildDiffService>();
            var version = diffs?.ResolveBuildVersion(requested);
            if (version == null || diffs!.IsLive(version) || !diffs.TryLease(version, out var lease)) return false;
            lease.Dispose();
            return true;
        }
        catch { return false; }
    }
}
