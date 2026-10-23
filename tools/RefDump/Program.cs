// Dumps everything the co-op mod depends on in a game build to a sorted, token-free text file,
// and diffs two such dumps. Run it on the old and the new AirDefender.dll after a game update to
// see exactly which references moved (see UPDATE.md and scripts/check-game-update.ps1).
//
//   RefDump dump <game AirDefender.dll> <mod AirDefender.Coop.dll> <mod src dir> <out.txt>
//   RefDump diff <old.txt> <new.txt> <out.md>
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length == 5 && args[0] == "dump") return Dump.Run(args[1], args[2], args[3], args[4]);
if (args.Length == 4 && args[0] == "diff") return Diff.Run(args[1], args[2], args[3]);
Console.Error.WriteLine("usage: RefDump dump <game.dll> <mod.dll> <mod src dir> <out.txt>\n       RefDump diff <old.txt> <new.txt> <out.md>");
return 1;

static class Dump
{
    static ModuleDefinition _game;
    static readonly Dictionary<string, TypeDefinition> ByFullName = new(StringComparer.Ordinal);
    static readonly Dictionary<string, List<TypeDefinition>> ByShortName = new(StringComparer.Ordinal);
    static readonly SortedDictionary<string, SortedSet<string>> Out = new(StringComparer.Ordinal);
    static readonly HashSet<TypeDefinition> Touched = new();

    static void Line(string section, string line)
    {
        if (!Out.TryGetValue(section, out var s)) Out[section] = s = new SortedSet<string>(StringComparer.Ordinal);
        s.Add(line);
    }

