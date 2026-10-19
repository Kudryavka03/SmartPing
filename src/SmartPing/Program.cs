using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SmartPing.Models;
using SmartPing.Services;

var contentRoot = File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "SmartPing.csproj"))
    ? Directory.GetCurrentDirectory() : AppContext.BaseDirectory;
Directory.SetCurrentDirectory(contentRoot);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = contentRoot });
// Agents run as a generic host: only outbound HTTP, no web listener or conflicting dashboard port.
if (builder.Configuration["SmartPing:Mode"] == "agent")
{
    var agentBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = contentRoot });
    var agentOptions = agentBuilder.Configuration.GetSection("SmartPing").Get<SmartPingOptions>() ?? new();
    if (string.IsNullOrWhiteSpace(agentOptions.AgentToken)) throw new InvalidOperationException("Agent mode requires SmartPing:AgentToken.");
    if (agentOptions.RetentionDays is < 1 or > 3650) throw new InvalidOperationException("RetentionDays must be between 1 and 3650.");
    agentBuilder.Services.AddWindowsService(o => o.ServiceName = agentBuilder.Configuration["ServiceName"] ?? "SmartPing");
    agentBuilder.Services.Configure<SmartPingOptions>(agentBuilder.Configuration.GetSection("SmartPing"));
    agentBuilder.Services.AddSingleton<Store>();
    agentBuilder.Services.AddSingleton<ProbeEngine>();
    agentBuilder.Services.AddSingleton<TraceRunner>();
    agentBuilder.Services.AddHostedService<AgentWorker>();
    agentBuilder.Services.AddHostedService<AgentTraceWorker>();
    using var agentHost = agentBuilder.Build();
    await agentHost.RunAsync();
    return;
}
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5080");
builder.Services.AddWindowsService(o => o.ServiceName = builder.Configuration["ServiceName"] ?? "SmartPing");
builder.Services.Configure<SmartPingOptions>(builder.Configuration.GetSection("SmartPing"));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton<ProbeEngine>();
builder.Services.AddSingleton<TraceRunner>();
builder.Services.AddSingleton<TraceCoordinator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<TraceCoordinator>());
builder.Services.AddHostedService<ProbeScheduler>();
builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddHostedService<RetentionWorker>();
builder.Services.AddHttpClient();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 2 * 1024 * 1024);

var app = builder.Build();
var options = app.Services.GetRequiredService<IOptions<SmartPingOptions>>().Value;
if (options.Mode is not ("server" or "agent")) throw new InvalidOperationException("SmartPing:Mode must be server or agent.");
if (options.RetentionDays is < 1 or > 3650) throw new InvalidOperationException("RetentionDays must be between 1 and 3650.");
if (options.Mode == "agent" && string.IsNullOrWhiteSpace(options.AgentToken)) throw new InvalidOperationException("Agent mode requires SmartPing:AgentToken.");
var configuredUrls = (builder.Configuration["urls"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
    .Concat(builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(e => e["Url"]).OfType<string>());
if (options.Mode == "server" && configuredUrls.Any(url => !IsLoopbackUrl(url)) &&
    (options.AdminToken.Length < 16 || options.AgentToken.Length < 16))
    throw new InvalidOperationException("Remote listening requires AdminToken and AgentToken of at least 16 characters. Use scripts/run.ps1 or configure SmartPing settings.");

var store = app.Services.GetRequiredService<Store>();
var traceCoordinator = app.Services.GetRequiredService<TraceCoordinator>();
if (options.Mode == "server")
    store.UpsertNode(new("local", options.NodeName, options.NodeAddress, DateTimeOffset.UtcNow, true));

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api") && context.Request.Path != "/api/health")
    {
        context.Response.Headers.CacheControl = "no-store";
        var agentPath = context.Request.Path.StartsWithSegments("/api/agent");
        if (options.Mode == "agent")
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsJsonAsync(new { error = "Agent 节点不提供管理面板，请访问中心端。" });
            return;
        }
        var expected = agentPath ? options.AgentToken : options.AdminToken;
        if (agentPath && string.IsNullOrEmpty(expected))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "请先在中心端配置 AgentToken。" });
            return;
        }
        if (!agentPath && string.IsNullOrEmpty(expected) &&
            (context.Connection.RemoteIpAddress is null || !IPAddress.IsLoopback(context.Connection.RemoteIpAddress) ||
             !IsLoopbackHost(context.Request.Host.Host)))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "未配置 AdminToken 时仅允许本机访问。" });
            return;
        }
        if (!string.IsNullOrEmpty(expected) && !MatchesToken(context.Request.Headers.Authorization.ToString(), expected))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "访问令牌无效或缺失。" });
            return;
        }
        if (!agentPath && context.Request.Method is not ("GET" or "HEAD") &&
            context.Request.Headers.Origin is var origin && !string.IsNullOrEmpty(origin) &&
            (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "禁止跨站修改配置。" });
            return;
        }
    }
    try { await next(); }
    catch (BadHttpRequestException ex)
    {
        context.Response.StatusCode = ex.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = "请求格式不正确。" });
    }
    catch (Exception ex) when (!context.Response.HasStarted)
    {
        app.Logger.LogError(ex, "Request failed: {Path}", context.Request.Path);
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "服务处理失败，请查看服务器日志。" });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", mode = options.Mode }));

