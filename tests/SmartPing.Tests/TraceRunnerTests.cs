using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using SmartPing.Models;
using SmartPing.Services;

public static class TraceRunnerTests
{
    public static async Task RunAsync()
    {
        TestHostValidation();
        TestOptionValidation();
        TestDiscovery();
        TestProcessArguments();
        await TestRejectionBeforeProcessLookup();
        await TestUnsupportedFallbackOptions();
        await TestOutputSafety();
        await TestOwnedNativeFixture();
        await TestNativeLocalhost();
        Console.WriteLine("PASS Traceroute host/options validation, ICMP/TCP arguments, capability fallback, process isolation, discovery, output flood, native cancellation/timeout, and localhost");
    }

    private static void TestOptionValidation()
    {
        foreach (var request in new[]
        {
            new TraceExecutionRequest("127.0.0.1", Protocol: "tcp", Port: 1, Psize: 40),
            new("example.com", Protocol: "tcp", Port: 65535, Psize: 65535),
            new("127.0.0.1", Psize: 28), new("example.com", Psize: 28), new("::1", Psize: 48),
            new("2001:db8::1", Protocol: "tcp", Port: 443, Psize: 60),
            new("::ffff:192.0.2.1", Protocol: "tcp", Port: 443, Psize: 60)
        }) Check(Validation.Trace(request) is null, "Valid protocol, port and packet size boundaries must pass shared validation");

        foreach (var request in new[]
        {
            new TraceExecutionRequest("127.0.0.1", Protocol: "tcp"),
            new("127.0.0.1", Protocol: "tcp", Port: 0), new("127.0.0.1", Protocol: "tcp", Port: 65536),
            new("127.0.0.1", Port: 443), new("127.0.0.1", Protocol: "udp"),
            new("127.0.0.1", Protocol: "TCP", Port: 443), new("127.0.0.1", Protocol: "tcp ", Port: 443),
            new("127.0.0.1", Protocol: "tcp&whoami", Port: 443), new("127.0.0.1", Protocol: ""),
            new("127.0.0.1", Protocol: null!), new("127.0.0.1", Psize: 0), new("127.0.0.1", Psize: -1),
            new("127.0.0.1", Psize: 27), new("127.0.0.1", Psize: 65536),
            new("127.0.0.1", Protocol: "tcp", Port: 443, Psize: 39), new("::1", Psize: 47),
            new("::1", Protocol: "tcp", Port: 443, Psize: 59)
        }) Check(Validation.Trace(request) is not null, "Missing/invalid port, unsupported protocols and undersized/oversized packets must fail shared validation");
    }