    public static int Run(string gamePath, string modPath, string srcDir, string outPath)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(gamePath)));
        var rp = new ReaderParameters { AssemblyResolver = resolver };
        _game = ModuleDefinition.ReadModule(gamePath, rp);
        foreach (var t in _game.GetTypes())
        {
            ByFullName[t.FullName] = t;
            if (!ByShortName.TryGetValue(t.Name, out var l)) ByShortName[t.Name] = l = new List<TypeDefinition>();
            l.Add(t);
        }
        var src = new Source(srcDir);

        Line("asm", $"fingerprint {_game.Mvid.ToString("N").Substring(0, 12)}");
        Line("asm", $"types {_game.Types.Count} (all incl. nested {ByFullName.Count})");

        var modResolver = new DefaultAssemblyResolver();
        modResolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(modPath)));
        var mod = ModuleDefinition.ReadModule(modPath, new ReaderParameters { AssemblyResolver = modResolver });
        CompileRefs(mod);
        NamedTargets(src);
        StringRefs(src);
        ScoringGuards();
        SimCandidates(src);
        MonoBehaviours();
        FieldSchemas(src);
        Save(src);
        IlHashes(src);
        Callers();
        Surface();

        var sb = new StringBuilder();
        sb.AppendLine("# RefDump of " + Path.GetFileName(gamePath) + " (sorted; diff two of these with 'RefDump diff')");
        foreach (var (section, lines) in Out)
        {
            sb.Append("## ").AppendLine(section);
            foreach (var l in lines) sb.AppendLine(l);
        }
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine($"wrote {outPath}: {Out.Sum(s => s.Value.Count)} lines in {Out.Count} sections");
        return 0;
    }

    // ------------------------------------------------------------------ type helpers

    static TypeDefinition FindType(string name)
    {
        if (name == null) return null;
        string n = name.Replace('+', '/');
        if (ByFullName.TryGetValue("AirDefender." + n, out var t) || ByFullName.TryGetValue(n, out t)) return t;
        // Nested by C# dotted name, e.g. "QRAInterceptorMover.MissionType" or "GameSaveManager.SaveBlob".
        var parts = n.Split('.');
        for (int i = parts.Length - 1; i > 0; i--)
        {
            string outer = string.Join(".", parts.Take(i)), nested = string.Join("/", parts.Skip(i));
            if (ByFullName.TryGetValue("AirDefender." + outer + "/" + nested, out t) || ByFullName.TryGetValue(outer + "/" + nested, out t)) return t;
        }
        if (!n.Contains('.') && ByShortName.TryGetValue(n, out var l) && l.Count == 1) return l[0];
        return null;
    }

    static TypeDefinition SafeResolve(TypeReference r)
    {
        try { return r?.Resolve(); } catch { return null; }
    }

    static bool DerivesFrom(TypeDefinition t, string baseFullName)
    {
        for (var x = SafeResolve(t?.BaseType); x != null; x = SafeResolve(x.BaseType))
            if (x.FullName == baseFullName) return true;
        return false;
    }

    static bool Implements(TypeDefinition t, string iface)
    {
        for (var x = t; x != null; x = SafeResolve(x.BaseType))
            if (x.Interfaces.Any(i => i.InterfaceType.FullName == iface)) return true;
        return false;
    }

    static bool IsMonoBehaviour(TypeDefinition t) => DerivesFrom(t, "UnityEngine.MonoBehaviour");
    static bool IsGameType(TypeReference r) => RootScope(r) == _game.Assembly.Name.Name || r.Module == _game && r is TypeDefinition;

    static string RootScope(TypeReference r)
    {
        while (r.DeclaringType != null) r = r.DeclaringType;
        return r.Scope switch { AssemblyNameReference a => a.Name, ModuleDefinition m => m.Assembly.Name.Name, _ => r.Scope?.Name };
    }

    /// <summary>Reflection-style name ('+' for nested) so it matches what the mod sees at runtime.</summary>
    static string RName(TypeReference t) => t.FullName.Replace('/', '+');

    static string Vis(MethodDefinition m) => m.IsPublic ? "public" : m.IsFamily ? "protected" : m.IsAssembly ? "internal" : m.IsFamilyOrAssembly ? "protected internal" : "private";
    static string Vis(FieldDefinition f) => f.IsPublic ? "public" : f.IsFamily ? "protected" : f.IsAssembly ? "internal" : f.IsFamilyOrAssembly ? "protected internal" : "private";

    static string Sig(MethodDefinition m) =>
        $"{Vis(m)}{(m.IsStatic ? " static" : "")}{(m.IsVirtual ? " virtual" : "")} {m.ReturnType.FullName} {m.Name}" +
        (m.HasGenericParameters ? "<" + string.Join(",", m.GenericParameters.Select(g => g.Name)) + ">" : "") +
        "(" + string.Join(", ", m.Parameters.Select(p => (p.IsOut ? "out " : "") + p.ParameterType.FullName + " " + p.Name + (p.HasDefault ? "=" + Fmt(p.Constant) : ""))) + ")";

    static string FieldSig(FieldDefinition f) =>
        $"{Vis(f)}{(f.IsStatic ? " static" : "")}{(f.IsInitOnly ? " readonly" : "")}{(f.IsLiteral ? " const" : "")} {f.FieldType.FullName} {f.Name}" +
        (f.HasConstant ? " = " + Fmt(f.Constant) : "");

    static string Fmt(object o) => o switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        IFormattable fo => fo.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => o.ToString(),
    };

    static IEnumerable<MethodDefinition> AllMethods() => _game.GetTypes().SelectMany(t => t.Methods);

    // ------------------------------------------------------------------ [compile-refs]

    static void CompileRefs(ModuleDefinition mod)
    {
        // user mod type -> referenced game members, collected from method bodies, signatures and attributes
        var users = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Use(MemberReference r, TypeDefinition modType)
        {
            if (r == null) return;
            while (modType.DeclaringType != null) modType = modType.DeclaringType;
            string key = Key(r);
            if (key == null) return;
            if (!users.TryGetValue(key, out var s)) users[key] = s = new SortedSet<string>(StringComparer.Ordinal);
            s.Add(modType.Name);
        }

        foreach (var mt in mod.GetTypes())
        {
            foreach (var ca in mt.CustomAttributes.Concat(mt.Methods.SelectMany(m => m.CustomAttributes)))
                foreach (var a in ca.ConstructorArguments)
                    if (a.Value is TypeReference tr) Use(tr, mt);
            foreach (var f in mt.Fields) Use(f.FieldType, mt);
            foreach (var m in mt.Methods)
            {
                Use(m.ReturnType, mt);
                foreach (var p in m.Parameters) Use(p.ParameterType, mt);
                if (!m.HasBody) continue;
                foreach (var v in m.Body.Variables) Use(v.VariableType, mt);
                foreach (var ins in m.Body.Instructions)
                    if (ins.Operand is MemberReference mr) Use(mr, mt);
            }
        }

        foreach (var (key, who) in users)
        {
            string status = Resolve(key);
            Line("compile-refs", $"{status} {key}  <- {string.Join(", ", who)}");
        }
    }

    // Normalised reference key, or null when the reference is not into the game assembly.
    static readonly Dictionary<string, MemberReference> KeyedRefs = new(StringComparer.Ordinal);
    static string Key(MemberReference r)
    {
        switch (r)
        {
            case GenericInstanceMethod gim: return Key(gim.ElementMethod);
            case TypeSpecification ts: return Key(ts.ElementType);
            case GenericParameter: return null;
            case TypeReference tr:
                if (RootScope(tr) != _game.Assembly.Name.Name) return null;
                return Remember("type " + tr.FullName, tr);
            case MethodReference mr:
            {
                var dt = mr.DeclaringType is GenericInstanceType git ? git.ElementType : mr.DeclaringType;
                if (dt is ArrayType || RootScope(dt) != _game.Assembly.Name.Name) return null;
                return Remember("method " + mr.ReturnType.FullName + " " + dt.FullName + "::" + mr.Name + "(" + string.Join(",", mr.Parameters.Select(p => p.ParameterType.FullName)) + ")", mr);
            }
            case FieldReference fr:
            {
                var dt = fr.DeclaringType is GenericInstanceType git ? git.ElementType : fr.DeclaringType;
                if (RootScope(dt) != _game.Assembly.Name.Name) return null;
                return Remember("field " + fr.FieldType.FullName + " " + dt.FullName + "::" + fr.Name, fr);
            }
        }
        return null;
    }

    static string Remember(string key, MemberReference r) { KeyedRefs[key] = r; return key; }

    static string Resolve(string key)
    {
        var r = KeyedRefs[key];
        switch (r)
        {
            case TypeReference tr:
            {
                var td = ByFullName.GetValueOrDefault(tr.FullName);
                if (td == null) return "MISSING";
                Touched.Add(td);
                return "OK";
            }
            case MethodReference mr:
            {
                var dt = mr.DeclaringType is GenericInstanceType git ? git.ElementType : mr.DeclaringType;
                var td = ByFullName.GetValueOrDefault(dt.FullName);
                if (td == null) return "MISSING(type)";
                Touched.Add(td);
                var sig = string.Join(",", mr.Parameters.Select(p => p.ParameterType.FullName));
                bool ok = td.Methods.Any(m => m.Name == mr.Name && m.ReturnType.FullName == mr.ReturnType.FullName &&
                                              string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName)) == sig);
                if (ok) return "OK";
                return td.Methods.Any(m => m.Name == mr.Name) ? "MISSING(signature changed)" : "MISSING";
            }
            case FieldReference fr:
            {
                var dt = fr.DeclaringType is GenericInstanceType git ? git.ElementType : fr.DeclaringType;
                var td = ByFullName.GetValueOrDefault(dt.FullName);
                if (td == null) return "MISSING(type)";
                Touched.Add(td);
                var f = td.Fields.FirstOrDefault(x => x.Name == fr.Name);
                if (f == null) return "MISSING";
                return f.FieldType.FullName == fr.FieldType.FullName ? "OK" : "MISSING(type changed to " + f.FieldType.FullName + ")";
            }
        }
        return "?";
    }

    // ------------------------------------------------------------------ [named-targets]
    // Members the mod names in source (nameof / string) and looks up by name at runtime:
    // Harmony targets, AccessTools lookups and RouteList entries. Overload count and parameter
    // names matter here (ambiguous matches, Harmony by-name argument injection).

    static void NamedTargets(Source src)
    {
        var targets = new SortedDictionary<string, (TypeDefinition t, string member, SortedSet<string> files, bool str)>(StringComparer.Ordinal);
        foreach (var (file, text) in src.Files)
        {
            foreach (Match m in Regex.Matches(text, @"nameof\(([A-Za-z_][\w.]*)\.(\w+)\)"))
                AddTarget(targets, m.Groups[1].Value, m.Groups[2].Value, file, false);
            foreach (Match m in Regex.Matches(text, @"typeof\(([\w.]+)\)\s*,\s*""(\w+)"""))
                AddTarget(targets, m.Groups[1].Value, m.Groups[2].Value, file, true);
        }
        foreach (var (_, (t, member, files, str)) in targets)
        {
            string head = $"{RName(t)}.{member}";
            string from = $"  <- {string.Join(", ", files)}{(str ? " [by string]" : "")}";
            var methods = t.Methods.Where(m => m.Name == member).ToList();
            var field = t.Fields.FirstOrDefault(f => f.Name == member);
            var prop = t.Properties.FirstOrDefault(p => p.Name == member);
            var nested = t.NestedTypes.FirstOrDefault(n => n.Name == member);
            if (methods.Count > 0)
            {
                Line("named-targets", $"{head} overloads={methods.Count}{from}");
                foreach (var m in methods) Line("named-targets", $"{head} :: {Sig(m)}");
            }
            else if (field != null) Line("named-targets", $"{head} field :: {FieldSig(field)}{from}");
            else if (prop != null) Line("named-targets", $"{head} property :: {prop.PropertyType.FullName}{from}");
            else if (nested != null) Line("named-targets", $"{head} nested type{from}");
            else Line("named-targets", $"{head} MISSING{from}");
        }
    }

    static void AddTarget(SortedDictionary<string, (TypeDefinition, string, SortedSet<string>, bool)> targets, string typeExpr, string member, string file, bool str)
    {
        var t = FindType(typeExpr);
        if (t == null) return; // mod's own types, Unity, Steamworks
        Touched.Add(t);
        string key = RName(t) + "." + member;
        if (!targets.TryGetValue(key, out var e)) targets[key] = e = (t, member, new SortedSet<string>(StringComparer.Ordinal), str);
        e.Item3.Add(file);
    }

    // ------------------------------------------------------------------ [string-refs]

    static readonly string[] FrameMethods = { "Update", "LateUpdate", "FixedUpdate" };

    static List<string> DeclaredFrameMethods(TypeDefinition t) =>
        FrameMethods.Where(n => t.Methods.Any(m => m.Name == n && m.Parameters.Count == 0)).ToList();

    static bool CallsTickRegister(TypeDefinition t) =>
        t.Methods.Concat(t.NestedTypes.SelectMany(n => n.Methods)).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
            .Any(i => i.Operand is MethodReference mr && mr.Name == "Register" && mr.DeclaringType.Name == "TickManager");

    static void StringRefs(Source src)
    {
        int frames = 0, ticks = 0;
        foreach (var (list, kind) in new[] { ("EntityLogicTypes", "entity"), ("WorldSimTypes", "world"), ("TickSimTypes", "tick") })
        {
            foreach (var name in src.StringArray("SimulationGate.cs", list))
            {
                var t = ByFullName.GetValueOrDefault("AirDefender." + name.Replace('.', '.'));
                if (t == null) { Line("string-refs", $"SimulationGate.{list} {name} NOT FOUND"); continue; }
                Touched.Add(t);
                var fm = DeclaredFrameMethods(t);
                bool tick = CallsTickRegister(t);
                if (kind == "tick") ticks++; else frames += fm.Count;
                Line("string-refs", $"SimulationGate.{list} {name} found base={t.BaseType?.FullName} frame=[{string.Join(",", fm)}] tickRegister={(tick ? "yes" : "no")}" +
                                    (kind == "tick" && !tick ? "  !! no TickManager.Register call" : "") +
                                    (kind != "tick" && fm.Count == 0 ? "  !! declares no frame method (nothing gated)" : ""));
            }
        }
        Line("string-refs", $"SimulationGate TOTAL: {frames} frame methods gated, {ticks} tick owners (log line 'SimulationGate: N frame methods gated, M tick owners')");

        foreach (var (type, fields) in src.AllowLists())
        {
            var t = FindType(type);
            if (t == null) { Line("string-refs", $"GlobalTargets.Allow[{type}] TYPE NOT FOUND"); continue; }
            foreach (var f in fields)
            {
                var fd = t.Fields.FirstOrDefault(x => x.Name == f);
                Line("string-refs", fd == null
                    ? $"GlobalTargets.Allow[{type}].{f} MISSING"
                    : $"GlobalTargets.Allow[{type}].{f} {(fd.IsStatic ? "static " : "")}{fd.FieldType.FullName} -> {Kind(fd.FieldType)}");
            }
        }

        foreach (var marker in src.Markers())
        {
            var users = AllMethods().Where(m => m.HasBody && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == marker))
                .Select(m => m.DeclaringType.Name + "::" + m.Name).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            Line("string-refs", $"SpawnCatalog marker {marker} used by game in: {(users.Count == 0 ? "NOWHERE" : string.Join(", ", users))}");
        }

        foreach (var name in src.SatelliteNames())
        {
            var t = FindType(name);
            Line("string-refs", t == null ? $"EntityReplicator.IsLocalAuthority {name} NOT FOUND"
                                          : $"EntityReplicator.IsLocalAuthority {name} found IContact={Implements(t, "AirDefender.IContact")}");
        }
    }

    // ------------------------------------------------------------------ [scoring-guards]

    static void ScoringGuards()
    {
        var t = FindType("TriStateScoringService");
        if (t == null) { Line("scoring-guards", "TriStateScoringService NOT FOUND"); return; }
        int n = 0;
        foreach (var m in t.Methods.Where(m => m.IsPublic && m.IsStatic && !m.IsConstructor && m.ReturnType.FullName == "System.Void" &&
                                               (m.Name.StartsWith("On", StringComparison.Ordinal) || m.Name.StartsWith("Add", StringComparison.Ordinal) || m.Name.StartsWith("Apply", StringComparison.Ordinal))))
        {
            Line("scoring-guards", "guarded " + Sig(m));
            n++;
        }
        foreach (var m in t.Methods.Where(m => m.IsStatic && !m.IsConstructor && m.ReturnType.FullName == "System.Void" && !m.IsPublic))
            Line("scoring-guards", "NOT guarded (non-public void) " + Sig(m));
        Line("scoring-guards", $"TOTAL guarded: {n} (log line 'Scoring guards: N')");
    }

    // ------------------------------------------------------------------ [sim-candidates]

    static void SimCandidates(Source src)
    {
        var gated = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (list, kind) in new[] { ("EntityLogicTypes", "entity"), ("WorldSimTypes", "world"), ("TickSimTypes", "tick") })
            foreach (var n in src.StringArray("SimulationGate.cs", list)) gated["AirDefender." + n] = kind;
        foreach (var t in _game.GetTypes().Where(IsMonoBehaviour))
        {
            var fm = DeclaredFrameMethods(t);
            bool tick = CallsTickRegister(t);
            if (fm.Count == 0 && !tick) continue;
            string g = gated.TryGetValue(t.FullName, out var k) ? "gated:" + k : "NOT gated";
            Line("sim-candidates", $"{RName(t)} frame=[{string.Join(",", fm)}] tick={(tick ? "yes" : "no")} {g}");
        }
    }

    // ------------------------------------------------------------------ [monobehaviours]

    static void MonoBehaviours()
    {
        int n = 0;
        foreach (var t in _game.GetTypes().Where(t => IsMonoBehaviour(t) && !t.IsAbstract && !t.HasGenericParameters))
        {
            n++;
            bool contact = Implements(t, "AirDefender.IContact");
            string cid = "";
            if (contact)
            {
                var f = t.Fields.FirstOrDefault(x => x.Name == "contactId" && !x.IsStatic);
                cid = f == null ? " contactId-field=MISSING (SpawnCatalog clone path)" : $" contactId-field={f.FieldType.FullName}";
            }
            Line("monobehaviours", $"{RName(t)}{(contact ? " IContact" : "")}{cid}");
        }
        Line("monobehaviours", $"TOTAL {n} (StateReplicator type ids = index in ordinal-sorted list)");
    }

    // ------------------------------------------------------------------ [field-schemas] (mirror of FieldCodec.TypeSchema.Build)

    static readonly string[] NameFragments = { "cache", "Cache", "debug", "Debug", "scratch", "Scratch", "lastLog", "nextLog", "_tmp" };
    static Dictionary<string, HashSet<string>> _allow;

    static void FieldSchemas(Source src)
    {
        _allow = src.AllowLists().ToDictionary(a => a.type, a => new HashSet<string>(a.fields), StringComparer.Ordinal);
        foreach (var (type, statics, instance) in src.GlobalTargetTypes())
        {
            var t = FindType(type);
            if (t == null) { Line("field-schemas", $"GlobalTargets {type} TYPE NOT FOUND"); continue; }
            Touched.Add(t);
            if (instance) Schema(t, false, "global ");
            if (statics) Schema(t, true, "global ");
        }
        var entityTypes = new HashSet<TypeDefinition>();
        foreach (var n in src.StringArray("SimulationGate.cs", "EntityLogicTypes"))
            if (FindType(n) is { } t) entityTypes.Add(t);
        foreach (var t in _game.GetTypes().Where(t => IsMonoBehaviour(t) && !t.IsAbstract && Implements(t, "AirDefender.IContact"))) entityTypes.Add(t);
        foreach (var t in entityTypes) Schema(t, false, "entity ");
    }

    static void Schema(TypeDefinition t, bool statics, string prefix)
    {
        string owner = prefix + RName(t) + (statics ? ".static" : "");
        var chain = new List<TypeDefinition>();
        for (var x = t; x != null && !IsUnityStop(x.FullName); x = statics ? null : SafeResolve(x.BaseType)) chain.Add(x);
        chain.Reverse();
        int sent = 0;
        foreach (var ct in chain)
        {
            foreach (var f in ct.Fields.Where(f => f.IsStatic == statics).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                string why = null;
                if (f.IsLiteral || (statics && f.IsInitOnly && !IsMutableCollection(f.FieldType))) continue;
                if (f.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute") && !f.Name.Contains("k__BackingField")) continue;
                if (IsDelegate(f.FieldType)) continue;
                if (_allow.TryGetValue(t.Name, out var allow)) { if (!allow.Contains(f.Name)) continue; }
                else if (NameFragments.Any(fr => f.Name.Contains(fr, StringComparison.Ordinal))) why = "name filter";
                string kind = why == null ? Kind(f.FieldType) : null;
                if (why == null && kind == "UNSUPPORTED") why = "unsupported type";
                if (why != null) Line("field-schemas", $"{owner} DROP {f.Name} : {f.FieldType.FullName} ({why})");
                else { Line("field-schemas", $"{owner} send {f.Name} : {f.FieldType.FullName} -> {kind}"); sent++; }
            }
        }
        Line("field-schemas", $"{owner} TOTAL sent={sent}");
    }

    static bool IsUnityStop(string n) => n is "UnityEngine.MonoBehaviour" or "UnityEngine.Behaviour" or "UnityEngine.Component" or "UnityEngine.Object" or "System.Object";

    static bool IsMutableCollection(TypeReference t) =>
        t is GenericInstanceType g && g.ElementType.FullName is "System.Collections.Generic.List`1" or "System.Collections.Generic.Dictionary`2" or "System.Collections.Generic.HashSet`1";

    static bool IsDelegate(TypeReference t)
    {
        var td = SafeResolve(t is GenericInstanceType g ? g.ElementType : t);
        for (var x = td; x != null; x = SafeResolve(x.BaseType))
            if (x.FullName is "System.MulticastDelegate" or "System.Delegate") return true;
        return false;
    }

    static string Scalar(TypeReference t)
    {
        switch (t.FullName)
        {
            case "System.Boolean": case "System.Byte": case "System.SByte": case "System.Int16": case "System.UInt16":
            case "System.Int32": case "System.UInt32": case "System.Int64": case "System.UInt64": case "System.Single":
            case "System.Double": case "System.String":
            case "UnityEngine.Vector2": case "UnityEngine.Vector3": case "UnityEngine.Vector4": case "UnityEngine.Quaternion":
            case "UnityEngine.Color": case "UnityEngine.Rect": case "UnityEngine.Vector2Int":
                return t.Name;
        }
        if (t is not GenericInstanceType && !t.IsArray && SafeResolve(t) is { IsEnum: true }) return "Enum";
        return null;
    }

    static string Kind(TypeReference t)
    {
        if (Scalar(t) is { } s) return s;
        if (t.FullName is "UnityEngine.Transform" or "UnityEngine.GameObject") return "ContactRef";
        if (t is not GenericInstanceType && !t.IsArray && SafeResolve(t) is { } td && IsGameType(td) && IsMonoBehaviour(td) && Implements(td, "AirDefender.IContact")) return "ContactRef";
        if (t is ArrayType at && at.Rank == 1) return Scalar(at.ElementType) is { } e ? "Array<" + e + ">" : "UNSUPPORTED";
        if (t is GenericInstanceType g)
        {
            var a = g.GenericArguments;
            switch (g.ElementType.FullName)
            {
                case "System.Collections.Generic.List`1": if (Scalar(a[0]) is { } e) return "List<" + e + ">"; break;
                case "System.Collections.Generic.Dictionary`2": if (a[0].FullName == "System.String" && Scalar(a[1]) is { } v) return "StringDictionary<" + v + ">"; break;
                case "System.Collections.Generic.HashSet`1": if (a[0].FullName == "System.String") return "StringSet"; break;
            }
        }
        return "UNSUPPORTED";
    }

    // ------------------------------------------------------------------ [save]

    static readonly string[] SaveTypes =
    {
        "GameSaveManager.SaveBlob", "GameSaveManager.CivilianAircraftState", "GameSaveManager.VFRAircraftState",
        "GameSaveManager.ActiveInterceptorState", "GameSaveManager.InFlightMissileState", "GameSaveManager.CAPAnchorState",
        "GameSaveManager.MilitaryStationedAsset", "GameSaveManager.ParkedAssetEntry", "EnemyAircraftController.SavedState",
        "AwacsMover.SavedState", "NimrodSigintMover.SavedState", "OrbitMover.SavedState", "TankerTrackMover.SavedState",
        "Bootstrapper.VirtualDeltaEntry", "MapFeaturesStorage.Feature", "NuclearDamageBands",
    };

    static void Save(Source src)
    {
        var gsm = FindType("GameSaveManager");
        if (gsm == null) { Line("save", "GameSaveManager NOT FOUND"); return; }
        foreach (var f in gsm.Fields.Where(f => f.HasConstant)) Line("save", $"GameSaveManager const {f.Name} = {Fmt(f.Constant)}");
        foreach (var name in SaveTypes)
        {
            var t = FindType(name);
            if (t == null) { Line("save", $"{name} NOT FOUND"); continue; }
            Touched.Add(t);
            Line("save", $"{name} kind={(t.IsValueType ? "struct" : "class")} serializable={t.IsSerializable}");
            int i = 0;
            foreach (var f in t.Fields.Where(f => !f.IsStatic && !f.IsLiteral))
                Line("save", $"{name} field#{i++:D2} {f.Name} : {f.FieldType.FullName}{(f.IsPublic ? "" : " (non-public)")}");
        }
    }

    // ------------------------------------------------------------------ [il-hash]
    // Game methods the mod copies (SpawnCatalog) or whose behaviour it relies on. A changed hash
    // means: re-read the new decompile of that method and update the mod's copy/assumption.

    static readonly (string type, string method, string why)[] Mirrored =
    {
        ("GameSaveManager", "LoadFromPath", "WorldSync load path; SpawnCatalog missing branch mirrors its in-flight-missile block"),
        ("GameSaveManager", "BuildSnapshot", "WorldSync + SpawnCatalog capture"),
        ("GameSaveManager", "UpgradeNamedSaveIfNeeded", "save schema migration"),
        ("GameSaveManager", "RestoreStationedMilitary", "SpawnCatalog Stationed copy"),
        ("GameSaveManager", "RestoreCivilianAircraft", "SpawnCatalog calls with one record (must replace only that id)"),
        ("GameSaveManager", "RestoreVFRAircraft", "SpawnCatalog calls with one record"),
        ("GameSaveManager", "RestoreActiveEnemies", "SpawnCatalog calls with one record"),
        ("GameSaveManager", "RestoreActiveInterceptors", "SpawnCatalog calls with one record"),
        ("GameSaveManager", "EnsureCapAnchorExists", "SpawnCatalog CapAnchor"),
        ("GameSaveManager", "ResolveNearestAirfieldName", "SpawnCatalog parked assets"),
        ("GameSaveManager", "IsEmbeddedParkedAssetStateMarker", "SpawnCatalog markers"),
        ("RadarPrimaryHistory", "Update", "RadarHitSync: ground hits = NotifyHit called inside this Update"),
        ("TrackManager", "NotifyHit", "RadarHitSync prefix"),
        ("TickManager", "Register", "SimulationGate.WrapTick"),
        ("TimeControlsUI", "SetTimeScale", "TimeSync / ReplicationPatches"),
        ("AsmaServiceImpl", "StartPostNow", "PresentationSync"),
        ("VoiceAudioQueue", "EvaluateShouldPlay", "PresentationSync"),
        ("SfxServiceImpl", "PlayAlertNonVoice", "PresentationSync"),
        ("Bootstrapper", "ExportVirtualDeltas", "CustomGlobals"),
        ("Bootstrapper", "ImportVirtualDeltas", "CustomGlobals"),
        ("Bootstrapper", "ClearVirtualDeltas", "CustomGlobals"),
        ("PlayerSession", "Login", "WorldSync client login"),
        ("GameResetService", "ResetToMainMenu", "LifecyclePatches / WorldSync"),
        ("GameResetService", "IsProcessLifetimePlatformRoot", "LifecyclePatches"),
        ("ContactRegistry", "Register", "ReplicationPatches / EntityReplicator"),
        ("TrackManager", "TrySetOperatorClassification", "ReplicationPatches identify routing"),
        ("IdentificationScoringEvaluator", "Evaluate", "ReplicationPatches"),
        ("LocalFileStorage", "WriteBytesAtomicSync", "SandboxPatches"),
        ("LocalFileStorage", "WriteTextAtomicSync", "SandboxPatches"),
        ("GameTimeline", "SetState", "TimeSync"),
        ("SteamManager", "get_Initialized", "Session: Steam wrapper"),
        ("SteamManager", "Awake", "Session: RestartAppIfNecessary / app id"),
    };

    static void IlHashes(Source src)
    {
        foreach (var (type, method, why) in Mirrored)
        {
            var t = FindType(type);
            if (t == null) { Line("il-hash", $"{type}::{method} TYPE NOT FOUND  ({why})"); continue; }
            var ms = t.Methods.Where(m => m.Name == method).ToList();
            if (ms.Count == 0) { Line("il-hash", $"{type}::{method} NOT FOUND  ({why})"); continue; }
            foreach (var m in ms) Line("il-hash", $"{type}::{method}({string.Join(",", m.Parameters.Select(p => p.ParameterType.Name))}) {IlHash(m)}  ({why})");
        }
        // Parked-asset restore blocks: whatever game methods contain the SpawnCatalog markers.
        foreach (var marker in src.Markers())
            foreach (var m in AllMethods().Where(m => m.HasBody && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == marker)))
                Line("il-hash", $"{m.DeclaringType.Name}::{m.Name}({string.Join(",", m.Parameters.Select(p => p.ParameterType.Name))}) {IlHash(m)}  (contains {marker}; SpawnCatalog parked-asset copy)");
    }

    static string IlHash(MethodDefinition m)
    {
        if (!m.HasBody) return "nobody";
        var ins = m.Body.Instructions;
        var index = new Dictionary<Instruction, int>();
        for (int i = 0; i < ins.Count; i++) index[ins[i]] = i;
        var sb = new StringBuilder();
        foreach (var i in ins)
        {
            sb.Append(i.OpCode.Name).Append(' ');
            sb.Append(i.Operand switch
            {
                null => "",
                Instruction target => "@" + index.GetValueOrDefault(target),
                Instruction[] targets => string.Join(",", targets.Select(x => "@" + index.GetValueOrDefault(x))),
                MemberReference mr => NormaliseGenerated(mr.FullName),
                VariableDefinition v => "v" + v.Index,
                ParameterDefinition p => "p" + p.Index,
                string s => "\"" + s + "\"",
                IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                var o => o.ToString(),
            });
            sb.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).Substring(0, 16);
    }

    // Compiler-generated lambda/closure names carry ordinals that shift when unrelated code changes.
    static string NormaliseGenerated(string s) =>
        Regex.Replace(Regex.Replace(s, @"__\d+(_\d+)?", "__N"), @"DisplayClass\d+(_\d+)?", "DisplayClassN");

    // ------------------------------------------------------------------ [callers]

    static void Callers()
    {
        foreach (var m in AllMethods().Where(m => m.HasBody))
        {
            foreach (var i in m.Body.Instructions)
            {
                if (i.Operand is not MethodReference mr) continue;
                string target = mr.DeclaringType.FullName + "::" + mr.Name;
                string caller = RName(m.DeclaringType) + "::" + NormaliseGenerated(m.Name);
                if (target == "AirDefender.TrackManager::NotifyHit")
                    Line("callers", $"NotifyHit <- {caller}");
                else if (mr.DeclaringType.FullName == "System.IO.File" &&
                         mr.Name is "Delete" or "Copy" or "Move" or "Replace" or "WriteAllText" or "WriteAllBytes" or "WriteAllLines" or "AppendAllText" or "Create" or "CreateText")
                    Line("callers", $"System.IO.File::{mr.Name} <- {caller}");
                else if (mr.DeclaringType.FullName is "System.IO.Directory" && mr.Name is "Delete" or "Move")
                    Line("callers", $"System.IO.Directory::{mr.Name} <- {caller}");
                else if (mr.DeclaringType.FullName is "AirDefender.SaveLoad.Storage.LocalFileStorage" && mr.Name.StartsWith("Write", StringComparison.Ordinal))
                    Line("callers", $"LocalFileStorage::{mr.Name} <- {caller}");
                else if (mr.DeclaringType.FullName is "Steamworks.SteamRemoteStorage" && (mr.Name.StartsWith("File", StringComparison.Ordinal) || mr.Name.StartsWith("SetCloud", StringComparison.Ordinal)))
                    Line("callers", $"SteamRemoteStorage::{mr.Name} <- {caller}");
                else if (mr.DeclaringType.FullName is "UnityEngine.PlayerPrefs" && mr.Name.StartsWith("Set", StringComparison.Ordinal))
                    Line("callers", $"PlayerPrefs::{mr.Name}({string.Join(",", mr.Parameters.Select(p => p.ParameterType.Name))}) used");
            }
        }
    }

    // ------------------------------------------------------------------ [surface]

    static void Surface()
    {
        var all = new HashSet<TypeDefinition>(Touched);
        foreach (var t in Touched.ToList()) for (var d = t.DeclaringType; d != null; d = d.DeclaringType) all.Add(d);
        foreach (var t in all)
        {
            if (t.Name.Contains('<')) continue;
            string n = RName(t);
            string kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : "class";
            Line("surface", $"{n} {kind} base={t.BaseType?.FullName} vis={(t.IsPublic || t.IsNestedPublic ? "public" : "non-public")}{(t.IsAbstract && t.IsSealed ? " static" : "")}");
            if (t.IsEnum)
            {
                foreach (var f in t.Fields.Where(f => f.HasConstant)) Line("surface", $"{n} = {f.Name} {Fmt(f.Constant)}");
                continue;
            }
            foreach (var f in t.Fields.Where(f => !f.Name.Contains('<'))) Line("surface", $"{n} F {FieldSig(f)}");
            foreach (var m in t.Methods.Where(m => !m.Name.Contains('<'))) Line("surface", $"{n} M {Sig(m)}");
        }
    }
}

