using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Naoking.ClaudeCreatorTools
{
    public static partial class CctSnapshot
    {
        const int PerKindIssueCap = 50;

        sealed class PlanItem
        {
            public FileEntry Entry;
            public string Path, TargetAbs, StoredAbs, StoredMetaAbs;
            public string Action; // overwrite | recreate | meta | reload | reimport | unchanged | skip
            public bool WriteFile, WriteMeta, IsLoadedScene, SceneDirty, AssetDirty;
            public string SkipCode, SkipMessage, GuidNow;
            public bool Wrote, Failed;

            /// <summary>Actions that change something (files, imports or open scenes).</summary>
            public bool Actionable => Action == "overwrite" || Action == "recreate" || Action == "meta" || Action == "reload" || Action == "reimport";

            /// <summary>Open scenes touched on disk (or reloaded) are unloaded first, so Unity never shows its modal "changed on disk" prompt.</summary>
            public bool NeedsSceneClose => IsLoadedScene && (Action == "overwrite" || Action == "recreate" || Action == "meta" || Action == "reload");
        }

        sealed class SceneArrangement
        {
            public readonly List<string> ClosedPaths = new List<string>();
            public string ActivePath;
            public Scene Placeholder;
            public bool HasPlaceholder;
        }

        static CctResult Restore(CctArgs args)
        {
            var id = args.GetString("id");
            var m = LoadManifest(id, out var dir, out var failure);
            if (m == null) return failure;
            bool confirm = args.GetBool("confirm", false);
            int maxEntries = args.GetInt("max_entries", 200, 1, 5000);
            bool busy = EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating;
            if (confirm && busy)
                return CctResult.Fail("Play モード中・コンパイル中・インポート中は復元できません。Edit モードで処理が終わってから再実行してください", "EDITOR_BUSY",
                    new Dictionary<string, object>
                    {
                        ["is_playing"] = EditorApplication.isPlayingOrWillChangePlaymode,
                        ["is_compiling"] = EditorApplication.isCompiling,
                        ["is_updating"] = EditorApplication.isUpdating,
                    });

            var r = CctResult.Ok("");
            r.Put("action", "restore");
            r.Put("id", m.Id);
            r.Put("label", m.Label);
            r.Put("created_at", m.CreatedAt);
            WarnProjectRoot(m, r);
            if (busy)
                r.AddIssue(CctSeverity.Warning, "EDITOR_BUSY", "現在 Play モード中・コンパイル中・インポート中のため、confirm=true の復元は拒否されます");

            // ---- select entries
            var selected = SelectEntries(m, args.GetStringList("assets"), r);
            if (selected.Count == 0)
            {
                var f = CctResult.Fail("復元対象がスナップショット内にありません", "NOT_IN_SNAPSHOT");
                CopyIssues(r, f);
                return f;
            }

            // ---- plan
            var scenes = LoadedScenesByPath();
            var dirtyAssets = new HashSet<string>(FindDirtyLoadedAssets(selected.Select(e => e.AssetPath)), StringComparer.OrdinalIgnoreCase);
            var plan = selected.Select(e => PlanEntry(dir, e, scenes, dirtyAssets, r)).ToList();
            var actionable = plan.Where(it => it.Actionable).ToList();
            var foldersToCreate = MissingFolders(actionable.Where(it => it.Action == "recreate").Select(it => it.Path));

            foreach (var it in actionable.Where(it => it.AssetDirty).Take(30))
                r.AddIssue(CctSeverity.Warning, "ASSET_HAS_UNSAVED_CHANGES",
                    $"'{it.Path}' はメモリ上に未保存の変更があります。復元するとこの変更は破棄され、復元前スナップショットにも含まれません", asset: it.Path);
            // A dirty Prefab Mode stage shows a modal dialog when its prefab changes on disk, and an in-context stage may
            // be closed (with a save prompt) when its scene is unloaded; both would block MCP, so refuse up front.
            var stagePath = CurrentPrefabStage(out var stageUnsaved);
            bool stageBlocked = stagePath != null && stageUnsaved &&
                                (actionable.Any(it => string.Equals(it.Path, stagePath, StringComparison.OrdinalIgnoreCase)) ||
                                 actionable.Any(it => it.NeedsSceneClose));
            if (stageBlocked)
                r.AddIssue(CctSeverity.Error, "PREFAB_STAGE_UNSAVED",
                    $"'{stagePath}' が Prefab モードで未保存の変更を持っています。Unity の確認ダイアログで処理が止まるため復元できません。Prefab モードで保存するか閉じてから再実行してください", asset: stagePath);
            FindAddedFiles(m, 0, out var addedCount);
            if (addedCount > 0)
                r.AddIssue(CctSeverity.Info, "FILES_ADDED",
                    $"スナップショット後に対象フォルダへ追加された {addedCount} 個のファイルは、restore では削除しません (diff で一覧できます)", value: addedCount);

            r.Put("snapshot_dir", dir);
            r.Put("unchanged_count", plan.Count(it => it.Action == "unchanged"));
            r.Put("skipped", Cap(plan.Where(it => it.Action == "skip").Select(it => (object)new Dictionary<string, object>
            {
                ["asset_path"] = it.Entry.AssetPath,
                ["code"] = it.SkipCode,
                ["reason"] = it.SkipMessage,
            }), maxEntries, out var skippedOmitted));
            if (skippedOmitted > 0) r.Put("skipped_omitted", skippedOmitted);

            if (!confirm)
            {
                r.Put("dry_run", true);
                r.Put("would_overwrite", Paths(actionable, "overwrite", maxEntries));
                r.Put("would_recreate", Paths(actionable, "recreate", maxEntries));
                r.Put("would_restore_meta", Paths(actionable, "meta", maxEntries));
                r.Put("would_reimport", Paths(actionable, "reimport", maxEntries));
                r.Put("would_reload_scenes", actionable.Where(it => it.NeedsSceneClose).Select(it => (object)new Dictionary<string, object>
                {
                    ["asset_path"] = it.Path,
                    ["unsaved_changes_discarded"] = it.SceneDirty,
                }).ToList());
                r.Put("would_create_folders", foldersToCreate.Cast<object>().ToList());
                r.AddIssue(CctSeverity.Warning, "CONFIRM_REQUIRED",
                    "確認のための試行です。何も変更していません。上の内容で復元するには confirm=true を付けて再実行してください (復元前の状態は自動でスナップショットされます)", value: m.Id);
                r.Message = $"[dry run] '{m.Id}' から復元すると: 上書き {Count(actionable, "overwrite")} / 再作成 {Count(actionable, "recreate")} / " +
                            $"meta {Count(actionable, "meta")} / シーン再読込 {actionable.Count(it => it.NeedsSceneClose)} / 再インポート {Count(actionable, "reimport")} / " +
                            $"変更なし {plan.Count(it => it.Action == "unchanged")} / スキップ {plan.Count(it => it.Action == "skip")} (未実行)";
                return r;
            }

            if (stageBlocked)
            {
                var f = CctResult.Fail($"'{stagePath}' が Prefab モードで未保存のため復元を中止しました (何も変更していません)", "PREFAB_STAGE_UNSAVED");
                CopyIssues(r, f);
                return f;
            }
            r.Put("dry_run", false);
            if (actionable.Count == 0)
            {
                r.Put("pre_restore_snapshot_id", null);
                r.Message = plan.Any(it => it.Action == "skip")
                    ? $"'{m.Id}': 復元できる対象がありません (スキップ {plan.Count(it => it.Action == "skip")} 件。issues を確認してください)"
                    : $"'{m.Id}': 現在の状態はスナップショットと同じです。何も変更していません";
                return r;
            }

            // ---- 1. back up the current state of everything that will be touched
            string preId = null;
            var preSpecs = actionable.Where(it => it.Action != "recreate").Select(it => it.Path).ToList();
            if (preSpecs.Count > 0)
            {
                var preReq = new CaptureRequest
                {
                    Label = "pre-restore-" + m.Id,
                    Kind = "pre-restore",
                    RestoreOf = m.Id,
                    Specs = preSpecs,
                    IncludeOpenScenes = false,
                    IncludeDependencies = false,
                    MaxTotalMb = double.MaxValue,
                    PreSkipped = actionable.Where(it => it.Action == "recreate").Select(it => new Dictionary<string, object>
                    {
                        ["asset_path"] = it.Path,
                        ["code"] = "MISSING_BEFORE_RESTORE",
                        ["reason"] = "file did not exist before the restore recreated it (undo = delete it)",
                    }).ToList(),
                };
                var preResult = CctResult.Ok("");
                var pre = Capture(preReq, preResult, out _, out var preFailure);
                if (pre == null || pre.Incomplete)
                {
                    var f = CctResult.Fail("復元前の現状スナップショットを作成できなかったため、復元を中止しました (何も変更していません)" +
                                           (preFailure != null ? ": " + preFailure.Message : ""), "PRE_RESTORE_FAILED",
                        new Dictionary<string, object> { ["pre_restore_snapshot_id"] = pre?.Id });
                    CopyIssues(r, f);
                    CopyIssues(preResult, f);
                    if (preFailure != null) CopyIssues(preFailure, f);
                    return f;
                }
                preId = pre.Id;
                foreach (var issue in preResult.Issues)
                {
                    // Unsaved-asset warnings were already reported for the restore itself.
                    var code = issue.TryGetValue("code", out var c) ? c as string : null;
                    if (code != "ASSET_HAS_UNSAVED_CHANGES" && code != "PREFAB_STAGE_UNSAVED") r.Issues.Add(issue);
                }
                r.AddIssue(CctSeverity.Info, "PRE_RESTORE_SNAPSHOT",
                    $"復元前の状態をスナップショット '{preId}' に保存しました (この復元を取り消すには restore id={preId})", value: preId);
            }
            else
            {
                r.AddIssue(CctSeverity.Info, "PRE_RESTORE_SNAPSHOT", "復元対象はすべて現在存在しないファイルのため、復元前スナップショットは作成していません");
            }
            r.Put("pre_restore_snapshot_id", preId);

            // ---- 2. unload affected open scenes, write, import, reopen
            var arrangement = new SceneArrangement();
            var reloaded = new List<object>();
            var createdFolders = new List<string>();
            bool aborted = false;
            try
            {
                var scenePaths = new HashSet<string>(actionable.Where(it => it.NeedsSceneClose).Select(it => it.Path), StringComparer.OrdinalIgnoreCase);
                if (!CloseScenesForRestore(scenePaths, arrangement, r)) aborted = true;
                else
                {
                    CreateFolders(foldersToCreate, m, dir, createdFolders, r);
                    using (var progress = new Progress("Claude Creator Tools: スナップショット復元", actionable.Count))
                    {
                        int i = 0;
                        foreach (var it in actionable)
                        {
                            progress.Step(++i, it.Path);
                            WriteItem(it, r);
                        }
                    }
                    ImportRestored(createdFolders, actionable, r);
                }
            }
            finally
            {
                ReopenScenes(arrangement, r, reloaded);
            }
            if (aborted)
            {
                var f = CctResult.Fail("開いているシーンを一時的に閉じられなかったため、復元を中止しました (ファイルは変更していません)", "SCENE_CLOSE_FAILED",
                    new Dictionary<string, object> { ["pre_restore_snapshot_id"] = preId, ["reloaded_scenes"] = reloaded });
                CopyIssues(r, f);
                return f;
            }

            // ---- 3. verify and report
            foreach (var it in actionable.Where(it => it.Wrote && it.WriteMeta && !string.IsNullOrEmpty(it.Entry.Guid)))
            {
                var guidNow = AssetDatabase.AssetPathToGUID(it.Path);
                if (!string.Equals(guidNow, it.Entry.Guid, StringComparison.OrdinalIgnoreCase))
                    r.AddIssue(CctSeverity.Warning, "GUID_MISMATCH_AFTER_RESTORE",
                        $"'{it.Path}' の GUID が復元後 {guidNow} です (スナップショットは {it.Entry.Guid})。GUID の衝突で Unity が振り直した可能性があります", asset: it.Path, value: guidNow);
            }
            var imported = actionable.Where(it => it.Wrote || (it.Action == "reimport" && !it.Failed)).Select(it => it.Path).ToList();
            foreach (var p in FindDirtyLoadedAssets(imported).Take(30))
                r.AddIssue(CctSeverity.Warning, "ASSET_STILL_DIRTY",
                    $"'{p}' は再インポート後もメモリ上に未保存の変更が残っています。保存 (Ctrl+S / SaveAssets) すると復元内容が上書きされます。Unity を再起動するか該当アセットを再インポートしてください", asset: p);

            var restored = Done(actionable, "overwrite");
            var recreated = Done(actionable, "recreate");
            var metaRestored = Done(actionable, "meta");
            var reimported = Done(actionable, "reimport");
            var failed = actionable.Where(it => it.Failed).Select(it => (object)it.Path).ToList();
            AddPerFileIssues(r, restored, "FILE_RESTORED", "スナップショットの内容で上書きしました");
            AddPerFileIssues(r, recreated, "FILE_RECREATED", "削除されていたファイルを再作成しました (GUID も復元)");
            AddPerFileIssues(r, metaRestored, "META_RESTORED", ".meta (GUID / インポート設定) を復元しました");
            AddPerFileIssues(r, reimported, "ASSET_REIMPORTED", "未保存のメモリ上の変更を破棄するため再インポートしました");

            r.Put("restored", Cap(restored, maxEntries, out var o1));
            r.Put("recreated", Cap(recreated, maxEntries, out var o2));
            r.Put("meta_restored", Cap(metaRestored, maxEntries, out var o3));
            r.Put("reimported", Cap(reimported, maxEntries, out var o4));
            r.Put("reloaded_scenes", reloaded);
            r.Put("created_folders", createdFolders.Cast<object>().ToList());
            r.Put("failed", failed);
            if (o1 + o2 + o3 + o4 > 0) r.Put("lists_omitted", o1 + o2 + o3 + o4);

            int succeeded = restored.Count + recreated.Count + metaRestored.Count + reimported.Count + reloaded.Count;
            var summary = $"上書き {restored.Count} / 再作成 {recreated.Count} / meta {metaRestored.Count} / シーン再読込 {reloaded.Count} / 再インポート {reimported.Count}" +
                          (failed.Count > 0 ? $" / 失敗 {failed.Count}" : "") + (preId != null ? $" (復元前スナップショット: {preId})" : "");
            if (succeeded == 0 && failed.Count > 0)
            {
                var f = CctResult.Fail($"'{m.Id}' からの復元はすべて失敗しました: {summary}", "RESTORE_FAILED",
                    new Dictionary<string, object> { ["failed"] = failed, ["pre_restore_snapshot_id"] = preId });
                CopyIssues(r, f);
                return f;
            }
            r.Message = $"'{m.Id}' から復元しました: {summary}";
            return r;
        }

        static List<FileEntry> SelectEntries(Manifest m, List<string> subset, CctResult r)
        {
            if (subset.Count == 0) return m.Files;
            var selected = new List<FileEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in subset)
            {
                if (!TryNormalizeAssetPath(raw, out var spec, out var code))
                {
                    r.AddIssue(CctSeverity.Error, code == "SKIPPED_PACKAGE" ? "SKIPPED_PACKAGE" : code == "NOT_ASSET_PATH" ? "NOT_ASSET_PATH" : "PATH_OUTSIDE_PROJECT",
                        $"復元対象に指定できないパスです: {raw}", value: raw);
                    continue;
                }
                var matches = m.Files.Where(f => string.Equals(f.AssetPath, spec, StringComparison.OrdinalIgnoreCase) ||
                                                 (f.AssetPath ?? "").StartsWith(spec + "/", StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0)
                {
                    r.AddIssue(CctSeverity.Warning, "NOT_IN_SNAPSHOT", $"'{spec}' はこのスナップショットに含まれていません", asset: spec);
                    continue;
                }
                foreach (var e in matches)
                    if (seen.Add(e.AssetPath)) selected.Add(e);
            }
            return selected;
        }

        static PlanItem PlanEntry(string dir, FileEntry e, Dictionary<string, Scene> scenes, HashSet<string> dirtyAssets, CctResult r)
        {
            var it = new PlanItem { Entry = e };
            if (!TryNormalizeAssetPath(e.AssetPath, out var path, out _))
                return Skip(it, "PATH_OUTSIDE_PROJECT", $"manifest のパスが不正です: {e.AssetPath}", r, CctSeverity.Error);
            it.Path = path;
            it.TargetAbs = AbsOf(path);
            it.StoredAbs = ResolveStored(dir, e.Stored);
            it.StoredMetaAbs = ResolveStored(dir, e.MetaStored);
            if (scenes.TryGetValue(path, out var scene))
            {
                it.IsLoadedScene = true;
                it.SceneDirty = scene.isDirty;
            }
            it.AssetDirty = dirtyAssets.Contains(path);
            if (Directory.Exists(it.TargetAbs))
                return Skip(it, "RESTORE_FAILED", $"'{path}' には現在フォルダがあるため復元できません", r, CctSeverity.Error);

            var metaAbs = it.TargetAbs + ".meta";
            bool exists = File.Exists(it.TargetAbs);
            if (!exists)
            {
                it.Action = "recreate";
                it.WriteFile = true;
                it.WriteMeta = e.MetaStored != null;
            }
            else
            {
                string sha, metaSha;
                try
                {
                    sha = Sha256File(it.TargetAbs);
                    metaSha = File.Exists(metaAbs) ? Sha256File(metaAbs) : null;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return Skip(it, "RESTORE_FAILED", $"'{path}' を読めません: {ex.Message}", r, CctSeverity.Error);
                }
                it.WriteFile = !string.Equals(sha, e.Sha256, StringComparison.OrdinalIgnoreCase);
                it.WriteMeta = e.MetaSha256 != null && !string.Equals(metaSha, e.MetaSha256, StringComparison.OrdinalIgnoreCase);
                if (it.WriteMeta && metaSha != null) it.GuidNow = ReadGuidFromMeta(metaAbs);
                it.Action = it.WriteFile ? "overwrite" : it.WriteMeta ? "meta" :
                    it.IsLoadedScene && it.SceneDirty ? "reload" : it.AssetDirty ? "reimport" : "unchanged";
            }

            if (it.WriteFile && !StoredMatches(it.StoredAbs, e.Sha256))
                return Skip(it, "SNAPSHOT_CORRUPT", $"'{path}' の保存コピーが見つからないか内容が manifest と一致しません", r, CctSeverity.Error);
            if (it.WriteMeta && !StoredMatches(it.StoredMetaAbs, e.MetaSha256))
                return Skip(it, "SNAPSHOT_CORRUPT", $"'{path}.meta' の保存コピーが見つからないか内容が manifest と一致しません", r, CctSeverity.Error);
            if ((it.WriteFile && exists && IsReadOnly(it.TargetAbs)) || (it.WriteMeta && File.Exists(metaAbs) && IsReadOnly(metaAbs)))
                return Skip(it, "FILE_READ_ONLY", $"'{path}' (または .meta) が読み取り専用のため上書きしません (バージョン管理のロック等)", r, CctSeverity.Error);

            if (it.WriteMeta && !string.IsNullOrEmpty(e.Guid))
            {
                var owner = AssetDatabase.GUIDToAssetPath(e.Guid);
                if (!string.IsNullOrEmpty(owner) && !string.Equals(owner, path, StringComparison.OrdinalIgnoreCase) &&
                    (owner.StartsWith("Packages/", StringComparison.Ordinal) || File.Exists(AbsOf(owner)) || Directory.Exists(AbsOf(owner))))
                    r.AddIssue(CctSeverity.Warning, "GUID_CONFLICT",
                        $"'{path}' の GUID {e.Guid} は現在 '{owner}' が使っています。復元すると Unity がどちらかの GUID を振り直します (移動済みなら移動先を確認してください)",
                        asset: path, value: owner);
            }
            return it;
        }

        static PlanItem Skip(PlanItem it, string code, string message, CctResult r, string severity)
        {
            it.Action = "skip";
            it.SkipCode = code;
            it.SkipMessage = message;
            r.AddIssue(severity, code, message, asset: it.Path ?? it.Entry.AssetPath);
            return it;
        }

        static bool StoredMatches(string storedAbs, string expectedSha)
        {
            if (storedAbs == null || expectedSha == null || !File.Exists(storedAbs)) return false;
            try { return string.Equals(Sha256File(storedAbs), expectedSha, StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        static bool IsReadOnly(string abs)
        {
            try { return (File.GetAttributes(abs) & FileAttributes.ReadOnly) != 0; }
            catch (Exception) { return false; }
        }

        /// <summary>Missing parent folders of recreated files, parents first ("Assets" itself always exists).</summary>
        static List<string> MissingFolders(IEnumerable<string> recreatePaths)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in recreatePaths)
            {
                for (var folder = Parent(p); folder != null && folder.Length > "Assets".Length; folder = Parent(folder))
                {
                    if (Directory.Exists(AbsOf(folder))) break;
                    set.Add(folder);
                }
            }
            return set.OrderBy(f => f.Count(ch => ch == '/')).ThenBy(f => f, StringComparer.Ordinal).ToList();
        }

        static string Parent(string assetPath)
        {
            int i = assetPath.LastIndexOf('/');
            return i > 0 ? assetPath.Substring(0, i) : null;
        }

        static void CreateFolders(List<string> folders, Manifest m, string dir, List<string> created, CctResult r)
        {
            foreach (var folder in folders)
            {
                var abs = AbsOf(folder);
                try
                {
                    Directory.CreateDirectory(abs);
                    created.Add(folder);
                    // Bring the folder's own GUID back too, when the snapshot has it.
                    var fe = m.Folders.FirstOrDefault(f => string.Equals(f.AssetPath, folder, StringComparison.OrdinalIgnoreCase));
                    var metaAbs = abs + ".meta";
                    if (fe == null || File.Exists(metaAbs)) continue;
                    var stored = ResolveStored(dir, fe.MetaStored);
                    if (StoredMatches(stored, fe.MetaSha256)) CopyOver(stored, metaAbs, fe.MetaSha256);
                }
                catch (Exception e)
                {
                    r.AddIssue(CctSeverity.Error, "RESTORE_FAILED", $"フォルダ '{folder}' を作成できませんでした: {e.Message}", asset: folder);
                }
            }
        }

        static void WriteItem(PlanItem it, CctResult r)
        {
            if (!it.WriteFile && !it.WriteMeta) return; // reload / reimport only
            try
            {
                if (it.WriteMeta)
                {
                    CopyOver(it.StoredMetaAbs, it.TargetAbs + ".meta", it.Entry.MetaSha256);
                    it.Wrote = true;
                }
                if (it.WriteFile)
                {
                    CopyOver(it.StoredAbs, it.TargetAbs, it.Entry.Sha256);
                    it.Wrote = true;
                }
            }
            catch (InvalidDataException e)
            {
                it.Failed = true;
                r.AddIssue(CctSeverity.Error, "RESTORE_VERIFY_FAILED", $"'{it.Path}' の書き込み後の内容がスナップショットと一致しません: {e.Message}", asset: it.Path);
            }
            catch (Exception e)
            {
                it.Failed = true;
                r.AddIssue(CctSeverity.Error, "RESTORE_FAILED", $"'{it.Path}' を書き込めませんでした: {e.Message}", asset: it.Path);
            }
        }

        /// <summary>Overwrites dst with src, bumps its timestamp (so VCS and Unity see a fresh change), then verifies the bytes.</summary>
        static void CopyOver(string src, string dst, string expectedSha)
        {
            var parent = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Copy(src, dst, true);
            try { File.SetLastWriteTimeUtc(dst, DateTime.UtcNow); }
            catch (Exception) { /* timestamp is cosmetic; the import below is forced */ }
            var sha = Sha256File(dst);
            if (!string.Equals(sha, expectedSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"sha256 {sha} != {expectedSha}");
        }

        static void ImportRestored(List<string> createdFolders, List<PlanItem> items, CctResult r)
        {
            foreach (var folder in createdFolders)
            {
                try { AssetDatabase.ImportAsset(folder, ImportAssetOptions.Default); }
                catch (Exception e) { r.AddIssue(CctSeverity.Warning, "RESTORE_FAILED", $"フォルダ '{folder}' のインポートで例外: {e.Message}", asset: folder); }
            }
            var paths = items.Where(it => it.Wrote || (it.Action == "reimport" && !it.Failed)).Select(it => it.Path).ToList();
            if (paths.Count == 0) return;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var p in paths) AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
        }

        /// <summary>
        /// Unloads (keeps in the Hierarchy) every loaded scene in <paramref name="paths"/>. Writing an open scene's file
        /// would make Unity ask "reload?" in a modal dialog, which blocks MCP. When no other scene would stay loaded, an
        /// empty placeholder scene is added first because the last loaded scene cannot be unloaded.
        /// </summary>
        static bool CloseScenesForRestore(HashSet<string> paths, SceneArrangement arr, CctResult r)
        {
            if (paths.Count == 0) return true;
            var active = SceneManager.GetActiveScene();
            arr.ActivePath = active.IsValid() ? active.path : null;
            var toClose = new List<Scene>();
            int others = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.IsValid() || !s.isLoaded) continue;
                if (!string.IsNullOrEmpty(s.path) && paths.Contains(s.path)) toClose.Add(s);
                else others++;
            }
            if (toClose.Count == 0) return true;
            if (others == 0)
            {
                try
                {
                    arr.Placeholder = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                    arr.HasPlaceholder = arr.Placeholder.IsValid();
                }
                catch (Exception e)
                {
                    r.AddIssue(CctSeverity.Error, "SCENE_CLOSE_FAILED", "一時的な空シーンを作成できませんでした: " + e.Message);
                    return false;
                }
                if (!arr.HasPlaceholder)
                {
                    r.AddIssue(CctSeverity.Error, "SCENE_CLOSE_FAILED", "一時的な空シーンを作成できませんでした");
                    return false;
                }
            }
            foreach (var s in toClose)
            {
                var path = s.path;
                bool ok;
                try { ok = EditorSceneManager.CloseScene(s, false); }
                catch (Exception e)
                {
                    r.AddIssue(CctSeverity.Error, "SCENE_CLOSE_FAILED", $"シーン '{path}' をアンロードできませんでした: {e.Message}", asset: path);
                    return false;
                }
                if (!ok)
                {
                    r.AddIssue(CctSeverity.Error, "SCENE_CLOSE_FAILED", $"シーン '{path}' をアンロードできませんでした", asset: path);
                    return false;
                }
                arr.ClosedPaths.Add(path);
            }
            return true;
        }

        /// <summary>Loads the unloaded scenes again in place (same Hierarchy slot), restores the active scene, removes the placeholder.</summary>
        static void ReopenScenes(SceneArrangement arr, CctResult r, List<object> reloaded)
        {
            foreach (var path in arr.ClosedPaths)
            {
                try
                {
                    var s = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                    if (s.IsValid() && s.isLoaded)
                    {
                        reloaded.Add(path);
                        r.AddIssue(CctSeverity.Info, "SCENE_RELOADED", $"シーン '{path}' をディスクから開き直しました (メモリ上の変更は破棄)", asset: path);
                    }
                    else r.AddIssue(CctSeverity.Error, "SCENE_REOPEN_FAILED", $"シーン '{path}' を開き直せませんでした。手動で開いてください", asset: path);
                }
                catch (Exception e)
                {
                    r.AddIssue(CctSeverity.Error, "SCENE_REOPEN_FAILED", $"シーン '{path}' を開き直せませんでした: {e.Message}", asset: path);
                }
            }
            try
            {
                if (!string.IsNullOrEmpty(arr.ActivePath))
                {
                    var a = SceneManager.GetSceneByPath(arr.ActivePath);
                    if (a.IsValid() && a.isLoaded && SceneManager.GetActiveScene() != a) SceneManager.SetActiveScene(a);
                }
            }
            catch (Exception) { /* active scene is cosmetic */ }
            if (!arr.HasPlaceholder || !arr.Placeholder.IsValid()) return;
            int loaded = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isLoaded) loaded++;
            bool closed = false;
            try { closed = loaded > 1 && EditorSceneManager.CloseScene(arr.Placeholder, true); }
            catch (Exception) { closed = false; }
            if (!closed)
                r.AddIssue(CctSeverity.Warning, "SCENE_REOPEN_FAILED", "一時的に追加した空シーン (Untitled) を閉じられませんでした。不要なら Hierarchy から削除してください");
        }

        static List<object> Paths(IEnumerable<PlanItem> items, string action, int max) =>
            items.Where(it => it.Action == action).Take(max).Select(it =>
            {
                var d = new Dictionary<string, object>
                {
                    ["asset_path"] = it.Path,
                    ["size"] = it.Entry.Size,
                    ["unsaved_in_memory"] = it.AssetDirty || (it.IsLoadedScene && it.SceneDirty),
                };
                // Restoring an old GUID breaks references that were re-pointed to the new one; make that visible.
                if (it.GuidNow != null && !string.IsNullOrEmpty(it.Entry.Guid) && !string.Equals(it.GuidNow, it.Entry.Guid, StringComparison.OrdinalIgnoreCase))
                {
                    d["guid_now"] = it.GuidNow;
                    d["guid_restored"] = it.Entry.Guid;
                }
                return (object)d;
            }).ToList();

        static int Count(IEnumerable<PlanItem> items, string action) => items.Count(it => it.Action == action);

        static List<object> Done(IEnumerable<PlanItem> items, string action) =>
            items.Where(it => it.Action == action && !it.Failed && (it.Wrote || action == "reimport")).Select(it => (object)it.Path).ToList();

        static void AddPerFileIssues(CctResult r, List<object> paths, string code, string what)
        {
            foreach (var p in paths.Take(PerKindIssueCap))
                r.AddIssue(CctSeverity.Info, code, $"'{p}': {what}", asset: p as string);
            if (paths.Count > PerKindIssueCap)
                r.AddIssue(CctSeverity.Info, code, $"ほか {paths.Count - PerKindIssueCap} 件: {what}", value: paths.Count - PerKindIssueCap);
        }
    }
}
