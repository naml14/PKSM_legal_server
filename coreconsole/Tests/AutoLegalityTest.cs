using System.Text.Json;
using coreconsole.handlers;
using coreconsole.Models;
using coreconsole.utils;
using PKHeX.Core;

namespace Tests;

[TestFixture]
public class AutoLegalityTest
{
    [SetUp]
    public void Setup()
    {
        Helpers.Init();
    }

    [Test]
    public void CanAutoLegalizePokemon()
    {
        // A shiny Aipom (Gen 9) whose PID does not match its EC and that has no HOME transfer
        // tracker: illegal for the current ALM, and legalizable by it.
        //
        // The file is embedded as base64 so the test does not depend on any file of the repository.
        // The previous fixture (a Gen 5 Basculin) is legal for ALM 26.10.18, so it no longer
        // exercises the auto-legalization path.
        var pkmn = CreateIllegalGen9Pokemon();

        // Check the legality first
        var legalityReport = Legality.CheckLegality(pkmn!);
        Assert.That(legalityReport.Valid, Is.False);

        // Now try to auto legalize
        var pokemon = Legality.AutoLegalize(pkmn!);
        Assert.That(pokemon, Is.Not.Null);
    }

    [Test]
    public void TheLegalizationReturnsThePokemonAndTheListOfChanges()
    {
        var pkmn = CreateIllegalGen9Pokemon();

        var output = CaptureConsole(() =>
            Legality.LegalizeHandler(ToBase64(pkmn), EntityContext.Gen9, null, GameVersion.SV));

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;

        Assert.That(root.GetProperty("legal").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("pokemon").GetString(), Is.Not.Null.And.Not.Empty);
        Assert.That(root.TryGetProperty("changes", out _), Is.True, "the changes of the legalization are reported");
    }

    [Test]
    public void TheChangesAreEmptyWhenNothingIsTouched()
    {
        var pkmn = CreateIllegalGen9Pokemon();

        var result = new AutoLegalizationResult(new LegalityAnalysis(pkmn), null, null, new List<string>());
        var json = JsonSerializer.Serialize(result);

        using var document = JsonDocument.Parse(json);
        Assert.That(document.RootElement.GetProperty("changes").GetArrayLength(), Is.EqualTo(0));
    }

    [Test]
    public void TheResponseKeepsTheFieldsTheApiLayerUses()
    {
        var pkmn = CreateIllegalGen9Pokemon();

        var result = new AutoLegalizationResult(new LegalityAnalysis(pkmn), null, null, new List<string> { "a change" });
        var json = JsonSerializer.Serialize(result);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.That(root.TryGetProperty("legal", out _), Is.True);
        Assert.That(root.TryGetProperty("report", out _), Is.True);
        Assert.That(root.TryGetProperty("pokemon", out _), Is.True);
        Assert.That(root.GetProperty("changes").EnumerateArray().Select(c => c.GetString()),
            Is.EqualTo(new[] { "a change" }));
    }

    [Test]
    public void AnAlreadyLegalPokemonIsNotLegalizedAgain()
    {
        var pkmn = CreateIllegalGen9Pokemon();
        var legalized = Legality.AutoLegalize(pkmn);
        Assert.That(legalized, Is.Not.Null);

        var output = CaptureConsole(() =>
            Legality.LegalizeHandler(ToBase64(legalized!), EntityContext.Gen9, null, null));

        Assert.That(output, Does.Contain("this pokemon is already legal!"));
    }

    [Test]
    public void TheChangesDoNotMentionTheValuesThatWereRestored()
    {
        // The Mew was received as a mystery gift with a met date of the 10th of April of 2023: the
        // met date is kept and it is not a change of the pokemon that is returned.
        var mew = CreateMew();

        var output = CaptureConsole(() =>
            Legality.LegalizeHandler(ToBase64(mew), EntityContext.Gen7, null, null));

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;

        Assert.That(root.GetProperty("legal").GetBoolean(), Is.True);
        var pokemon = root.GetProperty("pokemon").GetString();
        Assert.That(pokemon, Is.Not.Null.And.Not.Empty);

        var changes = root.GetProperty("changes").EnumerateArray().Select(c => c.GetString()!).ToArray();
        Assert.That(changes.Any(c => c.Contains("met date")), Is.False,
            "a met date that was kept is not a change");

        // The pokemon that is returned is legal and it still has the met date it was received with.
        var legalized = Helpers.PokemonFromBase64(pokemon!, EntityContext.Gen7);
        Assert.That(legalized, Is.Not.Null);
        Assert.That(legalized!.MetDate, Is.EqualTo(new DateOnly(2023, 4, 10)));
        Assert.That(Legality.CheckLegality(legalized).Valid, Is.True);
    }