/// <summary>The mod's own source, for lookups that only exist as strings.</summary>
sealed class Source
{
    public readonly List<(string file, string text)> Files = new();

    public Source(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, f);
            if (rel.Split(Path.DirectorySeparatorChar).Any(p => p is "obj" or "bin")) continue;
            Files.Add((Path.GetFileName(f), File.ReadAllText(f)));
        }
        Files.Sort((a, b) => string.CompareOrdinal(a.file, b.file));
    }

    string Text(string file) => Files.FirstOrDefault(f => f.file == file).text ?? throw new Exception("mod source file not found: " + file);

    public List<string> StringArray(string file, string field)
    {
        var m = Regex.Match(Text(file), field + @"\s*=\s*(?:new\s*\w*\[\]\s*)?\{(.*?)\};", RegexOptions.Singleline);
        if (!m.Success) throw new Exception($"{file}: could not find string array {field}");
        return Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).ToList();
    }

    public IEnumerable<(string type, List<string> fields)> AllowLists() =>
        Regex.Matches(Text("GlobalTargets.cs"), @"\[typeof\((\w+)\)\]\s*=\s*new HashSet<string>\s*\{(.*?)\}", RegexOptions.Singleline)
            .Select(m => (m.Groups[1].Value, Regex.Matches(m.Groups[2].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).ToList()));

    /// <summary>(type, has static target, has instance target) from GlobalTargets.Build.</summary>
    public IEnumerable<(string type, bool statics, bool instance)> GlobalTargetTypes()
    {
        var text = Text("GlobalTargets.cs");
        var d = new SortedDictionary<string, (bool s, bool i)>(StringComparer.Ordinal);
        void Set(string t, bool s, bool i) { d.TryGetValue(t, out var v); d[t] = (v.s || s, v.i || i); }
        foreach (Match m in Regex.Matches(text, @"Singleton\(typeof\((\w+)\)(\s*,\s*withStatics:\s*true)?\)")) Set(m.Groups[1].Value, m.Groups[2].Success, true);
        foreach (Match m in Regex.Matches(text, @"\bStatic\(typeof\((\w+)\)\)")) Set(m.Groups[1].Value, true, false);
        foreach (Match m in Regex.Matches(text, @"Key\s*=\s*""\w+"",\s*Type\s*=\s*typeof\((\w+)\)")) Set(m.Groups[1].Value, false, true);
        return d.Select(kv => (kv.Key, kv.Value.s, kv.Value.i));
    }

    public IEnumerable<string> Markers() =>
        Files.SelectMany(f => Regex.Matches(f.text, "\"(__[A-Z_]+__)\"").Select(m => m.Groups[1].Value)).Distinct().OrderBy(x => x, StringComparer.Ordinal);

    public IEnumerable<string> SatelliteNames() =>
        Regex.Matches(Text("EntityReplicator.cs"), @"==\s*""([A-Z]\w+)""").Select(m => m.Groups[1].Value).Distinct();
}

