// Writes a copy of a game assembly with every type, method and field made public,
// so the mod can compile against private game members. The copy is a compile-time
// reference only; the game's own DLL is what loads at runtime.
using Mono.Cecil;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: Publicizer <input.dll> <output.dll>");
    return 1;
}

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
using var asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });

foreach (var type in asm.MainModule.GetTypes())
{
    if (type.IsNested) type.IsNestedPublic = true; else type.IsPublic = true;
    foreach (var m in type.Methods) { m.IsPublic = true; }
    foreach (var f in type.Fields)
    {
        // Leave event backing fields alone: making them public clashes with the event name.
        if (type.Events.Any(e => e.Name == f.Name)) continue;
        f.IsPublic = true;
    }
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1])));
asm.Write(args[1]);
Console.WriteLine($"publicized {args[0]} -> {args[1]}");
return 0;
