using System.Globalization;

namespace TalosForge.UnlockerCli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (!TryParseOptions(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        if (options.ShowHelp || string.IsNullOrWhiteSpace(options.Verb))
        {
            PrintUsage();
            return options.ShowHelp ? 0 : 2;
        }

        if (!CommandTranslator.TryTranslate(
                options.Verb!,
                options.VerbArguments,
                out var command,
                out error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        if (options.DryRun)
        {
            Console.WriteLine(command.AckMessage);
            return 0;
        }

        if (!WowLuaRemoteExecutor.TryExecute(
                options.WowProcessName,
                command.LuaCode,
                options.LuaExecuteAddress,
                options.UseHardwareEventFlag ? options.HardwareEventFlagAddress : null,
                options.SourceLabel,
                out error))
        {
            Console.Error.WriteLine(error);
            return 4;
        }

        Console.WriteLine(command.AckMessage);
        return 0;
    }

    private static bool TryParseOptions(
        IReadOnlyList<string> args,
        out CliOptions options,
        out string error)
    {
        options = new CliOptions();
        error = string.Empty;

        var positional = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
            {
                options = options with { ShowHelp = true };
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
            {
                options = options with { ShowHelp = true };
                continue;
            }

            if (arg.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                options = options with { DryRun = true };
                continue;
            }

            if (arg.Equals("--no-hardware-flag", StringComparison.OrdinalIgnoreCase))
            {
                options = options with { UseHardwareEventFlag = false };
                continue;
            }

            if (TryGetOptionValue(args, ref i, "--wow-process", out var wowProcess))
            {
                if (string.IsNullOrWhiteSpace(wowProcess))
                {
                    error = "--wow-process requires a value.";
                    return false;
                }

                options = options with { WowProcessName = wowProcess.Trim() };
                continue;
            }

            if (TryGetOptionValue(args, ref i, "--lua-exec-addr", out var luaExecAddressRaw))
            {
                if (!TryParseHexOrDecimalUInt(luaExecAddressRaw, out var parsed))
                {
                    error = $"Invalid --lua-exec-addr value: {luaExecAddressRaw}";
                    return false;
                }

                options = options with { LuaExecuteAddress = parsed };
                continue;
            }

            if (TryGetOptionValue(args, ref i, "--hardware-flag-addr", out var hardwareFlagRaw))
            {
                if (!TryParseHexOrDecimalUInt(hardwareFlagRaw, out var parsed))
                {
                    error = $"Invalid --hardware-flag-addr value: {hardwareFlagRaw}";
                    return false;
                }

                options = options with { HardwareEventFlagAddress = parsed };
                continue;
            }

            if (TryGetOptionValue(args, ref i, "--source", out var sourceLabel))
            {
                options = options with
                {
                    SourceLabel = string.IsNullOrWhiteSpace(sourceLabel) ? options.SourceLabel : sourceLabel.Trim()
                };
                continue;
            }

            error = $"Unknown option: {arg}";
            return false;
        }

        if (positional.Count > 0)
        {
            options = options with
            {
                Verb = positional[0],
                VerbArguments = positional.Skip(1).ToArray()
            };
        }

        return true;
    }

    private static bool TryGetOptionValue(
        IReadOnlyList<string> args,
        ref int index,
        string optionName,
        out string value)
    {
        var current = args[index];
        value = string.Empty;

        if (current.Equals(optionName, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= args.Count)
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

    private static bool TryParseHexOrDecimalUInt(string value, out uint parsed)
    {
        parsed = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed);
        }

        return uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("TalosForge.UnlockerCli");
        Console.WriteLine("Usage:");
        Console.WriteLine("  TalosForge.UnlockerCli <verb> [args...] [options]");
        Console.WriteLine();
        Console.WriteLine("Verbs:");
        Console.WriteLine("  lua <code>");
        Console.WriteLine("  cast <spell>");
        Console.WriteLine("  target <guid>");
        Console.WriteLine("  face <facing> <smoothing>");
        Console.WriteLine("  moveto <x> <y> <z> <overshootThreshold>");
        Console.WriteLine("  interact [guid]");
        Console.WriteLine("  stop");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --wow-process <name>          WoW process name (default: Wow)");
        Console.WriteLine("  --lua-exec-addr <hex|dec>     Lua execute address (default: 0x00819210)");
        Console.WriteLine("  --hardware-flag-addr <hex|dec> Hardware flag address (default: 0x00B499A4)");
        Console.WriteLine("  --source <label>              Source label for Lua execute (default: TalosForge)");
        Console.WriteLine("  --no-hardware-flag            Do not set hardware event flag");
        Console.WriteLine("  --dry-run                     Skip process execution and return success ACK");
    }

    private sealed record CliOptions
    {
        public string WowProcessName { get; init; } = "Wow";
        public uint LuaExecuteAddress { get; init; } = 0x00819210;
        public uint HardwareEventFlagAddress { get; init; } = 0x00B499A4;
        public string SourceLabel { get; init; } = "TalosForge";
        public bool UseHardwareEventFlag { get; init; } = true;
        public bool DryRun { get; init; }
        public bool ShowHelp { get; init; }
        public string? Verb { get; init; }
        public IReadOnlyList<string> VerbArguments { get; init; } = Array.Empty<string>();
    }
}
