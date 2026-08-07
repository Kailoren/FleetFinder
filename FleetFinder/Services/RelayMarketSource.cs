using System.Net.Http;
using System.Text.Json;
using FleetView.Models;

namespace FleetView.Services;

/// <summary>
/// Fetches fleet-carrier component-market data from our own FleetView.Relay service (an EDDN
/// listener + SQLite database we run ourselves). The only <see cref="ICarrierMarketSource"/>
/// implementation this app uses against real data - no Inara-scraping path exists.
/// </summary>
public sealed class RelayMarketSource : ICarrierMarketSource
{
    /// <summary>
    /// Generously above any real catalog-wide response - guards against a compromised or
    /// man-in-the-middled relay response driving an unbounded deserialization. Enforced while
    /// reading (see <see cref="BoundedHttp.Limit"/>) rather than from <c>Content-Length</c>, which
    /// is absent on a chunked reply and is the sender's claim about itself in any case.
    /// </summary>
    private const long MaxResponseBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Ceiling on rows accepted from one answer. The byte limit above does not bound this on its
    /// own: a minimal JSON object is a few dozen bytes, so eight megabytes of them is six figures
    /// of rows, each expanded here into an object and a formatted string and then handed to a grid
    /// to lay out. The real catalog across every carrier is orders of magnitude under this.
    /// </summary>
    private const int MaxListings = 20_000;

    /// <summary>
    /// Ceiling on any single text field. Carrier names, system names and callsigns are all short;
    /// the length of what actually arrives is the sender's choice, and these strings are rendered.
    /// </summary>
    private const int MaxFieldLength = 120;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient Http = CreateClient();

    private readonly string _baseUrl;

    /// <param name="baseUrl">
    /// Must satisfy <see cref="TryNormaliseBaseUrl"/>. Callers taking this from configuration
    /// should normalise first and decide for themselves what to do with a value that fails,
    /// rather than letting the constructor throw during startup.
    /// </param>
    public RelayMarketSource(string baseUrl)
    {
        if (!TryNormaliseBaseUrl(baseUrl, out _baseUrl))
            throw new ArgumentException(
                "Relay base URL must be an absolute https:// URL (or http:// on loopback) with no " +
                "query, fragment or user info.", nameof(baseUrl));
    }

    /// <summary>
    /// Accepts an absolute https URL, or an http one pointing at loopback so a relay running on
    /// this machine can still be tested against. Rejects user info, a query and a fragment, and
    /// returns the scheme/host/path with any trailing slash removed.
    /// </summary>
    /// <remarks>
    /// The request URL is built by appending to this, so a value carrying its own query would
    /// silently demote the parameters meant for the relay into part of somebody else's - and the
    /// scheme decides whether the whole exchange is encrypted at all. Neither was checked before:
    /// the string was taken as given and only trimmed. This is reachable from configuration, so
    /// what it is allowed to be is worth stating rather than assuming.
    /// </remarks>
    public static bool TryNormaliseBaseUrl(string? value, out string normalised)
    {
        normalised = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return false;

        bool schemeOk = uri.Scheme == Uri.UriSchemeHttps
                        || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        if (!schemeOk) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;

        normalised = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return normalised.Length > 0;
    }

    public string SourceDescription => _baseUrl;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(BoundedHttp.CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }

