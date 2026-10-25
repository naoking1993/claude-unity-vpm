// Validates SerializedObject property paths against the real VRChat SDK 3.10.5 assemblies (reflection only).
// Usage: mono FieldCheck.exe check <file>      lines: Full.Type.Name|path.with[].arrays   (# comments ok)
//        mono FieldCheck.exe dump <Full.Type.Name> [depth]   -> serialized fields (public or [SerializeField]) recursively
//        mono FieldCheck.exe enum <Full.Enum.Name>           -> name=value pairs
using System; using System.IO; using System.Linq; using System.Reflection; using System.Collections.Generic;
public static class FieldCheck {
  static List<Assembly> asms = new List<Assembly>();
  static string[] dirs;
  static Type Find(string n) { foreach (var a in asms) { try { var t = a.GetType(n, false); if (t != null) return t; } catch (Exception e) { if (Environment.GetEnvironmentVariable("FC_DEBUG") != null) Console.WriteLine("GetType " + a.GetName().Name + ": " + e.Message); } } return null; }
  static bool IsSerialized(FieldInfo f) => !f.IsStatic && !f.IsNotSerialized && (f.IsPublic || f.GetCustomAttributes(false).Any(x => x.GetType().Name == "SerializeField" || x.GetType().Name == "SerializeReference"));
  static FieldInfo GetField(Type t, string name) { for (var c = t; c != null; c = c.BaseType) { var f = c.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); if (f != null) return f; } return null; }
  static Type Elem(Type t) => t.IsArray ? t.GetElementType() : (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) ? t.GetGenericArguments()[0] : null);
  public static int Main(string[] a) {
    var root = Environment.GetEnvironmentVariable("CCT_DEV") ?? throw new InvalidOperationException("set CCT_DEV (see tests/dev/README.md)");
    dirs = Directory.GetFiles(root + "/ext", "*.dll", SearchOption.AllDirectories).Where(p => p.Contains("/vrcsdk_") && !p.Contains("decompiled") && !p.Contains("/x86") && !p.Contains("Windows")).Select(Path.GetDirectoryName).Distinct().Concat(new[]{ root + "/refs/um/lib/net45", root + "/refs/nj/lib/net45" }).ToArray();
    AppDomain.CurrentDomain.AssemblyResolve += (s, e) => { var n = new AssemblyName(e.Name).Name + ".dll"; foreach (var d in dirs) { var p = Path.Combine(d, n); if (File.Exists(p)) return Assembly.LoadFrom(p); } return null; };
    foreach (var d in dirs) foreach (var f in Directory.GetFiles(d, "*.dll")) { var n = Path.GetFileName(f); if (!(n.StartsWith("VRC") || n.StartsWith("SDKBase"))) continue; try { asms.Add(Assembly.LoadFrom(f)); } catch (Exception e) { if (Environment.GetEnvironmentVariable("FC_DEBUG") != null) Console.WriteLine("load " + n + ": " + e.Message); } }
    if (Environment.GetEnvironmentVariable("FC_DEBUG") != null) Console.WriteLine("assemblies: " + string.Join(", ", asms.Select(x => x.GetName().Name)));
    if (a[0] == "dump") { var t = Find(a[1]); if (t == null) { Console.WriteLine("TYPE NOT FOUND " + a[1]); return 1; } Dump(t, "", a.Length > 2 ? int.Parse(a[2]) : 2, new HashSet<Type>()); return 0; }
    if (a[0] == "enum") { var t = Find(a[1]); if (t == null || !t.IsEnum) { Console.WriteLine("ENUM NOT FOUND " + a[1]); return 1; } foreach (var n in Enum.GetNames(t)) Console.WriteLine($"{n}={Convert.ToInt64(Enum.Parse(t, n))}"); return 0; }
    int bad = 0, ok = 0;
    foreach (var raw in File.ReadAllLines(a[1])) {
      var line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#")) continue;
      var parts = line.Split('|'); var t = Find(parts[0].Trim());
      if (t == null) { Console.WriteLine("MISSING TYPE   " + line); bad++; continue; }
      if (parts.Length < 2 || parts[1].Trim().Length == 0) { Console.WriteLine("OK type        " + parts[0]); ok++; continue; }
      var cur = t; string err = null;
      foreach (var seg0 in parts[1].Trim().Split('.')) {
        var arr = seg0.EndsWith("[]"); var seg = arr ? seg0.Substring(0, seg0.Length - 2) : seg0;
        var f = GetField(cur, seg);
        if (f == null) { err = $"no field '{seg}' on {cur.FullName}"; break; }
        if (!IsSerialized(f)) { err = $"field '{seg}' on {cur.FullName} is not serialized"; break; }
        cur = f.FieldType;
        if (arr) { var e = Elem(cur); if (e == null) { err = $"'{seg}' is not an array/list ({cur.Name})"; break; } cur = e; }
      }
      if (err != null) { Console.WriteLine("MISSING FIELD  " + line + "  -> " + err); bad++; } else { Console.WriteLine($"OK             {line}  : {cur.FullName}"); ok++; }
    }
    Console.WriteLine(bad == 0 ? $"FIELDCHECK OK ({ok})" : $"FIELDCHECK FAILED ({bad} missing, {ok} ok)"); return bad == 0 ? 0 : 1;
  }
  static bool IsUnityObject(Type t) { for (var c = t; c != null; c = c.BaseType) if (c.FullName == "UnityEngine.Object") return true; return false; }
  static void Dump(Type t, string ind, int depth, HashSet<Type> seen) {
    for (var c = t; c != null && c.Namespace?.StartsWith("UnityEngine") != true && c != typeof(object); c = c.BaseType)
      foreach (var f in c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
        if (!IsSerialized(f)) continue;
        var ft = f.FieldType; var e = Elem(ft) ?? ft;
        Console.WriteLine($"{ind}{f.Name} : {ft.Name}{(e.IsEnum ? " enum{" + string.Join(",", Enum.GetNames(e).Select(n => n + "=" + Convert.ToInt64(Enum.Parse(e, n)))) + "}" : "")}  [{c.Name}]");
        if (depth > 0 && !e.IsPrimitive && !e.IsEnum && e != typeof(string) && e.Namespace?.StartsWith("UnityEngine") != true && !IsUnityObject(e) && seen.Add(e)) Dump(e, ind + "  ", depth - 1, seen);
      }
  }
}
