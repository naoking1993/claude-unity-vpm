#if CCT_UNITY_MCP
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    [McpForUnityTool(CctAvatarAudit.ToolName,
        Description = "Claude Creator Tools: read-only pre-upload audit of one VRChat avatar. Returns the VRChat performance rank for PC " +
                      "and for Android/iOS (Quest/mobile share one table) with the stats that set the overall rank and the value needed " +
                      "for the next better rank; every ranked stat (triangles, bounds, texture memory, skinned/basic meshes, material " +
                      "slots, PhysBones, contacts, constraints, animators, bones, lights, particles, trails/lines, raycasts, cloth, " +
                      "physics, audio) computed by the VRChat SDK's own calculator via reflection when the avatars SDK is installed " +
                      "(data.method.stats = 'sdk', same numbers as the SDK Control Panel) or by the tool's re-implementation of the SDK " +
                      "3.10.5 rules otherwise (every stat marked estimate, info SDK_CALCULATOR_UNAVAILABLE). Also: upload blockers the " +
                      "SDK builder reports (missing scripts with m_Script GUIDs, missing references, non-whitelisted components, mesh " +
                      "Read/Write, texture mip streaming / max size, mobile shaders, humanoid rig, PipelineManager, particle stop actions, " +
                      "expression parameter budget 256 bits, menu parameters / icons), Write Defaults mixing per playable-layer " +
                      "controller, descriptor summary (layers, view position, lip sync), Expression Parameters list and menu tree, and " +
                      "notes about Modular Avatar / NDMF / AAO / VRCFury (values are pre-build unless post_build is used). Summary keys: " +
                      "avatar, method, ranks (overall + limiting_stats with limit_for_better_rank and min_displayed_rank_effect = what " +
                      "VRChat does for viewers who block that rank: avatar_fallback or <component>_removed), stats, upload (blocked = " +
                      "true when an error the SDK builder refuses exists for the ACTIVE build target; blocking codes listed; " +
                      "MISSING_SCRIPT etc. are errors that do not block), expressions, missing_scripts, missing_references, notes, " +
                      "post_build. Severity: the active build target's platform is primary; with platform='all' findings that only " +
                      "concern the other platform (e.g. Android ranks, mobile shaders and whitelist while the target is PC) are info, " +
                      "so pass platform='android' when preparing a Quest/mobile version on a PC target. Breakdown " +
                      "lists (heaviest meshes, textures, PhysBones, materials, shaders, ...) and rank tables are in the saved report " +
                      "file (report_path), not in the summary. Use it before and after optimisation work, before upload, and to find " +
                      "what dominates the rank. Side effects: none on scenes or assets (prefab assets are opened read-only and " +
                      "unloaded; the SDK scan only touches non-serialized runtime data). " +
                      "Parameters: target = avatar to audit: '' or 'selection' (default: the selected object, moved up to its " +
                      "VRCAvatarDescriptor; with nothing selected, the only avatar in the loaded scenes), a hierarchy path '/Root/Child', " +
                      "'#instanceId', a unique GameObject name, or a prefab asset path 'Assets/.../X.prefab'; ambiguity returns an " +
                      "error listing candidates; platform = 'all' (default; both, other-platform findings as info), 'pc', 'android' or " +
                      "'ios' (android and ios use the same mobile limits; the chosen platform's findings stay warnings); include_lists = include the per-component breakdown lists in the report (default true); " +
                      "top_n = rows per breakdown list (default 15, max 200); post_build = also process a temporary clone with NDMF " +
                      "(MA, AAO and other NDMF plugins; not VRCFury) in a private preview scene without saving generated assets, audit " +
                      "it, report data.post_build (ranks after build, stats that changed, NDMF errors, parameter bits after MA) and " +
                      "destroy the clone and its generated objects (default false; can take tens of seconds on big avatars, so pass " +
                      "timeout_seconds such as 120; writes NDMF pass logs to the Console; not available in Play mode)." +
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
