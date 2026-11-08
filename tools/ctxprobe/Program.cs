using PKHeX.Core;

var data = File.ReadAllBytes(args[0]);
foreach (var ctx in new[] { EntityContext.None, EntityContext.Gen7, EntityContext.Gen8, EntityContext.Gen9 })
{
    Console.WriteLine($"--- contexto {ctx}");
    try
    {
        var pkm = EntityFormat.GetFromBytes(data, ctx);
        if (pkm is null) { Console.WriteLine("    NULL (no se pudo cargar)"); continue; }
        Console.WriteLine($"    tipo={pkm.GetType().Name}  .Context={pkm.Context}  especie={pkm.Species}  moves={pkm.Move1}/{pkm.Move2}/{pkm.Move3}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"    EXCEPCION {e.GetType().Name}: {e.Message}");
    }
}
