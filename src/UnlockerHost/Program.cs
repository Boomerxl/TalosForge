using Microsoft.Extensions.Logging;
using TalosForge.UnlockerHost.Abstractions;
using TalosForge.UnlockerHost.Configuration;
using TalosForge.UnlockerHost.Execution;
using TalosForge.UnlockerHost.Host;

namespace TalosForge.UnlockerHost;

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

        var logger = loggerFactory.CreateLogger("TalosForge.UnlockerHost");
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

        using var host = new UnlockerHostService(options, executor, loggerFactory.CreateLogger<UnlockerHostService>());
        await host.RunAsync(cts.Token).ConfigureAwait(false);
    }

    private static ICommandExecutor CreateExecutor(UnlockerHostOptions options, ILogger logger)
    {
        switch (options.ExecutorMode.Trim().ToLowerInvariant())
        {
            case "mock":
                logger.LogInformation("Executor mode: mock");
                return new MockCommandExecutor();
            case "null":
                logger.LogInformation("Executor mode: null");
                return new NullCommandExecutor();
            default:
                logger.LogWarning("Unknown executor mode '{Mode}'. Falling back to 'mock'.", options.ExecutorMode);
                options.ExecutorMode = "mock";
                return new MockCommandExecutor();
        }
    }

    private static UnlockerHostOptions ParseArgs(string[] args, ILogger logger)
    {
        var options = new UnlockerHostOptions();

        const string commandRingArg = "--command-ring";
        const string eventRingArg = "--event-ring";
        const string capacityArg = "--ring-capacity";
        const string executorArg = "--executor";
        const string pollArg = "--poll-ms";
        const string ackRetriesArg = "--ack-retries";
        const string ackDelayArg = "--ack-delay-ms";
        const string statsArg = "--stats-interval";
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

            if (!TryParseOption(args, ref i, commandRingArg, out var value) &&
                !TryParseOption(args, ref i, eventRingArg, out value) &&
                !TryParseOption(args, ref i, capacityArg, out value) &&
                !TryParseOption(args, ref i, executorArg, out value) &&
                !TryParseOption(args, ref i, pollArg, out value) &&
                !TryParseOption(args, ref i, ackRetriesArg, out value) &&
                !TryParseOption(args, ref i, ackDelayArg, out value) &&
                !TryParseOption(args, ref i, statsArg, out value) &&
                !TryParseOption(args, ref i, smokeSecondsArg, out value))
            {
                logger.LogWarning("Unknown argument: {Argument}", arg);
                continue;
            }

            if (arg.StartsWith(commandRingArg, StringComparison.OrdinalIgnoreCase))
            {
                options.CommandRingName = value;
            }
            else if (arg.StartsWith(eventRingArg, StringComparison.OrdinalIgnoreCase))
            {
                options.EventRingName = value;
            }
            else if (arg.StartsWith(executorArg, StringComparison.OrdinalIgnoreCase))
            {
                options.ExecutorMode = value;
            }
            else if (arg.StartsWith(capacityArg, StringComparison.OrdinalIgnoreCase))
            {
                options.RingCapacityBytes = ParseInt(value, capacityArg, options.RingCapacityBytes, min: 256, logger);
            }
            else if (arg.StartsWith(pollArg, StringComparison.OrdinalIgnoreCase))
            {
                options.PollDelayMs = ParseInt(value, pollArg, options.PollDelayMs, min: 1, logger);
            }
            else if (arg.StartsWith(ackRetriesArg, StringComparison.OrdinalIgnoreCase))
            {
                options.AckWriteRetryCount = ParseInt(value, ackRetriesArg, options.AckWriteRetryCount, min: 0, logger);
            }
            else if (arg.StartsWith(ackDelayArg, StringComparison.OrdinalIgnoreCase))
            {
                options.AckWriteDelayMs = ParseInt(value, ackDelayArg, options.AckWriteDelayMs, min: 1, logger);
            }
            else if (arg.StartsWith(statsArg, StringComparison.OrdinalIgnoreCase))
            {
                options.StatsIntervalSeconds = ParseInt(value, statsArg, options.StatsIntervalSeconds, min: 1, logger);
            }
            else if (arg.StartsWith(smokeSecondsArg, StringComparison.OrdinalIgnoreCase))
            {
                options.SmokeDurationSeconds = ParseInt(value, smokeSecondsArg, options.SmokeDurationSeconds, min: 1, logger);
            }
        }

        return options;
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
