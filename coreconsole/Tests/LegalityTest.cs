using coreconsole.handlers;
using coreconsole.Models;
using coreconsole.utils;
using PKHeX.Core;

namespace Tests;

[TestFixture]
public class LegalityTest
{
    [SetUp]
    public void Setup()
    {
        Helpers.Init();
    }

    [TestCase("Gen1", 1)]
    [TestCase("Gen2", 2)]
    [TestCase("Gen3", 3)]
    [TestCase("Gen4", 4)]
    [TestCase("Gen5", 5)]
    [TestCase("Gen6", 6)]
    [TestCase("Gen7", 7)]
    [TestCase("Gen8", 8)]
    [TestCase("Gen9", 9)]
    public void MainlineGenerationsMapToTheirGenerationNumber(string name, int expected)
    {
        Assert.That(Enum.Parse<EntityContext>(name), Is.EqualTo((EntityContext)expected));
        Assert.That(Legality.GetLegalizationGeneration(Enum.Parse<EntityContext>(name)), Is.EqualTo(expected));
    }

    [TestCase("Gen7b", 7)]
    [TestCase("Gen8a", 8)]
    [TestCase("Gen8b", 8)]
    public void SplitGenerationsMapToTheirBaseGeneration(string name, int expected)
    {
        Assert.That(Legality.GetLegalizationGeneration(Enum.Parse<EntityContext>(name)), Is.EqualTo(expected));
    }

    [TestCase("None")]
    [TestCase("SplitInvalid")]
    [TestCase("MaxInvalid")]
    public void NonGenerationContextsDoNotForceAGeneration(string name)
    {
        Assert.That(Legality.GetLegalizationGeneration(Enum.Parse<EntityContext>(name)), Is.Null);
    }

    [TestCase(EntityContext.None)]
    [TestCase(EntityContext.SplitInvalid)]
    [TestCase(EntityContext.MaxInvalid)]
    public void ContextsWithoutAGenerationAreNotUsable(EntityContext context)
    {
        Assert.That(Legality.IsUsableContext(context), Is.False);
    }

    [TestCase(EntityContext.Gen1)]
    [TestCase(EntityContext.Gen7b)]
    [TestCase(EntityContext.Gen8a)]
    [TestCase(EntityContext.Gen8b)]
    [TestCase(EntityContext.Gen9)]
    public void ContextsOfAGenerationAreUsable(EntityContext context)
    {
        Assert.That(Legality.IsUsableContext(context), Is.True);
    }

    [Test]
    public void AGen9PokemonIsLegalizedIntoGen9()
    {
        var pkmn = CreateIllegalGen9Pokemon();

        Assert.That(pkmn.Context, Is.EqualTo(EntityContext.Gen9));
        Assert.That(Legality.ResolveLegalizationContext(pkmn), Is.EqualTo(EntityContext.Gen9));
    }

    [Test]
    public void TheForcedGenerationWinsOverTheContextOfThePokemon()
    {
        var pkmn = CreateIllegalGen9Pokemon();

        Assert.That(Legality.ResolveLegalizationContext(pkmn, EntityContext.Gen7), Is.EqualTo(EntityContext.Gen7));
    }

    [Test]
    public void TheVersionOfThePokemonIsUsedWhenItBelongsToTheDestinationGeneration()    {
        var pkmn = CreateIllegalGen9Pokemon();
        pkmn.Version = GameVersion.VL;

        Assert.That(Legality.ResolveDestinationVersion(pkmn, EntityContext.Gen9, null), Is.EqualTo(GameVersion.VL));
    }

    [Test]
    public void AVersionOfAnotherGenerationIsReplacedByTheVersionOfTheDestinationGeneration()
    {
        // The Aipom was caught in FireRed: its version does not belong to Gen 9.
        var pkmn = CreateIllegalGen9Pokemon();
        Assert.That(pkmn.Version, Is.EqualTo(GameVersion.FR));

        Assert.That(Legality.ResolveDestinationVersion(pkmn, EntityContext.Gen9, null), Is.EqualTo(GameVersion.SV));
    }

    [Test]
    public void TheForcedVersionWinsOverTheVersionOfThePokemon()
    {
        var pkmn = CreateIllegalGen9Pokemon();

        Assert.That(Legality.ResolveDestinationVersion(pkmn, EntityContext.Gen9, GameVersion.VL), Is.EqualTo(GameVersion.VL));
    }

