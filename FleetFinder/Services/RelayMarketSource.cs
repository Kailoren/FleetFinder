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
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient Http = CreateClient();

    private readonly string _baseUrl;

    public RelayMarketSource(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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
        string url = $"{_baseUrl}/listings?keys={keys}&direction={dir}";

        await using var stream = await Http.GetStreamAsync(url, ct).ConfigureAwait(false);
        var dtos = await JsonSerializer.DeserializeAsync<List<ListingDto>>(stream, JsonOptions, ct)
            .ConfigureAwait(false);
        if (dtos is null) return Array.Empty<CarrierListing>();

        var now = DateTime.Now;
        return dtos.Select(d =>
        {
            var updatedLocal = d.UpdatedAt.ToLocalTime();
            var age = now - updatedLocal;
            return new CarrierListing
            {
                Component = d.Component,
                StationName = d.StationName,
                Callsign = d.Callsign,
                System = d.System,
                Direction = d.Direction,
                Amount = d.Amount,
                Price = d.Price,
                UpdatedAt = updatedLocal,
                Age = age,
                UpdatedText = FormatAge(age),
                DockingAccess = d.DockingAccess,
            };
        }).ToList();
    }

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

    /// <summary>Mirrors the JSON shape returned by FleetView.Relay's GET /listings endpoint.</summary>
    private sealed record ListingDto(
        string Component, string StationName, string Callsign, string System,
        string Direction, int Amount, long Price, DateTime UpdatedAt, string DockingAccess);
}
