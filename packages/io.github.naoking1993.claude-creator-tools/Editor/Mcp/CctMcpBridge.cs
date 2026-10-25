#if CCT_UNITY_MCP
using System;
using System.Collections.Generic;
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

        public static object Handle(JObject @params, string toolName, Func<CctArgs, CctResult> tool, ICollection<string> summaryKeys)
        {
            CctArgs args;
            try { args = new CctArgs(ToPlain(@params) as Dictionary<string, object>); }
            catch (Exception e) { return new ErrorResponse($"Invalid parameters for {toolName}: {e.Message}"); }

            var result = CctRunner.Run(toolName, tool, args);
            bool full = args.GetEnum("detail", "summary", "summary", "full") == "full";
            var keep = summaryKeys == null ? null : new HashSet<string>(summaryKeys.Concat(AlwaysKeptKeys), StringComparer.Ordinal);
            var payload = full
                ? result.ToPlain()
                : result.ToCompact(args.GetInt("max_issues", 30, 0, 500), keep);
            var data = CctJson.ToPlain(payload);
            return result.Success
                ? (object)new SuccessResponse(result.Message, data)
                : new ErrorResponse(result.Message, data);
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
                default:
                    return token.ToString();
            }
        }
    }
}
#endif