    [Test]
    public void AnIllegalGen9PokemonIsLegalizedIntoALegalOne()
    {
        var pkmn = CreateIllegalGen9Pokemon();
        Assert.That(Legality.CheckLegality(pkmn).Valid, Is.False, "the fixture has to be illegal");

        var legalized = Legality.AutoLegalize(pkmn, EntityContext.Gen9, GameVersion.SV, out var failureReason);

        Assert.That(legalized, Is.Not.Null, failureReason);
        Assert.That(legalized!.Context, Is.EqualTo(EntityContext.Gen9));
        Assert.That(Legality.CheckLegality(legalized).Valid, Is.True, failureReason);
    }

    /// <summary>
    /// Builds the Gen 9 Pokemon used by the tests: an Aipom that was transferred from a Gen 3 game,
    /// so it has no HOME transfer tracker and cannot be legal.
    /// </remarks>
    private static PK9 CreateIllegalGen9Pokemon()
    {
        const string base64 =
            "094VrAAAm4q+AAAAwDqPSWQAAAA1AAIAAAAAANPeFawBAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAALL8s" +
            "AAAAAAAAAAAAAAAAAEEAaQBwAG8AbQAAAAAAAAAAAAAAAAAAAAAACgAnABwAAAAjHg8AAAAAAAAAAAAAAAAAFAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAUABLAEgAZQBYAAAAAAAAAAAAAAAAAAAAAAAAAAEAAABGAQQHAAAEAAAAAAD/AgAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQAEsASABlAFgAAAAAAAAAAAAAAAAAAAAAAEYAAAAAAAAAAAAaCwYFAAAxdQQFAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAUAFAANAAkADQAJAAoAAAA=";

        var pkmn = Helpers.PokemonFromBase64(base64, EntityContext.Gen9);
        Assert.That(pkmn, Is.Not.Null);
        return pkmn as PK9 ?? throw new InvalidOperationException("the fixture is not a Gen 9 Pokemon");
    }

    /// <summary>
    /// A Gen 7 Pokemon with the data that keeps ALM from finding its origin encounter: it has no
    /// met date, its first move has no PP left and it has no handling trainer.
    /// </summary>
    private static PK7 CreateGen7PokemonWithoutEncounterData()
    {
        var pkmn = new PK7
        {
            Species = 1,
            CurrentLevel = 5,
            Version = GameVersion.US,
            OriginalTrainerName = "TESTER",
            OriginalTrainerGender = 0,
            Language = 2,
            TID16 = 12345,
            SID16 = 54321,
            Move1 = 1, // Pound
            Move1_PP = 0,
            MetLocation = 6,
            MetLevel = 5
        };

        // The met date is not a date that can exist.
        pkmn.MetYear = 0;
        pkmn.MetMonth = 0;
        pkmn.MetDay = 0;

        return pkmn;
    }

    #region S10: the decision tree by reason of ALM

    [TestCase("Invalid: Met Date is not a valid calendar date.", LegalityRepair.MetDate)]
    [TestCase("Invalid Move 1: Invalid Move.", LegalityRepair.Move)]
    [TestCase("Invalid: Hidden Ability mismatch for encounter type.", LegalityRepair.Ability)]
    [TestCase("Invalid: Mystery Gift cannot be received by this version.", LegalityRepair.MetLocation)]
    [TestCase("Invalid: HT Language is missing.", LegalityRepair.HandlingTrainer)]
    [TestCase("Invalid: Invalid Current handler value.", LegalityRepair.HandlingTrainer)]
    [TestCase("Invalid: Memory: Not cleared properly.", LegalityRepair.HandlingTrainer)]
    [TestCase("Invalid: Unable to match an encounter from origin game.", LegalityRepair.Origin)]
    [TestCase("Invalid: PID should be equal to EC!", LegalityRepair.None)]
    [TestCase("Invalid: PID-Nature mismatch.", LegalityRepair.None)]
    [TestCase("Invalid: Should have at least 3 IVs = 31.", LegalityRepair.None)]
    [TestCase("Invalid: Pokemon HOME Transfer Tracker is missing.", LegalityRepair.None)]
    public void EachReasonOfAlmIsClassifiedIntoItsRepair(string reason, LegalityRepair expected)
    {
        Assert.That(Legality.ClassifyReason(reason), Is.EqualTo(expected));
    }

    [TestCase("Invalid Move 1: Invalid Move.", 1)]
    [TestCase("Invalid Move 3: Invalid Move.", 3)]
    public void TheMoveOfTheReasonIsTheSlotItOccupies(string reason, int expected)
    {
        Assert.That(Legality.InvalidMoveIndex(reason), Is.EqualTo(expected));
    }

    [Test]
    public void AReasonThatIsNotAboutAMoveHasNoMoveSlot()
    {
        Assert.That(Legality.InvalidMoveIndex("Invalid: Met Date is not a valid calendar date."), Is.Null);
    }

