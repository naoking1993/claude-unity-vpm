#if CCT_UNITY_MCP
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    [McpForUnityTool(CctDependencyReport.ToolName,
        Description = "Claude Creator Tools: read-only dependency and reference report for packaging a modified gimmick/outfit for another " +
                      "avatar or project, and for 'who uses this?' checks before deleting or replacing assets. Never saves, imports or dirties anything. " +
                      "mode=dependencies (default): AssetDatabase.GetDependencies of 'assets' (prefabs, materials, controllers, scenes, folders = all " +
                      "files inside, GUIDs accepted; omitted = Project-window selection) and/or the assets a scene GameObject 'target' references " +
                      "directly. Groups results into inside_export_root (export_root folders; input folders are the export root when omitted), " +
                      "assets_outside_root (by Assets/A/B with sizes and which file pulls them in), packages / required_packages (name, installed " +
                      "version, vpm/registry/embedded, which inputs use it - the version is the verified one, not a minimum), and Unity built-ins; plus " +
                      "a streaming YAML scan of every text asset and .meta for 'guid:' references that resolve to nothing (MISSING_GUID with file, " +
                      "line and field such as m_Script/m_Shader/m_SourcePrefab), and a load-based check of prefabs/materials/controllers/assets " +
                      "for Missing Script, Missing Prefab and Missing references (also catches a valid GUID with a wrong fileID). Reports file " +
                      "sizes, large files, asmdefs needed by Assets/ scripts, scenes pulled in as dependencies, and warns when Asset Serialization " +
                      "is not Force Text. mode=reverse: every file under search_root (default Assets) whose text or .meta contains the GUID of " +
                      "one of 'assets' (folder = every file inside; references between the targets themselves are counted separately), grouped by " +
                      "type, plus in-memory references from objects in the loaded scenes / open Prefab Mode (component, property path) and prefab " +
                      "instances of target prefabs; TARGET_UNUSED lists targets nothing references. Code (.cs/.shader) is skipped unless scan_code=true; " +
                      "files above max_file_mb and binary files are reported as skipped; name-based use from code (Shader.Find, Resources.Load) cannot " +
                      "be detected. mode=scene_references: scans every component under scene GameObject 'target' (SerializedObject, hidden fields " +
                      "included) for references to scene objects OUTSIDE the target (EXTERNAL_SCENE_REFERENCE: these become None when the target is " +
                      "saved as a standalone prefab; Modular Avatar referencePath references are PATH_RESOLVED_REFERENCE info), references to " +
                      "unsaved in-memory objects, Missing references/scripts/prefabs, references from the rest of the scene INTO the target " +
                      "(INBOUND_SCENE_REFERENCE), and whether the target is a prefab instance with overrides (PREFAB_HAS_OVERRIDES: exporting the " +
                      "original prefab would drop them). Asset references and references inside the target are normal and only counted. " +
                      "Results are file-based for dependencies/reverse (save first; UNSAVED_CHANGES warns) and in-memory for scene_references.")]
    public static class CctDependencyReportTool
    {
        static readonly string[] SummaryKeys =
        {
            "mode", "asset_serialization",
            // dependencies
            "inputs", "input_count", "inputs_omitted", "inputs_from_folders", "inputs_from_selection", "target", "target_assets",
            "target_assets_omitted", "export_roots", "totals", "by_type", "inside_export_root", "assets_outside_root",
            "assets_outside_root_omitted", "required_packages", "builtin", "missing_guids", "missing_guids_omitted", "missing_scripts",
            "missing_prefabs", "missing_references", "asmdefs", "large_files", "scan", "unsaved", "issues_capped",
            // reverse
            "targets", "target_count", "targets_omitted", "search_roots", "user_count", "users", "users_omitted", "users_by_type",
            "internal_reference_count", "per_target", "unused_targets", "unused_targets_omitted", "scene_user_count", "scene_users",
            "scene_users_omitted", "scenes_scanned",
            // scene_references
            "is_prefab_asset", "components_scanned", "asset_reference_count", "external_reference_count", "external_references",
            "external_references_omitted", "path_resolved_references", "scene_only_objects", "missing_reference_count",
            "missing_script_count", "inbound_reference_count", "inbound_references", "inbound_references_omitted", "prefab",
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

            [ToolParameter("dependencies: list .cs/.dll dependencies under Assets/ in the groups and sizes (default true). Packages needed by scripts are always reported",
                Required = false, DefaultValue = "true")]
            public bool include_scripts { get; set; }

            [ToolParameter("dependencies: packages the destination is known to have (exact names or prefix wildcards like 'com.vrchat.*'). Other packages become " +
                           "PACKAGE_DEPENDENCY warnings instead of info", Required = false)]
            public string[] declared_packages { get; set; }

            [ToolParameter("scene_references (required): scene GameObject to check, e.g. the gimmick/outfit root to be saved as a prefab ('selection', " +
                           "'/Avatar/Gimmick', '#instanceId', unique name). dependencies: optional scene GameObject whose directly referenced assets are " +
                           "added to the analysed set", Required = false)]
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

            [ToolParameter("Skip (and report) non-input files larger than this many MB in GUID scans; 0 = no limit (default 50)", Required = false, DefaultValue = "50")]
            public double max_file_mb { get; set; }

            [ToolParameter("dependencies: report dependency files larger than this many MB as LARGE_FILE info (default 4)", Required = false, DefaultValue = "4")]
            public double large_file_mb { get; set; }

            [ToolParameter("Maximum entries per returned list (default 50); counts always cover everything", Required = false, DefaultValue = "50")]
            public int max_entries { get; set; }

            [ToolParameter("'summary' (default) or 'full'. Summary keeps the response short; the full report is always saved to disk", Required = false, DefaultValue = "summary")]
            public string detail { get; set; }

            [ToolParameter("Maximum number of issues in a summary response, errors first (default 30)", Required = false, DefaultValue = "30")]
            public int max_issues { get; set; }

            [ToolParameter("Write the full JSON report under Library/ClaudeCreatorTools/Reports (default true)", Required = false, DefaultValue = "true")]
            public bool save_report { get; set; }
        }

        public static object HandleCommand(JObject @params) =>
            CctMcpBridge.Handle(@params, CctDependencyReport.ToolName, CctDependencyReport.Run, SummaryKeys);
    }
}
#endif
