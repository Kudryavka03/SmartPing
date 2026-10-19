using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SmartPing.Models;
using SmartPing.Services;

public static class HistoryWindowTests
{
    public static void Run()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SmartPingHistoryWindowTests"));
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var passed = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            Console.WriteLine($"PASS {description}");
            passed++;
        }
        static bool Near(double? actual, double expected) => actual.HasValue && Math.Abs(actual.Value - expected) < .000001;

        try
        {
            Check(HistoryQuery.TryCreate(null, null, null, out var defaults, out var defaultError)
                && defaults is { Hours: 1, RequestedBucketSeconds: 15, Minutes: 60 } && defaultError is null,
                "Default history query selects sixty minutes with fifteen-second aggregation");
            foreach (var minutes in new[] { 1, 3, 5, 10, 30, 60 })
                Check(HistoryQuery.TryCreate(null, minutes, 1, out var query, out _)
                    && Near(query?.Minutes, minutes) && query!.RequestedBucketSeconds == 1,
                    $"Validated {minutes}-minute query retains its exact duration and one-second interval");
            foreach (var minutes in new[] { -1, 0, 61, int.MaxValue })
                Check(!HistoryQuery.TryCreate(null, minutes, null, out var query, out var error)
                    && query is null && !string.IsNullOrEmpty(error), $"Invalid custom period {minutes} is rejected with a validation message");
            foreach (var seconds in new[] { -1, 0, 3601, int.MaxValue })
                Check(!HistoryQuery.TryCreate(null, null, seconds, out _, out _), $"Invalid aggregation interval {seconds} is rejected");
            Check(!HistoryQuery.TryCreate(1, 5, 15, out _, out _), "Minute and hour parameters cannot be supplied together");
            foreach (var hours in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                Check(!HistoryQuery.TryCreate(hours, null, null, out _, out _), "Nonfinite legacy durations are rejected");
            Check(HistoryQuery.TryCreate(0, null, null, out var lowerBound, out _) && Near(lowerBound?.Minutes, 1)
                && HistoryQuery.TryCreate(999, null, null, out var upperBound, out _) && upperBound!.Hours == 168,
                "Legacy hour queries clamp to one minute through seven days");

            var store = new Store(Options.Create(new SmartPingOptions { DataDirectory = directory }));
            var now = DateTimeOffset.UtcNow;
            store.UpsertNode(new("local", "Window local", "192.0.2.10", now, true));
            store.UpsertNode(new("agent", "Window agent", "192.0.2.20", now));
            TargetConfig Target(string id, string protocol = "icmp", string[]? nodes = null)
            {
                var target = new TargetConfig
                {
                    Id = id, Name = id, Host = "example.test", Protocol = protocol,
                    Enabled = false, NodeIds = nodes ?? ["local"]
                };
                store.SaveTarget(target);
                return target;
            }
            ProbeSample Sample(TargetConfig target, string id, DateTimeOffset time, double? latency = 10,
                bool success = true, bool suspected = false, string node = "local") =>
                new($"{target.Id}-{id}", target.Id, node, time, latency, success, suspected, success ? null : "Timeout");

            var windows = Target("windows");
            var offsets = new[] { 30, 120, 240, 420, 1200, 2700, 4200 };
            store.SaveSamples(offsets.Select((offset, index) => Sample(windows, index.ToString(), now.AddSeconds(-offset), index + 1)));
            var minuteWindows = new[] { 1, 3, 5, 10, 30, 60 };
            for (var i = 0; i < minuteWindows.Length; i++)
            {
                var minutes = minuteWindows[i];
                var history = store.GetHistory(windows, minutes / 60.0, null);
                Check(history.Stats.Sent == i + 1 && history.Series.Sum(point => point.Sent) == i + 1
                    && history.Recent.Count == i + 1, $"{minutes}-minute statistics, series and recent samples use the same exact window");
                Check(Near(history.Stats.AvgLatencyMs, (i + 2) / 2.0), $"{minutes}-minute average excludes older measurements");
            }
            Check(store.GetHistory(windows, 2.0 / 60, null).Stats.Sent == 1,
                "A custom two-minute window is not widened to a legacy fifteen-minute minimum");

            var dense = Target("granularity");
            // The first sample is aligned to every tested bucket; all samples remain safely inside a ten-minute window.
            var aligned = DateTimeOffset.FromUnixTimeSeconds(now.AddMinutes(-5).ToUnixTimeSeconds() / 30 * 30);
            var denseSamples = Enumerable.Range(0, 60).Select(index =>
                Sample(dense, index.ToString("D3"), aligned.AddSeconds(index).AddMilliseconds(100), index + 1,
                    success: index % 4 != 0)).ToArray();
            store.SaveSamples(denseSamples);
            foreach (var seconds in new[] { 1, 5, 15, 30 })
            {
                var history = store.GetHistory(dense, 10.0 / 60, null, bucketSeconds: seconds);
                Check(history.BucketSeconds == seconds && history.Series.Count == 60 / seconds,
                    $"Requested {seconds}-second aggregation produces the expected nonempty buckets");
                Check(history.Series.Sum(point => point.Sent) == 60 && history.Stats.Sent == 60
                    && history.Stats.Received == 45 && history.Stats.LossPercent == 25,
                    $"{seconds}-second aggregation conserves real samples and loss counts");
                Check(history.Series.All(point => point.Timestamp.ToUnixTimeSeconds() % seconds == 0),
                    $"{seconds}-second chart timestamps align to the actual aggregation interval");
                Check(Near(history.Stats.AvgLatencyMs, denseSamples.Where(sample => sample.Success).Average(sample => sample.LatencyMs!.Value)),
                    $"Changing to {seconds}-second buckets does not alter the overall average");
            }
            Check(store.GetHistory(dense, 1, null).BucketSeconds == 15,
                "Omitting the aggregation interval preserves the default fifteen-second buckets");

            var empty = store.GetHistory(Target("empty"), 1.0 / 60, null, bucketSeconds: 1);
            Check(empty.Series.Count == 0 && empty.Stats.Sent == 0 && empty.Stats.LossPercent == 0,
                "Empty windows do not invent time buckets or packet loss");
            var sparse = Target("sparse");
            store.SaveSamples([Sample(sparse, "first", aligned), Sample(sparse, "second", aligned.AddSeconds(59))]);
            var sparseHistory = store.GetHistory(sparse, 10.0 / 60, null, bucketSeconds: 1);
            Check(sparseHistory.Series.Count == 2 && sparseHistory.Series.All(point => point is { Sent: 1, LossPercent: 0 }),
                "Missing probe intervals remain gaps instead of fabricated failed probes");

            var tcp = Target("tcp-window", "tcp", ["local", "agent"]);
            var tcpStart = aligned.AddSeconds(1);
            store.SaveSamples([
                Sample(tcp, "01", tcpStart, 10),
                Sample(tcp, "02", tcpStart.AddSeconds(1), 12),
                Sample(tcp, "03", tcpStart.AddSeconds(2), 500, suspected: true),
                Sample(tcp, "04", tcpStart.AddSeconds(3), 20),
                Sample(tcp, "05", tcpStart.AddSeconds(4), 23),
                Sample(tcp, "06", tcpStart.AddSeconds(5), null, success: false),
                Sample(tcp, "07", tcpStart.AddSeconds(6), 30),
                Sample(tcp, "agent-01", tcpStart, 100, node: "agent"),
                Sample(tcp, "agent-02", tcpStart.AddSeconds(1), 110, node: "agent"),
                Sample(tcp, "old", now.AddHours(-2), 1)
            ]);
            foreach (var seconds in new[] { 1, 15, 30 })
            {
                var history = store.GetHistory(tcp, 10.0 / 60, "local", bucketSeconds: seconds);
                Check(history.Stats is { Sent: 7, Received: 6, AvgLatencySampleCount: 5 }
                    && Near(history.Stats.AvgLatencyMs, 19) && Near(history.Stats.JitterMs, 2.5),
                    $"{seconds}-second TCP aggregation retains average and jitter exclusions and breaks retransmit/loss pairs");
                Check(history.Series.Sum(point => point.Sent) == 7
                    && history.Stats.MinLatencyMs == 10 && history.Stats.MaxLatencyMs == 500,
                    $"{seconds}-second TCP aggregation retains raw extrema and successful retransmits");
            }
            var filtered = store.GetHistory(tcp, 10.0 / 60, "agent", bucketSeconds: 5);
            Check(filtered.Stats is { Sent: 2, AvgLatencyMs: 105, JitterMs: 10 }
                && filtered.Series.Sum(point => point.Sent) == 2 && filtered.Recent.All(sample => sample.NodeId == "agent"),
                "Custom windows and bucket intervals consistently respect the selected source node");
            var summaryOnly = store.GetHistory(tcp, 10.0 / 60, "local", includeRecent: false, bucketSeconds: 1);
            Check(summaryOnly.Recent.Count == 0 && summaryOnly.Stats.Sent == 7 && summaryOnly.BucketSeconds == 1,
                "Dashboard history mode preserves requested aggregation while omitting recent records");

            var bounded = Target("bounded");
            store.SaveSamples(Enumerable.Range(0, 4000).Select(index =>
                Sample(bounded, index.ToString("D4"), now.AddSeconds(-10 - index * 150))));
            var longHistory = store.GetHistory(bounded, 168, null, bucketSeconds: 1);
            Check(longHistory.BucketSeconds >= 168 && longHistory.Series.Count <= 3600
                && longHistory.Stats.Sent == 4000 && longHistory.Series.Sum(point => point.Sent) == 4000,
                "A seven-day request discloses coarser buckets, stays bounded and conserves every measurement");
            var compact = store.GetHistory(dense, 1, null, bucketSeconds: 1, maxPoints: 240);
            Check(compact.BucketSeconds >= 15 && compact.Series.Count <= 240 && compact.Series.Sum(point => point.Sent) == 60,
                "Compact dashboard series use their own bounded interval without changing totals");
            var allowedLargeBucket = store.GetHistory(dense, 1.0 / 60, null, bucketSeconds: 3600);
            Check(allowedLargeBucket.BucketSeconds == 3600 && allowedLargeBucket.Stats.Sent == 0,
                "A large aggregation interval never expands the selected statistics window");

            Console.WriteLine($"All {passed} history window and aggregation checks passed.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
                Directory.Delete(resolved, recursive: true);
        }
    }
}