    [Test]
    public void AnImpossibleMetDateIsRepairedWithAPlausibleDateOfTheGame()
    {
        var pkmn = CreateGen7PokemonWithoutEncounterData();
        Assert.That(pkmn.MetDate, Is.Null);

        var changes = new List<string>();
        var repaired = Legality.ApplyDirectedRepairs(pkmn, EntityContext.Gen7, GameVersion.USUM,
            new[] { "Invalid: Met Date is not a valid calendar date." }, changes);

        Assert.That(repaired.MetDate, Is.EqualTo(new DateOnly(2017, 11, 17)));
        Assert.That(changes, Does.Contain("met date (none) -> 17/11/2017"));
    }

    [Test]
    public void TheMetDateOfEachGameIsTheOneOfItsRelease()
    {
        Assert.That(Legality.PlausibleMetDate(EntityContext.Gen7, GameVersion.USUM), Is.EqualTo(new DateOnly(2017, 11, 17)));
        Assert.That(Legality.PlausibleMetDate(EntityContext.Gen7, GameVersion.SM), Is.EqualTo(new DateOnly(2016, 11, 23)));
        Assert.That(Legality.PlausibleMetDate(EntityContext.Gen7, GameVersion.ORAS), Is.EqualTo(new DateOnly(2016, 10, 21)));
        Assert.That(Legality.PlausibleMetDate(EntityContext.Gen6, GameVersion.ORAS), Is.EqualTo(new DateOnly(2014, 11, 21)));
        Assert.That(Legality.PlausibleMetDate(EntityContext.Gen5, GameVersion.B2W2), Is.EqualTo(new DateOnly(2010, 3, 6)));
        Assert.That(Legality.PlausibleMetDate(EntityContext.Gen4, GameVersion.HGSS), Is.EqualTo(new DateOnly(2009, 9, 12)));
        Assert.That(Legality.PlausibleMetDate(EntityContext.Gen8, GameVersion.SWSH), Is.EqualTo(new DateOnly(2020, 3, 19)));
        Assert.That(Legality.PlausibleMetDate(EntityContext.Gen9, GameVersion.SV), Is.EqualTo(new DateOnly(2022, 11, 18)));
    }

    [Test]
    public void TheMoveTheReasonPointsAtIsReplacedByAMoveTheSpeciesCanUse()
    {
        var pkmn = CreateGen7PokemonWithoutEncounterData();

        var changes = new List<string>();
        var repaired = Legality.ApplyDirectedRepairs(pkmn, EntityContext.Gen7, GameVersion.USUM,
            new[] { "Invalid Move 1: Invalid Move." }, changes);

        Assert.That(repaired.GetMove(0), Is.Not.EqualTo((ushort)0));
        Assert.That(repaired.GetMove(0), Is.Not.EqualTo(pkmn.GetMove(0)));
        Assert.That(repaired.Move1_PP, Is.EqualTo(repaired.GetBasePP(repaired.GetMove(0))));
        Assert.That(changes, Is.Not.Empty);
    }

    [Test]
    public void TheEmptyMoveSlotsAreLeftForAlmToFill()
    {
        var pkmn = CreateGen7PokemonWithoutEncounterData();

        var changes = new List<string>();
        var repaired = Legality.ApplyDirectedRepairs(pkmn, EntityContext.Gen7, GameVersion.USUM,
            new[] { "Invalid Move 1: Invalid Move." }, changes);

        Assert.That(repaired.Move2, Is.EqualTo(0));
        Assert.That(repaired.Move3, Is.EqualTo(0));
        Assert.That(repaired.Move4, Is.EqualTo(0));
        Assert.That(changes.Any(c => c.Contains("move 2")), Is.False);
    }

    [Test]
    public void TheMoveTheReasonDoesNotPointAtIsNotTouched()
    {
        var pkmn = CreateGen7PokemonWithoutEncounterData();
        pkmn.Move2 = 33;
        pkmn.Move2_PP = 35;

        var changes = new List<string>();
        var repaired = Legality.ApplyDirectedRepairs(pkmn, EntityContext.Gen7, GameVersion.USUM,
            new[] { "Invalid Move 1: Invalid Move." }, changes);

        Assert.That(repaired.Move2, Is.EqualTo(33));
        Assert.That(repaired.Move2_PP, Is.EqualTo(35));
    }

