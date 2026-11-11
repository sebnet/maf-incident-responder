using IncidentLab.Chaos;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddChaos();

var app = builder.Build();

app.UseChaos();
app.MapDefaultEndpoints();
app.MapChaosEndpoints();

// TODO(F0): move the catalog to Redis (cache) or Postgres so infrastructure faults become possible.
CatalogItem[] items =
[
    new(1, "Espresso beans 1kg", 18.50m),
    new(2, "Burr grinder", 120.00m),
    new(3, "Milk pitcher", 14.90m),
    new(4, "Tamper 58mm", 22.00m),
];

app.MapGet("/items", () => items);

app.MapGet("/items/{id:int}", (int id) =>
    items.FirstOrDefault(i => i.Id == id) is { } item ? Results.Ok(item) : Results.NotFound());

app.Run();

internal sealed record CatalogItem(int Id, string Name, decimal Price);
