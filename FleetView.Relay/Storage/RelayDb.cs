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

    // EDDN content is untrusted and carries no length limit of its own beyond the whole-message
    // cap in EddnListener. Real values here (callsigns, system names, carrier/component names) are
    // always short - clamping at write time keeps a hostile or malformed message from bloating the
    // database or being echoed back to every FleetFinder client unbounded.
    //
    // The cut is backed off a character when it would land between a surrogate pair. A .NET string
    // is UTF-16, so a name whose 128th code unit is the first half of an emoji or a supplementary
    // character would otherwise be stored ending in a lone surrogate - not well-formed text, and
    // handed on verbatim by QueryListings to every client that asks.
    private static string? Clamp(string? s, int maxLen)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s;
        int cut = maxLen;
        if (char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut];
    }

    /// <summary>
    /// Longest component key stored. Keys are already normalised to lower-case alphanumerics by
    /// <see cref="ComponentKey"/>, and the real catalog's longest is 25 characters; the listings
    /// endpoint drops anything over this length before querying, so a longer key could only ever
    /// occupy a primary key nothing can look up again. Rejected rather than truncated - truncating
    /// would file a row under a key that is not the one reported.
    /// </summary>
    private const int MaxComponentKeyLength = 64;

    /// <summary>The only two directions this schema has. Both are chosen by the calling handler
    /// rather than read from a message, so this is a guard against a future caller, not against
    /// the feed - but it is the other half of the primary key and it was the one field arriving
    /// unchecked.</summary>
    private static bool IsKnownDirection(string direction) =>
        direction is "Selling" or "Buying";

    /// <summary>Upserts carrier identity/location fields learned from a commodity-v3 message.</summary>
    public void UpsertCarrierFromCommodity(
        long marketId, string callsign, string starSystem, string dockingAccess, DateTime lastSeenUtc)
    {
        callsign = Clamp(callsign, 16) ?? "";
        starSystem = Clamp(starSystem, 128) ?? "";

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
        callsign = Clamp(callsign, 16) ?? "";
        starSystem = Clamp(starSystem, 128) ?? "";

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
    /// Sets a carrier's DockingAccess to a soft "No" from a real docking denial
    /// (<see cref="Eddn.DockingDeniedHandler"/>, its only caller now that the old Docked-based
    /// soft "Yes" fallback has been removed). Unlike that removed fallback, this one always
    /// overwrites - including over an existing authoritative "Yes" - because a denial is direct,
    /// unambiguous proof this exact player couldn't get in, which outweighs any prior belief
    /// (whether it's stale because the owner changed policy since, or a leftover stale "Yes" from
    /// the old removed fallback that was deliberately never retroactively cleared).
    /// </summary>
    public void UpsertCarrierDockingAccessFallback(long marketId, string dockingAccess, DateTime lastSeenUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Carriers (MarketId, DockingAccess, LastSeenUtc)
            VALUES ($marketId, $dockingAccess, $lastSeen)
            ON CONFLICT(MarketId) DO UPDATE SET
                DockingAccess = excluded.DockingAccess,
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
        carrierName = Clamp(carrierName, 128);

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
        // Both halves of the primary key are checked here rather than assumed. ComponentKey is the
        // one untrusted value in this method that Clamp never covered, and it is the field the row
        // is filed under - a bad one is not a long string in a column, it is a row nothing can find.
        if (componentKey.Length == 0 || componentKey.Length > MaxComponentKeyLength) return;
        if (!IsKnownDirection(direction)) return;

        componentName = Clamp(componentName, 128) ?? "";

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        if (amount > 0)
        {
            // The UpdatedUtc comparison is what stops a replayed or back-dated message from
            // overwriting fresher data. EDDN messages carry their own timestamp and are not
            // authenticated, so the feed decides the value this ordering is judged on; refusing to
            // go backwards is the part that can be enforced here.
            cmd.CommandText = """
                INSERT INTO MaterialListings
                    (MarketId, ComponentKey, ComponentName, Direction, Amount, Price, UpdatedUtc)
                VALUES
                    ($marketId, $key, $name, $direction, $amount, $price, $updated)
                ON CONFLICT(MarketId, ComponentKey, Direction) DO UPDATE SET
                    ComponentName = excluded.ComponentName,
                    Amount = excluded.Amount,
                    Price = excluded.Price,
                    UpdatedUtc = excluded.UpdatedUtc
                WHERE excluded.UpdatedUtc >= MaterialListings.UpdatedUtc;
                """;
        }
        else
        {
            cmd.CommandText = """
                UPDATE MaterialListings
                SET ComponentName = $name, Amount = $amount, Price = $price, UpdatedUtc = $updated
                WHERE MarketId = $marketId AND ComponentKey = $key AND Direction = $direction
                  AND $updated >= UpdatedUtc;
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
    /// Zeroes out any existing listing for this carrier/direction whose component key wasn't
    /// present in the fresh FCMaterials report just processed. A report enumerates every item
    /// currently on that side of the bartender - so a component that used to have a positive
    /// listing but is missing from the new report (not even at 0) means the market emptied out
    /// entirely, which the game apparently reports by omitting items rather than listing them at
    /// 0. Without this, a carrier that sells out completely keeps showing its last known stock
    /// forever, since <see cref="UpsertMaterialListing"/> only ever writes what's actually in a
    /// report. Only touches rows currently positive; never inserts, matching
    /// UpsertMaterialListing's own no-insert-at-zero rule.
    /// </summary>
    public void ClearUnreportedListings(
        long marketId, string direction, IReadOnlyCollection<string> reportedKeys, DateTime updatedUtc)
    {
        if (!IsKnownDirection(direction)) return;

        string updated = updatedUtc.ToString("o");

        using var conn = Open();

        // Which of this carrier's rows the report leaves out is worked out here rather than in SQL.
        // The obvious statement is one UPDATE with the reported keys in a NOT IN clause, but that
        // binds one parameter per reported key, and the report is an EDDN message whose item count
        // nothing bounds on the wire - past SQLite's own variable limit the statement is simply
        // refused. It also cannot be split: NOT IN over half the keys zeroes the rows named in the
        // other half, so several statements do not add up to the one they replace.
        //
        // Reading the keys that are actually stored inverts it into an IN clause over the rows that
        // really are stale, which is bounded by what this database already holds for one carrier
        // and one direction, and which does split cleanly across statements - each batch zeroes its
        // own rows and nothing else's.
        var stale = new List<string>();
        using (var existing = conn.CreateCommand())
        {
            existing.CommandText = """
                SELECT ComponentKey FROM MaterialListings
                WHERE MarketId = $marketId AND Direction = $direction AND Amount > 0
                  AND $updated >= UpdatedUtc;
                """;
            existing.Parameters.AddWithValue("$marketId", marketId);
            existing.Parameters.AddWithValue("$direction", direction);
            existing.Parameters.AddWithValue("$updated", updated);

            using var reader = existing.ExecuteReader();
            while (reader.Read())
            {
                string key = reader.GetString(0);
                if (!reportedKeys.Contains(key)) stale.Add(key);
            }
        }

        if (stale.Count == 0) return;

        // One transaction, so a report either clears everything it should or nothing: with the keys
        // split across several statements, a failure partway through would otherwise leave some of
        // this carrier's stale rows zeroed and the rest not.
        using var tx = conn.BeginTransaction();
        foreach (var batch in Batched(stale, MaxSqlParameters))
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            var placeholders = new string[batch.Count];
            for (int i = 0; i < batch.Count; i++)
            {
                string p = $"$k{i}";
                placeholders[i] = p;
                cmd.Parameters.AddWithValue(p, batch[i]);
            }
            cmd.CommandText = $"""
                UPDATE MaterialListings SET Amount = 0, UpdatedUtc = $updated
                WHERE MarketId = $marketId AND Direction = $direction AND Amount > 0
                  AND $updated >= UpdatedUtc
                  AND ComponentKey IN ({string.Join(",", placeholders)});
                """;
            cmd.Parameters.AddWithValue("$marketId", marketId);
            cmd.Parameters.AddWithValue("$direction", direction);
            cmd.Parameters.AddWithValue("$updated", updated);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// How many keys go into one <c>IN</c> clause. SQLite refuses a statement past its own variable
    /// limit - 999 on older builds, 32,766 since 3.32 - and this stays under the smaller of the two
    /// rather than depending on which one the deployed build enforces.
    /// </summary>
    private const int MaxSqlParameters = 500;

    private static IEnumerable<IReadOnlyList<string>> Batched(List<string> items, int size)
    {
        for (int i = 0; i < items.Count; i += size)
            yield return items.GetRange(i, Math.Min(size, items.Count - i));
    }

    /// <summary>
    /// Drops listing rows not refreshed since <paramref name="olderThanUtc"/>, and carriers left
    /// with no listings and no sighting since then.
    /// </summary>
    /// <remarks>
    /// Nothing else in this schema ever deletes. A carrier that stops broadcasting keeps its rows
    /// for good, and every distinct component key that has ever arrived from the feed keeps a row
    /// per carrier per direction - so the database's size is a function of everything EDDN has ever
    /// said rather than of what is currently true. Queries already exclude what is stale, which is
    /// why this had no visible symptom; the file growing without bound is the symptom.
    /// </remarks>
    public int PruneStale(DateTime olderThanUtc)
    {
        string cutoff = olderThanUtc.ToString("o");

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using var listings = conn.CreateCommand();
        listings.Transaction = tx;
        listings.CommandText = "DELETE FROM MaterialListings WHERE UpdatedUtc < $cutoff;";
        listings.Parameters.AddWithValue("$cutoff", cutoff);
        int removed = listings.ExecuteNonQuery();

        using var carriers = conn.CreateCommand();
        carriers.Transaction = tx;
        carriers.CommandText = """
            DELETE FROM Carriers
            WHERE LastSeenUtc < $cutoff
              AND MarketId NOT IN (SELECT DISTINCT MarketId FROM MaterialListings);
            """;
        carriers.Parameters.AddWithValue("$cutoff", cutoff);
        removed += carriers.ExecuteNonQuery();

        tx.Commit();
        return removed;
    }

    /// <summary>
    /// Listings for any of the given normalized component keys, joined with carrier info. Only
    /// returns rows for carriers whose Callsign and StarSystem are both already known (i.e. a
    /// commodity-v3 message has been observed for that MarketID) - a result with no location
    /// isn't actionable, so it's excluded here rather than shown with blank fields. Once a
    /// carrier's location becomes known, its already-stored material listings start being
    /// returned automatically on the next query, no re-ingestion needed.
    ///
    /// Also requires DockingAccess = 'Yes' and a resolved CarrierName (not just its callsign
    /// standing in for the name) - both are lower-coverage fields than location, so this trades
    /// result volume for confidence: every returned row is a carrier you're actually known to be
    /// able to dock at, with its real name shown rather than a callsign duplicated into both
    /// columns.
    /// </summary>
    public IReadOnlyList<ListingRow> QueryListings(IReadOnlyList<string> componentKeys, string direction)
    {
        if (componentKeys.Count == 0) return Array.Empty<ListingRow>();
        if (!IsKnownDirection(direction)) return Array.Empty<ListingRow>();

        // ListingsEndpoint already caps a request at 200 keys, well under this. Repeated here so
        // the statement this method builds is bounded by this method, rather than by a constant in
        // the one caller that happens to exist today.
        if (componentKeys.Count > MaxSqlParameters)
            componentKeys = componentKeys.Take(MaxSqlParameters).ToList();

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
            SELECT m.ComponentName, c.CarrierName, COALESCE(c.Callsign, ''),
                   COALESCE(c.StarSystem, ''), m.Direction, m.Amount, m.Price, m.UpdatedUtc,
                   c.DockingAccess
            FROM MaterialListings m
            JOIN Carriers c ON c.MarketId = m.MarketId
            WHERE m.ComponentKey IN ({string.Join(",", placeholders)})
              AND m.Direction = $direction
              AND m.Amount > 0
              AND c.Callsign IS NOT NULL AND c.Callsign != ''
              AND c.StarSystem IS NOT NULL AND c.StarSystem != ''
              AND c.DockingAccess = 'Yes'
              AND c.CarrierName IS NOT NULL AND c.CarrierName != '';
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
