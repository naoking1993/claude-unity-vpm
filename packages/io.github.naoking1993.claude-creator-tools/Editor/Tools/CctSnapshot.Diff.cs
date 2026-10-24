using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace Naoking.ClaudeCreatorTools
{
    public static partial class CctSnapshot
    {
        static CctResult Diff(CctArgs args)
        {
            var id = args.GetString("id");
            var m = LoadManifest(id, out var dir, out var failure);
            if (m == null) return failure;
            int maxEntries = args.GetInt("max_entries", 200, 1, 5000);

            var r = CctResult.Ok("");
            r.Put("action", "diff");
            r.Put("id", m.Id);
            r.Put("label", m.Label);
            r.Put("created_at", m.CreatedAt);
            r.Put("snapshot_dir", dir);
            WarnProjectRoot(m, r);

            var counts = new Dictionary<string, object>
            {
                ["unchanged"] = 0, ["modified"] = 0, ["deleted"] = 0, ["meta_changed"] = 0, ["error"] = 0,
            };
            var changes = new List<object>();
            int omitted = 0;
            var scenes = LoadedScenesByPath();
            using (var progress = new Progress("Claude Creator Tools: スナップショット差分", m.Files.Count))
            {
                int i = 0;
                foreach (var e in m.Files)
                {
                    progress.Step(++i, e.AssetPath);
                    var info = CompareEntry(dir, e);
                    var status = (string)info["status"];
                    var bucket = counts.ContainsKey(status) ? status : "error";
                    counts[bucket] = (int)counts[bucket] + 1;
                    if (e.SceneState != null) info["scene_was_dirty_at_snapshot"] = e.SceneWasDirty;
                    if (scenes.TryGetValue(e.AssetPath, out var scene) && scene.isDirty)
                    {
                        info["scene_has_unsaved_changes"] = true;
                        r.AddIssue(CctSeverity.Warning, "SCENE_HAS_UNSAVED_CHANGES",
                            $"シーン '{e.AssetPath}' には未保存の変更があり、その内容はディスクに無いため比較できません (比較するには保存が必要です)", asset: e.AssetPath);
                    }
                    if (status == "unchanged" && !info.ContainsKey("scene_has_unsaved_changes")) continue;
                    if (changes.Count < maxEntries) changes.Add(info);
                    else omitted++;
                }
            }

            var added = FindAddedFiles(m, maxEntries, out var addedCount);
            counts["added"] = addedCount;
            if (addedCount > 0)
                r.AddIssue(CctSeverity.Info, "FILES_ADDED",
                    $"スナップショット後に、対象フォルダへ {addedCount} 個のファイルが追加されています (restore はこれらを削除しません)", value: addedCount);

            foreach (var p in FindDirtyLoadedAssets(m.Files.Select(f => f.AssetPath)).Take(30))
                r.AddIssue(CctSeverity.Warning, "ASSET_HAS_UNSAVED_CHANGES",
                    $"'{p}' はメモリ上に未保存の変更があります。差分はディスク上のファイル同士の比較です", asset: p);
            var stagePath = CurrentPrefabStage(out var stageUnsaved);
            if (stagePath != null && stageUnsaved && m.Files.Any(f => string.Equals(f.AssetPath, stagePath, StringComparison.OrdinalIgnoreCase)))
                r.AddIssue(CctSeverity.Warning, "PREFAB_STAGE_UNSAVED",
                    $"'{stagePath}' は Prefab モードで未保存の変更があります。差分はディスク上の内容の比較です", asset: stagePath);

            r.Put("counts", counts);
            r.Put("changes", changes);
            if (omitted > 0) r.Put("changes_omitted", omitted);
            r.Put("added", added);
            if (addedCount > added.Count) r.Put("added_omitted", addedCount - added.Count);
            r.Message = $"'{m.Id}' との差分: 変更 {counts["modified"]} / 削除 {counts["deleted"]} / meta 変更 {counts["meta_changed"]} / " +
                        $"変更なし {counts["unchanged"]} / 追加 {addedCount}" + ((int)counts["error"] > 0 ? $" / 比較不能 {counts["error"]}" : "");
            return r;
        }

        /// <summary>Compares one manifest entry with the project file: unchanged | modified | deleted | meta_changed.</summary>
        static Dictionary<string, object> CompareEntry(string dir, FileEntry e)
        {
            var d = new Dictionary<string, object> { ["asset_path"] = e.AssetPath };
            if (!TryNormalizeAssetPath(e.AssetPath, out var path, out _))
            {
                d["status"] = "invalid_path";
                return d;
            }
            var abs = AbsOf(path);
            if (!File.Exists(abs))
            {
                d["status"] = "deleted";
                var owner = string.IsNullOrEmpty(e.Guid) ? null : AssetDatabase.GUIDToAssetPath(e.Guid);
                if (!string.IsNullOrEmpty(owner) && !string.Equals(owner, path, StringComparison.OrdinalIgnoreCase) && File.Exists(AbsOf(owner)))
                    d["moved_to"] = owner;
                return d;
            }

            string sha, metaSha;
            var metaAbs = abs + ".meta";
            try
            {
                sha = Sha256File(abs);
                metaSha = File.Exists(metaAbs) ? Sha256File(metaAbs) : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                d["status"] = "unreadable";
                d["error"] = ex.Message;
                return d;
            }

            bool contentChanged = !string.Equals(sha, e.Sha256, StringComparison.OrdinalIgnoreCase);
            // A snapshot taken before Unity wrote the .meta has none; then the current .meta is not a change.
            bool metaChanged = e.MetaSha256 != null && !string.Equals(metaSha, e.MetaSha256, StringComparison.OrdinalIgnoreCase);
            d["status"] = contentChanged ? "modified" : metaChanged ? "meta_changed" : "unchanged";

            if (contentChanged)
            {
                var stored = ResolveStored(dir, e.Stored);
                d["size_snapshot"] = e.Size;
                try { d["size_now"] = new FileInfo(abs).Length; }
                catch (Exception) { /* size is informational */ }
                if (stored != null && File.Exists(stored))
                {
                    d["snapshot_file"] = stored;
                    if (IsYaml(stored) && IsYaml(abs))
                    {
                        if (TryLineDiff(stored, abs, out var plus, out var minus))
                        {
                            d["lines_added"] = plus;
                            d["lines_removed"] = minus;
                        }
                        else d["text_diff"] = "too_large";
                    }
                    else d["text_diff"] = "binary";
                }
                else d["snapshot_file_missing"] = true;
            }

            if (metaChanged)
            {
                d["meta_changed"] = true;
                if (metaSha == null) d["meta_missing"] = true;
                else
                {
                    var guidNow = ReadGuidFromMeta(metaAbs);
                    if (!string.IsNullOrEmpty(e.Guid) && guidNow != null && !string.Equals(guidNow, e.Guid, StringComparison.OrdinalIgnoreCase))
                    {
                        d["guid_changed"] = true;
                        d["guid_snapshot"] = e.Guid;
                        d["guid_now"] = guidNow;
                    }
                    var storedMeta = ResolveStored(dir, e.MetaStored);
                    if (storedMeta != null && File.Exists(storedMeta) && TryLineDiff(storedMeta, metaAbs, out var plus, out var minus))
                    {
                        d["meta_lines_added"] = plus;
                        d["meta_lines_removed"] = minus;
                    }
                }
            }
            return d;
        }

        /// <summary>Files that now exist under the snapshot's folder roots but are not in the manifest.</summary>
        static List<object> FindAddedFiles(Manifest m, int max, out int count)
        {
            var known = new HashSet<string>(m.Files.Select(f => f.AssetPath), StringComparer.OrdinalIgnoreCase);
            foreach (var s in m.Skipped)
                if (s.TryGetValue("asset_path", out var p) && p is string ps) known.Add(ps);
            var added = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var root in m.FolderRoots)
            {
                if (!TryNormalizeAssetPath(root, out var path, out _)) continue;
                var abs = AbsOf(path);
                if (!Directory.Exists(abs)) continue;
                WalkAssetFolder(path, abs, file =>
                {
                    if (known.Contains(file) || !seen.Add(file)) return;
                    n++;
                    if (added.Count < max) added.Add(file);
                }, null, null);
            }
            count = n;
            return added;
        }

        static void WarnProjectRoot(Manifest m, CctResult r)
        {
            if (!string.IsNullOrEmpty(m.ProjectRoot) && !string.Equals(CctPaths.Normalize(m.ProjectRoot), CctPaths.ProjectRoot, StringComparison.OrdinalIgnoreCase))
                r.AddIssue(CctSeverity.Warning, "PROJECT_ROOT_DIFFERS",
                    $"このスナップショットは別のプロジェクトフォルダ '{m.ProjectRoot}' で作成されました。パスは Assets/ からの相対で扱います", value: m.ProjectRoot);
        }
    }
}
