using System.Collections.Concurrent;
using IncidentLab.Chaos;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddChaos();

// "https+http://catalog-api" is resolved by Aspire service discovery.
builder.Services.AddHttpClient<CatalogClient>(client => client.BaseAddress = new Uri("https+http://catalog-api"));

// TODO(F0): replace the in-memory store with Postgres (Aspire.Npgsql + the "ordersdb" resource in the AppHost)
// so that scenario s03 (connection pool exhaustion) can be implemented for real.
builder.Services.AddSingleton<ConcurrentDictionary<Guid, Order>>();

var app = builder.Build();

app.UseChaos();
app.MapDefaultEndpoints();
app.MapChaosEndpoints();

app.MapGet("/orders", (ConcurrentDictionary<Guid, Order> store) => store.Values.OrderByDescending(o => o.CreatedAt).Take(50));

app.MapPost("/orders", async (CreateOrder request, CatalogClient catalog, ConcurrentDictionary<Guid, Order> store, ILogger<Program> logger, CancellationToken ct) =>
{
    var item = await catalog.GetItemAsync(request.ItemId, ct);
    if (item is null)
    {
        return Results.NotFound();
    }

    var order = new Order(Guid.NewGuid(), item.Id, request.Quantity, item.Price * request.Quantity, DateTimeOffset.UtcNow);
    store[order.Id] = order;

    Log.OrderCreated(logger, order.Id, order.ItemId, order.Quantity);
    return Results.Created($"/orders/{order.Id}", order);
});

app.Run();

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} created for item {ItemId} x{Quantity}")]
    public static partial void OrderCreated(ILogger logger, Guid orderId, int itemId, int quantity);
}

internal sealed record CreateOrder(int ItemId, int Quantity);

internal sealed record Order(Guid Id, int ItemId, int Quantity, decimal Total, DateTimeOffset CreatedAt);

internal sealed record CatalogItem(int Id, string Name, decimal Price);

internal sealed class CatalogClient(HttpClient http)
{
    public async Task<CatalogItem?> GetItemAsync(int id, CancellationToken ct)
    {
        using var response = await http.GetAsync($"/items/{id}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        // A 500 from catalog-api surfaces here as an exception: that is the dependency-failure signal.
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CatalogItem>(ct);
    }
}
