// UsharpCheck - offline compile + UdonSharp feature lint + Udon extern exposure check for U# scripts.
// Scratch harness used by check_usharp.py (NOT shipped). Runs on .NET 8 with the SDK's Roslyn 4.8.
//
// Rules mirror UdonSharp 1.x as bundled in VRChat Worlds SDK 3.10.5 (Integrations/UdonSharp/Editor/Compiler):
//   - BinderSyntaxVisitor: only the syntax kinds it overrides are bindable; DefaultVisit throws CE_NodeNotSupported
//   - UdonSharpBehaviourMethodSymbol / FieldSymbol / *TypeSymbol: ctor, generic method, static field, nested type, partial
//   - UdonSharpCompilerV1: class name == file name, sync-type validation (CheckSyncCompatibility)
//   - CompilationContext.BuildMethodLayout / ValidateNetworkCallableMethod: event names, [NetworkCallable]
//   - CompilerUdonInterface.GetUdonMethodName / GetUdonAccessorName / GetUdonTypeName + UdonSharpUtils.RemapBaseType:
//     Udon extern signature naming, checked against the table extracted from VRC.Udon.Wrapper(.Modules).dll
// Output: "path(line,col): error|warning CODE: message", summary line, exit 0 = no errors (and no warnings with --strict).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

public static class Program
{
    static string _roslynDir;

    public static int Main(string[] args)
    {
        _roslynDir = Environment.GetEnvironmentVariable("USHARP_ROSLYN_DIR");
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            if (_roslynDir == null) return null;
            var p = Path.Combine(_roslynDir, name.Name + ".dll");
            return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
        };
        return Checker.Run(args);
    }
}

sealed class Diag
{
    public string File; public int Line; public int Col; public string Severity; public string Code; public string Message;
    public override string ToString() => $"{File}({Line},{Col}): {Severity} {Code}: {Message}";
}

static class Checker
{
    sealed class Options
    {
        public string Refs, Externs, SyncTypes, Events;
        public List<string> Defines = new List<string>();
        public List<string> Files = new List<string>();
        public bool NoExterns, Strict, RequireSyncMode = true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run(string[] argv)
    {
        var o = new Options();
        for (int i = 0; i < argv.Length; i++)
        {
            switch (argv[i])
            {
                case "--refs": o.Refs = argv[++i]; break;
                case "--externs": o.Externs = argv[++i]; break;
                case "--synctypes": o.SyncTypes = argv[++i]; break;
                case "--events": o.Events = argv[++i]; break;
                case "--define": o.Defines.AddRange(argv[++i].Split(';', StringSplitOptions.RemoveEmptyEntries)); break;
                case "--no-externs": o.NoExterns = true; break;
                case "--dump-sigs": Lint.DumpSigs = true; break;
                case "--strict": o.Strict = true; break;
                case "--no-require-syncmode": o.RequireSyncMode = false; break;
                default: o.Files.Add(argv[i]); break;
            }
        }
        if (o.Refs == null || o.Files.Count == 0)
        {
            Console.Error.WriteLine("usage: UsharpCheck --refs refs.txt [--externs t.txt] [--synctypes UdonNetworkTypes.cs] [--events events.txt] [--define A;B] [--strict] [--no-externs] files...");
            return 2;
        }

        var parse = new CSharpParseOptions(LanguageVersion.CSharp9, DocumentationMode.None, SourceCodeKind.Regular, o.Defines);
        var trees = new List<SyntaxTree>();
        foreach (var f in o.Files)
            trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(f), parse, Path.GetFullPath(f), Encoding.UTF8));

        var refs = File.ReadAllLines(o.Refs).Where(l => l.Trim().Length > 0).Select(l => (MetadataReference)MetadataReference.CreateFromFile(l.Trim())).ToList();
        var copts = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: false, nullableContextOptions: NullableContextOptions.Disable)
            .WithMetadataImportOptions(MetadataImportOptions.All);
        var comp = CSharpCompilation.Create("UsharpCheckAssembly", trees, refs, copts);

        var diags = new List<Diag>();
        var suppressed = new HashSet<string> { "CS1701", "CS1702", "CS0649", "CS0169", "CS0414", "CS8019", "CS1591", "CS0067" };
        foreach (var d in comp.GetDiagnostics())
        {
            if (d.Severity == DiagnosticSeverity.Error || (d.Severity == DiagnosticSeverity.Warning && !suppressed.Contains(d.Id)))
                diags.Add(Make(d.Location, d.Severity == DiagnosticSeverity.Error ? "error" : "warning", d.Id, d.GetMessage()));
        }
        bool compiled = !diags.Any(d => d.Severity == "error");

        HashSet<string> externs = null;
        if (!o.NoExterns && o.Externs != null && File.Exists(o.Externs))
            externs = new HashSet<string>(File.ReadAllLines(o.Externs), StringComparer.Ordinal);
        var sync = SyncTypes.Load(o.SyncTypes);
        var events = o.Events != null && File.Exists(o.Events) ? new HashSet<string>(File.ReadAllLines(o.Events).Where(l => l.Length > 0)) : new HashSet<string>();

        var lint = new Lint(comp, diags, externs, sync, events, o.RequireSyncMode);
        foreach (var t in trees) lint.CheckTree(t);

        foreach (var d in diags.OrderBy(d => d.File).ThenBy(d => d.Line).ThenBy(d => d.Col)) Console.WriteLine(d);
        int errors = diags.Count(d => d.Severity == "error"), warnings = diags.Count(d => d.Severity == "warning");
        bool ok = errors == 0 && (!o.Strict || warnings == 0);
        Console.WriteLine($"{(ok ? "USHARP CHECK OK" : "USHARP CHECK FAILED")} ({o.Files.Count} files, {errors} errors, {warnings} warnings, compile {(compiled ? "ok" : "failed")}, externs {(externs != null ? externs.Count + " sigs" : "skipped")}{(o.Strict ? ", strict" : "")})");
        return ok ? 0 : 1;
    }

    public static Diag Make(Location loc, string sev, string code, string msg)
    {
        var span = loc != null && loc.IsInSource ? loc.GetLineSpan() : default;
        return new Diag
        {
            File = loc != null && loc.IsInSource ? span.Path : "?",
            Line = loc != null && loc.IsInSource ? span.StartLinePosition.Line + 1 : 0,
            Col = loc != null && loc.IsInSource ? span.StartLinePosition.Character + 1 : 0,
            Severity = sev, Code = code, Message = msg,
        };
    }
}

/// <summary>Syncable types parsed from the SDK's VRC.Udon/UdonNetworkTypes.cs (typeof(...) entries per set).</summary>
sealed class SyncTypes
{
    public HashSet<string> Sync = new HashSet<string>(), Linear = new HashSet<string>(), Smooth = new HashSet<string>();

    static readonly Dictionary<string, string> Keywords = new Dictionary<string, string>
    {
        ["bool"] = "System.Boolean", ["char"] = "System.Char", ["byte"] = "System.Byte", ["sbyte"] = "System.SByte",
        ["short"] = "System.Int16", ["ushort"] = "System.UInt16", ["int"] = "System.Int32", ["uint"] = "System.UInt32",
        ["long"] = "System.Int64", ["ulong"] = "System.UInt64", ["float"] = "System.Single", ["double"] = "System.Double",
        ["string"] = "System.String", ["SDKBase.VRCUrl"] = "VRC.SDKBase.VRCUrl",
    };

    public static SyncTypes Load(string path)
    {
        var s = new SyncTypes();
        if (path == null || !File.Exists(path)) return s;
        var text = File.ReadAllText(path);
        foreach (var (field, set) in new[] { ("_syncTypes", s.Sync), ("_linearTypes", s.Linear), ("_smoothTypes", s.Smooth) })
        {
            var m = Regex.Match(text, field + @"\s*=\s*new\s+HashSet<Type>\s*\{(.*?)\};", RegexOptions.Singleline);
            if (!m.Success) continue;
            foreach (Match t in Regex.Matches(m.Groups[1].Value, @"typeof\(([\w\.]+)(\[\])?\)"))
            {
                var name = t.Groups[1].Value;
                name = Keywords.TryGetValue(name, out var k) ? k : (name.Contains('.') ? name : "UnityEngine." + name);
                set.Add(name + t.Groups[2].Value);
            }
        }
        return s;
    }
}

