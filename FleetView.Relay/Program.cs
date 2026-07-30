using FleetView.Relay;
using FleetView.Relay.Api;
using FleetView.Relay.Eddn;
using FleetView.Relay.Storage;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

string dbPath = builder.Configuration["RelayDbPath"] ?? "relay.db";
builder.Services.AddSingleton(new RelayDb(dbPath));
builder.Services.AddSingleton(new ComponentCatalog(
    Path.Combine(AppContext.BaseDirectory, "Data", "catalog.json")));
builder.Services.AddHostedService<EddnListener>();

// /listings is public and unauthenticated by design, so it has no per-caller identity to
// partition on (Caddy fronts it as a reverse proxy, and forwarded-header trust isn't configured
// here) - a global cap is still a real improvement over no throttling at all for a hobby-scale API.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("listings", opt =>
    {
        opt.Window = TimeSpan.FromSeconds(10);
        opt.PermitLimit = 120;
        opt.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 10;
    });
});

var app = builder.Build();

app.UseRateLimiter();
app.MapListingsEndpoint();
app.MapGet("/", () => "FleetView.Relay is running.");

string url = builder.Configuration["RelayUrl"] ?? "http://localhost:5085";
app.Run(url);
