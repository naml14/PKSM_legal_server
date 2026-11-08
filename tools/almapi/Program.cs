using System.Reflection;
var path = Path.Combine(AppContext.BaseDirectory, "PKHeX.Core.AutoMod.dll");
var asm = Assembly.LoadFrom(path);
Console.WriteLine("ALM " + asm.GetName().Version);
foreach (var name in new[] { "Legalizer", "RegenTemplate", "RegenSetting", "RegenSet", "RegenTemplateExtensions", "RegenUtil" })
{
    var t = asm.GetTypes().FirstOrDefault(x => x.Name == name);
    if (t is null) { Console.WriteLine($"\n--- {name}: (no existe)"); continue; }
    Console.WriteLine($"\n--- {t.FullName}");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
    {
        if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_")) continue;
        var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}{(p.HasDefaultValue ? " = " + (p.DefaultValue ?? "null") : "")}"));
        Console.WriteLine($"    {(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}({ps})");
    }
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        Console.WriteLine($"    prop {p.PropertyType.Name} {p.Name}");
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        Console.WriteLine($"    field {f.FieldType.Name} {f.Name}");
}
