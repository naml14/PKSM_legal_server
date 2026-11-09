using System.Diagnostics;
using System.Text.Json;
using coreconsole.Models;
using coreconsole.utils;
using PKHeX.Core;
using PKHeX.Core.AutoMod;
using Sentry;

namespace coreconsole.handlers;

public static class Legality
{
    private static PKM? MainHandler(string pokemon, EntityContext? context)
    {
        var pkmn = Helpers.PokemonFromBase64(pokemon, context ?? EntityContext.None);
        if (pkmn == null) return null;
        Helpers.Init();

        return pkmn;
    }

    public static void LegalityCheckHandler(string pokemon, EntityContext? context)
    {
        var pkmn = MainHandler(pokemon, context);
        if (pkmn == null) return;

        var la = CheckLegality(pkmn);
        Console.WriteLine(JsonSerializer.Serialize(new LegalityCheckReport(la), Helpers.SerializerOptions));
    }

    public static void LegalizeHandler(string pokemon, EntityContext? context, EntityContext? legalizationGeneration,
        GameVersion? version)
    {
        var pkmn = MainHandler(pokemon, context);
        if (pkmn == null) return;

        var report = CheckLegality(pkmn);
        if (report.Valid)
        {
            Console.WriteLine("{\"error\": \"this pokemon is already legal!\"}");
            return;
        }

        var targetContext = ResolveLegalizationContext(pkmn, legalizationGeneration, context);
        if (targetContext == null)
        {
            // The context of the destination cannot be deduced: say it plainly instead of
            // handing a context-less pokemon over to ALM (which fails with an opaque error).
            const string error =
                "{\"error\": \"cannot determine the target generation to legalize into: the pokemon has no context and no usable generation was provided\"}";
            Console.Error.WriteLine(error);
            // The serve loop has to stay alive when a request cannot be served (S9), so the
            // failure is thrown there instead of ending the process.
            Helpers.Fail(enums.ExitCode.UnknownErrorDuringBase64ToPokemon, error);
            return;
        }

        var result = AutoLegalizeWithFallback(pkmn, targetContext.Value, version, out var failureReason,
            out var changes, out var generationNote);
        if (result != null)
        {
            report = CheckLegality(result);
        }

        // The note of the generation says in which generation the Pokemon ended up when it had
        // to be legalized into another one (S13); the reason of a failure says why it could not
        // be legalized.
        var note = failureReason ?? generationNote;

        Console.WriteLine(JsonSerializer.Serialize(new AutoLegalizationResult(report, result, note, changes),
            Helpers.SerializerOptions));
    }

    public static LegalityAnalysis CheckLegality(PKM pokemon)
    {
        return new LegalityAnalysis(pokemon);
    }

    /// <summary>
    /// Works out the generation the legalized pokemon has to belong to.
    /// </summary>
    /// <remarks>
    /// The destination is the context of the pokemon itself (a .pk9 legalizes into Gen 9), unless the
    /// caller forces one with <c>--legalization-generation</c>. The context of the file is always known:
    /// PKHeX.Core 26.10.18 sets <see cref="PKM.Context"/> from the format it detected while reading the
    /// file, so a .pk9 always arrives as <see cref="EntityContext.Gen9"/>.
    /// <br />
    /// <c>--generation</c> is only used as a fallback (when the file carries no context of its own):
    /// it describes the generation the caller assumes for the file, which for a .pk9 is not the
    /// generation the pokemon has to be legalized into (a Gen 9 file cannot become a Gen 7 pokemon).
    /// </remarks>
    public static EntityContext? ResolveLegalizationContext(PKM pokemon, EntityContext? forcedContext = null,
        EntityContext? assumedContext = null)
    {
        if (forcedContext.HasValue && IsUsableContext(forcedContext.Value)) return forcedContext;

        if (IsUsableContext(pokemon.Context)) return pokemon.Context;

        if (assumedContext.HasValue && IsUsableContext(assumedContext.Value)) return assumedContext;

        // Last resort: the generation encoded in the file (PKHeX.Core 26.10.18 exposes it as PKM.Format).
        var generation = pokemon.Format;
        if (generation >= 1 && generation <= 9) return (EntityContext)generation;

        return null;
    }

    /// <summary>
    /// Tells whether a context describes a generation ALM can work with. None, SplitInvalid and
    /// MaxInvalid do not.
    /// </summary>
    public static bool IsUsableContext(EntityContext context)
    {
        return context switch
        {
            EntityContext.None => false,
            EntityContext.SplitInvalid => false,
            EntityContext.MaxInvalid => false,
            _ => true
        };
    }

    /// <summary>
    /// Works out the game version the legalized pokemon has to belong to.
    /// </summary>
    /// <remarks>
    /// The version given on the command line wins. Otherwise the version of the file is used when it
    /// belongs to the destination generation, and the generation's own version is used when it does
    /// not: the version stored in a .pk9 is the game the pokemon comes from (a HOME transfer keeps
    /// the original game, e.g. FireRed), not the game it is legalized into.
    /// </remarks>
    public static GameVersion ResolveDestinationVersion(PKM pokemon, EntityContext targetContext,
        GameVersion? overriddenVersion)
    {
        if (overriddenVersion.HasValue) return overriddenVersion.Value;

        var version = pokemon.Version;
        if (version != GameVersion.Any && version.Context == targetContext) return version;

        return DefaultVersionFor(targetContext);
    }

    /// <summary>
    /// The version of the game of a generation (Scarlet/Violet for Gen 9, Sword/Shield for Gen 8, ...).
    /// </summary>
    public static GameVersion DefaultVersionFor(EntityContext context)
    {
        return context switch
        {
            EntityContext.Gen9 => GameVersion.SV,
            EntityContext.Gen9a => GameVersion.ZA,
            EntityContext.Gen8 => GameVersion.SWSH,
            EntityContext.Gen8a => GameVersion.PLA,
            EntityContext.Gen8b => GameVersion.BDSP,
            EntityContext.Gen7 => GameVersion.USUM,
            EntityContext.Gen7b => GameVersion.GG,
            EntityContext.Gen6 => GameVersion.ORAS,
            EntityContext.Gen5 => GameVersion.B2W2,
            EntityContext.Gen4 => GameVersion.HGSS,
            EntityContext.Gen3 => GameVersion.FRLG,
            EntityContext.Gen2 => GameVersion.GSC,
            EntityContext.Gen1 => GameVersion.RB,
            _ => GameVersion.Any
        };
    }

    /// <summary>
    /// Translates a generation name into the generation number AutoMod (ALM) expects.
    /// </summary>
    /// <remarks>
    /// The names accepted here are the ones the API layer sends: Gen1 .. Gen9, Gen7b, Gen8a,
    /// Gen8b, None, SplitInvalid and MaxInvalid. They are the members of
    /// <see cref="EntityContext"/> in PKHeX.Core, so the CLI can parse them directly.
    /// <br />
    /// ALM identifies a generation with an integer, so that conversion is reused instead of a
    /// hand-written table.
    /// <br />
    /// None, SplitInvalid and MaxInvalid do not describe a usable generation (PKHeX.Core
    /// rejects the latter two when converting them), so they force no generation at all and
    /// ALM keeps its default one.
    /// </remarks>
    public static int? GetLegalizationGeneration(EntityContext context)
    {
        return context switch
        {
            EntityContext.None => null,
            EntityContext.SplitInvalid => null,
            EntityContext.MaxInvalid => null,
            _ => context.Generation
        };
    }

    /// <summary>
    /// The game versions of each generation, newest first.
    /// </summary>
    /// <remarks>
    /// The generation of a file comes from its format: a .pk7 is a Gen 7 file and a .pk9 a Gen 9
    /// one (PKHeX.Core sets <see cref="PKM.Context"/> while reading the file). When the version the
    /// caller asks for does not work, the versions of that generation are tried from the newest to
    /// the oldest, because the encounters a pokemon comes from belong to the game it was caught in.
    /// </remarks>
    private static readonly Dictionary<EntityContext, GameVersion[]> VersionsByContext = new()
    {
        [EntityContext.Gen9] = new[] { GameVersion.SV },
        [EntityContext.Gen9a] = new[] { GameVersion.ZA },
        [EntityContext.Gen8] = new[] { GameVersion.SWSH },
        [EntityContext.Gen8a] = new[] { GameVersion.PLA },
        [EntityContext.Gen8b] = new[] { GameVersion.BDSP },
        [EntityContext.Gen7] = new[] { GameVersion.USUM, GameVersion.SM, GameVersion.ORAS, GameVersion.XY, GameVersion.B2W2, GameVersion.BW },
        [EntityContext.Gen7b] = new[] { GameVersion.GG },
        [EntityContext.Gen6] = new[] { GameVersion.ORAS },
        [EntityContext.Gen5] = new[] { GameVersion.B2W2 },
        [EntityContext.Gen4] = new[] { GameVersion.HGSS, GameVersion.DPPt, GameVersion.DP },
        [EntityContext.Gen3] = new[] { GameVersion.FRLG, GameVersion.RSE, GameVersion.CXD },
        [EntityContext.Gen2] = new[] { GameVersion.GS, GameVersion.C },
        [EntityContext.Gen1] = new[] { GameVersion.RBY, GameVersion.RB },
    };

    /// <summary>
    /// Legalizes a pokemon trying the destination versions in order of preference.
    /// </summary>
    public static PKM? AutoLegalizeWithFallback(PKM pokemon, EntityContext targetContext,
        GameVersion? requestedVersion, out string? failureReason)
    {
        return AutoLegalizeWithFallback(pokemon, targetContext, requestedVersion, out failureReason, out _);
    }
    /// <summary>
    /// Legalizes a pokemon trying the destination versions in order of preference.
    /// </summary>
    /// <param name="changes">The changes that the pokemon that is returned has with respect to the
    /// one that was received. It is empty when the pokemon was not modified at all.</param>
    /// <remarks>
    /// The search has two parts (S13):
    /// <list type="number">
    /// <item>The generation of the file is searched first, with all its versions (the newest
    /// first) and the sweep of the levels: a Pokemon that can be legalized where it came from
    /// does not move, and its changes do not change either.</item>
    /// <item>When that search does not find any encounter, the other generations are searched,
    /// from the newest to the oldest (Gen9, Gen8, Gen7, Gen6, Gen5, Gen4, Gen3, Gen2, Gen1):
    /// the game a Pokemon was caught in does not matter, only that the Pokemon is legal. When the
    /// Pokemon ends up in another generation, the change says so
    /// (<c>generation Gen7 -&gt; Gen6</c>).</item>
    /// </list>
    /// Both searches are bounded by a budget of time and a number of attempts, so the answer
    /// always arrives before the timeout of the API, and the reason of a failure is always said.
    /// </remarks>
    public static PKM? AutoLegalizeWithFallback(PKM pokemon, EntityContext targetContext,
        GameVersion? requestedVersion, out string? failureReason, out List<string> changes)
    {
        return AutoLegalizeWithFallback(pokemon, targetContext, requestedVersion, out failureReason,
            out changes, out _);
    }

    /// <summary>
    /// Legalizes a pokemon trying the destination versions in order of preference.
    /// </summary>
    /// <param name="changes">The changes that the pokemon that is returned has with respect to the
    /// one that was received. It is empty when the pokemon was not modified at all.</param>
    /// <param name="generationNote">The explanation of the change of generation, when the Pokemon
    /// that is returned does not belong to the generation it had to be legalized into (S13).</param>
    public static PKM? AutoLegalizeWithFallback(PKM pokemon, EntityContext targetContext,
        GameVersion? requestedVersion, out string? failureReason, out List<string> changes,
        out string? generationNote)
    {
        failureReason = null;
        generationNote = null;
        changes = new List<string>();
        var tried = new List<GameVersion>();
        string? firstFailure = null;

        // 1) The generation of the file: its versions, from the newest to the oldest, and the
        // sweep of the levels. This is the part that keeps the behaviour of the Pokemon that can
        // be legalized where it came from.
        var budget = new SearchBudget(SearchTimeBudget, MaxSearchAttempts);
        var inFile = SearchGeneration(pokemon, targetContext, requestedVersion, budget, tried,
            MaxPreEvolutionAttempts, out var reason, out _);
        if (inFile != null)
        {
            changes.AddRange(DescribeChanges(pokemon, inFile));
            generationNote = GenerationChange(targetContext, inFile, changes);
            return inFile;
        }

        firstFailure ??= reason;

        // 2) The other generations, from the newest to the oldest (S13): the game the Pokemon was
        // caught in does not matter, only that the Pokemon is legal.
        var fallback = new SearchBudget(FallbackTimeBudget, MaxFallbackAttempts);
        foreach (var context in FallbackGenerations(targetContext))
        {
            if (fallback.Exhausted) break;

            var other = SearchGeneration(pokemon, context, null, fallback, tried,
                MaxFallbackPreEvolutionAttempts, out var otherReason, out _);
            if (other == null)
            {
                firstFailure ??= otherReason;
                continue;
            }

            changes.AddRange(DescribeChanges(pokemon, other));
            generationNote = GenerationChange(targetContext, other, changes);
            return other;
        }

        // 3) The Pokemon cannot be legalized. The message says whether the species has an
        // encounter of any kind at all (the "impossible" case, which is the only one that keeps
        // that word) or whether the search ran out of its budget.
        var exists = GenerationsWithEncounter(pokemon);
        if (exists.Count == 0)
        {
            // The species may still have an encounter as the evolution of another one.
            exists = GenerationsWithAnyEncounter(pokemon);
        }

        failureReason = exists.Count == 0
            ? ImpossibleReason(pokemon, targetContext, tried, ExtractReasons(firstFailure))
            : NotFoundReason(pokemon, targetContext, tried, exists, ExtractReasons(firstFailure), fallback);
        return null;
    }

