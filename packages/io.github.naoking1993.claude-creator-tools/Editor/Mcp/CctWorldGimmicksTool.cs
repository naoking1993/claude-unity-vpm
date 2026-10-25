#if CCT_UNITY_MCP
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    [McpForUnityTool(CctWorldGimmicks.ToolName,
        Description = "Claude Creator Tools: UdonSharp (U#) world gimmick samples for VRChat WORLD projects - local toggle, global " +
                      "synced toggle, mirror switcher, teleporter, pickup resetter, UI audio volume, area toggle, player counter. Use " +
                      "when the user wants one of these gimmicks in a world; call action=list first. Parameters: action = 'list' " +
                      "(default, read-only: per sample its sync mode, Japanese summary, required packages, source script path and " +
                      "import state, plus importable names and next_steps) or 'import' (WRITES files); dest = destination folder under " +
                      "Assets/ (default 'Assets/ClaudeWorldGimmicks'; must not be inside an asmdef/asmref folder, an Editor folder or " +
                      "Assets/Plugins); confirm = import only: true to actually write files, otherwise a dry run returning plan " +
                      "(default false); overwrite = import only: overwrite sample files that already exist at dest, existing .meta " +
                      "files are always kept (default false); create_program_assets = import only: create <Name>.asset " +
                      "(UdonSharpProgramAsset) next to each imported script (default true); only = import only: sample class names as " +
                      "a JSON array, e.g. [\"CctTeleporter\",\"CctLocalToggle\"] (through execute_custom_tool also the comma string " +
                      "\"CctTeleporter,CctLocalToggle\"; case-insensitive; README is always included; omit for all). import copies the " +
                      "package's Samples~/WorldGimmicks with their .meta (stable GUIDs), runs AssetDatabase.Refresh and creates the " +
                      "program assets (also next to a copy found elsewhere that has none); it refuses without UdonSharp (NO_UDONSHARP), when a selected sample needs a missing package " +
                      "(MISSING_DEPENDENCY, lists importable) or for a bad dest (DEST_HAS_ASMDEF / DEST_SPECIAL_FOLDER), and skips " +
                      "samples whose script exists elsewhere (SAMPLE_EXISTS_ELSEWHERE). Unity recompiles afterwards: wait and retry on " +
                      "EDITOR_BUSY. For the real import pass timeout_seconds=120; a timed-out import still finishes, so call list " +
                      "instead of repeating it. Then read <dest>/README.md: per gimmick the GameObject, serialized field names, " +
                      "collider/layer, UI SendCustomEvent wiring and the ClientSim / 2-client test checklist." +
                      CctMcpBridge.CommonParamsDoc)]
    public static class CctWorldGimmicksTool
    {
        static readonly string[] SummaryKeys =
        {
            "action", "udonsharp_installed", "udonsharp_program_asset_type", "vrchat_worlds_sdk", "vrchat_worlds_sdk_version",
            "requirements", "missing", "dest", "dest_exists", "dest_problem", "asmdef", "samples", "sample_count", "imported_count",
            "program_asset_count", "importable", "dry_run", "plan", "program_assets_planned", "files_written", "files_failed",
            "files_skipped", "program_assets_created", "next_steps",
            // failure context (SAMPLES_NOT_FOUND)
            "package_root",
        };

        public class Parameters
        {
            [ToolParameter("'list' (default, read-only) or 'import'", Required = false, DefaultValue = "list")]
            public string action { get; set; }

            [ToolParameter("Destination folder under Assets/ (default 'Assets/ClaudeWorldGimmicks'); must not be inside an asmdef/asmref folder, an Editor folder or Assets/Plugins", Required = false, DefaultValue = "Assets/ClaudeWorldGimmicks")]
            public string dest { get; set; }

            [ToolParameter("import: must be true to write files; otherwise a dry-run plan is returned (default false)", Required = false, DefaultValue = "false")]
            public bool confirm { get; set; }

            [ToolParameter("import: overwrite sample files that already exist at dest (existing .meta files are always kept) (default false)", Required = false, DefaultValue = "false")]
            public bool overwrite { get; set; }

            [ToolParameter("import: create a UdonSharpProgramAsset (<Name>.asset) next to each imported script, as UdonSharp's 'Create > U# Script' does (default true)", Required = false, DefaultValue = "true")]
            public bool create_program_assets { get; set; }

            [ToolParameter("import: only these samples by class name as a JSON array, e.g. [\"CctTeleporter\",\"CctLocalToggle\"] (case-insensitive; README is always included); omit for all", Required = false)]
            public string[] only { get; set; }

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
            CctMcpBridge.Handle(@params, CctWorldGimmicks.ToolName, CctWorldGimmicks.Run, SummaryKeys);
    }
}
#endif
