using System.Text;
using SmartPing.Models;

namespace SmartPing.Services;

/// <summary>Bounded, short-lived diagnostic jobs; monitoring history remains independent.</summary>
public sealed class TraceCoordinator(Store store, TraceRunner runner, ILogger<TraceCoordinator> logger) : BackgroundService
{
    public const int MaxOutputCharacters = 65_536;
    public const int MaxJobsPerNode = 2;
    public const int MaxActiveJobs = 16;
    private const int MaxRetainedJobs = 100;
    private readonly object gate = new();
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> agentVersions = new(StringComparer.Ordinal);
    private bool stopping;

    private sealed class Job(TraceJobSnapshot snapshot)
    {
        public TraceJobSnapshot Snapshot = snapshot;
        public readonly CancellationTokenSource Cancellation = new();
        public Task? LocalTask;
        public DateTimeOffset? LastReport;
        public bool CancelRequested;
        public bool AgentAcknowledged;
    }

    public TraceJobSnapshot? Create(TraceRequest request, out string? error, out int statusCode)
    {
        error = null;
        statusCode = 400;
        error = Validation.Trace(new(request.Host, request.MaxHops, request.TimeoutMs, request.Protocol, request.Port, request.Psize));
        if (error is not null) return null;
        var node = store.GetNodes().FirstOrDefault(n => n.Id == request.NodeId);
        if (node is null) { error = "所选执行节点不存在。"; return null; }
        if (!node.IsLocal && DateTimeOffset.UtcNow - node.LastSeen > TimeSpan.FromSeconds(35))
        { statusCode = 409; error = "节点已离线，请等待节点连接后再执行。"; return null; }
        var extended = request.Protocol == "tcp" || request.Psize is not null;
        if (node.IsLocal)
        {
            var tool = runner.GetTool();
            if (!tool.Available) { statusCode = 503; error = "未找到 ntrace.exe 或 Windows 系统 tracert.exe。"; return null; }
            if (extended && tool.Tool != "ntrace")
            { statusCode = 409; error = "TCP 路由追踪和 psize 需要 ntrace，系统 tracert 不支持这些选项。"; return null; }
        }

        lock (gate)
        {
            if (stopping) { statusCode = 503; error = "服务正在停止，请稍后重试。"; return null; }
            if (!node.IsLocal && extended && agentVersions.GetValueOrDefault(node.Id) < 2)
            { statusCode = 409; error = "请更新该节点的 Agent，以支持 TCP 和 psize，并等待节点重新注册。"; return null; }
            Prune(DateTimeOffset.UtcNow);
            var active = jobs.Values.Where(j => IsActive(j.Snapshot.Status) || j.LocalTask is { IsCompleted: false }).ToArray();
            if (active.Length >= MaxActiveJobs || active.Count(j => j.Snapshot.NodeId == request.NodeId) >= MaxJobsPerNode)
            { statusCode = 429; error = "路由追踪并发已达上限，每节点最多同时执行 2 个任务，请稍后再试。"; return null; }
            // Retain running jobs and evict the oldest completed diagnostic when memory is full.
            while (jobs.Count >= MaxRetainedJobs)
            {
                var oldest = jobs.Values.Where(j => !IsActive(j.Snapshot.Status) && j.LocalTask is not { IsCompleted: false })
                    .OrderBy(j => j.Snapshot.FinishedAt ?? j.Snapshot.CreatedAt).FirstOrDefault();
                if (oldest is null) { statusCode = 429; error = "路由追踪队列已满，请稍后再试。"; return null; }
                Remove(oldest.Snapshot.Id);
            }
            var snapshot = new TraceJobSnapshot(Guid.NewGuid().ToString("N"), request.NodeId, request.Host,
                request.MaxHops, request.TimeoutMs, "queued", null, "", false, null, DateTimeOffset.UtcNow, null, null, null,
                request.Protocol, request.Port, request.Psize);
            jobs.Add(snapshot.Id, new(snapshot));
            statusCode = 202;
            return snapshot;
        }
    }

    public TraceJobSnapshot? Get(string id)
    {
        lock (gate) return jobs.GetValueOrDefault(id)?.Snapshot;
    }

