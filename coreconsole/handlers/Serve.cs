using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using coreconsole.utils;
using PKHeX.Core;
using Sentry;

namespace coreconsole.handlers;

public static class Serve
{
    /// <summary>
    /// The options of the answers of the protocol: the text of the answer is written as it is,
    /// with the end of line escaped.
    /// </summary>
    private static readonly JsonSerializerOptions ProtocolOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Serves the requests of the standard input until it ends.
    /// </summary>
    public static void Run()
    {
        // The standard output is the channel of the protocol, so it is written with an explicit
        // UTF-8 writer (without a BOM) and without translating the end of line.
        var stdout = Console.OpenStandardOutput();
        var output = new StreamWriter(stdout, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

        ServeLoop(input, output);

        output.Flush();
    }

    /// <summary>
    /// Serves the requests of an input until it ends, writing one answer per request.
    /// </summary>
    /// <param name="input">Where the requests are read from.</param>
    /// <param name="output">Where the answers are written to.</param>
    /// <remarks>
    /// The engine is loaded once, before the first request, and it stays loaded between the
    /// requests (S3). A request that cannot be served is answered with its error and the loop
    /// goes on with the next one: the process is never killed by a request (S9).
    /// </remarks>
    public static void ServeLoop(TextReader input, TextWriter output)
    {
        var mode = Helpers.Mode;
        Helpers.Mode = Helpers.EngineMode.Serve;
        try
        {
            Helpers.Init();

            while (input.ReadLine() is { } line)
            {
                // An empty line is not a request.
                if (string.IsNullOrWhiteSpace(line)) continue;

                output.Write(Answer(line));
                output.Write('\n');
                output.Flush();
            }
        }
        finally
        {
            Helpers.Mode = mode;
        }
    }

    /// <summary>
    /// Serves one request and returns the answer, as a line of JSON.
    /// </summary>
    private static string Answer(string line)
    {
        try
        {
            var request = Parse(line, out var error);
            if (request == null) return Error(enums.ExitCode.InvalidCommandLine, error);

            return ServeRequest(request);
        }
        catch (Helpers.InvalidPokemonException e)
        {
            // The Pokemon could not be read (or the target of the legalization could not be
            // worked out): the answer carries the exit code of the failure and the process stays
            // alive (S9).
            return Error(e.ExitCode, e.Error);
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            return Error(enums.ExitCode.UnknownErrorDuringBase64ToPokemon,
                "{\"error\": \"unknown error while serving the request\"}");
        }
    }

    /// <summary>
    /// Serves a request that was parsed.
    /// </summary>
    private static string ServeRequest(Request request)
    {
        // The handlers write their payload to the standard output, which is the channel of the
        // protocol here, so it is captured while the request is served and put in the answer.
        var captured = new StringWriter();
        var original = Console.Out;
        Console.SetOut(captured);
        try
        {
            switch (request.Command)
            {
                case "legality":
                    Legality.LegalityCheckHandler(request.Pokemon, request.Generation);
                    break;
                case "legalize":
                    Legality.LegalizeHandler(request.Pokemon, request.Generation,
                        request.LegalizationGeneration, request.Version);
                    break;
                case "summary":
                    Summary.SummaryHandler(request.Pokemon, request.Generation);
                    break;
            }
        }
        finally
        {
            Console.SetOut(original);
        }

        return Response((int)enums.ExitCode.Success, captured.ToString());
    }

    /// <summary>
    /// Parses a request of the protocol.
    /// </summary>
    /// <param name="line">The line of the request.</param>
    /// <param name="error">The reason why the line is not a valid request.</param>
    /// <returns>The request, or null when the line is not a valid request.</returns>
    private static Request? Parse(string line, out string error)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException e)
        {
            error = ErrorObject($"the request is not a valid JSON: {e.Message}");
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = ErrorObject("the request has to be a JSON object");
                return null;
            }

            if (!TryGetString(root, "command", required: true, out var command, out var reason) ||
                !TryGetString(root, "pokemon", required: true, out var pokemon, out reason))
            {
                error = ErrorObject(reason!);
                return null;
            }

            if (!TryGetString(root, "generation", required: false, out var generation, out reason) ||
                !TryGetString(root, "legalizationGeneration", required: false, out var legalizationGeneration, out reason) ||
                !TryGetString(root, "version", required: false, out var version, out reason))
            {
                error = ErrorObject(reason!);
                return null;
            }