    /// <summary>
    /// The criterion of S11: a Pokemon that arrives with several reasons is legalized and the
    /// Pokemon that is returned does not have any reason left.
    /// </summary>
    [Test]
    public void TheLegalizationOfAPokemonWithManyReasonsLeavesNoReasonBehind()
    {
        // The Ivysaur comes from Gold inside a .pk7: it arrives with seven reasons.
        var pkmn = CreateIvysaur();
        Assert.That(Legality.InvalidReasons(pkmn), Has.Count.EqualTo(7));

        var output = CaptureConsole(() =>
            Legality.LegalizeHandler(ToBase64(pkmn), EntityContext.Gen7, null, null));

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;

        Assert.That(root.GetProperty("legal").GetBoolean(), Is.True);
        var report = root.GetProperty("report").EnumerateArray().Select(r => r.GetString()).ToArray();
        Assert.That(report, Is.EqualTo(new[] { "Legal!" }));
        Assert.That(root.GetProperty("pokemon").GetString(), Is.Not.Null.And.Not.Empty);
        Assert.That(root.GetProperty("changes").GetArrayLength(), Is.Not.EqualTo(0));

        // The Pokemon that is returned passes the check.
        var legalized = Helpers.PokemonFromBase64(root.GetProperty("pokemon").GetString()!, EntityContext.Gen7);
        Assert.That(legalized, Is.Not.Null);
        Assert.That(Legality.CheckLegality(legalized!).Valid, Is.True);
    }

    /// <summary>
    /// The Gen 7 Ivysaur used by the tests of S11: it comes from Gold (its version does not
    /// belong to Gen 7), it has no met date, its first move has no PP left, it has no handling
    /// trainer and no SID.
    /// </summary>
    /// <remarks>
    /// The file is embedded as base64 so the test does not depend on any file of the repository.
    /// </remarks>
    private static PK7 CreateIvysaur()
    {
        const string base64 =
            "/G71LwAAJjgCAAAAv2QAPwAAAABBAQAA6FpcAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEkAdgB5AHMAYQB1" +
            "AHIAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABBAG4AZAByAOkAcwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAGAAQBACcB" +
            "AgAHAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

        var pkmn = Helpers.PokemonFromBase64(base64, EntityContext.Gen7);
        Assert.That(pkmn, Is.Not.Null);
        return pkmn as PK7 ?? throw new InvalidOperationException("the fixture is not a Gen 7 Pokemon");
    }

    /// <summary>
    /// A Gen 7 Mew that was received as a mystery gift, with a valid met date of the 10th of April
    /// of 2023.
    /// </summary>
    /// <remarks>
    /// The file is embedded as base64 so the test does not depend on any file of the repository.
    /// </remarks>
    private static PK7 CreateMew()
    {
        const string base64 =
            "CBBBEAAA7qeXACYDbo4+DnDKAQAcAQAAB/PpkxQFAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAE0AZQB3AAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAXgBwAHYAkAAKFAoKAAAAAF4AcAB2AJAAAAA/Ifw/QQBuAGQAcgDpAHMAAAAAAAAAAAAAAAAAAAAAAQAA" +
            "AAAAAAAAAAAAAAAAZAAAAAAAAAAAAAAAAAAS/xD/NP8o/wYmAAAAAAAAAAAAAAAAAAAAAGQAAAAAAAAAAAAXBAoAAABDnBAyACAx" +
            "CwEHAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

        var pkmn = Helpers.PokemonFromBase64(base64, EntityContext.Gen7);
        Assert.That(pkmn, Is.Not.Null);
        return pkmn as PK7 ?? throw new InvalidOperationException("the fixture is not a Gen 7 Pokemon");
    }

    /// <summary>
    /// The Gen 9 Pokemon used by the tests: an Aipom that was transferred from a Gen 3 game, so it
    /// has no HOME transfer tracker and cannot be legal.
    /// </summary>
    /// <remarks>
    /// The file is embedded as base64 so the test does not depend on any file of the repository.
    /// </remarks>
    private static PK9 CreateIllegalGen9Pokemon()
    {
        const string base64 =
            "09+WLAAARYy+AAAAwDqPSWQAAABcAAQAAAAAANPflqwBAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAALL8sAAAAAAAAAAAAAAAAAEEAaQBwAG8AbQAAAAAAAAAAAAAAAAAAAAAACgAnABwAAAAjHg8AAAAAAAAAAAAAAAAAFAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAUABLAEgAZQBYAAAAAAAAAAAAAAAAAAAAAAAAAAEAAABGAQQHAAAEAAAAAAD/AgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQAEsASABlAFgAAAAAAAAAAAAAAAAAAAAAAEYAAAAAAAAAAAAaCwYFAAAxdQQFAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAUAFAANAAkADQAJAAoAAAA=";

        var pkmn = Helpers.PokemonFromBase64(base64, EntityContext.Gen9);
        Assert.That(pkmn, Is.Not.Null);
        return pkmn as PK9 ?? throw new InvalidOperationException("the fixture is not a Gen 9 Pokemon");
    }

