using IncidentLab.Platform;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddRuntimeOverrides();

// "ordersdb" comes from the AppHost. Aspire.Npgsql registers an NpgsqlDataSource (the connection pool) and adds
// Npgsql traces, pool metrics and a health check. Pool limits are explicit so that scenario s03 (pool exhaustion)
// produces exactly the driver error it describes.
builder.AddNpgsqlDataSource("ordersdb", configureDataSourceBuilder: dataSource =>
{
    dataSource.ConnectionStringBuilder.MaxPoolSize = 20;
    // Seconds to wait for a connection, including a free pool slot. Must stay below the callers' 10 s per-attempt
    // timeout (standard resilience handler): otherwise requests are cancelled before Npgsql reports "pool exhausted".
    dataSource.ConnectionStringBuilder.Timeout = 5;
});

// Periodic export job. Also the injection point for scenario s03 (see the class comment).
builder.Services.AddHostedService<OrderExportWorker>();

// "https+http://catalog-api" is resolved by Aspire service discovery.
builder.Services.AddHttpClient<CatalogClient>(client => client.BaseAddress = new Uri("https+http://catalog-api"));

var app = builder.Build();

await OrdersStore.EnsureSchemaAsync(app.Services.GetRequiredService<NpgsqlDataSource>());

app.UseRuntimeOverrides();
app.MapDefaultEndpoints();
app.MapRuntimeOverrideEndpoints();

app.MapGet("/orders", (NpgsqlDataSource db, CancellationToken ct) => OrdersStore.GetLatestAsync(db, limit: 50, ct));

app.MapPost("/orders", async (CreateOrder request, CatalogClient catalog, NpgsqlDataSource db, ILogger<Program> logger, CancellationToken ct) =>
{
    var item = await catalog.GetItemAsync(request.ItemId, ct);
    if (item is null)
    {
        return Results.NotFound();
    }

    var order = new Order(Guid.CreateVersion7(), item.Id, request.Quantity, item.Price * request.Quantity, DateTimeOffset.UtcNow);
    await OrdersStore.InsertAsync(db, order, ct);

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

/// <summary>Plain SQL over the pooled data source. Every call borrows a connection from the pool and returns it.</summary>
internal static class OrdersStore
{
    public static async Task EnsureSchemaAsync(NpgsqlDataSource db, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            CREATE TABLE IF NOT EXISTS orders (
                id         uuid           PRIMARY KEY,
                item_id    integer        NOT NULL,
                quantity   integer        NOT NULL,
                total      numeric(12, 2) NOT NULL,
                created_at timestamptz    NOT NULL
            );

            -- Used by OrderExportWorker. Slow on purpose (s03): it holds its connection for hold_seconds.
            CREATE OR REPLACE FUNCTION export_order_snapshot(hold_seconds double precision) RETURNS bigint
            LANGUAGE plpgsql AS $fn$
            BEGIN
                PERFORM pg_sleep(hold_seconds);
                RETURN (SELECT count(*) FROM orders);
            END
            $fn$;
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task InsertAsync(NpgsqlDataSource db, Order order, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "INSERT INTO orders (id, item_id, quantity, total, created_at) VALUES ($1, $2, $3, $4, $5)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = order.Id });
        cmd.Parameters.Add(new NpgsqlParameter { Value = order.ItemId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = order.Quantity });
        cmd.Parameters.Add(new NpgsqlParameter { Value = order.Total });
        cmd.Parameters.Add(new NpgsqlParameter { Value = order.CreatedAt });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<List<Order>> GetLatestAsync(NpgsqlDataSource db, int limit, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "SELECT id, item_id, quantity, total, created_at FROM orders ORDER BY created_at DESC LIMIT $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = limit });

        var orders = new List<Order>(limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            orders.Add(new Order(
                reader.GetGuid(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetDecimal(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return orders;
    }
}

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
