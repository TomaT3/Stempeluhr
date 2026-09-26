using System.Text.RegularExpressions;

namespace Stempeluhr.Api.Middleware;

/// <summary>
/// Cache-Control for everything served from wwwroot. Only files whose name
/// carries a build hash (main-ABCD1234.js, styles-ABCD1234.css, ...) change
/// their name on every build and may be cached forever. Everything else keeps
/// its name across deploys and must be revalidated: index.html (the kiosk
/// would otherwise keep the old app), the service worker files and the Pi
/// agent manifest under /pi/ (the terminals would never see an update).
/// </summary>
public static partial class StaticFileCachePolicy
{
    public const string Immutable = "public, max-age=31536000, immutable";
    public const string Revalidate = "no-cache";

    public static string GetCacheControl(string fileName) =>
        HashedFileName().IsMatch(fileName) ? Immutable : Revalidate;

    public static void Apply(Microsoft.AspNetCore.StaticFiles.StaticFileResponseContext context)
    {
        context.Context.Response.Headers.CacheControl = GetCacheControl(context.File.Name);
    }

    // Angular (esbuild) appends an 8-character uppercase hash: main-CNEKRQK7.js.
    [GeneratedRegex(@"-[A-Z0-9]{8,}\.(js|css|woff2?|ttf|svg|png|jpe?g|webp)$")]
    private static partial Regex HashedFileName();
}
