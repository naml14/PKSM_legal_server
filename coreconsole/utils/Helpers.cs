using System.Text.Json;
using coreconsole.Models;
using PKHeX.Core;
using PKHeX.Core.AutoMod;
using Sentry;

namespace coreconsole.utils;

public static class Helpers
{
    public static JsonSerializerOptions? SerializerOptions;

    /// <summary>
    /// How the engine is being used.
    /// </summary>
    public enum EngineMode
    {
        /// <summary>
        /// One request per process: the CLI. A failure ends the process with its exit code.
        /// </summary>
        SingleRequest,

        /// <summary>
        /// The serve loop: the process stays alive between requests, so a failure cannot end it
        /// (S9).
        /// </summary>
        Serve
    }

    /// <summary>
    /// The mode the engine is in.
    /// </summary>
    /// <remarks>
    /// The serve loop sets it while it serves the requests and restores it afterwards, so the
    /// single request mode of the CLI is not affected by it.
    /// </remarks>
    public static EngineMode Mode { get; set; } = EngineMode.SingleRequest;

    /// <summary>
    /// The failure of a request that cannot be served: the Pokemon could not be read, or the
    /// target of the legalization could not be worked out.
    /// </summary>
    /// <remarks>
    /// The error it carries is the object the engine prints, so the serve loop can answer with
    /// the same text the CLI prints (S9).
    /// </remarks>
    public class InvalidPokemonException : Exception
    {
        public InvalidPokemonException(enums.ExitCode exitCode, string error) : base(error)
        {
            ExitCode = exitCode;
            Error = error;
        }

        /// <summary>The exit code the CLI would return for this failure.</summary>
        public enums.ExitCode ExitCode { get; }

        /// <summary>The error object (JSON) that describes the failure.</summary>
        public string Error { get; }
    }

    private static bool _stackInitialized;
    private static bool _spritesInitialized;

    /// <summary>
    /// Whether the stack of PKHeX/ALM is loaded.
    /// </summary>
    public static bool StackInitialized => _stackInitialized;

    /// <summary>
    /// Whether the sprites are loaded: only the summary needs them (S6).
    /// </summary>
    public static bool SpritesInitialized => _spritesInitialized;

    // Used for tests
    public static byte[] StringToByteArray(string hex)
    {
        return Enumerable.Range(0, hex.Length)
            .Where(x => x % 2 == 0)
            .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
            .ToArray();
    }

    /// <summary>
    /// Loads the stack of PKHeX/ALM that every mode needs.
    /// </summary>
    /// <remarks>
    /// The load is done once per process: the serve loop keeps the stack hot between requests
    /// (S3), so the callers do not have to care about how many times they ask for it.
    /// <br />
    /// The sprites are not loaded here: only the summary needs them (S6).
    /// </remarks>
    public static void Init()
    {
        if (_stackInitialized) return;

        EncounterEvent.RefreshMGDB(string.Empty);
        // ALM 26.10.18 hides the easter eggs behind a switch; the console does not want them.
        Legalizer.EnableEasterEggs = false;

        SerializerOptions = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        _stackInitialized = true;
    }

    /// <summary>
    /// Loads the sprites, which only the summary needs (S6).
    /// </summary>
    /// <remarks>
    /// The load is done once per process, like the stack of PKHeX/ALM.
    /// </remarks>
    public static void InitSprites()
    {
        if (_spritesInitialized) return;

        Sprites.Init();

        _spritesInitialized = true;
    }

    /// <summary>
    /// Ends a request that cannot be served.
    /// </summary>
    /// <param name="exitCode">The exit code the CLI returns for this failure.</param>
    /// <param name="error">The error object (JSON) that describes the failure.</param>
    /// <remarks>
    /// The CLI serves one request per process, so the failure ends the process with its exit code.
    /// The serve loop serves many requests in the same process, so there the failure is thrown and
    /// the loop answers with it, leaving the process alive (S9).
    /// </remarks>
    public static void Fail(enums.ExitCode exitCode, string error)
    {
        if (Mode == EngineMode.Serve) throw new InvalidPokemonException(exitCode, error);

        Environment.Exit((int)exitCode);
    }

    public static PKM? PokemonFromBase64(string pokemon, EntityContext context = EntityContext.None)
    {
        PKM? pkmn = null;
        var exitCode = enums.ExitCode.Success;
        string? error = null;
        Exception? failure = null;
        // Whether the failure is written to the error output: the unknown error is only reported
        // to Sentry, as it was before.
        var reported = false;

        try
        {
            var pkmnStrBytes = Convert.FromBase64String(pokemon);

            pkmn = EntityFormat.GetFromBytes(pkmnStrBytes, context);

            if (pkmn is null)
            {
                error = "{\"error\": \"base64 is not a pokemon\"}";
                exitCode = enums.ExitCode.Base64NotPokemon;
                reported = true;
            }
        }
        catch (Exception e) when (e is FormatException or ArgumentNullException)
        {
            error = "{\"error\": \"invalid base64 string provided\"}";
            exitCode = enums.ExitCode.BadBase64;
            failure = e;
            reported = true;
        }
        catch (Exception e)
        {
            error = "{\"error\": \"unknown error while reading the pokemon\"}";
            exitCode = enums.ExitCode.UnknownErrorDuringBase64ToPokemon;
            failure = e;
        }

        if (pkmn is not null) return pkmn;

        if (reported) Console.Error.WriteLine(error);
        if (failure != null) SentrySdk.CaptureException(failure);
        else SentrySdk.CaptureMessage(level: SentryLevel.Error, message: "base64 provided is not a pokemon");

        Fail(exitCode, error!);

        return null;
    }

    public static bool LoadEnv()
    {
        if (!File.Exists(".env"))
        {
            Console.Error.WriteLine(".env is missing");
            return false;
        }

        foreach (var line in File.ReadAllLines(".env"))
        {
            var parts = line.Split("=");
            if (parts.Length != 2) continue;

            Environment.SetEnvironmentVariable(parts[0], parts[1]);
        }

        return true;
    }
}
