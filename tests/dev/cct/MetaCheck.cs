// Mirrors MCP for Unity 9.7.1 CommandRegistry/ToolDiscoveryService rules against our compiled MCP assembly.
using System; using System.IO; using System.Linq; using System.Reflection; using System.Text.RegularExpressions; using System.Collections.Generic;
public static class MetaCheck {
  static readonly string[] Groups = { "core", "vfx", "animation", "ui", "scripting_ext", "testing", "menu" };
  public static int Main(string[] a) {
    var dir = Path.GetDirectoryName(Path.GetFullPath(a[0]));
    var extra = a.Skip(1).ToArray();
    AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
      var n = new AssemblyName(e.Name).Name + ".dll";
      foreach (var d in new[]{dir}.Concat(extra)) { var p = Path.Combine(d, n); if (File.Exists(p)) return Assembly.LoadFrom(p); }
      return null; };
    var asm = Assembly.LoadFrom(a[0]);
    Type[] types; try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { foreach (var le in ex.LoaderExceptions) Console.WriteLine("LOADER: " + le.Message); return 2; }
    int errors = 0; var names = new HashSet<string>();
    foreach (var t in types) {
      var attr = t.GetCustomAttributes(false).FirstOrDefault(x => x.GetType().Name == "McpForUnityToolAttribute");
      if (attr == null) continue;
      var at = attr.GetType();
      string name = (string)at.GetProperty("Name").GetValue(attr);
      string desc = (string)at.GetProperty("Description").GetValue(attr);
      string group = (string)at.GetProperty("Group").GetValue(attr);
      bool auto = (bool)at.GetProperty("AutoRegister").GetValue(attr);
      void Err(string m) { Console.WriteLine($"ERROR {t.FullName}: {m}"); errors++; }
      if (string.IsNullOrEmpty(name)) Err("explicit Name required"); else if (!Regex.IsMatch(name, "^cct_[a-z0-9_]+$")) Err($"name '{name}' must be cct_snake_case");
      if (!names.Add(name ?? t.Name)) Err($"duplicate tool name {name}");
      if (string.IsNullOrWhiteSpace(desc) || desc.Length < 40) Err("Description missing/too short for the LLM");
      if (group != null && !Groups.Contains(group)) Err($"group '{group}' not one of {string.Join(",", Groups)}");
      if (!auto) Err("AutoRegister must be true");
      var m = t.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(x => x.Name == "HandleCommand" && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType.FullName == "Newtonsoft.Json.Linq.JObject");
      if (m == null) Err("no public static HandleCommand(JObject)"); else if (m.ReturnType != typeof(object)) Err("HandleCommand must return object (sync)");
      var pt = t.GetNestedType("Parameters");
      var plist = new List<string>();
      bool seenOptional = false;
      if (pt == null) Err("no public nested Parameters class");
      else foreach (var p in pt.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
        var pa = p.GetCustomAttributes(false).FirstOrDefault(x => x.GetType().Name == "ToolParameterAttribute");
        if (pa == null) { Err($"property {p.Name} lacks [ToolParameter]"); continue; }
        if (!Regex.IsMatch(p.Name, "^[a-z][a-z0-9_]*$")) Err($"parameter '{p.Name}' must be snake_case (it is sent verbatim as the JSON key)");
        var pdesc = (string)pa.GetType().GetProperty("Description").GetValue(pa);
        if (string.IsNullOrWhiteSpace(pdesc)) Err($"parameter {p.Name} has empty description");
        bool req = (bool)pa.GetType().GetProperty("Required").GetValue(pa);
        if (req && seenOptional) Err($"required parameter '{p.Name}' is declared after an optional one (9.7.1 server: 'non-default argument follows default argument' breaks registration)");
        if (!req) seenOptional = true;
        // [ToolParameter] descriptions never reach the LLM on 9.7.1 (only name/type/required/default do),
        // so the tool Description itself must document every parameter by name.
        if (desc != null && !Regex.IsMatch(desc, "\\b" + Regex.Escape(p.Name) + "\\b")) Err($"Description does not mention parameter '{p.Name}' (parameter descriptions are not sent to the LLM)");
        plist.Add(p.Name + (req ? "*" : "") + ":" + p.PropertyType.Name);
      }
      foreach (var common in new[] { "detail", "max_issues", "save_report", "timeout_seconds" })
        if (pt != null && pt.GetProperty(common) == null) Err($"Parameters lacks common parameter '{common}' (see CctEnvironmentTool)");
      if (desc != null && !desc.Contains("execute_custom_tool")) Err("Description must end with CctMcpBridge.CommonParamsDoc");
      // Claude Code truncates an upfront-loaded tool description at 4,096 characters (2,048 before v2.1.296), and every
      // description is a per-session token cost, so keep it short and put the parameter docs first.
      if (desc != null && desc.Length > 3500) Err($"Description is {desc.Length} chars (max 3500; move detail to the claude-creator-tools skill)");
      if (desc != null && (desc.IndexOf("Parameters:", StringComparison.Ordinal) < 0 || desc.IndexOf("Parameters:", StringComparison.Ordinal) > 1200)) Err("Description must contain 'Parameters:' within its first 1200 characters");
      Console.WriteLine($"tool {name} [{group}] params: {string.Join(", ", plist)}");
    }
    if (names.Count == 0) { Console.WriteLine("ERROR: no tools found"); errors++; }
    Console.WriteLine(errors == 0 ? $"METACHECK OK ({names.Count} tools)" : $"METACHECK FAILED ({errors} errors)");
    return errors == 0 ? 0 : 1;
  }
}
