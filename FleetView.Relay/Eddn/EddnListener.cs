using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FleetView.Relay.Storage;
using NetMQ;
using NetMQ.Sockets;

namespace FleetView.Relay.Eddn;

/// <summary>
/// Subscribes to EDDN's live ZeroMQ firehose and dispatches each decompressed message to the
/// handler for its schema. Runs on a dedicated thread (via Task.Run) since NetMQ's receive calls
/// block the calling thread and shouldn't tie up the ASP.NET Core host's async machinery.
/// </summary>
public sealed class EddnListener : BackgroundService
{
    private const string RelayUrl = "tcp://eddn.edcd.io:9500";

    // EDDN's overall firehose (every schema, not just the ones we care about) is high-volume -
    // realistically always at least one message every few seconds. A ZeroMQ SUB socket's
    // underlying TCP connection can die silently (no exception raised) if it's dropped at the
    // network level without a clean FIN/RST, so a receive-with-timeout loop alone can't tell
    // "quietly dead" apart from "briefly idle". Going this long with zero messages of any kind is
    // itself the signal that the connection is dead and needs to be torn down and recreated.
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(60);

    // Real EDDN messages are small JSON documents - even a large commodity-v3 dump is a handful of
    // KB. These caps guard against a maliciously crafted, highly-compressed message (a "zip bomb")
    // published to the public firehose from exhausting memory on this shared process: the raw frame
    // is rejected outright if implausibly large, and the decompressed copy is hard-stopped if it
    // ever exceeds a generous multiple of any real payload.
    private const int MaxRawFrameBytes = 1024 * 1024; // 1 MB compressed
    private const int MaxDecompressedBytes = 4 * 1024 * 1024; // 4 MB decompressed

    private readonly RelayDb _db;
    private readonly ComponentCatalog _catalog;
    private readonly ILogger<EddnListener> _log;

    public EddnListener(RelayDb db, ComponentCatalog catalog, ILogger<EddnListener> log)
    {
        _db = db;
        _catalog = catalog;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => RunLoop(stoppingToken), stoppingToken);

    private void RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new SubscriberSocket();
                socket.Connect(RelayUrl);
                socket.SubscribeToAnyTopic();
                _log.LogInformation("Connected to EDDN at {Url}", RelayUrl);

                var lastMessageUtc = DateTime.UtcNow;
                bool stale = false;

                while (!ct.IsCancellationRequested)
                {
                    if (!socket.TryReceiveFrameBytes(TimeSpan.FromSeconds(1), out var raw))
                    {
                        if (DateTime.UtcNow - lastMessageUtc > StaleThreshold)
                        {
                            stale = true;
                            break;
                        }
                        continue;
                    }

                    lastMessageUtc = DateTime.UtcNow;
                    try
                    {
                        HandleRawMessage(raw!);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Skipping malformed EDDN message");
                    }
                }

                if (stale)
                {
                    _log.LogWarning(
                        "No EDDN traffic of any kind for {Seconds}s - the firehose is normally " +
                        "constant, so the connection is presumed dead. Reconnecting.",
                        StaleThreshold.TotalSeconds);
                    Thread.Sleep(TimeSpan.FromSeconds(2));
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogError(ex, "EDDN connection dropped, reconnecting in 5s");
                Thread.Sleep(TimeSpan.FromSeconds(5));
            }
        }
    }

    private void HandleRawMessage(byte[] raw)
    {
        if (raw.Length > MaxRawFrameBytes)
        {
            _log.LogWarning("Skipping oversized EDDN frame ({Bytes} bytes)", raw.Length);
            return;
        }

        using var compressed = new MemoryStream(raw);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        CopyWithLimit(zlib, decompressed, MaxDecompressedBytes);

        using var doc = JsonDocument.Parse(decompressed.GetBuffer().AsMemory(0, (int)decompressed.Length));
        var root = doc.RootElement;

        string schemaRef = root.GetStringAny("$schemaRef") ?? "";
        if (!root.TryGetAny(out var message, "message")) return;

        if (schemaRef.Contains("commodity/3", StringComparison.OrdinalIgnoreCase)
            || schemaRef.Contains("commodity-v3", StringComparison.OrdinalIgnoreCase))
        {
            CommodityV3Handler.Handle(message, _db);
        }
        else if (schemaRef.Contains("fcmaterials_journal", StringComparison.OrdinalIgnoreCase)
            || schemaRef.Contains("fcmaterials_capi", StringComparison.OrdinalIgnoreCase))
        {
            FcMaterialsHandler.Handle(message, _db, _catalog);
        }
        else if (schemaRef.Contains("/journal/1", StringComparison.OrdinalIgnoreCase))
        {
            // Distinct from "fcmaterials_journal/1" above - that ends in "_journal/1" (no slash
            // before "journal"), this one is the plain "journal/1" schema.
            JournalHandler.Handle(message, _db);
        }
        else if (schemaRef.Contains("dockingdenied", StringComparison.OrdinalIgnoreCase))
        {
            DockingDeniedHandler.Handle(message, _db);
        }
    }

    /// <summary>Copies src into dest, throwing once the total exceeds maxBytes - guards against a
    /// decompression bomb (a small compressed frame expanding into an enormous stream) rather than
    /// letting <c>Stream.CopyTo</c> allocate without bound.</summary>
    private static void CopyWithLimit(Stream src, Stream dest, int maxBytes)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidDataException($"Decompressed EDDN message exceeded {maxBytes} bytes");
            dest.Write(buffer, 0, read);
        }
    }
}
