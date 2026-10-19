using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SmartPing.Models;

namespace SmartPing.Services;

public sealed class AgentWorker(Store store, ProbeEngine engine, IOptions<SmartPingOptions> options,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private TargetConfig[] _targets = [];
    private long _lastConfigTimestamp;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configuration = options.Value;
        if (!configuration.Mode.Equals("agent", StringComparison.OrdinalIgnoreCase)) return;
        if (!Uri.TryCreate(configuration.ServerUrl.TrimEnd('/') + "/", UriKind.Absolute, out var serverUri)
            || serverUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("SmartPing:ServerUrl must be an absolute HTTP or HTTPS URL.");

        var nodeId = string.IsNullOrWhiteSpace(configuration.NodeId) ? store.GetOrCreateNodeId() : configuration.NodeId;
        var address = string.IsNullOrWhiteSpace(configuration.NodeAddress) ? Dns.GetHostName() : configuration.NodeAddress;
        var registration = new AgentRegistration(nodeId, configuration.NodeName, address, TraceVersion: 2);
        using var client = new HttpClient { BaseAddress = serverUri, Timeout = TimeSpan.FromSeconds(8) };
        if (!string.IsNullOrWhiteSpace(configuration.AgentToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", configuration.AgentToken);
        // Isolate configured identities: changing NodeId must not report a previous node's queued samples.
        var identityKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(nodeId)))[..16].ToLowerInvariant();
        using var outbox = new DurableAgentOutbox(Path.Combine(configuration.DataDirectory, "agent-outbox", identityKey));
        var loop = new TargetProbeLoop(engine, logger, nodeId, () => CurrentTargets(nodeId),
            (sample, _) =>
            {
                var dropped = outbox.Enqueue(sample);
                if (dropped > 0) logger.LogWarning("Agent outbox reached {Limit} samples; dropped {Count} oldest sample(s)", DurableAgentOutbox.Capacity, dropped);
                return Task.CompletedTask;
            });
        logger.LogInformation("Agent started as {NodeId}, center {ServerUrl}; {Count} queued samples restored", nodeId, serverUri, outbox.Count);
        await Task.WhenAll(loop.RunAsync(stoppingToken), SynchronizeAsync(client, registration, outbox, stoppingToken));
    }

    private IReadOnlyList<TargetConfig> CurrentTargets(string nodeId)
    {
        var lastConfig = Interlocked.Read(ref _lastConfigTimestamp);
        if (lastConfig == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(lastConfig) > TimeSpan.FromSeconds(60)) return [];
        return Volatile.Read(ref _targets).Where(target => target.Enabled && target.NodeIds.Contains(nodeId)).ToArray();
    }

    private async Task SynchronizeAsync(HttpClient client, AgentRegistration registration,
        DurableAgentOutbox outbox, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            do
            {
                try
                {
                    using var registered = await client.PostAsJsonAsync("api/agent/register", registration, JsonOptions, stoppingToken);
                    registered.EnsureSuccessStatusCode();
                    var identity = await registered.Content.ReadFromJsonAsync<RegistrationResponse>(JsonOptions, stoppingToken);
                    if (identity?.NodeId != registration.Id)
                        throw new InvalidDataException("Center returned an unexpected node identity.");

                    var config = await client.GetFromJsonAsync<ConfigurationResponse>(
                        $"api/agent/config?nodeId={Uri.EscapeDataString(registration.Id)}", JsonOptions, stoppingToken);
                    if (config?.Targets is null) throw new InvalidDataException("Center returned an invalid target configuration.");
                    Volatile.Write(ref _targets, config.Targets);
                    Interlocked.Exchange(ref _lastConfigTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());

                    // Drain in bounded batches. IDs remain unchanged across retries for server-side deduplication.
                    for (var batchNumber = 0; batchNumber < 10; batchNumber++)
                    {
                        var samples = outbox.Peek(500);
                        if (samples.Length == 0) break;
                        using var response = await client.PostAsJsonAsync("api/agent/report",
                            new AgentReport(registration.Id, samples), JsonOptions, stoppingToken);
                        response.EnsureSuccessStatusCode();
                        outbox.Acknowledge(samples);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
                                                or JsonException or InvalidDataException or SqliteException)
                {
                    logger.LogWarning("Agent synchronization failed: {Reason}. Queued samples are retained; stale probing stops after 60 seconds", exception.Message);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private sealed record RegistrationResponse(string NodeId);
    private sealed record ConfigurationResponse(TargetConfig[] Targets);
}

internal sealed class DurableAgentOutbox : IDisposable
{
    public const int Capacity = 10_000;
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly int _capacity;
    private int _count;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public DurableAgentOutbox(string dataDirectory, int capacity = Capacity)
    {
        if (capacity < 1 || capacity > Capacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        var directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "agent-outbox.db"),
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS outbox (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                sample_id TEXT NOT NULL UNIQUE,
                payload TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT COUNT(*) FROM outbox";
        _count = Convert.ToInt32(command.ExecuteScalar());
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public int Enqueue(ProbeSample sample)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO outbox(sample_id, payload) VALUES($id, $payload)";
            command.Parameters.AddWithValue("$id", sample.Id);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(sample, JsonOptions));
            var inserted = command.ExecuteNonQuery();
            var overflow = Math.Max(0, _count + inserted - _capacity);
            var dropped = 0;
            if (overflow > 0)
            {
                command.Parameters.Clear();
                command.CommandText = "DELETE FROM outbox WHERE sequence IN (SELECT sequence FROM outbox ORDER BY sequence LIMIT $overflow)";
                command.Parameters.AddWithValue("$overflow", overflow);
                dropped = command.ExecuteNonQuery();
            }
            transaction.Commit();
            _count += inserted - dropped;
            return dropped;
        }
    }

    public ProbeSample[] Peek(int count)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT payload FROM outbox ORDER BY sequence LIMIT $count";
            command.Parameters.AddWithValue("$count", count);
            using var reader = command.ExecuteReader();
            var samples = new List<ProbeSample>();
            while (reader.Read())
                samples.Add(JsonSerializer.Deserialize<ProbeSample>(reader.GetString(0), JsonOptions)
                    ?? throw new InvalidDataException("Invalid sample in durable agent outbox."));
            return samples.ToArray();
        }
    }

    public void Acknowledge(IEnumerable<ProbeSample> samples)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM outbox WHERE sample_id = $id";
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var deleted = 0;
            foreach (var sample in samples)
            {
                id.Value = sample.Id;
                deleted += command.ExecuteNonQuery();
            }
            transaction.Commit();
            _count -= deleted;
        }
    }

    public void Dispose() => _connection.Dispose();
}
