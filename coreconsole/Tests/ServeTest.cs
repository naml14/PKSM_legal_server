using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using coreconsole;
using coreconsole.enums;
using coreconsole.handlers;
using coreconsole.utils;
using PKHeX.Core;

namespace Tests;

/// <summary>
/// The tests of the serve mode (T3): the protocol, the answers that have to be the same as the
/// ones of the single request mode, and the requests that cannot kill the process (S4, S9).
/// </summary>
[TestFixture]
public class ServeTest
{
    /// <summary>
    /// A Gen 7 Ivysaur that comes from Gold: it has to be legalized, so it is not legal as it is.
    /// </summary>
    /// <remarks>
    /// The file is embedded as base64 so the test does not depend on any file of the repository.
    /// </remarks>
    internal const string Ivysaur =
        "/G71LwAAJjgCAAAAv2QAPwAAAABBAQAA6FpcAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAEkAdgB5AHMAYQB1AHIAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAABBAG4AZAByAOkAcwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAGAAQBACcBAgAH" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    /// <summary>
    /// A Gen 7 Riolu that is already legal.
    /// </summary>
    /// <remarks>
    /// The file is embedded as base64 so the test does not depend on any file of the repository.
    /// </remarks>
    internal const string LegalRiolu =
        "FE7cDQAApT6/AQAAv2QAP7MAAAAnAgAAJO6etQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAFIAaQBvAGwAdQAAAAAAAAAAAAAAAAAAAAAAwQBiAMsARAAoHgoUAAAAAAAAAAAAAAAA" +
        "AAD///8/QQBuAGQAcgDpAHMAAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAARgABBAgAAAAA" +
        "AAAAAABBAG4AZAByAOkAcwAAAAAAAAAAAAAAAAAAAEYABz8AAAEAAAAaCwkAAABmAAQGABkxAAEH" +
        "AAAAAAAAAAAGAAAAFgAWAA8ACwAOAAsACwAAAAAAAAA=";

    /// <summary>
    /// A base64 that is not a Pokemon ("hello" in base64).
    /// </summary>
    private const string NotAPokemon = "aGVsbG8=";

    [SetUp]
    public void Setup()
    {
        Helpers.Init();
    }

    [Test]
    public void TheLegalityRequestAnswersTheSameTextAsTheSingleRequestMode()
    {
        // The single request mode of the CLI: the handler writes its payload to the standard
        // output, and that is what the answer of the serve mode has to carry.
        var cli = CaptureConsole(() => Legality.LegalityCheckHandler(Ivysaur, EntityContext.Gen7));

        var answers = Answers(Request("legality", Ivysaur, "Gen7"));

        Assert.That(answers, Has.Count.EqualTo(1));
        Assert.That(answers[0].ExitCode, Is.EqualTo((int)ExitCode.Success));
        Assert.That(answers[0].Output, Is.EqualTo(cli));
        Assert.That(answers[0].Output, Does.Contain("\"legal\":false"));
    }

    [Test]
    public void TwoRequestsAreServedByTheSameProcess()
    {
        var answers = Answers(
            Request("legality", Ivysaur, "Gen7"),
            Request("legality", LegalRiolu, "Gen7"));

        Assert.That(answers, Has.Count.EqualTo(2));
        Assert.That(answers[0].ExitCode, Is.EqualTo((int)ExitCode.Success));
        Assert.That(answers[1].ExitCode, Is.EqualTo((int)ExitCode.Success));
        Assert.That(answers[0].Output, Does.Contain("\"legal\":false"));
        Assert.That(answers[1].Output, Does.Contain("\"legal\":true"));
    }