    /// <summary>
    /// A Pokemon of the synthetic family the user sent: level 1, an encounter of a date that
    /// cannot exist, an invalid move and an ability that does not belong to the species.
    /// </summary>
    /// <remarks>
    /// It is the shape of the Pokemon that could not be legalized before: the species has no
    /// encounter at level 1 in the generation of the file, so the engine has to look for one at
    /// another level (S12).
    /// </remarks>
    private static PK7 CreateSyntheticFamilyPokemon(ushort species, GameVersion version)
    {
        var pk = new PK7();
        pk.Species = species;
        pk.CurrentLevel = 1;
        pk.Version = version;
        pk.Nickname = "PKSM";
        pk.OriginalTrainerName = "PKSM";
        pk.Language = 7;
        pk.MetLocation = 6;
        pk.MetYear = 0;
        pk.MetMonth = 1;
        pk.MetDay = 1;
        pk.MetLevel = 1;
        pk.Ball = (byte)Ball.Poke;
        pk.SetMoves(new ushort[] { 1, 0, 0, 0 });
        pk.SetRelearnMoves(new ushort[] { 0, 0, 0, 0 });
        pk.Move1_PP = 0;
        pk.Ability = 0;
        pk.AbilityNumber = 4;
        pk.CurrentFriendship = 0;
        pk.OriginalTrainerFriendship = 0;
        return pk;
    }

    [Test]
    public void APokemonWithoutEncounterAtItsLevelIsLegalizedAtTheLevelOfTheEncounter()
    {
        // A Pikachu (Gen 7) of level 1: the Pikachu has no encounter at level 1 in Alola (it is
        // a gift or an egg there), so the engine has to look for the encounter at another level.
        var pkmn = CreateSyntheticFamilyPokemon(25, GameVersion.SN);

        var result = Legality.AutoLegalizeWithFallback(pkmn, EntityContext.Gen7, GameVersion.USUM,
            out var failure, out var changes);

        Assert.That(failure, Is.Null);
        Assert.That(result, Is.Not.Null);
        Assert.That(Legality.CheckLegality(result!).Valid, Is.True, "the legalized Pokemon is legal");
        Assert.That(result!.CurrentLevel, Is.Not.EqualTo(1), "the level of the encounter is used");
        Assert.That(changes.Any(c => c.StartsWith("level 1 -> ")), Is.True,
            "the change of level is reported");
    }

    [Test]
    public void ASpeciesWithOnlyAGiftEncounterIsLegalizedFromTheGift()
    {
        // The Nidoking does not appear in the wild in Alola: the only encounter it has in Gen 7
        // is a gift, so the engine has to use it.
        var pkmn = CreateSyntheticFamilyPokemon(34, GameVersion.SN);

        var result = Legality.AutoLegalizeWithFallback(pkmn, EntityContext.Gen7, GameVersion.USUM,
            out var failure, out var changes);

        Assert.That(failure, Is.Null);
        Assert.That(result, Is.Not.Null);
        Assert.That(Legality.CheckLegality(result!).Valid, Is.True);
        Assert.That(changes.Any(c => c.StartsWith("level 1 -> ")), Is.True);

        // The gift is the encounter the species has in the generation of the file.
        Assert.That(Legality.EncounterKinds(pkmn, 34, EntityContext.Gen7, GameVersion.UM), Does.Contain("WC7"),
            "the Nidoking has a gift encounter in Gen 7");
    }

    [Test]
    public void APokemonThatCannotBeLegalizedSaysWhy()
    {
        // A species that does not exist in the games of the generation of the file: there is no
        // encounter of any kind for it, so the answer has to say so and say where it does exist.
        var pkmn = CreateSyntheticFamilyPokemon(866, GameVersion.SN);

        var result = Legality.AutoLegalizeWithFallback(pkmn, EntityContext.Gen7, GameVersion.USUM,
            out var failure, out _);

        Assert.That(result, Is.Null);
        Assert.That(failure, Is.Not.Null);
        Assert.That(failure, Does.Contain("impossible to legalize"));
        Assert.That(failure, Does.Contain("866"), "the species of the Pokemon is named");
        Assert.That(failure, Does.Contain("Gen7"), "the generation of the file is named");
    }

