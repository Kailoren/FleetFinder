using System.IO;
using System.Net.Http;

namespace FleetView.Services;

/// <summary>
/// Reads HTTP responses with a hard ceiling on how many bytes will be accepted.
/// </summary>
/// <remarks>
/// <para>
/// Every remote source this app reads is somebody else's server: edsm.net, api.github.com, and
/// our own relay, which is still a box on the internet. How big a reply is, is their decision
/// and not ours, so the size has to be refused rather than discovered.
/// </para>
/// <para>
/// The limit is applied while reading, not from <c>Content-Length</c>. That header is absent on
/// a chunked response and is written by the sender in any case, so checking it establishes
/// nothing; an earlier version of this app checked only the header and would have read an
/// unbounded chunked reply in full.
/// </para>
/// </remarks>
internal static class BoundedHttp
{
    /// <summary>
    /// Ceiling on a whole call, headers and body together. <see cref="HttpClient.Timeout"/> cannot
    /// serve as this: it stops applying the moment the headers arrive under
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, which is what every call here uses.
    /// A byte limit is no substitute either - it can only fire on bytes that arrive, so a sender
    /// that answers and then dribbles, or simply stops, never trips it.
    /// </summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Most redirects one call will follow. Generous for the endpoints this app uses, all of which
    /// answer directly, and far below the fifty hops <see cref="HttpClient"/> allows by default.
    /// </summary>
    private const int MaxRedirects = 5;

