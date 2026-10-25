#if CCT_UNITY_MCP
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    [McpForUnityTool(CctSnapshot.ToolName,
        Description = "Claude Creator Tools: file-level backup and restore for risky edits (MCP deletes are not on the editor Undo " +
                      "stack). Call action=create BEFORE destructive or bulk changes (deleting objects/assets, editing shared " +
                      "materials/controllers/prefabs, optimising, changing import settings) and keep the returned id. Flow: create -> " +
                      "edit -> diff -> restore (dry run) -> show the user -> restore confirm=true. Parameters: action (required) = " +
                      "'create' | 'list' | 'diff' | 'restore' | 'delete' (delete removes a stored snapshot, not project assets); " +
                      "label = create: free-text label stored in the manifest, " +
                      "Japanese OK (default ''); assets = create: JSON array of asset files and/or folders under Assets/ (folders " +
                      "recursive; without assets only the open scene files are saved, so list the shared assets you will change or " +
                      "pass include_dependencies=true); restore: optional subset of snapshot paths (a folder selects everything under " +
                      "it; omitted = all); include_open_scenes = create: also snapshot every loaded scene that has a file, unsaved " +
                      "changes captured from memory (default true); include_dependencies = create: also snapshot recursive " +
                      "dependencies under Assets/ (default false); dependency_filter = create: 'editable' (default: .unity .prefab " +
                      ".mat .anim .controller .overrideController .mask .asset .playable .physicMaterial .renderTexture .flare .signal " +
                      ".lighting) or 'all' (also textures, models, scripts, audio); max_total_mb = create: abort before copying if the " +
                      "estimate exceeds this (default 500); id = diff / restore / delete: snapshot id from create or list (or a " +
                      "pre_restore_snapshot_id to undo a restore); confirm = restore / delete: true to actually change files (default " +
                      "false = dry run; show it to the user and pass true only after they agree); limit = list: maximum snapshots " +
                      "returned (default 50); max_entries = create / diff / restore: maximum entries per returned list (default 50; " +
                      "complete lists in the report at report_path). Files are copied byte-for-byte with their .meta to " +
                      "<project>/ClaudeCreatorTools/Snapshots/<id>/ (outside Assets/; Packages/ is never copied). A real restore first " +
                      "snapshots the current state (pre_restore_snapshot_id), then writes files + .meta, reimports and reopens the " +
                      "affected scenes (in-memory changes are discarded; added files are never deleted); it is refused in Play mode or " +
                      "while the editor is busy (EDITOR_BUSY). Restoring scripts triggers a domain reload: wait for MCP to reconnect. " +
                      "Check BLUEPRINT_ID_CHANGES before restoring scenes/prefabs (an old file brings back its old Blueprint ID, " +
                      "which changes the upload target). Big snapshots/restores: pass timeout_seconds=240; " +
                      "after a timeout check with action=list or diff instead of repeating the call." +
                      CctMcpBridge.CommonParamsDoc)]
    public static class CctSnapshotTool
    {
        // "details" carries the payload of failed results (pre_restore_snapshot_id, SIZE_LIMIT's largest files, ...).
        // Not kept: "files" (create) and "full_lists" (complete versions of capped lists) - both stay in the saved report.
        static readonly string[] SummaryKeys =
        {
            "details", "action", "id", "label", "created_at", "snapshot_dir", "manifest_path", "file_count", "folder_count", "total_bytes",
            "total_mb", "incomplete", "scenes", "file_types", "skipped", "skipped_omitted",
            "snapshots_dir", "snapshots", "total_count", "total_mb_all", "omitted",
            "counts", "changes", "changes_omitted", "added", "added_omitted",
            "dry_run", "would_overwrite", "would_overwrite_omitted", "would_recreate", "would_recreate_omitted",
            "would_restore_meta", "would_restore_meta_omitted", "would_reimport", "would_reimport_omitted",
            "would_reload_scenes", "would_reload_scenes_omitted", "would_create_folders", "would_create_folders_omitted",
            "unchanged_count", "pre_restore_snapshot_id", "created_by_original_restore", "created_by_original_restore_omitted",
            "restored", "restored_omitted", "recreated", "recreated_omitted", "meta_restored", "meta_restored_omitted",
            "reimported", "reimported_omitted", "reloaded_scenes", "reloaded_scenes_omitted", "created_folders", "created_folders_omitted",
            "failed", "failed_omitted", "deleted",
        };

        public class Parameters
        {
            [ToolParameter("'create' | 'list' | 'diff' | 'restore' | 'delete'", Required = true)]
            public string action { get; set; }

            [ToolParameter("create: free-text label stored in the manifest (Japanese OK), e.g. 'before deleting old outfit'", Required = false, DefaultValue = "")]
            public string label { get; set; }

            [ToolParameter("create: asset files and/or folders under Assets/ (folders are copied recursively; Packages/ paths are reported as skipped). " +
                           "restore: optional subset of snapshot paths (a folder selects everything under it); omit to restore all", Required = false)]
            public string[] assets { get; set; }

            [ToolParameter("create: also snapshot every loaded scene that has a file; unsaved changes are captured from memory (default true)", Required = false, DefaultValue = "true")]
            public bool include_open_scenes { get; set; }

            [ToolParameter("create: also snapshot recursive dependencies (AssetDatabase.GetDependencies, plus in-memory references of unsaved scenes) under Assets/ (default false)", Required = false, DefaultValue = "false")]
            public bool include_dependencies { get; set; }

            [ToolParameter("create: which dependencies to copy - 'editable' (default: .unity .prefab .mat .anim .controller .overrideController .mask .asset " +
                           ".playable .physicMaterial .renderTexture .flare .signal .lighting) or 'all' (also textures, models, scripts, audio)", Required = false, DefaultValue = "editable")]
            public string dependency_filter { get; set; }

            [ToolParameter("create: abort before copying anything if the estimated total exceeds this many MB (default 500)", Required = false, DefaultValue = "500")]
            public double max_total_mb { get; set; }

            [ToolParameter("diff / restore / delete: snapshot id from create or list", Required = false)]
            public string id { get; set; }

            [ToolParameter("restore / delete: must be true to actually change files; otherwise a dry run is returned (default false)", Required = false, DefaultValue = "false")]
            public bool confirm { get; set; }

            [ToolParameter("list: maximum snapshots to return, newest first (default 50)", Required = false, DefaultValue = "50")]
            public int limit { get; set; }

            [ToolParameter("create / diff / restore: maximum entries per returned list (default 50); counts always cover everything and the report has the complete lists", Required = false, DefaultValue = "50")]
            public int max_entries { get; set; }

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
            CctMcpBridge.Handle(@params, CctSnapshot.ToolName, CctSnapshot.Run, SummaryKeys);
    }
}
#endif