    #region S13: the game of origin does not matter

    /// <summary>
    /// The species of the Unown has no encounter in the games of Alola: the engine has to look for
    /// the encounter in the other generations, and the change has to say in which generation the
    /// Pokemon ended up.
    /// </summary>
    [Test]
    public void TheChangesSayInWhichGenerationThePokemonEndedUp()
    {
        var pkmn = CreateUnown();

        var result = Legality.AutoLegalizeWithFallback(pkmn, EntityContext.Gen7, GameVersion.ORAS,
            out var failure, out var changes, out var note);

        Assert.That(failure, Is.Null);
        Assert.That(result, Is.Not.Null);
        Assert.That(Legality.CheckLegality(result!).Valid, Is.True);

        // The Unown does not appear in the wild in Alola: the encounter is of another generation.
        Assert.That(result!.Version.Context, Is.Not.EqualTo(EntityContext.Gen7),
            "the encounter of the Unown is not of the generation of the file");
        Assert.That(changes.Any(c => c.StartsWith("generation Gen7 -> ")), Is.True,
            $"the change of generation is reported: [{string.Join(", ", changes)}]");
        Assert.That(note, Is.Not.Null.And.Contains("generation Gen6"),
            "the response says in which generation the Pokemon ended up");
    }

    /// <summary>
    /// A Pokemon whose species has no encounter at all in the generation of the file is legalized
    /// from an encounter of another generation: the Wyrdeer does not exist in the games of Gen 9.
    /// </summary>
    [Test]
    public void APokemonWithoutEncounterInItsGenerationIsLegalizedInAnotherOne()
    {
        var pkmn = CreateSyntheticGen9Pokemon(865); // Wyrdeer: it only lives in Gen 8.

        var result = Legality.AutoLegalizeWithFallback(pkmn, EntityContext.Gen9, GameVersion.SV,
            out var failure, out var changes, out var note);

        Assert.That(failure, Is.Null);
        Assert.That(result, Is.Not.Null);
        Assert.That(Legality.CheckLegality(result!).Valid, Is.True, "the legalized Pokemon is legal");
        Assert.That(Legality.InvalidReasons(result!), Is.Empty);

        Assert.That(result!.Version.Context, Is.EqualTo(EntityContext.Gen8),
            "the encounter is the one of Gen 8");
        Assert.That(changes.Any(c => c == "generation Gen9 -> Gen8"), Is.True,
            $"the change of generation is reported: [{string.Join(", ", changes)}]");
        Assert.That(note, Does.Contain("Gen8"), "the response says in which generation it ended up");
    }

    /// <summary>
    /// The response of the engine says in which generation the Pokemon ended up when it had to
    /// be legalized into another one.
    /// </summary>
    [Test]
    public void TheResponseSaysInWhichGenerationThePokemonEndedUp()
    {
        var pkmn = CreateUnown();

        var output = CaptureConsole(() =>
            Legality.LegalizeHandler(ToBase64(pkmn), EntityContext.Gen7, null, GameVersion.ORAS));

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;

        Assert.That(root.GetProperty("legal").GetBoolean(), Is.True);
        var report = root.GetProperty("report").EnumerateArray().Select(r => r.GetString()!).ToArray();
        Assert.That(report.Any(r => r.Contains("legalized into the generation Gen6")), Is.True,
            $"the report says in which generation the Pokemon ended up: [{string.Join(", ", report)}]");

        var changes = root.GetProperty("changes").EnumerateArray().Select(c => c.GetString()!).ToArray();
        Assert.That(changes.Any(c => c.StartsWith("generation Gen7 -> ")), Is.True);
    }

    /// <summary>
    /// The fallback looks at the other generations from the newest to the oldest, and the
    /// generation of the file is not looked at twice.
    /// </summary>
    [Test]
    public void TheFallbackLooksAtTheOtherGenerationsFromTheNewestToTheOldest()
    {
        var generations = Legality.FallbackGenerations(EntityContext.Gen7).ToList();

        Assert.That(generations, Is.EqualTo(new[]
        {
            EntityContext.Gen9, EntityContext.Gen8, EntityContext.Gen6, EntityContext.Gen5,
            EntityContext.Gen4, EntityContext.Gen3, EntityContext.Gen2, EntityContext.Gen1
        }));
        Assert.That(generations, Does.Not.Contain(EntityContext.Gen7));
    }

