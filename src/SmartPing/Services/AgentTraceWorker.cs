using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartPing.Models;

namespace SmartPing.Services;

// Diagnostics have their own worker so process execution and report retries never block normal probes.
public sealed class AgentTraceWorker(Store store, TraceRunner runner, IOptions<SmartPingOptions> options,
    ILogger<AgentTraceWorker> logger) : BackgroundService
{
    private const int MaxConcurrentJobs = 2;
    private const int MaxOutputLength = 65_536;
    private static readonly TimeSpan CenterLease = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict };
    private long _lastSuccessfulPoll;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configuration = options.Value;
        if (!configuration.Mode.Equals("agent", StringComparison.OrdinalIgnoreCase)) return;
        if (!Uri.TryCreate(configuration.ServerUrl.TrimEnd('/') + "/", UriKind.Absolute, out var serverUri)
            || serverUri.Scheme is not ("http" or "https"))
        {
            logger.LogError("Trace agent requires an absolute HTTP or HTTPS ServerUrl");
            return;
        }

        var nodeId = string.IsNullOrWhiteSpace(configuration.NodeId) ? store.GetOrCreateNodeId() : configuration.NodeId;
        using var client = new HttpClient { BaseAddress = serverUri, Timeout = TimeSpan.FromSeconds(5) };
        if (!string.IsNullOrWhiteSpace(configuration.AgentToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", configuration.AgentToken);

        var jobs = new Dictionary<string, JobState>(StringComparer.Ordinal);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        Interlocked.Exchange(ref _lastSuccessfulPoll, Stopwatch.GetTimestamp());
        try
        {
            do
            {
                try
                {
                    using var response = await client.GetAsync(
                        $"api/agent/traces?nodeId={Uri.EscapeDataString(nodeId)}&traceVersion=2", stoppingToken);
                    // The probe worker registers this identity independently. Initial 404s are expected.
                    if (response.StatusCode == HttpStatusCode.NotFound) continue;
                    response.EnsureSuccessStatusCode();
                    var assignments = await response.Content.ReadFromJsonAsync<AssignmentsResponse>(JsonOptions, stoppingToken);
                    if (assignments?.Traces is null || assignments.Traces.Length > MaxConcurrentJobs)
                        throw new InvalidDataException("Center returned an invalid trace assignment list.");
                    if (assignments.Traces.Any(assignment => assignment is null || string.IsNullOrWhiteSpace(assignment.Id)
                            || assignment.Id.Length > 100)
                        || assignments.Traces.Select(assignment => assignment.Id).Distinct(StringComparer.Ordinal).Count()
                            != assignments.Traces.Length)
                        throw new InvalidDataException("Center returned invalid or duplicate trace identities.");

                    Interlocked.Exchange(ref _lastSuccessfulPoll, Stopwatch.GetTimestamp());
                    var assignedIds = assignments.Traces.Select(assignment => assignment.Id).ToHashSet(StringComparer.Ordinal);
                    foreach (var pair in jobs.ToArray())
                    {
                        if (assignedIds.Contains(pair.Key)) continue;
                        // An authoritative response without the job also revokes its execution lease (e.g. center restart).
                        pair.Value.Orphaned = true;
                        pair.Value.Cancel();
                        if (pair.Value.Completion.IsCompleted) RemoveJob(jobs, pair.Key);
                    }

                    foreach (var assignment in assignments.Traces)
                    {
                        if (jobs.TryGetValue(assignment.Id, out var existing))
                        {
                            if (assignment.CancelRequested) existing.Cancel();
                            continue;
                        }
                        if (jobs.Count >= MaxConcurrentJobs) continue;

                        var state = new JobState(assignment, stoppingToken);
                        jobs.Add(assignment.Id, state);
                        if (assignment.CancelRequested) state.Cancel();
                        state.Completion = ExecuteJobAsync(state, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
                                                or JsonException or InvalidDataException)
                {
                    logger.LogWarning("Trace assignment poll failed: {Reason}", exception.Message);
                }

                foreach (var pair in jobs.ToArray())
                {
                    var state = pair.Value;
                    if (state.Orphaned)
                    {
                        if (state.Completion.IsCompleted) RemoveJob(jobs, pair.Key);
                        continue;
                    }
                    if (state.Acknowledged) continue;
                    var update = state.Snapshot(nodeId);
                    try
                    {
                        using var report = await client.PostAsJsonAsync("api/agent/traces/report", update, JsonOptions, stoppingToken);
                        report.EnsureSuccessStatusCode();
                        var acknowledgement = await report.Content.ReadFromJsonAsync<AcknowledgementResponse>(JsonOptions, stoppingToken);
                        if (acknowledgement?.Accepted != true)
                            throw new InvalidDataException("Center did not acknowledge the trace report.");
                        // Keep this ID until the next GET omits it, preventing execution of an unchanged assignment twice.
                        if (update.Status != "running") state.Acknowledged = true;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
                                                    or JsonException or InvalidDataException)
                    {
                        logger.LogWarning("Trace report {TraceId} failed: {Reason}; the result will be retried", pair.Key, exception.Message);
                    }
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var state in jobs.Values) state.Cancel();
            await Task.WhenAll(jobs.Values.Select(state => state.Completion));
            foreach (var state in jobs.Values) state.Dispose();
        }
    }

    private async Task ExecuteJobAsync(JobState state, CancellationToken stoppingToken)
    {
        using var leaseStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var lease = WatchLeaseAsync(state, leaseStop.Token);
        try
        {
            var assignment = state.Assignment;
            var request = new TraceExecutionRequest(assignment.Host, assignment.MaxHops, assignment.TimeoutMs,
                assignment.Protocol, assignment.Port, assignment.Psize);
            if (Validation.Trace(request) is { } validationError)
            {
                state.Finish(new TraceExecutionResult(null, "failed", null, "", false, validationError));
                return;
            }
            state.SetTool(runner.GetTool().Tool);
            var result = await runner.RunAsync(request,
                state.AppendOutput, state.Cancellation.Token);
            state.Finish(result);
        }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
        {
            state.Fail("cancelled", null);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Trace execution {TraceId} failed", state.Assignment.Id);
            state.Fail("failed", "路由追踪执行失败。");
        }
        finally
        {
            leaseStop.Cancel();
            await lease;
        }
    }

    private async Task WatchLeaseAsync(JobState state, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastSuccessfulPoll)) < CenterLease) continue;
                logger.LogWarning("Cancelling trace {TraceId}: no successful assignment poll for 15 seconds", state.Assignment.Id);
                state.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static void RemoveJob(Dictionary<string, JobState> jobs, string id)
    {
        if (jobs.Remove(id, out var state)) state.Dispose();
    }

    private sealed record AssignmentsResponse(TraceAssignment[] Traces);
    private sealed record AcknowledgementResponse(bool Accepted);

    private sealed class JobState(TraceAssignment assignment, CancellationToken stoppingToken) : IDisposable
    {
        private readonly object _gate = new();
        private readonly StringBuilder _output = new();
        private string _status = "running";
        private string? _tool;
        private bool _truncated;
        private int? _exitCode;
        private string? _error;

        public TraceAssignment Assignment { get; } = assignment;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        public Task Completion { get; set; } = Task.CompletedTask;
        public bool Acknowledged { get; set; }
        public bool Orphaned { get; set; }

        public void Cancel() => Cancellation.Cancel();

        public void SetTool(string? tool)
        {
            lock (_gate) _tool = tool;
        }

        public void AppendOutput(string text)
        {
            lock (_gate)
            {
                var remaining = MaxOutputLength - _output.Length;
                _output.Append(text.AsSpan(0, Math.Min(remaining, text.Length)));
                if (text.Length > remaining) _truncated = true;
            }
        }

        public void Finish(TraceExecutionResult result)
        {
            lock (_gate)
            {
                _tool = result.Tool;
                _status = result.Status;
                _exitCode = result.ExitCode;
                _error = result.Error;
                _truncated = result.Truncated || result.Output.Length > MaxOutputLength;
                _output.Clear().Append(result.Output.AsSpan(0, Math.Min(result.Output.Length, MaxOutputLength)));
            }
        }

        public void Fail(string status, string? error)
        {
            lock (_gate)
            {
                _status = status;
                _error = error;
            }
        }

        public TraceAgentUpdate Snapshot(string nodeId)
        {
            lock (_gate)
                return new TraceAgentUpdate(nodeId, Assignment.Id, _status, _tool, _output.ToString(), _truncated, _exitCode, _error);
        }

        public void Dispose() => Cancellation.Dispose();
    }
}