            var context = ParseContext(generation);
            if (context == null && generation != null)
            {
                error = ErrorObject($"cannot parse '{generation}' as a generation");
                return null;
            }

            var legalizationContext = ParseContext(legalizationGeneration);
            if (legalizationContext == null && legalizationGeneration != null)
            {
                error = ErrorObject($"cannot parse '{legalizationGeneration}' as a generation");
                return null;
            }

            GameVersion? gameVersion = null;
            if (version != null)
            {
                if (!Enum.TryParse<GameVersion>(version, true, out var parsed) ||
                    !Enum.IsDefined(typeof(GameVersion), parsed))
                {
                    error = ErrorObject($"cannot parse '{version}' as a game version");
                    return null;
                }

                gameVersion = parsed;
            }

            if (command is not ("legality" or "legalize" or "summary"))
            {
                error = ErrorObject($"unknown command '{command}'");
                return null;
            }

            error = "";
            return new Request(command!, pokemon!, context, legalizationContext, gameVersion);
        }
    }

    /// <summary>
    /// Reads a field of the request.
    /// </summary>
    /// <param name="root">The object of the request.</param>
    /// <param name="name">The name of the field.</param>
    /// <param name="required">Whether the field has to be there.</param>
    /// <param name="value">The value of the field, when it is a string (or null).</param>
    /// <param name="error">The reason why the field is not valid, when it is not.</param>
    /// <returns>Whether the field is valid.</returns>
    private static bool TryGetString(JsonElement root, string name, bool required, out string? value,
        out string? error)
    {
        value = null;
        error = null;

        if (!root.TryGetProperty(name, out var element))
        {
            if (required) error = $"the field '{name}' is missing";
            return !required;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                if (required)
                {
                    error = $"the field '{name}' cannot be null";
                    return false;
                }

                return true;
            case JsonValueKind.String:
                value = element.GetString();
                return true;
            default:
                error = $"the field '{name}' has to be a string";
                return false;
        }
    }

    /// <summary>
    /// Translates the name of a generation into its context.
    /// </summary>
    /// <param name="value">The name (or the number) of the generation, or null.</param>
    /// <returns>The context of the generation, or null when it cannot be translated.</returns>
    private static EntityContext? ParseContext(string? value)
    {
        if (value == null) return null;

        var name = MainClass.MapGeneration(value);
        if (name == null) return null;

        return Enum.Parse<EntityContext>(name);
    }

    /// <summary>
    /// The answer of a request: the exit code the CLI would return and the text it would write.
    /// </summary>
    private static string Response(int exitCode, string output)
    {
        return JsonSerializer.Serialize(new ServeResponse { ExitCode = exitCode, Output = output },
            ProtocolOptions);
    }

    /// <summary>
    /// The answer of a request that could not be served.
    /// </summary>
    private static string Error(enums.ExitCode exitCode, string error)
    {
        return Response((int)exitCode, error);
    }

    /// <summary>
    /// An error object of the protocol.
    /// </summary>
    private static string ErrorObject(string message)
    {
        return JsonSerializer.Serialize(new ErrorPayload { Error = message }, ProtocolOptions);
    }

    /// <summary>
    /// A request of the protocol.
    /// </summary>
    private sealed class Request
    {
        public Request(string command, string pokemon, EntityContext? generation,
            EntityContext? legalizationGeneration, GameVersion? version)
        {
            Command = command;
            Pokemon = pokemon;
            Generation = generation;
            LegalizationGeneration = legalizationGeneration;
            Version = version;
        }

        public string Command { get; }
        public string Pokemon { get; }
        public EntityContext? Generation { get; }
        public EntityContext? LegalizationGeneration { get; }
        public GameVersion? Version { get; }
    }

    /// <summary>
    /// The answer of the protocol.
    /// </summary>
    private sealed class ServeResponse
    {
        [JsonPropertyName("exitCode")] public int ExitCode { get; set; }
        [JsonPropertyName("output")] public string Output { get; set; } = "";
    }

    /// <summary>
    /// The error of the protocol.
    /// </summary>
    private sealed class ErrorPayload
    {
        [JsonPropertyName("error")] public string Error { get; set; } = "";
    }
}
