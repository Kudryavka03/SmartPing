using System.Net;
using System.Net.Sockets;
using SmartPing.Models;

namespace SmartPing.Services;

public static class Validation
{
    // Validate the entire original value. In particular, never trim an unsafe value into a valid one.
    public static bool IsHost(string? host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253 || host.Any(c => c > 127)) return false;

        if (host.Contains(':'))
        {
            // Brackets, scope IDs, ports, and every shell metacharacter are deliberately excluded.
            return host.All(c => char.IsAsciiHexDigit(c) || c is ':' or '.')
                && (!host.Contains('.') || IsCanonicalIpv4(host[(host.LastIndexOf(':') + 1)..]))
                && IPAddress.TryParse(host, out var ipv6) && ipv6.AddressFamily == AddressFamily.InterNetworkV6;
        }

        if (host.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            return IsCanonicalIpv4(host);
        }

        // IPAddress also accepts abbreviated, hexadecimal and octal IPv4: do not pass those on as DNS.
        if (IPAddress.TryParse(host, out var ipv4) && ipv4.AddressFamily == AddressFamily.InterNetwork) return false;

        var name = host.EndsWith('.') ? host[..^1] : host;
        return name.Split('.').All(label => label.Length is >= 1 and <= 63
            && char.IsAsciiLetterOrDigit(label[0]) && char.IsAsciiLetterOrDigit(label[^1])
            && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }

    private static bool IsCanonicalIpv4(string address)
    {
        var octets = address.Split('.');
        return octets.Length == 4 && octets.All(o => o.Length is >= 1 and <= 3
            && (o.Length == 1 || o[0] != '0') && o.All(char.IsAsciiDigit) && byte.TryParse(o, out _));
    }

    public static string? Trace(TraceExecutionRequest request)
    {
        if (!IsHost(request.Host))
            return "目标仅允许有效的 ASCII 主机名或 IP，不能包含空格、协议、端口或特殊字符。";
        if (request.MaxHops is < 1 or > 64 || request.TimeoutMs is < 100 or > 5000)
            return "最大跳数必须为 1–64，每跳超时必须为 100–5000 毫秒。";
        if (request.Protocol is not ("icmp" or "tcp")) return "路由跟踪协议必须为 icmp 或 tcp。";
        if (request.Protocol == "tcp" && request.Port is not (>= 1 and <= 65535))
            return "TCP 路由跟踪必须填写 1–65535 之间的端口。";
        if (request.Protocol == "icmp" && request.Port is not null)
            return "ICMP 路由跟踪不能设置 TCP 端口。";
        if (request.Psize is { } packetSize)
        {
            var ipv6 = IPAddress.TryParse(request.Host, out var address)
                && address.AddressFamily == AddressFamily.InterNetworkV6;
            var minimum = request.Protocol == "tcp" ? (ipv6 ? 60 : 40) : (ipv6 ? 48 : 28);
            if (packetSize < minimum || packetSize > 65535)
                return $"psize 必须为 {minimum}–65535 之间的整数（整个 IP 数据包字节数）；域名实际使用的 IP 协议族由 ntrace 进一步校验。";
        }
        return null;
    }

    public static string? Target(TargetConfig target, IEnumerable<NodeInfo> nodes)
    {
        if (string.IsNullOrWhiteSpace(target.Name) || target.Name.Length > 100) return "名称不能为空，且不能超过 100 个字符。";
        if (!IsHost(target.Host)) return "请输入有效的 IP 或主机名，不要包含协议、端口或路径。";
        if (target.Protocol is not ("icmp" or "tcp")) return "协议必须为 icmp 或 tcp。";
        if (target.PacketSize is < 0 or > 65500) return "ICMP 数据载荷大小必须在 0–65500 字节之间。";
        if (target.IntervalSeconds is < 1 or > 3600) return "探测间隔必须在 1–3600 秒之间。";
        if (target.Protocol == "tcp")
        {
            if (target.Port is < 1 or > 65535) return "TCP 端口必须在 1–65535 之间。";
            if (target.TimeoutMs is < 100 or > 60000) return "TCP 超时必须在 100–60000 毫秒之间。";
            if (target.RetransmitThresholdMs < 1 || target.RetransmitThresholdMs >= target.TimeoutMs)
                return "疑似重传阈值必须大于 0，且小于超时阈值。";
        }
        if (target.Group is null || target.Group.Length > 60) return "分组不能超过 60 个字符。";
        if (target.NodeIds is null || target.NodeIds.Length == 0) return "至少选择一个探测节点。";
        if (target.NodeIds.Any(n => !nodes.Any(known => known.Id == n))) return "所选节点不存在，请刷新节点列表。";
        return null;
    }
}