    [Test]
    public void TheAbilitySlotIsTheOnlyThingTheAbilityReasonTouches()
    {
        // The Torracat was caught with the hidden ability in the wrong slot: the slot is the only
        // thing that has to change.
        var pkmn = CreateTorracat();

        var changes = new List<string>();
        var repaired = Legality.ApplyDirectedRepairs(pkmn, EntityContext.Gen7, GameVersion.USUM,
            new[] { "Invalid: Hidden Ability mismatch for encounter type." }, changes);

        Assert.That(repaired.AbilityNumber, Is.Not.EqualTo(pkmn.AbilityNumber));
        Assert.That(repaired.MetDate, Is.EqualTo(pkmn.MetDate));
        Assert.That(repaired.MetLocation, Is.EqualTo(pkmn.MetLocation));
        Assert.That(repaired.GetMove(0), Is.EqualTo(pkmn.GetMove(0)));
        Assert.That(Legality.CheckLegality(repaired).Valid, Is.True);
    }

    [Test]
    public void TheMissingHandlingTrainerIsFilledWithTheOriginalTrainer()
    {
        var pkmn = CreateGen7PokemonWithoutEncounterData();
        pkmn.CurrentHandler = 1;

        var changes = new List<string>();
        var repaired = Legality.ApplyDirectedRepairs(pkmn, EntityContext.Gen7, GameVersion.USUM,
            new[] { "Invalid: Untraded: Current handler should not be the Handling Trainer." }, changes);

        Assert.That(repaired.HandlingTrainerName, Is.EqualTo("TESTER"));
        Assert.That(repaired.HandlingTrainerGender, Is.EqualTo(pkmn.OriginalTrainerGender));
        Assert.That(changes, Is.Not.Empty);
    }

    [Test]
    public void TheReasonsAlmRegeneratesOnItsOwnAreNotRepaired()
    {
        var pkmn = CreateGen7PokemonWithoutEncounterData();

        var changes = new List<string>();
        var repaired = Legality.ApplyDirectedRepairs(pkmn, EntityContext.Gen7, GameVersion.USUM, new[]
        {
            "Invalid: PID should be equal to EC!",
            "Invalid: PID-Nature mismatch.",
            "Invalid: Should have at least 3 IVs = 31.",
        }, changes);

        Assert.That(changes, Is.Empty);
        Assert.That(repaired.MetDate, Is.EqualTo(pkmn.MetDate));
        Assert.That(repaired.GetMove(0), Is.EqualTo(pkmn.GetMove(0)));
    }

    [Test]
    public void TheOriginIsNotRepairedButRebuiltFromARealEncounter()
    {
        var pkmn = CreateGen7PokemonWithoutEncounterData();

        var changes = new List<string>();
        var repaired = Legality.ApplyDirectedRepairs(pkmn, EntityContext.Gen7, GameVersion.USUM,
            new[] { "Invalid: Unable to match an encounter from origin game." }, changes);

        // The origin is not a repair: the pokemon is rebuilt from a real encounter instead.
        Assert.That(changes, Is.Empty);
        Assert.That(repaired.MetDate, Is.EqualTo(pkmn.MetDate));
        Assert.That(repaired.GetMove(0), Is.EqualTo(pkmn.GetMove(0)));
    }

    [Test]
    public void ThePokemonThatWasReceivedIsNeverModified()
    {
        var pkmn = CreateGen7PokemonWithoutEncounterData();

        var changes = new List<string>();
        Legality.ApplyDirectedRepairs(pkmn, EntityContext.Gen7, GameVersion.USUM,
            new[] { "Invalid: Met Date is not a valid calendar date." }, changes);

        Assert.That(pkmn.MetDate, Is.Null);
        Assert.That(pkmn.Move1_PP, Is.EqualTo(0));
        Assert.That(pkmn.HandlingTrainerName, Is.Empty);
    }

    [Test]
    public void AHealthyPokemonIsNotRepaired()
    {
        var pkmn = new PK7
        {
            Species = 1,
            CurrentLevel = 5,
            Version = GameVersion.US,
            OriginalTrainerName = "TESTER",
            Language = 2,
            Move1 = 33, // Tackle, a move a Bulbasaur can use
            Move1_PP = 35,
            MetLocation = 6,
            MetLevel = 5
        };

        pkmn.MetDate = new DateOnly(2017, 11, 17);
        pkmn.HandlingTrainerName = "TESTER";

        var changes = new List<string>();
        var repaired = Legality.ApplyDirectedRepairs(pkmn, EntityContext.Gen7, GameVersion.USUM,
            Legality.InvalidReasons(pkmn), changes);

        Assert.That(changes, Is.Empty);
        Assert.That(repaired.MetDate, Is.EqualTo(new DateOnly(2017, 11, 17)));
        Assert.That(repaired.Move1_PP, Is.EqualTo(35));
        Assert.That(repaired.HandlingTrainerName, Is.EqualTo("TESTER"));
    }

