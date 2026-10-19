using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SmartPing.Models;

namespace SmartPing.Services;

public sealed record ProbeStats(long Sent, long Received, double LossPercent, double? AvgLatencyMs,
    double? MinLatencyMs, double? MaxLatencyMs, double? JitterMs, double RetransmitPercent,
    double? LastLatencyMs, DateTimeOffset? LastSeen, string Status, int MissingSources, long AvgLatencySampleCount);
public sealed record SeriesPoint(DateTimeOffset Timestamp, double? LatencyMs, double? MinLatencyMs,
    double? MaxLatencyMs, double LossPercent, double RetransmitPercent, long Sent);
public sealed record ProbeHistory(ProbeStats Stats, IReadOnlyList<SeriesPoint> Series, IReadOnlyList<ProbeSample> Recent,
    int BucketSeconds);

public sealed class Store
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string connectionString;
    private readonly SmartPingOptions options;
    private readonly object writeLock = new();
    public string DataDirectory { get; }

    public Store(IOptions<SmartPingOptions> configuration)
    {
        options = configuration.Value;
        DataDirectory = Path.GetFullPath(options.DataDirectory);
        Directory.CreateDirectory(DataDirectory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DataDirectory, "smartping.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 15
        }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS targets(id TEXT PRIMARY KEY, config TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS nodes(id TEXT PRIMARY KEY, name TEXT NOT NULL, address TEXT NOT NULL,
                last_seen INTEGER NOT NULL, is_local INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS samples(id TEXT PRIMARY KEY, target_id TEXT NOT NULL, node_id TEXT NOT NULL,
                timestamp INTEGER NOT NULL, latency REAL, success INTEGER NOT NULL, retransmit INTEGER NOT NULL, error TEXT);
            CREATE INDEX IF NOT EXISTS ix_samples_target_time ON samples(target_id,timestamp,node_id);
            CREATE INDEX IF NOT EXISTS ix_samples_time ON samples(timestamp);
            CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    public IReadOnlyList<TargetConfig> GetTargets()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT config FROM targets ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var result = new List<TargetConfig>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<TargetConfig>(reader.GetString(0), Json)!);
        return result;
    }

    public TargetConfig? GetTarget(string id) => GetTargets().FirstOrDefault(t => t.Id == id);

    public void SaveTarget(TargetConfig target)
    {
        lock (writeLock)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO targets(id,config) VALUES($id,$config) ON CONFLICT(id) DO UPDATE SET config=excluded.config";
            command.Parameters.AddWithValue("$id", target.Id);
            command.Parameters.AddWithValue("$config", JsonSerializer.Serialize(target, Json));
            command.ExecuteNonQuery();
        }
    }

    public string? TrySaveTarget(TargetConfig target, bool requireExisting = false)
    {
        lock (writeLock)
        {
            var targets = GetTargets();
            var exists = targets.Any(t => t.Id == target.Id);
            if (requireExisting && !exists) return "项目已被移除，请刷新后重试。";
            if (!exists && targets.Count >= 200) return "当前版本最多支持 200 个目标。";
            var error = Validation.Target(target, GetNodes());
            if (error is not null) return error;
            SaveTarget(target);
            return null;
        }
    }

    public bool DeleteTarget(string id)
    {
        lock (writeLock)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "DELETE FROM targets WHERE id=$id; DELETE FROM samples WHERE target_id=$id;";
            command.Parameters.AddWithValue("$id", id);
            var count = command.ExecuteNonQuery();
            tx.Commit();
            return count > 0;
        }
    }

    public IReadOnlyList<NodeInfo> GetNodes()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,name,address,last_seen,is_local FROM nodes ORDER BY is_local DESC,name";
        using var reader = command.ExecuteReader();
        var nodes = new List<NodeInfo>();
        while (reader.Read()) nodes.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), reader.GetBoolean(4)));
        return nodes;
    }

    public void UpsertNode(NodeInfo node)
    {
        lock (writeLock)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO nodes(id,name,address,last_seen,is_local) VALUES($id,$name,$address,$time,$local)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name,address=excluded.address,last_seen=excluded.last_seen;
                """;
            command.Parameters.AddWithValue("$id", node.Id);
            command.Parameters.AddWithValue("$name", node.Name);
            command.Parameters.AddWithValue("$address", node.Address);
            command.Parameters.AddWithValue("$time", node.LastSeen.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$local", node.IsLocal);
            command.ExecuteNonQuery();
        }
    }

    public bool RemoveNode(string id)
    {
        if (id == "local") return false;
        lock (writeLock)
        {
            foreach (var target in GetTargets().Where(t => t.NodeIds.Contains(id)))
            {
                var nodes = target.NodeIds.Where(n => n != id).ToArray();
                SaveTarget(target with { NodeIds = nodes, Enabled = nodes.Length > 0 && target.Enabled });
            }
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "DELETE FROM nodes WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public void TouchNode(string id, DateTimeOffset now)
    {
        lock (writeLock)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE nodes SET last_seen=MAX(last_seen,$time) WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$time", now.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }
    }

    public string GetOrCreateNodeId()
    {
        lock (writeLock)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO metadata(key,value) VALUES('node_id',$id); SELECT value FROM metadata WHERE key='node_id'";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            return (string)command.ExecuteScalar()!;
        }
    }

    public void SaveSamples(IEnumerable<ProbeSample> samples)
    {
        lock (writeLock)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT OR IGNORE INTO samples(id,target_id,node_id,timestamp,latency,success,retransmit,error)
                SELECT $id,$target,$node,$time,$latency,$success,$retransmit,$error
                WHERE EXISTS(SELECT 1 FROM targets WHERE id=$target);
                """;
            foreach (var name in new[] { "$id", "$target", "$node", "$time", "$latency", "$success", "$retransmit", "$error" })
                command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
            foreach (var sample in samples)
            {
                command.Parameters["$id"].Value = sample.Id;
                command.Parameters["$target"].Value = sample.TargetId;
                command.Parameters["$node"].Value = sample.NodeId;
                command.Parameters["$time"].Value = sample.Timestamp.ToUnixTimeMilliseconds();
                command.Parameters["$latency"].Value = (object?)sample.LatencyMs ?? DBNull.Value;
                command.Parameters["$success"].Value = sample.Success;
                command.Parameters["$retransmit"].Value = sample.SuspectedRetransmit;
                command.Parameters["$error"].Value = (object?)sample.Error ?? DBNull.Value;
                command.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public ProbeHistory GetHistory(TargetConfig target, double hours, string? nodeId, bool includeRecent = true,
        int? bucketSeconds = null, int maxPoints = HistoryQuery.MaximumChartPoints)
    {
        var effectiveBucketSeconds = HistoryQuery.ResolveBucketSeconds(hours, bucketSeconds, maxPoints);
        var now = DateTimeOffset.UtcNow;
        var since = now.AddHours(-hours).ToUnixTimeMilliseconds();
        using var db = Open();
        using var command = db.CreateCommand();
        const string filter = "target_id=$target AND timestamp >= $since AND timestamp <= $now AND ($node='' OR node_id=$node)";
        command.Parameters.AddWithValue("$target", target.Id);
        command.Parameters.AddWithValue("$since", since);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$node", nodeId ?? "");
        command.Parameters.AddWithValue("$isTcp", string.Equals(target.Protocol, "tcp", StringComparison.OrdinalIgnoreCase));
        const string averageLatency = "CASE WHEN success=1 AND ($isTcp=0 OR retransmit=0) THEN latency END";
        command.CommandText = $"""
            SELECT COUNT(*),COALESCE(SUM(success),0),AVG({averageLatency}),MIN(latency),MAX(latency),
                COALESCE(SUM(retransmit),0),COUNT({averageLatency})
            FROM samples WHERE {filter};
            """;
        long sent, received, retransmit, averageSampleCount;
        double? avg, min, max;
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            sent = reader.GetInt64(0); received = reader.GetInt64(1);
            avg = NullableDouble(reader, 2); min = NullableDouble(reader, 3); max = NullableDouble(reader, 4);
            retransmit = reader.GetInt64(5);
            averageSampleCount = reader.GetInt64(6);
        }
        command.CommandText = $"""
            SELECT AVG(ABS(latency-previous)) FROM
            (SELECT {averageLatency} AS latency,
                LAG({averageLatency}) OVER(PARTITION BY node_id ORDER BY timestamp,id) AS previous
            FROM samples WHERE {filter});
            """;
        var jitterValue = command.ExecuteScalar();
        double? jitter = jitterValue is null or DBNull ? null : Convert.ToDouble(jitterValue);
        command.CommandText = $"SELECT timestamp,latency,success FROM samples WHERE {filter} ORDER BY timestamp DESC,id DESC LIMIT 1";
        DateTimeOffset? lastSeen = null;
        double? lastLatency = null;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                lastSeen = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0));
                lastLatency = NullableDouble(reader, 1);
            }
        }
        var loss = sent > 0 ? 100.0 * (sent - received) / sent : 0;
        var freshFor = TimeSpan.FromSeconds(Math.Max(35, target.IntervalSeconds * 3 + target.TimeoutMs / 1000.0));
        command.CommandText = $"""
            SELECT node_id,timestamp,success FROM
            (SELECT node_id,timestamp,success,ROW_NUMBER() OVER(PARTITION BY node_id ORDER BY timestamp DESC,id DESC) AS rank
            FROM samples WHERE {filter}) WHERE rank=1;
            """;
        var latestSources = new Dictionary<string, (DateTimeOffset time, bool success)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) latestSources[reader.GetString(0)] =
                (DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)), reader.GetBoolean(2));
        var nodes = GetNodes().ToDictionary(n => n.Id);
        var expectedSources = target.NodeIds.Where(n => string.IsNullOrEmpty(nodeId) || n == nodeId).ToArray();
        var missingSources = expectedSources.Count(id => !latestSources.TryGetValue(id, out var sample) || now - sample.time > freshFor ||
            !nodes.TryGetValue(id, out var node) || now - node.LastSeen > TimeSpan.FromSeconds(35));
        var healthySources = expectedSources.Count(id => latestSources.TryGetValue(id, out var sample) && sample.success && now - sample.time <= freshFor &&
            nodes.TryGetValue(id, out var node) && now - node.LastSeen <= TimeSpan.FromSeconds(35));
        var status = !target.Enabled ? "paused" : lastSeen == null ? "waiting" :
            healthySources == 0 ? "offline" : missingSources > 0 || healthySources < expectedSources.Length || loss > 0 || retransmit > 0 ? "degraded" : "healthy";
        var stats = new ProbeStats(sent, received, loss, avg, min, max, jitter,
            sent > 0 ? 100.0 * retransmit / sent : 0, lastLatency, lastSeen, status, missingSources, averageSampleCount);

        // Keep the chosen short-window resolution; disclose any coarsening needed for bounded responses.
        var bucketMs = (long)effectiveBucketSeconds * 1000;
        command.Parameters.AddWithValue("$bucket", bucketMs);
        command.CommandText = $"""
            SELECT (timestamp/$bucket)*$bucket,AVG({averageLatency}),MIN(latency),MAX(latency),COUNT(*),SUM(success),SUM(retransmit)
            FROM samples WHERE {filter} GROUP BY timestamp/$bucket ORDER BY timestamp/$bucket;
            """;
        var series = new List<SeriesPoint>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var count = reader.GetInt64(4);
                series.Add(new(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)), NullableDouble(reader, 1),
                    NullableDouble(reader, 2), NullableDouble(reader, 3), 100.0 * (count - reader.GetInt64(5)) / count,
                    100.0 * reader.GetInt64(6) / count, count));
            }
        }
        var recent = new List<ProbeSample>();
        if (includeRecent)
        {
            command.CommandText = $"SELECT id,target_id,node_id,timestamp,latency,success,retransmit,error FROM samples WHERE {filter} ORDER BY timestamp DESC LIMIT 100";
            using var reader = command.ExecuteReader();
            while (reader.Read()) recent.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), NullableDouble(reader, 4), reader.GetBoolean(5),
                reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return new(stats, series, recent, effectiveBucketSeconds);
    }

    private static double? NullableDouble(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    public int Prune()
    {
        lock (writeLock)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "DELETE FROM samples WHERE timestamp < $cutoff";
            command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-options.RetentionDays).ToUnixTimeMilliseconds());
            return command.ExecuteNonQuery();
        }
    }
}

public sealed class RetentionWorker(Store store, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { store.Prune(); }
            catch (Exception ex) { logger.LogError(ex, "Failed to prune history"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
