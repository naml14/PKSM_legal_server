// See https://aka.ms/new-console-template for more information

using System.CommandLine;
using System.Text.Json;
using coreconsole.handlers;
using coreconsole.utils;
using PKHeX.Core;
using Sentry;
using Version = coreconsole.Models.Version;

namespace coreconsole;

public static class MainClass
{
    public static void Main(string[] args)
    {
        if (!Helpers.LoadEnv()) Environment.Exit((int)enums.ExitCode.EnvNotConfigured);

        var exitCode = (int)enums.ExitCode.Success;
        using (SentrySdk.Init(o => { o.Dsn = Environment.GetEnvironmentVariable("SENTRY_DSN"); }))
        {
            var pokemonArg = new Argument<string>(
                "pokemon",
                "The pokemon file (in base64 format)."
            );

            var generationOption = new Option<EntityContext?>(
                "--generation",
                "Generation of the file, used when the format could be 6/7, 8/8b or 9/9a. Accepts the generation names (Gen1 .. Gen9, Gen7b, Gen8a, Gen8b) and the generation numbers (1 .. 9)."
            );

            var cmd1 = new Command("summary", "Returns the summary for a given pokemon.")
            {
                pokemonArg,
                generationOption
            };
            cmd1.SetHandler(Summary.SummaryHandler, pokemonArg, generationOption);

            var cmd2 = new Command("legality", "Returns the legality status for a Pokemon, including what checks fail.")
            {
                pokemonArg,
                generationOption
            };

            cmd2.SetHandler(Legality.LegalityCheckHandler, pokemonArg, generationOption);

            // The generation is given by name (Gen1 .. Gen9, Gen7b, Gen8a, Gen8b, ...) or by number
            // (1 .. 9) so the values sent by the API layer are understood; it is translated to the
            // generation number ALM expects inside Legality.GetLegalizationGeneration.
            var legalizationGenerationOverride = new Option<EntityContext?>("--legalization-generation",
                "Forces the generation the legalized Pokemon has to belong to (may cause legalization to fail). Accepts the generation names and the generation numbers (1 .. 9).");

            var legalizationGameVersionOverride = new Option<GameVersion?>("--version",
                "Game Version to use in trying to legalize the Pokemon (may cause legalization to fail).");

            var cmd3 = new Command("legalize", "Attempts to auto legalize a pokemon and returns it if successful.")
            {
                pokemonArg,
                generationOption,
                legalizationGenerationOverride,
                legalizationGameVersionOverride
            };
            cmd3.SetHandler(Legality.LegalizeHandler, pokemonArg, generationOption,
                legalizationGenerationOverride, legalizationGameVersionOverride);

            var cmd4 = new Command("version", "Returns the version for ALM/PKHeX");
            cmd4.SetHandler(() => { Console.WriteLine(JsonSerializer.Serialize(new Version())); });

            var cli = new RootCommand("CoreConsole - a tool for interacting with PKHeX and Auto Legality via CLI.")
            {
                cmd1,
                cmd2,
                cmd3,
                cmd4
            };

            // A command line that cannot be understood must not be reported as success:
            // the API layer relies on the exit code to tell a failure apart from a result.
            var (normalized, invalid) = NormalizeArgs(args);
            if (invalid != null)
            {
                Console.Error.WriteLine(invalid);
                exitCode = (int)enums.ExitCode.InvalidCommandLine;
            }
            else
            {
                var parseResult = cli.Parse(normalized);
                if (parseResult.Errors.Count > 0)
                {
                    foreach (var error in parseResult.Errors)
                        Console.Error.WriteLine(error.Message);

                    exitCode = (int)enums.ExitCode.InvalidCommandLine;
                }
                else
                {
                    exitCode = cli.Invoke(normalized);
                }
            }
        }

        Environment.Exit(exitCode);
    }

    /// <summary>
    /// The generation options accept the names of <see cref="EntityContext"/> (Gen1 .. Gen9, Gen7b,
    /// Gen8a, Gen8b, ...) and the plain generation numbers ("1" .. "9") the API layer sends.
    /// </summary>
    /// <remarks>
    /// The numbers are rewritten into the matching name before the command line is parsed, and a
    /// value that is neither a known name nor a known number is reported as an invalid command
    /// line instead of being handed over to the parser (which would abort with an unhandled
    /// exception).
    /// </remarks>
    /// <returns>The arguments to parse, and the error of the first invalid option value, if any.</returns>
    private static (string[] Args, string? Error) NormalizeArgs(string[] args)
    {
        var result = (string[])args.Clone();

        for (var i = 0; i < result.Length; i++)
        {
            var arg = result[i];

            foreach (var option in new[] { "--generation", "--legalization-generation" })
            {
                string? raw = null;
                if (arg.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
                    raw = arg[(option.Length + 1)..];
                else if (arg.Equals(option, StringComparison.OrdinalIgnoreCase) && i + 1 < result.Length)
                    raw = result[i + 1];

                if (raw == null) continue;

                var mapped = MapGeneration(raw);
                if (mapped == null)
                    return (result, $"Cannot parse argument '{raw}' for option '{option}': expected a generation name (Gen1 .. Gen9) or a generation number (1 .. 9).");

                if (arg.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
                    result[i] = $"{option}={mapped}";
                else
                    result[i + 1] = mapped;
            }

            if (arg.StartsWith("--version=", StringComparison.OrdinalIgnoreCase))
            {
                var raw = arg["--version=".Length..];
                if (!Enum.TryParse<GameVersion>(raw, true, out _))
                    return (result, $"Cannot parse argument '{raw}' for option '--version': expected a game version name (SV, SL, US, ...).");
            }
            else if (arg.Equals("--version", StringComparison.OrdinalIgnoreCase) && i + 1 < result.Length)
            {
                var raw = result[i + 1];
                if (!Enum.TryParse<GameVersion>(raw, true, out _))
                    return (result, $"Cannot parse argument '{raw}' for option '--version': expected a game version name (SV, SL, US, ...).");
            }
        }

        return (result, null);
    }

    /// <summary>
    /// Maps a generation number ("1" .. "9") into the name of its <see cref="EntityContext"/>.
    /// A name is returned canonicalized, and anything else returns null so the command line is
    /// reported as invalid.
    /// </summary>
    private static string? MapGeneration(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 1 && trimmed[0] >= '1' && trimmed[0] <= '9') return "Gen" + trimmed;

        if (Enum.TryParse<EntityContext>(trimmed, true, out var context) && Enum.IsDefined(typeof(EntityContext), context))
            return context.ToString();

        return null;
    }
}
