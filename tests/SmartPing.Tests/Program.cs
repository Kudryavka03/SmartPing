using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SmartPing.Models;
using SmartPing.Services;

var directory = Path.Combine(Path.GetTempPath(), "SmartPingTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var passed = 0;
void Assert(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    Console.WriteLine($"PASS {description}");
    passed++;
}
try
{
    var options = Options.Create(new SmartPingOptions { DataDirectory = directory, RetentionDays = 1 });
    var store = new Store(options);
    var now = DateTimeOffset.UtcNow;
    store.UpsertNode(new("local", "Test center", "192.0.2.10", now, true));
    store.UpsertNode(new("agent", "Test agent", "192.0.2.20", now));
    Assert(store.GetOrCreateNodeId() == new Store(options).GetOrCreateNodeId(), "Agent identity persists across restart");
    var target = new TargetConfig { Name = "Roundtrip", Host = "127.0.0.1", NodeIds = ["local", "agent"] };
    Assert(Validation.Target(target, store.GetNodes()) is null, "Valid ICMP target accepted");
    Assert(Validation.Target(target with { Host = "http://localhost:5080" }, store.GetNodes()) is not null, "URL rejected as host");
    Assert(Validation.Target(target with { NodeIds = ["missing"] }, store.GetNodes()) is not null, "Unknown node rejected");
    Assert(Validation.Target(target with { PacketSize = 65501 }, store.GetNodes()) is not null, "Oversize payload rejected");
    Assert(Validation.Target(target with { Protocol = "tcp", TimeoutMs = 100, RetransmitThresholdMs = 100 }, store.GetNodes()) is not null,
        "TCP retransmit threshold must be below timeout");
    Assert(Validation.Target(target with { TimeoutMs = -1, RetransmitThresholdMs = -1 }, store.GetNodes()) is null,
        "ICMP ignores TCP-only thresholds");
    store.SaveTarget(target);
    var samples = new[]
    {
        new ProbeSample("first", target.Id, "local", now.AddSeconds(-20), 10, true, false, null),
        new ProbeSample("second", target.Id, "local", now.AddSeconds(-10), 30, true, false, null),
        new ProbeSample("third", target.Id, "agent", now.AddSeconds(-5), null, false, false, "Timeout"),
        new ProbeSample("fourth", target.Id, "agent", now, 50, true, true, null)
    };
    store.SaveSamples(samples);
    store.SaveSamples(samples);
    var history = store.GetHistory(target, 1, null);
    Assert(history.Stats.Sent == 4 && history.Stats.Received == 3, "Retried reports are deduplicated by sample ID");
    Assert(history.Stats.LossPercent == 25 && history.Stats.AvgLatencyMs == 30, "Loss and average include the correct sample populations");
    Assert(history.Stats.MinLatencyMs == 10 && history.Stats.MaxLatencyMs == 50, "History preserves latency extrema");
    Assert(history.Stats.JitterMs == 20, "Jitter compares consecutive replies within the same node");
    Assert(history.Stats.RetransmitPercent == 25, "Retransmit percentage uses all sent probes");
    store.UpsertNode(new("agent", "Test agent", "192.0.2.20", now.AddMinutes(-1)));
    var staleAgent = store.GetHistory(target, 1, null);
    Assert(staleAgent.Stats.Status == "degraded" && staleAgent.Stats.MissingSources == 1,
        "A healthy local source cannot hide an offline assigned agent");
    store.UpsertNode(new("agent", "Test agent", "192.0.2.20", now));
    store.SaveSamples([new("future", target.Id, "local", now.AddMinutes(5), 999, true, false, null)]);
    Assert(store.GetHistory(target, 1, null).Stats.Sent == 4, "Future timestamps cannot distort current history or status");
    var local = store.GetHistory(target, 1, "local");
    Assert(local.Stats.Sent == 2 && local.Stats.LossPercent == 0 && local.Stats.AvgLatencyMs == 20, "Node filter isolates probe sources");
    Assert(history.Series.Sum(p => p.Sent) == 4 && history.Recent.Count == 4, "Bucketed history conserves samples");
    Assert(new Store(options).GetHistory(target, 1, null).Stats.Sent == 4, "Targets and history persist across restart");
    store.SaveSamples([new("old", target.Id, "local", now.AddDays(-2), 1, true, false, null)]);
    Assert(store.Prune() == 1, "Retention removes only expired samples");
    Assert(!store.RemoveNode("local"), "Local node cannot be removed");
    Assert(store.RemoveNode("agent") && store.GetTarget(target.Id)!.NodeIds.SequenceEqual(["local"]), "Removing node updates target assignments");
    store.TouchNode("agent", DateTimeOffset.UtcNow);
    Assert(store.GetNodes().All(n => n.Id != "agent"), "Late report heartbeat cannot recreate a removed node");
    Assert(store.TrySaveTarget(target) is not null, "Atomic target save rejects a removed node assignment");
    Assert(store.DeleteTarget(target.Id), "Deleting target succeeds");
    store.SaveSamples(samples);
    Assert(store.GetHistory(target, 1, null).Stats.Sent == 0, "Deleted target history cannot be resurrected by in-flight probes");
    Assert(store.TrySaveTarget(target with { NodeIds = ["local"] }, true) is not null && store.GetTargets().Count == 0,
        "A concurrent edit cannot recreate a deleted target");
    await ProbeTests.RunAsync();
    await TraceRunnerTests.RunAsync();
    TraceCoordinatorTests.Run();
    TcpAverageTests.Run();
    TcpJitterTests.Run();
    Console.WriteLine($"All {passed} storage/validation checks, probe checks and traceroute checks passed.");
}
finally
{
    SqliteConnection.ClearAllPools();
    var resolved = Path.GetFullPath(directory);
    var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SmartPingTests")) + Path.DirectorySeparatorChar;
    if (resolved.StartsWith(testRoot, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
        Directory.Delete(resolved, true);
}
