#if CCT_UNITY_MCP
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    [McpForUnityTool(CctAvatarAudit.ToolName,
        Description = "Claude Creator Tools: read-only pre-upload audit of one VRChat avatar: PC and Android/iOS performance ranks " +
                      "with the stats that set them and the value needed for the next better rank, upload blockers the SDK builder " +
                      "reports, Write Defaults mixing, Expression Parameters / menu checks, missing scripts and references. Use it " +
                      "before and after optimisation, before upload, and to find what dominates the rank. Parameters: target = avatar " +
                      "to audit: '' / 'selection' (default; the selection is moved up to its VRCAvatarDescriptor; nothing selected = " +
                      "the only avatar in the loaded scenes), '/Root/Child', '#instanceId', unique GameObject name or " +
                      "'Assets/.../X.prefab' (ambiguity returns an error listing candidates); platform = 'all' (default; findings that " +
                      "only concern the platform that is not the active build target are info), 'pc', 'android' or 'ios' (android and " +
                      "ios share the mobile limits; pass 'android' when preparing a Quest/mobile version on a PC target); " +
                      "include_lists = include the per-component breakdown lists in the report (default true); top_n = rows per " +
                      "breakdown list (default 15, max 200); post_build = also process a temporary clone with NDMF (MA, AAO and other " +
                      "NDMF plugins; not VRCFury) in a private preview scene, audit it, return post_build (ranks after build, changed " +
                      "stats, NDMF errors) and destroy the clone (default false; not in Play mode; can take tens of seconds, so pass " +
                      "timeout_seconds=120). Stats come from the VRChat SDK's own calculator when available (method.stats='sdk', same " +
                      "numbers as the SDK Control Panel), otherwise from an estimate (SDK_CALCULATOR_UNAVAILABLE). The summary has " +
                      "avatar, method, ranks (overall, limiting_stats), stats, upload (blocked = an error the SDK refuses on the " +
                      "ACTIVE build target), expressions, missing_scripts, missing_references, notes, post_build; breakdown lists and " +
                      "rank tables are only in the report at report_path. Values are pre-build unless post_build is used. Side " +
                      "effects: none on scenes or assets (post_build writes NDMF logs to the Console)." +
                      CctMcpBridge.CommonParamsDoc)]
    public static class CctAvatarAuditTool
    {
        static readonly string[] SummaryKeys =
        {
            "avatar", "method", "ranks", "stats", "upload", "expressions", "missing_scripts", "missing_references", "notes", "post_build",
        };

        public class Parameters
        {
            [ToolParameter("Avatar to audit: '' / 'selection' (default; selection moved up to its VRCAvatarDescriptor, else the only avatar in loaded scenes), '/Root/Child' path, '#instanceId', unique name, or 'Assets/.../X.prefab'", Required = false, DefaultValue = "")]
            public string target { get; set; }

            [ToolParameter("'all' (default; findings for the platform that is not the active build target are info), 'pc', 'android' or 'ios' (android and ios share the mobile limits)", Required = false, DefaultValue = "all")]
            public string platform { get; set; }

            [ToolParameter("Include per-component breakdown lists in the report file (default true)", Required = false, DefaultValue = "true")]
            public bool include_lists { get; set; }

            [ToolParameter("Rows per breakdown list (default 15, max 200)", Required = false, DefaultValue = "15")]
            public int top_n { get; set; }

            [ToolParameter("Also process a temporary clone with NDMF and audit the result (default false; slow, pass timeout_seconds)", Required = false, DefaultValue = "false")]
            public bool post_build { get; set; }

            [ToolParameter("'summary' (default) or 'full'. Summary keeps the response short; the full report is always saved to disk", Required = false, DefaultValue = "summary")]
            public string detail { get; set; }

            [ToolParameter("Maximum number of issues in a summary response, errors first (default 30)", Required = false, DefaultValue = "30")]
            public int max_issues { get; set; }

            [ToolParameter("Write the full JSON report under Library/ClaudeCreatorTools/Reports (default true)", Required = false, DefaultValue = "true")]
            public bool save_report { get; set; }

            [ToolParameter("MCP call timeout in seconds (default 30, max 3600); read by the MCP server, not by the tool", Required = false, DefaultValue = "30")]
            public int timeout_seconds { get; set; }
        }

        public static object HandleCommand(JObject @params) =>
            CctMcpBridge.Handle(@params, CctAvatarAudit.ToolName, CctAvatarAudit.Run, SummaryKeys);
    }
}
#endif