    public int AgentVersion(string nodeId)
    {
        if (nodeId == "local") return 2;
        lock (gate) return agentVersions.GetValueOrDefault(nodeId);
    }

    public void SetAgentVersion(string nodeId, int version)
    {
        var knownNodes = store.GetNodes().Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        lock (gate)
        {
            foreach (var stale in agentVersions.Keys.Where(id => !knownNodes.Contains(id)).ToArray()) agentVersions.Remove(stale);
            agentVersions[nodeId] = version;
            if (version >= 2) return;
            // An older Agent must never execute a queued TCP job as an ICMP trace by ignoring new fields.
            foreach (var job in jobs.Values.Where(j => j.Snapshot.NodeId == nodeId && IsActive(j.Snapshot.Status) &&
                         (j.Snapshot.Protocol == "tcp" || j.Snapshot.Psize is not null)))
            {
                job.CancelRequested = true;
                job.Snapshot = job.Snapshot with
                { Status = "failed", Error = "节点 Agent 版本不支持 TCP 或 psize，请更新后重试。", FinishedAt = DateTimeOffset.UtcNow };
            }
        }
    }

    public TraceJobSnapshot? Cancel(string id)
    {
        Job? job;
        CancellationTokenSource? cancellation = null;
        TraceJobSnapshot snapshot;
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out job)) return null;
            if (IsActive(job.Snapshot.Status))
            {
                job.CancelRequested = true;
                cancellation = job.Cancellation;
                job.Snapshot = job.Snapshot with { Status = "cancelled", FinishedAt = DateTimeOffset.UtcNow, Error = "任务已取消。" };
            }
            snapshot = job.Snapshot;
        }
        // A completed queued job may be evicted after releasing the lock; cancellation stays idempotent.
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        return snapshot;
    }

    public TraceAssignment[] Assignments(string nodeId, int traceVersion = 0)
    {
        lock (gate)
            // Omission is authoritative cancellation for an Agent's still-running local process.
            return jobs.Values.Where(j => j.Snapshot.NodeId == nodeId && nodeId != "local" && IsActive(j.Snapshot.Status) &&
                    (traceVersion >= 2 || j.Snapshot.Protocol == "icmp" && j.Snapshot.Psize is null))
                .Select(j => new TraceAssignment(j.Snapshot.Id, j.Snapshot.Host, j.Snapshot.MaxHops,
                    j.Snapshot.TimeoutMs, j.CancelRequested, j.Snapshot.Protocol, j.Snapshot.Port, j.Snapshot.Psize)).ToArray();
    }

    public bool Report(TraceAgentUpdate update, out string? error)
    {
        error = null;
        if (update.Status is not ("running" or "completed" or "failed" or "timedOut" or "cancelled") ||
            update.Tool is not (null or "ntrace" or "tracert"))
        { error = "路由追踪结果格式无效。"; return false; }
        lock (gate)
        {
            if (!jobs.TryGetValue(update.TraceId ?? "", out var job) || job.Snapshot.NodeId != update.NodeId || update.NodeId == "local")
            { error = "路由追踪任务不存在或未分配给此节点。"; return false; }
            if (!IsActive(job.Snapshot.Status))
            {
                if (update.Status != "running") job.AgentAcknowledged = true;
                return true; // Terminal reports are idempotent, including a late report after cancellation.
            }
            if (update.Status == "completed" && (job.Snapshot.Protocol == "tcp" || job.Snapshot.Psize is not null) && update.Tool != "ntrace")
            { error = "TCP 或 psize 任务必须由 ntrace 执行，不能以 tracert 结果完成。"; return false; }
            var output = PlainText(update.Output, MaxOutputCharacters);
            var now = DateTimeOffset.UtcNow;
            job.LastReport = now;
            job.AgentAcknowledged = update.Status != "running";
            job.Snapshot = job.Snapshot with
            {
                Status = update.Status, Tool = update.Tool, Output = output,
                Truncated = update.Truncated || (update.Output?.Length ?? 0) > MaxOutputCharacters,
                Error = update.Error is null ? null : PlainText(update.Error, 256), ExitCode = update.ExitCode,
                StartedAt = job.Snapshot.StartedAt ?? now, FinishedAt = update.Status == "running" ? null : now
            };
            return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            do
            {
                Job[] toStart;
                lock (gate)
                {
                    var now = DateTimeOffset.UtcNow;
                    Prune(now);
                    foreach (var job in jobs.Values.Where(j => j.Snapshot.NodeId != "local" && IsActive(j.Snapshot.Status)))
                    {
                        var queuedExpired = job.Snapshot.Status == "queued" && now - job.Snapshot.CreatedAt > TimeSpan.FromSeconds(45);
                        var stoppedReporting = job.Snapshot.Status == "running" && now - (job.LastReport ?? job.Snapshot.CreatedAt) > TimeSpan.FromSeconds(15);
                        var executionExpired = job.Snapshot.StartedAt is { } started && now - started > TimeSpan.FromSeconds(130);
                        if (!queuedExpired && !stoppedReporting && !executionExpired) continue;
                        job.CancelRequested = true;
                        job.Snapshot = job.Snapshot with
                        {
                            Status = executionExpired ? "timedOut" : "failed", FinishedAt = now,
                            Error = executionExpired ? "路由追踪超过执行时限。" : "Agent 未及时响应，任务已结束；请检查节点连接与版本。"
                        };
                    }
                    toStart = jobs.Values.Where(j => j.Snapshot.NodeId == "local" && j.Snapshot.Status == "queued").ToArray();
                    foreach (var job in toStart) job.Snapshot = job.Snapshot with { Status = "running", StartedAt = now };
                }
                foreach (var job in toStart) job.LocalTask = RunLocalAsync(job, stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            Job[] pending;
            lock (gate) { stopping = true; pending = jobs.Values.ToArray(); }
            foreach (var job in pending)
            {
                try { job.Cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            await Task.WhenAll(pending.Select(j => j.LocalTask ?? Task.CompletedTask));
        }
    }

    private async Task RunLocalAsync(Job job, CancellationToken stoppingToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cancellation.Token);
        try
        {
            var request = job.Snapshot;
            lock (gate) job.Snapshot = job.Snapshot with { Tool = runner.GetTool().Tool };
            var result = await runner.RunAsync(new(request.Host, request.MaxHops, request.TimeoutMs, request.Protocol, request.Port, request.Psize), chunk =>
            {
                lock (gate)
                {
                    if (!IsActive(job.Snapshot.Status)) return;
                    var remaining = MaxOutputCharacters - job.Snapshot.Output.Length;
                    job.Snapshot = job.Snapshot with
                    {
                        Output = job.Snapshot.Output + PlainText(chunk, remaining),
                        Truncated = job.Snapshot.Truncated || chunk.Length > remaining
                    };
                }
            }, cancellation.Token);
            lock (gate)
                job.Snapshot = job.Snapshot with
                {
                    Status = job.CancelRequested ? job.Snapshot.Status : result.Status,
                    Tool = result.Tool, Output = PlainText(result.Output, MaxOutputCharacters),
                    Truncated = result.Truncated, Error = job.CancelRequested ? job.Snapshot.Error : result.Error,
                    ExitCode = result.ExitCode, FinishedAt = DateTimeOffset.UtcNow
                };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Traceroute job failed: {TraceId}", job.Snapshot.Id);
            lock (gate)
                if (IsActive(job.Snapshot.Status)) job.Snapshot = job.Snapshot with
                { Status = "failed", Error = "执行路由追踪失败，请检查节点日志。", FinishedAt = DateTimeOffset.UtcNow };
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var job in jobs.Values.Where(j => j.Snapshot.FinishedAt is { } finished && now - finished > TimeSpan.FromMinutes(15) &&
                     j.LocalTask is not { IsCompleted: false }).ToArray()) Remove(job.Snapshot.Id);
    }

    private void Remove(string id)
    {
        if (jobs.Remove(id, out var job)) job.Cancellation.Dispose();
    }

    private static bool IsActive(string status) => status is "queued" or "running";

    private static string PlainText(string? value, int limit)
    {
        if (string.IsNullOrEmpty(value) || limit <= 0) return "";
        var text = new StringBuilder(Math.Min(value.Length, limit));
        foreach (var c in value)
        {
            if (text.Length >= limit) break;
            if (!char.IsControl(c) || c is '\r' or '\n' or '\t') text.Append(c);
        }
        return text.ToString();
    }
}