    #endregion

    #region S10: the minimal restorer

    [Test]
    public void AMysteryGiftIsGivenAVersionWhereItsMetLocationIsReal()
    {
        // The Mew was received as a mystery gift of a version that does not hand it out: the only
        // thing that has to change is the version of the pokemon.
        var mew = CreateMew();
        Assert.That(Legality.ClassifyReason("Invalid: Mystery Gift cannot be received by this version."),
            Is.EqualTo(LegalityRepair.MetLocation));

        var changes = new List<string>();
        var repaired = Legality.ApplyDirectedRepairs(mew, EntityContext.Gen7, GameVersion.USUM,
            new[] { "Invalid: Mystery Gift cannot be received by this version." }, changes);

        Assert.That(repaired.Version, Is.EqualTo(GameVersion.MN));
        Assert.That(repaired.MetDate, Is.EqualTo(mew.MetDate), "the met date is not touched");
        Assert.That(repaired.MetLocation, Is.EqualTo(mew.MetLocation), "the met location is not touched");
        Assert.That(Legality.CheckLegality(repaired).Valid, Is.True);
    }

    [Test]
    public void TheMetDateOfTheReceivedPokemonIsKeptWhenItStaysLegal()
    {
        var mew = CreateMew();

        var legalized = Legality.AutoLegalizeWithFallback(mew, EntityContext.Gen7, null, out var failureReason,
            out var changes);

        Assert.That(legalized, Is.Not.Null, failureReason);
        Assert.That(legalized!.MetDate, Is.EqualTo(new DateOnly(2023, 4, 10)));
        Assert.That(changes.Any(c => c.Contains("met date")), Is.False,
            "a met date that was kept is not a change");
    }

    [Test]
    public void TheChangesOnlyMentionWhatIsLeftInThePokemonThatIsReturned()
    {
        var mew = CreateMew();

        var legalized = Legality.AutoLegalizeWithFallback(mew, EntityContext.Gen7, null, out var failureReason,
            out var changes);

        Assert.That(legalized, Is.Not.Null, failureReason);
        Assert.That(changes, Is.EqualTo(new[] { "version US -> MN" }));
        Assert.That(Legality.DescribeChanges(mew, legalized!), Is.EqualTo(changes));
    }

    [Test]
    public void TheRestorerGivesBackAValueOnlyWhileThePokemonStaysLegal()
    {
        var mew = CreateMew();
        var legalized = Legality.AutoLegalizeWithFallback(mew, EntityContext.Gen7, null, out _, out _);
        Assert.That(legalized, Is.Not.Null);

        // The met date of the Mew is restored.
        var restored = Legality.RestoreMinimal(mew, legalized!);
        Assert.That(restored.MetDate, Is.EqualTo(mew.MetDate));

        // A value that would make the pokemon illegal is not restored.
        var broken = Legality.ClonePokemon(legalized!);
        Assert.That(broken, Is.Not.Null);
        broken!.MetLocation = 6;
        var stillBroken = Legality.RestoreMinimal(mew, broken!);
        Assert.That(Legality.CheckLegality(stillBroken).Valid, Is.True);
    }

    #endregion

    #region S11: every reason of the check has a repair

    [Test]
    public void AVersionOfAnotherGenerationIsCorrectedToOneOfTheDestinationGeneration()
    {
        // The Ivysaur comes from Gold inside a .pk7: its version does not belong to the
        // generation it has to be legalized into.
        var pkmn = CreateIvysaur();
        Assert.That(pkmn.Version, Is.EqualTo(GameVersion.GD));

        var corrected = Legality.RepairVersionForGeneration(pkmn, EntityContext.Gen7, GameVersion.USUM);

        Assert.That(corrected.Version, Is.EqualTo(GameVersion.USUM));
        Assert.That(corrected.Version.Context, Is.EqualTo(EntityContext.Gen7));
        Assert.That(pkmn.Version, Is.EqualTo(GameVersion.GD),
            "the pokemon that was received is never modified");
    }

    [Test]
    public void AVersionOfTheDestinationGenerationIsNotChanged()
    {
        var pkmn = CreateIvysaur();
        pkmn.Version = GameVersion.US;

        var corrected = Legality.RepairVersionForGeneration(pkmn, EntityContext.Gen7, GameVersion.USUM);

        Assert.That(corrected, Is.SameAs(pkmn));
        Assert.That(corrected.Version, Is.EqualTo(GameVersion.US));
    }

