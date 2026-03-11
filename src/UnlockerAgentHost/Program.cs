using Microsoft.Extensions.Logging;
using TalosForge.UnlockerAgentHost.Execution;
using TalosForge.UnlockerAgentHost.Runtime;

namespace TalosForge.UnlockerAgentHost;

public static class Program
{
    public static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(LogLevel.Information)
                .AddSimpleConsole(console =>
                {
                    console.SingleLine = true;
                    console.TimestampFormat = "HH:mm:ss ";
                });
        });

        var logger = loggerFactory.CreateLogger("TalosForge.UnlockerAgentHost");
        var options = ParseArgs(args, logger);
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        if (options.SmokeMode)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.SmokeDurationSeconds)));
            logger.LogInformation("Smoke mode enabled. duration_seconds={Duration}", options.SmokeDurationSeconds);
        }

        await using var runtime = CreateRuntime(options, logger);
        var sessionManager = new AgentSessionManager(options, runtime);
        var processor = new AgentCommandProcessor(
            options,
            sessionManager,
            runtime,
            loggerFactory.CreateLogger<AgentCommandProcessor>());
        var service = new AgentPipeService(
            options,
            processor,
            loggerFactory.CreateLogger<AgentPipeService>());

        logger.LogInformation(
            "Agent config pipe={Pipe} wow_process={Process} runtime={RuntimeMode} retry_count={RetryCount} backoff_ms={BaseBackoff}-{MaxBackoff} evasion={Evasion}",
            options.PipeName,
            options.WowProcessName,
            options.RuntimeMode,
            options.RetryCount,
            options.BackoffBaseMs,
            options.BackoffMaxMs,
            options.DisableEvasion ? "off" : options.EvasionProfile);

        await service.RunAsync(cts.Token).ConfigureAwait(false);
    }

    private static IAgentRuntime CreateRuntime(AgentHostOptions options, ILogger logger)
    {
        var mode = string.IsNullOrWhiteSpace(options.RuntimeMode)
            ? "auto"
            : options.RuntimeMode.Trim().ToLowerInvariant();

        if (mode == "simulated")
        {
            logger.LogWarning("Agent runtime mode=simulated (no native injection).");
            return new SimulatedAgentRuntime(options);
        }

        if (mode == "native")
        {
            logger.LogInformation("Agent runtime mode=native.");
            return new NativePipeAgentRuntime(options);
        }

        var nativeDll = NativePipeAgentRuntime.ResolveNativeDllPath(options);
        if (!string.IsNullOrWhiteSpace(nativeDll))
        {
            logger.LogInformation("Agent runtime mode=auto resolved native DLL at {DllPath}.", nativeDll);
            options.NativeDllPath = nativeDll;
            return new NativePipeAgentRuntime(options);
        }

        logger.LogWarning("Agent runtime mode=auto fallback to simulated (native DLL not found).");
        return new SimulatedAgentRuntime(options);
    }

    private static AgentHostOptions ParseArgs(string[] args, ILogger logger)
    {
        var options = new AgentHostOptions();

        const string pipeArg = "--pipe-name";
        const string wowProcessArg = "--wow-process";
        const string timeoutArg = "--request-timeout-ms";
        const string runtimeModeArg = "--runtime-mode";
        const string retryCountArg = "--retry-count";
        const string backoffBaseArg = "--backoff-base-ms";
        const string backoffMaxArg = "--backoff-max-ms";
        const string nativeDllPathArg = "--native-dll-path";
        const string nativePipePrefixArg = "--native-pipe-prefix";
        const string nativeConnectTimeoutArg = "--native-connect-timeout-ms";
        const string evasionProfileArg = "--evasion-profile";
        const string disableEvasionArg = "--disable-evasion";
        const string smokeArg = "--smoke";
        const string smokeSecondsArg = "--smoke-seconds";
        const string simulateEvasionFailureArg = "--simulate-evasion-init-failure";

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals(smokeArg, StringComparison.OrdinalIgnoreCase))
            {
                options.SmokeMode = true;
                continue;
            }

            if (arg.Equals(disableEvasionArg, StringComparison.OrdinalIgnoreCase))
            {
                options.DisableEvasion = true;
                continue;
            }

            if (arg.Equals(simulateEvasionFailureArg, StringComparison.OrdinalIgnoreCase))
            {
                options.SimulateEvasionInitFailure = true;
                continue;
            }

            if (!TryParseOption(args, ref i, pipeArg, out var value) &&
                !TryParseOption(args, ref i, wowProcessArg, out value) &&
                !TryParseOption(args, ref i, timeoutArg, out value) &&
                !TryParseOption(args, ref i, runtimeModeArg, out value) &&
                !TryParseOption(args, ref i, retryCountArg, out value) &&
                !TryParseOption(args, ref i, backoffBaseArg, out value) &&
                !TryParseOption(args, ref i, backoffMaxArg, out value) &&
                !TryParseOption(args, ref i, nativeDllPathArg, out value) &&
                !TryParseOption(args, ref i, nativePipePrefixArg, out value) &&
                !TryParseOption(args, ref i, nativeConnectTimeoutArg, out value) &&
                !TryParseOption(args, ref i, evasionProfileArg, out value) &&
                !TryParseOption(args, ref i, smokeSecondsArg, out value))
            {
                logger.LogWarning("Unknown argument: {Argument}", arg);
                continue;
            }

            if (arg.StartsWith(pipeArg, StringComparison.OrdinalIgnoreCase))
            {
                options.PipeName = value;
            }
            else if (arg.StartsWith(wowProcessArg, StringComparison.OrdinalIgnoreCase))
            {
                options.WowProcessName = value;
            }
            else if (arg.StartsWith(timeoutArg, StringComparison.OrdinalIgnoreCase))
            {
                options.RequestTimeoutMs = ParseInt(value, timeoutArg, options.RequestTimeoutMs, 1, logger);
            }
            else if (arg.StartsWith(runtimeModeArg, StringComparison.OrdinalIgnoreCase))
            {
                options.RuntimeMode = ParseRuntimeMode(value, options.RuntimeMode, logger);
            }
            else if (arg.StartsWith(retryCountArg, StringComparison.OrdinalIgnoreCase))
            {
                options.RetryCount = ParseInt(value, retryCountArg, options.RetryCount, 0, logger);
            }
            else if (arg.StartsWith(backoffBaseArg, StringComparison.OrdinalIgnoreCase))
            {
                options.BackoffBaseMs = ParseInt(value, backoffBaseArg, options.BackoffBaseMs, 1, logger);
            }
            else if (arg.StartsWith(backoffMaxArg, StringComparison.OrdinalIgnoreCase))
            {
                options.BackoffMaxMs = ParseInt(value, backoffMaxArg, options.BackoffMaxMs, 1, logger);
            }
            else if (arg.StartsWith(nativeDllPathArg, StringComparison.OrdinalIgnoreCase))
            {
                options.NativeDllPath = value;
            }
            else if (arg.StartsWith(nativePipePrefixArg, StringComparison.OrdinalIgnoreCase))
            {
                options.NativePipePrefix = value;
            }
            else if (arg.StartsWith(nativeConnectTimeoutArg, StringComparison.OrdinalIgnoreCase))
            {
                options.NativeConnectTimeoutMs = ParseInt(value, nativeConnectTimeoutArg, options.NativeConnectTimeoutMs, 1, logger);
            }
            else if (arg.StartsWith(evasionProfileArg, StringComparison.OrdinalIgnoreCase))
            {
                options.EvasionProfile = ParseEvasionProfile(value, options.EvasionProfile, logger);
            }
            else if (arg.StartsWith(smokeSecondsArg, StringComparison.OrdinalIgnoreCase))
            {
                options.SmokeDurationSeconds = ParseInt(value, smokeSecondsArg, options.SmokeDurationSeconds, 1, logger);
            }
        }

        if (options.BackoffMaxMs < options.BackoffBaseMs)
        {
            options.BackoffMaxMs = options.BackoffBaseMs;
        }

        return options;
    }

    private static string ParseRuntimeMode(string value, string fallback, ILogger logger)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "auto" or "native" or "simulated" => normalized,
            _ => LogAndReturnRuntimeFallback(logger, value, fallback),
        };
    }

    private static string LogAndReturnRuntimeFallback(ILogger logger, string value, string fallback)
    {
        logger.LogWarning("Invalid runtime mode '{Value}'. Keeping '{Fallback}'.", value, fallback);
        return fallback;
    }

    private static string ParseEvasionProfile(string value, string fallback, ILogger logger)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "off" or "standard" or "full" => normalized,
            _ => LogAndReturnFallback(logger, value, fallback),
        };
    }

    private static string LogAndReturnFallback(ILogger logger, string value, string fallback)
    {
        logger.LogWarning("Invalid evasion profile '{Value}'. Keeping '{Fallback}'.", value, fallback);
        return fallback;
    }

    private static bool TryParseOption(string[] args, ref int index, string optionName, out string value)
    {
        var current = args[index];
        value = string.Empty;

        if (current.Equals(optionName, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= args.Length)
            {
                return false;
            }

            value = args[++index];
            return true;
        }

        var prefix = optionName + "=";
        if (current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = current[prefix.Length..];
            return true;
        }

        return false;
    }

    private static int ParseInt(string value, string optionName, int fallback, int min, ILogger logger)
    {
        if (!int.TryParse(value, out var parsed) || parsed < min)
        {
            logger.LogWarning(
                "Invalid value '{Value}' for {Option}. Keeping {Fallback}.",
                value,
                optionName,
                fallback);
            return fallback;
        }

        return parsed;
    }
}
