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
    private static readonly HttpClient Http = CreateClient();

    private readonly string _cachePath;
    private readonly Dictionary<string, SystemCoords> _cache = new(StringComparer.Ordinal);
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

        try
        {
            var json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("name", out var nameEl)) continue;
                if (!el.TryGetProperty("coords", out var co)) continue;
                var coords = new SystemCoords(
                    co.GetProperty("x").GetDouble(),
                    co.GetProperty("y").GetDouble(),
                    co.GetProperty("z").GetDouble());
                var key = ShipLockerReader.Normalize(nameEl.GetString());
                _cache[key] = coords;
                result[key] = coords;
            }
        }
        catch
        {
            // Network/parse failure: leave these systems unresolved (distance shows blank).
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, SystemCoords>>(
                File.ReadAllText(_cachePath));
            if (data != null)
                foreach (var kv in data) _cache[kv.Key] = kv.Value;
        }
        catch { /* ignore corrupt cache */ }
    }

    private void SaveCache()
    {
        try { File.WriteAllText(_cachePath, JsonSerializer.Serialize(_cache)); }
        catch { /* best effort */ }
    }
}