    [TestCase("Invalid: SID16 should be 0.", LegalityRepair.TrainerId)]
    [TestCase("Invalid: Current handler cannot be the OT.", LegalityRepair.HandlingTrainer)]
    [TestCase("Invalid: GeoLocation Memory: Memories should be present.", LegalityRepair.GeoMemory)]
    [TestCase("Invalid: Event OT Friendship does not match base friendship.", LegalityRepair.Friendship)]
    public void EachNewReasonOfAlmIsClassifiedIntoItsRepair(string reason, LegalityRepair expected)
    {
        Assert.That(Legality.ClassifyReason(reason), Is.EqualTo(expected));
    }

    [Test]
    public void TheSidIsZeroWhenTheCheckSaysItHasToBeZero()
    {
        var pkmn = CreateGen7PokemonWithoutEncounterData();
        pkmn.Version = GameVersion.GD;
        pkmn.SID16 = 12345;

        var changes = new List<string>();
        var repaired = Legality.ApplyDirectedRepairs(pkmn, EntityContext.Gen7, GameVersion.USUM,
            new[] { "Invalid: SID16 should be 0." }, changes);

        Assert.That(repaired.SID16, Is.EqualTo(0));
        Assert.That(changes, Does.Contain("SID16 12345 -> 0"));
    }

    [Test]
    public void TheGeoLocationMemoryIsFilledWhenItIsNotThere()
    {
        var pkmn = CreateGen7PokemonWithoutEncounterData();
        pkmn.Version = GameVersion.GD;
        Assert.That(((IGeoTrack)pkmn).Geo1_Country, Is.EqualTo(0));

        var changes = new List<string>();
        var repaired = Legality.ApplyDirectedRepairs(pkmn, EntityContext.Gen7, GameVersion.USUM,
            new[] { "Invalid: GeoLocation Memory: Memories should be present." }, changes);

        Assert.That(((IGeoTrack)repaired).Geo1_Country, Is.Not.EqualTo(0));
        Assert.That(changes, Is.Not.Empty);
    }

    [Test]
    public void ThePokemonIsInTheHandsOfTheHandlingTrainerWhenTheCheckSaysItCannotBeTheOriginalTrainer()
    {
        var pkmn = CreateIvysaur();
        Assert.That(pkmn.CurrentHandler, Is.EqualTo(0));

        var changes = new List<string>();
        var repaired = Legality.ApplyDirectedRepairs(pkmn, EntityContext.Gen7, GameVersion.USUM,
            new[] { "Invalid: Current handler cannot be the OT." }, changes);

        Assert.That(repaired.CurrentHandler, Is.EqualTo(1));
        Assert.That(repaired.HandlingTrainerName, Is.EqualTo(pkmn.OriginalTrainerName));
        Assert.That(changes, Is.Not.Empty);
    }

    [Test]
    public void TheFriendshipIsTheBaseFriendshipOfTheSpeciesWhenTheCheckSaysItIsNot()
    {
        var pkmn = CreateIvysaur();
        Assert.That(pkmn.CurrentFriendship, Is.EqualTo(0));

        var changes = new List<string>();
        var repaired = Legality.ApplyDirectedRepairs(pkmn, EntityContext.Gen7, GameVersion.USUM,
            new[] { "Invalid: Event OT Friendship does not match base friendship." }, changes);

        Assert.That(repaired.CurrentFriendship, Is.EqualTo(pkmn.PersonalInfo.BaseFriendship));
        Assert.That(changes, Is.Not.Empty);
    }

    [Test]
    public void EveryReasonOfTheIvysaurIsResolvedByTheLegalization()
    {
        // The Ivysaur is the criterion of S11: it arrives with seven reasons and none of them
        // can be left when it is legalized.
        var pkmn = CreateIvysaur();
        var reasons = Legality.InvalidReasons(pkmn);
        Assert.That(reasons, Has.Count.EqualTo(7));

        var legalized = Legality.AutoLegalizeWithFallback(pkmn, EntityContext.Gen7, null,
            out var failureReason, out var changes);

        Assert.That(legalized, Is.Not.Null, failureReason);
        Assert.That(Legality.CheckLegality(legalized!).Valid, Is.True, failureReason);
        Assert.That(Legality.InvalidReasons(legalized!), Is.Empty);
        Assert.That(legalized!.Version.Context, Is.EqualTo(EntityContext.Gen7),
            "the version belongs to the generation the pokemon is legalized into");
        Assert.That(changes, Is.Not.Empty);
    }