    [Test]
    public void ABase64ThatIsNotAPokemonIsAnsweredWithAnErrorAndTheProcessKeepsServing()
    {
        var answers = Answers(
            Request("legality", NotAPokemon, "Gen7"),
            Request("legality", Ivysaur, "Gen7"));

        Assert.That(answers, Has.Count.EqualTo(2));
        Assert.That(answers[0].ExitCode, Is.EqualTo((int)ExitCode.Base64NotPokemon));
        Assert.That(answers[0].Output, Does.Contain("base64 is not a pokemon"));
        // The process is alive: the next request is served.
        Assert.That(answers[1].ExitCode, Is.EqualTo((int)ExitCode.Success));
        Assert.That(answers[1].Output, Does.Contain("\"legal\":false"));
    }

    [Test]
    public void ALineThatIsNotJsonIsAnsweredWithAnErrorAndTheProcessKeepsServing()
    {
        var answers = Answers(
            "this is not a request",
            Request("legality", Ivysaur, "Gen7"));

        Assert.That(answers, Has.Count.EqualTo(2));
        Assert.That(answers[0].ExitCode, Is.EqualTo((int)ExitCode.InvalidCommandLine));
        Assert.That(answers[0].Output, Does.Contain("error"));
        // The process is alive: the next request is served.
        Assert.That(answers[1].ExitCode, Is.EqualTo((int)ExitCode.Success));
    }

    [Test]
    public void ARequestWithoutTheFieldsItNeedsIsAnsweredWithAnError()
    {
        var answers = Answers(
            "{\"pokemon\":\"aGVsbG8=\"}",
            Request("legality", Ivysaur, "Gen7"));

        Assert.That(answers, Has.Count.EqualTo(2));
        Assert.That(answers[0].ExitCode, Is.EqualTo((int)ExitCode.InvalidCommandLine));
        Assert.That(answers[0].Output, Does.Contain("error"));
        Assert.That(answers[1].ExitCode, Is.EqualTo((int)ExitCode.Success));
    }

    [Test]
    public void AnUnknownCommandIsAnsweredWithAnError()
    {
        var answers = Answers(Request("somethingelse", Ivysaur, "Gen7"));

        Assert.That(answers, Has.Count.EqualTo(1));
        Assert.That(answers[0].ExitCode, Is.EqualTo((int)ExitCode.InvalidCommandLine));
        Assert.That(answers[0].Output, Does.Contain("error"));
    }

    [Test]
    public void TheLegalizeRequestAnswersTheSameTextAsTheSingleRequestMode()
    {
        // The Riolu is already legal, so the answer does not depend on anything that ALM
        // generates: it can be compared with the one of the single request mode, text by text.
        var cli = CaptureConsole(() => Legality.LegalizeHandler(LegalRiolu, EntityContext.Gen7, null, null));

        var answers = Answers(Request("legalize", LegalRiolu, "Gen7"));

        Assert.That(answers, Has.Count.EqualTo(1));
        Assert.That(answers[0].ExitCode, Is.EqualTo((int)ExitCode.Success));
        Assert.That(answers[0].Output, Is.EqualTo(cli));
        Assert.That(answers[0].Output, Does.Contain("this pokemon is already legal!"));
    }

    [Test]
    public void TheLegalizeRequestLegalizesThePokemonLikeTheSingleRequestMode()
    {
        var cli = CaptureConsole(() => Legality.LegalizeHandler(Ivysaur, EntityContext.Gen7, null, null));

        var answers = Answers(Request("legalize", Ivysaur, "Gen7"));

        Assert.That(answers, Has.Count.EqualTo(1));
        Assert.That(answers[0].ExitCode, Is.EqualTo((int)ExitCode.Success));

        // The Pokemon that ALM regenerates gets a new PID, so the Pokemon itself is not the same
        // in two legalizations: the rest of the answer has to be.
        Assert.That(WithoutPokemon(answers[0].Output), Is.EqualTo(WithoutPokemon(cli)));

        // The Pokemon that is returned is legal.
        var pokemon = Pokemon(answers[0].Output);
        Assert.That(pokemon, Is.Not.Null.And.Not.Empty);
        var legalized = Helpers.PokemonFromBase64(pokemon!, EntityContext.Gen7);
        Assert.That(legalized, Is.Not.Null);
        Assert.That(Legality.CheckLegality(legalized!).Valid, Is.True);
    }

