namespace SmartPing.Models;

public record TraceRequest(string Host, string NodeId = "local", int MaxHops = 30, int TimeoutMs = 1000,
    string Protocol = "icmp", int? Port = null, int? Psize = null);
public record TraceExecutionRequest(string Host, int MaxHops = 30, int TimeoutMs = 1000,
    string Protocol = "icmp", int? Port = null, int? Psize = null);
public record TraceToolInfo(bool Available, string? Tool, bool SupportsTcp = false, bool SupportsPsize = false);
public record TraceExecutionResult(string? Tool, string Status, int? ExitCode, string Output, bool Truncated, string? Error);
public record TraceJobSnapshot(string Id, string NodeId, string Host, int MaxHops, int TimeoutMs, string Status,
    string? Tool, string Output, bool Truncated, string? Error, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int? ExitCode,
    string Protocol = "icmp", int? Port = null, int? Psize = null);
public record TraceAssignment(string Id, string Host, int MaxHops, int TimeoutMs, bool CancelRequested,
    string Protocol = "icmp", int? Port = null, int? Psize = null);
public record TraceAgentUpdate(string NodeId, string TraceId, string Status, string? Tool = null,
    string Output = "", bool Truncated = false, int? ExitCode = null, string? Error = null);
