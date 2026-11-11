using System.Net.Http.Json;

// Generates steady traffic against orders-api so that faults produce visible signals
// (error rate, latency) in metrics, logs and traces. Without traffic there is no incident.
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHttpClient("orders", client => client.BaseAddress = new Uri("https+http://orders-api"));
builder.Services.AddHostedService<TrafficWorker>();

builder.Build().Run();

internal sealed partial class TrafficWorker(IHttpClientFactory httpClientFactory, ILogger<TrafficWorker> logger) : BackgroundService
{
    // TODO(F0): make rate configurable (env var) and add a bit of jitter / bursts.
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var http = httpClientFactory.CreateClient("orders");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var itemId = Random.Shared.Next(1, 5);
                using var response = await http.PostAsJsonAsync("/orders", new { itemId, quantity = Random.Shared.Next(1, 4) }, stoppingToken);
                if (!response.IsSuccessStatusCode)
                {
                    LogUnsuccessfulResponse(logger, (int)response.StatusCode);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRequestFailed(logger, ex);
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "orders-api answered {StatusCode}")]
    private static partial void LogUnsuccessfulResponse(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Request to orders-api failed")]
    private static partial void LogRequestFailed(ILogger logger, Exception exception);
}
