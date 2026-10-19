using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using SmartPing.Models;
using SmartPing.Services;

public static class ProbeTests
{
    public static async Task RunAsync()
    {
        var engine = new ProbeEngine();
        Check(!ProbeEngine.IsSuspectedRetransmit(300, 300)
              && !ProbeEngine.IsSuspectedRetransmit(299.999, 300)
              && ProbeEngine.IsSuspectedRetransmit(300.001, 300),
            "Suspected retransmission must use strict greater-than comparison on unrounded latency");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var target = new TargetConfig
        {
            Id = "tcp-test", Host = "127.0.0.1", Protocol = "tcp", Port = port,
            TimeoutMs = 1500, RetransmitThresholdMs = int.MaxValue
        };
        try
        {
            var accepted = listener.AcceptTcpClientAsync(deadline.Token);
            var success = await engine.ProbeAsync(target, "test-node", deadline.Token);
            using var peer = await accepted;
            Check(success.Success && success.LatencyMs is >= 0 && success.Error is null,
                "TCP listening port should return a successful measured connection");
            Check(!success.SuspectedRetransmit, "Fast TCP connection should not exceed a large threshold");
            Check(success.TargetId == target.Id && success.NodeId == "test-node",
                "Probe should retain target and node identity");

            accepted = listener.AcceptTcpClientAsync(deadline.Token);
            var slowThreshold = await engine.ProbeAsync(target with { RetransmitThresholdMs = 0 }, "test-node", deadline.Token);
            using var peer2 = await accepted;
            Check(slowThreshold.Success && slowThreshold.SuspectedRetransmit,
                "Positive handshake latency should exceed a zero retransmission threshold");
        }
        finally { listener.Stop(); }

        var refused = await engine.ProbeAsync(target with { TimeoutMs = 5000 }, "test-node", deadline.Token);
        Check(!refused.Success && refused.LatencyMs is null && !refused.SuspectedRetransmit
              && refused.Error == "Connection refused", $"Closed TCP port should fail without a latency or retransmit flag (actual error: {refused.Error})");

        foreach (var size in new[] { 0, 56, 1200 })
        {
            var ping = await engine.ProbeAsync(new TargetConfig
            {
                Id = "icmp-test", Host = "127.0.0.1", Protocol = "icmp", PacketSize = size,
                TimeoutMs = 1, RetransmitThresholdMs = 0
            }, "test-node", deadline.Token);
            Check(ping.Success && ping.LatencyMs is >= 0 && !ping.SuspectedRetransmit,
                $"ICMP localhost payload {size} should succeed and ignore TCP threshold fields");
        }

        var invalidPayload = await engine.ProbeAsync(new TargetConfig
        {
            Host = "127.0.0.1", Protocol = "icmp", PacketSize = -1
        }, "test-node", deadline.Token);
        Check(!invalidPayload.Success && invalidPayload.LatencyMs is null,
            "Invalid payload must become a failure instead of crashing the scheduler");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await engine.ProbeAsync(target, "test-node", canceled.Token);
            throw new InvalidOperationException("A canceled probe must propagate cancellation");
        }
        catch (OperationCanceledException) { }

        TestDurableOutbox();
    }

    private static void TestDurableOutbox()
    {
        var directory = Path.Combine(Path.GetTempPath(), "smartping-outbox-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var samples = Enumerable.Range(1, 5).Select(i => new ProbeSample($"sample-{i}", "target", "node",
            DateTimeOffset.UtcNow, i, true, false, null)).ToArray();
        try
        {
            using (var outbox = new DurableAgentOutbox(directory, 3))
            {
                Check(outbox.Count == 0, "New durable outbox should be empty");
                outbox.Enqueue(samples[0]);
                outbox.Enqueue(samples[0]);
                Check(outbox.Count == 1, "Outbox should deduplicate sample IDs");
                outbox.Enqueue(samples[1]);
                outbox.Enqueue(samples[2]);
                Check(outbox.Enqueue(samples[3]) == 1, "Full outbox should evict exactly one oldest sample");
                Check(outbox.Count == 3 && outbox.Peek(10).Select(sample => sample.Id)
                    .SequenceEqual(samples.Skip(1).Take(3).Select(sample => sample.Id)),
                    "Outbox must retain the latest samples in FIFO order after capacity eviction");
            }
            using (var restored = new DurableAgentOutbox(directory, 3))
            {
                Check(restored.Count == 3 && restored.Peek(1)[0] == samples[1],
                    "Queued samples and all fields should survive process-like reopening");
                var batch = restored.Peek(2);
                Check(restored.Count == 3, "Reading for transmission must retain unacknowledged samples");
                restored.Acknowledge(batch);
                restored.Acknowledge(batch);
                Check(restored.Count == 1 && restored.Peek(1)[0] == samples[3],
                    "Acknowledgement must be idempotent and remove only the transmitted batch");
                restored.Enqueue(samples[4]);
            }
            using (var restored = new DurableAgentOutbox(directory, 3))
                Check(restored.Peek(10).Select(sample => sample.Id).SequenceEqual(new[] { "sample-4", "sample-5" }),
                    "Acknowledgements should persist and later samples should keep FIFO order");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolved).StartsWith("smartping-outbox-test-", StringComparison.Ordinal))
                Directory.Delete(resolved, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
