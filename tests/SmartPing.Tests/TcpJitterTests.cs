using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SmartPing.Models;
using SmartPing.Services;

public static class TcpJitterTests
{
    public static void Run()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SmartPingTcpJitterTests"));
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var passed = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            Console.WriteLine($"PASS {description}");
            passed++;
        }
        static bool Near(double? actual, double expected) => actual.HasValue && Math.Abs(actual.Value - expected) < 0.000001;

        try
        {
            var options = Options.Create(new SmartPingOptions { DataDirectory = directory });
            var store = new Store(options);
            var now = DateTimeOffset.UtcNow;
            // All ordinary fixtures fit inside one 15-second chart bucket.
            var start = DateTimeOffset.FromUnixTimeMilliseconds(now.AddMinutes(-2).ToUnixTimeMilliseconds() / 15000 * 15000);
            store.UpsertNode(new("local", "Local test node", "192.0.2.10", now, true));
            store.UpsertNode(new("agent", "Remote test node", "192.0.2.20", now));

            TargetConfig Target(string id, string protocol = "tcp", string[]? nodes = null)
            {
                var target = new TargetConfig
                {
                    Id = id, Name = id, Host = "example.test", Protocol = protocol, NodeIds = nodes ?? ["local"]
                };
                store.SaveTarget(target);
                return target;
            }
            ProbeSample Sample(TargetConfig target, string id, int seconds, double? latency,
                bool success = true, bool suspected = false, string node = "local") =>
                new($"{target.Id}-{id}", target.Id, node, start.AddSeconds(seconds), latency, success, suspected,
                    success ? null : "Timeout");

            var mixed = Target("jitter-mixed");
            ProbeSample[] mixedSamples = [
                Sample(mixed, "01", 1, 10),
                Sample(mixed, "02", 2, 12),
                Sample(mixed, "03", 3, 500, suspected: true),
                Sample(mixed, "04", 4, 14),
                Sample(mixed, "05", 5, 15),
                Sample(mixed, "06", 6, null, success: false),
                Sample(mixed, "07", 7, 20),
                Sample(mixed, "08", 8, 22)
            ];
            store.SaveSamples(mixedSamples);
            var mixedHistory = store.GetHistory(mixed, 1, null);
            Check(Near(mixedHistory.Stats.JitterMs, 5.0 / 3),
                "TCP jitter averages adjacent eligible differences 2, 1 and 2 without bridging excluded attempts");
            Check(mixedHistory.Stats is { AvgLatencyMs: 15.5, AvgLatencySampleCount: 6, MinLatencyMs: 10,
                    MaxLatencyMs: 500, LastLatencyMs: 22, Sent: 8, Received: 7, LossPercent: 12.5, RetransmitPercent: 12.5 },
                "Correcting TCP jitter preserves mean, extrema, latest latency and delivery statistics");
            Check(mixedHistory.Series.Count == 1 && mixedHistory.Series[0] is { LatencyMs: 15.5,
                    MinLatencyMs: 10, MaxLatencyMs: 500, Sent: 8, LossPercent: 12.5, RetransmitPercent: 12.5 },
                "Correcting TCP jitter preserves chart bucket metrics");
            Check(mixedHistory.Recent.OrderBy(s => s.Id).SequenceEqual(mixedSamples.OrderBy(s => s.Id)),
                "Correcting TCP jitter does not change raw measurements, retransmission flags or failures");
            var summary = store.GetHistory(mixed, 1, null, includeRecent: false);
            Check(Near(summary.Stats.JitterMs, 5.0 / 3) && summary.Recent.Count == 0,
                "Dashboard summary requests use the same corrected TCP jitter");

            var multipleNodes = Target("jitter-multiple-nodes", nodes: ["local", "agent"]);
            store.SaveSamples([
                Sample(multipleNodes, "01", 1, 10),
                Sample(multipleNodes, "02", 2, 100, node: "agent"),
                Sample(multipleNodes, "03", 3, 12),
                Sample(multipleNodes, "04", 4, 140, node: "agent"),
                Sample(multipleNodes, "05", 5, 400, suspected: true),
                Sample(multipleNodes, "06", 6, 110, node: "agent"),
                Sample(multipleNodes, "07", 7, 14)
            ]);
            Check(Near(store.GetHistory(multipleNodes, 1, null).Stats.JitterMs, 24),
                "Multi-node TCP jitter averages three within-node pairs and never compares nodes");
            Check(store.GetHistory(multipleNodes, 1, "local").Stats.JitterMs == 2
                  && store.GetHistory(multipleNodes, 1, "agent").Stats.JitterMs == 35,
                "Node filtering isolates eligible adjacent pairs for each TCP probe source");

            var boundaries = Target("jitter-suspected-boundaries");
            store.SaveSamples([
                Sample(boundaries, "01", 1, 800, suspected: true),
                Sample(boundaries, "02", 2, 10),
                Sample(boundaries, "03", 3, 12),
                Sample(boundaries, "04", 4, 900, suspected: true)
            ]);
            Check(store.GetHistory(boundaries, 1, null).Stats.JitterMs == 2,
                "Suspected retransmissions at the start and end do not contribute TCP jitter");

            var allSuspected = Target("jitter-all-suspected");
            store.SaveSamples([
                Sample(allSuspected, "01", 1, 400, suspected: true),
                Sample(allSuspected, "02", 2, 600, suspected: true)
            ]);
            Check(store.GetHistory(allSuspected, 1, null).Stats.JitterMs is null,
                "TCP jitter is unavailable when all replies are suspected retransmissions");

            var onlyOne = Target("jitter-one-eligible");
            store.SaveSamples([Sample(onlyOne, "01", 1, 10)]);
            Check(store.GetHistory(onlyOne, 1, null).Stats.JitterMs is null,
                "A single eligible TCP reply cannot produce jitter");

            var separated = Target("jitter-separated");
            store.SaveSamples([
                Sample(separated, "01", 1, 10),
                Sample(separated, "02", 2, 500, suspected: true),
                Sample(separated, "03", 3, 20)
            ]);
            Check(store.GetHistory(separated, 1, null).Stats.JitterMs is null,
                "Eligible TCP replies separated by a suspected retransmission do not form a jitter pair");

            var eachNodeOne = Target("jitter-one-per-node", nodes: ["local", "agent"]);
            store.SaveSamples([
                Sample(eachNodeOne, "01", 1, 10),
                Sample(eachNodeOne, "02", 2, 100, node: "agent")
            ]);
            Check(store.GetHistory(eachNodeOne, 1, null).Stats.JitterMs is null,
                "One eligible TCP reply per node does not create a cross-node jitter pair");

            var zero = Target("jitter-zero");
            store.SaveSamples([Sample(zero, "01", 1, 10), Sample(zero, "02", 2, 10)]);
            Check(store.GetHistory(zero, 1, null).Stats.JitterMs == 0,
                "Identical adjacent TCP reply latencies produce legitimate zero jitter");

            var empty = Target("jitter-empty");
            Check(store.GetHistory(empty, 1, null).Stats.JitterMs is null,
                "An empty TCP history has unavailable jitter");

            var missingLatency = Target("jitter-missing-latency");
            store.SaveSamples([
                Sample(missingLatency, "01", 1, 10),
                Sample(missingLatency, "02", 2, null),
                Sample(missingLatency, "03", 3, 20),
                Sample(missingLatency, "04", 4, 23)
            ]);
            Check(store.GetHistory(missingLatency, 1, null).Stats.JitterMs == 3,
                "A reply without measured latency breaks TCP adjacency without discarding later valid pairs");

            var failedLatency = Target("jitter-failed-with-latency");
            store.SaveSamples([
                Sample(failedLatency, "01", 1, 10),
                Sample(failedLatency, "02", 2, 500, success: false),
                Sample(failedLatency, "03", 3, 20),
                Sample(failedLatency, "04", 4, 22)
            ]);
            Check(store.GetHistory(failedLatency, 1, null).Stats.JitterMs == 2,
                "An unsuccessful TCP sample with recorded latency cannot contribute a jitter pair");

            var ordered = Target("jitter-stable-order");
            store.SaveSamples([
                Sample(ordered, "04", 2, 13),
                Sample(ordered, "02", 1, 500, suspected: true),
                Sample(ordered, "01", 1, 10),
                Sample(ordered, "03", 1, 12)
            ]);
            Check(store.GetHistory(ordered, 1, null).Stats.JitterMs == 1,
                "TCP adjacency follows timestamp and ID ordering regardless of insertion order");

            var windows = Target("jitter-window");
            store.SaveSamples([
                Sample(windows, "old", 1, 1) with { Timestamp = now.AddHours(-2) },
                Sample(windows, "current-1", 2, 10),
                Sample(windows, "current-2", 3, 12),
                Sample(windows, "future", 4, 900) with { Timestamp = now.AddMinutes(5) }
            ]);
            Check(store.GetHistory(windows, 1, null).Stats is { JitterMs: 2, Sent: 2 },
                "TCP jitter ignores pairs outside the selected window and excludes future samples");
            Check(store.GetHistory(windows, 3, null).Stats is { JitterMs: 5.5, Sent: 3 },
                "Expanding the TCP history window includes an older eligible adjacent pair");

            var oneInWindow = Target("jitter-one-in-window");
            store.SaveSamples([
                Sample(oneInWindow, "old", 1, 1) with { Timestamp = now.AddHours(-2) },
                Sample(oneInWindow, "current", 2, 10),
                Sample(oneInWindow, "future", 3, 900) with { Timestamp = now.AddMinutes(5) }
            ]);
            Check(store.GetHistory(oneInWindow, 1, null).Stats.JitterMs is null,
                "The first TCP reply in the window cannot pair with a predecessor outside that window");

            var caseInsensitive = Target("jitter-uppercase-protocol", "TCP");
            store.SaveSamples([
                Sample(caseInsensitive, "01", 1, 10),
                Sample(caseInsensitive, "02", 2, 500, suspected: true),
                Sample(caseInsensitive, "03", 3, 20)
            ]);
            Check(store.GetHistory(caseInsensitive, 1, null).Stats.JitterMs is null,
                "TCP jitter exclusions apply regardless of protocol casing");

            var icmp = Target("jitter-icmp-compatibility", "icmp");
            store.SaveSamples([
                Sample(icmp, "01", 1, 10),
                Sample(icmp, "02", 2, 500, suspected: true),
                Sample(icmp, "03", 3, 20)
            ]);
            Check(store.GetHistory(icmp, 1, null).Stats.JitterMs == 485,
                "ICMP jitter still includes a measured reply carrying an imported retransmission flag");

            var failedIcmp = Target("jitter-icmp-failed-with-latency", "icmp");
            store.SaveSamples([
                Sample(failedIcmp, "01", 1, 10),
                Sample(failedIcmp, "02", 2, 500, success: false),
                Sample(failedIcmp, "03", 3, 20),
                Sample(failedIcmp, "04", 4, 23)
            ]);
            Check(store.GetHistory(failedIcmp, 1, null).Stats.JitterMs == 3,
                "A malformed imported failed ICMP sample with latency breaks adjacency without contributing jitter");

            var reopened = new Store(options);
            var persisted = reopened.GetHistory(reopened.GetTarget(mixed.Id)!, 1, null);
            Check(Near(persisted.Stats.JitterMs, 5.0 / 3),
                "Previously persisted TCP samples immediately use corrected jitter after reopening storage");
            Check(persisted.Recent.OrderBy(s => s.Id).SequenceEqual(mixedSamples.OrderBy(s => s.Id))
                  && persisted.Stats is { AvgLatencyMs: 15.5, MinLatencyMs: 10, MaxLatencyMs: 500,
                      Sent: 8, Received: 7, LossPercent: 12.5, RetransmitPercent: 12.5 },
                "Reading corrected TCP jitter leaves stored samples and all other recorded metrics intact");

            Console.WriteLine($"All {passed} TCP jitter regression checks passed.");
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
