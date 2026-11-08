// Sonda de reflexion sobre PKHeX.Core: verifica la semantica de EntityContext
// (None = 0?, Gen9 presente?) y de EntityContextExtensions.Generation(), que es
// la conversion que usa coreconsole para --legalization-generation.
//
//	dotnet run --project tools/almprobe
using System.Reflection;

var pkhex = typeof(PKHeX.Core.PKM).Assembly;
Console.WriteLine($"PKHeX.Core: {pkhex.GetName().Version}");
Console.WriteLine();

var ctx = typeof(PKHeX.Core.EntityContext);
Console.WriteLine("=== EntityContext (PKHeX.Core) ===");
foreach (var n in Enum.GetNames(ctx))
{
    var v = Convert.ToInt64(Enum.Parse(ctx, n));
    Console.WriteLine($"  {n,-14} = {v}");
}

Console.WriteLine();
Console.WriteLine("=== EntityContextExtensions: metodos de conversion a generacion ===");
var ext = pkhex.GetTypes().FirstOrDefault(t => t.Name == "EntityContextExtensions");
if (ext is null)
{
    Console.WriteLine("  (no se encontro EntityContextExtensions)");
}
else
{
    foreach (var m in ext.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
    {
        Console.WriteLine($"  {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}) -> {m.ReturnType.Name}");
    }
}

Console.WriteLine();
Console.WriteLine("=== EntityContextExtensions.Generation() por cada contexto ===");
if (ext is not null)
{
    var gen = ext.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .FirstOrDefault(m => m.Name == "Generation" && m.GetParameters().Length == 1);
    if (gen is not null)
    {
        foreach (var n in Enum.GetNames(ctx))
        {
            object result;
            try { result = gen.Invoke(null, new[] { Enum.Parse(ctx, n) })!; }
            catch (Exception e) { result = "error: " + (e.InnerException ?? e).GetType().Name; }
            Console.WriteLine($"  {n,-14} -> {result}");
        }
    }
}

Console.WriteLine();
Console.WriteLine("=== ALM (PKHeX.Core.AutoMod): tipos con 'Gen' en el nombre ===");
try
{
    var almPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "PKHeX.Core.AutoMod.dll"));
    if (!File.Exists(almPath))
        almPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "coreconsole", "deps", "PKHeX.Core.AutoMod.dll"));
    var alm = Assembly.LoadFrom(almPath);
    Console.WriteLine($"  cargado: {almPath}");
    foreach (var t in alm.GetTypes().Where(t => t.Name.Contains("Gen", StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine($"  {t.FullName} (enum={t.IsEnum})");
}
catch (Exception e)
{
    Console.WriteLine("  (no se pudo cargar ALM: " + e.Message + ")");
}
return 0;
