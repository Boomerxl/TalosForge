using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using TalosForge.UnlockerAgentHost.Models;

namespace TalosForge.UnlockerAgentHost.Runtime;

public sealed class NativePipeAgentRuntime : IAgentRuntime
{
    private static readonly TimeSpan InjectionRetryCooldown = TimeSpan.FromSeconds(8);

    private readonly AgentHostOptions _options;
    private readonly object _sync = new();
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _currentPid;
    private string _currentProfile = "off";
    private bool _ready;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private DateTimeOffset _lastInjectionAttemptUtc = DateTimeOffset.MinValue;
    private int _lastInjectionPid;
    private string? _lastInjectionError;

    public NativePipeAgentRuntime(AgentHostOptions options)
    {
        _options = options;
    }

    public async ValueTask<AgentRuntimeReadyResult> EnsureReadyAsync(string evasionProfile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetWowProcessId(out var processId))
        {
            MarkDisconnected();
            return new AgentRuntimeReadyResult(false, "WoW process not found.", AgentResultCodes.NotInGame);
        }

        var dllPath = ResolveNativeDllPath(_options);
        if (string.IsNullOrWhiteSpace(dllPath))
        {
            MarkDisconnected();
            return new AgentRuntimeReadyResult(
                false,
                "Native agent DLL path could not be resolved. Build native agent first.",
                AgentResultCodes.BackendUnavailable);
        }

        var profile = string.IsNullOrWhiteSpace(evasionProfile) ? "off" : evasionProfile.Trim().ToLowerInvariant();
        var pipeName = $"{_options.NativePipePrefix}.{processId}";

        try
        {
            // If a prior attempt already injected this process, prefer a quick reconnect
            // over issuing repeated remote-thread injections.
            if (IsInjectionBackoffActive(processId))
            {
                await EnsurePipeConnectedAsync(processId, pipeName, 300, cancellationToken).ConfigureAwait(false);

                lock (_sync)
                {
                    _ready = true;
                    _currentProfile = profile;
                    _lastInjectionError = null;
                }

                return new AgentRuntimeReadyResult(
                    true,
                    $"Native runtime ready (pid={processId}, pipe={pipeName}, evasion={profile}, reused_pending_injection=true).",
                    AgentResultCodes.Ok);
            }
        }
        catch
        {
            // Proceed with injection attempt below.
        }

        RecordInjectionAttempt(processId);

        if (!NativeAgentInjector.TryInject(processId, dllPath, _options.NativeConnectTimeoutMs, out var injectError))
        {
            RecordInjectionFailure(processId, injectError);

            if (IsInjectionTimeoutError(injectError))
            {
                try
                {
                    await EnsurePipeConnectedAsync(processId, pipeName, 600, cancellationToken).ConfigureAwait(false);

                    lock (_sync)
                    {
                        _ready = true;
                        _currentProfile = profile;
                        _lastInjectionError = null;
                    }

                    return new AgentRuntimeReadyResult(
                        true,
                        $"Native runtime ready (pid={processId}, pipe={pipeName}, evasion={profile}, delayed_injection_connect=true).",
                        AgentResultCodes.Ok);
                }
                catch
                {
                    // Keep original injection timeout as failure reason.
                }
            }

            MarkDisconnected();
            return new AgentRuntimeReadyResult(false, injectError, AgentResultCodes.InjectionFailed);
        }

        try
        {
            await EnsurePipeConnectedAsync(processId, pipeName, _options.NativeConnectTimeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MarkDisconnected();
            return new AgentRuntimeReadyResult(
                false,
                $"Native pipe connect failed ({ex.GetType().Name}).",
                AgentResultCodes.HookNotReady);
        }

        lock (_sync)
        {
            _ready = true;
            _currentProfile = profile;
            _lastInjectionError = null;
        }

        return new AgentRuntimeReadyResult(
            true,
            $"Native runtime ready (pid={processId}, pipe={pipeName}, evasion={profile}).",
            AgentResultCodes.Ok);
    }

