#if CCT_UNITY_MCP
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    [McpForUnityTool(CctSnapshot.ToolName,
        Description = "Claude Creator Tools: file-level backup for risky edits. MCP deletes are NOT on the editor Undo stack, and a scene " +
                      "copy cannot bring back shared materials/meshes/AnimationClips changed later, so call action=create BEFORE destructive " +
                      "or bulk changes (deleting objects/assets, editing shared materials, optimizing, re-importing). " +
                      "create: copies asset files byte-for-byte with their .meta (GUIDs and importer settings preserved) to " +
                      "<project>/ClaudeCreatorTools/Snapshots/<id>/ - 'assets' (files and/or folders under Assets/, folders recursive), every " +
                      "open saved scene by default (unsaved scene changes are captured from memory without saving the scene), optionally " +
                      "their dependencies; Packages/ is never copied; aborts before copying if larger than max_total_mb. Returns id, file " +
                      "count, size, folder. list: snapshots newest first. diff: per file unchanged/modified/deleted/meta_changed (GUID or " +
                      "import settings), changed-line counts for YAML assets, files added to snapshotted folders, and warnings for unsaved " +
                      "scene/asset changes the files cannot show. restore: writes files + .meta back and reimports, reloads affected open " +
                      "scenes (discarding their in-memory changes), but only with confirm=true - otherwise a dry run listing exactly what " +
                      "would be overwritten/recreated. A real restore first snapshots the current state automatically " +
                      "(pre_restore_snapshot_id) so it can be undone; refused in Play mode or while compiling. Added files are never " +
                      "deleted. delete: removes a snapshot folder (confirm=true required).")]
    public static class CctSnapshotTool
    {
        static readonly string[] SummaryKeys =
        {
            "action", "id", "label", "created_at", "snapshot_dir", "manifest_path", "file_count", "folder_count", "total_bytes",
            "total_mb", "incomplete", "scenes", "skipped", "skipped_omitted", "files_omitted",
            "snapshots_dir", "snapshots", "total_count", "omitted",
            "counts", "changes", "changes_omitted", "added", "added_omitted",
            "dry_run", "would_overwrite", "would_recreate", "would_restore_meta", "would_reimport", "would_reload_scenes",
            "would_create_folders", "unchanged_count", "pre_restore_snapshot_id", "restored", "recreated", "meta_restored",
            "reimported", "reloaded_scenes", "created_folders", "failed", "lists_omitted", "deleted",
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

            [ToolParameter("diff / restore: maximum entries per returned list (default 200); counts always cover everything", Required = false, DefaultValue = "200")]
            public int max_entries { get; set; }

            [ToolParameter("'summary' (default) or 'full'. Summary keeps the response short; the full report is always saved to disk", Required = false, DefaultValue = "summary")]
            public string detail { get; set; }

            [ToolParameter("Maximum number of issues in a summary response, errors first (default 30)", Required = false, DefaultValue = "30")]
            public int max_issues { get; set; }

            [ToolParameter("Write the full JSON report under Library/ClaudeCreatorTools/Reports (default true)", Required = false, DefaultValue = "true")]
            public bool save_report { get; set; }
        }

        public static object HandleCommand(JObject @params) =>
            CctMcpBridge.Handle(@params, CctSnapshot.ToolName, CctSnapshot.Run, SummaryKeys);
    }
}
#endif