    public async Task<IReadOnlyList<CarrierListing>> GetListingsAsync(
        IReadOnlyList<Component> components, MarketDirection direction, CancellationToken ct = default)
    {
        if (components.Count == 0)
            return Array.Empty<CarrierListing>();

        string keys = string.Join(",", components.Select(c => Uri.EscapeDataString(c.Key)));
        string dir = direction == MarketDirection.Selling ? "selling" : "buying";
        var requested = new Uri($"{_baseUrl}/listings?keys={keys}&direction={dir}", UriKind.Absolute);

        // The body read is bounded independently of the caller's token: HttpClient.Timeout stops
        // applying once the headers arrive under ResponseHeadersRead, and a byte limit can only
        // fire on bytes that arrive, so neither bounds a relay that answers and then stalls.
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(BoundedHttp.DefaultDeadline);

        // The https-only base URL checked in the constructor only decides where the first request
        // goes; a redirect would otherwise deliver the answer from anywhere. BoundedHttp.GetAsync
        // checks each hop before following it, rather than the chain being walked inside HttpClient
        // and only the destination being judged afterwards.
        using var response = await BoundedHttp.GetAsync(Http, requested, timed.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        BoundedHttp.RejectUnusable(response, requested, MaxResponseBytes);

        await using var stream = await response.Content.ReadAsStreamAsync(timed.Token).ConfigureAwait(false);
        await using var bounded = BoundedHttp.Limit(stream, MaxResponseBytes);

        var now = DateTime.Now;
        var listings = new List<CarrierListing>();
        bool truncated = false;

        // Streamed rather than deserialized into a List first. MaxListings used to be checked in
        // the loop below, which is after the whole array already exists as objects - eight megabytes
        // of minimal JSON is six figures of rows, so the cap that was meant to bound the work was
        // only ever bounding what got kept. Stopping the enumeration stops the parse.
        var rows = JsonSerializer.DeserializeAsyncEnumerable<ListingDto>(bounded, JsonOptions, timed.Token);
        await foreach (var d in rows.ConfigureAwait(false))
        {
            if (listings.Count >= MaxListings)
            {
                truncated = true;
                break;
            }
            if (d is null) continue;

            // Nothing between the deserializer and here decided these were present. The DTO
            // declares them non-nullable and System.Text.Json does not enforce that, so a reply of
            // [{"component":null}] deserializes cleanly into a record whose every string is null,
            // and those nulls would reach the grid as the fields it binds and formats.
            var component = Clean(d.Component);
            if (component.Length == 0) continue; // a row naming no component is not a result

            var updatedLocal = d.UpdatedAt.ToLocalTime();
            var age = now - updatedLocal;

            listings.Add(new CarrierListing
            {
                Component = component,
                StationName = Clean(d.StationName),
                Callsign = Clean(d.Callsign),
                System = Clean(d.System),
                Direction = Clean(d.Direction),
                Amount = Math.Max(0, d.Amount),
                Price = Math.Max(0, d.Price),
                UpdatedAt = updatedLocal,
                Age = age,
                UpdatedText = FormatAge(age),
                DockingAccess = Clean(d.DockingAccess) is { Length: > 0 } access ? access : "Unknown",
            });
        }

        // Reports what was kept, not what was sent: the parse stops at the cap, so how many rows
        // the relay actually had is no longer something this app finds out. Saying so is the point
        // of the note - silently keeping a prefix reads as a complete answer.
        if (truncated)
            DiagnosticLog.Note(
                $"Relay returned more than {MaxListings:N0} rows for {dir}; kept the first " +
                $"{MaxListings:N0} and stopped reading.");

        return listings;
    }

    /// <summary>
    /// Reduces one field of an answer to something safe to put in a grid cell: never null, capped
    /// at <see cref="MaxFieldLength"/>, and with the invisible formatting characters removed. Those
    /// can reorder a line as displayed without changing the text, which is worth denying in a name
    /// shown next to a price the user is about to fly to. See <see cref="SafeText"/> for the set.
    /// </summary>
    private static string Clean(string? value) => SafeText.Clean(value, MaxFieldLength);

    /// <summary>Turns a TimeSpan into friendly text, e.g. "11 minutes ago".</summary>
    public static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalSeconds < 60) return "just now";
        if (age.TotalMinutes < 60) return Plural((int)age.TotalMinutes, "minute");
        if (age.TotalHours < 24) return Plural((int)age.TotalHours, "hour");
        if (age.TotalDays < 30) return Plural((int)age.TotalDays, "day");
        return Plural((int)(age.TotalDays / 30), "month");
    }

    private static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";

    /// <summary>
    /// Mirrors the JSON shape returned by FleetView.Relay's GET /listings endpoint.
    /// </summary>
    /// <remarks>
    /// Every string is nullable, and that is the honest declaration rather than a defensive one:
    /// System.Text.Json will happily write a JSON null into a non-nullable string property without
    /// complaint, so declaring these non-null only made the compiler stop asking about a case that
    /// can still happen. <see cref="Clean"/> is where they become non-null.
    /// </remarks>
    private sealed record ListingDto(
        string? Component, string? StationName, string? Callsign, string? System,
        string? Direction, int Amount, long Price, DateTime UpdatedAt, string? DockingAccess);
}
