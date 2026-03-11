using Microsoft.Extensions.Logging;
using TalosForge.AdapterBridge.Execution;
using TalosForge.AdapterBridge.Runtime;

namespace TalosForge.AdapterBridge;

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

        var logger = loggerFactory.CreateLogger("TalosForge.AdapterBridge");
        var options = ParseArgs(args, logger);
        var executor = CreateExecutor(options, logger);

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

        var service = new PipeBridgeService(options, executor, loggerFactory.CreateLogger<PipeBridgeService>());
        await service.RunAsync(cts.Token).ConfigureAwait(false);
    }

    private static IBridgeCommandExecutor CreateExecutor(BridgeOptions options, ILogger logger)
    {
        switch (options.Mode.Trim().ToLowerInvariant())
        {
            case "mock":
                logger.LogInformation("Bridge mode: mock");
                return new MockBridgeCommandExecutor();
            case "wow-cli":
            case "wowcli":
                logger.LogInformation(
                    "Bridge mode: wow-cli path={Path} timeout_ms={TimeoutMs}",
                    options.CommandPath ?? "n/a",
                    options.CommandTimeoutMs);
                logger.LogWarning("Bridge mode 'wow-cli' is experimental/unsafe for live clients. Prefer 'wow-agent'.");
                return new WowCliBridgeCommandExecutor(options);
            case "wow-agent":
            case "wowagent":
                logger.LogInformation(
                    "Bridge mode: wow-agent pipe={Pipe} connect_timeout_ms={ConnectTimeoutMs} request_timeout_ms={RequestTimeoutMs} evasion={EvasionProfile}",
                    options.AgentPipeName,
                    options.AgentConnectTimeoutMs,
                    options.AgentRequestTimeoutMs,
                    options.AgentEvasionProfile ?? "default");
                return new WowAgentBridgeCommandExecutor(options);
            case "process":
                logger.LogInformation(
                    "Bridge mode: process path={Path} timeout_ms={TimeoutMs}",
                    options.CommandPath ?? "n/a",
                    options.CommandTimeoutMs);
                return new ProcessBridgeCommandExecutor(options);
            default:
                logger.LogWarning("Unknown bridge mode '{Mode}'. Falling back to 'mock'.", options.Mode);
                options.Mode = "mock";
                return new MockBridgeCommandExecutor();
        }
    }

    private static BridgeOptions ParseArgs(string[] args, ILogger logger)
    {
        var options = new BridgeOptions();

        const string pipeArg = "--pipe-name";
        const string modeArg = "--mode";
        const string commandPathArg = "--command-path";
        const string commandArgsArg = "--command-args";
        const string commandTimeoutArg = "--command-timeout-ms";
        const string agentPipeArg = "--agent-pipe";
        const string agentConnectTimeoutArg = "--agent-connect-timeout-ms";
        const string agentRequestTimeoutArg = "--agent-request-timeout-ms";
        const string agentEvasionProfileArg = "--agent-evasion-profile";
        const string smokeArg = "--smoke";
        const string smokeSecondsArg = "--smoke-seconds";

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals(smokeArg, StringComparison.OrdinalIgnoreCase))
            {
                options.SmokeMode = true;
                continue;
            }

            if (!TryParseOption(args, ref i, pipeArg, out var value) &&
                !TryParseOption(args, ref i, modeArg, out value) &&
                !TryParseOption(args, ref i, commandPathArg, out value) &&
                !TryParseOption(args, ref i, commandArgsArg, out value) &&
                !TryParseOption(args, ref i, commandTimeoutArg, out value) &&
                !TryParseOption(args, ref i, agentPipeArg, out value) &&
                !TryParseOption(args, ref i, agentConnectTimeoutArg, out value) &&
                !TryParseOption(args, ref i, agentRequestTimeoutArg, out value) &&
                !TryParseOption(args, ref i, agentEvasionProfileArg, out value) &&
                !TryParseOption(args, ref i, smokeSecondsArg, out value))
            {
                logger.LogWarning("Unknown argument: {Argument}", arg);
                continue;
            }

            if (arg.StartsWith(pipeArg, StringComparison.OrdinalIgnoreCase))
            {
                options.PipeName = value;
            }
            else if (arg.StartsWith(modeArg, StringComparison.OrdinalIgnoreCase))
            {
                options.Mode = value;
            }
            else if (arg.StartsWith(commandPathArg, StringComparison.OrdinalIgnoreCase))
            {
                options.CommandPath = value;
            }
            else if (arg.StartsWith(commandArgsArg, StringComparison.OrdinalIgnoreCase))
            {
                options.CommandArgs = value;
            }
            else if (arg.StartsWith(commandTimeoutArg, StringComparison.OrdinalIgnoreCase))
            {
                options.CommandTimeoutMs = ParseInt(value, commandTimeoutArg, options.CommandTimeoutMs, 1, logger);
            }
            else if (arg.StartsWith(agentPipeArg, StringComparison.OrdinalIgnoreCase))
            {
                options.AgentPipeName = value;
            }
            else if (arg.StartsWith(agentConnectTimeoutArg, StringComparison.OrdinalIgnoreCase))
            {
                options.AgentConnectTimeoutMs = ParseInt(value, agentConnectTimeoutArg, options.AgentConnectTimeoutMs, 1, logger);
            }
            else if (arg.StartsWith(agentRequestTimeoutArg, StringComparison.OrdinalIgnoreCase))
            {
                options.AgentRequestTimeoutMs = ParseInt(value, agentRequestTimeoutArg, options.AgentRequestTimeoutMs, 1, logger);
            }
            else if (arg.StartsWith(agentEvasionProfileArg, StringComparison.OrdinalIgnoreCase))
            {
                options.AgentEvasionProfile = ParseEvasionProfile(value, logger);
            }
            else if (arg.StartsWith(smokeSecondsArg, StringComparison.OrdinalIgnoreCase))
            {
                options.SmokeDurationSeconds = ParseInt(value, smokeSecondsArg, options.SmokeDurationSeconds, 1, logger);
            }
        }

        return options;
    }

    private static string? ParseEvasionProfile(string value, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "off" or "standard" or "full" => normalized,
            _ => LogInvalidEvasionProfile(value, logger),
        };
    }

    private static string? LogInvalidEvasionProfile(string value, ILogger logger)
    {
        logger.LogWarning("Invalid --agent-evasion-profile value '{Value}'. Expected off|standard|full.", value);
        return null;
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