object PublicSettings() => new
{
    options.Mode, options.NodeName, options.NodeAddress, options.RetentionDays,
    serverUrl = options.ServerUrl,
    agentTokenConfigured = !string.IsNullOrEmpty(options.AgentToken),
    adminTokenConfigured = !string.IsNullOrEmpty(options.AdminToken)
};

app.MapGet("/api/settings", () => Results.Ok(PublicSettings()));
app.MapGet("/api/nodes", () => Results.Ok(store.GetNodes().Select(NodeView)));
app.MapGet("/api/targets", () => Results.Ok(store.GetTargets()));

app.MapGet("/api/traces/tools", (TraceRunner runner) => Results.Ok(runner.GetTool()));
app.MapPost("/api/traces", (TraceRequest request, TraceCoordinator traces) =>
{
    var job = traces.Create(request, out var error, out var statusCode);
    return job is null ? Results.Json(new { error }, statusCode: statusCode) :
        Results.Accepted($"/api/traces/{job.Id}", job);
});
app.MapGet("/api/traces/{id}", (string id, TraceCoordinator traces) => traces.Get(id) is { } job ?
    Results.Ok(job) : Results.NotFound(new { error = "路由追踪任务不存在或已过期。" }));
app.MapDelete("/api/traces/{id}", (string id, TraceCoordinator traces) => traces.Cancel(id) is { } job ?
    Results.Ok(job) : Results.NotFound(new { error = "路由追踪任务不存在或已过期。" }));
app.MapGet("/api/dashboard", (double? hours, string? nodeId) =>
{
    var window = Math.Clamp(hours ?? 1, .25, 168);
    if (!double.IsFinite(window)) return Results.BadRequest(new { error = "时间范围无效。" });
    var nodes = store.GetNodes();
    var histories = store.GetTargets().Select(t => (target: t, history: store.GetHistory(t, window, nodeId, false))).ToArray();
    var sent = histories.Sum(h => h.history.Stats.Sent);
    var received = histories.Sum(h => h.history.Stats.Received);
    var sumLatency = histories.Sum(h => (h.history.Stats.AvgLatencyMs ?? 0) * h.history.Stats.Received);
    return Results.Ok(new
    {
        nodes = nodes.Select(NodeView),
        targets = histories.Select(h => new
        {
            h.target.Id, h.target.Name, h.target.Host, h.target.Protocol, h.target.Port,
            h.target.PacketSize, h.target.IntervalSeconds, h.target.TimeoutMs, h.target.RetransmitThresholdMs,
            h.target.Enabled, h.target.NodeIds, h.target.Group, h.history.Stats, h.history.Series
        }),
        summary = new
        {
            targets = histories.Length, nodes = nodes.Count, onlineNodes = nodes.Count(IsOnline), sent,
            lossPercent = sent > 0 ? 100.0 * (sent - received) / sent : 0,
            avgLatencyMs = received > 0 ? (double?)(sumLatency / received) : null
        },
        settings = PublicSettings(), serverTime = DateTimeOffset.UtcNow
    });
});
app.MapGet("/api/targets/{id}/history", (string id, double? hours, string? nodeId) =>
{
    var target = store.GetTarget(id);
    if (target is null) return Results.NotFound(new { error = "探测项目不存在。" });
    var window = Math.Clamp(hours ?? 1, .25, 168);
    if (!double.IsFinite(window)) return Results.BadRequest(new { error = "时间范围无效。" });
    return Results.Ok(store.GetHistory(target, window, nodeId));
});
app.MapPost("/api/targets", (TargetConfig target) =>
{
    if (store.GetTargets().Count >= 200) return Results.BadRequest(new { error = "当前版本最多支持 200 个目标。" });
    var error = Validation.Target(target, store.GetNodes());
    if (error is not null) return Results.BadRequest(new { error });
    target = Normalize(target with { Id = Guid.NewGuid().ToString("N") });
    if (store.TrySaveTarget(target) is { } conflict) return Results.Conflict(new { error = conflict });
    return Results.Created($"/api/targets/{target.Id}", target);
});
app.MapPut("/api/targets/{id}", (string id, TargetConfig target) =>
{
    if (store.GetTarget(id) is null) return Results.NotFound(new { error = "探测项目不存在。" });
    var error = Validation.Target(target, store.GetNodes());
    if (error is not null) return Results.BadRequest(new { error });
    target = Normalize(target with { Id = id });
    if (store.TrySaveTarget(target, true) is { } conflict) return Results.Conflict(new { error = conflict });
    return Results.Ok(target);
});
app.MapDelete("/api/targets/{id}", (string id) => store.DeleteTarget(id)
    ? Results.Ok(new { deleted = true }) : Results.NotFound(new { error = "探测项目不存在。" }));
