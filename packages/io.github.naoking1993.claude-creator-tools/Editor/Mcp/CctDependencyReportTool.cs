#if CCT_UNITY_MCP
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    [McpForUnityTool(CctDependencyReport.ToolName,
        Description = "Claude Creator Tools: read-only dependency / reference report. Use it before packaging a modified gimmick or " +
                      "outfit for another avatar or project (mode=scene_references on its scene root, save it as an independent " +
                      "prefab, then mode=dependencies on that prefab or folder with export_root and declared_packages) and before " +
                      "deleting or replacing assets (mode=reverse: who uses this?). Parameters: mode = 'dependencies' (default: " +
                      "dependencies grouped inside / outside export_root, required packages, Missing GUIDs/scripts/prefabs/references, " +
                      "TSV of every dependency at dependency_list_path) | 'reverse' (files under search_root that contain the GUID of " +
                      "one of the assets, plus in-memory users in loaded scenes) | 'scene_references' (references from components " +
                      "under target to scene objects outside it, which become None in a prefab); assets = JSON array of asset paths, " +
                      "folders (= every file inside) or 32-hex GUIDs (default: Project-window selection); export_root = dependencies: " +
                      "comma-separated export folder(s) (default: folders in assets, else no inside/outside verdict); include_scripts " +
                      "= dependencies: false skips .cs/.dll under Assets/ (default true); declared_packages = dependencies: JSON array " +
                      "of packages the destination already has (names or 'com.vrchat.*'), others become warnings; target = " +
                      "scene_references: scene GameObject ('/Avatar/Gimmick', '#instanceId', unique name; default: Hierarchy " +
                      "selection); dependencies: optional scene object or prefab asset added to the inputs; search_root = reverse: " +
                      "comma-separated folder(s) to search (default 'Assets'); scan_open_scenes = reverse: also check loaded scenes in " +
                      "memory (default true); scan_code = reverse: also search .cs/.shader/.hlsl text (default false); include_inbound " +
                      "= scene_references: also list references into target (default true); max_components = scene components to scan " +
                      "(default 20000); max_file_mb = skip non-input files above this size (default 50; 0 = no limit); large_file_mb = " +
                      "LARGE_FILE info threshold (default 4); max_entries = items per list (default 50; totals and *_omitted counts " +
                      "cover the rest); time_budget_seconds = stop the scans sooner (default and maximum: timeout_seconds - 5). " +
                      "conclusive=false (any mode) means a partial scan (SCAN_INCOMPLETE says why): never treat 0 missing / " +
                      "TARGET_UNUSED as final then. Scans stop at timeout_seconds - 5 (25 s by default): pass timeout_seconds=240 for " +
                      "a whole-project reverse search or a big folder. Reads saved files (UNSAVED_CHANGES); binary files and " +
                      "name-based use from code (Shader.Find, Resources.Load) are not covered. Run in edit mode. Writes only the " +
                      "report and TSV under Library/ClaudeCreatorTools." +
                      CctMcpBridge.CommonParamsDoc)]
    public static class CctDependencyReportTool
    {
        static readonly string[] SummaryKeys =
        {
            "mode", "asset_serialization", "time_budget", "issues_capped",
            // dependencies
            "inputs", "input_count", "inputs_omitted", "input_folders", "input_folders_omitted", "inputs_from_folders", "inputs_from_selection", "target", "target_assets",
            "target_assets_omitted", "export_roots", "totals", "by_type", "inside_export_root", "assets_outside_root",
            "assets_outside_root_omitted", "required_packages", "builtin", "missing_guids", "missing_guids_omitted", "missing_scripts",
            "missing_scripts_omitted", "missing_prefabs", "missing_prefabs_omitted", "missing_references", "missing_references_omitted",
            "asmdefs", "asmdefs_omitted", "large_files", "large_files_omitted", "scan", "unsaved", "dependency_list_path",
            "dependency_list_rows",
            // reverse
            "targets", "target_count", "targets_omitted", "search_roots", "user_count", "users", "users_omitted", "users_by_type",
            "internal_reference_count", "per_target", "per_target_omitted", "unused_targets", "unused_targets_omitted", "conclusive",
            "scene_user_count", "scene_users", "scene_users_omitted", "scenes_scanned",
            // scene_references
            "is_prefab_asset", "components_scanned", "asset_reference_count", "external_reference_count", "external_references",
            "external_references_omitted", "path_resolved_references", "path_resolved_references_omitted", "scene_only_objects",
            "scene_only_objects_omitted", "missing_reference_count", "missing_script_count", "missing_prefab_count",
            "inbound_reference_count", "inbound_references", "inbound_references_omitted", "prefab",
        };

        public class Parameters
        {
            [ToolParameter("'dependencies' (default) | 'reverse' | 'scene_references'", Required = false, DefaultValue = "dependencies")]
            public string mode { get; set; }

            [ToolParameter("dependencies / reverse: asset paths ('Assets/...', 'Packages/...'), folders (expanded to every file inside) or 32-hex GUIDs. " +
                           "Omitted = current Project-window selection", Required = false)]
            public string[] assets { get; set; }

            [ToolParameter("dependencies: folder(s) that make up the export, comma-separated (e.g. 'Assets/MyGimmick'). Dependencies under Assets/ outside " +
                           "it are OUTSIDE_EXPORT_ROOT warnings. Default: the folders given in 'assets'; with no folder, no inside/outside verdict", Required = false)]
            public string export_root { get; set; }

            [ToolParameter("dependencies: false leaves .cs/.dll files under Assets/ out of the groups, sizes and script checks (default true). " +
                           "Packages needed by scripts are always reported", Required = false, DefaultValue = "true")]
            public bool include_scripts { get; set; }

            [ToolParameter("dependencies: packages the destination is known to have (exact names or prefix wildcards like 'com.vrchat.*'). Other packages become " +
                           "PACKAGE_DEPENDENCY warnings instead of info", Required = false)]
            public string[] declared_packages { get; set; }

            [ToolParameter("scene_references: scene GameObject to check, e.g. the gimmick/outfit root to be saved as a prefab ('selection' (default), " +
                           "'/Avatar/Gimmick', '#instanceId', unique name). dependencies: optional scene GameObject whose directly referenced assets are " +
                           "added to the analysed set, or a prefab asset path (analysed like assets=[path])", Required = false)]
            public string target { get; set; }

            [ToolParameter("reverse: folder(s) to search, comma-separated (default 'Assets'; e.g. 'Assets,Packages/com.example')", Required = false, DefaultValue = "Assets")]
            public string search_root { get; set; }

            [ToolParameter("reverse: also check objects in the loaded scenes / open Prefab Mode for in-memory references (default true)", Required = false, DefaultValue = "true")]
            public bool scan_open_scenes { get; set; }

            [ToolParameter("reverse: also search .cs/.shader/.hlsl/.cginc/.compute text for the GUID string (default false)", Required = false, DefaultValue = "false")]
            public bool scan_code { get; set; }

            [ToolParameter("scene_references: also report objects elsewhere in the scene that reference INTO the target (default true)", Required = false, DefaultValue = "true")]
            public bool include_inbound { get; set; }

            [ToolParameter("reverse / scene_references: maximum scene components to scan before stopping (default 20000)", Required = false, DefaultValue = "20000")]
            public int max_components { get; set; }

            [ToolParameter("Skip (and report) non-input files larger than this many MB in the GUID/text and Missing scans; 0 = no limit (default 50)", Required = false, DefaultValue = "50")]
            public double max_file_mb { get; set; }

            [ToolParameter("dependencies: report dependency files larger than this many MB as LARGE_FILE info (default 4)", Required = false, DefaultValue = "4")]
            public double large_file_mb { get; set; }

            [ToolParameter("Maximum entries per returned list (default 50); counts always cover everything", Required = false, DefaultValue = "50")]
            public int max_entries { get; set; }

            [ToolParameter("Stop the scans after this many seconds with partial results (default and maximum: timeout_seconds - 5, i.e. 25; " +
                           "raise timeout_seconds for longer scans)", Required = false)]
            public double time_budget_seconds { get; set; }

            [ToolParameter("'summary' (default) or 'full'. Summary keeps the response short; the full report is always saved to disk", Required = false, DefaultValue = "summary")]
            public string detail { get; set; }

            [ToolParameter("Maximum number of issues in a summary response, errors first (default 30)", Required = false, DefaultValue = "30")]
            public int max_issues { get; set; }

            [ToolParameter("Write the full JSON report under Library/ClaudeCreatorTools/Reports (default true)", Required = false, DefaultValue = "true")]
            public bool save_report { get; set; }

            [ToolParameter("MCP call timeout in seconds (default 30, max 3600); read by the MCP server, and by this tool: scans stop at timeout_seconds - 5", Required = false, DefaultValue = "30")]
            public int timeout_seconds { get; set; }
        }

        public static object HandleCommand(JObject @params) =>
            CctMcpBridge.Handle(@params, CctDependencyReport.ToolName, CctDependencyReport.Run, SummaryKeys);
    }
}
#endif
