using FleetView.Models;

namespace FleetView.Services;

/// <summary>Which side of the market to look at.</summary>
public enum MarketDirection
{
    /// <summary>Carriers selling the component (you buy from them).</summary>
    Selling,
    /// <summary>Carriers buying the component (you sell to them).</summary>
    Buying
}

/// <summary>
/// Abstraction over "where do I get live fleet-carrier market data for a component". Backed by
/// FleetView.Relay, our own EDDN-derived database - kept as an abstraction so the UI never
/// depends on how that data is actually fetched.
/// </summary>
public interface ICarrierMarketSource
{
    /// <summary>
    /// Returns current fleet-carrier/station offers for the given components and direction, as
    /// a single combined request where the underlying source supports it (a 15-component search
    /// still costs just one request, not fifteen).
    /// Implementations should cache and rate-limit to be a good citizen of the data source.
    /// </summary>
    Task<IReadOnlyList<CarrierListing>> GetListingsAsync(
        IReadOnlyList<Component> components, MarketDirection direction, CancellationToken ct = default);
}