static class Diff
{
    // Section -> (priority, mod files to look at, what a change means)
    static readonly Dictionary<string, (int prio, string files, string meaning)> Info = new()
    {
        ["asm"] = (9, "Session/CoopSession.cs (GameFingerprint)", "Handshake fingerprint. A change is expected on any code update; players on different builds are refused."),
        ["compile-refs"] = (1, "see '<-' on each line", "Every game member the built mod references. MISSING = the build against the new game will fail (or a member moved)."),
        ["named-targets"] = (1, "Patches/*.cs, Commands/RouteList.cs, Replication/PresentationSync.cs, RadarHitSync.cs, Puppet/SimulationGate.cs", "Members looked up by name at runtime (Harmony / AccessTools / RouteList). New overloads -> ambiguous match (co-op disabled) or silent wrong pick; renamed parameters break Harmony by-name injection."),
        ["string-refs"] = (1, "Puppet/SimulationGate.cs, Replication/GlobalTargets.cs, Replication/SpawnCatalog.cs, Replication/EntityReplicator.cs", "String-only references. NOT FOUND / MISSING fail silently at runtime."),
        ["scoring-guards"] = (2, "Patches/ReplicationPatches.cs (ApplyScoringGuards)", "Scoring entry points made host-only by naming convention. Removed/renamed ones silently stop being guarded."),
        ["save"] = (2, "Bootstrap/WorldSync.cs, Replication/SpawnCatalog.cs", "Save schema + record layouts used for world join and entity spawns."),
        ["il-hash"] = (2, "Replication/SpawnCatalog.cs and the file named in each line", "Game method bodies the mod copies or relies on. Re-read the new decompile of each changed method."),
        ["sim-candidates"] = (3, "Puppet/SimulationGate.cs", "MonoBehaviours with Update/LateUpdate/FixedUpdate or TickManager callbacks. New 'NOT gated' simulation types may need adding to SimulationGate."),
        ["field-schemas"] = (3, "Replication/FieldCodec.cs, StateReplicator.cs, GlobalTargets.cs", "What field replication sends/drops per type. New DROP (unsupported type) on important state = not synced."),
        ["callers"] = (3, "Replication/RadarHitSync.cs, Patches/SandboxPatches.cs", "NotifyHit callers (ground vs forwarded hits) and file/cloud/prefs writers (sandbox coverage)."),
        ["monobehaviours"] = (4, "Replication/StateReplicator.cs, SpawnCatalog.cs (clone path)", "Type id order shifts automatically; check IContact types still have a contactId field."),
        ["surface"] = (5, "any file using the type", "Full member list of every game type the mod touches. Mostly context for the sections above."),
    };