    private static void TestHostValidation()
    {
        string[] valid = ["localhost", "example.com", "EXAMPLE.COM.", "a-b.example", "a", "0.0.0.0", "127.0.0.1",
            "255.255.255.255", "::", "::1", "2001:db8::1", "::ffff:192.0.2.1", new string('a', 63) + ".example"];
        foreach (var host in valid) Check(Validation.IsHost(host), $"Safe host should be accepted: {host}");

        string?[] invalid = [null, "", " ", " localhost", "localhost ", "local host", "local\thost", "localhost\r\nwhoami",
            "localhost&whoami", "localhost&&whoami", "localhost|whoami", "localhost||whoami", "localhost;whoami",
            "localhost>file", "localhost<file", "localhost^&whoami", "localhost`whoami`", "$(whoami)", "${PATH}",
            "%COMSPEC%", "!COMSPEC!", "localhost\"", "localhost'", "localhost\\whoami", "localhost/whoami",
            "-h", "--help", "/?", "https://example.com", "localhost:80", "[::1]", "[::1]:80", "fe80::1%12",
            "user@host", "host#fragment", "host?query", "host=value", "host,other", "host(other)", "host{other}",
            "host[other]", "*.example.com", "bad_name", "-bad.example", "bad-.example", ".example", "example..com",
            "example.com..", "例子.com", "ｌｏｃａｌｈｏｓｔ", "local\u200bhost", "local\u00a0host", "local\u2028host",
            "localhost\0", "127.1", "127", "2130706433", "0x7f000001", "0x7f.0.0.1", "0177.0.0.1",
            "127.00.0.1", "127.0.0.256", "127.0.0.1.", "1.2.3.4.5", "9999999999999999", ":1", "2001:db8:::1",
            "::ffff:192.168.001.1", "::ffff:0177.0.0.1", "::ffff:0x7f.0.0.1", "::ffff:127.1", "::ffff:2130706433",
            new string('a', 64) + ".example", string.Join('.', Enumerable.Repeat(new string('a', 63), 4))];
        foreach (var host in invalid) Check(!Validation.IsHost(host), $"Unsafe or ambiguous host was accepted: {host}");

        // Every forbidden ASCII character is tested at every input boundary, including NUL and option prefixes.
        foreach (var c in Enumerable.Range(0, 128).Select(i => (char)i)
            .Where(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or ':')))
        {
            foreach (var value in new[] { c + "localhost", "local" + c + "host", "localhost" + c })
                Check(!Validation.IsHost(value), $"ASCII code {(int)c} must be rejected wherever it appears");
        }
    }

    private static void TestDiscovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "SmartPing-trace-test-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "app");
        var first = Path.Combine(root, "first path with spaces");
        var second = Path.Combine(root, "second");
        var system = Path.Combine(root, "system");
        var appNtrace = Path.Combine(app, "ntrace.exe");
        var bundledNtrace = Path.Combine(app, "tools", "nexttrace", "win-x64", "ntrace.exe");
        var firstNtrace = Path.Combine(first, "ntrace.exe");
        var secondNtrace = Path.Combine(second, "ntrace.exe");
        var tracert = Path.Combine(system, "tracert.exe");
        var present = new HashSet<string>([bundledNtrace, appNtrace, firstNtrace, secondNtrace, tracert], StringComparer.OrdinalIgnoreCase);
        var visited = new List<string>();
        bool Exists(string path) { visited.Add(path); return present.Contains(path); }
        var pathValue = string.Join(Path.PathSeparator, "", ".", "relative", first, second);

        var found = TraceRunner.ResolveExecutable(app, pathValue, system, Exists, Architecture.X64);
        Check(found is { Name: "ntrace" } && found.Path == bundledNtrace, "Published backend bundle must take precedence over application root and PATH");
        found = TraceRunner.ResolveExecutable(app, "", system, Exists, Architecture.X64);
        Check(found is { Name: "ntrace" } && found.Path == bundledNtrace, "Published bundle must work with an empty PATH");
        found = TraceRunner.ResolveExecutable(app, pathValue, system, Exists, Architecture.Arm64);
        Check(found is { Name: "ntrace" } && found.Path == appNtrace, "ARM64 must not silently select an incompatible bundled x64 driver tool");
        present.Remove(bundledNtrace);
        found = TraceRunner.ResolveExecutable(app, pathValue, system, Exists);
        Check(found is { Name: "ntrace" } && found.Path == appNtrace, "Application ntrace should take precedence over PATH");
        present.Remove(appNtrace);
        found = TraceRunner.ResolveExecutable(app, pathValue, system, Exists);
        Check(found is { Name: "ntrace" } && found.Path == firstNtrace, "First absolute PATH ntrace should win, including paths with spaces");
        present.Remove(firstNtrace);
        present.Remove(secondNtrace);
        found = TraceRunner.ResolveExecutable(app, pathValue, system, Exists);
        Check(found is { Name: "tracert" } && found.Path == tracert, "Only absent ntrace should select trusted SystemDirectory tracert");
        Check(visited.All(Path.IsPathFullyQualified) && visited.All(p => Path.GetExtension(p) == ".exe"),
            "Discovery must never examine relative paths or shell wrappers");
        present.Remove(tracert);
        Check(TraceRunner.ResolveExecutable(app, pathValue, system, Exists) is null, "No executable should report unavailable");
        present.Add(Path.Combine(first, "tracert.exe"));
        Check(TraceRunner.ResolveExecutable(app, pathValue, system, Exists) is null, "PATH tracert must never be used as fallback");
        Check(TraceRunner.ResolveExecutable("relative", "", "relative", _ => true) is null,
            "Neither app nor fallback resolution may produce relative executable paths");
    }

    private static void TestProcessArguments()
    {
        var directory = Path.Combine(Path.GetTempPath(), "native tools with spaces");
        foreach (var tool in new[] { "ntrace", "tracert" })
        {
            var executable = new TraceRunner.TraceExecutable(tool, Path.Combine(directory, tool + ".exe"));
            var info = TraceRunner.BuildStartInfo(executable, new("2001:db8::1", 64, 5000));
            Check(info.FileName == executable.Path && Path.IsPathFullyQualified(info.FileName), "Executable must be a fixed absolute path");
            Check(!info.UseShellExecute && info.CreateNoWindow && info.RedirectStandardOutput
                && info.RedirectStandardError && info.RedirectStandardInput, "Native process must not use a shell or interactive input");
            Check(info.Arguments == "" && info.ArgumentList[^1] == "2001:db8::1", "Host must be one final ArgumentList token");
            var expected = tool == "ntrace"
                ? new[] { "--traceroute", "--no-color", "--no-rdns", "--map", "-q", "3", "--max-hops", "64", "--timeout", "5000",
                    "--icmp-mode", "1", "2001:db8::1" }
                : new[] { "-d", "-h", "64", "-w", "5000", "2001:db8::1" };
            Check(info.ArgumentList.SequenceEqual(expected), $"{tool} should receive only the fixed option whitelist");
        }

        var ntrace = new TraceRunner.TraceExecutable("ntrace", Path.Combine(directory, "ntrace.exe"));
        var tcp = TraceRunner.BuildStartInfo(ntrace, new("203.0.113.10", 30, 1000, "tcp", 443, 64));
        Check(tcp.ArgumentList.SequenceEqual(new[] { "--traceroute", "--no-color", "--no-rdns", "--map", "-q", "3",
            "--max-hops", "30", "--timeout", "1000", "--tcp", "--port", "443", "--psize", "64", "203.0.113.10" }),
            "TCP trace must receive port and psize as separate fixed argument tokens with default GeoIP and without ICMP mode");
        var icmp = TraceRunner.BuildStartInfo(ntrace, new("203.0.113.10", Psize: 64));
        Check(icmp.ArgumentList.TakeLast(3).SequenceEqual(new[] { "--psize", "64", "203.0.113.10" }),
            "Optional ICMP psize must precede the single final host token");
    }

    private static async Task TestRejectionBeforeProcessLookup()
    {
        var lookups = 0;
        var runner = new TraceRunner(() => { lookups++; return null; });
        foreach (var request in new[] { new TraceExecutionRequest("127.0.0.1&whoami"), new(" localhost"), new("--help"),
            new("127.0.0.1", 0), new("127.0.0.1", 65), new("127.0.0.1", 1, 99), new("127.0.0.1", 1, 5001),
            new("127.0.0.1", int.MaxValue, int.MinValue), new("127.0.0.1", Protocol: "tcp"),
            new("127.0.0.1", Protocol: "TCP", Port: 443), new("127.0.0.1", Protocol: "tcp&whoami", Port: 443),
            new("127.0.0.1", Protocol: "tcp", Port: -1), new("127.0.0.1", Protocol: "tcp", Port: 65536),
            new("127.0.0.1", Port: 443), new("127.0.0.1", Psize: 0), new("127.0.0.1", Psize: -1),
            new("127.0.0.1", Psize: 65536), new("::1", Protocol: "tcp", Port: 443, Psize: 59) })
        {
            var result = await runner.RunAsync(request, _ => throw new InvalidOperationException("Must not emit output"), CancellationToken.None);
            Check(result.Status == "failed" && result.Tool is null && result.Output == "", "Invalid requests must fail without process side effects");
        }
        Check(lookups == 0, "Invalid hosts, protocols, ports and packet sizes must be rejected before executable lookup");
        foreach (var request in new[] { new TraceExecutionRequest("127.0.0.1", 1, 100), new("::1", 64, 5000) })
            Check((await runner.RunAsync(request, null, CancellationToken.None)).Status == "failed", "Valid boundary options should reach unavailable tool handling");
        Check(lookups == 2, "Allowed boundary options should pass validation");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Check((await runner.RunAsync(new("localhost"), null, cancelled.Token)).Status == "cancelled" && lookups == 2,
            "Already-cancelled executions should never look up or start an executable");

        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ntrace.exe");
        var missingNtrace = new TraceRunner(() => new("ntrace", missing));
        var failed = await missingNtrace.RunAsync(new("127.0.0.1", 1, 100), null, CancellationToken.None);
        Check(failed.Status == "failed" && failed.Tool == "ntrace" && !failed.Error!.Contains(missing),
            "A selected ntrace that fails to start must fail without fallback or exposing local executable paths");
    }

    private static async Task TestUnsupportedFallbackOptions()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "tracert.exe");
        var runner = new TraceRunner(() => new("tracert", missing));
        foreach (var request in new[] { new TraceExecutionRequest("127.0.0.1", Protocol: "tcp", Port: 443),
            new("127.0.0.1", Psize: 64), new("127.0.0.1", Protocol: "tcp", Port: 443, Psize: 64) })
        {
            var result = await runner.RunAsync(request, _ => throw new InvalidOperationException("No process output expected"), CancellationToken.None);
            Check(result.Status == "failed" && result.Tool == "tracert" && result.ExitCode is null && result.Output == ""
                && result.Error!.Contains("需要 ntrace"), "Fallback must explicitly reject unsupported TCP/psize before attempting to start any process");
        }
        Check(runner.GetTool() is { Available: true, Tool: "tracert", SupportsTcp: false, SupportsPsize: false },
            "Windows tracert must advertise basic ICMP capabilities only");
        Check(new TraceRunner(() => new("ntrace", missing)).GetTool() is { Available: true, Tool: "ntrace", SupportsTcp: true, SupportsPsize: true },
            "ntrace must advertise both TCP and psize capabilities");
        Check(new TraceRunner(() => null).GetTool() is { Available: false, Tool: null, SupportsTcp: false, SupportsPsize: false },
            "Missing tools must advertise no trace capabilities");
    }

    private static async Task TestOutputSafety()
    {
        var received = new StringBuilder();
        var output = new TraceRunner.TraceOutput(chunk => received.Append(chunk));
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++) output.Append(new string('x', 1000));
        })));
        var result = output.Snapshot();
        Check(result.Truncated && result.Text.Length == TraceRunner.MaximumOutputChars
            && received.ToString() == result.Text, "Concurrent stdout/stderr must share one bounded output and streaming cap");
        var disconnected = new TraceRunner.TraceOutput(_ => throw new IOException("Disconnected"));
        disconnected.Append("still drained");
        Check(disconnected.Snapshot().Text == "still drained", "Observer errors must not stop pipe draining");
        var filter = new TraceRunner.TerminalOutputFilter();
        var clean = filter.Filter("a\u001b[3".AsSpan())
            + filter.Filter("1mred\u001b[0m\u001b]8;;https://bad/".AsSpan())
            + filter.Filter("\u001b\\click\u001b]8;;\u001b\\\0\b\r\n\tend".AsSpan());
        Check(clean == "aredclick\r\n\tend", "ANSI colors, terminal links and forbidden controls must be removed across stream boundaries");
    }

    private static async Task TestNativeLocalhost()
    {
        if (!OperatingSystem.IsWindows()) return;
        var tracert = Path.Combine(Environment.SystemDirectory, "tracert.exe");
        if (!File.Exists(tracert)) return;
        var runner = new TraceRunner(() => new("tracert", tracert));
        var streamed = new StringBuilder();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await runner.RunAsync(new("127.0.0.1", 1, 100), chunk => streamed.Append(chunk), cancellation.Token);
        Check(result.Status == "completed" && result.Tool == "tracert" && result.ExitCode == 0 && result.Output.Contains("127.0.0.1")
            && result.Output == streamed.ToString(), "Native Windows tracert should stream a real localhost route and finish successfully");
    }

    private static async Task TestOwnedNativeFixture()
    {
        var testDirectory = Path.GetDirectoryName(typeof(TraceRunnerTests).Assembly.Location)!;
        var configuration = Directory.GetParent(testDirectory)!.Name;
        var executable = Path.GetFullPath(Path.Combine(testDirectory, "..", "..", "..", "..", "SmartPing.TraceFixture", "bin",
            configuration, "net8.0", "SmartPing.TraceFixture" + (OperatingSystem.IsWindows() ? ".exe" : "")));
        Check(File.Exists(executable), "Native trace fixture must be built alongside the test project");
        TraceRunner CreateRunner(TimeSpan? deadline = null) => new(() => new("ntrace", executable), deadline);

        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var argsResult = await CreateRunner().RunAsync(new("args.test", 17, 1234), null, guard.Token);
        Check(argsResult.Status == "completed" && argsResult.ExitCode == 0, "Native fixture argument inspection should complete");
        var arguments = JsonSerializer.Deserialize<string[]>(argsResult.Output);
        Check(arguments is not null && arguments.SequenceEqual(new[] { "--traceroute", "--no-color", "--no-rdns", "--map", "-q", "3",
            "--max-hops", "17", "--timeout", "1234", "--icmp-mode", "1", "args.test" }),
            "The actual native process must receive precisely the fixed argument tokens");

        var tcpArgsResult = await CreateRunner().RunAsync(new("args.test", 17, 1234, "tcp", 443, 64), null, guard.Token);
        var tcpArguments = JsonSerializer.Deserialize<string[]>(tcpArgsResult.Output);
        Check(tcpArgsResult.Status == "completed" && tcpArgsResult.ExitCode == 0 && tcpArguments is not null
            && tcpArguments.SequenceEqual(new[] { "--traceroute", "--no-color", "--no-rdns", "--map", "-q", "3", "--max-hops", "17",
                "--timeout", "1234", "--tcp", "--port", "443", "--psize", "64", "args.test" }),
            "The actual TCP native process must receive required port443 and optional psize64 without an ICMP mode or GeoIP override");

        var streamed = new StringBuilder();
        var flood = await CreateRunner().RunAsync(new("flood.test"), chunk => streamed.Append(chunk), guard.Token);
        Check(flood.Status == "completed" && flood.ExitCode == 0 && flood.Truncated
            && flood.Output.Length == TraceRunner.MaximumOutputChars && flood.Output == streamed.ToString(),
            "Actual stdout and stderr floods must remain capped while both pipes drain to successful completion");
        Check(flood.Output.All(c => c is 'o' or 'e'), "Flood output must contain only emitted native text");

        var failure = await CreateRunner().RunAsync(new("fail.test"), null, guard.Token);
        Check(failure.Status == "failed" && failure.Tool == "ntrace" && failure.ExitCode == 7
            && failure.Output.Contains("Fixture intentional failure."),
            "A native nonzero exit must retain its selected tool and stderr without retrying a fallback");

        var timeoutOutput = new StringBuilder();
        var watch = Stopwatch.StartNew();
        var timeout = await CreateRunner(TimeSpan.FromSeconds(2)).RunAsync(new("sleep.test"), chunk => timeoutOutput.Append(chunk), guard.Token);
        Check(timeout.Status == "timedOut" && timeout.Tool == "ntrace" && watch.Elapsed < TimeSpan.FromSeconds(10),
            "An actually running native process must be terminated at its overall deadline");
        AssertProcessStopped(GetFixtureProcessId(timeoutOutput.ToString()), "Timed-out native process must no longer exist");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cancellationOutput = new StringBuilder();
        int? runningPid = null;
        watch.Restart();
        var cancelled = await CreateRunner().RunAsync(new("sleep.test"), chunk =>
        {
            cancellationOutput.Append(chunk);
            var text = cancellationOutput.ToString();
            if (!text.Contains('\n')) return;
            runningPid = GetFixtureProcessId(text);
            cancellation.Cancel();
        }, cancellation.Token);
        Check(cancelled.Status == "cancelled" && runningPid.HasValue && watch.Elapsed < TimeSpan.FromSeconds(8),
            "User cancellation after native stdout must terminate a confirmed running process promptly");
        AssertProcessStopped(runningPid!.Value, "User-cancelled native process must no longer exist");
    }

    private static int GetFixtureProcessId(string output)
    {
        var line = output.Split('\n').Select(value => value.TrimEnd('\r')).SingleOrDefault(value => value.StartsWith("PID:", StringComparison.Ordinal));
        Check(line is not null && int.TryParse(line[4..], out var parsed) && parsed > 0,
            "Sleeping fixture must report its native process ID before termination");
        return int.Parse(line![4..], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AssertProcessStopped(int pid, string message)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Check(process.HasExited, message);
        }
        catch (ArgumentException) { /* No process with that ID remains. */ }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