    /// <summary>
    /// Searches an encounter for the Pokemon in one generation.
    /// </summary>
    /// <param name="pokemon">The Pokemon that was received.</param>
    /// <param name="targetContext">The generation that is being searched.</param>
    /// <param name="requestedVersion">The version the caller asks for, when it is the first one
    /// that is tried.</param>
    /// <param name="budget">The budget of time and attempts of the search.</param>
    /// <param name="tried">The versions that have been tried, to be able to report them.</param>
    /// <param name="maxPreEvolutionAttempts">How many times the previous form of the Pokemon is
    /// tried.</param>
    /// <param name="failureReason">The reason of the failure, when no encounter was found.</param>
    /// <param name="changes">The changes of the Pokemon that is returned.</param>
    /// <returns>The legal Pokemon, or null when no encounter was found.</returns>
    /// <remarks>
    /// The legalization follows the decision tree of S10: the reasons ALM reports about the Pokemon
    /// are classified and each one gets the repair it points at, and nothing else. The repairs are
    /// tried with the chain of versions of the generation.
    /// <list type="number">
    /// <item>The reasons of the report of the Pokemon that was received are classified and the
    /// repair each one points at is applied (the met date, a move, the ability slot, the met
    /// location or the data of the handling trainer).</item>
    /// <item>When the repairs are enough the Pokemon is legal as it is and it is returned without
    /// being regenerated: that is the result that changes the least.</item>
    /// <item>Otherwise ALM is asked to regenerate it, and the result is given back with the values
    /// of the Pokemon that was received restored (the met date, the met location, the met level,
    /// the moves and their PP, the ability, the handling trainer, the IVs/EVs, the nature and the
    /// held item), as long as the Pokemon stays legal.</item>
    /// <item>When even that is not enough the Pokemon is built again from a real encounter of its
    /// species, which is the last resort.</item>
    /// </list>
    /// </remarks>
    private static PKM? SearchGeneration(PKM pokemon, EntityContext targetContext,
        GameVersion? requestedVersion, SearchBudget budget, List<GameVersion> tried,
        int maxPreEvolutionAttempts, out string? failureReason, out List<string> changes)
    {
        failureReason = null;
        changes = new List<string>();
        string? firstFailure = null;
        var candidates = CandidateVersions(pokemon, targetContext, requestedVersion).ToList();

        var siblingTried = false;

        foreach (var candidate in candidates)
        {
            tried.Add(candidate);

            // Each attempt is made with the version the Pokemon has, and, when that version does
            // not belong to the destination generation, with the version of the generation being
            // tried: a Pokemon of Gold inside a .pk7 has no encounter at all in Gen 7, so the
            // version has to be corrected BEFORE the encounter is looked for (S11).
            foreach (var attempt in VersionedAttempts(pokemon, targetContext, candidate))
            {
                // The reasons ALM reports about the Pokemon of the attempt: they are collected
                // again because the version correction can solve some of them.
                var reasons = InvalidReasons(attempt);

                // The repair each reason points at, and nothing else. The changes are not taken
                // from here: the list of changes is the difference between the Pokemon that was
                // received and the one that is returned, so a value that ends up being restored
                // is not a change.
                var repaired = ApplyDirectedRepairs(attempt, targetContext, candidate, reasons,
                    new List<string>());

                // The repairs can be enough on their own: the Pokemon is legal without being
                // regenerated, and that is the result that changes the least.
                if (CheckLegality(repaired).Valid)
                {
                    changes.AddRange(DescribeChanges(pokemon, repaired));
                    return repaired;
                }

                // The pokemon is also tried with the other game of the pair of its version (Ultra
                // Sun and Ultra Moon, Sun and Moon, ...): a Pokemon caught in Ultra Moon whose file
                // says Ultra Sun is legal as soon as the version names the game it comes from, and
                // that is the change that modifies the Pokemon the least, so it is tried before
                // regenerating it. It is tried once: the version of the Pokemon does not change
                // from one candidate to the next.
                if (!siblingTried)
                {
                    siblingTried = true;

                    var sibling = SiblingVersionAttempt(pokemon, targetContext);
                    if (sibling != null)
                    {
                        var siblingRepaired = ApplyDirectedRepairs(sibling, targetContext, candidate,
                            InvalidReasons(sibling), new List<string>());
                        if (CheckLegality(siblingRepaired).Valid)
                        {
                            changes.AddRange(DescribeChanges(pokemon, siblingRepaired));
                            return siblingRepaired;
                        }
                    }
                }

                // Otherwise ALM is asked to regenerate the Pokemon with the repairs applied.
                var legalized = TryLegalize(repaired, targetContext, candidate, budget, out var reason);
                if (legalized != null)
                {
                    var restored = RestoreMinimal(pokemon, legalized);
                    restored = PreserveShiny(pokemon, restored);

                    // The Pokemon that is returned has to pass the check: a Pokemon that still has
                    // a reason is not a result (S11).
                    if (CheckLegality(restored).Valid)
                    {
                        changes.AddRange(DescribeChanges(pokemon, restored));
                        return restored;
                    }

                    firstFailure ??= reason;
                    continue;
                }

                firstFailure ??= reason;
                if (budget.Exhausted) break;
            }

            if (budget.Exhausted) break;
        }

        // The last resort: the Pokemon is built again from a real encounter of its species.
        foreach (var candidate in candidates)
        {
            if (budget.Exhausted) break;

            var rebuilt = RebuildFromEncounter(pokemon, targetContext, candidate, budget, out _);
            if (rebuilt == null) continue;

            var restored = RestoreMinimal(pokemon, rebuilt);
            restored = PreserveShiny(pokemon, restored);

            // The Pokemon that is returned has to pass the check (S11).
            if (!CheckLegality(restored).Valid) continue;

            changes.AddRange(DescribeChanges(pokemon, restored));
            return restored;
        }

        // The last of the last resort: the species does not have an encounter of its own in the
        // destination generation (the Ivysaur does not appear in the wild in Alola: only the
        // starter Bulbasaur does), so the Pokemon is built from the encounter of the species it
        // evolves from and then evolved.
        var attempts = maxPreEvolutionAttempts;
        foreach (var candidate in candidates)
        {
            foreach (var pre in PreEvolutionCandidates(pokemon))
            {
                if (attempts-- <= 0) break;
                if (budget.Exhausted) break;

                var evolved = RebuildFromPreEvolution(pokemon, targetContext, candidate, pre, budget, out _);
                if (evolved == null) continue;

                var restored = RestoreMinimal(pokemon, evolved);

                // The shiny flag is put back on the RESTORED pokemon: the other two paths (the
                // regeneration and the rebuild from an encounter) do the same, and handing over
                // the pokemon of ALM instead would throw away everything RestoreMinimal put
                // back (the met location, the ability, the moves, the IVs/EVs, ...).
                restored = PreserveShiny(pokemon, restored);
                if (!CheckLegality(restored).Valid) continue;

                changes.AddRange(DescribeChanges(pokemon, restored));
                return restored;
            }

            if (attempts <= 0) break;
            if (budget.Exhausted) break;
        }

        // The last resource of the last resource: the Pokemon is built again from a real encounter
        // of its species, trying the levels that have an encounter in the destination generation
        // (S12). The species does not always have an encounter at the level the Pokemon has (a
        // Pikachu of Alola is not wild at level 1), and the level of the encounter is the one
        // that makes the Pokemon legal.
        foreach (var candidate in candidates)
        {
            if (budget.Exhausted) break;

            var swept = LegalizeFromEncounterWithLevels(pokemon, targetContext, candidate, budget);
            if (swept == null) continue;

            var restoredSweep = RestoreMinimal(pokemon, swept);

            // The shiny flag is put back on the RESTORED pokemon, like in the other paths.
            restoredSweep = PreserveShiny(pokemon, restoredSweep);
            if (!CheckLegality(restoredSweep).Valid) continue;

            changes.AddRange(DescribeChanges(pokemon, restoredSweep));
            return restoredSweep;
        }

        failureReason = firstFailure ?? BudgetReason(targetContext, budget);
        return null;
    }

    /// <summary>
    /// The change of generation when the Pokemon that is returned does not belong to the
    /// generation it had to be legalized into (S13).
    /// </summary>
    /// <param name="targetContext">The generation the Pokemon had to be legalized into.</param>
    /// <param name="legalized">The Pokemon that was legalized.</param>
    /// <param name="changes">The list of changes, where the change of generation is added.</param>
    /// <returns>The explanation of the change of generation, or null when there is none.</returns>
    /// <remarks>
    /// The change is the first one of the list, because it is the one that says in which
    /// generation the Pokemon ended up: the game a Pokemon was caught in does not matter, only
    /// that it is legal.
    /// </remarks>
    private static string? GenerationChange(EntityContext targetContext, PKM legalized, List<string> changes)
    {
        var final = legalized.Version.Context;
        if (final == EntityContext.None || final == targetContext) return null;

        changes.Insert(0, $"generation {targetContext} -> {final}");
        return $"the Pokemon was legalized into the generation {final} (it was received in the generation {targetContext})";
    }

    /// <summary>
    /// The generations the fallback looks at when the generation of the file does not have any
    /// encounter for the species, the newest first (S13).
    /// </summary>
    public static IEnumerable<EntityContext> FallbackGenerations(EntityContext targetContext)
    {
        foreach (var context in new[]
                 {
                     EntityContext.Gen9, EntityContext.Gen8, EntityContext.Gen7, EntityContext.Gen6,
                     EntityContext.Gen5, EntityContext.Gen4, EntityContext.Gen3, EntityContext.Gen2,
                     EntityContext.Gen1
                 })
        {
            if (context == targetContext) continue;
            yield return context;
        }
    }

    /// <summary>
    /// The reason a pokemon cannot be legalized because its species has no encounter of any kind
    /// in any generation (S13).
    /// </summary>
    /// <param name="pokemon">The pokemon that was received.</param>
    /// <param name="targetContext">The generation the pokemon has to be legalized into.</param>
    /// <param name="tried">The versions that were tried, in every generation.</param>
    /// <param name="reasons">The reasons ALM reported about the pokemon.</param>
    /// <remarks>
    /// The reason says which species it is, in which generations the species has an encounter and
    /// why it cannot be legalized here: the generation of the file is the one the pokemon has to
    /// be legalized into, and the engine looks for the encounter in every other generation too
    /// (S13), so this message is only used when there is nothing to look for.
    /// </remarks>
    private static string ImpossibleReason(PKM pokemon, EntityContext targetContext,
        IReadOnlyList<GameVersion> tried, string reasons)
    {
        var species = pokemon.Species;
        var name = SpeciesName(species);
        var exists = GenerationsWithEncounter(pokemon);
        var where = exists.Count == 0
            ? "no generation known to PKHeX"
            : string.Join(", ", exists);

        return
            $"Auto-legalization failed: it is impossible to legalize this Pokemon: {name} (species #{species}) has no encounter of any kind (wild, gift, event or previous form) in ANY generation (the generations that were looked at were: {string.Join(", ", GenerationsLookedAt(targetContext))}; the versions that were tried were: {string.Join(", ", tried)}). The species has an encounter in: {where}. The generation of the file is {targetContext} and the engine looked for the encounter in every other generation too. Reasons: {reasons}";
    }

    /// <summary>
    /// The generations the engine looks at when it looks for an encounter: the generation of the
    /// file and the other ones, from the newest to the oldest.
    /// </summary>
    private static IEnumerable<EntityContext> GenerationsLookedAt(EntityContext targetContext)
    {
        yield return targetContext;
        foreach (var context in FallbackGenerations(targetContext)) yield return context;
    }

