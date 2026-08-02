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
    /// GETs <paramref name="url"/> and returns the body, or throws once it passes
    /// <paramref name="maxBytes"/>.
    /// </summary>
    public static async Task<string> GetStringAsync(
        HttpClient http, string url, long maxBytes, CancellationToken ct)
    {
        using var response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(Limit(stream, maxBytes));
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
    }

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