app.MapDelete("/api/nodes/{id}", (string id) => id == "local"
    ? Results.BadRequest(new { error = "本机节点不可移除。" }) : store.RemoveNode(id)
    ? Results.Ok(new { deleted = true }) : Results.NotFound(new { error = "节点不存在。" }));

app.MapPost("/api/mesh", (MeshRequest request) =>
{
    if (request.NodeIds is null || request.NodeIds.Distinct().Count() < 2)
        return Results.BadRequest(new { error = "请选择至少两个节点。" });
    if (request.NodeIds.Distinct().Count() > 15) return Results.BadRequest(new { error = "一次最多选择 15 个节点。" });
    var allNodes = store.GetNodes();
    var selected = allNodes.Where(n => request.NodeIds.Contains(n.Id)).ToArray();
    if (selected.Length != request.NodeIds.Distinct().Count()) return Results.BadRequest(new { error = "所选节点不存在。" });
    if (selected.Any(n => !Validation.IsHost(n.Address) || IsLoopbackHost(n.Address)))
        return Results.BadRequest(new { error = "互测节点需要配置可被其他机器访问的 IP 或主机名，不能使用回环地址。" });
    var existing = store.GetTargets().ToList();
    var pending = new List<TargetConfig>();
    foreach (var source in selected)
    foreach (var destination in selected.Where(n => n.Id != source.Id))
    {
        if (existing.Concat(pending).Any(t => t.Host.Equals(destination.Address, StringComparison.OrdinalIgnoreCase) && t.Protocol == request.Protocol &&
            (request.Protocol == "icmp" || t.Port == request.Port) && t.NodeIds.Contains(source.Id))) continue;
        var target = Normalize(new TargetConfig
        {
            Name = $"{source.Name[..Math.Min(45, source.Name.Length)]} → {destination.Name[..Math.Min(45, destination.Name.Length)]}", Host = destination.Address, NodeIds = [source.Id],
            Protocol = request.Protocol, Port = request.Port, PacketSize = request.PacketSize,
            IntervalSeconds = request.IntervalSeconds, TimeoutMs = request.TimeoutMs,
            RetransmitThresholdMs = request.RetransmitThresholdMs, Group = "节点互测"
        });
        var error = Validation.Target(target, allNodes);
        if (error is not null) return Results.BadRequest(new { error });
        pending.Add(target);
    }
    if (pending.Count + existing.Count > 200) return Results.BadRequest(new { error = "创建后目标数将超过 200，请减少互测节点。" });
    foreach (var target in pending)
        if (store.TrySaveTarget(target) is { } conflict) return Results.Conflict(new { error = conflict });
    return Results.Ok(new { created = pending.Count });
});