    #endregion

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
    /// A Gen 7 Torracat whose ability slot does not match its encounter.
    /// </summary>
    /// <remarks>
    /// The file is embedded as base64 so the test does not depend on any file of the repository.
    /// </remarks>
    private static PK7 CreateTorracat()
    {
        const string base64 =
            "qUun0wAAL/zWAgAAv2QAP7kTAAAWBAAAmxcpTA8AVVVVVVVVAAAAAAAAAPIAAAAAAAAAAAAAAAAAAAAAAAAAAFQAbwByAHIAYQBj" +
            "AGEAdAAAAAAAAAAAAAAACgAYAKgBegAjHg8eAAAAAAAAAAAAAAAAAAD/+/8/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABixBAG4AZAByAOkAcwAAAAAAAAAAAAAAAAAAAP//AAAAAAAAAAAXBAoAAAAIAAQFACAx" +
            "CwEHAAAAAAAAAAATAAAAPwA/ACoAIQAxADEAIQAAAAAAAAA=";

        var pkmn = Helpers.PokemonFromBase64(base64, EntityContext.Gen7);
        Assert.That(pkmn, Is.Not.Null);
        return pkmn as PK7 ?? throw new InvalidOperationException("the fixture is not a Gen 7 Pokemon");
    }

    #region S3: the version of the other game of the pair

    [TestCase(GameVersion.US, EntityContext.Gen7, GameVersion.UM)]
    [TestCase(GameVersion.UM, EntityContext.Gen7, GameVersion.US)]
    [TestCase(GameVersion.SN, EntityContext.Gen7, GameVersion.MN)]
    [TestCase(GameVersion.MN, EntityContext.Gen7, GameVersion.SN)]
    public void TheOtherGameOfThePairIsTheSiblingOfTheVersion(GameVersion version, EntityContext context,
        GameVersion expected)
    {
        Assert.That(Legality.SiblingVersion(version, context), Is.EqualTo(expected));
    }

    [TestCase(GameVersion.CXD)]
    [TestCase(GameVersion.RB)]
    [TestCase(GameVersion.RD)]
    public void AVersionWithoutAPairDoesNotHaveASibling(GameVersion version)
    {
        Assert.That(Legality.SiblingVersion(version, EntityContext.Gen7), Is.Null);
    }

    [Test]
    public void APokemonWithTheVersionOfTheOtherGameOfItsPairIsLegalizedWithThatChangeAlone()
    {
        // The Latias was caught in Ultra Moon but its file says Ultra Sun: the only thing that has
        // to change is the version, and the Pokemon must not be regenerated (S3).
        var latias = CreateLatias();
        Assert.That(latias.Version, Is.EqualTo(GameVersion.US));

        var legalized = Legality.AutoLegalizeWithFallback(latias, EntityContext.Gen7, GameVersion.ORAS,
            out var failureReason, out var changes);

        Assert.That(legalized, Is.Not.Null, failureReason);
        Assert.That(Legality.CheckLegality(legalized!).Valid, Is.True, failureReason);

        Assert.That(changes, Has.Count.EqualTo(1),
            $"the only change is the version: [{string.Join(", ", changes)}]");
        Assert.That(changes[0], Does.StartWith("version US -> UM"));

        // The Pokemon was not regenerated: everything it had is still there.
        Assert.That(legalized!.OriginalTrainerName, Is.EqualTo(latias.OriginalTrainerName));
        Assert.That(legalized.TID16, Is.EqualTo(latias.TID16));
        Assert.That(legalized.SID16, Is.EqualTo(latias.SID16));
        Assert.That(legalized.CurrentLevel, Is.EqualTo(latias.CurrentLevel));
        Assert.That(legalized.CurrentFriendship, Is.EqualTo(latias.CurrentFriendship));
        Assert.That(legalized.MetDate, Is.EqualTo(latias.MetDate));
    }

    #endregion

    #region S4: the met date is not the day the engine runs

    [Test]
    public void APokemonWithoutAMetDateEndsWithThePlausibleDateOfTheGameItEndsUpIn()
    {
        // The Riolu has no met date: the date of the day the engine runs must not end up in it.
        var riolu = CreateRiolu();
        Assert.That(riolu.MetDate, Is.Null);

        var legalized = Legality.AutoLegalizeWithFallback(riolu, EntityContext.Gen7, null,
            out var failureReason, out _);

        Assert.That(legalized, Is.Not.Null, failureReason);
        Assert.That(legalized!.MetDate, Is.Not.EqualTo(DateOnly.FromDateTime(DateTime.Now)),
            "the met date is not the day the engine runs");
        Assert.That(legalized.MetDate,
            Is.EqualTo(Legality.PlausibleMetDate(legalized.Version.Context, legalized.Version)),
            "the met date is the plausible date of the game the Pokemon ends up in");
    }

