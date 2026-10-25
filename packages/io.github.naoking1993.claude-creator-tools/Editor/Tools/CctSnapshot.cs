using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// cct_snapshot: makes destructive MCP work recoverable. MCP deletes do not go onto the editor Undo stack, and a
    /// scene copy alone cannot bring back shared meshes / materials / AnimationClips changed later, so this tool copies
    /// the asset files themselves (+ their .meta, so GUIDs and importer settings survive) to
    /// &lt;project&gt;/ClaudeCreatorTools/Snapshots/&lt;id&gt;/ where they can be diffed and restored byte-for-byte.
    ///
    /// Actions: create | list | diff | restore | delete. restore and delete change nothing unless confirm=true
    /// (otherwise they return a dry run). restore first snapshots the current state of every file it will touch
    /// ("pre-restore"), so a restore can itself be undone.
    ///
    /// Snapshot layout: &lt;id&gt;/manifest.json (UTF-8, no BOM), &lt;id&gt;/files/NNNNN-&lt;name&gt; (+ ".meta"),
    /// &lt;id&gt;/folders/NNNNN-&lt;name&gt;.meta. Stored names are short on purpose (Windows MAX_PATH).
    ///
    /// Issue codes:
    /// | code                       | severity        | meaning                                                                      |
    /// |----------------------------|-----------------|------------------------------------------------------------------------------|
    /// | CONFIRM_REQUIRED           | warning         | restore/delete dry run: nothing was changed; call again with confirm=true    |
    /// | SNAPSHOT_NOT_FOUND         | (fail code)     | no snapshot with that id                                                     |
    /// | INVALID_ID                 | (fail code)     | id is empty or contains characters other than A-Z a-z 0-9 _ - .              |
    /// | INVALID_ARGUMENT           | (fail code)     | bad action / dependency_filter / max_total_mb                                |
    /// | MANIFEST_INVALID           | fail / warning  | manifest.json missing or unreadable (warning in list)                        |
    /// | NOTHING_TO_SNAPSHOT        | (fail code)     | create found no file to copy                                                 |
    /// | SIZE_LIMIT                 | (fail code)     | estimated size exceeds max_total_mb; nothing was copied                      |
    /// | PATH_OUTSIDE_PROJECT       | error           | absolute path or '..' segment; skipped                                       |
    /// | NOT_ASSET_PATH             | error           | path is not under Assets/; skipped                                           |
    /// | INVALID_PATH               | error           | path contains invalid characters; skipped                                    |
    /// | SKIPPED_PACKAGE            | info / warning  | Packages/ path; managed by VCC, not snapshotted (warning for a restore       |
    /// |                            |                 | subset). Hidden Packages/ sub-scenes (NDMF's "___NDMF Preview___", always    |
    /// |                            |                 | loaded with Modular Avatar) and Packages/ dependencies only go to the        |
    /// |                            |                 | manifest's skipped list; dependencies are counted by one info issue          |
    /// | ASSET_NOT_FOUND            | warning         | path does not exist; skipped                                                 |
    /// | UNTITLED_SCENE_SKIPPED     | warning         | an open Untitled scene has no file and cannot be snapshotted                 |
    /// | SCENE_CAPTURED_FROM_MEMORY | info            | open scene had unsaved changes; its in-memory state was stored               |
    /// | SCENE_CAPTURE_FAILED       | warning         | in-memory scene state could not be saved; disk version stored (or skipped)   |
    /// | SCENE_STATE_CHANGED        | warning         | saving the in-memory copy changed the open scene's path or dirty flag        |
    /// | PLAY_MODE_SCENE_FROM_DISK  | warning         | in Play mode the scene files are copied from disk (play state is not stored) |
    /// | ASSET_HAS_UNSAVED_CHANGES  | warning         | asset is loaded with unsaved in-memory changes the file does not contain     |
    /// | PREFAB_STAGE_UNSAVED       | warning / fail  | prefab open in Prefab Mode has unsaved changes (restore refuses)             |
    /// | DEPENDENCIES_FAILED        | warning         | AssetDatabase.GetDependencies threw; dependencies may be incomplete          |
    /// | COPY_FAILED                | error / fail    | a file could not be read/copied (snapshot incomplete); fail when none copied |
    /// | TEMP_CLEANUP_FAILED        | warning         | the temporary scene copy / temp folder could not be removed                  |
    /// | SCENE_HAS_UNSAVED_CHANGES  | warning         | diff: open scene differs in memory from disk (save it to diff that state)    |
    /// | FILES_ADDED                | info            | diff/restore: files now under a snapshotted folder that the snapshot lacks   |
    /// | PROJECT_ROOT_DIFFERS       | warning         | the snapshot was taken in a different project folder                         |
    /// | BLUEPRINT_ID_CHANGES       | warning         | diff/restore: a .unity/.prefab holds a different VRChat Blueprint ID         |
    /// |                            |                 | (PipelineManager.blueprintId, incl. prefab overrides) than the snapshot;     |
    /// |                            |                 | restoring it changes the upload target                                       |
    /// | NOT_IN_SNAPSHOT            | warning / fail  | restore: a requested path is not in the snapshot (fail when none matched)    |
    /// | SNAPSHOT_CORRUPT           | error           | restore: stored copy missing or its SHA-256 differs from the manifest        |
    /// | FILE_READ_ONLY             | error           | restore: target file is read-only (e.g. VCS lock); not overwritten           |
    /// | GUID_CONFLICT              | warning         | restore: the snapshot's GUID is now used by another asset                    |
    /// | EDITOR_BUSY                | (fail code)     | restore refused while playing, compiling, importing or baking lighting      |
    /// | PRE_RESTORE_SNAPSHOT       | info            | restore: id of the automatic pre-restore snapshot                            |
    /// | PRE_RESTORE_FAILED         | (fail code)     | restore aborted: current state (incl. unsaved open-scene changes) not backed up|
    /// | MISSING_BEFORE_RESTORE     | (skipped entry) | pre-restore manifest: file did not exist before the restore recreated it     |
    /// | RECREATED_FILES_REMAIN     | warning         | restore of a pre-restore snapshot (undo): files the original restore         |
    /// |                            |                 | recreated still exist; restore never deletes, so delete them for a full undo |
    /// | FILE_RESTORED              | info            | restore: file content overwritten from the snapshot                          |
    /// | FILE_RECREATED             | info            | restore: missing file recreated from the snapshot                            |
    /// | META_RESTORED              | info            | restore: only the .meta (GUID / importer settings) was written back          |
    /// | SCENE_RELOADED             | info            | restore: open scene reloaded from disk (in-memory changes discarded)         |
    /// | SCRIPTS_RESTORED           | info            | restore: .cs/.asmdef/.asmref/.dll/.rsp written; compile + domain reload follow|
    /// | ASSET_REIMPORTED           | info            | restore: unchanged file force-reimported to drop unsaved in-memory changes   |
    /// | RESTORE_FAILED             | error / fail    | restore: writing/importing a file failed (fail when nothing succeeded)       |
    /// | RESTORE_VERIFY_FAILED      | error           | restore: SHA-256 after writing differs from the snapshot                     |
    /// | GUID_MISMATCH_AFTER_RESTORE| warning         | restore: AssetDatabase reports a different GUID than the snapshot after import|
    /// | ASSET_STILL_DIRTY          | warning         | restore: asset still has unsaved in-memory changes after the reimport        |
    /// | SCENE_CLOSE_FAILED         | (fail code)     | restore: an open scene could not be unloaded before writing; nothing written |
    /// | SCENE_REOPEN_FAILED        | error / warning | restore: a scene could not be reopened afterwards (open it manually);        |
    /// |                            |                 | warning when only the temporary empty scene could not be closed              |
    /// | DELETE_FAILED              | (fail code)     | delete: the snapshot folder could not be removed                             |
    /// | EXCEPTION / NO_RESULT      | (fail code)     | from CctRunner: unexpected exception (details.stack)                         |
    ///
    /// Response lists (skipped, changes, added, would_*, restored, ...) are capped at max_entries with a
    /// "&lt;key&gt;_omitted" count; the saved report then has the complete list under full_lists.&lt;key&gt;.
    /// </summary>
    public static partial class CctSnapshot
    {
        public const string ToolName = "cct_snapshot";

        /// <summary>Temporary folder for in-memory scene copies; removed afterwards when this tool created it and it is empty.</summary>
        public const string TempFolder = "Assets/__ClaudeCreatorToolsTemp";
        const string TempFolderName = "__ClaudeCreatorToolsTemp";
        const string ManifestName = "manifest.json";
        const int FormatVersion = 1;
        const int BufferSize = 1 << 16;
        const long LineDiffMaxBytes = 32L * 1024 * 1024;
        const int DirtyCheckMax = 2000;
        /// <summary>Upper bound for the complete lists kept in the report (full_lists), so a huge project cannot bloat it.</summary>
        const int FullListMax = 20000;
        const int DefaultMaxEntries = 50;

        /// <summary>dependency_filter "editable": text/native assets an MCP session typically changes.</summary>
        static readonly HashSet<string> EditableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".unity", ".prefab", ".mat", ".anim", ".controller", ".overrideController", ".mask", ".asset",
            ".playable", ".physicMaterial", ".renderTexture", ".flare", ".signal", ".lighting",
        };

        public static CctResult Run(CctArgs args)
        {
            var action = args.GetEnum("action", null, "create", "list", "diff", "restore", "delete");
            switch (action)
            {
                case "create": return Create(args);
                case "list": return List(args);
                case "diff": return Diff(args);
                case "restore": return Restore(args);
                case "delete": return Delete(args);
                default:
                    return CctResult.Fail("action には create / list / diff / restore / delete のいずれかを指定してください",
                        "INVALID_ARGUMENT", new Dictionary<string, object> { ["action"] = args.GetString("action") });
            }
        }

        // ------------------------------------------------------------------ list / delete

        static CctResult List(CctArgs args)
        {
            int limit = args.GetInt("limit", 50, 1, 1000);
            var root = CctPaths.SnapshotsDir;
            var r = CctResult.Ok("");
            r.Put("action", "list");
            r.Put("snapshots_dir", root);
            var rows = new List<Dictionary<string, object>>();
            int noManifest = 0;
            long allBytes = 0;
            if (Directory.Exists(root))
            {
                foreach (var dirAbs in Directory.GetDirectories(root))
                {
                    var id = Path.GetFileName(dirAbs);
                    if (!IsValidId(id)) continue;
                    var manifestPath = CctPaths.Combine(CctPaths.Normalize(dirAbs), ManifestName);
                    if (!File.Exists(manifestPath)) { noManifest++; continue; }
                    try
                    {
                        var m = Manifest.FromJson(ReadJsonFile(manifestPath));
                        var row = new Dictionary<string, object>
                        {
                            ["id"] = id,
                            ["label"] = m.Label,
                            ["created_at"] = m.CreatedAt,
                            ["kind"] = m.Kind,
                            ["file_count"] = m.Files.Count,
                            ["total_mb"] = Mb(m.TotalBytes),
                        };
                        if (!string.IsNullOrEmpty(m.RestoreOf)) row["restore_of"] = m.RestoreOf;
                        if (m.Incomplete) row["incomplete"] = true;
                        rows.Add(row);
                        allBytes += m.TotalBytes;
                    }
                    catch (Exception e)
                    {
                        r.AddIssue(CctSeverity.Warning, "MANIFEST_INVALID", $"スナップショット '{id}' の manifest.json を読めません: {e.Message}", value: manifestPath);
                    }
                }
            }
            if (noManifest > 0)
                r.AddIssue(CctSeverity.Info, "MANIFEST_INVALID", $"manifest.json の無いフォルダが {noManifest} 個あります (作成途中で中断されたもの)。一覧には含めません。不要なら {root} から手動で削除できます", value: noManifest);

            var sorted = rows.OrderByDescending(x => SortKey(x["created_at"] as string)).ThenByDescending(x => (string)x["id"], StringComparer.Ordinal).ToList();
            r.Put("total_count", sorted.Count);
            r.Put("total_mb_all", Mb(allBytes));
            r.Put("snapshots", sorted.Take(limit).Cast<object>().ToList());
            if (sorted.Count > limit) r.Put("omitted", sorted.Count - limit);
            r.Message = sorted.Count == 0 ? "スナップショットはまだありません" : $"スナップショット {sorted.Count} 件 / 合計 {Mb(allBytes)} MB (新しい順に {Math.Min(limit, sorted.Count)} 件を表示)";
            return r;
        }

        static CctResult Delete(CctArgs args)
        {
            var id = args.GetString("id");
            if (!IsValidId(id))
                return CctResult.Fail("id を指定してください (list で確認できます)", "INVALID_ID", new Dictionary<string, object> { ["id"] = id });
            var dir = SnapshotDir(id);
            if (!Directory.Exists(dir))
                return CctResult.Fail($"スナップショット '{id}' が見つかりません (cct_snapshot action=list で確認できます)", "SNAPSHOT_NOT_FOUND", new Dictionary<string, object> { ["snapshots_dir"] = CctPaths.SnapshotsDir });

            var r = CctResult.Ok("");
            r.Put("action", "delete");
            r.Put("id", id);
            r.Put("snapshot_dir", dir);
            Manifest m = null;
            try { m = Manifest.FromJson(ReadJsonFile(CctPaths.Combine(dir, ManifestName))); }
            catch (Exception e) { r.AddIssue(CctSeverity.Warning, "MANIFEST_INVALID", "manifest.json を読めません (フォルダごと削除できます): " + e.Message); }
            if (m != null)
            {
                r.Put("label", m.Label);
                r.Put("created_at", m.CreatedAt);
                r.Put("file_count", m.Files.Count);
                r.Put("total_mb", Mb(m.TotalBytes));
            }

            if (!args.GetBool("confirm", false))
            {
                r.Put("dry_run", true);
                r.AddIssue(CctSeverity.Warning, "CONFIRM_REQUIRED", "確認用の dry run です。まだ何も削除していません。削除してよいかユーザーに確認してから confirm=true を付けて再実行してください", value: id);
                r.Message = $"[dry run] スナップショット '{id}'" + (m != null ? $" ({m.CreatedAt} / {m.Files.Count} ファイル / {Mb(m.TotalBytes)} MB)" : "") + " を削除します (未実行)";
                return r;
            }

            try
            {
                // Copies are written by this tool without the read-only flag, but clear it anyway so Windows can delete.
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, true);
            }
            catch (Exception e)
            {
                return CctResult.Fail($"スナップショット '{id}' を削除できませんでした: {e.Message}", "DELETE_FAILED", new Dictionary<string, object> { ["snapshot_dir"] = dir });
            }
            r.Put("dry_run", false);
            r.Put("deleted", true);
            r.Message = $"スナップショット '{id}' を削除しました";
            return r;
        }

        // ------------------------------------------------------------------ manifest model

        sealed class FileEntry
        {
            public string AssetPath, Guid, Sha256, MetaSha256, Stored, MetaStored, Source, SceneState;
            public long Size, MetaSize;
            public bool SceneWasDirty;

            public Dictionary<string, object> ToJson()
            {
                var d = new Dictionary<string, object>
                {
                    ["asset_path"] = AssetPath,
                    ["guid"] = Guid,
                    ["size"] = Size,
                    ["sha256"] = Sha256,
                    ["meta_sha256"] = MetaSha256,
                    ["meta_size"] = MetaSize,
                    ["stored"] = Stored,
                    ["meta_stored"] = MetaStored,
                    ["source"] = Source,
                };
                if (SceneState != null)
                {
                    d["scene_was_dirty"] = SceneWasDirty;
                    d["scene_state"] = SceneState;
                }
                return d;
            }

            public static FileEntry FromJson(Dictionary<string, object> d) => new FileEntry
            {
                AssetPath = Str(d, "asset_path"),
                Guid = Str(d, "guid"),
                Size = Long(d, "size"),
                Sha256 = Str(d, "sha256"),
                MetaSha256 = Str(d, "meta_sha256"),
                MetaSize = Long(d, "meta_size"),
                Stored = Str(d, "stored"),
                MetaStored = Str(d, "meta_stored"),
                Source = Str(d, "source"),
                SceneState = Str(d, "scene_state"),
                SceneWasDirty = d.TryGetValue("scene_was_dirty", out var v) && v is bool b && b,
            };
        }

        sealed class FolderEntry
        {
            public string AssetPath, Guid, MetaSha256, MetaStored;

            public Dictionary<string, object> ToJson() => new Dictionary<string, object>
            {
                ["asset_path"] = AssetPath,
                ["guid"] = Guid,
                ["meta_sha256"] = MetaSha256,
                ["meta_stored"] = MetaStored,
            };

            public static FolderEntry FromJson(Dictionary<string, object> d) => new FolderEntry
            {
                AssetPath = Str(d, "asset_path"),
                Guid = Str(d, "guid"),
                MetaSha256 = Str(d, "meta_sha256"),
                MetaStored = Str(d, "meta_stored"),
            };
        }

        sealed class Manifest
        {
            public string Id, Label, CreatedAt, UnityVersion, ProjectRoot, Kind, RestoreOf, ToolVersion;
            public bool Incomplete;
            public long TotalBytes;
            public List<FileEntry> Files = new List<FileEntry>();
            public List<FolderEntry> Folders = new List<FolderEntry>();
            public List<string> FolderRoots = new List<string>();
            public List<Dictionary<string, object>> Skipped = new List<Dictionary<string, object>>();
            public Dictionary<string, object> Options = new Dictionary<string, object>();

            public Dictionary<string, object> ToJson()
            {
                var d = new Dictionary<string, object>
                {
                    ["format_version"] = FormatVersion,
                    ["id"] = Id,
                    ["label"] = Label,
                    ["created_at"] = CreatedAt,
                    ["kind"] = Kind,
                    ["unity_version"] = UnityVersion,
                    ["project_root"] = ProjectRoot,
                    ["tool_version"] = ToolVersion,
                    ["file_count"] = Files.Count,
                    ["total_bytes"] = TotalBytes,
                    ["incomplete"] = Incomplete,
                    ["options"] = Options,
                    ["folder_roots"] = FolderRoots.Cast<object>().ToList(),
                    ["files"] = Files.Select(f => (object)f.ToJson()).ToList(),
                    ["folders"] = Folders.Select(f => (object)f.ToJson()).ToList(),
                    ["skipped"] = Skipped.Cast<object>().ToList(),
                };
                if (!string.IsNullOrEmpty(RestoreOf)) d["restore_of"] = RestoreOf;
                return d;
            }

            public static Manifest FromJson(Dictionary<string, object> d)
            {
                if (d == null) throw new FormatException("manifest root is not an object");
                var m = new Manifest
                {
                    Id = Str(d, "id"),
                    Label = Str(d, "label") ?? "",
                    CreatedAt = Str(d, "created_at"),
                    Kind = Str(d, "kind"),
                    UnityVersion = Str(d, "unity_version"),
                    ProjectRoot = Str(d, "project_root"),
                    ToolVersion = Str(d, "tool_version"),
                    RestoreOf = Str(d, "restore_of"),
                    TotalBytes = Long(d, "total_bytes"),
                    Incomplete = d.TryGetValue("incomplete", out var inc) && inc is bool b && b,
                };
                if (d.TryGetValue("options", out var o) && o is Dictionary<string, object> od) m.Options = od;
                // Entries without a path cannot be compared or restored; dropping them here keeps every caller null-safe.
                foreach (var x in Objects(d, "files"))
                {
                    var f = FileEntry.FromJson(x);
                    if (!string.IsNullOrEmpty(f.AssetPath)) m.Files.Add(f);
                }
                foreach (var x in Objects(d, "folders"))
                {
                    var f = FolderEntry.FromJson(x);
                    if (!string.IsNullOrEmpty(f.AssetPath)) m.Folders.Add(f);
                }
                foreach (var x in Objects(d, "skipped")) m.Skipped.Add(x);
                if (d.TryGetValue("folder_roots", out var fr) && fr is List<object> frl)
                    foreach (var x in frl)
                        if (x is string s) m.FolderRoots.Add(s);
                return m;
            }
        }

        static string Str(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

        static long Long(Dictionary<string, object> d, string key)
        {
            if (!d.TryGetValue(key, out var v) || v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
        }

        static IEnumerable<Dictionary<string, object>> Objects(Dictionary<string, object> d, string key)
        {
            if (!d.TryGetValue(key, out var v) || !(v is List<object> list)) yield break;
            foreach (var x in list)
                if (x is Dictionary<string, object> xd) yield return xd;
        }

        static Dictionary<string, object> ReadJsonFile(string path) =>
            CctJson.Parse(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;

        static void WriteManifest(string dir, Manifest m)
        {
            var path = CctPaths.Combine(dir, ManifestName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, CctJson.Serialize(m.ToJson(), true), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>Loads a snapshot by id; on failure returns null and a ready-made failed result.</summary>
        static Manifest LoadManifest(string id, out string dir, out CctResult failure)
        {
            dir = null;
            failure = null;
            if (!IsValidId(id))
            {
                failure = CctResult.Fail("id を指定してください (cct_snapshot action=list で確認できます)", "INVALID_ID", new Dictionary<string, object> { ["id"] = id });
                return null;
            }
            dir = SnapshotDir(id);
            var path = CctPaths.Combine(dir, ManifestName);
            if (!File.Exists(path))
            {
                failure = CctResult.Fail($"スナップショット '{id}' が見つかりません (cct_snapshot action=list で確認できます)", "SNAPSHOT_NOT_FOUND",
                    new Dictionary<string, object> { ["snapshots_dir"] = CctPaths.SnapshotsDir });
                return null;
            }
            try
            {
                var m = Manifest.FromJson(ReadJsonFile(path));
                if (string.IsNullOrEmpty(m.Id)) m.Id = id;
                return m;
            }
            catch (Exception e)
            {
                failure = CctResult.Fail($"スナップショット '{id}' の manifest.json を読めません: {e.Message}", "MANIFEST_INVALID",
                    new Dictionary<string, object> { ["manifest"] = path });
                return null;
            }
        }

        // ------------------------------------------------------------------ paths

        static string SnapshotDir(string id) => CctPaths.Combine(CctPaths.SnapshotsDir, id);

        static bool IsValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 120 || id.Contains("..")) return false;
            if (!char.IsLetterOrDigit(id[0]) || id[0] > 127) return false;
            foreach (var ch in id)
                if (!(ch < 128 && (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.'))) return false;
            return true;
        }

        /// <summary>
        /// Normalizes a user/manifest path to "Assets/..." and checks it stays inside the project's Assets folder.
        /// Returns false with a code: PATH_OUTSIDE_PROJECT, NOT_ASSET_PATH, INVALID_PATH, or SKIPPED_PACKAGE
        /// (then <paramref name="assetPath"/> holds the normalized Packages/ path).
        /// </summary>
        static bool TryNormalizeAssetPath(string raw, out string assetPath, out string code) =>
            TryNormalizeAssetPath(raw, CctPaths.ProjectRoot, out assetPath, out code);

        static bool TryNormalizeAssetPath(string raw, string projectRoot, out string assetPath, out string code)
        {
            assetPath = null;
            code = null;
            if (string.IsNullOrWhiteSpace(raw)) { code = "INVALID_PATH"; return false; }
            var p = raw.Trim().Replace('\\', '/');
            if (p.StartsWith("/", StringComparison.Ordinal) || p.IndexOf(':') >= 0) { code = "PATH_OUTSIDE_PROJECT"; return false; }
            if (p.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || p.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|' }) >= 0) { code = "INVALID_PATH"; return false; }
            while (p.Contains("//")) p = p.Replace("//", "/");
            while (p.StartsWith("./", StringComparison.Ordinal)) p = p.Substring(2);
            p = p.TrimEnd('/');
            if (p.Length == 0) { code = "INVALID_PATH"; return false; }
            var segments = p.Split('/');
            foreach (var s in segments)
                if (s.Length == 0 || s == "." || s == ".." || s.Trim().Length == 0) { code = "PATH_OUTSIDE_PROJECT"; return false; }
            if (string.Equals(segments[0], "Packages", StringComparison.OrdinalIgnoreCase))
            {
                assetPath = "Packages" + p.Substring(segments[0].Length);
                code = "SKIPPED_PACKAGE";
                return false;
            }
            if (!string.Equals(segments[0], "Assets", StringComparison.OrdinalIgnoreCase)) { code = "NOT_ASSET_PATH"; return false; }
            p = "Assets" + p.Substring(segments[0].Length);
            if (p.Length > 5 && p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) p = p.Substring(0, p.Length - 5);

            string full;
            try { full = CctPaths.Normalize(Path.GetFullPath(Path.Combine(projectRoot, p))); }
            catch (Exception) { code = "INVALID_PATH"; return false; }
            var assetsRoot = CctPaths.Normalize(projectRoot).TrimEnd('/') + "/Assets";
            if (!string.Equals(full, assetsRoot, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(assetsRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                code = "PATH_OUTSIDE_PROJECT";
                return false;
            }
            assetPath = p;
            return true;
        }

        /// <summary>Absolute path (forward slashes) of an already validated asset path.</summary>
        static string AbsOf(string assetPath) => CctPaths.Combine(CctPaths.ProjectRoot, assetPath);

        /// <summary>Absolute path of a file stored inside a snapshot folder; null when the manifest entry would escape it.</summary>
        static string ResolveStored(string dir, string relative)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            try
            {
                var full = CctPaths.Normalize(Path.GetFullPath(Path.Combine(dir, relative)));
                var root = CctPaths.Normalize(Path.GetFullPath(dir)).TrimEnd('/') + "/";
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch (Exception) { return null; }
        }

        static bool IsInTempFolder(string assetPath) =>
            string.Equals(assetPath, TempFolder, StringComparison.OrdinalIgnoreCase) ||
            assetPath.StartsWith(TempFolder + "/", StringComparison.OrdinalIgnoreCase);

        /// <summary>Names the AssetDatabase ignores (hidden, trailing '~', cvs, *.tmp): not assets, never snapshotted.</summary>
        static bool IsIgnoredName(string name) =>
            string.IsNullOrEmpty(name) || name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith("~", StringComparison.Ordinal) ||
            string.Equals(name, "cvs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

        /// <summary>Walks an asset folder like the AssetDatabase does (skips ignored names and .meta files).</summary>
        static void WalkAssetFolder(string assetDir, string absDir, Action<string> onFile, Action<string> onFolder, Action<string, Exception> onError, int depth = 0)
        {
            if (depth > 64) return; // junction loops
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(absDir);
                dirs = Directory.GetDirectories(absDir);
            }
            catch (Exception e)
            {
                onError?.Invoke(assetDir, e);
                return;
            }
            Array.Sort(files, StringComparer.Ordinal);
            Array.Sort(dirs, StringComparer.Ordinal);
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                if (IsIgnoredName(name) || name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                onFile(assetDir + "/" + name);
            }
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (IsIgnoredName(name)) continue;
                var sub = assetDir + "/" + name;
                if (IsInTempFolder(sub)) continue;
                onFolder?.Invoke(sub);
                WalkAssetFolder(sub, d, onFile, onFolder, onError, depth + 1);
            }
        }

        // ------------------------------------------------------------------ file helpers

        static FileStream OpenRead(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);

        static string Hex(byte[] hash)
        {
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var s = OpenRead(path))
                return Hex(sha.ComputeHash(s));
        }

        /// <summary>Copies src to a new file dst while hashing what was read, so the stored hash always matches the stored bytes.</summary>
        static string CopyWithHash(string src, string dst, out long size)
        {
            size = 0;
            try
            {
                using (var sha = SHA256.Create())
                using (var input = OpenRead(src))
                using (var output = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize))
                {
                    var buffer = new byte[BufferSize];
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        sha.TransformBlock(buffer, 0, n, null, 0);
                        output.Write(buffer, 0, n);
                        size += n;
                    }
                    sha.TransformFinalBlock(buffer, 0, 0);
                    return Hex(sha.Hash);
                }
            }
            catch
            {
                TryDeleteFile(dst);
                throw;
            }
        }

        static void TryDeleteFile(string path)
        {
            try { if (path != null && File.Exists(path)) File.Delete(path); }
            catch (Exception) { /* best effort */ }
        }

        static string ReadGuidFromMeta(string metaAbs)
        {
            try
            {
                if (!File.Exists(metaAbs)) return null;
                using (var reader = new StreamReader(OpenRead(metaAbs), Encoding.UTF8))
                {
                    for (int i = 0; i < 16; i++)
                    {
                        var line = reader.ReadLine();
                        if (line == null) break;
                        if (line.StartsWith("guid:", StringComparison.Ordinal)) return line.Substring(5).Trim();
                    }
                }
            }
            catch (Exception) { /* unreadable meta: no GUID */ }
            return null;
        }

        /// <summary>Text-serialized Unity file (YAML header)?</summary>
        static bool IsYaml(string path)
        {
            try
            {
                using (var s = OpenRead(path))
                {
                    var head = new byte[5];
                    int n = s.Read(head, 0, 5);
                    return n == 5 && head[0] == (byte)'%' && head[1] == (byte)'Y' && head[2] == (byte)'A' && head[3] == (byte)'M' && head[4] == (byte)'L';
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>Line multiset difference (order-insensitive): cheap "how much changed" for YAML files, no text dump.</summary>
        static bool TryLineDiff(string before, string after, out int added, out int removed)
        {
            added = removed = 0;
            try
            {
                if (new FileInfo(before).Length > LineDiffMaxBytes || new FileInfo(after).Length > LineDiffMaxBytes) return false;
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var line in ReadLines(before)) counts[line] = (counts.TryGetValue(line, out var c) ? c : 0) + 1;
                foreach (var line in ReadLines(after)) counts[line] = (counts.TryGetValue(line, out var c) ? c : 0) - 1;
                foreach (var v in counts.Values)
                {
                    if (v > 0) removed += v;
                    else if (v < 0) added -= v;
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        static IEnumerable<string> ReadLines(string path)
        {
            using (var reader = new StreamReader(OpenRead(path), Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null) yield return line;
            }
        }

        static double Mb(long bytes) => Math.Round(bytes / (1024.0 * 1024.0), 2);

        static DateTimeOffset SortKey(string iso) =>
            DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : DateTimeOffset.MinValue;

        /// <summary>Short stored name "00012-Body.mat": keeps the extension, avoids deep paths (Windows MAX_PATH).</summary>
        static string StoredName(int index, string assetPath)
        {
            var file = assetPath.Substring(assetPath.LastIndexOf('/') + 1);
            var ext = Path.GetExtension(file);
            var stem = file.Substring(0, file.Length - ext.Length);
            var safeExt = ext.Length > 0 && ext.Length <= 24 ? "." + CctPaths.SafeName(ext.Substring(1), 23) : "";
            return index.ToString("D5", CultureInfo.InvariantCulture) + "-" + CctPaths.SafeName(stem, 48) + safeExt;
        }

        static string AsciiSlug(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder();
            foreach (var ch in text)
            {
                if (sb.Length >= max) break;
                if (ch < 128 && char.IsLetterOrDigit(ch)) sb.Append(ch);
                else if ((ch == '-' || ch == '_' || ch == ' ') && sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
            }
            return sb.ToString().Trim('-');
        }

        static string NewSnapshotId(string label, string suffix)
        {
            var slug = AsciiSlug(label, 24);
            var baseId = CctPaths.NewId() + (string.IsNullOrEmpty(suffix) ? "" : "-" + suffix) + (slug.Length > 0 ? "-" + slug : "");
            var id = baseId;
            for (int n = 2; Directory.Exists(SnapshotDir(id)); n++) id = baseId + "-" + n.ToString(CultureInfo.InvariantCulture);
            return id;
        }

        // ------------------------------------------------------------------ editor state helpers

        static Dictionary<string, Scene> LoadedScenesByPath()
        {
            var d = new Dictionary<string, Scene>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in CctTargets.LoadedScenes())
                if (!string.IsNullOrEmpty(s.path) && !d.ContainsKey(s.path)) d[s.path] = s;
            return d;
        }

        /// <summary>
        /// Editable assets that are loaded and have unsaved in-memory changes (their file does not contain them).
        /// Only already-loaded assets are inspected, so nothing new is loaded.
        /// </summary>
        static List<string> FindDirtyLoadedAssets(IEnumerable<string> assetPaths)
        {
            var dirty = new List<string>();
            int checkedCount = 0;
            foreach (var p in assetPaths)
            {
                if (p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) continue;
                if (!EditableExtensions.Contains(Path.GetExtension(p))) continue;
                if (++checkedCount > DirtyCheckMax) break;
                try
                {
                    if (!AssetDatabase.IsMainAssetAtPathLoaded(p)) continue;
                    var main = AssetDatabase.LoadMainAssetAtPath(p);
                    if (main != null && EditorUtility.IsDirty(main)) { dirty.Add(p); continue; }
                    foreach (var o in AssetDatabase.LoadAllAssetsAtPath(p))
                        if (o != null && EditorUtility.IsDirty(o)) { dirty.Add(p); break; }
                }
                catch (Exception) { /* unloadable asset: cannot be dirty in memory */ }
            }
            return dirty;
        }

        /// <summary>
        /// Asset path of the prefab open in Prefab Mode (null in the main stage) and whether it has unsaved changes.
        /// Stage.hasUnsavedChanges is internal, so it is read by reflection; if that fails we fall back to the stage
        /// scene's dirty flag, and if that is unavailable too we assume "unsaved" (the safe answer for restore).
        /// </summary>
        static string CurrentPrefabStage(out bool unsaved)
        {
            unsaved = false;
            Stage stage;
            try
            {
                stage = StageUtility.GetCurrentStage();
                if (stage == null || stage == StageUtility.GetMainStage()) return null;
            }
            catch (Exception) { return null; }
            var path = stage.assetPath;
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                var prop = typeof(Stage).GetProperty("hasUnsavedChanges", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.PropertyType == typeof(bool))
                {
                    unsaved = (bool)prop.GetValue(stage, null);
                    return path;
                }
            }
            catch (Exception) { /* fall through */ }
            if (stage is PreviewSceneStage ps && ps.scene.IsValid())
            {
                unsaved = ps.scene.isDirty;
                return path;
            }
            unsaved = true;
            return path;
        }

        sealed class Progress : IDisposable
        {
            readonly string _title;
            readonly int _total;
            readonly bool _show;
            int _lastPercent = -1;

            public Progress(string title, int total)
            {
                _title = title;
                _total = Math.Max(1, total);
                _show = total >= 20 && !Application.isBatchMode;
            }

            public void Step(int index, string info)
            {
                if (!_show) return;
                int pct = (int)(100L * index / _total);
                if (pct == _lastPercent) return;
                _lastPercent = pct;
                EditorUtility.DisplayProgressBar(_title, info ?? "", Mathf.Clamp01(index / (float)_total));
            }

            public void Dispose()
            {
                if (_show) EditorUtility.ClearProgressBar();
            }
        }

        static void CopyIssues(CctResult from, CctResult to)
        {
            if (from == null || to == null || ReferenceEquals(from, to)) return;
            foreach (var i in from.Issues) to.Issues.Add(i);
        }

        static List<object> Cap(IEnumerable<object> items, int max, out int omitted)
        {
            var all = items.ToList();
            omitted = Math.Max(0, all.Count - max);
            return all.Take(max).ToList();
        }

        /// <summary>
        /// Puts the first <paramref name="max"/> items under <paramref name="key"/> (the summary response keeps it). When the
        /// list is longer, adds "&lt;key&gt;_omitted" and keeps the complete list in <paramref name="full"/>, which goes to the
        /// report only (data key full_lists), so the response stays small and nothing is lost.
        /// </summary>
        static void PutList(CctResult r, string key, IEnumerable<object> items, int max, Dictionary<string, object> full)
        {
            var all = items.ToList();
            r.Put(key, all.Take(max).ToList());
            if (all.Count <= max) return;
            r.Put(key + "_omitted", all.Count - max);
            full[key] = all.Take(FullListMax).ToList();
        }

        static void PutFullLists(CctResult r, Dictionary<string, object> full)
        {
            if (full.Count > 0) r.Put("full_lists", full);
        }

        // ------------------------------------------------------------------ VRChat Blueprint IDs

        /// <summary>
        /// VRChat Blueprint IDs in a text-serialized scene/prefab: VRC.Core.PipelineManager's "blueprintId: ..." lines and
        /// prefab-instance overrides ("propertyPath: blueprintId" + "value: ..."). Read as text, so the SDK is not needed and
        /// nothing is loaded. "" stands for an empty ID. Null when the file is not a YAML .unity/.prefab or is too big.
        /// </summary>
        static List<string> BlueprintIds(string abs, string assetPath)
        {
            var ext = Path.GetExtension(assetPath ?? "");
            if (!string.Equals(ext, ".unity", StringComparison.OrdinalIgnoreCase) && !string.Equals(ext, ".prefab", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                if (abs == null || !File.Exists(abs) || new FileInfo(abs).Length > LineDiffMaxBytes || !IsYaml(abs)) return null;
                var ids = new SortedSet<string>(StringComparer.Ordinal);
                bool overrideValueNext = false;
                foreach (var raw in ReadLines(abs))
                {
                    var line = raw.Trim();
                    if (overrideValueNext)
                    {
                        overrideValueNext = false;
                        if (line.StartsWith("value:", StringComparison.Ordinal)) ids.Add(line.Substring(6).Trim());
                        continue;
                    }
                    if (line.StartsWith("blueprintId:", StringComparison.Ordinal)) ids.Add(line.Substring(12).Trim());
                    else if (line == "propertyPath: blueprintId") overrideValueNext = true;
                }
                return ids.ToList();
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Compares the Blueprint IDs of the stored copy and the current file; when they differ, adds blueprint_ids_now /
        /// blueprint_ids_snapshot to <paramref name="info"/> and returns a ready Japanese message, else null.
        /// </summary>
        static string CompareBlueprintIds(string storedAbs, string currentAbs, string assetPath, Dictionary<string, object> info, bool forRestore)
        {
            var snap = BlueprintIds(storedAbs, assetPath);
            var now = BlueprintIds(currentAbs, assetPath);
            if (snap == null || now == null || snap.SequenceEqual(now)) return null;
            if (snap.Count == 0 || now.Count == 0) return null; // no PipelineManager on one side: nothing to keep
            info["blueprint_ids_now"] = now.Cast<object>().ToList();
            info["blueprint_ids_snapshot"] = snap.Cast<object>().ToList();
            return forRestore
                ? $"'{assetPath}' を復元すると VRChat の Blueprint ID が {FormatIds(now)} から {FormatIds(snap)} に戻り、次のアップロード先が変わります。" +
                  $"復元後に PipelineManager の Blueprint ID を {FormatIds(now)} に設定し直すか、assets でこのファイルを除いて復元してください"
                : $"'{assetPath}' の VRChat Blueprint ID がスナップショット ({FormatIds(snap)}) と現在 ({FormatIds(now)}) で異なります。restore するとスナップショット側に戻ります";
        }

        static string FormatIds(List<string> ids) => string.Join(", ", ids.Select(x => x.Length == 0 ? "(空)" : x));
    }
}