app.MapPost("/api/agent/register", (AgentRegistration registration, TraceCoordinator traces) =>
{
    if (string.IsNullOrWhiteSpace(registration.Id) || registration.Id == "local" || registration.Id.Length > 100 ||
        registration.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
        return Results.BadRequest(new { error = "Agent ID 无效。" });
    if (string.IsNullOrWhiteSpace(registration.Name) || registration.Name.Length > 100 ||
        (!string.IsNullOrEmpty(registration.Address) && !Validation.IsHost(registration.Address)) || registration.TraceVersion is < 0 or > 100)
        return Results.BadRequest(new { error = "Agent 名称或地址无效。" });
    if (store.GetNodes().Count >= 100 && !store.GetNodes().Any(n => n.Id == registration.Id))
        return Results.BadRequest(new { error = "节点数量已达到上限 100。" });
    traces.SetAgentVersion(registration.Id, registration.TraceVersion);
    store.UpsertNode(new(registration.Id, registration.Name.Trim(), registration.Address ?? "", DateTimeOffset.UtcNow));
    return Results.Ok(new { nodeId = registration.Id });
});
app.MapGet("/api/agent/config", (string nodeId) =>
{
    if (!store.GetNodes().Any(n => n.Id == nodeId && !n.IsLocal)) return Results.NotFound(new { error = "请先注册 Agent。" });
    return Results.Ok(new { targets = store.GetTargets().Where(t => t.Enabled && t.NodeIds.Contains(nodeId)) });
});
app.MapPost("/api/agent/report", (AgentReport report) =>
{
    var node = store.GetNodes().FirstOrDefault(n => n.Id == report.NodeId && !n.IsLocal);
    if (node is null) return Results.NotFound(new { error = "请先注册 Agent。" });
    if (report.Samples is null || report.Samples.Length > 2000) return Results.BadRequest(new { error = "每批最多上报 2000 条结果。" });
    var now = DateTimeOffset.UtcNow;
    var targets = store.GetTargets().ToDictionary(t => t.Id);
    var accepted = report.Samples.Where(s => s is not null && !string.IsNullOrEmpty(s.Id) && s.Id.Length <= 100 &&
        s.TargetId is not null && s.NodeId == node.Id && targets.TryGetValue(s.TargetId, out var target) && target.NodeIds.Contains(node.Id) &&
        s.Timestamp >= now.AddDays(-options.RetentionDays) && s.Timestamp <= now.AddMinutes(5) &&
        (!s.Success || s.LatencyMs is >= 0 and <= 60000) && (s.LatencyMs is null || double.IsFinite(s.LatencyMs.Value)))
        .Select(s => s with
        {
            Timestamp = s.Timestamp > now ? now : s.Timestamp,
            LatencyMs = s.Success ? s.LatencyMs : null,
            SuspectedRetransmit = s.Success && targets[s.TargetId].Protocol == "tcp" && s.LatencyMs > targets[s.TargetId].RetransmitThresholdMs,
            Error = s.Error is { Length: > 256 } ? s.Error[..256] : s.Error
        }).ToArray();
    store.SaveSamples(accepted);
    store.TouchNode(node.Id, now);
    return Results.Ok(new { accepted = accepted.Length });
});

app.MapGet("/api/agent/traces", (string nodeId, int? traceVersion, TraceCoordinator traces) =>
{
    if (!store.GetNodes().Any(n => n.Id == nodeId && !n.IsLocal)) return Results.NotFound(new { error = "请先注册 Agent。" });
    if (traceVersion is < 0 or > 100) return Results.BadRequest(new { error = "路由追踪协议版本无效。" });
    return Results.Ok(new { traces = traces.Assignments(nodeId, traceVersion ?? 0) });
});
app.MapPost("/api/agent/traces/report", (TraceAgentUpdate update, TraceCoordinator traces) =>
{
    if (!store.GetNodes().Any(n => n.Id == update.NodeId && !n.IsLocal)) return Results.NotFound(new { error = "请先注册 Agent。" });
    return traces.Report(update, out var error) ? Results.Ok(new { accepted = true }) : Results.BadRequest(new { error });
});

app.Run();

static bool IsOnline(NodeInfo node) => DateTimeOffset.UtcNow - node.LastSeen < TimeSpan.FromSeconds(35);
object NodeView(NodeInfo node) => new { node.Id, node.Name, node.Address, node.LastSeen, node.IsLocal,
    online = IsOnline(node), traceVersion = traceCoordinator.AgentVersion(node.Id) };
static TargetConfig Normalize(TargetConfig target) => target with
{
    Name = target.Name.Trim(), Host = target.Host.Trim(), Group = string.IsNullOrWhiteSpace(target.Group) ? "默认分组" : target.Group.Trim(),
    NodeIds = target.NodeIds.Distinct().ToArray(), TimeoutMs = target.Protocol == "icmp" ? 2000 : target.TimeoutMs
};
static bool IsLoopbackUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsLoopbackHost(uri.Host);
static bool IsLoopbackHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
    IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
static bool MatchesToken(string header, string expected) => header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
    CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(header[7..])), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

public partial class Program { }
