using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace FleetView.Relay.Api;

/// <summary>
/// Rate limiting for /listings: a window per client, chained with the global window this relay
/// already had. The client address comes from X-Forwarded-For, trusted only from Caddy on this
/// machine.
/// </summary>
/// <remarks>
/// <para>
/// Until 2026-10 there was only the global window (120 per 10 seconds), shared by every
/// FleetFinder user, because every request reaches the relay from Caddy on loopback and the relay
/// could not tell callers apart. One script could use the whole window and everyone else's
/// searches would fail with it.
/// </para>
/// <para>
/// The per-client limit is sized from what FleetFinder sends. A search is one /listings request
/// per market direction, so one or two per click, and nothing in the app polls; the update check
/// goes to GitHub and the distance lookups to EDSM, never here. Thirty a minute is fifteen
/// two-direction searches, a click every four seconds for a whole minute, which no person
/// searching by hand reaches. The global window keeps its old size, queue and role as the cap on
/// the total, so the box is protected exactly as before.
/// </para>
/// <para>
/// Only /listings is limited, as before; "/" is a liveness string and stays open. The forwarded
/// address is used for nothing but the partition key and is never written to the relay's log.
/// </para>
/// </remarks>
public static class RelayRateLimiting
{
    /// <summary>/listings requests one client may make per <see cref="PerClientWindow"/>.</summary>
    public const int PerClientPermits = 30;

    /// <summary>The per-client window.</summary>
    public static readonly TimeSpan PerClientWindow = TimeSpan.FromMinutes(1);

    /// <summary>/listings requests all clients together may make per <see cref="GlobalWindow"/>.</summary>
    public const int GlobalPermits = 120;

    /// <summary>The global window, unchanged from before per-client limiting.</summary>
    public static readonly TimeSpan GlobalWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Requests that may wait for the next global window instead of being refused, as before.
    /// Smooths a short burst from many clients at once; a client over its own limit never waits.
    /// </summary>
    public const int GlobalQueue = 10;

    public static IServiceCollection AddRelayRateLimiting(this IServiceCollection services)
    {
        // X-Forwarded-For is believed only from a direct connection out of 127.0.0.1 or ::1,
        // which on the production box can only be Caddy: the relay listens on 127.0.0.1 alone.
        // KnownIPNetworks is cleared because its default admits all of 127.0.0.0/8, and
        // ForwardLimit 1 takes just the address Caddy itself appended. Anyone else's
        // X-Forwarded-For is ignored and they are limited by their own connection address.
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            options.KnownProxies.Add(IPAddress.Loopback);
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
        });

        services.AddSingleton<RateLimitRejectionLog>();
        services.AddHostedService(sp => sp.GetRequiredService<RateLimitRejectionLog>());

        services.AddRateLimiter(options =>
        {
            var perClient = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                IsLimited(context)
                    ? RateLimitPartition.GetFixedWindowLimiter(ClientKey(context.Connection.RemoteIpAddress), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = PerClientPermits,
                        Window = PerClientWindow,
                        QueueLimit = 0,
                    })
                    : RateLimitPartition.GetNoLimiter("unlimited"));

            var global = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                IsLimited(context)
                    ? RateLimitPartition.GetFixedWindowLimiter("global", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = GlobalPermits,
                        Window = GlobalWindow,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = GlobalQueue,
                    })
                    : RateLimitPartition.GetNoLimiter("unlimited"));

            // Per client first, so a client already over its own limit is turned away without
            // using up a permit, or a queue place, everyone else shares.
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(perClient, global);

            options.OnRejected = async (context, cancellationToken) =>
            {
                // A fixed window lease says when its window reopens. Which limiter refused is not
                // known here, so the fallback is the longer of the two windows.
                int seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                    : (int)PerClientWindow.TotalSeconds;

                var response = context.HttpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                context.HttpContext.RequestServices.GetRequiredService<RateLimitRejectionLog>().Record();

                // A JSON string, the same shape as this relay's other error answer
                // (Results.BadRequest("direction must be ...")).
                await response.WriteAsJsonAsync("too many requests", cancellationToken);
            };
        });

        return services;
    }

    private static bool IsLimited(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/listings", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The partition key for a client address, after forwarded-header processing. IPv4 is keyed
    /// per address. IPv6 is keyed per /64, because one home or one server is normally handed a
    /// whole /64 and can pick a fresh address from it for every request; keyed per address, the
    /// per-client limit would not limit such a client at all. An IPv4 address that arrived as
    /// IPv4-mapped IPv6 is folded back to IPv4 first, so it is not mistaken for an IPv6 client.
    /// </summary>
    public static string ClientKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes).ToString() + "/64";
    }
}

/// <summary>
/// Counts rate-limit rejections and logs the count at most once a minute, with no addresses.
/// </summary>
/// <remarks>
/// One line per rejected request would let a single abusive client fill the journal, and
/// addresses there would be personal data kept unmasked for as long as the journal keeps
/// anything. Caddy's access log, with addresses masked, is the place for traffic detail. The
/// first rejection after a quiet minute is logged at once; the rest are summed into the next
/// line, which a 10-second timer writes once the minute is up.
/// </remarks>
public sealed class RateLimitRejectionLog : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly ILogger<RateLimitRejectionLog> _log;
    private readonly object _gate = new();
    private long _pending;
    private long _lastLoggedMs = long.MinValue / 2;

    public RateLimitRejectionLog(ILogger<RateLimitRejectionLog> log) => _log = log;

    public void Record()
    {
        lock (_gate)
        {
            _pending++;
            FlushIfDue();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                lock (_gate) FlushIfDue();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void FlushIfDue()
    {
        long now = Environment.TickCount64;
        if (_pending == 0 || now - _lastLoggedMs < (long)Interval.TotalMilliseconds) return;
        _log.LogWarning("Rate limit: {Count} request(s) rejected since the last report", _pending);
        _pending = 0;
        _lastLoggedMs = now;
    }
}
