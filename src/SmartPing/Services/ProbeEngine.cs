using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SmartPing.Models;

namespace SmartPing.Services;

public sealed class ProbeEngine
{
    public const int IcmpTimeoutMs = 2000;

    public async Task<ProbeSample> ProbeAsync(TargetConfig target, string nodeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timestamp = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        try
        {
            if (target.Protocol.Equals("icmp", StringComparison.OrdinalIgnoreCase))
            {
                if (target.PacketSize is < 0 or > 65500)
                    throw new ArgumentOutOfRangeException(nameof(target.PacketSize));
                // PacketSize is the ICMP payload length, excluding IP and ICMP headers.
                using var ping = new Ping();
                using var icmpTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                icmpTimeout.CancelAfter(IcmpTimeoutMs);
                var payload = new byte[target.PacketSize];
                PingReply reply;
                try
                {
                    reply = await ping.SendPingAsync(target.Host, TimeSpan.FromMilliseconds(IcmpTimeoutMs),
                        payload, new PingOptions(), icmpTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new(id, target.Id, nodeId, timestamp, null, false, false, "Timeout");
                }
                return reply.Status == IPStatus.Success
                    ? new(id, target.Id, nodeId, timestamp, reply.RoundtripTime, true, false, null)
                    : new(id, target.Id, nodeId, timestamp, null, false, false, IcmpError(reply.Status));
            }

            if (!target.Protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase))
                return new(id, target.Id, nodeId, timestamp, null, false, false, "Unsupported protocol");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(target.TimeoutMs);
            using var client = new TcpClient();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                // The timeout and latency include name resolution and the complete TCP handshake.
                await client.ConnectAsync(target.Host, target.Port, timeout.Token);
                var latency = stopwatch.Elapsed.TotalMilliseconds;
                return new(id, target.Id, nodeId, timestamp, latency, true,
                    IsSuspectedRetransmit(latency, target.RetransmitThresholdMs), null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(id, target.Id, nodeId, timestamp, null, false, false, "Timeout");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is PingException or SocketException or ArgumentException
                                        or InvalidOperationException or NotSupportedException)
        {
            return new(id, target.Id, nodeId, timestamp, null, false, false, NormalizeError(exception));
        }
    }

    internal static bool IsSuspectedRetransmit(double latencyMs, int thresholdMs) => latencyMs > thresholdMs;

    private static string IcmpError(IPStatus status) => status switch
    {
        IPStatus.TimedOut => "Timeout",
        IPStatus.DestinationHostUnreachable => "Destination host unreachable",
        IPStatus.DestinationNetworkUnreachable => "Destination network unreachable",
        IPStatus.DestinationPortUnreachable => "Destination port unreachable",
        IPStatus.BadDestination => "Invalid destination",
        IPStatus.TtlExpired => "TTL expired",
        IPStatus.PacketTooBig => "Packet too big",
        _ => status.ToString()
    };

    private static string NormalizeError(Exception exception)
    {
        if (exception is PingException { InnerException: not null } pingException)
            return NormalizeError(pingException.InnerException!);
        return exception switch
        {
            SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "Connection refused",
            SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData
                or SocketError.TryAgain } => "DNS resolution failed",
            SocketException { SocketErrorCode: SocketError.TimedOut } => "Timeout",
            SocketException { SocketErrorCode: SocketError.HostUnreachable } => "Host unreachable",
            SocketException { SocketErrorCode: SocketError.NetworkUnreachable } => "Network unreachable",
            SocketException socketException => socketException.SocketErrorCode.ToString(),
            ArgumentException => "Invalid probe configuration",
            NotSupportedException => "Probe not supported on this platform",
            _ => exception.Message
        };
    }
}
