using FleetView.Relay;
using FleetView.Relay.Api;
using FleetView.Relay.Eddn;
using FleetView.Relay.Storage;

var builder = WebApplication.CreateBuilder(args);

string dbPath = builder.Configuration["RelayDbPath"] ?? "relay.db";
builder.Services.AddSingleton(new RelayDb(dbPath));
builder.Services.AddSingleton(new ComponentCatalog(
    Path.Combine(AppContext.BaseDirectory, "Data", "catalog.json")));
builder.Services.AddHostedService<EddnListener>();

var app = builder.Build();

app.MapListingsEndpoint();
app.MapGet("/", () => "FleetView.Relay is running.");

string url = builder.Configuration["RelayUrl"] ?? "http://localhost:5085";
app.Run(url);
