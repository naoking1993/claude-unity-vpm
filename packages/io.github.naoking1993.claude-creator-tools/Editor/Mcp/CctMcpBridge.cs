#if CCT_UNITY_MCP
using System;
using System.Collections.Generic;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    /// <summary>
    /// Adapts MCP for Unity tool calls (JObject in, IMcpResponse out) to the SDK-independent core tools.
    /// Common parameters for every cct_* tool:
    ///   detail      "summary" (default) | "full"  - full returns the whole report inline
    ///   max_issues  issues to include in a summary response (default 30)
    ///   save_report write the full JSON report to Library/ClaudeCreatorTools/Reports (default true)
    /// </summary>
    internal static class CctMcpBridge
    {
        public static object Handle(JObject @params, string toolName, Func<CctArgs, CctResult> tool, ICollection<string> summaryKeys)
        {
            CctArgs args;
            try { args = new CctArgs(ToPlain(@params) as Dictionary<string, object>); }
            catch (Exception e) { return new ErrorResponse($"Invalid parameters for {toolName}: {e.Message}"); }

            var result = CctRunner.Run(toolName, tool, args);
            bool full = args.GetEnum("detail", "summary", "summary", "full") == "full";
            var payload = full
                ? result.ToPlain()
                : result.ToCompact(args.GetInt("max_issues", 30, 0, 500), summaryKeys);
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