    /// <summary>
    /// The search has to fit in the timeout of the API (30 seconds), so it has a budget of time
    /// and a number of attempts.
    /// </summary>
    [Test]
    public void TheSearchHasABudgetThatFitsInTheTimeoutOfTheApi()
    {
        Assert.That(Legality.SearchTimeBudget, Is.LessThan(TimeSpan.FromSeconds(30)));
        Assert.That(Legality.FallbackTimeBudget, Is.LessThan(TimeSpan.FromSeconds(30)));
        Assert.That(Legality.MaxSearchAttempts, Is.GreaterThan(0));
        Assert.That(Legality.MaxFallbackAttempts, Is.GreaterThan(0));

        // The two budgets are apart, so the whole search fits in the timeout of the API.
        Assert.That(Legality.SearchTimeBudget + Legality.FallbackTimeBudget,
            Is.LessThan(TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// The word "impossible" is reserved for the species that have no encounter of any kind in
    /// any generation: the engine looked everywhere before saying it.
    /// </summary>
    [Test]
    public void TheImpossibleMessageIsOnlyUsedWhenTheSpeciesHasNoEncounterAtAll()
    {
        // The Meltan has no encounter of any kind in any generation (PKHeX does not know the gift
        // of the professor of Galar).
        var meltan = CreateSyntheticFamilyPokemon(866, GameVersion.SN);
        Assert.That(Legality.GenerationsWithEncounter(meltan), Is.Empty);

        var result = Legality.AutoLegalizeWithFallback(meltan, EntityContext.Gen7, GameVersion.USUM,
            out var failure, out _);

        Assert.That(result, Is.Null);
        Assert.That(failure, Does.Contain("impossible to legalize"));
        Assert.That(failure, Does.Contain("ANY generation"),
            "the message says that there is no encounter in any generation");
    }

    /// <summary>
    /// The Unown of the tests of S13: it is a Gen 7 Pokemon whose species has no encounter in
    /// the games of Alola.
    /// </summary>
    /// <remarks>
    /// The file is embedded as base64 so the test does not depend on any file of the repository.
    /// </remarks>
    private static PK7 CreateUnown()
    {
        const string base64 =
            "/G71LwAAfJHJAAAAOTAx1AAAAAAaAQAAj41EkgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFUAbgBvAHcAbgAA" +
            "AAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD///8/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQAEsAUwBNAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAGAAQBAB4B" +
            "AgAHAAAAAAAAAAABAAAADAAMAAYABgAGAAYABgAAAAAAAAA=";

        var pkmn = Helpers.PokemonFromBase64(base64, EntityContext.Gen7);
        Assert.That(pkmn, Is.Not.Null);
        return pkmn as PK7 ?? throw new InvalidOperationException("the fixture is not a Gen 7 Pokemon");
    }

    /// <summary>
    /// A Gen 9 Pokemon of the shape the user sent: level 1, an encounter of a date that cannot
    /// exist, an invalid move and an ability that does not belong to the species.
    /// </summary>
    private static PK9 CreateSyntheticGen9Pokemon(ushort species)
    {
        var pk = new PK9
        {
            Species = species,
            CurrentLevel = 1,
            Version = GameVersion.VL,
            Nickname = "PKSM",
            OriginalTrainerName = "PKSM",
            Language = 7,
            MetLocation = 6,
            MetYear = 0,
            MetMonth = 1,
            MetDay = 1,
            MetLevel = 1,
            Ball = (byte)Ball.Poke
        };
        pk.SetMoves(new ushort[] { 1, 0, 0, 0 });
        pk.SetRelearnMoves(new ushort[] { 0, 0, 0, 0 });
        pk.Move1_PP = 0;
        pk.Ability = 0;
        pk.AbilityNumber = 4;
        pk.CurrentFriendship = 0;
        pk.OriginalTrainerFriendship = 0;
        return pk;
    }

    #endregion

    /// <summary>
    /// The base64 of a pokemon, the same way the console returns it.
    /// </summary>
    private static string ToBase64(PKM pkmn)
    {
        var useParty = pkmn.SIZE_PARTY > pkmn.SIZE_STORED;
        var data = new byte[useParty ? pkmn.SIZE_PARTY : pkmn.SIZE_STORED];
        if (useParty)
            pkmn.WriteDecryptedDataParty(data);
        else
            pkmn.WriteDecryptedDataStored(data);

        return Convert.ToBase64String(data);
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
}
