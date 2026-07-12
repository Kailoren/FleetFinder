namespace FleetView.Services;

public readonly record struct SystemCoords(double X, double Y, double Z);

/// <summary>Resolves star-system names to galactic coordinates (for distance calculations).</summary>
public interface ICoordinateSource
{
    /// <summary>
    /// Returns coordinates for as many of the requested systems as can be resolved,
    /// keyed by <see cref="ShipLockerReader.Normalize"/>d system name. Missing systems are omitted.
    /// </summary>
    Task<IReadOnlyDictionary<string, SystemCoords>> GetCoordsAsync(
        IEnumerable<string> systemNames, CancellationToken ct = default);
}
