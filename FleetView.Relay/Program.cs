using FleetView.Relay;
using FleetView.Relay.Api;
using FleetView.Relay.Eddn;
using FleetView.Relay.Storage;

var builder = WebApplication.CreateBuilder(args);

string dbPath = builder.Configuration["RelayDbPath"] ?? "relay.db";
builder.Services.AddSingleton(new RelayDb(dbPath));
builder.Services.AddSingleton(new ComponentCatalog(
    Path.Combine(AppContext.BaseDirectory, "Data", "catalog.json")));

// EddnIngestion=false (command line "--EddnIngestion=false" or the environment) keeps the relay
// off EDDN entirely, so its tests can start the real binary without a live connection writing
// into their throwaway database. Production leaves it on; the default is on.
bool eddnIngestion = builder.Configuration.GetValue("EddnIngestion", true);
if (eddnIngestion) builder.Services.AddHostedService<EddnListener>();
builder.Services.AddHostedService<RetentionService>();

// /listings is limited per client (30 a minute, IPv6 per /64) and, behind that, by the global
// window it always had (120 per 10 s). The client is the address Caddy forwards. See
// Api/RateLimiting.cs for the trust rules and how the numbers were chosen.
builder.Services.AddRelayRateLimiting();

var app = builder.Build();

if (!eddnIngestion)
    app.Logger.LogWarning("EDDN ingestion is OFF (EddnIngestion=false): nothing new will be stored");

// First in the pipeline so the rate limiter sees the client address Caddy forwarded. It only
// ever rewrites RemoteIpAddress, and only for connections from Caddy on loopback.
app.UseForwardedHeaders();
app.UseRateLimiter();
app.MapListingsEndpoint();
app.MapGet("/", () => "FleetView.Relay is running.");

string url = builder.Configuration["RelayUrl"] ?? "http://localhost:5085";
app.Run(url);