    public async ValueTask<AgentRuntimeExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_ready)
        {
            return new AgentRuntimeExecutionResult(
                false,
                "Native runtime not ready.",
                null,
                AgentResultCodes.HookNotReady,
                TransientFailure: true);
        }

        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var writer = _writer;
            var reader = _reader;
            var pipe = _pipe;
            if (pipe is null || writer is null || reader is null || !pipe.IsConnected)
            {
                MarkDisconnected();
                return new AgentRuntimeExecutionResult(
                    false,
                    "Native pipe disconnected.",
                    null,
                    AgentResultCodes.BackendUnavailable,
                    TransientFailure: true);
            }

            try
            {
                await writer.WriteLineAsync(request.Opcode).ConfigureAwait(false);
                await writer.WriteLineAsync(request.PayloadJson).ConfigureAwait(false);
                await writer.WriteLineAsync(request.RequestTimeoutMs.ToString()).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                MarkDisconnected();
                return new AgentRuntimeExecutionResult(
                    false,
                    $"Native pipe write failed ({ex.GetType().Name}).",
                    null,
                    AgentResultCodes.BackendUnavailable,
                    TransientFailure: true);
            }

            var timeoutMs = Math.Max(1, request.RequestTimeoutMs);
            var successLine = await ReadLineWithTimeoutAsync(reader, timeoutMs, cancellationToken).ConfigureAwait(false);
            if (successLine is null)
            {
                MarkDisconnected();
                return TimeoutResult(timeoutMs);
            }

            var code = await ReadLineWithTimeoutAsync(reader, timeoutMs, cancellationToken).ConfigureAwait(false);
            var message = await ReadLineWithTimeoutAsync(reader, timeoutMs, cancellationToken).ConfigureAwait(false);
            var payload = await ReadLineWithTimeoutAsync(reader, timeoutMs, cancellationToken).ConfigureAwait(false);

            if (code is null || message is null || payload is null)
            {
                MarkDisconnected();
                return new AgentRuntimeExecutionResult(
                    false,
                    "Native pipe response was truncated.",
                    null,
                    AgentResultCodes.InternalError,
                    TransientFailure: true);
            }

            var success = string.Equals(successLine.Trim(), "1", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(successLine.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            var normalizedCode = string.IsNullOrWhiteSpace(code)
                ? (success ? AgentResultCodes.Ok : AgentResultCodes.InternalError)
                : code.Trim();
            var normalizedPayload = string.IsNullOrWhiteSpace(payload) ? null : payload;

            return new AgentRuntimeExecutionResult(
                success,
                message,
                normalizedPayload,
                normalizedCode,
                TransientFailure: !success &&
                                  (normalizedCode == AgentResultCodes.ExecutionTimeout ||
                                   normalizedCode == AgentResultCodes.BackendUnavailable ||
                                   normalizedCode == AgentResultCodes.HookNotReady));
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _requestLock.Dispose();
        MarkDisconnected();
        return ValueTask.CompletedTask;
    }

    public static string? ResolveNativeDllPath(AgentHostOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.NativeDllPath))
        {
            var full = Path.GetFullPath(options.NativeDllPath);
            return File.Exists(full) ? full : null;
        }

        var repoRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(repoRoot, "artifacts", "native-agent", "build", "Release", "Release", "TalosForge.UnlockerAgent.Native.dll"),
            Path.Combine(repoRoot, "artifacts", "native-agent", "build", "Debug", "Debug", "TalosForge.UnlockerAgent.Native.dll"),
            Path.Combine(repoRoot, "src", "UnlockerAgent.Native", "build", "Release", "TalosForge.UnlockerAgent.Native.dll")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task EnsurePipeConnectedAsync(
        int processId,
        string pipeName,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_pipe is { IsConnected: true } && _currentPid == processId)
            {
                return;
            }
        }

        MarkDisconnected();

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(Math.Max(1, timeoutMs));
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);

            var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

            lock (_sync)
            {
                _pipe = pipe;
                _writer = writer;
                _reader = reader;
                _currentPid = processId;
            }
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private bool IsInjectionBackoffActive(int processId)
    {
        lock (_sync)
        {
            if (_lastInjectionPid != processId || string.IsNullOrWhiteSpace(_lastInjectionError))
            {
                return false;
            }

            return (DateTimeOffset.UtcNow - _lastInjectionAttemptUtc) < InjectionRetryCooldown;
        }
    }

    private void RecordInjectionAttempt(int processId)
    {
        lock (_sync)
        {
            _lastInjectionPid = processId;
            _lastInjectionAttemptUtc = DateTimeOffset.UtcNow;
        }
    }

    private void RecordInjectionFailure(int processId, string error)
    {
        lock (_sync)
        {
            _lastInjectionPid = processId;
            _lastInjectionAttemptUtc = DateTimeOffset.UtcNow;
            _lastInjectionError = error;
        }
    }

    private static bool IsInjectionTimeoutError(string error)
    {
        return !string.IsNullOrWhiteSpace(error) &&
               error.Contains("timed out", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(
        StreamReader reader,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        var readTask = reader.ReadLineAsync();
        var delayTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);
        var completed = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);
        if (completed != readTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        try
        {
            return await readTask.ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static AgentRuntimeExecutionResult TimeoutResult(int timeoutMs)
    {
        return new AgentRuntimeExecutionResult(
            false,
            $"Native execution timed out after {timeoutMs}ms.",
            null,
            AgentResultCodes.ExecutionTimeout,
            TransientFailure: true);
    }

    private void MarkDisconnected()
    {
        lock (_sync)
        {
            _ready = false;
            _currentPid = 0;
            _currentProfile = "off";

            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }

            _writer = null;
            _reader = null;
            _pipe = null;
        }
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "TalosForge.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private bool TryGetWowProcessId(out int processId)
    {
        processId = 0;
        var processes = Process.GetProcessesByName(_options.WowProcessName);
        if (processes.Length == 0)
        {
            return false;
        }

        try
        {
            var selected = processes
                .OrderByDescending(static p => p.MainWindowHandle != IntPtr.Zero)
                .ThenByDescending(static p =>
                {
                    try
                    {
                        return p.WorkingSet64;
                    }
                    catch
                    {
                        return 0;
                    }
                })
                .First();

            processId = selected.Id;
            return true;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
