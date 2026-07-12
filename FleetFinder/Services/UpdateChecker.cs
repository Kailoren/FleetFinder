using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace FleetView.Services;

/// <summary>
/// Checks the GitHub Releases API for a newer published version than the one currently running,
/// so the app can prompt in-app instead of relying on manual forum/Reddit announcements.
/// </summary>
public static class UpdateChecker
{
    private const string ReleasesUrl = "https://api.github.com/repos/Kailoren/FleetFinder/releases/latest";
    private static readonly HttpClient Http = CreateClient();

    public sealed record UpdateInfo(Version Version, string DisplayVersion, string HtmlUrl);

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub's API rejects requests with no User-Agent header.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("FleetFinder-UpdateCheck");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>
    /// Returns info about a newer release, or null if already current or the check failed for any
    /// reason (offline, rate-limited, malformed response, no releases yet). Never throws — a
    /// failed check should be silently invisible to the user, same as this app's other optional
    /// network calls (EDSM distances, relay listings).
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var stream = await Http.GetStreamAsync(ReleasesUrl, ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
            var tag = tagEl.GetString();
            if (string.IsNullOrWhiteSpace(tag)) return null;

            // Release tags are "vX.Y.Z" (see the tagging convention in FleetView.csproj's Version).
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            if (latest <= current) return null;

            string htmlUrl = root.TryGetProperty("html_url", out var urlEl)
                ? urlEl.GetString() ?? "https://github.com/Kailoren/FleetFinder/releases/latest"
                : "https://github.com/Kailoren/FleetFinder/releases/latest";

            return new UpdateInfo(latest, tag, htmlUrl);
        }
        catch
        {
            return null;
        }
    }
}
