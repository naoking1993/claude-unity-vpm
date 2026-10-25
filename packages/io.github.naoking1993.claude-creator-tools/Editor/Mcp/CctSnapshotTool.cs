#if CCT_UNITY_MCP
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    [McpForUnityTool(CctSnapshot.ToolName,
        Description = "Claude Creator Tools: file-level backup and restore for risky edits. MCP deletes are NOT on the editor Undo stack, and a " +
                      "scene copy cannot bring back shared materials/meshes/AnimationClips changed later, so call action=create BEFORE destructive " +
                      "or bulk changes (deleting objects/assets, editing shared materials/controllers/prefabs, optimizing, changing import " +
                      "settings) and keep the returned id. Typical flow: create -> edit -> diff id=<id> to see what changed -> restore id=<id> (dry " +
                      "run) -> show the user -> restore id=<id> confirm=true. " +
                      "create: copies files byte-for-byte with their .meta (GUIDs and import settings kept) to " +
                      "<project>/ClaudeCreatorTools/Snapshots/<id>/ (outside Assets/, never imported). Without assets it saves only the open scene " +
                      "files; to make shared assets recoverable too, list the materials/prefabs/clips/controllers you will change in assets, or " +
                      "pass include_dependencies=true. Open scenes with unsaved changes are captured from memory via a temporary copy in " +
                      "Assets/__ClaudeCreatorToolsTemp (removed again; the open scene keeps its path and unsaved state; other packages' scene-save " +
                      "hooks run as on Ctrl+S); in Play mode scenes come from disk. Other assets with unsaved in-memory changes are stored as on " +
                      "disk (ASSET_HAS_UNSAVED_CHANGES). Packages/ is never copied (SKIPPED_PACKAGE). Aborts before copying if over max_total_mb " +
                      "(SIZE_LIMIT; details.largest lists the biggest files). Returns id, file_count, file_types (count per extension), total_mb, " +
                      "scenes (scene_state disk | in_memory), snapshot_dir. " +
                      "list: snapshots newest first (id, label, created_at, kind manual | pre-restore, file_count, total_mb) and total_mb_all. " +
                      "diff: per file unchanged/modified/deleted/meta_changed (GUID or import settings) with counts, changed-line counts for YAML " +
                      "assets, snapshot_file (absolute path of the stored copy, readable with file tools), files added to snapshotted folders, " +
                      "VRChat Blueprint ID differences (BLUEPRINT_ID_CHANGES), and unsaved scene/asset changes the files cannot show " +
                      "(SCENE_HAS_UNSAVED_CHANGES). " +
                      "restore: without confirm=true it is a dry run that lists exactly what would be overwritten/recreated/reimported and which " +
                      "open scenes would be reloaded, changing nothing (CONFIRM_REQUIRED); show it to the user and pass confirm=true only after " +
                      "they agree. A real restore first snapshots the current state (including unsaved open-scene changes) and returns " +
                      "pre_restore_snapshot_id (restore that id to undo; files the restore recreated are not deleted by the undo, " +
                      "RECREATED_FILES_REMAIN); then it unloads the affected open scenes, writes files + .meta, reimports them and reopens the " +
                      "scenes in the same Hierarchy slots (in-memory scene and asset changes are discarded). An old scene/prefab can bring back an " +
                      "old VRChat Blueprint ID (BLUEPRINT_ID_CHANGES: re-set it after the restore or leave that file out of assets). Refused in " +
                      "Play mode, while compiling/importing/baking lighting (EDITOR_BUSY) or while an affected prefab has unsaved changes in " +
                      "Prefab Mode (PREFAB_STAGE_UNSAVED). Added files are never deleted. Restoring scripts triggers a compile and domain reload " +
                      "after the call (SCRIPTS_RESTORED): wait for MCP to reconnect before the next call. " +
                      "delete: removes a snapshot folder (dry run unless confirm=true; ask the user first). " +
                      "Response lists are capped at max_entries and report <key>_omitted; the complete lists are then under full_lists in the " +
                      "saved report (report_path) or with detail='full'. Big snapshots/restores (hundreds of MB, dependency_filter='all', reimporting textures/models) can " +
                      "exceed the 30 s default: pass timeout_seconds=300; a timed-out call keeps running in Unity, so check with action=list or " +
                      "diff instead of repeating it. " +
                      "Parameters: action (required) = 'create' | 'list' | 'diff' | 'restore' | 'delete'; " +
                      "label = create: free-text label stored in the manifest, Japanese OK (default ''); " +
                      "assets = create: JSON array of asset files and/or folders under Assets/ (folders recursive, 'Assets/...' paths only); " +
                      "restore: optional subset of snapshot paths (a folder selects everything under it; omitted = all); " +
                      "include_open_scenes = create: also snapshot every loaded scene that has a file (default true); " +
                      "include_dependencies = create: also snapshot recursive dependencies under Assets/ (AssetDatabase.GetDependencies, " +
                      "plus in-memory references of unsaved scenes; default false); " +
                      "dependency_filter = create: 'editable' (default: .unity .prefab .mat .anim .controller .overrideController .mask " +
                      ".asset .playable .physicMaterial .renderTexture .flare .signal .lighting) or 'all' (also textures, models, scripts, " +
                      "audio); " +
                      "max_total_mb = create: abort before copying if the estimate exceeds this (default 500); " +
                      "id = diff / restore / delete: snapshot id from create or list (or a pre_restore_snapshot_id to undo a restore); " +
                      "confirm = restore / delete: true to actually change files (default false = dry run); " +
                      "limit = list: maximum snapshots returned (default 50); " +
                      "max_entries = create / diff / restore: maximum entries per returned list (default 50; counts always cover everything)." +
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
