using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SmartPing.Models;
using SmartPing.Services;

public static class TcpAverageTests
{
    public static void Run()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SmartPingTcpAverageTests"));
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var passed = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            Console.WriteLine($"PASS {description}");
            passed++;
        }

        try
        {
            var options = Options.Create(new SmartPingOptions { DataDirectory = directory });
            var store = new Store(options);
            var now = DateTimeOffset.UtcNow;
            // One-hour histories use 15-second buckets. Keep these fixtures away from bucket boundaries.
            var bucket = DateTimeOffset.FromUnixTimeMilliseconds(now.AddMinutes(-2).ToUnixTimeMilliseconds() / 15000 * 15000);
            store.UpsertNode(new("local", "Local test node", "192.0.2.10", now, true));
            store.UpsertNode(new("agent", "Remote test node", "192.0.2.20", now));
            var mixed = new TargetConfig
            {
                Id = "tcp-average-mixed", Name = "Mixed TCP", Host = "example.test", Protocol = "tcp",
                NodeIds = ["local", "agent"]
            };
            store.SaveTarget(mixed);
            store.SaveSamples([
                new("tcp-normal-10", mixed.Id, "local", bucket.AddSeconds(1), 10, true, false, null),
                new("tcp-normal-30", mixed.Id, "agent", bucket.AddSeconds(2), 30, true, false, null),
                new("tcp-suspected-500", mixed.Id, "agent", bucket.AddSeconds(3), 500, true, true, null),
                new("tcp-timeout", mixed.Id, "local", bucket.AddSeconds(4), null, false, false, "Timeout")
            ]);

            var mixedHistory = store.GetHistory(mixed, 1, null);
            Check(mixedHistory.Stats.AvgLatencyMs == 20 && mixedHistory.Stats.AvgLatencySampleCount == 2,
                "TCP mean excludes suspected retransmissions and counts only eligible replies");
            Check(mixedHistory.Stats.Sent == 4 && mixedHistory.Stats.Received == 3
                  && mixedHistory.Stats.LossPercent == 25 && mixedHistory.Stats.RetransmitPercent == 25,
                "Excluding suspected retransmissions from TCP mean preserves delivery and retransmission statistics");
            Check(mixedHistory.Stats.MinLatencyMs == 10 && mixedHistory.Stats.MaxLatencyMs == 500
                  && mixedHistory.Stats.JitterMs == 470,
                "TCP minimum, maximum and per-node jitter retain suspected retransmission latency");
            Check(mixedHistory.Recent.Single(s => s.Id == "tcp-suspected-500") is
                  { LatencyMs: 500, Success: true, SuspectedRetransmit: true }
                  && mixedHistory.Recent.Count == 4,
                "Raw TCP history retains suspected retransmission flags and measured latencies");
            Check(mixedHistory.Series.Count == 1 && mixedHistory.Series[0] is
                  { LatencyMs: 20, MinLatencyMs: 10, MaxLatencyMs: 500, Sent: 4, LossPercent: 25, RetransmitPercent: 25 },
                "TCP chart buckets exclude suspected retransmissions only from mean latency");

            var local = store.GetHistory(mixed, 1, "local");
            var agent = store.GetHistory(mixed, 1, "agent");
            Check(local.Stats is { AvgLatencyMs: 10, AvgLatencySampleCount: 1, Sent: 2, Received: 1, LossPercent: 50 }
                  && agent.Stats is { AvgLatencyMs: 30, AvgLatencySampleCount: 1, Sent: 2, Received: 2, RetransmitPercent: 50 },
                "Node-filtered TCP averages use only each source's eligible replies");
            Check(agent.Series.Count == 1 && agent.Series[0].LatencyMs == 30
                  && agent.Series[0].MaxLatencyMs == 500,
                "Node-filtered TCP chart mean excludes suspected retransmissions while retaining extrema");
            var withoutRecent = store.GetHistory(mixed, 1, null, includeRecent: false);
            Check(withoutRecent.Stats.AvgLatencyMs == 20 && withoutRecent.Stats.AvgLatencySampleCount == 2
                  && withoutRecent.Recent.Count == 0,
                "Dashboard summary requests receive the same TCP mean and eligible sample count");

            var allSuspected = mixed with { Id = "tcp-average-all-suspected", Name = "All suspected", NodeIds = ["agent"] };
            store.SaveTarget(allSuspected);
            store.SaveSamples([
                new("all-suspected-1", allSuspected.Id, "agent", bucket.AddSeconds(1), 400, true, true, null),
                new("all-suspected-2", allSuspected.Id, "agent", bucket.AddSeconds(2), 600, true, true, null)
            ]);
            var allHistory = store.GetHistory(allSuspected, 1, "agent");
            Check(allHistory.Stats is { AvgLatencyMs: null, AvgLatencySampleCount: 0, Sent: 2, Received: 2,
                    LossPercent: 0, RetransmitPercent: 100 },
                "All-suspected TCP replies produce an unavailable mean without becoming packet loss");
            Check(allHistory.Series.Count == 1 && allHistory.Series[0] is
                  { LatencyMs: null, MinLatencyMs: 400, MaxLatencyMs: 600, LossPercent: 0, RetransmitPercent: 100 },
                "An all-suspected TCP chart bucket has a null mean and retains its other metrics");
            Check(allHistory.Stats.JitterMs == 200 && allHistory.Stats.LastLatencyMs == 600,
                "All-suspected TCP history still preserves jitter and the latest actual latency");

            var empty = mixed with { Id = "tcp-average-empty", Name = "No samples" };
            store.SaveTarget(empty);
            var emptyHistory = store.GetHistory(empty, 1, null);
            Check(emptyHistory.Stats is { Sent: 0, Received: 0, AvgLatencyMs: null, AvgLatencySampleCount: 0 }
                  && emptyHistory.Series.Count == 0,
                "A TCP target without samples has a null mean and zero eligible sample count");

            var severalBuckets = mixed with { Id = "tcp-average-buckets", Name = "Multiple buckets", NodeIds = ["local"] };
            store.SaveTarget(severalBuckets);
            store.SaveSamples([
                new("bucket-normal-10", severalBuckets.Id, "local", bucket.AddSeconds(1), 10, true, false, null),
                new("bucket-normal-30", severalBuckets.Id, "local", bucket.AddSeconds(2), 30, true, false, null),
                new("bucket-suspected-500", severalBuckets.Id, "local", bucket.AddSeconds(3), 500, true, true, null),
                new("bucket-all-suspected-400", severalBuckets.Id, "local", bucket.AddSeconds(31), 400, true, true, null),
                new("bucket-all-suspected-600", severalBuckets.Id, "local", bucket.AddSeconds(32), 600, true, true, null),
                new("bucket-normal-100", severalBuckets.Id, "local", bucket.AddSeconds(61), 100, true, false, null)
            ]);
            var bucketHistory = store.GetHistory(severalBuckets, 1, null);
            Check(bucketHistory.Series.Count == 3 && bucketHistory.Series[0].LatencyMs == 20
                  && bucketHistory.Series[1].LatencyMs is null && bucketHistory.Series[2].LatencyMs == 100,
                "Each TCP chart bucket independently excludes suspected retransmission samples");
            Check(bucketHistory.Stats.AvgLatencySampleCount == 3
                  && Math.Abs(bucketHistory.Stats.AvgLatencyMs!.Value - 140.0 / 3) < 0.000001,
                "TCP history mean is weighted by eligible replies across differently populated chart buckets");

            var icmp = mixed with { Id = "icmp-average-unchanged", Name = "ICMP compatibility", Protocol = "icmp" };
            store.SaveTarget(icmp);
            store.SaveSamples(mixedHistory.Recent.Select(s => s with { Id = "icmp-" + s.Id, TargetId = icmp.Id }));
            var icmpHistory = store.GetHistory(icmp, 1, null);
            Check(icmpHistory.Stats.AvgLatencyMs == 180 && icmpHistory.Stats.AvgLatencySampleCount == 3
                  && icmpHistory.Series.Single().LatencyMs == 180,
                "ICMP means retain every reply even if an imported sample has a retransmission flag");
            Check(icmpHistory.Stats.Sent == 4 && icmpHistory.Stats.Received == 3
                  && icmpHistory.Stats.MinLatencyMs == 10 && icmpHistory.Stats.MaxLatencyMs == 500
                  && icmpHistory.Stats.JitterMs == 470,
                "ICMP packet accounting and other latency statistics remain unchanged");

            var windows = mixed with { Id = "tcp-average-window", Name = "Window filter", NodeIds = ["local"] };
            store.SaveTarget(windows);
            store.SaveSamples([
                new("window-old", windows.Id, "local", now.AddHours(-2), 2, true, false, null),
                new("window-current", windows.Id, "local", now.AddSeconds(-20), 12, true, false, null),
                new("window-suspected", windows.Id, "local", now.AddSeconds(-10), 600, true, true, null),
                new("window-future", windows.Id, "local", now.AddMinutes(5), 900, true, false, null)
            ]);
            var current = store.GetHistory(windows, 1, null);
            var extended = store.GetHistory(windows, 3, null);
            Check(current.Stats is { AvgLatencyMs: 12, AvgLatencySampleCount: 1, Sent: 2 }
                  && current.Series.Sum(p => p.Sent) == 2,
                "TCP mean, count and chart buckets respect the requested time window and reject future samples");
            Check(extended.Stats is { AvgLatencyMs: 7, AvgLatencySampleCount: 2, Sent: 3 },
                "Extending the TCP history window includes older eligible replies in its mean and count");

            var weightedTarget = mixed with { Id = "tcp-average-weighted-summary", Name = "One eligible reply", NodeIds = ["local"] };
            store.SaveTarget(weightedTarget);
            store.SaveSamples([
                new("weighted-normal", weightedTarget.Id, "local", bucket.AddSeconds(1), 100, true, false, null),
                new("weighted-suspected-1", weightedTarget.Id, "local", bucket.AddSeconds(2), 500, true, true, null),
                new("weighted-suspected-2", weightedTarget.Id, "local", bucket.AddSeconds(3), 600, true, true, null)
            ]);
            var weightedHistory = store.GetHistory(weightedTarget, 1, null, includeRecent: false);
            Check(weightedHistory.Stats is { AvgLatencyMs: 100, AvgLatencySampleCount: 1, Received: 3 }
                  && mixedHistory.Stats is { AvgLatencyMs: 20, AvgLatencySampleCount: 2, Received: 3 },
                "Target summaries expose eligible reply counts independently of received counts for weighted dashboard means");

            var missingLatency = mixed with { Id = "tcp-average-missing-latency", Name = "No measured latency", NodeIds = ["local"] };
            store.SaveTarget(missingLatency);
            store.SaveSamples([new("missing-latency", missingLatency.Id, "local", bucket.AddSeconds(1), null, true, false, null)]);
            var missingHistory = store.GetHistory(missingLatency, 1, null);
            Check(missingHistory.Stats is { AvgLatencyMs: null, AvgLatencySampleCount: 0, Received: 1, LossPercent: 0 },
                "Replies without a measured latency do not contribute to the mean sample count");

            var reopened = new Store(options);
            var persisted = reopened.GetHistory(reopened.GetTarget(mixed.Id)!, 1, null);
            Check(persisted.Stats is { AvgLatencyMs: 20, AvgLatencySampleCount: 2, Sent: 4, Received: 3 }
                  && persisted.Recent.Single(s => s.Id == "tcp-suspected-500").LatencyMs == 500,
                "Existing persisted TCP samples immediately use the corrected mean without altering stored measurements");
            Console.WriteLine($"All {passed} TCP average regression checks passed.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var resolved = Path.GetFullPath(directory);
            var safeRoot = root + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
                Directory.Delete(resolved, recursive: true);
        }
    }
}
