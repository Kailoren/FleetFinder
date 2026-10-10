using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FleetView.Relay.Api;

// Self-test for FleetView.Relay's rate limiting (2026-10): starts the real relay binary on
// loopback with EDDN switched off and a throwaway database, and checks the per-client window, a
// forged X-Forwarded-For, IPv6 keyed per /64, the global window and the 429 itself. Prints one
// PASS or FAIL line per check and a final SUMMARY line; exit code 1 if anything failed.
var suite = new SelfTest();
suite.Run();
Console.WriteLine($"SUMMARY: {suite.Passed} passed, {suite.Failed} failed");
return suite.Failed == 0 ? 0 : 1;

internal sealed class SelfTest
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fleetview relay selftest " + Guid.NewGuid().ToString("N")[..8]);

    private const string Listings = "/listings?keys=chemicalcatalyst&direction=selling";

    public void Run()
    {
        Directory.CreateDirectory(_dir);
        try
        {
            Section("client keys", KeyTests);
            Section("per-client window", PerClientTests);
            Section("global window", GlobalTests);
        }
        finally
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void Section(string name, Action body)
    {
        Console.WriteLine($"-- {name}");
        try { body(); }
        catch (Exception ex) { Check($"{name}: ran without throwing", false, ex.ToString()); }
    }

    private void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine(ok || detail.Length == 0 ? $"{(ok ? "PASS" : "FAIL")}: {name}" : $"FAIL: {name} ({detail})");
        if (ok) Passed++; else Failed++;
    }

    private void Equal<T>(string name, T expected, T actual) =>
        Check(name, EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    private void KeyTests()
    {
        Equal("IPv4 clients are keyed per address", "203.0.113.5", RelayRateLimiting.ClientKey(IPAddress.Parse("203.0.113.5")));
        Equal("IPv4-mapped IPv6 is keyed as the IPv4 address", "203.0.113.5", RelayRateLimiting.ClientKey(IPAddress.Parse("::ffff:203.0.113.5")));
        Equal("IPv6 clients are keyed per /64", "2001:db8:1:2::/64", RelayRateLimiting.ClientKey(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd")));
        Equal("a missing address has its own key", "unknown", RelayRateLimiting.ClientKey(null));
    }

    /// <remarks>
    /// The "not Caddy" peer is 127.0.0.2. The relay trusts exactly 127.0.0.1 and ::1, not the
    /// 127.0.0.0/8 network, so to the relay 127.0.0.2 is as untrusted as any outside address. A
    /// truly non-loopback peer would need the relay listening on a network interface, which on
    /// Windows pops up a firewall prompt.
    /// </remarks>
    private void PerClientTests()
    {
        var relay = StartRelay("per-client.db");
        try
        {
            string? baseUrl = relay.WaitForListening(TimeSpan.FromSeconds(60));
            Check("relay starts with EDDN off", baseUrl is not null && relay.Output.Contains("EDDN ingestion is OFF"), relay.Output);
            if (baseUrl is null) return;
            var clock = Stopwatch.StartNew();

            using var fromCaddy = new HttpClient();
            using var fromElsewhere = BoundClient("127.0.0.2");

            Equal("\"/\" stays unlimited", 40, Enumerable.Range(0, 40).Count(_ => Get(fromCaddy, baseUrl + "/", "203.0.113.10").Status == 200));

            int okA = Enumerable.Range(0, 30).Count(_ => Get(fromCaddy, baseUrl + Listings, "203.0.113.10").Status == 200);
            var overA = Get(fromCaddy, baseUrl + Listings, "203.0.113.10");
            Equal("one forwarded address gets 30 /listings requests a minute", 30, okA);
            Equal("its 31st is refused with 429", 429, overA.Status);
            Check("the 429 carries Retry-After in whole seconds, at most a minute",
                int.TryParse(overA.RetryAfter, out int wait) && wait >= 1 && wait <= 60, $"Retry-After {overA.RetryAfter}");
            Equal("the 429 body is a JSON string, like the relay's 400", "\"too many requests\"", overA.Body);
            Check("the 429 is JSON", overA.ContentType?.StartsWith("application/json", StringComparison.Ordinal) == true, overA.ContentType ?? "none");
            Equal("another forwarded address is unaffected", 200, Get(fromCaddy, baseUrl + Listings, "203.0.113.11").Status);

            int okForged;
            int overForged;
            try
            {
                okForged = Enumerable.Range(1, 30).Count(i => Get(fromElsewhere, baseUrl + Listings, $"198.51.100.{i}").Status == 200);
                overForged = Get(fromElsewhere, baseUrl + Listings, "198.51.100.99").Status;
            }
            catch (HttpRequestException ex)
            {
                Check("a client bound to 127.0.0.2 can reach the relay", false, ex.Message);
                return;
            }
            Equal("a peer that is not Caddy gets its 30 even with a new X-Forwarded-For each time", 30, okForged);
            Equal("and is then refused: its X-Forwarded-For was ignored", 429, overForged);

            int okV6 = Enumerable.Range(1, 30).Count(i => Get(fromCaddy, baseUrl + Listings, $"2001:db8:aa:1::{i:x}").Status == 200);
            Equal("thirty different IPv6 addresses in one /64 share one window", 30, okV6);
            Equal("a 31st address in the same /64 is refused", 429, Get(fromCaddy, baseUrl + Listings, "2001:db8:aa:1:ffff:ffff:ffff:ffff").Status);
            Equal("the neighbouring /64 is a different client", 200, Get(fromCaddy, baseUrl + Listings, "2001:db8:aa:2::1").Status);
            Check("all of that inside one per-client window", clock.Elapsed < TimeSpan.FromSeconds(50), $"{clock.Elapsed.TotalSeconds:F1}s");

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!relay.Output.Contains("request(s) rejected since the last report") && DateTime.UtcNow < deadline)
                Thread.Sleep(100);
            string output = relay.Output;
            Check("rejections are logged as a count, once so far",
                output.Split("request(s) rejected since the last report").Length - 1 == 1, output);
            Check("no client address appears in the relay's log",
                !output.Contains("203.0.113.") && !output.Contains("198.51.100.") && !output.Contains("2001:db8") && !output.Contains("127.0.0.2"), output);
        }
        finally
        {
            relay.Stop();
        }
    }

    /// <summary>
    /// 140 /listings requests at once from 140 different clients. The global window lets 120
    /// through, queues 10 for the next window and refuses the rest, exactly as before per-client
    /// limiting existed.
    /// </summary>
    private void GlobalTests()
    {
        var relay = StartRelay("global.db");
        try
        {
            string? baseUrl = relay.WaitForListening(TimeSpan.FromSeconds(60));
            Check("relay starts for the global check", baseUrl is not null, relay.Output);
            if (baseUrl is null) return;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var clock = Stopwatch.StartNew();
            var tasks = Enumerable.Range(1, 140)
                .Select(i => Task.Run(() => Get(http, baseUrl + Listings, $"2001:db8:cc:{i:x}::1")))
                .ToArray();
            Task.WaitAll(tasks);
            var statuses = tasks.Select(t => t.Result.Status).ToList();
            var refused = tasks.Select(t => t.Result).Where(r => r.Status == 429).ToList();

            Equal("130 of 140 simultaneous clients are served (120 now, 10 queued)", 130, statuses.Count(s => s == 200));
            Equal("the other 10 are refused", 10, refused.Count);
            Check("their 429s carry Retry-After too", refused.All(r => int.TryParse(r.RetryAfter, out int s) && s >= 1 && s <= 60),
                string.Join(",", refused.Select(r => r.RetryAfter ?? "none")));
            Check("and the burst itself took under one 10-second window to start", clock.Elapsed < TimeSpan.FromSeconds(25), $"{clock.Elapsed.TotalSeconds:F1}s");
        }
        finally
        {
            relay.Stop();
        }
    }

    // ---------------------------------------------------------------- helpers

    private static (int Status, string? RetryAfter, string? ContentType, string Body) Get(HttpClient client, string url, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        using var response = client.Send(request);
        string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return ((int)response.StatusCode,
            response.Headers.TryGetValues("Retry-After", out var ra) ? ra.First() : null,
            response.Content.Headers.ContentType?.ToString(),
            body);
    }

    private static HttpClient BoundClient(string localAddress) => new(new SocketsHttpHandler
    {
        ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Parse(localAddress), 0));
                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    });

    /// <summary>
    /// The relay's own build output, found by walking up from this test's folder to the repository
    /// root, so it runs with its own deps.json, native SQLite and Data\catalog.json.
    /// </summary>
    private static string FindRelayDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string framework = dir.Name;
        string config = dir.Parent?.Name ?? "Debug";
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "FleetView.Relay")))
            dir = dir.Parent;
        if (dir is null) throw new FileNotFoundException("FleetView.Relay folder not found above " + AppContext.BaseDirectory);
        string dll = Path.Combine(dir.FullName, "FleetView.Relay", "bin", config, framework, "FleetView.Relay.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("relay build not found", dll);
        return dll;
    }

    private RunningRelay StartRelay(string dbName)
    {
        string dll = FindRelayDll();
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add($"--RelayDbPath={Path.Combine(_dir, dbName)}");
        psi.ArgumentList.Add("--EddnIngestion=false");
        psi.ArgumentList.Add("--RelayUrl=http://127.0.0.1:0");
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        return new RunningRelay(Process.Start(psi)!);
    }

    private sealed class RunningRelay
    {
        private readonly Process _process;
        private readonly StringBuilder _output = new();
        private readonly ManualResetEventSlim _listening = new();
        private string? _url;

        public RunningRelay(Process process)
        {
            _process = process;
            _process.OutputDataReceived += (_, e) => OnLine(e.Data);
            _process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public string Output { get { lock (_output) return _output.ToString(); } }

        private void OnLine(string? line)
        {
            if (line is null) return;
            lock (_output) _output.AppendLine(line);
            const string marker = "Now listening on: ";
            int at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                _url = line[(at + marker.Length)..].Trim();
                _listening.Set();
            }
        }

        public string? WaitForListening(TimeSpan timeout) => _listening.Wait(timeout) ? _url : null;

        public void Stop()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _process.WaitForExit(10_000);
            _process.Dispose();
        }
    }
}
