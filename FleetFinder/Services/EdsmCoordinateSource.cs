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

    private static readonly HttpClient Http = CreateClient();

    private readonly string _cachePath;

    /// <summary>
    /// Concurrent because the read path does not take <see cref="_gate"/>. That gate serialises
    /// writers against each other, but a second overlapping <see cref="GetCoordsAsync"/> reads
    /// this dictionary before ever reaching it, and a plain Dictionary being resized by a writer
    /// can make such a reader throw or walk a stale bucket chain. Nothing about this class's
    /// public surface forbids concurrent use, so the collection is the right place to fix it.
    /// </summary>
    private readonly ConcurrentDictionary<string, SystemCoords> _cache = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public EdsmCoordinateSource()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "system-coords-cache.json");
        LoadCache();
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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
            if (_cache.TryGetValue(key, out var c)) result[key] = c;
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
        catch
        {
            // Network or protocol failure: leave these systems unresolved (distance shows blank).
            // Deliberately broad - the transport can fail in a dozen unrelated ways and none of
            // them should interrupt a search. Cancellation is excluded by the clause above, which
            // is the part that actually matters here.
            return;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return; // edsm.net answered with something that is not JSON
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
                result[key] = coords;
            }
        }
    }

    private static bool TryDouble(JsonElement parent, string name, out double value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var el)
               && el.ValueKind == JsonValueKind.Number
               && el.TryGetDouble(out value);
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, SystemCoords>>(
                File.ReadAllText(_cachePath));
            if (data == null) return;

            foreach (var kv in data.Take(MaxCachedSystems))
                if (!string.IsNullOrEmpty(kv.Key)) _cache[kv.Key] = kv.Value;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable cache: start empty and refetch. Caught by type rather than
            // blanket so anything unexpected still surfaces instead of being hidden here.
        }
    }

    private void SaveCache()
    {
        try
        {
            var snapshot = _cache.Count <= MaxCachedSystems
                ? _cache.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
                : _cache.Take(MaxCachedSystems)
                        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

            File.WriteAllText(_cachePath, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the cache is an optimisation, and losing it only costs a refetch.
        }
    }
}
