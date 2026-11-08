// Reproduce la legalizacion de coreconsole (coreconsole/handlers/Legality.cs)
// pero mostrando el resultado intermedio de ALM y cualquier excepcion, para
// saber por que la regeneracion no ocurre.
//
//	dotnet run --project tools/legalprobe -- <archivo.pk9> [generacion]
using PKHeX.Core;
using PKHeX.Core.AutoMod;

if (args.Length == 0)
{
    Console.Error.WriteLine("uso: legalprobe <archivo.pk9> [generacion]");
    return 1;
}

var generation = args.Length > 1 ? args[1] : "Gen9";
var data = File.ReadAllBytes(args[0]);

Console.WriteLine($"archivo      : {Path.GetFileName(args[0])} ({data.Length} B)");
Console.WriteLine($"generacion   : {generation}");
Console.WriteLine($"ALM          : {typeof(LegalityAnalysis).Assembly.GetName().Version}");
Console.WriteLine($"PKHeX.Core   : {typeof(PKM).Assembly.GetName().Version}");
Console.WriteLine();

var pkmn = EntityFormat.GetFromBytes(data, EntityContext.None);
if (pkmn is null)
{
    Console.WriteLine("PKHeX no reconoce el archivo");
    return 2;
}

Console.WriteLine($"tipo         : {pkmn.GetType().Name}");
Console.WriteLine($"contexto     : {pkmn.Context}");
Console.WriteLine($"especie      : {pkmn.Species} (nickname {pkmn.Nickname})");
Console.WriteLine($"version juego: {pkmn.Version}");
Console.WriteLine();

try
{
    EncounterEvent.RefreshMGDB(string.Empty);
    RibbonStrings.ResetDictionary(GameInfo.Strings.ribbons);
}
catch (Exception e)
{
    Console.WriteLine($"aviso al inicializar ALM: {e.GetType().Name}: {e.Message}");
}

var analysis = new LegalityAnalysis(pkmn);
Console.WriteLine($"analisis     : Valid={analysis.Valid}");
foreach (var line in analysis.Report().Split("\n"))
{
    if (line.Length > 0) Console.WriteLine($"  - {line}");
}

Console.WriteLine();

var info = new SimpleTrainerInfo(GameVersion.SL)
{
    OT = pkmn.OT_Name,
    SID16 = pkmn.SID16,
    TID16 = pkmn.TID16,
    Language = pkmn.Language,
    Gender = pkmn.OT_Gender,
};

var targetGeneration = generation switch
{
    "1" or "Gen1" => 1,
    "2" or "Gen2" => 2,
    "3" or "Gen3" => 3,
    "4" or "Gen4" => 4,
    "5" or "Gen5" => 5,
    "6" or "Gen6" => 6,
    "7" or "Gen7" => 7,
    "8" or "Gen8" => 8,
    "9" or "Gen9" => 9,
    _ => 0,
};

if (targetGeneration == 0)
{
    Console.WriteLine($"generacion no reconocida: {generation}");
    return 3;
}

try
{
    var template = new RegenTemplate(pkmn, targetGeneration);
    Console.WriteLine($"plantilla    : {template.GetType().Name} creacion={template.GetType().GetProperty("Creation")?.GetValue(template)}");
    Console.WriteLine($"  species    : {template.GetType().GetProperty("Species")?.GetValue(template)}");
    Console.WriteLine($"  ability    : {template.GetType().GetProperty("Ability")?.GetValue(template)}");
    Console.WriteLine($"  moves      : {template.GetType().GetProperty("Moves")?.GetValue(template)}");
    Console.WriteLine($"  ivs        : {template.GetType().GetProperty("IVs")?.GetValue(template)}");
    Console.WriteLine($"  evs        : {template.GetType().GetProperty("EVs")?.GetValue(template)}");
    Console.WriteLine($"  nature     : {template.GetType().GetProperty("Nature")?.GetValue(template)}");
    Console.WriteLine($"  gender     : {template.GetType().GetProperty("Gender")?.GetValue(template)}");
    Console.WriteLine($"  ball       : {template.GetType().GetProperty("Ball")?.GetValue(template)}");
    Console.WriteLine($"  version    : {template.GetType().GetProperty("Version")?.GetValue(template)}");
}
catch (Exception e)
{
    Console.WriteLine($"EXCEPCION al crear la plantilla: {e.GetType().Name}: {e.Message}");
    return 4;
}

Console.WriteLine();

try
{
    var template = new RegenTemplate(pkmn, targetGeneration);
    var start = DateTime.UtcNow;
    var result = info.GetLegalFromTemplateTimeout(pkmn, template, out var outcome);
    var elapsed = DateTime.UtcNow - start;

    Console.WriteLine($"resultado ALM : outcome={outcome} ({elapsed.TotalSeconds:F1}s)");
    Console.WriteLine($"pokemon devuelto: {(result is null ? "null" : result.GetType().Name)}");

    if (result is not null)
    {
        var legal = new LegalityAnalysis(result);
        Console.WriteLine($"reanalisis   : Valid={legal.Valid}");
        foreach (var line in legal.Report().Split("\n"))
        {
            if (line.Length > 0) Console.WriteLine($"  - {line}");
        }
    }
}
catch (Exception e)
{
    Console.WriteLine($"EXCEPCION al legalizar: {e.GetType().Name}: {e.Message}");
    Console.WriteLine(e.StackTrace);
    return 5;
}

return 0;
