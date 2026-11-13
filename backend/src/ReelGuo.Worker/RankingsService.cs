using ReelGuo.Data.Catalog;

namespace ReelGuo.Worker;

/// <summary>Recomputes hot scores and the three rankings on a timer (default every 10 minutes).</summary>
public sealed class RankingsService(IServiceScopeFactory scopes, WorkerOptions options, ILogger<RankingsService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the API a moment to apply migrations when both start together.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.RankingsMinutes)));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var run = await scope.ServiceProvider.GetRequiredService<RankingCalculator>().RecomputeAsync(stoppingToken);
                if (run is null) logger.LogDebug("Rankings skipped: another worker holds the lock");
                else logger.LogInformation("Rankings: {Series} series, hot {Hot}, recommend {Recommend}, potential {Potential}, pruned {Deleted} events",
                    run.Series, run.Hot, run.Recommend, run.Potential, run.EventsDeleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Rankings recompute failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
