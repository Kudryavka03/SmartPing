using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SmartPing.Models;

namespace SmartPing.Services;

/// <summary>Executes a fixed native traceroute command without invoking any command interpreter.</summary>
public sealed class TraceRunner
{
    internal const int MaximumOutputChars = 65_536;
    private readonly Func<TraceExecutable?> resolveExecutable;
    private readonly TimeSpan deadline;

    public TraceRunner() : this(() => ResolveExecutable(AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("PATH"), Environment.SystemDirectory, File.Exists)) { }

    internal TraceRunner(Func<TraceExecutable?> resolveExecutable, TimeSpan? deadline = null)
    {
        this.resolveExecutable = resolveExecutable;
        this.deadline = deadline ?? TimeSpan.FromSeconds(120);
    }

    public TraceToolInfo GetTool()
    {
        var tool = resolveExecutable();
        var supportsOptions = tool?.Name == "ntrace";
        return new(tool is not null, tool?.Name, supportsOptions, supportsOptions);
    }

    public async Task<TraceExecutionResult> RunAsync(TraceExecutionRequest request, Action<string>? onOutput,
        CancellationToken cancellationToken)
    {
        // This boundary is independent of API validation, including requests received by remote agents.
        if (Validation.Trace(request) is { } validationError)
            return new(null, "failed", null, "", false, validationError);
        if (cancellationToken.IsCancellationRequested)
            return new(null, "cancelled", null, "", false, null);

        var executable = resolveExecutable();
        if (executable is null)
            return new(null, "failed", null, "", false, "未找到 ntrace.exe 或 Windows tracert.exe。");
        if (executable.Name != "ntrace" && (request.Protocol == "tcp" || request.Psize is not null))
            return new(executable.Name, "failed", null, "", false, "TCP 路由跟踪和 psize 参数需要 ntrace；当前仅找到 tracert，请安装 ntrace 后重试。");

        var output = new TraceOutput(onOutput);
        using var process = new Process { StartInfo = BuildStartInfo(executable, request) };
        using var timeout = new CancellationTokenSource(deadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var stopReaders = new CancellationTokenSource();
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        var started = false;
        var status = "failed";
        string? error = null;
        int? exitCode = null;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            started = process.Start();
            if (!started) throw new InvalidOperationException("Native process did not start.");
            process.StandardInput.Close();
            stdout = ReadOutputAsync(process.StandardOutput, output, stopReaders.Token);
            stderr = ReadOutputAsync(process.StandardError, output, stopReaders.Token);
            await process.WaitForExitAsync(linked.Token);
            // A descendant retaining a pipe must not bypass the same overall deadline.
            await Task.WhenAll(stdout, stderr).WaitAsync(linked.Token);
            exitCode = process.ExitCode;
            status = exitCode == 0 ? "completed" : "failed";
            if (exitCode != 0) error = $"{executable.Name} 执行失败（退出码 {exitCode}）。";
        }
        catch (OperationCanceledException)
        {
            status = cancellationToken.IsCancellationRequested ? "cancelled" : "timedOut";
            if (status == "timedOut") error = "路由跟踪超过 120 秒，已终止。";
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Do not disclose executable paths or exception details through the public API.
            error = $"无法启动或读取 {executable.Name}，请检查安装和运行权限。";
        }
        finally
        {
            if (started)
            {
                if (!HasExited(process))
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or Win32Exception) { }
                }
                stopReaders.Cancel();
                try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException) { }
                if (HasExited(process)) exitCode ??= process.ExitCode;
            }
        }

        var snapshot = output.Snapshot();
        return new(executable.Name, status, exitCode, snapshot.Text, snapshot.Truncated, error);
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    internal static TraceExecutable? ResolveExecutable(string baseDirectory, string? path, string systemDirectory,
        Func<string, bool> exists, Architecture? processArchitecture = null)
    {
        // Prefer the versioned, published bundle; migration must not depend on a machine-specific PATH.
        if ((processArchitecture ?? RuntimeInformation.ProcessArchitecture) == Architecture.X64 && Path.IsPathFullyQualified(baseDirectory))
        {
            var bundled = Path.GetFullPath(Path.Combine(baseDirectory, "tools", "nexttrace", "win-x64", "ntrace.exe"));
            if (exists(bundled)) return new("ntrace", bundled);
        }
        foreach (var directory in new[] { baseDirectory }.Concat((path ?? "").Split(Path.PathSeparator)))
        {
            // Never search the current directory, relative PATH entries, PATHEXT, or shell wrappers.
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) continue;
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, "ntrace.exe"));
                if (exists(candidate)) return new("ntrace", candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }

        // Only the operating system's trusted absolute location may provide the fallback.
        if (!string.IsNullOrEmpty(systemDirectory) && Path.IsPathFullyQualified(systemDirectory))
        {
            var fallback = Path.GetFullPath(Path.Combine(systemDirectory, "tracert.exe"));
            if (exists(fallback)) return new("tracert", fallback);
        }
        return null;
    }

    internal static ProcessStartInfo BuildStartInfo(TraceExecutable executable, TraceExecutionRequest request)
    {
        var encoding = executable.Name == "ntrace" ? new UTF8Encoding(false) : GetTracertEncoding();
        var info = new ProcessStartInfo
        {
            FileName = executable.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
            WorkingDirectory = Path.GetDirectoryName(executable.Path)!
        };
        var hops = request.MaxHops.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var timeout = request.TimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        List<string> arguments;
        if (executable.Name == "ntrace")
        {
            arguments = ["--traceroute", "--no-color", "--no-rdns", "--map", "-q", "3", "--max-hops", hops, "--timeout", timeout];
            if (request.Protocol == "tcp")
                arguments.AddRange(["--tcp", "--port", request.Port!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            else
                arguments.AddRange(["--icmp-mode", "1"]);
            if (request.Psize is { } packetSize)
                arguments.AddRange(["--psize", packetSize.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            arguments.Add(request.Host);
        }
        else
        {
            arguments = ["-d", "-h", hops, "-w", timeout, request.Host];
        }
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static Encoding GetTracertEncoding()
    {
        if (!OperatingSystem.IsWindows()) return new UTF8Encoding(false);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding((int)GetOEMCP());
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static async Task ReadOutputAsync(StreamReader reader, TraceOutput output, CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        var filter = new TerminalOutputFilter();
        try
        {
            int length;
            while ((length = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
                output.Append(filter.Filter(buffer.AsSpan(0, length)));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    internal sealed record TraceExecutable(string Name, string Path);

    internal sealed class TraceOutput(Action<string>? onOutput)
    {
        private readonly object gate = new();
        private readonly StringBuilder text = new();
        private bool truncated;

        public void Append(string chunk)
        {
            if (chunk.Length == 0) return;
            lock (gate)
            {
                var remaining = MaximumOutputChars - text.Length;
                if (chunk.Length > remaining) truncated = true;
                if (remaining <= 0) return; // The pipe reader keeps draining after this cap.
                var accepted = chunk.Length <= remaining ? chunk : chunk[..remaining];
                text.Append(accepted);
                try { onOutput?.Invoke(accepted); }
                catch { /* A disconnected observer must not stop draining native stdout/stderr. */ }
            }
        }

        public (string Text, bool Truncated) Snapshot()
        {
            lock (gate) return (text.ToString(), truncated);
        }
    }

    internal sealed class TerminalOutputFilter
    {
        private int state;

        public string Filter(ReadOnlySpan<char> input)
        {
            var clean = new StringBuilder(input.Length);
            foreach (var c in input)
            {
                switch (state)
                {
                    case 1: state = c == '[' ? 2 : c == ']' ? 3 : 0; continue;
                    case 2: if (c is >= '@' and <= '~') state = 0; continue;
                    case 3: if (c == '\a' || c == '\u009c') state = 0; else if (c == '\u001b') state = 4; continue;
                    case 4: state = c == '\\' ? 0 : 3; continue;
                }
                if (c == '\u001b') { state = 1; continue; }
                if (c == '\u009b') { state = 2; continue; }
                if (c == '\u009d') { state = 3; continue; }
                if (char.IsControl(c) && c is not ('\n' or '\r' or '\t')) continue;
                clean.Append(c);
            }
            return clean.ToString();
        }
    }
}
