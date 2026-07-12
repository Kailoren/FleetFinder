using Microsoft.Data.Sqlite;

namespace FleetView.Relay.Storage;

/// <summary>One row of a fleet-carrier's Odyssey-materials market, joined with its carrier info.</summary>
public sealed record ListingRow(
    string Component, string StationName, string Callsign, string System,
    string Direction, int Amount, long Price, DateTime UpdatedAt, string DockingAccess);

/// <summary>
/// SQLite-backed store for EDDN-derived carrier/market data. A new <see cref="SqliteConnection"/>
/// is opened per call (pooled under the hood by the same connection string) rather than sharing
/// one connection across threads, since ingestion (background service) and queries (HTTP requests)
/// happen concurrently and <see cref="SqliteConnection"/> isn't safe to share across threads.
/// </summary>
public sealed class RelayDb
{
    private readonly string _connectionString;

    public RelayDb(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        using var conn = Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();

        using var create = conn.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS Carriers (
                MarketId INTEGER PRIMARY KEY,
                Callsign TEXT,
                CarrierName TEXT,
                StarSystem TEXT,
                DockingAccess TEXT,
                LastSeenUtc TEXT
            );

            CREATE TABLE IF NOT EXISTS MaterialListings (
                MarketId INTEGER NOT NULL,
                ComponentKey TEXT NOT NULL,
                ComponentName TEXT NOT NULL,
                Direction TEXT NOT NULL,
                Amount INTEGER NOT NULL,
                Price INTEGER NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                PRIMARY KEY (MarketId, ComponentKey, Direction)
            );

            CREATE INDEX IF NOT EXISTS IX_MaterialListings_ComponentKey
                ON MaterialListings (ComponentKey, Direction);
            """;
        create.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>Upserts carrier identity/location fields learned from a commodity-v3 message.</summary>
    public void UpsertCarrierFromCommodity(
        long marketId, string callsign, string starSystem, string dockingAccess, DateTime lastSeenUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Carriers (MarketId, Callsign, StarSystem, DockingAccess, LastSeenUtc)
            VALUES ($marketId, $callsign, $starSystem, $dockingAccess, $lastSeen)
            ON CONFLICT(MarketId) DO UPDATE SET
                Callsign = excluded.Callsign,
                StarSystem = excluded.StarSystem,
                DockingAccess = excluded.DockingAccess,
                LastSeenUtc = excluded.LastSeenUtc;
            """;
        cmd.Parameters.AddWithValue("$marketId", marketId);
        cmd.Parameters.AddWithValue("$callsign", callsign);
        cmd.Parameters.AddWithValue("$starSystem", starSystem);
        cmd.Parameters.AddWithValue("$dockingAccess", dockingAccess);
        cmd.Parameters.AddWithValue("$lastSeen", lastSeenUtc.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Upserts a carrier's Callsign/StarSystem learned from a "journal/1" Docked/CarrierJump
    /// message - a much more frequent location source than commodity-v3 (fires on every dock,
    /// not just when a carrier's commodity market happens to get uploaded). Deliberately doesn't
    /// touch DockingAccess itself (see <see cref="UpsertCarrierDockingAccessFallback"/> for that)
    /// or CarrierName - this schema doesn't carry docking-access info at all, only commodity-v3
    /// does, and CarrierName is FCMaterials' job (see UpsertCarrierName).
    /// </summary>
    public void UpsertCarrierLocation(long marketId, string callsign, string starSystem, DateTime lastSeenUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Carriers (MarketId, Callsign, StarSystem, LastSeenUtc)
            VALUES ($marketId, $callsign, $starSystem, $lastSeen)
            ON CONFLICT(MarketId) DO UPDATE SET
                Callsign = excluded.Callsign,
                StarSystem = excluded.StarSystem,
                LastSeenUtc = excluded.LastSeenUtc;
            """;
        cmd.Parameters.AddWithValue("$marketId", marketId);
        cmd.Parameters.AddWithValue("$callsign", callsign);
        cmd.Parameters.AddWithValue("$starSystem", starSystem);
        cmd.Parameters.AddWithValue("$lastSeen", lastSeenUtc.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Sets a carrier's DockingAccess only if it isn't already known from any source - a
    /// lower-confidence fallback from a successful Docked/CarrierJump event (proves *someone*
    /// could dock, not that access is open to everyone - a friends/squadron-only carrier can
    /// still show successful dock events from actual friends/squadron members) used only while
    /// commodity-v3's authoritative access *policy* hasn't been observed yet for this carrier.
    /// Never overwrites an existing value, from any source, including a previous call to this
    /// same method - once something (soft or authoritative) is known, this is a no-op.
    /// </summary>
    public void UpsertCarrierDockingAccessFallback(long marketId, string dockingAccess, DateTime lastSeenUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Carriers (MarketId, DockingAccess, LastSeenUtc)
            VALUES ($marketId, $dockingAccess, $lastSeen)
            ON CONFLICT(MarketId) DO UPDATE SET
                DockingAccess = CASE
                    WHEN DockingAccess IS NULL OR DockingAccess = '' THEN excluded.DockingAccess
                    ELSE DockingAccess
                END,
                LastSeenUtc = excluded.LastSeenUtc;
            """;
        cmd.Parameters.AddWithValue("$marketId", marketId);
        cmd.Parameters.AddWithValue("$dockingAccess", dockingAccess);
        cmd.Parameters.AddWithValue("$lastSeen", lastSeenUtc.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Upserts the carrier's owner-chosen display name, learned from an FCMaterials message.</summary>
    public void UpsertCarrierName(long marketId, string? carrierName, DateTime lastSeenUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Carriers (MarketId, CarrierName, LastSeenUtc)
            VALUES ($marketId, $carrierName, $lastSeen)
            ON CONFLICT(MarketId) DO UPDATE SET
                CarrierName = COALESCE(excluded.CarrierName, Carriers.CarrierName),
                LastSeenUtc = excluded.LastSeenUtc;
            """;
        cmd.Parameters.AddWithValue("$marketId", marketId);
        cmd.Parameters.AddWithValue("$carrierName", (object?)carrierName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lastSeen", lastSeenUtc.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Upserts one commodity's current stock/demand at one carrier. Callers pass every item from
    /// a fresh FCMaterials report, including ones now at 0 - this method decides what to do with
    /// a 0: if the row already exists (this carrier was previously seen offering it), it's
    /// updated to 0 rather than left stale at its last known positive value, which is what
    /// previously caused the app to keep suggesting a carrier for a component it had actually sold
    /// out of. If the row doesn't exist yet, a 0 is NOT inserted - that would just be database
    /// bloat for "this carrier's bartender lists this material at all, currently with none",
    /// which every carrier's price list technically enumerates for every catalog item regardless
    /// of whether it's ever actually stocked.
    /// </summary>
    public void UpsertMaterialListing(
        long marketId, string componentKey, string componentName, string direction,
        int amount, long price, DateTime updatedUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        if (amount > 0)
        {
            cmd.CommandText = """
                INSERT INTO MaterialListings
                    (MarketId, ComponentKey, ComponentName, Direction, Amount, Price, UpdatedUtc)
                VALUES
                    ($marketId, $key, $name, $direction, $amount, $price, $updated)
                ON CONFLICT(MarketId, ComponentKey, Direction) DO UPDATE SET
                    ComponentName = excluded.ComponentName,
                    Amount = excluded.Amount,
                    Price = excluded.Price,
                    UpdatedUtc = excluded.UpdatedUtc;
                """;
        }
        else
        {
            cmd.CommandText = """
                UPDATE MaterialListings
                SET ComponentName = $name, Amount = $amount, Price = $price, UpdatedUtc = $updated
                WHERE MarketId = $marketId AND ComponentKey = $key AND Direction = $direction;
                """;
        }
        cmd.Parameters.AddWithValue("$marketId", marketId);
        cmd.Parameters.AddWithValue("$key", componentKey);
        cmd.Parameters.AddWithValue("$name", componentName);
        cmd.Parameters.AddWithValue("$direction", direction);
        cmd.Parameters.AddWithValue("$amount", amount);
        cmd.Parameters.AddWithValue("$price", price);
        cmd.Parameters.AddWithValue("$updated", updatedUtc.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Listings for any of the given normalized component keys, joined with carrier info. Only
    /// returns rows for carriers whose Callsign and StarSystem are both already known (i.e. a
    /// commodity-v3 message has been observed for that MarketID) - a result with no location
    /// isn't actionable, so it's excluded here rather than shown with blank fields. Once a
    /// carrier's location becomes known, its already-stored material listings start being
    /// returned automatically on the next query, no re-ingestion needed.
    /// </summary>
    public IReadOnlyList<ListingRow> QueryListings(IReadOnlyList<string> componentKeys, string direction)
    {
        if (componentKeys.Count == 0) return Array.Empty<ListingRow>();

        using var conn = Open();
        using var cmd = conn.CreateCommand();

        var placeholders = new string[componentKeys.Count];
        for (int i = 0; i < componentKeys.Count; i++)
        {
            string p = $"$k{i}";
            placeholders[i] = p;
            cmd.Parameters.AddWithValue(p, componentKeys[i]);
        }
        cmd.Parameters.AddWithValue("$direction", direction);

        cmd.CommandText = $"""
            SELECT m.ComponentName, COALESCE(c.CarrierName, c.Callsign, ''), COALESCE(c.Callsign, ''),
                   COALESCE(c.StarSystem, ''), m.Direction, m.Amount, m.Price, m.UpdatedUtc,
                   COALESCE(c.DockingAccess, 'Unknown')
            FROM MaterialListings m
            JOIN Carriers c ON c.MarketId = m.MarketId
            WHERE m.ComponentKey IN ({string.Join(",", placeholders)})
              AND m.Direction = $direction
              AND m.Amount > 0
              AND c.Callsign IS NOT NULL AND c.Callsign != ''
              AND c.StarSystem IS NOT NULL AND c.StarSystem != '';
            """;

        using var reader = cmd.ExecuteReader();
        var results = new List<ListingRow>();
        while (reader.Read())
        {
            results.Add(new ListingRow(
                Component: reader.GetString(0),
                StationName: reader.GetString(1),
                Callsign: reader.GetString(2),
                System: reader.GetString(3),
                Direction: reader.GetString(4),
                Amount: reader.GetInt32(5),
                Price: reader.GetInt64(6),
                UpdatedAt: DateTime.Parse(reader.GetString(7)).ToUniversalTime(),
                DockingAccess: reader.GetString(8)));
        }
        return results;
    }
}
