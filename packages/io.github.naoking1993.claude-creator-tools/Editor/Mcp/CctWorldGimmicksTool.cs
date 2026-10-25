#if CCT_UNITY_MCP
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    [McpForUnityTool(CctWorldGimmicks.ToolName,
        Description = "Claude Creator Tools: UdonSharp (U#) world gimmick samples for VRChat WORLD projects - local toggle, global " +
                      "synced toggle (Manual sync, late joiners), mirror switcher, teleporter, pickup resetter (ObjectSync-aware: idle pickups are reset by the presser taking " +
                      "ownership, held ones by their holder), UI audio volume (+PlayerData), area toggle (trigger zone with periodic " +
                      "position re-check), player counter. Use when the user wants one of these gimmicks in a world; call list first. " +
                      "action=list (read-only): each sample's sync mode, Japanese summary, required packages and whether they are present, " +
                      "source (absolute path of the script in the package; its Japanese header has the setup), whether UdonSharp / Worlds SDK " +
                      "are present, whether each sample is already imported at dest (with its U# program asset) or exists elsewhere in the " +
                      "project, whether dest is usable, importable (names an import would add; pass them as only), next_steps. " +
                      "action=import (WRITES files, then Unity recompiles; wait and retry on EDITOR_BUSY before the next call): copies the " +
                      "package's Samples~/WorldGimmicks into dest with their .meta (stable GUIDs); refuses with NO_UDONSHARP when UdonSharp " +
                      "is absent (avatar projects would get compile errors), MISSING_DEPENDENCY when a selected sample needs a missing " +
                      "package (the failure lists missing and importable), DEST_HAS_ASMDEF / DEST_SPECIAL_FOLDER when dest is under an " +
                      "asmdef/asmref, an Editor folder or Assets/Plugins; skips samples whose script already exists elsewhere " +
                      "(SAMPLE_EXISTS_ELSEWHERE); never overwrites existing files unless overwrite=true and never replaces existing .meta; " +
                      "then AssetDatabase.Refresh and creates one UdonSharpProgramAsset (<Name>.asset) per script, also for a copy found " +
                      "elsewhere under Assets/ that has none (UdonSharp does not create them for copied scripts). Without confirm=true it " +
                      "is a dry run returning plan and program_assets_planned. Returns files_written / files_skipped / files_failed / " +
                      "program_assets_created and Japanese next_steps. After import read <dest>/README.md: per gimmick which GameObject " +
                      "gets the script, inspector fields with their serialized names (for setting them over MCP), collider / Is Trigger / " +
                      "layer, UI OnClick / OnValueChanged wiring (UdonBehaviour.SendCustomEvent + exact method name), ClientSim and " +
                      "Build & Test (2 clients) checklist. " +
                      "Parameters: action = 'list' (default) or 'import'; dest = destination folder under Assets/ (default " +
                      "'Assets/ClaudeWorldGimmicks'; must not be inside an asmdef/asmref folder, an Editor folder or Assets/Plugins); " +
                      "confirm = import only: true to actually write files, otherwise dry run (default false); overwrite = import only: " +
                      "overwrite sample files that already exist at dest, existing .meta files are always kept (default false); " +
                      "create_program_assets = import only: create <Name>.asset (UdonSharpProgramAsset) next to each imported script " +
                      "(default true); only = import only: array of sample class names to import, e.g. ['CctTeleporter','CctLocalToggle'] " +
                      "(case-insensitive; README is always included; omit for all)." +
                      CctMcpBridge.CommonParamsDoc)]
    public static class CctWorldGimmicksTool
    {
        static readonly string[] SummaryKeys =
        {
            "action", "udonsharp_installed", "udonsharp_program_asset_type", "vrchat_worlds_sdk", "vrchat_worlds_sdk_version",
            "requirements", "missing", "dest", "dest_exists", "dest_problem", "asmdef", "samples", "sample_count", "imported_count",
            "program_asset_count", "importable", "dry_run", "plan", "program_assets_planned", "files_written", "files_failed",
            "files_skipped", "program_assets_created", "next_steps",
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

            [ToolParameter("import: only these samples by class name, e.g. ['CctTeleporter','CctLocalToggle'] (case-insensitive; README is always included); omit for all", Required = false)]
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
