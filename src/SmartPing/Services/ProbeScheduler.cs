using System.Diagnostics;
using Microsoft.Extensions.Options;
using SmartPing.Models;

namespace SmartPing.Services;

public sealed class ProbeScheduler(Store store, ProbeEngine engine, IOptions<SmartPingOptions> options,
    ILogger<ProbeScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Mode.Equals("server", StringComparison.OrdinalIgnoreCase)) return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        var loop = new TargetProbeLoop(engine, logger, "local",
            () => store.GetTargets().Where(target => target.Enabled && target.NodeIds.Contains("local")).ToArray(),
            (sample, _) => { store.SaveSamples([sample]); return Task.CompletedTask; });
        logger.LogInformation("Local probe scheduler started; maximum concurrent probes: {Concurrency}", TargetProbeLoop.MaxConcurrency);
        await Task.WhenAll(loop.RunAsync(stoppingToken), HeartbeatAsync(timer, stoppingToken));
    }

    private async Task HeartbeatAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            do
            {
                try
                {
                    store.UpsertNode(new NodeInfo("local", options.Value.NodeName,
                        string.IsNullOrWhiteSpace(options.Value.NodeAddress) ? System.Net.Dns.GetHostName() : options.Value.NodeAddress,
                        DateTimeOffset.UtcNow, true));
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Failed to update local node heartbeat");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

// One state entry per configured target and at most 32 tasks: no timer-per-target or unbounded task queue.
internal sealed class TargetProbeLoop(ProbeEngine engine, ILogger logger, string nodeId,
    Func<IReadOnlyList<TargetConfig>> getTargets, Func<ProbeSample, CancellationToken, Task> saveSample)
{
    internal const int MaxConcurrency = 32;

    private sealed class TargetState(TargetConfig target)
    {
        public TargetConfig Target = target;
        public long NextDue;
        public Task? Running;
        public CancellationTokenSource? Cancellation;
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var states = new Dictionary<string, TargetState>(StringComparer.Ordinal);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            do
            {
                IReadOnlyList<TargetConfig> targets;
                try { targets = getTargets(); }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Failed to load probe targets");
                    continue;
                }

                var activeIds = targets.Where(target => target.Enabled).Select(target => target.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var entry in states.ToArray())
                {
                    if (activeIds.Contains(entry.Key)) continue;
                    entry.Value.Cancellation?.Cancel();
                    // Keep canceled tasks tracked until they exit, including their concurrency slot.
                    if (entry.Value.Running is { IsCompleted: false }) continue;
                    entry.Value.Cancellation?.Dispose();
                    states.Remove(entry.Key);
                }
                foreach (var target in targets.Where(target => target.Enabled))
                {
                    if (states.TryGetValue(target.Id, out var state)) state.Target = target;
                    else states[target.Id] = new TargetState(target);
                }

                foreach (var state in states.Values)
                {
                    if (state.Running is not { IsCompleted: true }) continue;
                    state.Running = null;
                    state.Cancellation?.Dispose();
                    state.Cancellation = null;
                }

                var running = states.Values.Count(state => state.Running is not null);
                var now = Stopwatch.GetTimestamp();
                // Oldest due first avoids starvation when there are more targets than slots.
                foreach (var state in states.Values.Where(state => activeIds.Contains(state.Target.Id)
                             && state.Running is null && state.NextDue <= now).OrderBy(state => state.NextDue))
                {
                    if (running >= MaxConcurrency) break;
                    state.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    state.NextDue = now + (long)(Math.Max(1, state.Target.IntervalSeconds) * (double)Stopwatch.Frequency);
                    state.Running = RunOneAsync(state.Target, state.Cancellation.Token);
                    running++;
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var state in states.Values) state.Cancellation?.Cancel();
            await Task.WhenAll(states.Values.Select(state => state.Running ?? Task.CompletedTask));
            foreach (var state in states.Values) state.Cancellation?.Dispose();
        }
    }

    private async Task RunOneAsync(TargetConfig target, CancellationToken cancellationToken)
    {
        try
        {
            var sample = await engine.ProbeAsync(target, nodeId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await saveSample(sample, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Probe or sample persistence failed for target {TargetId}", target.Id);
        }
    }
}
