#if CCT_UNITY_MCP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    /// <summary>
    /// Adapts MCP for Unity tool calls (JObject in, IMcpResponse out) to the SDK-independent core tools.
    /// Common parameters for every cct_* tool:
    ///   detail          "summary" (default) | "full"  - full returns the whole report inline
    ///   max_issues      issues to include in a summary response (default 30)
    ///   save_report     write the full JSON report to Library/ClaudeCreatorTools/Reports (default true)
    ///   timeout_seconds read by the MCP server to extend its 30 s call timeout (some tools also bound their scans by it)
    /// </summary>
    internal static class CctMcpBridge
    {
        /// <summary>
        /// Data keys every summary response keeps on top of a tool's own summary keys: CctRunner's provenance (tool,
        /// tool_version, elapsed_ms) and the failure context that CctResult.Fail and target resolution put into data
        /// (details: exception stack / per-tool context, candidates: what to pass as target instead).
        /// </summary>
        static readonly string[] AlwaysKeptKeys = { "tool", "tool_version", "elapsed_ms", "details", "candidates" };

        /// <summary>
        /// Appended to every tool Description. MCP for Unity 9.7.1 does not forward [ToolParameter] descriptions
        /// to the LLM (only names/types/defaults), and on Python 3.14 servers parameterised custom tools are only
        /// reachable through execute_custom_tool (no schema at all), so the Description must explain every parameter.
        /// </summary>
        internal const string CommonParamsDoc =
            " Common parameters: detail = 'summary' (default; short response with errors first) or 'full' (whole report inline);" +
            " max_issues = issues kept in a summary response (default 30); save_report = write the full JSON report to" +
            " Library/ClaudeCreatorTools/Reports and return its path as report_path (default true); timeout_seconds = raise the" +
            " MCP call timeout (default 30) for big projects; keep it at 240 or less, because Claude Code aborts an HTTP MCP call" +
            " that sends nothing for 5 minutes. If cct_* tools are not listed as separate MCP tools," +
            " call them via execute_custom_tool(tool_name=\"cct_...\", parameters={...}).";

        /// <summary>
        /// Upper bound for a summary response in estimated tokens (see <see cref="EstimateTokens"/>). Claude Code warns
        /// above 10,000 tokens and moves a result over 25,000 tokens or 50,000 characters into a file that Claude must Read
        /// separately. Each tool caps its own lists, but caps add up (e.g. 120 parameters + 80 layers + 40 controllers in
        /// cct_animator_inspect, 64 menus + 64 parameters + 50 missing-script rows in cct_avatar_audit), so the bridge enforces
        /// one budget for every tool. detail='full' is never trimmed.
        /// </summary>
        internal const int SummaryTokenBudget = 7000;

        /// <summary>Default of max_issues; issues are only shortened by the budget when the caller did not ask for more.</summary>
        const int DefaultMaxIssues = 30;

        /// <summary>Top-level keys that are never shortened or dropped by the budget.</summary>
        static readonly HashSet<string> BudgetProtectedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "success", "message", "code", "issue_counts", "issues", "issues_omitted", "report_path", "tool", "tool_version",
            "elapsed_ms", "response_trimmed",
        };

        public static object Handle(JObject @params, string toolName, Func<CctArgs, CctResult> tool, ICollection<string> summaryKeys)
        {
            CctArgs args;
            try { args = new CctArgs(ToPlain(@params) as Dictionary<string, object>); }
            catch (Exception e) { return new ErrorResponse($"Invalid parameters for {toolName}: {e.Message}"); }

            var result = CctRunner.Run(toolName, tool, args);
            bool full = args.GetEnum("detail", "summary", "summary", "full") == "full";
            var keep = summaryKeys == null ? null : new HashSet<string>(summaryKeys.Concat(AlwaysKeptKeys), StringComparer.Ordinal);
            int maxIssues = args.GetInt("max_issues", DefaultMaxIssues, 0, 500);
            var payload = full
                ? result.ToPlain()
                : result.ToCompact(maxIssues, keep);
            var data = CctJson.ToPlain(payload);
            if (!full && data is Dictionary<string, object> compact)
            {
                try { FitToBudget(compact, SummaryTokenBudget, maxIssues <= DefaultMaxIssues, result.ReportPath != null); }
                catch (Exception e) { compact["response_trimmed"] = "error: " + e.Message; }
            }
            return result.Success
                ? (object)new SuccessResponse(result.Message, data)
                : new ErrorResponse(result.Message, data);
        }

        /// <summary>
        /// Rough token count of the compact JSON that reaches Claude: ASCII at 3.2 characters per token, any other character
        /// (Japanese messages and names) as one token each - on the safe side of the real tokenizer for this kind of content.
        /// </summary>
        internal static int EstimateTokens(object plain)
        {
            var json = CctJson.Serialize(plain, false);
            int ascii = 0, other = 0;
            foreach (var c in json)
                if (c < 128) ascii++; else other++;
            return (int)Math.Ceiling(ascii / 3.2) + other;
        }

        /// <summary>
        /// Shortens a summary payload (plain Dictionary/List tree) in place until it fits <paramref name="budget"/>: first every
        /// list longer than 40, 25, 15, 10 items (at any depth, also inside issues), then the issue list (to 15, then 10; only
        /// when <paramref name="mayTrimIssues"/>), lists to 5 items, and as a last resort the largest unprotected data keys.
        /// Records what was cut under response_trimmed. Returns false when nothing had to be cut.
        /// </summary>
        internal static bool FitToBudget(Dictionary<string, object> payload, int budget, bool mayTrimIssues, bool reportSaved)
        {
            // An explicit max_issues above the default is honoured: the issues get their size, the rest keeps half the budget.
            if (!mayTrimIssues && payload.TryGetValue("issues", out var requested) && requested is List<object>)
                budget = Math.Max(budget, EstimateTokens(requested) + budget / 2);
            if (EstimateTokens(payload) <= budget) return false;
            var lists = new SortedDictionary<string, int>(StringComparer.Ordinal); // path -> item count before trimming
            int keptPerList = 0;
            bool fits = false;

            bool TrimTo(int cap)
            {
                foreach (var kv in payload.ToList())
                    if (kv.Key == "issues" || !BudgetProtectedKeys.Contains(kv.Key))
                        TrimLists(kv.Value, kv.Key, cap, kv.Key == "issues", lists, 0);
                if (lists.Count > 0) keptPerList = cap;
                return EstimateTokens(payload) <= budget;
            }

            bool TrimIssues(int cap)
            {
                if (!mayTrimIssues || !(payload.TryGetValue("issues", out var v) && v is List<object> issues) || issues.Count <= cap)
                    return EstimateTokens(payload) <= budget;
                long before = payload.TryGetValue("issues_omitted", out var o) && o != null ? Convert.ToInt64(o) : 0;
                payload["issues_omitted"] = before + issues.Count - cap;
                issues.RemoveRange(cap, issues.Count - cap);
                return EstimateTokens(payload) <= budget;
            }

            foreach (var cap in new[] { 40, 25, 15, 10 })
                if (fits = TrimTo(cap)) break;
            if (!fits) fits = TrimIssues(15);
            if (!fits) fits = TrimTo(5);
            if (!fits) fits = TrimIssues(10);

            var dropped = new List<object>();
            while (!fits)
            {
                string largest = null;
                int largestSize = 0;
                foreach (var kv in payload)
                {
                    if (BudgetProtectedKeys.Contains(kv.Key) || kv.Value == null || kv.Value is string || kv.Value is bool || kv.Value is IConvertible) continue;
                    int size = EstimateTokens(kv.Value);
                    if (size > largestSize) { largest = kv.Key; largestSize = size; }
                }
                if (largest == null) break;
                payload[largest] = "(省略: 応答サイズの上限)";
                dropped.Add(largest);
                fits = EstimateTokens(payload) <= budget;
            }

            var info = new Dictionary<string, object>
            {
                ["note"] = "応答が大きいため一覧を短くしました。" + (reportSaved
                    ? "全件は report_path のレポートにあります (detail='full' なら応答に全件)"
                    : "save_report=true で再実行するとレポートに全件が保存されます (detail='full' なら応答に全件)"),
                ["budget_tokens"] = budget,
            };
            if (lists.Count > 0)
            {
                info["kept_per_list"] = keptPerList;
                info["lists"] = lists.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
            }
            if (dropped.Count > 0) info["keys_omitted"] = dropped;
            payload["response_trimmed"] = info;
            return true;
        }

        /// <summary>Cuts every list under <paramref name="node"/> to <paramref name="cap"/> items, recording the original counts by path.</summary>
        static void TrimLists(object node, string path, int cap, bool keepThisList, IDictionary<string, int> trimmed, int depth)
        {
            if (depth > 12) return;
            if (node is Dictionary<string, object> d)
            {
                foreach (var kv in d.ToList()) TrimLists(kv.Value, path + "." + kv.Key, cap, false, trimmed, depth + 1);
                return;
            }
            if (!(node is List<object> l)) return;
            if (!keepThisList && l.Count > cap)
            {
                if (!trimmed.TryGetValue(path, out var before) || before < l.Count) trimmed[path] = l.Count;
                l.RemoveRange(cap, l.Count - cap);
            }
            foreach (var item in l) TrimLists(item, path + "[]", cap, false, trimmed, depth + 1);
        }

        /// <summary>JToken -> Dictionary/List/primitive (the representation CctArgs expects).</summary>
        public static object ToPlain(JToken token)
        {
            if (token == null) return null;
            switch (token.Type)
            {
                case JTokenType.Object:
                    var d = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var prop in ((JObject)token).Properties()) d[prop.Name] = ToPlain(prop.Value);
                    return d;
                case JTokenType.Array:
                    var l = new List<object>();
                    foreach (var item in (JArray)token) l.Add(ToPlain(item));
                    return l;
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return null;
                case JTokenType.Integer:
                    return token.Value<long>();
                case JTokenType.Float:
                    return token.Value<double>();
                case JTokenType.Boolean:
                    return token.Value<bool>();
                case JTokenType.Date:
                    // Newtonsoft's default DateParseHandling turns ISO-8601-looking strings into dates while the transport
                    // parses the message; JValue.ToString() would then print them in the Editor's culture (2026/10/25 ...).
                    var date = ((JValue)token).Value;
                    return date is DateTime dt ? dt.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture)
                        : date is DateTimeOffset dto ? dto.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture)
                        : Convert.ToString(date, CultureInfo.InvariantCulture);
                default:
                    return token.ToString();
            }
        }
    }
}
#endif
