using System.Reflection;

var runtimeDir = @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.4";
var ksaDir = @"D:\Games\Kitten Space Agency";
var assemblyPaths = Directory.GetFiles(runtimeDir, "*.dll")
    .Concat(Directory.GetFiles(ksaDir, "*.dll"));

var resolver = new PathAssemblyResolver(assemblyPaths);
using var mlc = new MetadataLoadContext(resolver);
var asm = mlc.LoadFromAssemblyPath(Path.Combine(ksaDir, "KSA.dll"));

DumpType(asm, "KSA.Tank");
DumpType(asm, "KSA.PartMenu");
DumpType(asm, "KSA.CombustionObject");
DumpVehicleEditorBits(asm);

static void DumpType(Assembly asm, string typeName)
{
    var type = asm.GetType(typeName, throwOnError: false);
    Console.WriteLine($"TYPE {typeName}: {(type == null ? "<null>" : type.FullName)}");
    if (type == null)
    {
        Console.WriteLine();
        return;
    }

    Console.WriteLine("FIELDS");
    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        Console.WriteLine($"  {field.FieldType.Name} {field.Name}");

    Console.WriteLine("PROPS");
    foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        Console.WriteLine($"  {prop.PropertyType.Name} {prop.Name}");

    Console.WriteLine("METHODS");
    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Where(m => !m.IsSpecialName)
        .OrderBy(m => m.Name))
    {
        var parms = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  {method.ReturnType.Name} {method.Name}({parms})");
    }

    Console.WriteLine();
}

static void DumpVehicleEditorBits(Assembly asm)
{
    var type = asm.GetType("KSA.VehicleEditor", throwOnError: false);
    Console.WriteLine($"TYPE KSA.VehicleEditor: {(type == null ? "<null>" : type.FullName)}");
    if (type == null)
        return;

    Console.WriteLine("RELEVANT FIELDS");
    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
        .Where(f => f.Name.Contains("combust", StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine($"  {field.FieldType.Name} {field.Name}");
    }

    Console.WriteLine("RELEVANT METHODS");
    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
        .Where(m => m.Name.Contains("combust", StringComparison.OrdinalIgnoreCase)))
    {
        var parms = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  {method.ReturnType.Name} {method.Name}({parms})");
    }
}