    #endregion

    #region S6: the minimal restorer is not thrown away

    [Test]
    public void APokemonBuiltFromItsPreviousFormKeepsTheDataThatCanBeRestored()
    {
        // The Metapod is built again from the encounter of the species it evolves from: the
        // values that can be restored (the met location and the ability) have to be the ones
        // of the Pokemon that was received, and not the ones of the encounter (S10.3, S6).
        var metapod = CreateMetapod();

        var legalized = Legality.AutoLegalizeWithFallback(metapod, EntityContext.Gen7, GameVersion.ORAS,
            out var failureReason, out _);

        Assert.That(legalized, Is.Not.Null, failureReason);
        Assert.That(Legality.CheckLegality(legalized!).Valid, Is.True, failureReason);

        Assert.That(legalized!.MetLocation, Is.EqualTo(metapod.MetLocation),
            "the met location of the Pokemon that was received is kept");
        Assert.That(legalized.Ability, Is.EqualTo(metapod.Ability),
            "the ability of the Pokemon that was received is kept");
        Assert.That(legalized.AbilityNumber, Is.EqualTo(metapod.AbilityNumber),
            "the ability slot of the Pokemon that was received is kept");
    }

    #endregion

    /// <summary>
    /// A Gen 7 Latias whose file says Ultra Sun although it was caught in Ultra Moon.
    /// </summary>
    /// <remarks>
    /// The file is embedded as base64 so the test does not depend on any file of the repository.
    /// </remarks>
    private static PK7 CreateLatias()
    {
        const string base64 =
            "XQQZEAAAXNB8AQAAv2QAP7AeBAAaAVUFod8chAACAAAAAAAAAAAAAAAAAAAAAAAAiAAAAADAAwAA" +
            "AAAAAAAAAEwAYQB0AGkAYQBzAAAAAAAAAAAAAAAAAAAAKAGWAXcBEQEFCgoKAAAAACgBlgF3AREB" +
            "AAD///8/UABLAFMATQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABBAQAAAAA" +
            "AAAAAABBAG4AZAByAOkAcwAAAAAAAAAAAAAAAAAAAFoAAAAAAAAAAAAaCwgAAADeABI8ACAxBwEH" +
            "AAAAAAAAAAA8AAAAuAC4AHcAgwCbAJsAswAAAAAAAAA=";

        var pkmn = Helpers.PokemonFromBase64(base64, EntityContext.Gen7);
        Assert.That(pkmn, Is.Not.Null);
        return pkmn as PK7 ?? throw new InvalidOperationException("the fixture is not a Gen 7 Pokemon");
    }

    /// <summary>
    /// A Gen 7 Riolu with no met date.
    /// </summary>
    /// <remarks>
    /// The file is embedded as base64 so the test does not depend on any file of the repository.
    /// </remarks>
    private static PK7 CreateRiolu()
    {
        const string base64 =
            "/G71LwAAzLS/AQAAv2QAPwAAAACeBAAAsiIIeQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAFIAaQBvAGwAdQAAAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAD///8/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAABBAG4AZAByAOkAcwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAGAAQBACABAgAH" +
            "AAAAAAAAAAABAAAADAAMAAYABgAGAAYABgAAAAAAAAA=";

        var pkmn = Helpers.PokemonFromBase64(base64, EntityContext.Gen7);
        Assert.That(pkmn, Is.Not.Null);
        return pkmn as PK7 ?? throw new InvalidOperationException("the fixture is not a Gen 7 Pokemon");
    }

    /// <summary>
    /// A Gen 7 Metapod that has to be built again from the encounter of its previous form.
    /// </summary>
    /// <remarks>
    /// The file is embedded as base64 so the test does not depend on any file of the repository.
    /// </remarks>
    private static PK7 CreateMetapod()
    {
        const string base64 =
            "/G71LwAAI9ILAAAAOTAx1AAAAAA9AQAANEymEgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAE0AZQB0AGEAcABvAGQAAAAAAAAAAAAAAAAAagAAAAAAAAAeAAAAAAAAAAAAAAAAAAAAAAD///8/" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "ABQAEsAUwBNAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQEAAAAGAAQBAB4BAgAHAAAAAAAAAA" +
            "ABAAAADAAMAAUABgAFAAUABQAAAAAAAAA=";

        var pkmn = Helpers.PokemonFromBase64(base64, EntityContext.Gen7);
        Assert.That(pkmn, Is.Not.Null);
        return pkmn as PK7 ?? throw new InvalidOperationException("the fixture is not a Gen 7 Pokemon");
    }
}