    /// <summary>
    /// The handler every <see cref="HttpClient"/> in this app is built on.
    /// </summary>
    /// <remarks>
    /// Automatic redirect following is turned off deliberately. With it on, the intermediate
    /// requests happen inside the awaited <c>GetAsync</c> call, so every check in this app - which
    /// hosts are acceptable, what a URL is allowed to look like - runs against the final response,
    /// after this app has already contacted whatever the chain pointed at. Refusing the answer
    /// afterwards does not undo the request that fetched it. <see cref="GetAsync"/> walks the chain
    /// itself instead, checking each hop before it is followed.
    /// </remarks>
    public static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false };

    /// <summary>
    /// GETs <paramref name="requested"/>, following redirects only to the same origin and only up
    /// to <see cref="MaxRedirects"/> times. The returned response's headers have been read; its
    /// body has not.
    /// </summary>
    /// <remarks>
    /// The origin is compared against the URL originally asked for, not against the previous hop,
    /// so a chain cannot walk somewhere a hop at a time. The caller still owns the response and
    /// must dispose it.
    /// </remarks>
    public static async Task<HttpResponseMessage> GetAsync(
        HttpClient http, Uri requested, CancellationToken ct)
    {
        var next = requested;

        for (int hop = 0; ; hop++)
        {
            var response = await http
                .GetAsync(next, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode)) return response;

            // Disposed here rather than left to the caller: this one is being discarded in favour
            // of the hop it names, and its body is never read.
            var location = response.Headers.Location;
            response.Dispose();

            if (hop >= MaxRedirects)
                throw new InvalidDataException(
                    $"Request to {Origin(requested)} was redirected more than {MaxRedirects} times.");

            if (location is null)
                throw new InvalidDataException(
                    $"Request to {Origin(requested)} was redirected without saying where to.");

            // Relative Locations are legal and common, and resolving against the current hop is
            // what a browser does. Absolute ones are checked below either way.
            next = location.IsAbsoluteUri ? location : new Uri(next, location);
            RejectOtherOrigin(next, requested);
        }
    }

    private static bool IsRedirect(System.Net.HttpStatusCode status) =>
        (int)status is >= 300 and < 400;

    /// <summary>
    /// GETs <paramref name="url"/> and returns the body, or throws once it passes
    /// <paramref name="maxBytes"/> or <see cref="DefaultDeadline"/>.
    /// </summary>
    public static Task<string> GetStringAsync(
        HttpClient http, string url, long maxBytes, CancellationToken ct) =>
        GetStringAsync(http, url, maxBytes, DefaultDeadline, ct);

    /// <inheritdoc cref="GetStringAsync(HttpClient, string, long, CancellationToken)"/>
    public static async Task<string> GetStringAsync(
        HttpClient http, string url, long maxBytes, TimeSpan deadline, CancellationToken ct)
    {
        var requested = new Uri(url, UriKind.Absolute);

        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(deadline);
        var token = timed.Token;

        using var response = await GetAsync(http, requested, token).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        RejectUnusable(response, requested, maxBytes);

        var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(Limit(stream, maxBytes));
            return await reader.ReadToEndAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Refuses a response before a byte of its body is read, on the two things the headers can
    /// settle on their own: a declared length already over the limit, and a content type that is
    /// not text this app parses.
    /// </summary>
    /// <remarks>
    /// Neither replaces <see cref="Limit"/>. <c>Content-Length</c> is absent on a chunked reply
    /// and is the sender's claim about itself in any case, so it can only ever catch an honest
    /// oversize answer early - the limit still has to be enforced while reading. The content-type
    /// check is the same kind of thing: a courtesy the sender extends, worth acting on when it is
    /// there and proving nothing when it is not.
    /// </remarks>
    public static void RejectUnusable(HttpResponseMessage response, Uri requested, long maxBytes)
    {
        RejectRedirectedElsewhere(response, requested);

        if (response.Content.Headers.ContentLength is long declared && declared > maxBytes)
            throw new InvalidDataException(
                $"Response declared {declared:N0} bytes, over the {maxBytes:N0} byte limit, and was not read.");

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType != null && !IsTextual(mediaType))
            throw new InvalidDataException(
                $"Response content type '{mediaType}' is not text this app reads.");
    }

    /// <summary>
    /// Refuses an answer that came from a different origin than the one asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-hop check in <see cref="GetAsync"/> is what actually stops this app contacting
    /// somewhere it did not intend to. This stays as the check on the response finally accepted,
    /// which is a different question and worth asking separately: the callers' own restrictions
    /// (RelayMarketSource refuses a base URL that is not https, UpdateChecker refuses a release URL
    /// that is not on github.com) decide only where the first request goes.
    /// </para>
    /// <para>
    /// Same-origin redirects are allowed, so an endpoint that reorganises its own paths keeps
    /// working. No credentials are sent on any of these requests, so what matters is which origin
    /// the answer is accepted <em>from</em>, and that is what the final URI records.
    /// </para>
    /// </remarks>
    private static void RejectRedirectedElsewhere(HttpResponseMessage response, Uri requested)
    {
        // Set by HttpClient on every response. Refused rather than skipped when absent: this is
        // the only evidence of where the answer came from, and "could not tell" is not a reason to
        // accept it.
        var final = response.RequestMessage?.RequestUri;
        if (final is null)
            throw new InvalidDataException(
                $"Could not confirm the answer to {requested.Host} came from {requested.Host}.");

        RejectOtherOrigin(final, requested);
    }

    /// <summary>
    /// Throws unless <paramref name="candidate"/> is on the same origin as <paramref name="requested"/>.
    /// </summary>
    /// <remarks>
    /// An origin is scheme, host <em>and</em> port. The port used to be left out, which made
    /// "same host" the whole test - so a redirect from an endpoint to another service listening on
    /// a different port of the same machine read as staying put. On a shared host that is a
    /// different party's service; on our own relay's box it is the plain-HTTP listener that exists
    /// for older builds. Neither is the thing that was asked.
    /// </remarks>
    private static void RejectOtherOrigin(Uri candidate, Uri requested)
    {
        if (string.Equals(candidate.Scheme, requested.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Host, requested.Host, StringComparison.OrdinalIgnoreCase)
            && candidate.Port == requested.Port)
        {
            return;
        }

        throw new InvalidDataException(
            $"Request to {Origin(requested)} was redirected to {Origin(candidate)}, " +
            "which this app does not accept an answer from.");
    }

    private static string Origin(Uri uri) => uri.GetLeftPart(UriPartial.Authority);

    private static bool IsTextual(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("/json", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Wraps <paramref name="inner"/> so reading past <paramref name="maxBytes"/> throws rather
    /// than returning the extra bytes.
    /// </summary>
    public static Stream Limit(Stream inner, long maxBytes) => new LimitedStream(inner, maxBytes);

    private sealed class LimitedStream(Stream inner, long maxBytes) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            Count(n);
            return n;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            int n = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            Count(n);
            return n;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private void Count(int n)
        {
            _read += n;
            if (_read > maxBytes)
                throw new InvalidDataException(
                    $"Response exceeded the {maxBytes:N0} byte limit and was not read.");
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
