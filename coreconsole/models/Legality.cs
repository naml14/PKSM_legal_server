using System.Text.Json.Serialization;
using PKHeX.Core;
using Sentry;

namespace coreconsole.Models;

/// <summary>
/// The action a reason reported by ALM points at.
/// </summary>
/// <remarks>
/// The reasons of ALM are classified with their text, never with a guess about the pokemon: each
/// reason is mapped to the repair that fixes it, or to <see cref="None"/> when there is nothing to
/// repair because ALM regenerates that data on its own.
/// </remarks>
public enum LegalityRepair
{
    /// <summary>Nothing to repair: ALM regenerates that data when it legalizes the pokemon.</summary>
    None = 0,

    /// <summary>The met date of the encounter is not a date that can exist.</summary>
    MetDate,

    /// <summary>A move is not a move the species can use (or it has no PP left).</summary>
    Move,

    /// <summary>The ability slot does not match the encounter.</summary>
    Ability,

    /// <summary>The met location is not a real location of the version of the pokemon.</summary>
    MetLocation,

    /// <summary>The data of the handling trainer is missing or wrong.</summary>
    HandlingTrainer,

    /// <summary>
    /// The origin of the pokemon cannot be matched. It is not a repair: the pokemon has to be
    /// built again from a real encounter, which is the last resort.
    /// </summary>
    Origin,

    /// <summary>
    /// The version of the pokemon does not belong to the generation it has to be legalized into
    /// (a Pokemon of Gold inside a .pk7). The version is given one of the destination generation
    /// before the encounter is looked for.
    /// </summary>
    Version,

    /// <summary>The SID of the original trainer is not the one the encounter needs.</summary>
    TrainerId,

    /// <summary>The memory of the geolocation of the original trainer is not there.</summary>
    GeoMemory,

    /// <summary>The friendship of the original trainer is not the base friendship of the species.</summary>
    Friendship,
}

public struct LegalityCheckReport
{
    [JsonPropertyName("legal")] public bool Legal { get; set; }
    [JsonPropertyName("report")] public string[] Report { get; set; }

    public LegalityCheckReport(LegalityAnalysis la)
    {
        Legal = la.Valid;
        Report = la.Report().Split("\n");
    }
}

public struct AutoLegalizationResult
{
    [JsonPropertyName("legal")] public bool Legal { get; set; }
    [JsonPropertyName("report")] public string[] Report { get; set; }
    [JsonPropertyName("pokemon")] public string? PokemonBase64 { get; set; }

    /// <summary>
    /// The changes applied to the pokemon that was received to get the legal one.
    /// </summary>
    /// <remarks>
    /// It is empty when the pokemon was not modified at all. It is added to the response of the
    /// auto-legalization: the other fields of the response are not changed.
    /// </remarks>
    [JsonPropertyName("changes")] public string[] Changes { get; set; }

    /// <param name="la">Legality analysis of the pokemon that is reported (the original one when the
    /// legalization failed, the regenerated one when it succeeded).</param>
    /// <param name="pokemon">The legalized pokemon, or null when the legalization did not produce one.</param>
    /// <param name="note">Extra explanation appended to the report when the legalization failed, so a
    /// null <see cref="PokemonBase64"/> is never returned without saying why.</param>
    /// <param name="changes">The changes applied to the pokemon that was received.</param>
    public AutoLegalizationResult(LegalityAnalysis la, PKM? pokemon, string? note = null,
        IEnumerable<string>? changes = null)
    {
        Legal = la.Valid;
        var lines = la.Report().Split("\n").ToList();
        if (!string.IsNullOrWhiteSpace(note)) lines.Add(note);
        Report = lines.ToArray();
        Changes = changes?.ToArray() ?? Array.Empty<string>();

        if (pokemon == null) return;
        try
        {
            // PKHeX.Core 26.10.18 no longer exposes the decrypted byte buffers as properties:
            // the data has to be written into a buffer of the right size.
            var useParty = pokemon.SIZE_PARTY > pokemon.SIZE_STORED;
            var data = new byte[useParty ? pokemon.SIZE_PARTY : pokemon.SIZE_STORED];
            if (useParty)
                pokemon.WriteDecryptedDataParty(data);
            else
                pokemon.WriteDecryptedDataStored(data);

            PokemonBase64 = Convert.ToBase64String(data);
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
        }
    }
}
