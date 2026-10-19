using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartPing.Models;
using SmartPing.Services;

public static class TraceCoordinatorTests
{
    public static void Run()
    {
        var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SmartPingTraceCoordinatorTests"));
        var directory = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
        try
        {
            var store = new Store(Options.Create(new SmartPingOptions { DataDirectory = directory }));
            var now = DateTimeOffset.UtcNow;
            store.UpsertNode(new("local", "Center", "127.0.0.1", now, true));
            store.UpsertNode(new("agent", "Agent", "127.0.0.1", now));
            store.UpsertNode(new("other", "Other", "127.0.0.1", now));
            using var traces = new TraceCoordinator(store, new TraceRunner(() => null), NullLogger<TraceCoordinator>.Instance);

            var invalid = traces.Create(new("127.0.0.1&whoami", "agent"), out _, out var status);
            Check(invalid is null && status == 400, "Injection is rejected before creating a trace job");
            Check(traces.Create(new("127.0.0.1", "local"), out _, out status) is null && status == 503,
                "Missing native tools are reported without queueing a local job");
            Check(traces.Create(new("127.0.0.1", "missing"), out _, out status) is null && status == 400,
                "Unknown trace source is rejected");
            Check(traces.Create(new("127.0.0.1", "agent", 65), out _, out status) is null && status == 400,
                "Trace option bounds are enforced at job admission");

            var first = traces.Create(new("127.0.0.1", "agent"), out _, out status)!;
            Check(status == 202 && first.Status == "queued", "Valid remote trace starts as queued");
            var second = traces.Create(new("::1", "agent"), out _, out status)!;
            Check(traces.Create(new("localhost", "agent"), out _, out status) is null && status == 429,
                "At most two jobs can be active on one node");
            Check(traces.Assignments("agent").Length == 2 && traces.Assignments("local").Length == 0,
                "Only the assigned remote node receives its jobs");
            Check(!traces.Report(new("other", first.Id, "completed", "tracert"), out _),
                "One node cannot report another node's diagnostic");
            Check(!traces.Report(new("agent", first.Id, "running", "cmd"), out _),
                "Reports cannot introduce arbitrary executable names");
            Check(!traces.Report(new("agent", first.Id, "queued", "ntrace"), out _),
                "Invalid report states cannot change a job");
            Check(traces.Report(new("agent", first.Id, "running", "ntrace", "hop 1\n"), out _) &&
                traces.Get(first.Id)!.StartedAt is not null, "Remote progress updates preserve execution state");
            var hugeOutput = new string('x', 70_000);
            Check(traces.Report(new("agent", first.Id, "completed", "ntrace", hugeOutput, false, 0), out _),
                "Remote completion is accepted");
            var complete = traces.Get(first.Id)!;
            Check(complete.Status == "completed" && complete.Output.Length == 65_536 && complete.Truncated && complete.FinishedAt is not null,
                "All stored remote output is bounded and records truncation");
            Check(traces.Report(new("agent", first.Id, "failed", "tracert", "changed"), out _) && traces.Get(first.Id) == complete,
                "Repeated terminal reports are idempotent");
            Check(traces.Assignments("agent").Length == 1, "Completed jobs are omitted from authoritative assignments");

            var cancelled = traces.Cancel(second.Id)!;
            Check(cancelled.Status == "cancelled" && traces.Cancel(second.Id) == cancelled,
                "Cancellation is terminal and idempotent");
            Check(traces.Assignments("agent").Length == 0, "Cancelled jobs revoke the Agent execution lease");
            Check(traces.Report(new("agent", second.Id, "completed", "ntrace", "late output"), out _) &&
                traces.Get(second.Id)!.Status == "cancelled", "Late Agent completion cannot resurrect a cancelled job");

            var third = traces.Create(new("localhost", "agent"), out _, out status)!;
            Check(third is not null && status == 202, "Completed and cancelled jobs release queue capacity");
            traces.Report(new("agent", third!.Id, "completed", "tracert", "a\0b\b\r\n\tend"), out _);
            Check(traces.Get(third.Id)!.Output == "ab\r\n\tend", "Remote terminal controls are filtered before storage");
            var tcpRequest = new TraceRequest("127.0.0.1", "agent", Protocol: "tcp", Port: 443, Psize: 64);
            Check(traces.Create(tcpRequest, out _, out status) is null && status == 409,
                "Older Agents cannot silently execute TCP or psize tasks as ordinary ICMP");
            traces.SetAgentVersion("agent", 2);
            var tcp = traces.Create(tcpRequest, out _, out status)!;
            Check(status == 202 && tcp.Protocol == "tcp" && tcp.Port == 443 && tcp.Psize == 64,
                "Extended parameters are preserved in the diagnostic snapshot");
            Check(traces.Assignments("agent").Length == 0, "A legacy polling process cannot receive extended tasks despite a cached new Agent registration");
            var assignment = traces.Assignments("agent", 2).Single(a => a.Id == tcp.Id);
            Check(assignment.Protocol == "tcp" && assignment.Port == 443 && assignment.Psize == 64,
                "Extended parameters reach the assigned Agent without changes");
            Check(!traces.Report(new("agent", tcp.Id, "completed", "tracert", "ICMP result"), out _),
                "A TCP task cannot be completed with an unsupported tracert result");
            traces.SetAgentVersion("agent", 0);
            Check(traces.Get(tcp.Id)!.Status == "failed" && traces.Assignments("agent").Length == 0,
                "An Agent downgrade revokes incompatible queued work");
            Check(traces.Report(new("agent", tcp.Id, "completed", "ntrace", "late result"), out _) &&
                traces.Get(tcp.Id)!.Status == "failed", "A late report cannot revive a task revoked by an Agent downgrade");
            traces.SetAgentVersion("agent", 2);
            var customIcmp = traces.Create(new("127.0.0.1", "agent", Psize: 28), out _, out status)!;
            Check(status == 202 && customIcmp.Psize == 28 && customIcmp.Port is null,
                "Custom ICMP packet size is preserved without introducing a TCP port");
            traces.Cancel(customIcmp.Id);
            store.UpsertNode(new("agent", "Agent", "127.0.0.1", now.AddMinutes(-1)));
            Check(traces.Create(new("localhost", "agent"), out _, out status) is null && status == 409,
                "Offline Agents cannot receive new diagnostics");
            Check(traces.Cancel("missing") is null && traces.Get("missing") is null, "Unknown jobs are reported missing");
            Console.WriteLine("PASS Traceroute admission, report ownership, concurrency, cancellation, output limits and offline sources");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var resolved = Path.GetFullPath(directory);
            if (Path.GetDirectoryName(resolved) == testRoot && Guid.TryParseExact(Path.GetFileName(resolved), "N", out _) && Directory.Exists(resolved))
                Directory.Delete(resolved, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
