#if CCT_UNITY_MCP
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    [McpForUnityTool(CctWorldAudit.ToolName,
        Description = "Claude Creator Tools: read-only pre-upload audit of a VRChat WORLD scene (Worlds SDK 3.10.5, Unity " +
                      "2022.3.22f1). Use it before Build & Test or upload, after big scene changes, and when a world fails to build or " +
                      "behaves differently in VRChat. Parameters: scene = 'active' (default; the scene the SDK uploads) | 'all' (every " +
                      "loaded scene) | path or name of a loaded scene (the SDK-blocking descriptor/pipeline/ObjectSync/PhysBone checks " +
                      "always cover every loaded scene); platform = 'all' (default) | 'pc' (PC-only world: skips the Android/iOS " +
                      "checks) | 'android' | 'ios' (android and ios share the mobile limits); top_n = list length per section (default " +
                      "15, 1-500); check_references = scan components for references to deleted assets/objects (default true); " +
                      "max_reference_scan = maximum components to scan for missing references (default 5000; the scan also stops when " +
                      "the audit has run timeout_seconds - 10 s); check_editor_log = read the tail of Editor.log for 'error CS' and " +
                      "UdonSharp errors (default true). Read data.verdict first: sdk_build_blocked and build_blockers are the errors " +
                      "the SDK or UdonSharp really stop on (the SDK's own conditions re-implemented), upload_blockers (last " +
                      "Android/iOS build > 100 MB), other_errors (broken in VRChat but not blocking). Warnings/info cover the SDK's " +
                      "non-blocking checks and the creator docs' advice (mirrors, lighting, cameras, post processing, Udon sync, " +
                      "pickups, video players, UI, NOT_WHITELISTED; ANDROID_* matter only for a Quest/mobile upload). Static " +
                      "editor-side estimate: it never builds, uploads, runs ClientSim or modifies the scene. Summary data: verdict, " +
                      "scenes, platforms, counts, project, descriptor, layers, lighting, rendering, mirrors, udon, gimmicks, android, " +
                      "not_checked, notes; interactables, colliders, ui, components, hotspots and editor_log are only in the report at " +
                      "report_path. For big worlds pass timeout_seconds=120." +
                      CctMcpBridge.CommonParamsDoc)]
    public static class CctWorldAuditTool
    {
        static readonly string[] SummaryKeys =
        {
            "verdict", "scenes", "platforms", "counts", "project", "descriptor", "layers", "lighting", "rendering", "mirrors", "udon",
            "gimmicks", "android", "not_checked", "notes",
            // failure context (SCENE_NOT_LOADED / WORLDS_SDK_MISSING)
            "loaded_scenes", "avatars_sdk_present",
        };

        public class Parameters
        {
            [ToolParameter("'active' (default), 'all', or the path/name of a loaded scene to audit; SDK-blocking descriptor/pipeline checks always use every loaded scene", Required = false, DefaultValue = "active")]
            public string scene { get; set; }

            [ToolParameter("'all' (default), 'pc', 'android' or 'ios' (android and ios share the mobile limits)", Required = false, DefaultValue = "all")]
            public string platform { get; set; }

            [ToolParameter("Entries per list in each section (default 15, 1-500)", Required = false, DefaultValue = "15")]
            public int top_n { get; set; }

            [ToolParameter("Scan components for references to deleted assets/objects (default true)", Required = false, DefaultValue = "true")]
            public bool check_references { get; set; }

            [ToolParameter("Maximum components scanned for missing references (default 5000); the scan also stops at timeout_seconds - 10 s", Required = false, DefaultValue = "5000")]
            public int max_reference_scan { get; set; }

            [ToolParameter("Read the tail of Editor.log for 'error CS' and UdonSharp errors (default true)", Required = false, DefaultValue = "true")]
            public bool check_editor_log { get; set; }

            [ToolParameter("'summary' (default) or 'full'. Summary keeps the response short; the full report is always saved to disk", Required = false, DefaultValue = "summary")]
            public string detail { get; set; }

            [ToolParameter("Maximum number of issues in a summary response, errors first (default 30)", Required = false, DefaultValue = "30")]
            public int max_issues { get; set; }

            [ToolParameter("Write the full JSON report under Library/ClaudeCreatorTools/Reports (default true)", Required = false, DefaultValue = "true")]
            public bool save_report { get; set; }

            [ToolParameter("MCP call timeout in seconds (default 30, max 3600); read by the MCP server, and by this tool to bound its scans", Required = false, DefaultValue = "30")]
            public int timeout_seconds { get; set; }
        }

        public static object HandleCommand(JObject @params) =>
            CctMcpBridge.Handle(@params, CctWorldAudit.ToolName, CctWorldAudit.Run, SummaryKeys);
    }
}
#endif