sealed class Lint
{
    readonly CSharpCompilation _comp;
    readonly List<Diag> _diags;
    readonly HashSet<string> _externs;
    readonly SyncTypes _sync;
    readonly HashSet<string> _udonEventNames;
    readonly bool _requireSyncMode;
    readonly INamedTypeSymbol _usb, _udonBehaviour, _component, _transform, _unityObject, _systemArray, _gameObject;
    readonly Dictionary<INamedTypeSymbol, INamedTypeSymbol> _remap = new Dictionary<INamedTypeSymbol, INamedTypeSymbol>(SymbolEqualityComparer.Default);
    readonly HashSet<string> _reported = new HashSet<string>();
    readonly HashSet<string> _modules = new HashSet<string>(StringComparer.Ordinal); // Udon types that have a wrapper module
    readonly Dictionary<string, bool> _enumSeen = new Dictionary<string, bool>(StringComparer.Ordinal); // extern enum -> used by an exposed sig

    static readonly HashSet<string> GetComponentNames = new HashSet<string>
        { "GetComponent", "GetComponents", "GetComponentInChildren", "GetComponentsInChildren", "GetComponentInParent", "GetComponentsInParent" };

    // Unity messages Udon never forwards (no "_awake" etc. in VRC.Udon/UdonBehaviour.cs RunEvent calls).
    static readonly HashSet<string> NotForwardedUnityMessages = new HashSet<string>
    {
        "Awake", "OnGUI", "OnApplicationQuit", "OnApplicationPause", "OnApplicationFocus", "Reset",
        "OnTriggerEnter2D", "OnTriggerExit2D", "OnTriggerStay2D", "OnCollisionEnter2D", "OnCollisionExit2D", "OnCollisionStay2D",
    };

    public Lint(CSharpCompilation comp, List<Diag> diags, HashSet<string> externs, SyncTypes sync, HashSet<string> udonEvents, bool requireSyncMode)
    {
        _comp = comp; _diags = diags; _externs = externs; _sync = sync; _udonEventNames = udonEvents; _requireSyncMode = requireSyncMode;
        if (externs != null)
            foreach (var sig in externs) { int dot = sig.IndexOf('.'); if (dot > 0) _modules.Add(sig.Substring(0, dot)); }
        _usb = comp.GetTypeByMetadataName("UdonSharp.UdonSharpBehaviour");
        _udonBehaviour = comp.GetTypeByMetadataName("VRC.Udon.UdonBehaviour");
        _component = comp.GetTypeByMetadataName("UnityEngine.Component");
        _transform = comp.GetTypeByMetadataName("UnityEngine.Transform");
        _unityObject = comp.GetTypeByMetadataName("UnityEngine.Object");
        _gameObject = comp.GetTypeByMetadataName("UnityEngine.GameObject");
        _systemArray = comp.GetSpecialType(SpecialType.System_Array);
        BuildRemap();
    }

    void Report(SyntaxNode node, string sev, string code, string msg) => Report(node?.GetLocation(), sev, code, msg);

    void Report(Location loc, string sev, string code, string msg)
    {
        var d = Checker.Make(loc, sev, code, msg);
        if (_reported.Add(d.ToString())) _diags.Add(d);
    }

    // ------------------------------------------------------------------ type helpers
    bool IsSource(ISymbol s) => s != null && SymbolEqualityComparer.Default.Equals(s.ContainingAssembly, _comp.Assembly);

    bool InheritsFrom(ITypeSymbol t, INamedTypeSymbol baseType)
    {
        for (var c = t as INamedTypeSymbol; c != null; c = c.BaseType)
            if (SymbolEqualityComparer.Default.Equals(c.OriginalDefinition, baseType)) return true;
        return false;
    }

    bool IsUsb(ITypeSymbol t) => _usb != null && InheritsFrom(t, _usb);

    void BuildRemap()
    {
        // UdonSharpUtils.GetInheritedTypeMap: VRCSDK3 types whose base type lives in VRC.SDKBase (except VRCNetworkBehaviour)
        var sdk3 = _comp.References.Select(r => _comp.GetAssemblyOrModuleSymbol(r) as IAssemblySymbol).FirstOrDefault(a => a?.Name == "VRCSDK3");
        if (sdk3 != null)
        {
            foreach (var t in AllTypes(sdk3.GlobalNamespace))
            {
                var b = t.BaseType;
                var ns = b?.ContainingNamespace?.ToDisplayString();
                if (b != null && ns != null && ns.StartsWith("VRC.SDKBase", StringComparison.Ordinal) && b.Name != "VRCNetworkBehaviour" && !_remap.ContainsKey(b))
                    _remap[b] = t;
            }
        }
        var basePlayer = _comp.GetTypeByMetadataName("VRC.SDK3.Video.Components.Base.BaseVRCVideoPlayer");
        foreach (var n in new[] { "VRC.SDK3.Video.Components.VRCUnityVideoPlayer", "VRC.SDK3.Video.Components.AVPro.VRCAVProVideoPlayer" })
        {
            var t = _comp.GetTypeByMetadataName(n);
            if (t != null && basePlayer != null) _remap[t] = basePlayer;
        }
    }

    static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
    {
        foreach (var t in ns.GetTypeMembers()) { yield return t; foreach (var n in Nested(t)) yield return n; }
        foreach (var c in ns.GetNamespaceMembers()) foreach (var t in AllTypes(c)) yield return t;
    }