    /// <summary>
    /// The generations in which the species of the Pokemon, or one of the forms it comes from, has
    /// an encounter of some kind.
    /// </summary>
    /// <param name="pokemon">The pokemon whose species is looked for.</param>
    /// <remarks>
    /// The lookup uses the same encounter data ALM uses (wild slots, static encounters, gifts and
    /// events), so the answer is the one PKHeX gives and not a guess about the games. The forms
    /// the species comes from are looked for too, because a Pokemon that evolves from a real
    /// encounter is legal even when its own species has none.
    /// </remarks>
    public static IReadOnlyList<EntityContext> GenerationsWithAnyEncounter(PKM pokemon)
    {
        var species = new List<ushort> { pokemon.Species };
        foreach (var previous in PreEvolutions(pokemon))
        {
            if (!species.Contains(previous)) species.Add(previous);
        }

        var result = new List<EntityContext>();
        foreach (var context in AllContexts())
        {
            foreach (var version in EncounterVersions(context))
            {
                var found = false;
                foreach (var one in species)
                {
                    if (!HasEncounter(pokemon, one, context, version)) continue;
                    found = true;
                    break;
                }

                if (!found) continue;
                if (!result.Contains(context)) result.Add(context);
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// The reason a pokemon could not be legalized even though its species does have an encounter
    /// somewhere (S13).
    /// </summary>
    /// <param name="pokemon">The pokemon that was received.</param>
    /// <param name="targetContext">The generation the pokemon has to be legalized into.</param>
    /// <param name="tried">The versions that were tried, in every generation.</param>
    /// <param name="exists">The generations in which the species has an encounter.</param>
    /// <param name="reasons">The reasons ALM reported about the pokemon.</param>
    /// <param name="budget">The budget of the fallback, to say whether it ran out.</param>
    /// <remarks>
    /// The "impossible" word is reserved for the species that have no encounter at all, so this
    /// message says what the engine did: it looked in every generation and did not find an
    /// encounter that fits the pokemon.
    /// </remarks>
    private static string NotFoundReason(PKM pokemon, EntityContext targetContext,
        IReadOnlyList<GameVersion> tried, IReadOnlyList<EntityContext> exists, string reasons,
        SearchBudget budget)
    {
        var species = pokemon.Species;
        var name = SpeciesName(species);

        return
            $"Auto-legalization failed: this Pokemon could not be matched with an encounter: {name} (species #{species}) (the versions that were tried were: {string.Join(", ", tried)}). The species (or one of the forms it comes from) has an encounter in: {string.Join(", ", exists)}. The generation of the file is {targetContext} and the engine looked for the encounter in every other generation too, but the search ran out of its budget ({budget.Attempts} attempts, {budget.Elapsed.TotalSeconds:F1} s). Reasons: {reasons}";
    }

    /// <summary>
    /// The reason the search ran out of its budget.
    /// </summary>
    private static string BudgetReason(EntityContext targetContext, SearchBudget budget)
    {
        return
            $"Auto-legalization failed: the search for an encounter ran out of the budget of the engine ({budget.Attempts} attempts, {budget.Elapsed.TotalSeconds:F1} s) while the generation {targetContext} was being looked at.";
    }

    /// <summary>
    /// The time the search of the generation of the file may take.
    /// </summary>
    /// <remarks>
    /// The API has a timeout of 30 seconds per request, so the search of the encounters has to
    /// be bounded. The budget of the generation of the file and the budget of the fallback to the
    /// other generations are apart, and both of them are inside the timeout of the API.
    /// </remarks>
    public static readonly TimeSpan SearchTimeBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The number of calls to ALM the search of the generation of the file may do.
    /// </summary>
    public const int MaxSearchAttempts = 400;

    /// <summary>
    /// The time the fallback to the other generations may take.
    /// </summary>
    public static readonly TimeSpan FallbackTimeBudget = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The number of calls to ALM the fallback to the other generations may do.
    /// </summary>
    public const int MaxFallbackAttempts = 200;

    /// <summary>
    /// How many times the previous form of the Pokemon is tried in the fallback.
    /// </summary>
    /// <remarks>
    /// The fallback looks at many generations, so the number of attempts of the previous form is
    /// smaller there than in the generation of the file: the search has to fit in the budget.
    /// </remarks>
    private const int MaxFallbackPreEvolutionAttempts = 4;

    /// <summary>
    /// The budget of a search: the time it may take and the number of calls to ALM it may do.
    /// </summary>
    /// <remarks>
    /// The budget is what keeps the answer of the engine inside the timeout of the API. When it
    /// runs out, the search stops and the reason is reported: a failure that is never silent.
    /// </remarks>
    private sealed class SearchBudget
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly TimeSpan _maxTime;
        private readonly int _maxAttempts;

        /// <summary>The number of attempts that have been used.</summary>
        public int Attempts { get; private set; }

        /// <summary>Whether the budget has run out.</summary>
        public bool Exhausted { get; private set; }

        /// <summary>The time the search has been running for.</summary>
        public TimeSpan Elapsed => _watch.Elapsed;

        public SearchBudget(TimeSpan maxTime, int maxAttempts)
        {
            _maxTime = maxTime;
            _maxAttempts = maxAttempts;
        }

        /// <summary>
        /// Uses one attempt of the budget.
        /// </summary>
        /// <returns>Whether the attempt is allowed.</returns>
        public bool TryUse()
        {
            if (Exhausted) return false;

            Attempts++;
            if (Attempts > _maxAttempts || _watch.Elapsed > _maxTime)
            {
                Exhausted = true;
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// The generations in which a species has an encounter of some kind.
    /// </summary>
    /// <param name="pokemon">The pokemon whose species is looked for.</param>
    /// <remarks>
    /// The lookup uses the same encounter data ALM uses (wild slots, static encounters, gifts and
    /// events), so the answer is the one PKHeX gives and not a guess about the games.
    /// </remarks>
    public static IReadOnlyList<EntityContext> GenerationsWithEncounter(PKM pokemon)
    {
        var contexts = new List<EntityContext>();

        foreach (var context in AllContexts())
        {
            foreach (var version in EncounterVersions(context))
            {
                if (!HasEncounter(pokemon, pokemon.Species, context, version)) continue;

                if (!contexts.Contains(context)) contexts.Add(context);
                break;
            }
        }

        return contexts;
    }

    /// <summary>
    /// The generations the engine knows about.
    /// </summary>
    private static IEnumerable<EntityContext> AllContexts()
    {
        return new[]
        {
            EntityContext.Gen9a, EntityContext.Gen9, EntityContext.Gen8b, EntityContext.Gen8a,
            EntityContext.Gen8, EntityContext.Gen7b, EntityContext.Gen7, EntityContext.Gen6,
            EntityContext.Gen5, EntityContext.Gen4, EntityContext.Gen3, EntityContext.Gen2,
            EntityContext.Gen1
        };
    }

    /// <summary>
    /// Tells whether a species has an encounter in a version of a generation.
    /// </summary>
    public static bool HasEncounter(PKM source, ushort species, EntityContext context, GameVersion version)
    {
        try
        {
            var template = EncounterTemplate(source, species, context, version);
            if (template == null) return false;

            var info = _GetTrainerInfo(template, version, context);
            var encounters = EncounterMovesetGenerator.GenerateEncounters(template, info,
                ReadOnlyMemory<ushort>.Empty, new[] { version });

            foreach (var encounter in encounters)
            {
                if (EncounterSpecies(encounter) == species) return true;
            }
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
        }

        return false;
    }

    /// <summary>
    /// The species of an encounter.
    /// </summary>
    /// <remarks>
    /// The encounters of an evolution are of the species it evolves from (an egg of Pichu is an
    /// encounter of Pichu), so the species of the encounter is not always the one that was asked
    /// for. The encounter is still an encounter of the species when the one that was asked for is
    /// in its evolution chain.
    /// </remarks>
    private static ushort EncounterSpecies(IEncounterable encounter)
    {
        var property = encounter.GetType().GetProperty("Species");
        if (property == null) return 0;

        try
        {
            return (ushort)property.GetValue(encounter)!;
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            return 0;
        }
    }

    /// <summary>
    /// The name of a species, or its number when the names are not available.
    /// </summary>
    private static string SpeciesName(ushort species)
    {
        try
        {
            var names = GameInfo.Strings.Species;
            if (names != null && species < names.Count && !string.IsNullOrWhiteSpace(names[species]))
                return names[species];
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
        }

        return $"#{species}";
    }

    /// <summary>
    /// The destination versions to try, in order of preference.
    /// </summary>
    private static IEnumerable<GameVersion> CandidateVersions(PKM pokemon, EntityContext targetContext,
        GameVersion? requestedVersion)
    {
        var seen = new HashSet<GameVersion>();

        // The version the caller asks for comes first: it is the version of the save the pokemon
        // belongs to, so it is the most likely to match its encounters.
        if (requestedVersion.HasValue && requestedVersion.Value.Context == targetContext &&
            seen.Add(requestedVersion.Value))
        {
            yield return requestedVersion.Value;
        }

        // Then the versions of the generation of the file, newest first.
        if (VersionsByContext.TryGetValue(targetContext, out var versions))
        {
            foreach (var version in versions)
            {
                if (seen.Add(version)) yield return version;
            }
        }

        // And finally the version of the file itself, which may not be in the table above.
        var own = pokemon.Version;
        if (own != GameVersion.Any && own.Context == targetContext && seen.Add(own))
        {
            yield return own;
        }
    }

    /// <summary>
    /// The attempts to make with a version of the destination generation.
    /// </summary>
    /// <param name="pokemon">The pokemon that was received.</param>
    /// <param name="targetContext">The generation the pokemon has to be legalized into.</param>
    /// <param name="version">The version of the generation being tried.</param>
    /// <remarks>
    /// The pokemon is first tried as it was received, so the Pokemon that already works is not
    /// touched. When its version does not belong to the destination generation (a Pokemon of Gold
    /// inside a .pk7), the same pokemon is tried again with the version of the generation being
    /// tried: the encounter of a Pokemon of another generation does not exist in the destination
    /// one, so the version has to be corrected before the encounter is looked for.
    /// </remarks>
    private static IEnumerable<PKM> VersionedAttempts(PKM pokemon, EntityContext targetContext,
        GameVersion version)
    {
        yield return pokemon;

        var corrected = RepairVersionForGeneration(pokemon, targetContext, version);
        if (!ReferenceEquals(corrected, pokemon)) yield return corrected;
    }

    /// <summary>
    /// Gives the pokemon a version of the destination generation when the one it has does not
    /// belong to it.
    /// </summary>
    /// <param name="source">The pokemon that was received. It is never modified.</param>
    /// <param name="targetContext">The generation the pokemon has to be legalized into.</param>
    /// <param name="version">The version of the destination generation to give to the pokemon.</param>
    /// <returns>A copy of the pokemon with the version corrected, or the pokemon itself when its
    /// version already belongs to the destination generation.</returns>
    /// <remarks>
    /// A .pk7 that says it comes from Gold is a Pokemon of the Virtual Console: its SID has to be
    /// 0, it needs the memory of the geolocation and it cannot be in the hands of its original
    /// trainer. None of that can be resolved while the version says Gen 2, because the encounter
    /// of the Pokemon does not exist in Gen 7 at all.
    /// </remarks>
    public static PKM RepairVersionForGeneration(PKM source, EntityContext targetContext,
        GameVersion version)
    {
        if (source.Version == GameVersion.Any) return source;
        if (source.Version.Context == targetContext) return source;
        if (version.Context != targetContext) return source;

        var candidate = ClonePokemon(source);
        if (candidate == null) return source;

        candidate.Version = version;
        return candidate;
    }

    /// <summary>
    /// Takes the reasons out of the failure message of an attempt.
    /// </summary>
    private static string ExtractReasons(string? failure)
    {
        if (string.IsNullOrEmpty(failure)) return "unknown";

        const string marker = "Reasons:";
        var index = failure.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return failure;

        return failure[(index + marker.Length)..].Trim();
    }

    /// <summary>
    /// Legalizes a pokemon into the generation it already belongs to.
    /// </summary>
    public static PKM? AutoLegalize(PKM pokemon, GameVersion? overriddenVersion = null)
    {
        var targetContext = ResolveLegalizationContext(pokemon);
        if (targetContext == null) return null;

        return AutoLegalize(pokemon, targetContext.Value, ResolveDestinationVersion(pokemon, targetContext.Value, overriddenVersion),
            out _);
    }

    /// <summary>
    /// Legalizes a pokemon into the given generation and version.
    /// </summary>
    public static PKM? AutoLegalize(PKM pokemon, EntityContext targetContext, GameVersion targetVersion,
        out string? failureReason)
    {
        var legalized = TryLegalize(pokemon, targetContext, targetVersion,
            new SearchBudget(SearchTimeBudget, MaxSearchAttempts), out failureReason);
        if (legalized == null) return null;

        var restored = RestoreMinimal(pokemon, legalized);
        return PreserveShiny(pokemon, restored);
    }

    /// <summary>
    /// Asks ALM to regenerate a pokemon.
    /// </summary>
    /// <returns>The regenerated pokemon, or null when ALM could not produce one.</returns>
    /// <remarks>
    /// ALM is given a copy of the pokemon, because it may modify the one it receives. The result is
    /// only accepted when it is a legal pokemon of the same species and form: when ALM cannot match
    /// the origin encounter of the pokemon it is given, it may hand back a legal pokemon of another
    /// species, which is not the pokemon the caller sent.
    /// </remarks>
    private static PKM? TryLegalize(PKM source, EntityContext targetContext, GameVersion targetVersion,
        SearchBudget budget, out string? failureReason)
    {
        failureReason = null;

        // Every call to ALM is an attempt of the budget: when the budget runs out the search
        // stops and the reason is reported.
        if (!budget.TryUse())
        {
            failureReason = BudgetReason(targetContext, budget);
            return null;
        }

        var info = _GetTrainerInfo(source, targetVersion, targetContext);
        var working = ClonePokemon(source) ?? source;
        var analysis = CheckLegality(working);

        PKM? legalized;
        try
        {
            ConfigureAutoMod(targetContext);

            // ALM 26.10.18 replaces RegenTemplate + GetLegalFromTemplateTimeout with
            // Legalizer.Legalize(trainer, pokemon, analysis).
            legalized = Legalizer.Legalize(info, working, analysis);
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            var reason = e is AggregateException aggregate && aggregate.InnerException != null
                ? aggregate.InnerException
                : e;
            failureReason = $"Auto-legalization failed: {reason.GetType().Name}: {reason.Message.Replace("\r", " ").Replace("\n", " ")}";
            return null;
        }

        if (legalized == null)
        {
            failureReason =
                $"Auto-legalization failed: ALM could not regenerate a legal Pokemon for this file (target generation {targetContext}, version {targetVersion}).";
            return null;
        }

        if (legalized.Species != source.Species || legalized.Form != source.Form)
        {
            failureReason =
                $"Auto-legalization failed: ALM regenerated a Pokemon of another species ({source.Species} -> {legalized.Species}) instead of the one that was received (target generation {targetContext}, version {targetVersion}).";
            return null;
        }

        // The trainer data of the regenerated Pokemon has to match the original one, otherwise the
        // result is not the Pokemon the caller sent. When writing it makes the Pokemon illegal, the
        // trainer data ALM generated is kept instead.
        if (!TryApplyTrainerData(legalized, info))
        {
            var reasons = CheckLegality(legalized).Report().Split('\n').Where(l => l.Length > 0).ToArray();
            failureReason =
                $"Auto-legalization failed: ALM did not regenerate a legal Pokemon (target generation {targetContext}, version {targetVersion}). Reasons: {string.Join(" | ", reasons)}";
            return null;
        }

        if (!CheckLegality(legalized).Valid)
        {
            var reasons = CheckLegality(legalized).Report().Split('\n').Where(l => l.Length > 0).ToArray();
            failureReason =
                $"Auto-legalization failed: ALM did not regenerate a legal Pokemon (target generation {targetContext}, version {targetVersion}). Reasons: {string.Join(" | ", reasons)}";
            return null;
        }

        return legalized;
    }

    /// <summary>
    /// Tells ALM which game versions it may use to look for encounters.
    /// </summary>
    /// <remarks>
    /// ALM 26.10.18 builds its list of game versions from the range of versions the file allows
    /// (PKHeX.Core <c>GameUtil.GetVersionsWithinRange</c>). For a Gen 9 file that range also contains
    /// the not-yet-released games "Pokémon Winds" (WI) and "Pokémon Waves" (WA), whose
    /// <see cref="GameVersion.Context"/> is <see cref="EntityContext.None"/>. PKHeX.Core then asks for
    /// the evolution group of that context-less version and throws
    /// <c>ArgumentOutOfRangeException: Parameter 'context'. Actual value was None.</c>
    /// (PKHeX.Core.EvolutionGroupUtil.GetGroup).
    /// <br />
    /// Restricting ALM to the versions of the destination generation keeps that version out of the
    /// list, so the encounters are looked up in a real game.
    /// </remarks>
    public static void ConfigureAutoMod(EntityContext targetContext)
    {
        APILegality.GameVersionPriority = GameVersionPriorityType.PriorityOrder;
        APILegality.PriorityOrder = new List<GameVersion>(
            GameUtil.GetVersionsInGeneration(targetContext, DefaultVersionFor(targetContext))
                .Where(v => v.Context != EntityContext.None)
                .ToArray());
    }

    /// <summary>
    /// The met date of the games, by generation and version.
    /// </summary>
    /// <remarks>
    /// A pokemon cannot be met before the game it belongs to was released, so the date used to
    /// repair an impossible met date is the release date of the game (or a date close to it).
    /// </remarks>
    private static readonly Dictionary<(EntityContext Context, GameVersion Version), DateOnly> PlausibleMetDates = new()
    {
        [(EntityContext.Gen7, GameVersion.USUM)] = new(2017, 11, 17),
        [(EntityContext.Gen7, GameVersion.SM)] = new(2016, 11, 23),
        [(EntityContext.Gen7, GameVersion.ORAS)] = new(2016, 10, 21),
        [(EntityContext.Gen7, GameVersion.XY)] = new(2013, 10, 12),
        [(EntityContext.Gen7, GameVersion.B2W2)] = new(2011, 3, 6),
        [(EntityContext.Gen7, GameVersion.BW)] = new(2010, 9, 6),
        [(EntityContext.Gen6, GameVersion.ORAS)] = new(2014, 11, 21),
        [(EntityContext.Gen5, GameVersion.B2W2)] = new(2010, 3, 6),
        [(EntityContext.Gen4, GameVersion.HGSS)] = new(2009, 9, 12),
        [(EntityContext.Gen8, GameVersion.SWSH)] = new(2020, 3, 19),
        [(EntityContext.Gen9, GameVersion.SV)] = new(2022, 11, 18),
    };

    /// <summary>
    /// The met date used for each generation when the version is not in the table above.
    /// </summary>
    private static readonly Dictionary<EntityContext, DateOnly> PlausibleMetDatesByGeneration = new()
    {
        [EntityContext.Gen1] = new(1996, 2, 27),
        [EntityContext.Gen2] = new(1999, 10, 21),
        [EntityContext.Gen3] = new(2003, 1, 28),
        [EntityContext.Gen4] = new(2009, 9, 12),
        [EntityContext.Gen5] = new(2010, 3, 6),
        [EntityContext.Gen6] = new(2014, 11, 21),
        [EntityContext.Gen7] = new(2017, 11, 17),
        [EntityContext.Gen7b] = new(2017, 12, 1),
        [EntityContext.Gen8] = new(2020, 3, 19),
        [EntityContext.Gen8a] = new(2021, 1, 28),
        [EntityContext.Gen8b] = new(2021, 11, 19),
        [EntityContext.Gen9] = new(2022, 11, 18),
        [EntityContext.Gen9a] = new(2025, 10, 16),
    };

    /// <summary>
    /// The date a pokemon of the destination generation can have been met on.
    /// </summary>
    public static DateOnly PlausibleMetDate(EntityContext context, GameVersion version)
    {
        if (PlausibleMetDates.TryGetValue((context, version), out var exact)) return exact;
        if (PlausibleMetDatesByGeneration.TryGetValue(context, out var byGeneration)) return byGeneration;

        return context.Generation switch
        {
            1 => new DateOnly(1996, 2, 27),
            2 => new DateOnly(1999, 10, 21),
            3 => new DateOnly(2003, 1, 28),
            4 => new DateOnly(2009, 9, 12),
            5 => new DateOnly(2010, 3, 6),
            6 => new DateOnly(2014, 11, 21),
            7 => new DateOnly(2017, 11, 17),
            8 => new DateOnly(2020, 3, 19),
            9 => new DateOnly(2022, 11, 18),
            _ => new DateOnly(2022, 11, 18),
        };
    }

    /// <summary>
    /// Tells whether a met date is a date that can exist (the year 1 and the impossible days of
    /// the calendar are not).
    /// </summary>
    public static bool IsValidMetDate(DateOnly? date)
    {
        if (date == null) return false;

        var value = date.Value;
        if (value.Year < 2 || value.Month < 1 || value.Month > 12 || value.Day < 1 || value.Day > 31)
            return false;

        return value <= DateOnly.FromDateTime(DateTime.Now);
    }

    /// <summary>
    /// The reasons ALM reports about a pokemon.
    /// </summary>
    /// <remarks>
    /// The line that says the pokemon is legal is left out: only the reasons are of interest here.
    /// </remarks>
    public static List<string> InvalidReasons(PKM pokemon)
    {
        return CheckLegality(pokemon).Report()
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("Legal!", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// The action a reason of ALM points at.
    /// </summary>
    /// <remarks>
    /// The classification is made with the text of the reason, never with a guess about the pokemon:
    /// each reason of ALM is mapped to the repair that fixes it, or to
    /// <see cref="LegalityRepair.None"/> when there is nothing to repair (ALM regenerates that data
    /// on its own).
    /// </remarks>
    public static LegalityRepair ClassifyReason(string reason)
    {
        if (reason.Contains("Met Date is not a valid calendar date", StringComparison.OrdinalIgnoreCase))
            return LegalityRepair.MetDate;

        if (InvalidMoveIndex(reason) != null)
            return LegalityRepair.Move;

        if (reason.Contains("Hidden Ability mismatch for encounter type", StringComparison.OrdinalIgnoreCase))
            return LegalityRepair.Ability;

        if (reason.Contains("Mystery Gift cannot be received by this version", StringComparison.OrdinalIgnoreCase))
            return LegalityRepair.MetLocation;

        if (reason.Contains("HT Language is missing", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("Current handler", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("Memory: Not cleared properly", StringComparison.OrdinalIgnoreCase))
            return LegalityRepair.HandlingTrainer;

        // El SID del entrenador original tiene que ser 0 en un Pokemon que viene de la Virtual
        // Console: el Pokemon llega con el SID de la partida de origen.
        if (reason.Contains("SID16 should be 0", StringComparison.OrdinalIgnoreCase))
            return LegalityRepair.TrainerId;

        // La memoria de la geolocalizacion del entrenador original tiene que estar presente.
        if (reason.Contains("GeoLocation Memory", StringComparison.OrdinalIgnoreCase))
            return LegalityRepair.GeoMemory;

        // La amistad del entrenador original tiene que ser la amistad base de la especie cuando
        // el encuentro es un evento.
        if (reason.Contains("does not match base friendship", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("Event OT Friendship", StringComparison.OrdinalIgnoreCase))
            return LegalityRepair.Friendship;

        if (reason.Contains("Unable to match an encounter from origin game", StringComparison.OrdinalIgnoreCase))
            return LegalityRepair.Origin;

        // Everything else (the PID and the EC that have to match, the PID and the nature that have
        // to match, the IVs that have to be perfect, ...) is data ALM regenerates on its own.
        return LegalityRepair.None;
    }

    /// <summary>
    /// The slot of the move a reason points at, when the reason is about a move.
    /// </summary>
    /// <remarks>
    /// The reason about a move of the pokemon is "Invalid Move 3: Invalid Move.". The number of
    /// the move is the one-based slot it occupies. The reasons about the moves a pokemon relearns
    /// ("Invalid Relearn Move 3: Invalid Move. Expected: (None)") are about another field and are
    /// not a move of the pokemon.
    /// </remarks>
    public static int? InvalidMoveIndex(string reason)
    {
        var text = reason.Trim();
        const string prefix = "Invalid: ";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = text[prefix.Length..].Trim();

        const string movePrefix = "Invalid Move ";
        if (!text.StartsWith(movePrefix, StringComparison.OrdinalIgnoreCase)) return null;

        var start = movePrefix.Length;
        var end = start;
        while (end < text.Length && char.IsDigit(text[end])) end++;
        if (end == start) return null;

        return int.Parse(text[start..end]);
    }

    /// <summary>
    /// Applies the repair each reason points at, and nothing else.
    /// </summary>
    /// <param name="source">The pokemon that was received. It is never modified.</param>
    /// <param name="targetContext">The generation the pokemon has to be legalized into.</param>
    /// <param name="version">The game version the legalization is tried with.</param>
    /// <param name="reasons">The reasons ALM reports about the pokemon.</param>
    /// <param name="changes">The list of the changes applied, in a readable form.</param>
    /// <returns>A copy of the pokemon with the repairs applied.</returns>
    /// <remarks>
    /// The reason "Unable to match an encounter from origin game" is not repaired here: it needs the
    /// whole origin of the pokemon to be rebuilt, which is the last resort and not a repair.
    /// </remarks>
    public static PKM ApplyDirectedRepairs(PKM source, EntityContext targetContext, GameVersion version,
        IReadOnlyList<string> reasons, List<string> changes)
    {
        var current = ClonePokemon(source) ?? source;

        foreach (var reason in reasons)
        {
            current = ClassifyReason(reason) switch
            {
                LegalityRepair.MetDate => RepairMetDate(current, targetContext, version, reason, changes),
                LegalityRepair.Move => RepairMove(current, reason, changes),
                LegalityRepair.Ability => RepairAbility(current, reason, changes),
                LegalityRepair.MetLocation => RepairMetLocation(current, targetContext, reason, changes),
                LegalityRepair.HandlingTrainer => RepairHandlingTrainer(current, reason, changes),
                LegalityRepair.TrainerId => RepairTrainerId(current, reason, changes),
                LegalityRepair.GeoMemory => RepairGeoMemory(current, reason, changes),
                LegalityRepair.Friendship => RepairFriendship(current, reason, changes),
                _ => current,
            };
        }

        return current;
    }

    /// <summary>
    /// Tells whether a repair is worth keeping.
    /// </summary>
    /// <param name="before">The pokemon before the repair.</param>
    /// <param name="after">The pokemon with the repair applied.</param>
    /// <param name="targetReason">The reason the repair was chosen for.</param>
    /// <remarks>
    /// A repair is only kept when the reason it points at is gone, the reasons of the pokemon are
    /// fewer after it and no new reason appears: a repair that does not fix what the message points
    /// at, or that breaks something else, is discarded.
    /// </remarks>
    private static bool IsRepairAccepted(PKM before, PKM after, string targetReason)
    {
        var beforeReasons = new HashSet<string>(InvalidReasons(before));
        var afterReasons = new HashSet<string>(InvalidReasons(after));

        if (afterReasons.Count >= beforeReasons.Count) return false;
        if (!afterReasons.IsSubsetOf(beforeReasons)) return false;

        return !afterReasons.Contains(targetReason.Trim());
    }

    /// <summary>
    /// Repairs the met date of a pokemon whose date is not a date that can exist.
    /// </summary>
    private static PKM RepairMetDate(PKM source, EntityContext targetContext, GameVersion version,
        string reason, List<string> changes)
    {
        if (IsValidMetDate(source.MetDate)) return source;

        var before = source.MetDate;
        var fixedDate = PlausibleMetDate(targetContext, version);

        var candidate = ClonePokemon(source);
        if (candidate == null) return source;

        candidate.MetDate = fixedDate;
        if (!IsRepairAccepted(source, candidate, reason)) return source;

        changes.Add($"met date {FormatMetDate(before)} -> {FormatMetDate(fixedDate)}");
        return candidate;
    }

    /// <summary>
    /// Repairs the move a reason points at.
    /// </summary>
    /// <remarks>
    /// Only the move of the reason is touched. Its PP is given back first (a move with no PP left
    /// cannot be a move of a real encounter), and when that is not enough the move is replaced with
    /// one the species can use at that level. The other moves, the date and the location are left
    /// alone.
    /// </remarks>
    private static PKM RepairMove(PKM source, string reason, List<string> changes)
    {
        var index = InvalidMoveIndex(reason);
        if (index == null) return source;

        var slot = index.Value - 1;
        if (slot < 0 || slot > 3) return source;

        var move = source.GetMove(slot);

        // The PP of the move.
        if (move != 0)
        {
            var maxPp = source.GetBasePP(move);
            if (GetMovePP(source, slot) != maxPp)
            {
                var candidate = ClonePokemon(source);
                if (candidate != null)
                {
                    SetMovePP(candidate, slot, maxPp);
                    if (IsRepairAccepted(source, candidate, reason))
                    {
                        changes.Add($"move {slot + 1} ({MoveName(move)}) PP {GetMovePP(source, slot)} -> {maxPp}");
                        return candidate;
                    }
                }
            }
        }

        // The move itself: one the species can use at that level.
        foreach (var replacement in MoveCandidates(source, slot))
        {
            var candidate = ClonePokemon(source);
            if (candidate == null) break;

            candidate.SetMove(slot, replacement);
            SetMovePP(candidate, slot, candidate.GetBasePP(replacement));
            if (!IsRepairAccepted(source, candidate, reason)) continue;

            changes.Add($"move {slot + 1} ({MoveName(move)}) -> {MoveName(replacement)}");
            return candidate;
        }

        return source;
    }

    /// <summary>
    /// The moves that can replace an invalid one, the most common ones first.
    /// </summary>
    private static IEnumerable<ushort> MoveCandidates(PKM pokemon, int slot)
    {
        var current = pokemon.GetMove(slot);

        ushort[] common = { 33, 1, 45, 39, 10, 74, 52, 55, 98, 29, 16, 84, 86 };
        foreach (var move in common)
        {
            if (move == current) continue;
            yield return move;
        }

        var max = pokemon.MaxMoveID;
        for (var move = 1; move <= max; move++)
        {
            if ((ushort)move == current) continue;
            yield return (ushort)move;
        }
    }

    /// <summary>
    /// Repairs the ability slot of a pokemon.
    /// </summary>
    /// <remarks>
    /// The slot of the ability is the one thing that is touched: the date, the location and the
    /// moves are left alone.
    /// </remarks>
    private static PKM RepairAbility(PKM source, string reason, List<string> changes)
    {
        var personal = source.PersonalInfo;
        for (var slot = 0; slot < personal.AbilityCount; slot++)
        {
            var candidate = ClonePokemon(source);
            if (candidate == null) break;

            candidate.AbilityNumber = (byte)slot;
            candidate.Ability = personal.GetAbilityAtIndex((byte)slot);
            if (!IsRepairAccepted(source, candidate, reason)) continue;

            changes.Add($"ability {source.Ability} (slot {source.AbilityNumber}) -> {candidate.Ability} (slot {slot})");
            return candidate;
        }

        return source;
    }

    /// <summary>
    /// Repairs the met location of a pokemon that cannot be received by its version.
    /// </summary>
    /// <remarks>
    /// The met location of a mystery gift is only a real location in the versions where the gift
    /// was handed out, so the repair gives the pokemon a version of its generation where its met
    /// location really exists. When that is not possible, the met location is replaced with a
    /// location the version has.
    /// </remarks>
    private static PKM RepairMetLocation(PKM source, EntityContext targetContext, string reason,
        List<string> changes)
    {
        foreach (var version in GameVersionsOfGeneration(targetContext))
        {
            var candidate = ClonePokemon(source);
            if (candidate == null) break;

            candidate.Version = version;
            if (!IsRepairAccepted(source, candidate, reason)) continue;

            changes.Add($"version {source.Version} -> {version} (the met location {source.MetLocation} is only a real location in that version)");
            return candidate;
        }

        foreach (var location in PlausibleMetLocations())
        {
            if (location == source.MetLocation) continue;

            var candidate = ClonePokemon(source);
            if (candidate == null) break;

            candidate.MetLocation = location;
            if (!IsRepairAccepted(source, candidate, reason)) continue;

            changes.Add($"met location {source.MetLocation} -> {location}");
            return candidate;
        }

        return source;
    }

    /// <summary>
    /// The versions of a generation, the one of the pokemon first.
    /// </summary>
    private static IEnumerable<GameVersion> GameVersionsOfGeneration(EntityContext context)
    {
        var seen = new HashSet<GameVersion>();

        foreach (var version in GameUtil.GetVersionsInGeneration(context, DefaultVersionFor(context)))
        {
            if (version.Context == EntityContext.None) continue;
            if (!seen.Add(version)) continue;

            yield return version;
        }
    }

    /// <summary>
    /// The met locations a pokemon can have been met in.
    /// </summary>
    /// <remarks>
    /// They are the locations ALM itself uses for the encounters of the generations: the repair
    /// keeps one of them only when the reasons of the pokemon get better with it.
    /// </remarks>
    private static ushort[] PlausibleMetLocations()
    {
        return new ushort[] { 6, 8, 20, 78, 100, 144 };
    }

    /// <summary>
    /// Repairs the data of the handling trainer of a pokemon.
    /// </summary>
    /// <remarks>
    /// The name, the language and the current handler of the handling trainer are fixed, and the
    /// memories are cleared. Nothing else of the pokemon is touched.
    /// </remarks>
    private static PKM RepairHandlingTrainer(PKM source, string reason, List<string> changes)
    {
        var candidate = ClonePokemon(source);
        if (candidate == null) return source;

        var changed = new List<string>();

        if (string.IsNullOrEmpty(candidate.HandlingTrainerName) &&
            !string.IsNullOrEmpty(candidate.OriginalTrainerName))
        {
            if (TryApplyHandlingTrainer(candidate, candidate.OriginalTrainerName))
                changed.Add($"HT name '' -> '{candidate.HandlingTrainerName}'");
        }

        try
        {
            SimpleEdits.SetHTLanguage(candidate, (byte)candidate.Language);
            changed.Add("HT language -> the language of the original trainer");
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
        }

        foreach (var handler in new byte[] { 0, 1 })
        {
            if (candidate.CurrentHandler == handler) continue;

            var attempt = ClonePokemon(candidate);
            if (attempt == null) break;

            attempt.CurrentHandler = handler;
            if (!IsRepairAccepted(source, attempt, reason)) continue;

            candidate = attempt;
            changed.Add($"current handler {source.CurrentHandler} -> {handler}");
            break;
        }

        try
        {
            MemoryApplicator.ClearMemories(candidate);
            changed.Add("memories cleared");
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
        }

        if (changed.Count == 0) return source;
        if (!IsRepairAccepted(source, candidate, reason)) return source;

        changes.AddRange(changed);
        return candidate;
    }

    /// <summary>
    /// Repairs the SID of the original trainer when the check says it has to be 0.
    /// </summary>
    /// <remarks>
    /// A Pokemon that comes from the Virtual Console has no SID (the game it was caught in does
    /// not have one), so the SID of the original trainer has to be 0. The SID is only written in
    /// the generations in which the PID does not depend on it (Gen 6 and later): in the older ones
    /// the PID and the EC are calculated with the SID, so writing it would break the pokemon
    /// instead of fixing it.
    /// </remarks>
    private static PKM RepairTrainerId(PKM source, string reason, List<string> changes)
    {
        if (source.SID16 == 0) return source;
        if (source.Format < 6) return source;

        var candidate = ClonePokemon(source);
        if (candidate == null) return source;

        candidate.SID16 = 0;
        if (!IsRepairAccepted(source, candidate, reason)) return source;

        changes.Add($"SID16 {source.SID16} -> 0");
        return candidate;
    }

    /// <summary>
    /// Repairs the memory of the geolocation of the original trainer when it is not there.
    /// </summary>
    /// <remarks>
    /// A Pokemon that comes from the Virtual Console carries the memory of the country and the
    /// region where it was caught. When the memory is empty the check says it is not there, so a
    /// country and a region of that country are given to it. The pair has to exist and it has to
    /// be valid for the region of the console of the pokemon.
    /// </remarks>
    private static PKM RepairGeoMemory(PKM source, string reason, List<string> changes)
    {
        if (source is not IGeoTrack geo) return source;
        if (geo.Geo1_Country != 0) return source;

        var (country, region) = PlausibleGeoMemory(source);
        if (country == 0) return source;

        var candidate = ClonePokemon(source);
        if (candidate is not IGeoTrack candidateGeo) return source;

        candidateGeo.Geo1_Country = country;
        candidateGeo.Geo1_Region = region;
        if (!IsRepairAccepted(source, candidate, reason)) return source;

        changes.Add($"geolocation memory {geo.Geo1_Country}/{geo.Geo1_Region} -> {country}/{region}");
        return candidate;
    }

    /// <summary>
    /// The country and the region of the memory of the geolocation of a pokemon.
    /// </summary>
    /// <remarks>
    /// The country has to be one of the countries of the region of the console of the pokemon and
    /// the region has to be one of the regions of that country, otherwise the check complains
    /// about a different reason.
    /// </remarks>
    private static (byte Country, byte Region) PlausibleGeoMemory(PKM source)
    {
        var consoleRegion = (source as IRegionOrigin)?.ConsoleRegion ?? 0;

        for (byte country = 1; country <= 12; country++)
        {
            if (!Locale3DS.IsConsoleRegionCountryValid(consoleRegion, country)) continue;

            for (byte region = 1; region <= 16; region++)
            {
                if (!GeoLocation.GetIsCountryRegionExist(country, region)) continue;
                return (country, region);
            }
        }

        return (0, 0);
    }

    /// <summary>
    /// Repairs the friendship of the original trainer when it is not the base friendship of the
    /// species the encounter needs.
    /// </summary>
    /// <remarks>
    /// The friendship is the one PKHeX itself suggests for the encounter of the pokemon
    /// (<see cref="HistoryVerifier.GetSuggestedFriendshipCurrent"/>), so the value that is written
    /// is the one the check asks for.
    /// </remarks>
    private static PKM RepairFriendship(PKM source, string reason, List<string> changes)
    {
        var expected = SuggestedFriendship(source);
        if (expected == null) return source;
        if (source.CurrentFriendship == expected.Value) return source;

        var candidate = ClonePokemon(source);
        if (candidate == null) return source;

        candidate.CurrentFriendship = expected.Value;
        if (!IsRepairAccepted(source, candidate, reason)) return source;

        changes.Add($"friendship {source.CurrentFriendship} -> {expected.Value}");
        return candidate;
    }

    /// <summary>
    /// The friendship the original trainer of a pokemon should have, according to the encounter
    /// PKHeX finds for it.
    /// </summary>
    private static byte? SuggestedFriendship(PKM source)
    {
        try
        {
            var encounter = CheckLegality(source).EncounterMatch;
            if (encounter == null) return null;

            return HistoryVerifier.GetSuggestedFriendshipCurrent(source, encounter);
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            return null;
        }
    }

    /// <summary>
    /// Copies the original trainer into the handling trainer slot.
    /// </summary>
    /// <returns>Whether the write landed in the handling trainer slot without damaging the name
    /// of the original trainer.</returns>
    /// <remarks>
    /// Not every generation stores the name of the handling trainer, and in the ones that do not
    /// the write goes to a place that does not belong to it. The write is therefore verified and
    /// discarded when it does not end up in the handling trainer slot.
    /// </remarks>
    private static bool TryApplyHandlingTrainer(PKM pokemon, string originalTrainerName)
    {
        var originalName = pokemon.OriginalTrainerName;

        try
        {
            pokemon.HandlingTrainerName = originalTrainerName;
            pokemon.HandlingTrainerGender = pokemon.OriginalTrainerGender;
            pokemon.HandlingTrainerFriendship = 100;
            SimpleEdits.SetHTLanguage(pokemon, (byte)pokemon.Language);
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            return false;
        }

        return pokemon.HandlingTrainerName == originalTrainerName && pokemon.OriginalTrainerName == originalName;
    }

    /// <summary>
    /// The species the pokemon can come from: the ones that evolve into it.
    /// </summary>
    /// <remarks>
    /// Not every species has an encounter of its own in the generation it lives in (the Ivysaur
    /// does not appear in the wild in Alola: only the starter Bulbasaur does), but a Pokemon of
    /// that species is legal when it is the evolution of a real encounter of the species it comes
    /// from.
    /// <br />
    /// The candidates are the species with a lower number than the one that was received, the
    /// closest ones first (the previous form of the Pokemon is the one that evolves into it).
    /// </remarks>
    public static IReadOnlyList<ushort> PreEvolutionCandidates(PKM pokemon)
    {
        var result = new List<ushort>();

        foreach (var species in PreEvolutions(pokemon))
        {
            if (!result.Contains(species)) result.Add(species);
        }

        for (var species = (int)pokemon.Species - 1; species >= 1 && result.Count < MaxPreEvolutionCandidates; species--)
        {
            if (!result.Contains((ushort)species)) result.Add((ushort)species);
        }

        return result;
    }

    /// <summary>
    /// How many species are tried as the previous form of the Pokemon.
    /// </summary>
    private const int MaxPreEvolutionCandidates = 8;

    /// <summary>
    /// How many times the previous form of the Pokemon is tried in total.
    /// </summary>
    /// <remarks>
    /// Every attempt asks ALM to look for an encounter, so the number of attempts is bounded to
    /// keep the answer of the engine fast enough.
    /// </remarks>
    private const int MaxPreEvolutionAttempts = 12;

    /// <summary>
    /// Builds the pokemon again from a real encounter of one of the species it evolves from.
    /// </summary>
    /// <param name="original">The pokemon that was received.</param>
    /// <param name="targetContext">The generation the pokemon has to belong to.</param>
    /// <param name="version">The game version the encounter has to come from.</param>
    /// <param name="pre">The species the Pokemon is built from before it is evolved.</param>
    /// <param name="changes">The list of changes applied, in a readable form.</param>
    /// <returns>The legal pokemon, or null when no encounter could be used.</returns>
    /// <remarks>
    /// The Pokemon is built from the encounter of the previous form (the one ALM can legalize) and
    /// then evolved to the species that was received: the level is the lowest one that leaves the
    /// Pokemon legal (the evolution has a level of its own) and the nickname is the one of the
    /// Pokemon that was received (the nickname of a Pokemon has to match its species).
    /// </remarks>
    private static PKM? RebuildFromPreEvolution(PKM original, EntityContext targetContext,
        GameVersion version, ushort pre, SearchBudget budget, out List<string> changes,
        bool forEncounterSearch = false)
    {
        changes = new List<string>();

        // The template is the pokemon that was received with the repairs of its reasons applied
        // (the same ones the other attempts use): ALM needs a pokemon it can work with.
        var versioned = RepairVersionForGeneration(original, targetContext, version);
        var repaired = ApplyDirectedRepairs(versioned, targetContext, version,
            InvalidReasons(versioned), new List<string>());

        var template = ClonePokemon(repaired);
        if (template == null) return null;

        template.Species = pre;

        if (forEncounterSearch)
        {
            // ALM looks for an encounter whose ability is the one of the pokemon it is given and
            // whose moves are the ones of the pokemon, so an ability that does not belong to the
            // species or a move that the encounter does not have leaves it without any encounter.
            ApplyValidAbility(template);
            template.SetMoves(new ushort[] { 0, 0, 0, 0 });
            template.SetRelearnMoves(new ushort[] { 0, 0, 0, 0 });
            for (var i = 0; i < 4; i++) SetMovePP(template, i, 0);
        }

        var legalized = TryLegalize(template, targetContext, version, budget, out _);
        if (legalized == null) return null;

        var evolved = ClonePokemon(legalized);
        if (evolved == null) return null;

        evolved.Species = original.Species;
        evolved.Nickname = original.Nickname;

        // The ability of the encounter is not always one of the evolved species (an encounter of
        // a Weedle gives the ability of the Weedle), so the slots of the species are tried until
        // the pokemon is legal.
        var personal = evolved.PersonalInfo;
        for (var slot = 0; slot < personal.AbilityCount; slot++)
        {
            var attempt = ClonePokemon(evolved);
            if (attempt == null) break;

            attempt.AbilityNumber = (byte)slot;
            attempt.Ability = personal.GetAbilityAtIndex((byte)slot);

            // The level has to allow the evolution: the lowest level that leaves the Pokemon legal
            // is the one that changes the least.
            var level = LowestLegalLevel(attempt);
            if (level == null) continue;

            attempt.CurrentLevel = level.Value;
            attempt.EXP = Experience.GetEXP(level.Value, personal.EXPGrowth);
            if (!CheckLegality(attempt).Valid) continue;

            changes.AddRange(DescribeChanges(original, attempt));
            return attempt;
        }

        return null;
    }

    /// <summary>
    /// The species the pokemon can come from: the ones that evolve into it.
    /// </summary>
    /// <remarks>
    /// The chain of evolutions of the species is the one PKHeX builds for the pokemon, so the
    /// previous forms are the ones that come before it in that chain.
    /// </remarks>
    public static IReadOnlyList<ushort> PreEvolutions(PKM pokemon)
    {
        var result = new List<ushort>();

        try
        {
            var chain = CheckLegality(pokemon).Info.EvoChainsAllGens.Get(pokemon.Context);
            var dbg = new List<string>();
            foreach (var c in chain) dbg.Add(c.Species + "@" + c.LevelUpRequired);
            foreach (var evo in chain)
            {
                if (evo.Species == pokemon.Species) break;
                if (evo.Species != 0 && !result.Contains(evo.Species)) result.Add(evo.Species);
            }
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
        }

        result.Reverse();
        return result;
    }
    /// <summary>
    /// The lowest level that leaves a pokemon legal.
    /// </summary>
    /// <remarks>
    /// A Pokemon that evolves has a level of its own: the encounter it comes from is at a lower
    /// level, so the level is the one that makes the Pokemon legal.
    /// </remarks>
    private static byte? LowestLegalLevel(PKM pokemon)
    {
        var current = pokemon.CurrentLevel;
        if (CheckLegality(pokemon).Valid) return current;

        for (var level = 1; level <= 100; level++)
        {
            if (level == current) continue;

            var candidate = ClonePokemon(pokemon);
            if (candidate == null) break;

            candidate.CurrentLevel = (byte)level;
            if (CheckLegality(candidate).Valid) return (byte)level;
        }

        return null;
    }

    /// <summary>
    /// Gives a pokemon an ability that is one of its species.
    /// </summary>
    /// <param name="pokemon">The pokemon whose ability is fixed.</param>
    /// <remarks>
    /// ALM looks for an encounter whose ability is the one of the pokemon it is given, so a
    /// pokemon with an ability that does not belong to its species (the ability slot of the
    /// pokemon is out of range, or the ability itself is not one of the species) leaves ALM
    /// without any encounter at all. The first ability of the species is the one that most
    /// encounters have, so it is the one that is used.
    /// </remarks>
    private static void ApplyValidAbility(PKM pokemon)
    {
        var personal = pokemon.PersonalInfo;
        if (personal.AbilityCount == 0) return;

        pokemon.AbilityNumber = 0;
        pokemon.Ability = personal.GetAbilityAtIndex(0);
    }

    /// <summary>
    /// The kinds of encounter a species has in a version.
    /// </summary>
    /// <param name="source">The pokemon the encounters are looked up for: it gives the format of
    /// the template.</param>
    /// <param name="species">The species to look for.</param>
    /// <param name="context">The generation the version belongs to.</param>
    /// <param name="version">The version to look the encounters up in.</param>
    /// <returns>The names of the kinds of encounter (a wild slot, a static encounter, a gift...).</returns>
    /// <remarks>
    /// The kinds are the ones PKHeX knows: a wild slot (Slot7), a static encounter (Static7), an
    /// egg (Egg7), a gift or an event (WC7), a trade (Trade7)...
    /// </remarks>
    public static IReadOnlyList<string> EncounterKinds(PKM source, ushort species, EntityContext context,
        GameVersion version)
    {
        var kinds = new List<string>();

        foreach (var encounter in EncountersOf(source, species, context, version))
        {
            var kind = encounter.GetType().Name.Replace("Encounter", "");
            if (!kinds.Contains(kind)) kinds.Add(kind);
        }

        return kinds;
    }

    /// <summary>
    /// The base game versions of a generation: the ones whose encounter data PKHeX knows.
    /// </summary>
    /// <remarks>
    /// The versions of a generation come in pairs (Ultra Sun/Ultra Moon, Sun/Moon, ...). The
    /// encounter data is stored per game, so the encounters of a version have to be looked up with
    /// the base version of the game and not with the group version: the group version (the one
    /// that is stored in the pokemon) is not a game of its own and PKHeX throws when it is used to
    /// look for an encounter.
    /// <br />
    /// The order is the one of the generation from the newest game to the oldest one.
    /// </remarks>
    public static IReadOnlyList<GameVersion> EncounterVersions(EntityContext context)
    {
        var versions = new List<GameVersion>();

        foreach (var version in GameUtil.GetVersionsInGeneration(context, DefaultVersionFor(context)))
        {
            if (version.Context != context) continue;

            // Battle Revolution is a game of the e-Reader of Gen 4 that has no encounter data.
            if (version == GameVersion.BATREV) continue;

            versions.Add(version);
        }

        return versions;
    }


    /// <summary>
    /// The range of levels of an encounter.
    /// </summary>
    private static (byte Min, byte Max)? EncounterLevelRange(IEncounterable encounter)
    {
        if (encounter is ILevelRange range) return (range.LevelMin, range.LevelMax);

        var type = encounter.GetType();
        var min = type.GetProperty("LevelMin");
        var max = type.GetProperty("LevelMax");
        if (min == null || max == null) return null;

        try
        {
            return ((byte)min.GetValue(encounter)!, (byte)max.GetValue(encounter)!);
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            return null;
        }
    }

    /// <summary>
    /// A minimal pokemon of a species, used to look its encounters up.
    /// </summary>
    /// <param name="source">The pokemon that was received: it is the base of the template, so the
    /// template has the format of the file.</param>
    /// <param name="species">The species of the template.</param>
    /// <param name="context">The generation the version belongs to.</param>
    /// <param name="version">The version the encounters are looked up in.</param>
    private static PKM? EncounterTemplate(PKM source, ushort species, EntityContext context, GameVersion version)
    {
        try
        {
            var template = ClonePokemon(source);
            if (template == null) return null;

            template.Species = species;
            template.CurrentLevel = 1;
            template.Version = version;
            template.Nickname = SpeciesName(species);
            template.OriginalTrainerName = "ALM";
            template.Language = 2;
            template.OriginalTrainerGender = 0;
            template.Ball = (byte)Ball.Poke;
            template.Nature = Nature.Hardy;
            template.CurrentFriendship = 70;

            // The ability has to be one of the species: ALM looks for an encounter whose ability
            // is the one of the pokemon it is given, and an invalid ability leaves it without any
            // encounter at all.
            ApplyValidAbility(template);

            return template;
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            return null;
        }
    }

    /// <summary>
    /// Legalizes a pokemon from a real encounter of its species or of one of the species it
    /// evolves from.
    /// </summary>
    /// <param name="original">The pokemon that was received.</param>
    /// <param name="targetContext">The generation the pokemon has to belong to.</param>
    /// <param name="version">The version of the destination generation being tried.</param>
    /// <returns>The legal pokemon, or null when no encounter could be used.</returns>
    /// <remarks>
    /// The species of the pokemon is tried first, at the levels that have an encounter, and then
    /// the species it evolves from: a species that does not live in the destination generation
    /// (a Gloom does not appear in the wild in Alola) is still legal when it is the evolution of
    /// a species that does (an Oddish of Alola).
    /// </remarks>
    private static PKM? LegalizeFromEncounterWithLevels(PKM original, EntityContext targetContext,
        GameVersion version, SearchBudget budget)
    {
        var swept = LegalizeWithLevelSweep(original, targetContext, version, budget);
        if (swept != null) return swept;

        // The previous forms of the species: the pokemon is built from the encounter of the
        // species it evolves from and then evolved.
        var attempts = MaxPreEvolutionAttempts;
        foreach (var pre in PreEvolutionCandidates(original))
        {
            if (attempts-- <= 0) break;
            if (budget.Exhausted) break;

            var evolved = RebuildFromPreEvolution(original, targetContext, version, pre, budget, out _, true);
            if (evolved == null) continue;

            return evolved;
        }

        // The previous forms of the species, built from their encounter: ALM applies the
        // evolution chain itself, so the pokemon comes out with the level, the ability and the
        // moves the evolution needs.
        var fromEncounter = LegalizeFromEncounterOfPreEvolution(original, targetContext, version, budget);
        if (fromEncounter != null) return fromEncounter;

        return null;
    }


    /// <summary>
    /// Legalizes a pokemon from the encounter of the species it evolves from.
    /// </summary>
    /// <param name="original">The pokemon that was received.</param>
    /// <param name="targetContext">The generation the pokemon has to belong to.</param>
    /// <param name="version">The version of the destination generation being tried.</param>
    /// <returns>The legal pokemon, or null when no encounter could be used.</returns>
    /// <remarks>
    /// The encounter of an egg is the encounter of the species it hatches into (the egg of a
    /// Venonat is the encounter of a Venomoth), and ALM applies the evolution chain: the pokemon
    /// that comes out is the evolved one, with the level the evolution needs. The levels are
    /// tried from the lowest one, because the evolution has a level of its own.
    /// </remarks>
    private static PKM? LegalizeFromEncounterOfPreEvolution(PKM original, EntityContext targetContext,
        GameVersion version, SearchBudget budget)
    {
        foreach (var baseVersion in VersionsOfGroup(version, targetContext))
        {
            var encounters = EncountersOf(original, original.Species, targetContext, baseVersion);
            foreach (var encounter in encounters)
            {
                // The encounter of an egg is of the species it hatches into: the one of the
                // pokemon that was received is its own encounter.
                var encounterSpecies = EncounterSpecies(encounter);
                if (encounterSpecies == 0 || encounterSpecies == original.Species) continue;

                for (var level = 1; level <= 100; level++)
                {
                    if (budget.Exhausted) return null;

                    var legalized = LegalizeFromEncounter(original, targetContext, baseVersion, encounter,
                        (byte)level, budget);
                    if (legalized == null) continue;

                    return legalized;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The encounters of a species in a version.
    /// </summary>
    private static IReadOnlyList<IEncounterable> EncountersOf(PKM source, ushort species,
        EntityContext context, GameVersion version)
    {
        try
        {
            var template = EncounterTemplate(source, species, context, version);
            if (template == null) return Array.Empty<IEncounterable>();

            var info = _GetTrainerInfo(template, version, context);
            return EncounterMovesetGenerator.GenerateEncounters(template, info,
                ReadOnlyMemory<ushort>.Empty, new[] { version }).ToList();
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            return Array.Empty<IEncounterable>();
        }
    }


    /// <summary>
    /// Builds a pokemon from a real encounter with ALM.
    /// </summary>
    /// <param name="original">The pokemon that was received.</param>
    /// <param name="targetContext">The generation the pokemon has to belong to.</param>
    /// <param name="version">The version the encounter has to come from.</param>
    /// <param name="encounter">The encounter the pokemon is built from.</param>
    /// <param name="level">The level the pokemon has to have.</param>
    /// <returns>The legal pokemon, or null when the encounter does not produce one.</returns>
    /// <remarks>
    /// The API used is <c>APILegality.GetLegalFromTemplate(ITrainerInfo, PKM, IBattleTemplate,
    /// out LegalizationResult, IEncounterable)</c>: ALM builds the pokemon from the encounter it
    /// is given, applying the evolution chain when the encounter is of a species that evolves into
    /// the one that was received.
    /// </remarks>
    private static PKM? LegalizeFromEncounter(PKM original, EntityContext targetContext,
        GameVersion version, IEncounterable encounter, byte level, SearchBudget budget)
    {
        var template = BuildEncounterTemplate(original, targetContext, version, true);
        if (template == null) return null;

        template.CurrentLevel = level;
        template.EXP = Experience.GetEXP(level, template.PersonalInfo.EXPGrowth);
        template.Nickname = SpeciesName(original.Species);

        // Every call to ALM is an attempt of the budget.
        if (!budget.TryUse()) return null;

        try
        {
            var info = _GetTrainerInfoWithRegion(template, version, targetContext);

            var set = new RegenTemplate(template, (byte)targetContext.Generation)
            {
                Species = original.Species,
                Level = level,
                Context = targetContext,
                Nickname = SpeciesName(original.Species),
            };

            var legalized = APILegality.GetLegalFromTemplate(info, template, set, out _, encounter);
            if (legalized == null) return null;
            if (legalized.Species != original.Species) return null;

            legalized = FitAbilityToEncounter(legalized);
            legalized = RepairMetDataOfEncounter(legalized, targetContext, version, encounter);

            if (!TryApplyTrainerData(legalized, info)) return null;
            if (!CheckLegality(legalized).Valid) return null;

            return legalized;
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            return null;
        }
    }

    /// <summary>
    /// Gives a pokemon built from an encounter a met date and a met level that can exist.
    /// </summary>
    /// <param name="pokemon">The pokemon that was built from the encounter.</param>
    /// <param name="targetContext">The generation the pokemon has to belong to.</param>
    /// <param name="version">The version the encounter has to come from.</param>
    /// <remarks>
    /// The met date of a Pokemon that comes from an encounter is the day it was met on, so it has
    /// to be a date of the game it was met in, and the met level has to be the level of the
    /// encounter (an egg is met at level 1).
    /// </remarks>
    private static PKM RepairMetDataOfEncounter(PKM pokemon, EntityContext targetContext, GameVersion version,
        IEncounterable encounter)
    {
        var candidate = ClonePokemon(pokemon);
        if (candidate == null) return pokemon;

        if (!IsValidMetDate(candidate.MetDate))
            candidate.MetDate = PlausibleMetDate(targetContext, version);

        // The met level of the encounter is the level the pokemon was met at: the egg of a
        // Pokemon is met at level 1.
        var range = EncounterLevelRange(encounter);
        if (range != null && candidate.MetLevel != range.Value.Min)
        {
            candidate.MetLevel = range.Value.Min;
            if (candidate.MetLevel > candidate.CurrentLevel) candidate.MetLevel = candidate.CurrentLevel;
        }

        if (!CheckLegality(candidate).Valid)
        {
            return pokemon;
        }

        return candidate;
    }

    /// <summary>
    /// Legalizes a pokemon from a real encounter of its species, trying the levels that have an
    /// encounter in the destination generation.
    /// </summary>
    /// <param name="original">The pokemon that was received.</param>
    /// <param name="targetContext">The generation the pokemon has to belong to.</param>
    /// <param name="version">The version of the destination generation being tried.</param>
    /// <returns>The legal pokemon, or null when no encounter could be used.</returns>
    /// <remarks>
    /// The pokemon is built from the encounter of its species at the level that has one: a species
    /// does not always have an encounter at the level the pokemon has (the Pikachu of Alola is
    /// not wild at level 1, it is a gift or an egg), and the level of the encounter is the one
    /// that makes the pokemon legal.
    /// <br />
    /// The level of the pokemon is the first one that is tried, so a pokemon that already has an
    /// encounter at its level does not change it.
    /// </remarks>
    private static PKM? LegalizeWithLevelSweep(PKM original, EntityContext targetContext,
        GameVersion version, SearchBudget budget)
    {
        var group = VersionsOfGroup(version, targetContext).ToList();

        foreach (var baseVersion in group)
        {
            var encounters = EncountersOf(original, original.Species, targetContext, baseVersion);
            if (encounters.Count == 0) continue;

            var levels = new List<byte>();
            foreach (var encounter in encounters)
            {
                var range = EncounterLevelRange(encounter);
                if (range == null) continue;

                for (var level = range.Value.Min; level <= range.Value.Max; level++)
                {
                    if (!levels.Contains(level)) levels.Add(level);
                }
            }
            if (levels.Count == 0) continue;

            // The level of the pokemon is the first one that is tried: it is the one that changes
            // the least.
            var ordered = new List<byte>();
            if (!levels.Contains(original.CurrentLevel)) ordered.Add(original.CurrentLevel);
            ordered.AddRange(levels);

            foreach (var level in ordered)
            {
                if (budget.Exhausted) return null;

                var legalized = LegalizeAtLevel(original, targetContext, baseVersion, group, level, budget);
                if (legalized == null) continue;

                return legalized;
            }

            // The species does not have an encounter of its own in this version: the only
            // encounters are the ones of the species it evolves from (the egg of a Venonat for a
            // Venomoth), and the evolution has a level of its own, so the levels are tried one
            // by one until the evolution is possible.
            if (encounters.Any(e => EncounterSpecies(e) == original.Species)) continue;

            for (var level = 1; level <= 100; level++)
            {
                if (ordered.Contains((byte)level)) continue;
                if (budget.Exhausted) return null;

                var legalized = LegalizeAtLevel(original, targetContext, baseVersion, group, (byte)level, budget);
                if (legalized == null) continue;

                return legalized;
            }
        }

        return null;
    }

    /// <summary>
    /// The base versions of the group of games a version belongs to.
    /// </summary>
    /// <param name="version">The version being tried.</param>
    /// <param name="context">The generation the version belongs to.</param>
    /// <remarks>
    /// A version of the generation can be a group of games (Ultra Sun and Ultra Moon are the same
    /// version for the pokemon), and the encounters of a group are the ones of each of its games.
    /// </remarks>
    /// <summary>
    /// The base versions of the group of games a version belongs to.
    /// </summary>
    /// <remarks>
    /// The versions of a generation come in groups (Ultra Sun and Ultra Moon are the same version
    /// for a pokemon, and so are Sun and Moon), and the encounters of a group are the ones of each
    /// of its games. A version of the group is looked up with the base version of the game, which
    /// is the one whose encounter data PKHeX knows.
    /// </remarks>
    private static readonly Dictionary<GameVersion, GameVersion[]> BaseVersionsByGroup = new()
    {
        [GameVersion.USUM] = new[] { GameVersion.UM, GameVersion.US },
        [GameVersion.SM] = new[] { GameVersion.SN, GameVersion.MN },
        [GameVersion.ORAS] = new[] { GameVersion.OR, GameVersion.AS },
        [GameVersion.XY] = new[] { GameVersion.X, GameVersion.Y },
        [GameVersion.B2W2] = new[] { GameVersion.B2, GameVersion.W2 },
        [GameVersion.BW] = new[] { GameVersion.B, GameVersion.W },
        [GameVersion.HGSS] = new[] { GameVersion.HG, GameVersion.SS },
        [GameVersion.DPPt] = new[] { GameVersion.Pt, GameVersion.D },
        [GameVersion.DP] = new[] { GameVersion.D },
        [GameVersion.FRLG] = new[] { GameVersion.FR, GameVersion.LG },
        [GameVersion.RSE] = new[] { GameVersion.R, GameVersion.S },
        [GameVersion.CXD] = new[] { GameVersion.CXD },
        [GameVersion.GS] = new[] { GameVersion.GD, GameVersion.SI },
        [GameVersion.C] = new[] { GameVersion.C },
        [GameVersion.RBY] = new[] { GameVersion.RD, GameVersion.GN, GameVersion.BU, GameVersion.YW },
        [GameVersion.RB] = new[] { GameVersion.RB },
    };

    private static IEnumerable<GameVersion> VersionsOfGroup(GameVersion version, EntityContext context)
    {
        if (BaseVersionsByGroup.TryGetValue(version, out var group)) return group;

        foreach (var value in BaseVersionsByGroup.Values)
        {
            if (value.Contains(version)) return value;
        }

        return new[] { version };
    }

    /// <summary>
    /// The other game of the pair the version of the pokemon belongs to.
    /// </summary>
    /// <param name="version">The version of the pokemon.</param>
    /// <param name="context">The generation the version has to belong to.</param>
    /// <returns>The other game of the pair, or null when the version does not belong to a pair.</returns>
    /// <remarks>
    /// The games of a generation come in pairs (Ultra Sun and Ultra Moon, Sun and Moon, Omega
    /// Ruby and Alpha Sapphire, ...). A Pokemon that was caught in one of them is legal with the
    /// version of that game: a file that says the other one of the pair makes the Pokemon illegal,
    /// because the moves it relearns and the encounter it comes from belong to the game it was
    /// really caught in. Only a group of two games is a pair: the four games of Gen 1 (Red, Green,
    /// Blue and Yellow) are one group and they do not have a sibling.
    /// </remarks>
    public static GameVersion? SiblingVersion(GameVersion version, EntityContext context)
    {
        var group = VersionsOfGroup(version, context).ToList();
        if (group.Count != 2) return null;

        return group[0] == version ? group[1] : group[0];
    }

    /// <summary>
    /// The pokemon tried with the other game of the pair of its version.
    /// </summary>
    /// <param name="source">The pokemon that was received. It is never modified.</param>
    /// <param name="targetContext">The generation the pokemon has to be legalized into.</param>
    /// <returns>A copy of the pokemon with the version corrected, or null when there is no sibling.</returns>
    private static PKM? SiblingVersionAttempt(PKM source, EntityContext targetContext)
    {
        if (source.Version == GameVersion.Any) return null;
        if (source.Version.Context != targetContext) return null;

        var sibling = SiblingVersion(source.Version, targetContext);
        if (sibling == null || sibling.Value.Context != targetContext) return null;

        var candidate = ClonePokemon(source);
        if (candidate == null) return null;

        candidate.Version = sibling.Value;
        return candidate;
    }

    /// <summary>
    /// Legalizes a pokemon from a real encounter of its species at a level.
    /// </summary>
    /// <param name="original">The pokemon that was received.</param>
    /// <param name="targetContext">The generation the pokemon has to belong to.</param>
    /// <param name="version">The version the encounter has to come from.</param>
    /// <param name="level">The level the encounter has to be at.</param>
    /// <returns>The legal pokemon, or null when there is no encounter at that level.</returns>
    private static PKM? LegalizeAtLevel(PKM original, EntityContext targetContext, GameVersion version,
        IReadOnlyList<GameVersion> searchVersions, byte level, SearchBudget budget)
    {
        var template = BuildEncounterTemplate(original, targetContext, version, true);
        if (template == null) return null;

        template.CurrentLevel = level;
        template.EXP = Experience.GetEXP(level, template.PersonalInfo.EXPGrowth);

        // Every call to ALM is an attempt of the budget.
        if (!budget.TryUse()) return null;

        try
        {
            var info = _GetTrainerInfo(template, version, targetContext);

            // ALM only looks for the encounters of the games of the group that is being tried, so
            // the encounter that is looked for is the one of the version the level comes from.
            APILegality.GameVersionPriority = GameVersionPriorityType.PriorityOrder;
            APILegality.PriorityOrder = new List<GameVersion>(searchVersions);

            var legalized = Legalizer.Legalize(info, template, new LegalityAnalysis(template));
            if (legalized == null) return null;

            if (legalized.Species != original.Species) return null;

            legalized = FitAbilityToEncounter(legalized);

            if (!TryApplyTrainerData(legalized, info)) return null;
            if (!CheckLegality(legalized).Valid) return null;

            return legalized;
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            return null;
        }
    }

    /// <summary>
    /// Builds the pokemon again from a real encounter of its species.
    /// </summary>
    /// <param name="original">The pokemon that was received.</param>
    /// <param name="targetContext">The generation the pokemon has to belong to.</param>
    /// <param name="version">The game version the encounter has to come from.</param>
    /// <param name="changes">The list of changes applied, in a readable form.</param>
    /// <returns>The legal pokemon, or null when no encounter could be used.</returns>
    /// <remarks>
    /// ALM 26.10.18 rebuilds a pokemon from a real encounter when the pokemon it is given does not
    /// describe one: the encounter fixes the met location, the met date, the ball, the moves and
    /// the ability, while the species, the nickname, the level, the nature, the IVs/EVs, the shiny
    /// flag, the gender and the held item of the pokemon that was received are kept.
    /// <br />
    /// The API used is <c>Legalizer.Legalize(ITrainerInfo, PKM, LegalityAnalysis)</c>: the template
    /// is the pokemon that was received with its encounter data (met location, met date, moves and
    /// ability) cleared, so ALM has to look for an encounter that fits it.
    /// <br />
    /// This is the last resort: it is only used when the repairs that point at the reasons of ALM
    /// are not enough.
    /// </remarks>
    private static PKM? RebuildFromEncounter(PKM original, EntityContext targetContext, GameVersion version,
        SearchBudget budget, out List<string> changes)
    {
        changes = new List<string>();

        // Every call to ALM is an attempt of the budget: when the budget runs out the search
        // stops and the reason is reported.
        if (!budget.TryUse())
        {
            changes.Add(BudgetReason(targetContext, budget));
            return null;
        }

        try
        {
            var template = BuildEncounterTemplate(original, targetContext, version);
            if (template == null) return null;

            var info = _GetTrainerInfo(template, version, targetContext);

            ConfigureAutoMod(targetContext);
            var legalized = Legalizer.Legalize(info, template, new LegalityAnalysis(template));
            if (legalized == null) return null;

            if (legalized.Species != original.Species || legalized.Form != original.Form) return null;

            // The ability slot of the encounter is the one that makes the pokemon legal.
            legalized = FitAbilityToEncounter(legalized);

            // The trainer of the pokemon that was received is kept when the result stays legal.
            if (!TryApplyTrainerData(legalized, info)) return null;

            // The shiny flag of the pokemon that was received is kept when the result stays legal.
            legalized = PreserveShiny(original, legalized);

            changes.AddRange(DescribeChanges(original, legalized));
            return legalized;
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            return null;
        }
    }

    /// <summary>
    /// The template given to ALM to build the pokemon from a real encounter.
    /// </summary>
    /// <remarks>
    /// It is a copy of the pokemon that was received with the data of the encounter cleared (met
    /// location, met date, met level, moves, PP and ability), so ALM has to look for an encounter
    /// that fits the species, the level and the game of the template.
    /// </remarks>
    private static PKM? BuildEncounterTemplate(PKM original, EntityContext targetContext, GameVersion version,
        bool forEncounterSearch = false)
    {
        var template = ClonePokemon(original);
        if (template == null) return null;

        template.MetLocation = 0;
        template.MetYear = 0;
        template.MetMonth = 0;
        template.MetDay = 0;
        template.MetLevel = 0;
        template.SetMoves(new ushort[] { 0, 0, 0, 0 });
        template.SetRelearnMoves(new ushort[] { 0, 0, 0, 0 });
        for (var i = 0; i < 4; i++) SetMovePP(template, i, 0);
        if (forEncounterSearch)
        {
            // ALM looks for an encounter whose ability is the one of the pokemon it is given and
            // whose moves are the ones of the pokemon, so an ability that does not belong to the
            // species or a move that the encounter does not have leaves it without any encounter.
            ApplyValidAbility(template);
        }
        else
        {
            template.Ability = 0;
            template.AbilityNumber = 0;
        }
        template.FatefulEncounter = false;
        template.Version = version;

        return template;
    }

    /// <summary>
    /// Copies the trainer of the pokemon that was received into the legalized one.
    /// </summary>
    /// <returns>Whether the pokemon is legal after the change.</returns>
    /// <remarks>
    /// ALM fills the trainer data with its own values. The trainer of the pokemon that was
    /// received is restored when the result stays legal with it; otherwise the data ALM generated
    /// is kept, because a legal pokemon is more important than the name of the trainer.
    /// </remarks>
    private static bool TryApplyTrainerData(PKM pokemon, ITrainerInfo info)
    {
        var name = pokemon.OriginalTrainerName;
        var language = pokemon.Language;
        var gender = pokemon.OriginalTrainerGender;
        var tid = pokemon.TID16;
        var sid = pokemon.SID16;
        var id32 = pokemon.ID32;

        try
        {
            SimpleEdits.SetTrainerData(pokemon, info);
            if (pokemon.OriginalTrainerName != info.OT) pokemon.OriginalTrainerName = info.OT;
            if (pokemon.Language != info.Language) pokemon.Language = info.Language;
            if (pokemon.OriginalTrainerGender != info.Gender) pokemon.OriginalTrainerGender = info.Gender;
            if (pokemon.TID16 != info.TID16) pokemon.TID16 = info.TID16;
            if (pokemon.SID16 != info.SID16) pokemon.SID16 = info.SID16;
            if (pokemon.ID32 != info.ID32) pokemon.ID32 = info.ID32;
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            return false;
        }

        if (CheckLegality(pokemon).Valid) return true;

        try
        {
            pokemon.OriginalTrainerName = name;
            pokemon.Language = language;
            pokemon.OriginalTrainerGender = gender;
            pokemon.TID16 = tid;
            pokemon.SID16 = sid;
            pokemon.ID32 = id32;
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            return false;
        }

        return CheckLegality(pokemon).Valid;
    }

    /// <summary>
    /// Keeps the shiny flag of the pokemon that was received.
    /// </summary>
    /// <remarks>
    /// ALM regenerates the PID of the pokemon, so a shiny pokemon comes back not shiny. The PID and
    /// the encryption constant of the pokemon that was received are put back when the result stays
    /// legal with them.
    /// </remarks>
    private static PKM PreserveShiny(PKM original, PKM legalized)
    {
        if (!original.IsShiny || legalized.IsShiny) return legalized;

        var copy = ClonePokemon(legalized);
        if (copy == null) return legalized;

        try
        {
            copy.PID = original.PID;
            copy.EncryptionConstant = original.EncryptionConstant;
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            return legalized;
        }

        if (copy.IsShiny && CheckLegality(copy).Valid) return copy;

        return legalized;
    }

    /// <summary>
    /// Tries the ability slots of the species until the pokemon is legal.
    /// </summary>
    /// <remarks>
    /// ALM keeps the ability of the pokemon it is given, but the encounter it finds may need
    /// another ability slot (the hidden ability of an encounter, for example).
    /// </remarks>
    private static PKM FitAbilityToEncounter(PKM pokemon)
    {
        if (CheckLegality(pokemon).Valid) return pokemon;

        var personal = pokemon.PersonalInfo;
        for (var slot = 0; slot < personal.AbilityCount; slot++)
        {
            var copy = ClonePokemon(pokemon);
            if (copy == null) continue;

            copy.AbilityNumber = (byte)slot;
            copy.Ability = personal.GetAbilityAtIndex((byte)slot);
            if (CheckLegality(copy).Valid) return copy;
        }

        return pokemon;
    }

    /// <summary>
    /// Gives the pokemon back the values it had when it was received, as long as it stays legal.
    /// </summary>
    /// <param name="original">The pokemon that was received.</param>
    /// <param name="legalized">The pokemon ALM generated.</param>
    /// <returns>The pokemon with the values that could be restored.</returns>
    /// <remarks>
    /// Each field is restored on its own: the value of the pokemon that was received is put back
    /// and the field is kept when the pokemon is still legal with it. A field that cannot be
    /// restored (because the pokemon would not be legal any more) keeps the value ALM gave it, and
    /// it is the only one that shows up in the list of changes.
    /// <br />
    /// The fields that are restored are the met date, the met location, the met level, the moves
    /// and their PP, the ability and its slot, the handling trainer, the IVs and the EVs, the
    /// nature and the held item.
    /// </remarks>
    public static PKM RestoreMinimal(PKM original, PKM legalized)
    {
        var result = ClonePokemon(legalized) ?? legalized;

        result = RestoreMetDate(result, original);
        result = RestoreField(result, original, (target, source) => target.MetLocation = source.MetLocation);
        result = RestoreField(result, original, (target, source) => target.MetLevel = source.MetLevel);
        result = RestoreField(result, original, (target, source) =>
        {
            target.Ability = source.Ability;
            target.AbilityNumber = (byte)source.AbilityNumber;
        });
        result = RestoreField(result, original, (target, source) =>
        {
            target.SetMoves(new[] { source.GetMove(0), source.GetMove(1), source.GetMove(2), source.GetMove(3) });
            for (var i = 0; i < 4; i++) SetMovePP(target, i, GetMovePP(source, i));
        });
        result = RestoreField(result, original, (target, source) => target.Nature = source.Nature);
        result = RestoreField(result, original, (target, source) => target.HeldItem = source.HeldItem);
        result = RestoreField(result, original, (target, source) =>
        {
            target.SetIVs(source.GetIVs());
            target.EV_HP = source.EV_HP;
            target.EV_ATK = source.EV_ATK;
            target.EV_DEF = source.EV_DEF;
            target.EV_SPA = source.EV_SPA;
            target.EV_SPD = source.EV_SPD;
            target.EV_SPE = source.EV_SPE;
        });
        result = RestoreField(result, original, (target, source) =>
        {
            target.HandlingTrainerName = source.HandlingTrainerName;
            target.HandlingTrainerGender = source.HandlingTrainerGender;
        });

        return result;
    }

    /// <summary>
    /// Restores the met date of the pokemon that was received.
    /// </summary>
    /// <param name="current">The pokemon that ALM generated.</param>
    /// <param name="original">The pokemon that was received.</param>
    /// <returns>The pokemon with the met date it has to have.</returns>
    /// <remarks>
    /// The date of the pokemon that was received is the one that is kept, when it is a date that
    /// can exist. When it is not (a pokemon with no date, or the first of January of the year
    /// one), ALM puts the date of the day the engine ran, and that makes the answer of the engine
    /// depend on the day it runs: the date of the release of the game the pokemon ends up in is
    /// used instead, because it is a date that can exist and it is a day the pokemon could have
    /// been met on.
    /// </remarks>
    private static PKM RestoreMetDate(PKM current, PKM original)
    {
        if (IsValidMetDate(original.MetDate))
        {
            var restored = RestoreField(current, original, (target, source) => target.MetDate = source.MetDate);
            if (!ReferenceEquals(restored, current)) return restored;
        }

        if (!IsUsableContext(current.Version.Context)) return current;

        var plausible = PlausibleMetDate(current.Version.Context, current.Version);
        if (!IsValidMetDate(plausible)) return current;

        var candidate = ClonePokemon(current);
        if (candidate == null) return current;

        candidate.MetDate = plausible;
        return CheckLegality(candidate).Valid ? candidate : current;
    }

    /// <summary>
    /// Puts the value of a field of the pokemon that was received back, when the pokemon stays legal.
    /// </summary>
    private static PKM RestoreField(PKM current, PKM original, Action<PKM, PKM> restore)
    {
        var candidate = ClonePokemon(current);
        if (candidate == null) return current;

        try
        {
            restore(candidate, original);
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            return current;
        }

        if (!CheckLegality(candidate).Valid) return current;

        return candidate;
    }

    /// <summary>
    /// The list of the differences between the pokemon that was received and the one that is
    /// returned, in a readable form.
    /// </summary>
    public static List<string> DescribeChanges(PKM before, PKM after)
    {
        var changes = new List<string>();

        if (before.Species != after.Species)
            changes.Add($"species {before.Species} -> {after.Species}");
        if (before.Nickname != after.Nickname)
            changes.Add($"nickname '{before.Nickname}' -> '{after.Nickname}'");
        if (before.CurrentLevel != after.CurrentLevel)
            changes.Add($"level {before.CurrentLevel} -> {after.CurrentLevel}");
        if (before.Nature != after.Nature)
            changes.Add($"nature {before.Nature} -> {after.Nature}");
        if (before.Ability != after.Ability || before.AbilityNumber != after.AbilityNumber)
            changes.Add($"ability {before.Ability} (slot {before.AbilityNumber}) -> {after.Ability} (slot {after.AbilityNumber})");
        if (before.HeldItem != after.HeldItem)
            changes.Add($"held item {before.HeldItem} -> {after.HeldItem}");
        if (before.Gender != after.Gender)
            changes.Add($"gender {before.Gender} -> {after.Gender}");
        if (before.IsShiny != after.IsShiny)
            changes.Add($"shiny {before.IsShiny} -> {after.IsShiny}");
        if (before.Ball != after.Ball)
            changes.Add($"ball {before.Ball} -> {after.Ball}");
        if (before.Version != after.Version)
            changes.Add($"version {before.Version} -> {after.Version}");
        if (before.MetLocation != after.MetLocation)
            changes.Add($"met location {before.MetLocation} -> {after.MetLocation}");
        if (before.MetLevel != after.MetLevel)
            changes.Add($"met level {before.MetLevel} -> {after.MetLevel}");
        if (before.MetDate != after.MetDate)
            changes.Add($"met date {FormatMetDate(before.MetDate)} -> {FormatMetDate(after.MetDate)}");
        if (before.OriginalTrainerName != after.OriginalTrainerName)
            changes.Add($"OT name '{before.OriginalTrainerName}' -> '{after.OriginalTrainerName}'");
        if (before.Language != after.Language)
            changes.Add($"OT language {before.Language} -> {after.Language}");
        if (before.OriginalTrainerGender != after.OriginalTrainerGender)
            changes.Add($"OT gender {before.OriginalTrainerGender} -> {after.OriginalTrainerGender}");
        if (before.HandlingTrainerName != after.HandlingTrainerName)
            changes.Add($"HT name '{before.HandlingTrainerName}' -> '{after.HandlingTrainerName}'");
        if (before.HandlingTrainerGender != after.HandlingTrainerGender)
            changes.Add($"HT gender {before.HandlingTrainerGender} -> {after.HandlingTrainerGender}");
        if (before.CurrentFriendship != after.CurrentFriendship)
            changes.Add($"friendship {before.CurrentFriendship} -> {after.CurrentFriendship}");

        for (var i = 0; i < 4; i++)
        {
            var move = before.GetMove(i);
            var newMove = after.GetMove(i);
            var pp = GetMovePP(before, i);
            var newPp = GetMovePP(after, i);
            if (move == newMove && pp == newPp) continue;

            changes.Add($"move {i + 1} ({MoveName(move)}) PP {pp} -> {MoveName(newMove)} PP {newPp}");
        }

        if (!ReadIVs(before).SequenceEqual(ReadIVs(after)))
            changes.Add($"IVs [{string.Join(",", ReadIVs(before))}] -> [{string.Join(",", ReadIVs(after))}]");
        if (!ReadEVs(before).SequenceEqual(ReadEVs(after)))
            changes.Add($"EVs [{string.Join(",", ReadEVs(before))}] -> [{string.Join(",", ReadEVs(after))}]");

        return changes;
    }

    private static int[] ReadIVs(PKM pokemon)
    {
        return new[] { pokemon.IV_HP, pokemon.IV_ATK, pokemon.IV_DEF, pokemon.IV_SPA, pokemon.IV_SPD, pokemon.IV_SPE };
    }

    private static int[] ReadEVs(PKM pokemon)
    {
        return new[] { pokemon.EV_HP, pokemon.EV_ATK, pokemon.EV_DEF, pokemon.EV_SPA, pokemon.EV_SPD, pokemon.EV_SPE };
    }

    /// <summary>
    /// A copy of a pokemon, so the one that was received is never modified.
    /// </summary>
    public static PKM? ClonePokemon(PKM pokemon)
    {
        try
        {
            var useParty = pokemon.SIZE_PARTY > pokemon.SIZE_STORED;
            var data = new byte[useParty ? pokemon.SIZE_PARTY : pokemon.SIZE_STORED];
            if (useParty)
                pokemon.WriteDecryptedDataParty(data);
            else
                pokemon.WriteDecryptedDataStored(data);

            return EntityFormat.GetFromBytes(data, pokemon.Context);
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            return null;
        }
    }

    private static int GetMovePP(PKM pokemon, int index)
    {
        return index switch
        {
            0 => pokemon.Move1_PP,
            1 => pokemon.Move2_PP,
            2 => pokemon.Move3_PP,
            _ => pokemon.Move4_PP
        };
    }

    private static void SetMovePP(PKM pokemon, int index, int pp)
    {
        switch (index)
        {
            case 0:
                pokemon.Move1_PP = pp;
                break;
            case 1:
                pokemon.Move2_PP = pp;
                break;
            case 2:
                pokemon.Move3_PP = pp;
                break;
            default:
                pokemon.Move4_PP = pp;
                break;
        }
    }

    /// <summary>
    /// The name of a move, or its number when the names are not available.
    /// </summary>
    private static string MoveName(ushort move)
    {
        if (move == 0) return "-";

        try
        {
            var names = GameInfo.Strings.Move;
            if (names != null && move < names.Count && !string.IsNullOrWhiteSpace(names[move]))
                return names[move];
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
        }

        return $"#{move}";
    }

    private static string FormatMetDate(DateOnly? date)
    {
        return date == null ? "(none)" : date.Value.ToString("d/M/yyyy");
    }

    /// <summary>
    /// The data of the trainer of a pokemon, with the region of its console.
    /// </summary>
    /// <remarks>
    /// The region of the trainer is the one of the pokemon that was received: ALM builds the
    /// pokemon of the encounter with the data of the trainer, and a trainer without a region is
    /// not a trainer of any console.
    /// </remarks>
    private static SimpleTrainerInfo _GetTrainerInfoWithRegion(PKM pokemon, GameVersion version,
        EntityContext targetContext)
    {
        var geo = pokemon as IGeoTrack;
        var origin = pokemon as IRegionOrigin;

        return new SimpleTrainerInfo(version)
        {
            OT = pokemon.OriginalTrainerName,
            SID16 = pokemon.SID16,
            TID16 = pokemon.TID16,
            ID32 = pokemon.ID32,
            Language = pokemon.Language,
            Gender = pokemon.OriginalTrainerGender,
            Generation = targetContext.Generation,
            Context = targetContext,
            Country = geo?.Geo1_Country ?? 0,
            Region = geo?.Geo1_Region ?? 0,
            ConsoleRegion = origin?.ConsoleRegion ?? 0,
        };
    }

    private static SimpleTrainerInfo _GetTrainerInfo(PKM pokemon, GameVersion version, EntityContext targetContext)
    {
        // ALM 26.10.18 reads the generation and the context of the trainer to pick the encounter
        // data it uses. Leaving them unset (EntityContext.None) makes ALM ask for the evolution
        // group of a context-less trainer, which throws.
        return new SimpleTrainerInfo(version)
        {
            OT = pokemon.OriginalTrainerName,
            SID16 = pokemon.SID16,
            TID16 = pokemon.TID16,
            ID32 = pokemon.ID32,
            Language = pokemon.Language,
            Gender = pokemon.OriginalTrainerGender,
            Generation = targetContext.Generation,
            Context = targetContext
        };
    }
}
