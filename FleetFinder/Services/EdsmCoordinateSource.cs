using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace FleetView.Services;

/// <summary>
/// Resolves system coordinates via EDSM's public API (edsm.net), with an in-memory and
/// on-disk cache. System coordinates are fixed, so cached entries never expire.
///
/// This is a separate service from the carrier-market source; it is only used to compute
/// distances from the commander's current location.
/// </summary>
public sealed class EdsmCoordinateSource : ICoordinateSource
{
    private const int ChunkSize = 40;

    // Forty systems with coordinates is a few kilobytes. This is far above any real answer and
    // exists so a hostile or broken edsm.net cannot hand us an unbounded body: the names in it
    // are passed to ShipLockerReader.Normalize, which sizes a buffer from their length.
    private const long MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Ceiling on entries kept on disk. Coordinates are fixed, so there is nothing to expire and a
    /// time-to-live would only throw away correct answers - but the file grows for as long as the
    /// app is used and nothing else bounds it, and each entry's key comes from a reply rather than
    /// from us. This is the bound: past it the file stops growing rather than being rewritten
    /// larger every session.
    /// </summary>
    private const int MaxCachedSystems = 50_000;

    /// <summary>
    /// Ceiling on the cache file. <see cref="MaxCachedSystems"/> entries of a name and three
    /// numbers is a few megabytes at the outside; this is well past that and exists because
    /// <see cref="MaxCachedSystems"/> is applied to the deserialized result, which is one
    /// allocation too late to bound the read that produced it.
    /// </summary>
    private const long MaxCacheFileBytes = 16 * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Null when the cache directory could not be created, i.e. this session keeps its
    /// coordinates in memory only.</summary>
    private readonly string? _cachePath;