    static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol t)
    {
        foreach (var n in t.GetTypeMembers()) { yield return n; foreach (var x in Nested(n)) yield return x; }
    }

    ITypeSymbol RemapBase(ITypeSymbol t)
    {
        int depth = 0;
        var cur = t;
        while (cur is IArrayTypeSymbol a) { cur = a.ElementType; depth++; }
        if (cur is INamedTypeSymbol n && _remap.TryGetValue(n, out var mapped))
        {
            ITypeSymbol r = mapped;
            for (int i = 0; i < depth; i++) r = _comp.CreateArrayTypeSymbol(r);
            return r;
        }
        return t;
    }

    /// <summary>UdonSharpUtils.UserTypeToUdonType + RemapBaseType.</summary>
    ITypeSymbol UdonType(ITypeSymbol t)
    {
        if (t is IArrayTypeSymbol a)
        {
            if (a.ElementType is IArrayTypeSymbol) return _comp.CreateArrayTypeSymbol(_comp.GetSpecialType(SpecialType.System_Object));
            var e = a.ElementType;
            if (IsUsb(e) || (_udonBehaviour != null && InheritsFrom(e, _udonBehaviour)))
                return _comp.CreateArrayTypeSymbol(_component);
            if (e.TypeKind == TypeKind.Enum && IsSource(e))
                return _comp.CreateArrayTypeSymbol(((INamedTypeSymbol)e).EnumUnderlyingType);
            return RemapBase(t);
        }
        if (IsUsb(t)) return _udonBehaviour ?? t;
        if (t.TypeKind == TypeKind.Enum && IsSource(t)) return ((INamedTypeSymbol)t).EnumUnderlyingType;
        return RemapBase(t);
    }

    static string Sanitize(string s) => s.Replace(",", "").Replace(".", "").Replace("[]", "Array").Replace("&", "Ref").Replace("+", "");

    static string Ns(ITypeSymbol t)
    {
        var ns = t.ContainingNamespace;
        return ns == null || ns.IsGlobalNamespace ? null : ns.ToDisplayString();
    }

    /// <summary>System.Type.FullName equivalent for non-generic types (namespace.Outer+Inner, arrays as []).</summary>
    static string FullName(ITypeSymbol t)
    {
        if (t is IArrayTypeSymbol a) return FullName(a.ElementType) + "[]";
        if (t is ITypeParameterSymbol) return null;
        var name = t.MetadataName;
        for (var c = t.ContainingType; c != null; c = c.ContainingType) name = c.MetadataName + "+" + name;
        var ns = Ns(t);
        return ns == null ? name : ns + "." + name;
    }

    /// <summary>CompilerUdonInterface.GetUdonTypeName(Type).</summary>
    string TypeName(ITypeSymbol t)
    {
        string NameWithoutArity(ITypeSymbol x)
        {
            if (x is IArrayTypeSymbol arr) return NameWithoutArity(arr.ElementType) + "[]";
            return x.Name; // Roslyn Name has no `N arity suffix
        }
        var externTypeName = NameWithoutArity(t);
        var elem = t;
        while (elem is IArrayTypeSymbol a) elem = a.ElementType;
        string typeNamespace = elem is ITypeParameterSymbol ? null : Ns(elem);
        if (elem.ContainingType != null)
        {
            // mirrors the original loop, which always prepends the *immediate* declaring type's name
            string declaringTypeNamespace = "";
            for (var d = elem.ContainingType; d != null; d = d.ContainingType)
                declaringTypeNamespace = $"{elem.ContainingType.Name}.{declaringTypeNamespace}";
            typeNamespace += $".{declaringTypeNamespace}";
        }
        if (externTypeName == "T" || externTypeName == "T[]") typeNamespace = "";
        var full = Sanitize($"{typeNamespace}.{externTypeName}");
        if (elem is INamedTypeSymbol n && n.IsGenericType)
            foreach (var ga in n.TypeArguments) full += TypeName(ga);
        if (full == "SystemCollectionsGenericListT") full = "ListT";
        else if (full == "SystemCollectionsGenericIEnumerableT") full = "IEnumerableT";
        return full;
    }

    /// <summary>GetUdonTypeName(TypeSymbol): "T" for type parameters, else the Udon-mapped type's name.</summary>
    string SymTypeName(ITypeSymbol t) => t is ITypeParameterSymbol ? "T" : TypeName(UdonType(t));

    string MethodNamespace(ITypeSymbol containing)
    {
        var src = RemapBase(UdonType(containing));
        var fn = FullName(src) ?? (Ns(src) + src.Name);
        return Sanitize(fn).Replace("VRCUdonUdonBehaviour", "VRCUdonCommonInterfacesIUdonEventReceiver");
    }

    /// <summary>CompilerUdonInterface.GetUdonMethodName(ExternMethodSymbol) with an optional containing-type override (alternate lookup).</summary>
    string MethodSig(IMethodSymbol m, ITypeSymbol containingOverride = null)
    {
        var ns = MethodNamespace(containingOverride ?? m.ContainingType);
        var name = "__" + m.Name.Trim('_').TrimStart('.');
        string paramStr = "";
        if (m.Parameters.Length > 0)
        {
            paramStr = "_";
            foreach (var p in m.Parameters)
            {
                paramStr += "_" + SymTypeName(p.Type);
                if (p.RefKind != RefKind.None) paramStr += "Ref";
            }
        }
        else if (m.MethodKind == MethodKind.Constructor) paramStr = "__";
        string ret;
        if (m.MethodKind == MethodKind.Constructor) ret = "__" + SymTypeName(containingOverride ?? m.ContainingType);
        else if (!IsSource(m.ReturnType) && Ns(m.ReturnType) == "VRC.SDKBase" && !(m.ReturnType is IArrayTypeSymbol)) ret = "__" + TypeName(m.ReturnType);
        else ret = "__" + SymTypeName(m.ReturnType);
        return $"{ns}.{name}{paramStr}{ret}";
    }

    /// <summary>CompilerUdonInterface.GetUdonAccessorName(FieldSymbol, ...).</summary>
    string FieldSig(IFieldSymbol f, bool set, ITypeSymbol containingOverride = null)
    {
        var containing = RemapBase(containingOverride ?? f.ContainingType);
        var ns = Sanitize(FullName(containing) ?? containing.Name)
            .Replace("VRCUdonUdonBehaviour", "VRCUdonCommonInterfacesIUdonEventReceiver")
            .Replace("UdonSharpUdonSharpBehaviour", "VRCUdonCommonInterfacesIUdonEventReceiver");
        return $"{ns}.__{(set ? "set" : "get")}_{f.Name.Trim('_')}__{SymTypeName(f.Type)}";
    }

    IEnumerable<ITypeSymbol> AlternateTypes(ITypeSymbol receiver)
    {
        if (receiver == null) yield break;
        foreach (var i in receiver.AllInterfaces) yield return i;
        for (var t = receiver; t != null; t = t.BaseType) yield return t;
    }

    bool Exposed(string sig) => _externs.Contains(sig);

    // ------------------------------------------------------------------ per-file checks
    public void CheckTree(SyntaxTree tree)
    {
        var model = _comp.GetSemanticModel(tree);
        var root = tree.GetRoot();
        var fileName = Path.GetFileNameWithoutExtension(tree.FilePath);
        int usbCount = 0;

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case DelegateDeclarationSyntax d:
                    Report(d.Identifier.GetLocation(), "error", "USL001", "U# はデリゲート型の宣言をサポートしません (Udon にデリゲートは無い)");
                    break;
                case BaseTypeDeclarationSyntax td:
                    usbCount += CheckTypeDeclaration(td, model, fileName);
                    break;
            }
            CheckSyntaxNode(node, model);
        }
        if (usbCount > 1)
            Report(root, "error", "USL005", "1 ファイルに UdonSharpBehaviour は 1 クラスのみ (U# は最初の 1 つだけをプログラムのルートにする: UdonSharpCompilerV1)");

        // semantic walk over every executable body (methods, accessors, field initializers)
        foreach (var body in root.DescendantNodes().Where(n => n is BlockSyntax || n is ArrowExpressionClauseSyntax || n is EqualsValueClauseSyntax))
        {
            if (!IsOutermostBody(body)) continue; // nested blocks are covered by their member's body
            var op = model.GetOperation(body);
            if (op == null) continue;
            var owner = model.GetEnclosingSymbol(body.SpanStart);
            bool isFieldInit = body is EqualsValueClauseSyntax && body.Parent is VariableDeclaratorSyntax && body.Parent.Parent?.Parent is FieldDeclarationSyntax;
            CheckOperations(op, model, owner, isFieldInit);
        }
    }

    static bool IsOutermostBody(SyntaxNode body)
    {
        switch (body.Parent)
        {
            case BaseMethodDeclarationSyntax _:
            case AccessorDeclarationSyntax _:
            case PropertyDeclarationSyntax _ when body is ArrowExpressionClauseSyntax || body is EqualsValueClauseSyntax:
            case IndexerDeclarationSyntax _ when body is ArrowExpressionClauseSyntax:
                return true;
            case VariableDeclaratorSyntax v when v.Parent?.Parent is FieldDeclarationSyntax:
                return true;
            case ParameterSyntax _:
                return false; // default values are constants
        }
        return false;
    }

    int CheckTypeDeclaration(BaseTypeDeclarationSyntax td, SemanticModel model, string fileName)
    {
        var sym = model.GetDeclaredSymbol(td) as INamedTypeSymbol;
        if (sym == null) return 0;
        if (td.Parent is BaseTypeDeclarationSyntax)
            Report(td.Identifier.GetLocation(), "error", "USL002", "入れ子の型宣言は U# 未対応 (\"Nested type declarations are not currently supported by U#\")");

        switch (td)
        {
            case InterfaceDeclarationSyntax _:
                Report(td.Identifier.GetLocation(), "error", "USL001", "U# は interface をサポートしません (公式 FAQ: Interfaces)");
                return 0;
            case StructDeclarationSyntax _:
                Report(td.Identifier.GetLocation(), "error", "USL001", "U# はユーザー定義 struct をサポートしません");
                return 0;
            case RecordDeclarationSyntax _:
                Report(td.Identifier.GetLocation(), "error", "USL001", "U# は record をサポートしません");
                return 0;
            case EnumDeclarationSyntax _:
                return 0; // user enums are supported (ImportedUdonSharpTypeSymbol maps them to the underlying type)
        }

        var cls = (ClassDeclarationSyntax)td;
        bool isUsb = IsUsb(sym);
        if (!isUsb)
        {
            if (!sym.IsStatic)
                Report(cls.Identifier.GetLocation(), "error", "USL003", $"'{sym.Name}' は UdonSharpBehaviour を継承していない通常クラスです。U# でインスタンス化・使用できません (static クラスの static メソッドのみ可)");
            foreach (var f in sym.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsStatic && !f.IsConst && !f.IsImplicitlyDeclared))
                Report(f.Locations.FirstOrDefault(), "error", "USL009", $"static フィールド '{f.Name}' は U# 未対応 (\"Static fields are not yet supported on user defined types\")。const にするか インスタンスフィールドにする");
            return 0;
        }

        if (!sym.IsAbstract && sym.Name != fileName)
            Report(cls.Identifier.GetLocation(), "error", "USL004", $"UdonSharpBehaviour のクラス名 '{sym.Name}' はファイル名 '{fileName}.cs' と一致する必要があります");
        if (sym.IsGenericType && !sym.IsAbstract)
            Report(cls.Identifier.GetLocation(), "error", "USL006", "ジェネリックな U# behaviour はプログラムアセットを持てません (\"Generic U# behaviours cannot have an associated U# program asset\")");
        foreach (var i in sym.Interfaces)
            Report(cls.Identifier.GetLocation(), "error", "USL001", $"UdonSharpBehaviour は interface ('{i.Name}') を実装できません");

        var syncMode = GetSyncMode(sym);
        if (_requireSyncMode && !sym.IsAbstract && syncMode == null)
            Report(cls.Identifier.GetLocation(), "warning", "USL013", "[UdonBehaviourSyncMode(...)] を明示してください (None / NoVariableSync / Manual / Continuous)。省略時は Any でインスペクター次第になる");

        foreach (var member in sym.GetMembers())
        {
            if (member.IsImplicitlyDeclared) continue;
            var loc = member.Locations.FirstOrDefault();
            switch (member)
            {
                case IMethodSymbol m:
                    CheckMethodDecl(sym, m, loc);
                    break;
                case IFieldSymbol f:
                    CheckFieldDecl(sym, f, loc, syncMode);
                    break;
                case IEventSymbol e:
                    Report(loc, "error", "USL001", $"C# event '{e.Name}' は U# 未対応");
                    break;
                case IPropertySymbol p when p.IsIndexer:
                    Report(loc, "warning", "USL001", "ユーザー定義インデクサーは U# で未検証の機能です。メソッドに置き換えてください");
                    break;
                case INamedTypeSymbol _:
                    break; // reported as USL002 by its own declaration
            }
        }
        return 1;
    }

    string GetSyncMode(INamedTypeSymbol sym)
    {
        for (var t = sym; t != null && IsSource(t); t = t.BaseType)
        {
            var a = t.GetAttributes().FirstOrDefault(x => x.AttributeClass?.Name == "UdonBehaviourSyncModeAttribute");
            if (a == null) continue;
            if (a.ConstructorArguments.Length == 0) return "Any";
            return EnumName(a.ConstructorArguments[0]) ?? "Any";
        }
        return null;
    }

    static string EnumName(TypedConstant c)
    {
        if (c.Type is INamedTypeSymbol et && et.TypeKind == TypeKind.Enum)
            foreach (var f in et.GetMembers().OfType<IFieldSymbol>())
                if (f.HasConstantValue && Equals(Convert.ToInt64(f.ConstantValue), Convert.ToInt64(c.Value))) return f.Name;
        return c.Value?.ToString();
    }

    void CheckMethodDecl(INamedTypeSymbol cls, IMethodSymbol m, Location loc)
    {
        switch (m.MethodKind)
        {
            case MethodKind.Constructor:
            case MethodKind.StaticConstructor:
                Report(loc, "error", "USL007", "UdonSharpBehaviour にコンストラクターは書けません (CE_UdonSharpBehaviourConstructorsNotSupported)。初期化は Start() で");
                return;
            case MethodKind.Destructor:
                Report(loc, "error", "USL007", "デストラクターは U# 未対応");
                return;
            case MethodKind.UserDefinedOperator:
            case MethodKind.Conversion:
                Report(loc, "error", "USL001", "ユーザー定義演算子/変換は U# 未対応");
                return;
            case MethodKind.ExplicitInterfaceImplementation:
                Report(loc, "error", "USL001", "interface の明示的実装は U# 未対応");
                return;
            case MethodKind.PropertyGet:
            case MethodKind.PropertySet:
                return;
        }
        if (m.MethodKind != MethodKind.Ordinary) return;
        if (m.IsGenericMethod)
            Report(loc, m.IsStatic ? "warning" : "error", "USL008",
                m.IsStatic ? "static ジェネリックメソッドは U# で限定的にしかサポートされません。非ジェネリックにしてください"
                           : "UdonSharpBehaviour のジェネリックメソッドは U# 未対応 (CE_UdonSharpBehaviourGenericMethodsNotSupported)");
        if (m.IsAsync)
            Report(loc, "error", "USL102", "async メソッドは U# 未対応 (Udon にタスク/await は無い)");
        if (m.PartialImplementationPart != null || m.PartialDefinitionPart != null)
            Report(loc, "error", "USL010", "partial メソッドは U# 未対応 (CE_PartialMethodsNotSupported)");
        if (m.ReturnType is INamedTypeSymbol rt && (rt.Name == "IEnumerator" || rt.Name == "IEnumerable") && Ns(rt)?.StartsWith("System.Collections") == true)
            Report(loc, "error", "USL102", "コルーチン/イテレーター (IEnumerator, yield) は U# 未対応。SendCustomEventDelayedSeconds を使用");

        bool isNetworkCallable = m.GetAttributes().Any(a => a.AttributeClass?.Name == "NetworkCallableAttribute");
        CheckEventName(cls, m, loc, isNetworkCallable);
        if (isNetworkCallable) CheckNetworkCallable(cls, m, loc);

        foreach (var p in m.Parameters) CheckDeclaredType(p.Type, p.Locations.FirstOrDefault(), "パラメーター");
        CheckDeclaredType(m.ReturnType, loc, "戻り値");
    }

    void CheckEventName(INamedTypeSymbol cls, IMethodSymbol m, Location loc, bool isNetworkCallable)
    {
        if (NotForwardedUnityMessages.Contains(m.Name) && !m.IsStatic)
        {
            Report(loc, "warning", "USL019", $"'{m.Name}' は Udon イベントとして呼ばれません (VRC.Udon UdonBehaviour が転送しない Unity メッセージ)。初期化は Start() を使用");
            return;
        }
        if (_usb == null || m.IsStatic) return;
        var baseEvents = _usb.GetMembers(m.Name).OfType<IMethodSymbol>().Where(x => x.IsVirtual).ToList();
        bool isEvent = baseEvents.Count > 0 || _udonEventNames.Contains(m.Name);
        if (!isEvent) return;
        if (isNetworkCallable)
            Report(loc, "error", "USL016", $"組み込み Udon イベント '{m.Name}' に [NetworkCallable] は付けられません");
        if (baseEvents.Count > 0)
        {
            bool matches = baseEvents.Any(b => b.Parameters.Length == m.Parameters.Length &&
                b.Parameters.Zip(m.Parameters, (x, y) => SymbolEqualityComparer.Default.Equals(x.Type, y.Type)).All(z => z));
            if (!matches && m.Parameters.Length > 0)
                Report(loc, "error", "USL012", $"組み込みイベント '{m.Name}' と同名でパラメーター型が一致しません (\"Method with same name as built-in event ... cannot be declared with parameter types that do not match the event\")");
            else if (!matches && m.Parameters.Length == 0 && baseEvents.All(b => b.Parameters.Length > 0))
                Report(loc, "warning", "USL012", $"'{m.Name}' は引数付きイベントです。'public override void {m.Name}({string.Join(", ", baseEvents[0].Parameters.Select(p => p.Type.Name + " " + p.Name))})' を使ってください");
            else if (matches && !m.IsOverride)
                Report(loc, "warning", "USL012", $"'{m.Name}' には override を付けてください");
            var obsolete = baseEvents.FirstOrDefault(b => b.Parameters.Length == m.Parameters.Length && b.GetAttributes().Any(a => a.AttributeClass?.Name == "ObsoleteAttribute"));
            if (obsolete != null)
                Report(loc, "error", "USL012", $"'{m.Name}()' は廃止イベントです。VRCPlayerApi 引数版を使ってください");
        }
    }

    void CheckNetworkCallable(INamedTypeSymbol cls, IMethodSymbol m, Location loc)
    {
        void Fail(string why) => Report(loc, "error", "USL016", $"[NetworkCallable] '{m.Name}': {why} (CompilationContext.ValidateNetworkCallableMethod / docs networking/events)");
        if (m.DeclaredAccessibility != Accessibility.Public) Fail("public である必要があります");
        if (m.IsStatic) Fail("static は不可");
        if (m.IsVirtual || m.IsAbstract || m.IsOverride || m.IsSealed) Fail("virtual/abstract/override/sealed は不可");
        if (!m.ReturnsVoid) Fail("戻り値は void のみ");
        if (m.IsGenericMethod) Fail("ジェネリック不可");
        if (m.Parameters.Length > 8) Fail("パラメーターは 8 個まで");
        foreach (var p in m.Parameters)
        {
            if (p.IsParams) Fail("params は不可");
            if (p.HasExplicitDefaultValue) Fail("既定値付きパラメーターは不可");
            if (!CanSync(p.Type)) Fail($"パラメーター '{p.Name}' の型 {p.Type.ToDisplayString()} は同期可能型ではありません");
        }
        if (cls.GetMembers(m.Name).OfType<IMethodSymbol>().Count() > 1) Fail("オーバーロード不可 (同名メソッドが他にあります)");
    }

    bool CanSync(ITypeSymbol t, HashSet<string> set = null)
    {
        set = set ?? _sync.Sync;
        if (set.Count == 0) return true; // table not available: do not guess
        var u = t;
        if (t is IArrayTypeSymbol a && a.ElementType.TypeKind == TypeKind.Enum && IsSource(a.ElementType)) u = _comp.CreateArrayTypeSymbol(((INamedTypeSymbol)a.ElementType).EnumUnderlyingType);
        else if (t.TypeKind == TypeKind.Enum && IsSource(t)) u = ((INamedTypeSymbol)t).EnumUnderlyingType;
        var name = FullName(u)?.Replace('+', '.');
        return name != null && set.Contains(name);
    }

    void CheckFieldDecl(INamedTypeSymbol cls, IFieldSymbol f, Location loc, string syncMode)
    {
        if (f.IsStatic && !f.IsConst)
            Report(loc, "error", "USL009", $"static フィールド '{f.Name}' は U# 未対応 (\"Static fields are not yet supported on user defined types\")");
        CheckDeclaredType(f.Type, loc, "フィールド");

        // hiding a field of a user-defined base class
        for (var b = cls.BaseType; b != null && IsSource(b); b = b.BaseType)
            if (b.GetMembers(f.Name).OfType<IFieldSymbol>().Any(x => !x.IsConst))
                Report(loc, "error", "USL011", $"基底クラスのフィールド '{f.Name}' を隠すことはできません (\"U# does not yet support hiding base fields\")");

        var synced = f.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "UdonSyncedAttribute");
        if (synced != null)
        {
            string interp = synced.ConstructorArguments.Length > 0 ? EnumName(synced.ConstructorArguments[0]) : "None";
            bool userNonEnum = IsSource(f.Type is IArrayTypeSymbol arr ? arr.ElementType : f.Type) &&
                               (f.Type is IArrayTypeSymbol arr2 ? arr2.ElementType : f.Type).TypeKind != TypeKind.Enum;
            if (userNonEnum || !CanSync(f.Type))
                Report(loc, "error", "USL014", $"'{f.Name}' の型 {f.Type.ToDisplayString()} は Udon で同期できません (UdonNetworkTypes.CanSync)");
            switch (syncMode)
            {
                case "None":
                case "NoVariableSync":
                    Report(loc, "error", "USL015", $"'{f.Name}': 同期モード {syncMode} の behaviour に [UdonSynced] は置けません");
                    break;
                case "Continuous":
                    if (f.Type is IArrayTypeSymbol)
                        Report(loc, "error", "USL015", $"'{f.Name}': 配列は Continuous 同期できません。Manual にしてください");
                    break;
                case "Manual":
                    if (interp == "Linear" || interp == "Smooth")
                        Report(loc, "error", "USL015", $"'{f.Name}': {interp} 補間は Manual 同期では使えません");
                    break;
            }
            if (interp == "Linear" && !CanSync(f.Type, _sync.Linear))
                Report(loc, "error", "USL014", $"'{f.Name}' の型は Linear 補間同期できません");
            if (interp == "Smooth" && !CanSync(f.Type, _sync.Smooth))
                Report(loc, "error", "USL014", $"'{f.Name}' の型は Smooth 補間同期できません");
            if (f.Type is IArrayTypeSymbol)
            {
                var decl = f.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax;
                if (decl?.Initializer == null)
                    Report(loc, "warning", "USL014", $"同期配列 '{f.Name}' は必ず初期化してください (未初期化の同期配列があると behaviour 全体が同期されない: docs networking/network-details)");
            }
        }

        var fcc = f.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "FieldChangeCallbackAttribute");
        if (fcc != null && fcc.ConstructorArguments.Length == 1)
        {
            var propName = fcc.ConstructorArguments[0].Value as string;
            var prop = cls.GetMembers(propName ?? "").OfType<IPropertySymbol>().FirstOrDefault();
            if (prop == null)
                Report(loc, "error", "USL017", $"FieldChangeCallback の対象プロパティ '{propName}' が見つかりません");
            else if (!SymbolEqualityComparer.Default.Equals(prop.Type, f.Type))
                Report(loc, "error", "USL017", $"FieldChangeCallback: プロパティ型 {prop.Type.ToDisplayString()} がフィールド型 {f.Type.ToDisplayString()} と一致しません");
            else if (prop.SetMethod == null)
                Report(loc, "error", "USL017", $"FieldChangeCallback の対象プロパティ '{propName}' に set アクセサーがありません");
        }
    }

    void CheckDeclaredType(ITypeSymbol t, Location loc, string what)
    {
        var e = t;
        while (e is IArrayTypeSymbol a)
        {
            if (a.Rank > 1) Report(loc, "error", "USL101", $"{what}: 多次元配列 [,] は U# 未対応。ジャグ配列 [][] を使用");
            e = a.ElementType;
        }
        if (e is INamedTypeSymbol n && n.IsGenericType && !IsSource(n))
            Report(loc, "error", "USL101", $"{what}: ジェネリック型 {n.ToDisplayString()} は Udon で使えません (List<T>/Dictionary 等は未対応: 配列か VRC.SDK3.Data の DataList/DataDictionary を使用)");
        if (e is INamedTypeSymbol d && d.TypeKind == TypeKind.Delegate)
            Report(loc, "error", "USL121", $"{what}: デリゲート型 {d.ToDisplayString()} は Udon で使えません");
        CheckUdonType(t, loc, what);
    }

    /// <summary>
    /// Every heap variable needs an Udon type. Approximation: the Udon type name (after U# user-type mapping)
    /// must have a wrapper module in the extern table. Enums, void and UdonBehaviour (IUdonEventReceiver) are accepted.
    /// </summary>
    void CheckUdonType(ITypeSymbol t, Location loc, string what)
    {
        if (_externs == null || t == null || t.SpecialType == SpecialType.System_Void || t is ITypeParameterSymbol) return;
        var e = t;
        while (e is IArrayTypeSymbol a) e = a.ElementType;
        if (e.TypeKind == TypeKind.Error) return;
        if (IsSource(e) && !IsUsb(e)) return; // reported elsewhere (USL001/003) or a user enum (U# maps it to its underlying int type)
        if (e.TypeKind == TypeKind.Enum)
        {
            // Extern enums have no wrapper module of their own. Approximation: the enum is usable from Udon when some
            // exposed extern takes or returns it (e.g. VRC_SceneDescriptor.SpawnOrientation via VRCPlayerApi.TeleportTo).
            var enumName = TypeName(e);
            if (!EnumUsedByExposedExtern(enumName))
                Report(loc, "error", "USL125", $"{what}: 列挙型 {e.ToDisplayString()} (Udon 型名 {enumName}) を使う Udon API が見つかりません (Udon に公開されていない可能性が高い)");
            return;
        }
        var u = UdonType(t);
        var name = TypeName(u);
        if (name == "VRCUdonUdonBehaviour") name = "VRCUdonCommonInterfacesIUdonEventReceiver";
        if (name == "VRCUdonUdonBehaviourArray") return; // U# maps UdonBehaviour[] to Component[]
        if (!_modules.Contains(name))
            Report(loc, "error", "USL125", $"{what}: 型 {t.ToDisplayString()} (Udon 型名 {name}) は Udon に公開されていません");
    }

    bool EnumUsedByExposedExtern(string udonTypeName)
    {
        if (_enumSeen.TryGetValue(udonTypeName, out var known)) return known;
        // a parameter/return token: preceded by '_' and followed by '_', "Ref", "Array" or the end of the signature
        var rx = new Regex("_" + Regex.Escape(udonTypeName) + "(?:Array)?(?:Ref)?(?:_|$)", RegexOptions.CultureInvariant);
        bool found = _modules.Contains(udonTypeName) || _externs.Any(s => s.IndexOf(udonTypeName, StringComparison.Ordinal) > 0 && rx.IsMatch(s));
        _enumSeen[udonTypeName] = found;
        return found;
    }

    // ------------------------------------------------------------------ syntax checks (BinderSyntaxVisitor coverage)
    static readonly Dictionary<SyntaxKind, string> Unsupported = new Dictionary<SyntaxKind, string>
    {
        [SyntaxKind.TryStatement] = "try/catch/finally は未対応 (Udon に例外処理は無い)",
        [SyntaxKind.ThrowStatement] = "throw は未対応",
        [SyntaxKind.ThrowExpression] = "throw 式は未対応",
        [SyntaxKind.SimpleLambdaExpression] = "ラムダ式は未対応 (デリゲートが無い)",
        [SyntaxKind.ParenthesizedLambdaExpression] = "ラムダ式は未対応 (デリゲートが無い)",
        [SyntaxKind.AnonymousMethodExpression] = "匿名メソッドは未対応",
        [SyntaxKind.LocalFunctionStatement] = "ローカル関数は未対応 (CE_LocalMethodsNotSupported)",
        [SyntaxKind.QueryExpression] = "LINQ クエリ式は未対応",
        [SyntaxKind.YieldReturnStatement] = "yield (イテレーター/コルーチン) は未対応",
        [SyntaxKind.YieldBreakStatement] = "yield (イテレーター/コルーチン) は未対応",
        [SyntaxKind.AwaitExpression] = "await は未対応",
        [SyntaxKind.LockStatement] = "lock は未対応",
        [SyntaxKind.UsingStatement] = "using 文は未対応",
        [SyntaxKind.GotoStatement] = "goto は未対応",
        [SyntaxKind.GotoCaseStatement] = "goto case は未対応",
        [SyntaxKind.GotoDefaultStatement] = "goto default は未対応",
        [SyntaxKind.LabeledStatement] = "ラベル文は未対応",
        [SyntaxKind.CheckedStatement] = "checked/unchecked は未対応",
        [SyntaxKind.UncheckedStatement] = "checked/unchecked は未対応",
        [SyntaxKind.CheckedExpression] = "checked/unchecked は未対応",
        [SyntaxKind.UncheckedExpression] = "checked/unchecked は未対応",
        [SyntaxKind.UnsafeStatement] = "unsafe は未対応",
        [SyntaxKind.FixedStatement] = "fixed は未対応",
        [SyntaxKind.StackAllocArrayCreationExpression] = "stackalloc は未対応",
        [SyntaxKind.ImplicitStackAllocArrayCreationExpression] = "stackalloc は未対応",
        [SyntaxKind.SizeOfExpression] = "sizeof は未対応",
        [SyntaxKind.PointerIndirectionExpression] = "ポインターは未対応",
        [SyntaxKind.AddressOfExpression] = "ポインターは未対応",
        [SyntaxKind.PointerMemberAccessExpression] = "ポインターは未対応",
        [SyntaxKind.ConditionalAccessExpression] = "?. (null 条件演算子) は未対応。if (x != null) で書く",
        [SyntaxKind.IsPatternExpression] = "is パターンは未対応",
        [SyntaxKind.IsExpression] = "is 型チェックは未対応 (U# の VisitBinaryExpression に演算子シンボルが無く失敗する)",
        [SyntaxKind.AsExpression] = "as キャストは未対応。(T)x の明示キャストを使用",
        [SyntaxKind.SwitchExpression] = "switch 式は未対応。switch 文を使用",
        [SyntaxKind.TupleExpression] = "タプルは未対応",
        [SyntaxKind.TupleType] = "タプルは未対応",
        [SyntaxKind.AnonymousObjectCreationExpression] = "匿名型は未対応",
        [SyntaxKind.ObjectInitializerExpression] = "オブジェクト初期化子 { X = ... } は未対応 (CE_InitializerListsNotSupported)",
        [SyntaxKind.CollectionInitializerExpression] = "コレクション初期化子は未対応 (CE_InitializerListsNotSupported)",
        [SyntaxKind.ImplicitObjectCreationExpression] = "target-typed new() は未対応。new T(...) と型を書く",
        [SyntaxKind.WithExpression] = "with 式は未対応",
        [SyntaxKind.RangeExpression] = "範囲 a..b は未対応",
        [SyntaxKind.IndexExpression] = "^ インデックスは未対応",
        [SyntaxKind.RefExpression] = "ref 式/ref ローカルは未対応",
        [SyntaxKind.RefType] = "ref ローカル/ref 戻り値は未対応",
        [SyntaxKind.CoalesceAssignmentExpression] = "??= は未対応",
        [SyntaxKind.DiscardDesignation] = "破棄 (_) は未対応。ダミー変数を宣言する",
        [SyntaxKind.ForEachVariableStatement] = "foreach の分解は未対応",
        [SyntaxKind.ParenthesizedVariableDesignation] = "分解宣言は未対応",
        [SyntaxKind.CasePatternSwitchLabel] = "switch のパターン case は未対応 (U# は定数 case と default のみ扱い、パターン case は黙って無視される)",
        [SyntaxKind.WhenClause] = "case ... when は未対応",
        [SyntaxKind.SuppressNullableWarningExpression] = "! (null 許容抑制) は未対応",
        [SyntaxKind.ArgListExpression] = "__arglist は未対応",
        [SyntaxKind.MakeRefExpression] = "__makeref は未対応",
        [SyntaxKind.EventFieldDeclaration] = "C# event は未対応",
        [SyntaxKind.EventDeclaration] = "C# event は未対応",
    };

    void CheckSyntaxNode(SyntaxNode node, SemanticModel model)
    {
        if (Unsupported.TryGetValue(node.Kind(), out var msg))
        {
            Report(node, "error", "USL100", msg);
            return;
        }
        switch (node)
        {
            case LocalDeclarationStatementSyntax l when l.UsingKeyword != default:
                Report(node, "error", "USL100", "using 宣言は未対応");
                break;
            case ArrayRankSpecifierSyntax r when r.Rank > 1:
                Report(node, "error", "USL100", "多次元配列は未対応 (\"Multidimensional arrays are not yet supported by UdonSharp\")。ジャグ配列を使用");
                break;
            case IdentifierNameSyntax id when id.Identifier.Text == "_" && model.GetSymbolInfo(id).Symbol is IDiscardSymbol:
                Report(node, "error", "USL100", "破棄 (_) は未対応。ダミー変数を宣言する");
                break;
            case MethodDeclarationSyntax m when m.Parent is not TypeDeclarationSyntax:
                Report(node, "error", "USL100", "ローカルメソッドは未対応");
                break;
            case SwitchStatementSyntax sw:
                foreach (var label in sw.Sections.SelectMany(s => s.Labels).OfType<CaseSwitchLabelSyntax>())
                    if (!model.GetConstantValue(label.Value).HasValue)
                        Report(label, "error", "USL100", "switch の case ラベルは定数である必要があります");
                break;
        }
    }

    // ------------------------------------------------------------------ semantic checks
    void CheckOperations(IOperation rootOp, SemanticModel model, ISymbol owner, bool isFieldInit)
    {
        var ownerMethod = owner as IMethodSymbol;
        var ownerType = owner as INamedTypeSymbol ?? owner?.ContainingType;
        foreach (var op in rootOp.DescendantsAndSelf())
        {
            var node = op.Syntax;
            switch (op)
            {
                case ITypeOfOperation t when IsSource(t.TypeOperand):
                    Report(node, "error", "USL110", "ユーザー定義型に typeof は使えません (\"Cannot use typeof on user-defined types\")");
                    break;
                case IForEachLoopOperation fe:
                {
                    var coll = (fe.Collection as IConversionOperation)?.Operand ?? fe.Collection;
                    var ct = coll.Type;
                    bool ok = ct is IArrayTypeSymbol || ct?.SpecialType == SpecialType.System_String ||
                              (ct != null && _transform != null && InheritsFrom(ct, _transform));
                    if (!ok)
                        Report(node, "error", "USL111", $"foreach は配列・string・Transform のみ対応 ({ct?.ToDisplayString()} は不可)。for ループと Count/Length を使用");
                    break;
                }
                case IObjectCreationOperation oc when oc.Type != null && IsSource(oc.Type):
                    Report(node, "error", "USL112", $"ユーザー定義型 {oc.Type.Name} を new できません (U# behaviour はシーンに置いた component として使う)");
                    break;
                case IAnonymousFunctionOperation _:
                case IDelegateCreationOperation _:
                case IMethodReferenceOperation _:
                    Report(node, "error", "USL121", "デリゲート/メソッドグループは Udon で使えません");
                    break;
                case IEventReferenceOperation _:
                case IEventAssignmentOperation _:
                    Report(node, "error", "USL122", "C# イベントの購読 (+= / AddListener) は Udon で使えません。UI はインスペクターの OnClick/OnValueChanged → UdonBehaviour.SendCustomEvent で配線");
                    break;
                case IVariableDeclaratorOperation vd:
                    CheckDeclaredType(vd.Symbol.Type, node.GetLocation(), "ローカル変数");
                    break;
                case IDiscardOperation _:
                    Report(node, "error", "USL100", "破棄 (_) は未対応。ダミー変数を宣言する");
                    break;
                case IInvocationOperation inv:
                    CheckInvocation(inv, ownerMethod, ownerType);
                    break;
                case IPropertyReferenceOperation pr:
                    CheckPropertyRef(pr);
                    break;
                case IFieldReferenceOperation fr:
                    CheckFieldRef(fr);
                    break;
            }
            if (_externs != null)
            {
                switch (op)
                {
                    case IObjectCreationOperation oc when oc.Constructor != null && !IsSource(oc.Type):
                        bool allConst = oc.Arguments.All(a => a.Value.ConstantValue.HasValue);
                        if (oc.Type.IsValueType && allConst) break; // folded at compile time by U#
                        RequireExtern(node, MethodSig(oc.Constructor), oc.Constructor.ToDisplayString());
                        break;
                    case IBinaryOperation b when b.OperatorMethod != null && !IsSource(b.OperatorMethod):
                        if (b.OperatorMethod.ContainingType?.TypeKind == TypeKind.Enum) break;
                        RequireExtern(node, MethodSig(b.OperatorMethod), b.OperatorMethod.ToDisplayString());
                        break;
                    case IUnaryOperation u when u.OperatorMethod != null && !IsSource(u.OperatorMethod):
                        RequireExtern(node, MethodSig(u.OperatorMethod), u.OperatorMethod.ToDisplayString());
                        break;
                    case IConversionOperation c when c.OperatorMethod != null && !IsSource(c.OperatorMethod):
                        RequireExtern(node, MethodSig(c.OperatorMethod), c.OperatorMethod.ToDisplayString());
                        break;
                }
            }
            if (isFieldInit && (op is IInvocationOperation || (op is IPropertyReferenceOperation p2 && !IsSource(p2.Property))))
                Report(node, "warning", "USL140", "フィールド初期化子は U# のコンパイル時に (エディタ上で) 評価されます。シーンやプレイヤーに依存する初期化は Start() で行う");
        }
    }

    public static bool DumpSigs;

    void RequireExtern(SyntaxNode node, string sig, string display, IEnumerable<string> alternates = null)
    {
        if (DumpSigs && _externs != null)
        {
            var alt = alternates?.FirstOrDefault(Exposed);
            Console.WriteLine($"SIG {(Exposed(sig) ? "ok " : alt != null ? "alt" : "MISSING")} {sig}{(alt != null && !Exposed(sig) ? "  -> " + alt : "")}");
        }
        if (_externs == null || Exposed(sig)) return;
        if (alternates != null && alternates.Any(Exposed)) return;
        Report(node, "error", "USL120", $"Udon に公開されていない API です: {display} (sig: {sig})");
    }

    bool IsUsbDeclared(ISymbol s) => _usb != null && SymbolEqualityComparer.Default.Equals(s.ContainingType, _usb);

    void CheckInvocation(IInvocationOperation inv, IMethodSymbol ownerMethod, INamedTypeSymbol ownerType)
    {
        var m = inv.TargetMethod;
        var node = inv.Syntax;

        // direct recursion without [RecursiveMethod]
        if (ownerMethod != null && SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, ownerMethod.OriginalDefinition) &&
            !ownerMethod.GetAttributes().Any(a => a.AttributeClass?.Name == "RecursiveMethodAttribute"))
            Report(node, "warning", "USL130", $"'{m.Name}' の再帰呼び出しには [RecursiveMethod] が必要です (無いとローカル変数が壊れる)");

        CheckCustomEventName(inv, ownerType);

        // Networking.LocalPlayer.<member> chaining
        if (inv.Instance is IPropertyReferenceOperation lp && lp.Property.Name == "LocalPlayer" && lp.Property.ContainingType?.Name == "Networking")
            Report(node, "warning", "USL180", "Networking.LocalPlayer を直接連鎖せず、変数に入れて Utilities.IsValid() で確認してから使う (エディタ/ClientSim 初期化前は null)");

        if (_externs == null) return;
        var mm = m.ReducedFrom ?? m;
        if (IsSource(mm)) return; // user code (U# user methods, static helpers, extension methods)

        // compiler shims / special cases (BoundInvocationExpression.TryCreateShimInvocation)
        if (IsUsbDeclared(mm) && (mm.Name == "GetUdonTypeID" || mm.Name == "GetUdonTypeName" || mm.Name == "VRCInstantiate" ||
                                  mm.Name == "SetProgramVariable" || mm.Name == "GetProgramVariable")) return;
        if (IsUsbDeclared(mm) && mm.IsVirtual) return; // base.Event() calls are no-ops
        if (mm.IsGenericMethod && GetComponentNames.Contains(mm.Name)) return; // GetComponentShim / typeof() rewrite
        if (mm.Name == "Instantiate" && SymbolEqualityComparer.Default.Equals(mm.ContainingType, _unityObject)) return; // InstantiationShim
        if (mm.Name == "CompareTo" && mm.ContainingType?.Name == "IComparable") return;
        if ((mm.Name == "ToString" || mm.Name == "GetHashCode" || mm.Name == "Equals") && mm.ContainingType?.SpecialType == SpecialType.System_Enum) return;
        if (mm.Name == "nameof") return;

        var target = mm;
        if ((mm.Name == "IndexOf" || mm.Name == "BinarySearch" || mm.Name == "LastIndexOf" || mm.Name == "Reverse") &&
            SymbolEqualityComparer.Default.Equals(mm.ContainingType, _systemArray) && mm.IsGenericMethod)
        {
            var nonGeneric = _systemArray.GetMembers(mm.Name).OfType<IMethodSymbol>().FirstOrDefault(x => !x.IsGenericMethod && x.Parameters.Length == mm.Parameters.Length);
            if (nonGeneric != null) target = nonGeneric;
        }
        if (target.IsGenericMethod) target = target.OriginalDefinition;

        var receiver = inv.Instance?.Type;
        string sig;
        IEnumerable<string> alts = null;
        if (target.ContainingType?.ToDisplayString() == "TMPro.TMP_Text" && receiver != null)
            sig = MethodSig(target, receiver); // TryCreateTMPMethodInvocation: synthesized on the instance type
        else
        {
            sig = MethodSig(target);
            if (!target.IsStatic && receiver != null)
                alts = AlternateTypes(receiver).Select(t => MethodSig(target, t)).ToList();
        }
        RequireExtern(node, sig, target.ToDisplayString(), alts);
    }

    void CheckCustomEventName(IInvocationOperation inv, INamedTypeSymbol ownerType)
    {
        var m = inv.TargetMethod;
        if (m.Name != "SendCustomEvent" && m.Name != "SendCustomEventDelayedSeconds" && m.Name != "SendCustomEventDelayedFrames" && m.Name != "SendCustomNetworkEvent")
            return;
        bool network = m.Name == "SendCustomNetworkEvent";
        var nameArg = inv.Arguments.FirstOrDefault(a => a.Parameter?.Name == "eventName");
        if (nameArg == null || !nameArg.Value.ConstantValue.HasValue || !(nameArg.Value.ConstantValue.Value is string evName)) return;
        // which class receives the event? only checkable for this/implicit-this or a typed U# reference
        ITypeSymbol recvType = inv.Instance == null || inv.Instance is IInstanceReferenceOperation ? ownerType : inv.Instance.Type;
        if (recvType == null || !IsUsb(recvType) || !IsSource(recvType)) return;
        var candidates = new List<IMethodSymbol>();
        for (var t = recvType as INamedTypeSymbol; t != null && IsSource(t); t = t.BaseType)
            candidates.AddRange(t.GetMembers(evName).OfType<IMethodSymbol>().Where(x => x.MethodKind == MethodKind.Ordinary));
        int extraArgs = inv.Arguments.Count(a => a.Parameter != null && a.Parameter.Name.StartsWith("parameter", StringComparison.Ordinal) && !(a.ArgumentKind == ArgumentKind.DefaultValue));
        var node = nameArg.Syntax;
        if (candidates.Count == 0)
        {
            Report(node, "error", "USL161", $"イベント名 '{evName}' のメソッドが {recvType.Name} にありません (nameof() を使うと typo を防げる)");
            return;
        }
        var target = candidates.FirstOrDefault(c => c.Parameters.Length == extraArgs) ?? candidates[0];
        if (target.DeclaredAccessibility != Accessibility.Public && !_udonEventNames.Contains(evName))
            Report(node, "error", "USL161", $"'{evName}' は public ではないため Udon からイベントとして呼べません (export されるのは public メソッドのみ)");
        if (target.IsStatic)
            Report(node, "error", "USL161", $"'{evName}' は static メソッドなのでイベントとして呼べません");
        bool callable = target.GetAttributes().Any(a => a.AttributeClass?.Name == "NetworkCallableAttribute");
        if (!network && target.Parameters.Length > 0)
            Report(node, "error", "USL161", $"'{evName}' は引数付きメソッドです。SendCustomEvent で呼べるのは引数なしの public メソッドだけ (引数付きは名前が __0_ 等にマングルされる)");
        if (network)
        {
            // docs networking/events: "the *receiving* UdonBehaviour must not use sync mode None" - U# compiles it,
            // but the event is silently never delivered at runtime.
            if (GetSyncMode(recvType as INamedTypeSymbol) == "None")
                Report(node, "error", "USL163", $"ネットワークイベント '{evName}' の受信側 {recvType.Name} が BehaviourSyncMode.None です。None の behaviour にはネットワークイベントが届きません (NoVariableSync / Manual にする: docs networking/events)");
            if (extraArgs > 0 && !callable)
                Report(node, "error", "USL162", $"引数付きネットワークイベント '{evName}' には [NetworkCallable] が必要です");
            else if (evName.StartsWith("_", StringComparison.Ordinal) && !callable)
                Report(node, "error", "USL162", $"'_' で始まるメソッド '{evName}' はネットワーク経由で呼ばれません ([NetworkCallable] を付けるか名前を変える)");
            else if (!callable)
                Report(node, "warning", "USL162", $"'{evName}' はレガシーなネットワークイベントです。[NetworkCallable] を付けることが推奨されます (SDK 3.8.1+)");
        }
    }

    void CheckPropertyRef(IPropertyReferenceOperation pr)
    {
        if (pr.Instance is IPropertyReferenceOperation lp && lp.Property.Name == "LocalPlayer" && lp.Property.ContainingType?.Name == "Networking")
            Report(pr.Syntax, "warning", "USL180", "Networking.LocalPlayer を直接連鎖せず、変数に入れて Utilities.IsValid() で確認してから使う");
        if (_externs == null) return;
        var p = pr.Property;
        if (IsSource(p)) return;
        if (pr.Instance?.Type?.SpecialType == SpecialType.System_String && p.IsIndexer) return; // BoundStringAccessExpression
        var (get, set) = AccessKind(pr);
        var receiver = pr.Instance?.Type;
        foreach (var (acc, needed) in new[] { (p.GetMethod, get), (p.SetMethod, set) })
        {
            if (!needed) continue;
            var accessor = acc ?? (needed == set ? p.SetMethod : p.GetMethod);
            if (accessor == null) continue;
            var sig = MethodSig(accessor);
            var alts = !p.IsStatic && receiver != null ? AlternateTypes(receiver).Select(t => MethodSig(accessor, t)).ToList() : null;
            RequireExtern(pr.Syntax, sig, p.ToDisplayString() + (acc == p.SetMethod ? " (set)" : " (get)"), alts);
        }
    }

    void CheckFieldRef(IFieldReferenceOperation fr)
    {
        if (fr.Instance is IPropertyReferenceOperation lp && lp.Property.Name == "LocalPlayer" && lp.Property.ContainingType?.Name == "Networking")
            Report(fr.Syntax, "warning", "USL180", "Networking.LocalPlayer を直接連鎖せず、変数に入れて Utilities.IsValid() で確認してから使う");
        if (_externs == null) return;
        var f = fr.Field;
        if (IsSource(f) || f.IsConst || f.ContainingType?.TypeKind == TypeKind.Enum) return;
        var (get, set) = AccessKind(fr);
        var receiver = fr.Instance?.Type;
        foreach (var (isSet, needed) in new[] { (false, get), (true, set) })
        {
            if (!needed) continue;
            var sig = FieldSig(f, isSet);
            var alts = !f.IsStatic && receiver != null ? AlternateTypes(receiver).Select(t => FieldSig(f, isSet, t)).ToList() : null;
            RequireExtern(fr.Syntax, sig, f.ToDisplayString() + (isSet ? " (set)" : " (get)"), alts);
        }
    }

    static (bool get, bool set) AccessKind(IOperation op)
    {
        var parent = op.Parent;
        switch (parent)
        {
            case ISimpleAssignmentOperation a when a.Target == op: return (false, true);
            case ICompoundAssignmentOperation c when c.Target == op: return (true, true);
            case IIncrementOrDecrementOperation _: return (true, true);
            case IArgumentOperation arg when arg.Parameter != null && arg.Parameter.RefKind == RefKind.Out: return (false, true);
            case IArgumentOperation arg when arg.Parameter != null && arg.Parameter.RefKind == RefKind.Ref: return (true, true);
        }
        return (true, false);
    }
}
