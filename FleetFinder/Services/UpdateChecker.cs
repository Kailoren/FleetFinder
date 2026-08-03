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

    /// <summary>Where the banner goes when the API's own <c>html_url</c> is missing or fails
    /// <see cref="SafeReleaseUrl"/>. Constant, so this is always somewhere sane.</summary>
    private const string ReleasesPageUrl = "https://github.com/Kailoren/FleetFinder/releases/latest";

    private const string ReleasesHost = "github.com";

    /// <summary>Comfortably above any real release payload, and small enough that a hostile or
    /// broken answer cannot be read into memory unbounded.</summary>
    private const long MaxResponseBytes = 1 * 1024 * 1024;

    /// <summary>A tag is "v2.2.7"-shaped. Anything longer is not a version, and this string is
    /// displayed, so it gets a ceiling rather than being trusted to be short.</summary>
    private const int MaxDisplayVersionLength = 24;

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
    /// reason (offline, rate-limited, malformed response, no releases yet). Never throws - a
    /// failed check should be silently invisible to the user, same as this app's other optional
    /// network calls (EDSM distances, relay listings).
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await BoundedHttp
                .GetStringAsync(Http, ReleasesUrl, MaxResponseBytes, ct)
                .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!root.TryGetProperty("tag_name", out var tagEl)
                || tagEl.ValueKind != JsonValueKind.String) return null;
            var tag = tagEl.GetString();
            if (string.IsNullOrWhiteSpace(tag)) return null;

            // Release tags are "vX.Y.Z" (see the tagging convention in FleetView.csproj's Version).
            if (!Version.TryParse(StripVersionPrefix(tag), out var latest)) return null;

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            if (latest <= current) return null;

            string htmlUrl = root.TryGetProperty("html_url", out var urlEl)
                && urlEl.ValueKind == JsonValueKind.String
                ? SafeReleaseUrl(urlEl.GetString())
                : ReleasesPageUrl;

            return new UpdateInfo(latest, DisplayVersionFor(tag, latest), htmlUrl);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Removes a single leading "v". <c>TrimStart('v', 'V')</c> removed every leading one, so
    /// "vvvV9.9.9" parsed as a release, and a version this app will offer to send a user to should
    /// be a version and not merely something a lenient parser accepted.
    /// </summary>
    private static string StripVersionPrefix(string tag) =>
        tag.Length > 0 && (tag[0] == 'v' || tag[0] == 'V') ? tag[1..] : tag;

    /// <summary>
    /// Returns <paramref name="value"/> only if it is an absolute https URL on GitHub's own host;
    /// anything else falls back to <see cref="ReleasesPageUrl"/>.
    /// </summary>
    /// <remarks>
    /// This value's whole purpose is to be handed to the shell when the user clicks the update
    /// banner, so what it is allowed to be matters more than that it is non-null - the null check
    /// this replaced let through <c>file://</c>, a UNC path and any registered custom scheme
    /// alike. MainViewModel.OpenUpdate checks the same thing again at the point it opens: this is
    /// a value from someone else's server that gets stored, and the two checks are one apiece for
    /// the trust boundary it crosses and the action it ends at.
    /// </remarks>
    private static string SafeReleaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ReleasesPageUrl;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return ReleasesPageUrl;
        if (uri.Scheme != Uri.UriSchemeHttps) return ReleasesPageUrl;

        bool onGitHub = uri.Host.Equals(ReleasesHost, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + ReleasesHost, StringComparison.OrdinalIgnoreCase);
        return onGitHub ? value : ReleasesPageUrl;
    }

    /// <summary>
    /// The tag as shown in the update banner, reduced to characters a version tag can contain and
    /// capped at <see cref="MaxDisplayVersionLength"/>. Falls back to the parsed version when
    /// nothing usable survives, so the banner always names something.
    /// </summary>
    private static string DisplayVersionFor(string tag, Version parsed)
    {
        var kept = new string(tag
            .Where(c => char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-' || c == '+' || c == '_')
            .Take(MaxDisplayVersionLength)
            .ToArray());

        return string.IsNullOrWhiteSpace(kept) ? "v" + parsed.ToString(3) : kept;
    }
}