    /// <summary>
    /// Concurrent because the read path does not take <see cref="_gate"/>. That gate serialises
    /// writers against each other, but a second overlapping <see cref="GetCoordsAsync"/> reads
    /// this dictionary before ever reaching it, and a plain Dictionary being resized by a writer
    /// can make such a reader throw or walk a stale bucket chain. Nothing about this class's
    /// public surface forbids concurrent use, so the collection is the right place to fix it.
    /// </summary>
    private readonly ConcurrentDictionary<string, SystemCoords> _cache = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Prepares the on-disk cache, degrading to an in-memory one if the directory cannot be made.
    /// </summary>
    /// <remarks>
    /// Every other filesystem operation in this class catches and carries on; this one used to
    /// throw, out of a constructor, on a read-only or otherwise unwritable install directory - and
    /// the caller is App's startup path, so the app that could not create a cache folder did not
    /// start. A missing cache costs a round trip to EDSM per session and nothing else.
    /// </remarks>
    public EdsmCoordinateSource()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Data");
            Directory.CreateDirectory(dir);
            _cachePath = Path.Combine(dir, "system-coords-cache.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Note(
                $"System coordinate cache directory could not be created ({ex.GetType().Name}); " +
                "coordinates will be kept in memory for this session only.");
            _cachePath = null;
        }

        LoadCache();
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(BoundedHttp.CreateHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("FleetView/0.1 (personal Odyssey material finder)");
        return c;
    }

    public async Task<IReadOnlyDictionary<string, SystemCoords>> GetCoordsAsync(
        IEnumerable<string> systemNames, CancellationToken ct = default)
    {
        var result = new Dictionary<string, SystemCoords>(StringComparer.Ordinal);
        var missing = new List<string>();

        // Serve from cache; collect the rest.
        var wanted = systemNames
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .GroupBy(ShipLockerReader.Normalize)
            .Select(g => g.First()); // one representative per normalized name

        foreach (var name in wanted)
        {
            var key = ShipLockerReader.Normalize(name);
            if (_cache.TryGetValue(key, out var c))
            {
                result[key] = c;
                Touch(key);
            }
            else missing.Add(name);
        }

        if (missing.Count == 0) return result;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int i = 0; i < missing.Count; i += ChunkSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = missing.Skip(i).Take(ChunkSize).ToList();
                await FetchChunkAsync(chunk, result, ct).ConfigureAwait(false);
            }
            SaveCache();
        }
        finally
        {
            _gate.Release();
        }
        return result;
    }

    private async Task FetchChunkAsync(List<string> names,
        Dictionary<string, SystemCoords> result, CancellationToken ct)
    {
        var query = string.Join("&", names.Select(n => "systemName[]=" + Uri.EscapeDataString(n)));
        var url = $"https://www.edsm.net/api-v1/systems?{query}&showCoordinates=1";

        string json;
        try
        {
            json = await BoundedHttp
                .GetStringAsync(Http, url, MaxResponseBytes, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the caller's decision and has to reach them. Swallowing it here left
            // the chunk loop in GetCoordsAsync running to the end of the list, working through
            // requests nobody was waiting for any more.
            throw;
        }
        catch (Exception ex)
        {
            // Network or protocol failure: leave these systems unresolved (distance shows blank).
            DiagnosticLog.Note($"EDSM lookup of {names.Count} system(s) failed: {ex.GetType().Name}.");
            return;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            DiagnosticLog.Note("EDSM answered with something that is not JSON.");
            return;
        }

        // Only names we actually asked about are accepted. The key used to come from the reply,
        // with nothing checking it against the request, which let the far end decide what went
        // into a cache this app then persists and trusts. Nothing is lost by requiring the match:
        // a coordinate filed under a name we never asked for could never be looked up again.
        var requested = new HashSet<string>(
            names.Select(ShipLockerReader.Normalize), StringComparer.Ordinal);

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                // Per-element and non-throwing. A single malformed entry used to abort the whole
                // enumeration from inside the try, discarding every later system in a 40-item
                // batch along with it.
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!el.TryGetProperty("name", out var nameEl)
                    || nameEl.ValueKind != JsonValueKind.String) continue;
                if (!el.TryGetProperty("coords", out var co)
                    || co.ValueKind != JsonValueKind.Object) continue;
                if (!TryDouble(co, "x", out var x)) continue;
                if (!TryDouble(co, "y", out var y)) continue;
                if (!TryDouble(co, "z", out var z)) continue;

                var key = ShipLockerReader.Normalize(nameEl.GetString());
                if (key.Length == 0 || !requested.Contains(key)) continue;

                var coords = new SystemCoords(x, y, z);
                _cache[key] = coords;
                Touch(key);
                result[key] = coords;
            }
        }
    }

    /// <summary>
    /// Reads one coordinate, requiring it to be a number this app can actually compute with.
    /// </summary>
    /// <remarks>
    /// Being a JSON number is not enough. <c>TryGetDouble</c> returns true for an exponent past
    /// double's range and hands back an infinity - verified, <c>1e400</c> parses to +∞ - and a
    /// single infinite coordinate turns every distance into NaN, which then sorts arbitrarily and
    /// renders as blank. The galaxy is about 100,000 ly across, so the bound below is far outside
    /// anything real while still excluding a number that is finite but meaningless.
    /// </remarks>
    private static bool TryDouble(JsonElement parent, string name, out double value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var el)
               && el.ValueKind == JsonValueKind.Number
               && el.TryGetDouble(out value)
               && IsPlausibleCoordinate(value);
    }

    /// <summary>Finite, and inside a box generously larger than the galaxy.</summary>
    internal static bool IsPlausibleCoordinate(double v) =>
        double.IsFinite(v) && Math.Abs(v) <= MaxCoordinate;

    private const double MaxCoordinate = 1_000_000;

    /// <summary>
    /// When each cached system was last written or looked up, used to decide what to keep once
    /// <see cref="MaxCachedSystems"/> is reached.
    /// </summary>
    /// <remarks>
    /// A plain <c>Take(MaxCachedSystems)</c> over a <see cref="ConcurrentDictionary"/> keeps
    /// whichever entries enumeration happens to reach first, which is hash-dependent and unrelated
    /// to usefulness - so past the ceiling the file froze around an arbitrary set and newly fetched
    /// coordinates were the ones most likely to be dropped, every session, forever.
    /// </remarks>
    private readonly ConcurrentDictionary<string, long> _lastUsed = new(StringComparer.Ordinal);

    private long _useCounter;

    private void Touch(string key) => _lastUsed[key] = Interlocked.Increment(ref _useCounter);

    private void LoadCache()
    {
        if (_cachePath is null) return;

        try
        {
            var file = new FileInfo(_cachePath);
            if (!file.Exists) return;

            // Checked before reading, not after parsing. The entry ceiling below is applied to an
            // already-materialised dictionary, so on its own it bounds what is kept rather than
            // what is read - and this file is only as trustworthy as the directory it sits in.
            if (file.Length > MaxCacheFileBytes)
            {
                DiagnosticLog.Note(
                    $"System coordinate cache is {file.Length:N0} bytes, over the " +
                    $"{MaxCacheFileBytes:N0} byte limit; starting empty.");
                return;
            }

            var data = JsonSerializer.Deserialize<Dictionary<string, SystemCoords>>(
                File.ReadAllText(_cachePath));
            if (data == null) return;

            int kept = 0;
            foreach (var kv in data)
            {
                if (kept >= MaxCachedSystems) break;
                if (string.IsNullOrEmpty(kv.Key)) continue;

                // The same plausibility test the network path applies. Coordinates arriving from
                // edsm.net were gated on it and coordinates arriving from this file were not, which
                // is the same data reaching the same dictionary through a door with no check on it.
                // A half-written file is enough to produce an infinity here without any attacker,
                // and one infinite coordinate turns every computed distance into NaN.
                if (!IsPlausibleCoordinate(kv.Value.X)
                    || !IsPlausibleCoordinate(kv.Value.Y)
                    || !IsPlausibleCoordinate(kv.Value.Z)) continue;

                var key = ShipLockerReader.Normalize(kv.Key);
                if (key.Length == 0) continue;

                _cache[key] = kv.Value;
                Touch(key);
                kept++;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Note($"System coordinate cache could not be read ({ex.GetType().Name}); starting empty.");
        }
    }

    private void SaveCache()
    {
        if (_cachePath is null) return;

        try
        {
            if (_cache.Count > MaxCachedSystems) EvictLeastRecentlyUsed();

            var snapshot = _cache.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Note($"System coordinate cache could not be written ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Drops the least recently used entries until the cache is back at
    /// <see cref="MaxCachedSystems"/>. Caller holds <see cref="_gate"/>.
    /// </summary>
    /// <remarks>
    /// In memory as well as on disk. The ceiling used to apply only to the snapshot being written,
    /// so the dictionary itself carried every system looked up since launch regardless - the file
    /// stopped growing and the process did not.
    /// </remarks>
    private void EvictLeastRecentlyUsed()
    {
        var doomed = _cache.Keys
            .OrderBy(k => _lastUsed.GetValueOrDefault(k))
            .Take(_cache.Count - MaxCachedSystems)
            .ToList();

        foreach (var key in doomed)
        {
            _cache.TryRemove(key, out _);
            _lastUsed.TryRemove(key, out _);
        }
    }
}
