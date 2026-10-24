#if CCT_UNITY_MCP
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    [McpForUnityTool(CctWorldGimmicks.ToolName,
        Description = "Claude Creator Tools: UdonSharp (U#) world gimmick samples for VRChat WORLD projects - local toggle, global " +
                      "synced toggle (Manual sync, late joiners), mirror switcher, teleporter, pickup resetter (ObjectSync owner-safe), " +
                      "UI audio volume (+PlayerData), area toggle (trigger zone), player counter. action=list (read-only): samples with " +
                      "sync mode and summary, whether UdonSharp / TextMeshPro / uGUI / Worlds SDK are present, whether each sample is " +
                      "already imported at dest and has a U# program asset, next steps. action=import: copies the package's " +
                      "Samples~/WorldGimmicks into dest (default Assets/ClaudeWorldGimmicks) with their .meta (stable GUIDs), refuses " +
                      "with NO_UDONSHARP when UdonSharp is absent (avatar projects would get compile errors), requires confirm=true " +
                      "(otherwise returns a dry-run plan), never overwrites existing files unless overwrite=true and never replaces " +
                      "existing .meta, then AssetDatabase.Refresh and creates one UdonSharpProgramAsset per script (UdonSharp does not " +
                      "create them for copied scripts). Returns files_written / files_skipped / program_assets_created and Japanese " +
                      "next_steps (wait for compile, Add Component, wiring, ClientSim + Build & Test). Triggers a script recompile.")]
    public static class CctWorldGimmicksTool
    {
        static readonly string[] SummaryKeys =
        {
            "action", "udonsharp_installed", "udonsharp_program_asset_type", "textmeshpro_installed", "ugui_installed",
            "vrchat_worlds_sdk", "vrchat_worlds_sdk_version", "dest", "dest_exists", "samples", "sample_count",
            "imported_count", "program_asset_count", "dry_run", "plan", "files_written", "files_failed", "files_skipped",
            "program_assets_created", "next_steps", "asmdef",
        };

        public class Parameters
        {
            [ToolParameter("'list' (default, read-only) or 'import'", Required = false, DefaultValue = "list")]
            public string action { get; set; }

            [ToolParameter("Destination folder under Assets/ (default 'Assets/ClaudeWorldGimmicks'); must not be inside a folder with an .asmdef", Required = false, DefaultValue = "Assets/ClaudeWorldGimmicks")]
            public string dest { get; set; }

            [ToolParameter("import: must be true to write files; otherwise a dry-run plan is returned (default false)", Required = false, DefaultValue = "false")]
            public bool confirm { get; set; }

            [ToolParameter("import: overwrite sample files that already exist at dest (existing .meta files are always kept) (default false)", Required = false, DefaultValue = "false")]
            public bool overwrite { get; set; }

            [ToolParameter("import: create a UdonSharpProgramAsset (<Name>.asset) next to each imported script, as UdonSharp's 'Create > U# Script' does (default true)", Required = false, DefaultValue = "true")]
            public bool create_program_assets { get; set; }

            [ToolParameter("import: only these samples by class name, e.g. ['CctTeleporter','CctLocalToggle'] (README is always included); omit for all", Required = false)]
            public string[] only { get; set; }

            [ToolParameter("'summary' (default) or 'full'. Summary keeps the response short; the full report is always saved to disk", Required = false, DefaultValue = "summary")]
            public string detail { get; set; }

            [ToolParameter("Maximum number of issues in a summary response, errors first (default 30)", Required = false, DefaultValue = "30")]
            public int max_issues { get; set; }

            [ToolParameter("Write the full JSON report under Library/ClaudeCreatorTools/Reports (default true)", Required = false, DefaultValue = "true")]
            public bool save_report { get; set; }
        }

        public static object HandleCommand(JObject @params) =>
            CctMcpBridge.Handle(@params, CctWorldGimmicks.ToolName, CctWorldGimmicks.Run, SummaryKeys);
    }
}
#endif
