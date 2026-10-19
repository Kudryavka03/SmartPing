namespace SmartPing.Models;

public record TargetConfig
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public string Protocol { get; init; } = "icmp";
    public int Port { get; init; } = 443;
    public int PacketSize { get; init; } = 32;
    public int IntervalSeconds { get; init; } = 5;
    public int TimeoutMs { get; init; } = 2000;
    public int RetransmitThresholdMs { get; init; } = 300;
    public bool Enabled { get; init; } = true;
    public string[] NodeIds { get; init; } = ["local"];
    public string Group { get; init; } = "默认分组";
}

public record ProbeSample(string Id, string TargetId, string NodeId, DateTimeOffset Timestamp,
    double? LatencyMs, bool Success, bool SuspectedRetransmit, string? Error);

public record NodeInfo(string Id, string Name, string Address, DateTimeOffset LastSeen, bool IsLocal = false);
public record AgentRegistration(string Id, string Name, string Address, int TraceVersion = 0);
public record AgentReport(string NodeId, ProbeSample[] Samples);
public record MeshRequest(string[] NodeIds, string Protocol = "icmp", int Port = 443,
    int PacketSize = 32, int IntervalSeconds = 5, int TimeoutMs = 2000, int RetransmitThresholdMs = 300);

public class SmartPingOptions
{
    public string Mode { get; set; } = "server";
    public string DataDirectory { get; set; } = "data";
    public int RetentionDays { get; set; } = 30;
    public string NodeName { get; set; } = Environment.MachineName;
    public string NodeAddress { get; set; } = "";
    public string ServerUrl { get; set; } = "http://127.0.0.1:5080";
    public string AgentToken { get; set; } = "";
    public string AdminToken { get; set; } = "";
    public string NodeId { get; set; } = "";
}
