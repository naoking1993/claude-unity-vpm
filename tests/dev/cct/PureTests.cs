using System; using System.Collections.Generic; using Naoking.ClaudeCreatorTools;
public static class PureTests {
  static int fails = 0;
  static void Eq(object a, object b, string what) { if (!Equals(a, b)) { Console.WriteLine($"FAIL {what}: got [{a}] want [{b}]"); fails++; } }
  public static int Main() {
    var j = CctJson.Serialize(new Dictionary<string, object>{ ["a"]=1, ["b"]=new List<object>{1.5f, "x\"y", null, true}, ["c"]=new Dictionary<string,object>{["日本語"]="霧島\n"} , ["n"]=double.NaN}, false);
    Eq(j, "{\"a\":1,\"b\":[1.5,\"x\\\"y\",null,true],\"c\":{\"日本語\":\"霧島\\n\"},\"n\":null}", "serialize");
    var back = (Dictionary<string, object>)CctJson.Parse(j);
    Eq(back["a"], 1L, "parse int"); Eq(((List<object>)back["b"])[0], 1.5, "parse float"); Eq(((Dictionary<string,object>)back["c"])["日本語"], "霧島\n", "parse unicode");
    Eq(CctJson.Serialize(CctJson.Parse("{\"x\":[1,2,{\"y\":\"\\u3042\"}],\"e\":-1.25e2}"), false), "{\"x\":[1,2,{\"y\":\"あ\"}],\"e\":-125}", "roundtrip");
    var a = CctArgs.FromJson("{\"include_inactive\":\"false\",\"maxIssues\":\"12\",\"views\":\"front, back\",\"list\":[\"a\",\"b\"],\"detail\":\"FULL\"}");
    Eq(a.GetBool("include_inactive", true), false, "bool string");
    Eq(a.GetBool("includeInactive", true), false, "camel->snake lookup");
    Eq(a.GetInt("max_issues", 30), 12, "snake->camel lookup");
    Eq(string.Join("|", a.GetStringList("views")), "front|back", "csv list");
    Eq(string.Join("|", a.GetStringList("list")), "a|b", "json list");
    Eq(a.GetEnum("detail", "summary", "summary", "full"), "full", "enum");
    Eq(a.GetInt("missing", 7, 0, 5), 5, "clamp");
    Eq(CctArgs.ToSnake("maxIssues"), "max_issues", "snake"); Eq(CctArgs.ToCamel("max_issues"), "maxIssues", "camel");
    var lazy = CctJson.Serialize(System.Linq.Enumerable.Select(new[]{1,2}, x => x * 2), false); Eq(lazy, "[2,4]", "lazy enumerable");
    Console.WriteLine(fails == 0 ? "PURE TESTS OK" : $"{fails} FAILED"); return fails;
  }
}