    public static int Run(string oldPath, string newPath, string outPath)
    {
        var a = Parse(oldPath);
        var b = Parse(newPath);
        var sections = a.Keys.Union(b.Keys).OrderBy(s => Info.TryGetValue(s, out var i) ? i.prio : 8).ThenBy(s => s, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("# Reference diff (old -> new game build)").AppendLine();
        sb.AppendLine($"old: `{oldPath}`  ").AppendLine($"new: `{newPath}`").AppendLine();
        sb.AppendLine("| Prio | Section | Removed | Added | Look at |").AppendLine("|---|---|---|---|---|");
        int changed = 0;
        foreach (var s in sections)
        {
            var (rem, add) = Delta(a, b, s);
            if (rem.Count + add.Count > 0) changed++;
            var info = Info.GetValueOrDefault(s, (prio: 8, files: "", meaning: ""));
            sb.AppendLine($"| {info.prio} | [{s}](#{s}) | {rem.Count} | {add.Count} | {info.files} |");
        }
        sb.AppendLine();

        var alerts = new List<string>();
        foreach (var l in b.Values.SelectMany(x => x))
            if (Regex.IsMatch(l, @"\bMISSING\b|NOT FOUND|NOWHERE|!!")) alerts.Add(l);
        var oldAlerts = new HashSet<string>(a.Values.SelectMany(x => x).Where(l => Regex.IsMatch(l, @"\bMISSING\b|NOT FOUND|NOWHERE|!!")));
        var newAlerts = alerts.Where(l => !oldAlerts.Contains(l)).ToList();
        sb.AppendLine("## Alerts in the new build").AppendLine();
        if (newAlerts.Count == 0) sb.AppendLine("None (no new MISSING / NOT FOUND / !! lines).");
        else { sb.AppendLine("```"); foreach (var l in newAlerts) sb.AppendLine(l); sb.AppendLine("```"); }
        sb.AppendLine();

        foreach (var s in sections)
        {
            var (rem, add) = Delta(a, b, s);
            if (rem.Count + add.Count == 0) continue;
            var info = Info.GetValueOrDefault(s, (prio: 8, files: "", meaning: ""));
            sb.AppendLine($"## {s}").AppendLine().AppendLine(info.meaning).AppendLine().AppendLine($"Look at: {info.files}").AppendLine();
            var merged = rem.Select(l => (l, '-')).Concat(add.Select(l => (l, '+'))).OrderBy(x => x.l, StringComparer.Ordinal).ToList();
            const int cap = 600;
            sb.AppendLine("```diff");
            foreach (var (l, sign) in merged.Take(cap)) sb.Append(sign).Append(' ').AppendLine(l);
            sb.AppendLine("```");
            if (merged.Count > cap) sb.AppendLine($"... {merged.Count - cap} more lines; diff the two refdump files directly.");
            sb.AppendLine();
        }
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine($"{changed} section(s) changed, {newAlerts.Count} new alert(s) -> {outPath}");
        return 0;
    }

    static (List<string> rem, List<string> add) Delta(Dictionary<string, HashSet<string>> a, Dictionary<string, HashSet<string>> b, string s)
    {
        var x = a.GetValueOrDefault(s) ?? new HashSet<string>();
        var y = b.GetValueOrDefault(s) ?? new HashSet<string>();
        return (x.Except(y).ToList(), y.Except(x).ToList());
    }

    static Dictionary<string, HashSet<string>> Parse(string path)
    {
        var d = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        HashSet<string> cur = null;
        foreach (var l in File.ReadLines(path))
        {
            if (l.StartsWith("## ", StringComparison.Ordinal)) d[l.Substring(3)] = cur = new HashSet<string>(StringComparer.Ordinal);
            else if (cur != null && l.Length > 0) cur.Add(l);
        }
        return d;
    }
}
