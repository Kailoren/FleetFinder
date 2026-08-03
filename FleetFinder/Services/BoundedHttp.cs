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
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(deadline);
        var token = timed.Token;

        using var response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        RejectUnusable(response, maxBytes);

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
    public static void RejectUnusable(HttpResponseMessage response, long maxBytes)
    {
        if (response.Content.Headers.ContentLength is long declared && declared > maxBytes)
            throw new InvalidDataException(
                $"Response declared {declared:N0} bytes, over the {maxBytes:N0} byte limit, and was not read.");

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType != null && !IsTextual(mediaType))
            throw new InvalidDataException(
                $"Response content type '{mediaType}' is not text this app reads.");
    }

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