    [Test]
    public void TheSummaryRequestAnswersTheSameTextAsTheSingleRequestMode()
    {
        var cli = CaptureConsole(() => Summary.SummaryHandler(LegalRiolu, EntityContext.Gen7));

        var answers = Answers(Request("summary", LegalRiolu, "Gen7"));

        Assert.That(answers, Has.Count.EqualTo(1));
        Assert.That(answers[0].ExitCode, Is.EqualTo((int)ExitCode.Success));
        Assert.That(answers[0].Output, Is.EqualTo(cli));
    }

    [Test]
    public void AnEmptyLineIsNotARequest()
    {
        var answers = Answers("", Request("legality", Ivysaur, "Gen7"));

        Assert.That(answers, Has.Count.EqualTo(1));
        Assert.That(answers[0].ExitCode, Is.EqualTo((int)ExitCode.Success));
    }

    [Test]
    public void TheAnswerIsOneLineOfUtf8WithoutABom()
    {
        // The standard output is the channel of the protocol, so it is written with an explicit
        // UTF-8 writer (without a BOM) and without translating the end of line.
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };

        Serve.ServeLoop(new StringReader(Request("legality", Ivysaur, "Gen7") + "\n"), writer);
        writer.Flush();

        var bytes = stream.ToArray();
        Assert.That(bytes.Take(3), Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }),
            "the answer is written without a BOM");

        var text = Encoding.UTF8.GetString(bytes);
        Assert.That(text, Does.EndWith("\n"));
        Assert.That(text.Split('\n', StringSplitOptions.RemoveEmptyEntries), Has.Length.EqualTo(1),
            "the answer of a request is one line");
    }

    /// <summary>
    /// Serves the requests and returns the answers of the protocol.
    /// </summary>
    internal static List<Answer> Answers(params string[] requests)
    {
        var writer = new StringWriter();
        Serve.ServeLoop(new StringReader(string.Join("\n", requests) + "\n"), writer);

        return writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<Answer>(line)!)
            .ToList();
    }

    /// <summary>
    /// A request of the protocol, as a line of JSON.
    /// </summary>
    internal static string Request(string command, string pokemon, string? generation = null,
        string? legalizationGeneration = null, string? version = null)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = command,
            ["pokemon"] = pokemon,
            ["generation"] = generation,
            ["legalizationGeneration"] = legalizationGeneration,
            ["version"] = version,
        });
    }

    /// <summary>
    /// Runs the code and returns what it wrote to the console.
    /// </summary>
    private static string CaptureConsole(Action action)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }

    /// <summary>
    /// The answer of the protocol without the Pokemon it carries.
    /// </summary>
    /// <remarks>
    /// The Pokemon that ALM regenerates is not the same in two legalizations (it gets a new PID),
    /// so it cannot be compared when the answer is compared.
    /// </remarks>
    private static string WithoutPokemon(string output)
    {
        using var document = JsonDocument.Parse(output);
        var fields = document.RootElement.EnumerateObject()
            .Where(field => field.Name != "pokemon")
            .ToDictionary(field => field.Name, field => field.Value.GetRawText());

        return JsonSerializer.Serialize(fields);
    }

    /// <summary>
    /// The Pokemon of an answer.
    /// </summary>
    private static string? Pokemon(string output)
    {
        using var document = JsonDocument.Parse(output);
        return document.RootElement.TryGetProperty("pokemon", out var pokemon)
            ? pokemon.GetString()
            : null;
    }

    /// <summary>
    /// An answer of the protocol.
    /// </summary>
    internal sealed class Answer
    {
        [JsonPropertyName("exitCode")] public int ExitCode { get; set; }
        [JsonPropertyName("output")] public string Output { get; set; } = "";
    }
}
