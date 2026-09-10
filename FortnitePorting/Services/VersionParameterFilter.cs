using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FortnitePorting.Services;

/// <summary>
/// Resolves the <c>version</c> query parameter on <see cref="VersionAwareAttribute"/> controllers and
/// points the request's <see cref="RequestBuildProvider"/> at that build for the duration of the
/// action, so every existing read endpoint can serve an older build without knowing anything about
/// the build archive.
/// </summary>
public sealed class VersionParameterFilter : IAsyncActionFilter
{
    /// <summary>Query parameter naming the build to read.</summary>
    public const string VersionParameter = "version";

    /// <summary>Query parameter that allows mounting an archived build that is not loaded yet.</summary>
    public const string LoadParameter = "loadVersion";

    private readonly BuildDiffService _diffs;
    private readonly HistoricalBuildService _historical;
    private readonly RequestBuildProvider _requestProvider;

    public VersionParameterFilter(BuildDiffService diffs, HistoricalBuildService historical,
        RequestBuildProvider requestProvider)
    {
        _diffs = diffs;
        _historical = historical;
        _requestProvider = requestProvider;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!context.Controller.GetType().IsDefined(typeof(VersionAwareAttribute), inherit: true))
        {
            await next();
            return;
        }

        var requested = context.HttpContext.Request.Query[VersionParameter].ToString();
        if (string.IsNullOrWhiteSpace(requested))
        {
            await next();   // no version named: the live build, exactly as before
            return;
        }

        var resolved = _diffs.ResolveBuildVersion(requested);
        if (resolved == null)
        {
            context.Result = new NotFoundObjectResult(new ProblemDetails
            {
                Title = "ビルドが見つかりません",
                Detail = $"No known build matches '{requested}'. Call GET /api/v1/versions to see what this instance has.",
                Status = StatusCodes.Status404NotFound,
                Extensions = { { "known", _diffs.KnownBuildVersions() } }
            });
            return;
        }

        var allowLoad = string.Equals(context.HttpContext.Request.Query[LoadParameter].ToString(),
            "true", StringComparison.OrdinalIgnoreCase);

        BuildLease lease;
        if (_diffs.TryLease(resolved, out var existing))
        {
            lease = existing;
        }
        else if (!allowLoad)
        {
            // Mounting a build takes minutes, so it is never done implicitly on a read: the caller is
            // told how to mount it, or can opt in per request.
            context.Result = new ObjectResult(new ProblemDetails
            {
                Title = "そのビルドは読み込まれていません",
                Detail = _historical.CanLoad(resolved)
                    ? $"Build '{resolved}' is archived but not loaded. Mount it with " +
                      $"POST /api/v1/versions/load?version={Uri.EscapeDataString(resolved)}, " +
                      $"or re-send this request with {LoadParameter}=true."
                    : $"Build '{resolved}' has no archived data left, so its files cannot be read. " +
                      "Its recorded changelists are still available under /api/v1/changes.",
                Status = StatusCodes.Status409Conflict,
                Extensions = { { "build", resolved }, { "loadable", _historical.CanLoad(resolved) } }
            })
            {
                StatusCode = StatusCodes.Status409Conflict
            };
            return;
        }
        else
        {
            try
            {
                lease = await _diffs.LeaseAsync(resolved, context.HttpContext.RequestAborted);
            }
            catch (InvalidOperationException ex)
            {
                context.Result = new NotFoundObjectResult(new ProblemDetails
                {
                    Title = "アーカイブされたビルドがありません",
                    Detail = ex.Message,
                    Status = StatusCodes.Status404NotFound
                });
                return;
            }
        }

        using (lease)
        {
            _requestProvider.Bind(lease);
            context.HttpContext.Response.Headers["X-Build-Version"] = resolved;
            context.HttpContext.Response.Headers["X-Build-Is-Live"] = _diffs.IsLive(resolved) ? "true" : "false";
            await next();
        }
    }
}
