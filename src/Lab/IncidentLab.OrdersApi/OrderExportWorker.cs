using IncidentLab.Platform;
using Npgsql;

/// <summary>
/// Fault injection for scenario s03, disguised as a periodic export job (the name shows up in logs and must look like
/// real code, AGENTS.md §6.5). While the <see cref="OverrideKind.HoldDbConnections"/> override is active it runs N slow
/// "snapshot" queries in parallel. Each one borrows a pooled connection for the hold time, gives it back and starts
/// again, so normal requests mostly find the pool full and fail with Npgsql's real "pool exhausted" error. Sometimes a
/// request wins a slot as it frees up: failures are intermittent, as in production.
/// </summary>
internal sealed partial class OrderExportWorker(
    RuntimeOverrideState overrides,
    NpgsqlDataSource db,
    ILogger<OrderExportWorker> logger) : BackgroundService
{
    private const int DefaultConnections = 20;
    private static readonly TimeSpan DefaultHold = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CancellationTokenSource? running = null;
        Task[] batches = [];
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var active = overrides.Current is { Kind: OverrideKind.HoldDbConnections } current ? current : null;

                if (active is not null && running is null)
                {
                    running = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var connections = active.Connections ?? DefaultConnections;
                    var hold = active.Delay ?? DefaultHold;
                    LogExportStarted(logger, connections);
                    batches = [.. Enumerable.Range(0, connections).Select(_ => RunBatchesAsync(hold, running.Token))];
                }
                else if (active is null && running is not null)
                {
                    await running.CancelAsync();
                    await Task.WhenAll(batches);
                    running.Dispose();
                    running = null;
                    LogExportFinished(logger);
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down.
        }
        finally
        {
            running?.Dispose();
        }
    }

    private async Task RunBatchesAsync(TimeSpan hold, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var cmd = db.CreateCommand("SELECT export_order_snapshot($1)");
                cmd.Parameters.Add(new NpgsqlParameter { Value = hold.TotalSeconds });
                cmd.CommandTimeout = (int)hold.TotalSeconds + 30;
                await cmd.ExecuteScalarAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // override cleared: Npgsql cancels the running query on the server, freeing the connection
            }
            catch (NpgsqlException ex) when (!ct.IsCancellationRequested)
            {
                // The job competes for the same pool as the requests and sometimes loses. Try again.
                LogBatchFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Order snapshot export started with {Batches} parallel batches")]
    private static partial void LogExportStarted(ILogger logger, int batches);

    [LoggerMessage(Level = LogLevel.Information, Message = "Order snapshot export finished")]
    private static partial void LogExportFinished(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Order snapshot export batch failed, retrying")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);
}
