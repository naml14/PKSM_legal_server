// Sonda de diagnostico: identifica con que formato/generacion interpreta
// PKHeX.Core 23.6.3 un archivo .pk9 (Gen 9) y que lee de el.
//
//	dotnet run --project tools/pk9probe -- <archivo.pk9>
using System.Reflection;
using PKHeX.Core;

if (args.Length == 0)
{
    Console.Error.WriteLine("uso: pk9probe <archivo.pk9>");
    return 1;
}

// Lectura tolerante: algunas propiedades cambian entre versiones de PKHeX.
static string Get(object o, string name)
{
    var t = o.GetType();
    var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
    if (prop is not null)
    {
        try { return prop.GetValue(o)?.ToString() ?? "null"; }
        catch (Exception e) { return $"<error: {e.GetType().Name}>"; }
    }

    var method = t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
    if (method is not null)
    {
        try { return method.Invoke(o, null)?.ToString() ?? "null"; }
        catch (Exception e) { return $"<error: {e.GetType().Name}>"; }
    }

    return "(miembro inexistente)";
}

var data = File.ReadAllBytes(args[0]);
Console.WriteLine($"archivo            : {Path.GetFileName(args[0])}");
Console.WriteLine($"bytes             : {data.Length}");

var asm = typeof(PKM).Assembly;
Console.WriteLine($"PKHeX version     : {asm.GetName().Version}");
Console.WriteLine($"PKHeX soporta PK9?: {(asm.GetType("PKHeX.Core.PK9") is not null ? "SI" : "NO -> sin soporte Gen 9")}");
Console.WriteLine($"PKHeX soporta PK8?: {(asm.GetType("PKHeX.Core.PK8") is not null ? "SI" : "NO")}");
Console.WriteLine($"GameVersion SV?   : {Enum.IsDefined(typeof(GameVersion), "SV")}");

PKM? pkm;
try
{
    pkm = EntityFormat.GetFromBytes(data, EntityContext.None);
}
catch (Exception e)
{
    Console.WriteLine($"error al decodificar: {e.GetType().Name}: {e.Message}");
    return 2;
}

if (pkm is null)
{
    Console.WriteLine("PKHeX no reconoce el archivo (null)");
    return 3;
}

Console.WriteLine($"tipo              : {pkm.GetType().Name}");
Console.WriteLine($"format            : {Get(pkm, "Format")}");
Console.WriteLine($"version (juego)   : {Get(pkm, "Version")}");
Console.WriteLine($"generation        : {Get(pkm, "Generation")}");
Console.WriteLine($"especie           : {Get(pkm, "Species")} / {Get(pkm, "Nickname")} shiny={Get(pkm, "IsShiny")}");
Console.WriteLine($"nivel             : {Get(pkm, "Level")} exp={Get(pkm, "Experience")}");
Console.WriteLine($"movimiento 1      : {Get(pkm, "Move1")} (id={Get(pkm, "Move1_ID")})");
Console.WriteLine($"movimiento 2      : {Get(pkm, "Move2")} (id={Get(pkm, "Move2_ID")})");
Console.WriteLine($"movimiento 3      : {Get(pkm, "Move3")} (id={Get(pkm, "Move3_ID")})");
Console.WriteLine($"objeto            : {Get(pkm, "HeldItem")} (id={Get(pkm, "HeldItem_ID")})");
Console.WriteLine($"pid={Get(pkm, "PID")} ec={Get(pkm, "EncryptionConstant")} tsv={Get(pkm, "TSV")}");
Console.WriteLine($"ot={Get(pkm, "OT_Name")} idioma={Get(pkm, "OT_Language")} genero={Get(pkm, "OT_Gender")}");
Console.WriteLine($"t16={Get(pkm, "TID16")} s16={Get(pkm, "SID16")}");
Console.WriteLine($"ht_idioma={Get(pkm, "HT_Language")} ht_memoria={Get(pkm, "HT_Memory_Cleared")}");
Console.WriteLine($"tracker={Get(pkm, "TrackerVersion")} trade_ht={Get(pkm, "TradeMemoryHT")}");
return 0;
