using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    public static partial class CctDependencyReport
    {
        const int DefaultMaxComponents = 20000;
        const int MaxPerTargetIssues = 20;

        sealed class UserHit
        {
            public string Path;
            public int Occurrences;
            public int FirstLine;
            public bool FirstInMeta;
            public bool ViaMeta;
            public bool ViaMain;
            public bool AsReference;
            public bool AsString;
            public readonly HashSet<string> Targets = new HashSet<string>(StringComparer.Ordinal);
        }

        sealed class SceneHit
        {
            public string Scene;
            public string Object;
            public string Component;
            public string Property;
            public string Target;
            public bool SceneDirty;
            public int Count;
        }

        static CctResult RunReverse(CctArgs args)
        {
            var r = CctResult.Ok("");
            var ctx = new Context(r, args);
            r.Put("mode", ModeReverse);
            bool scanCode = args.GetBool("scan_code", false);
            bool scanScenes = args.GetBool("scan_open_scenes", true);
            int maxComponents = args.GetInt("max_components", DefaultMaxComponents, 100, 2000000);

            // ---- targets: GUID -> asset path (folders contribute their files and their own GUID)
            var inputs = ResolveInputs(ctx, args.GetStringList("assets"), true);
            var targets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in inputs.Files.Concat(inputs.Folders))
            {
                var g = AssetDatabase.AssetPathToGUID(p);
                if (!string.IsNullOrEmpty(g)) targets[g] = p;
            }
            if (targets.Count == 0)
                return FailWith(r, inputs.FromSelection || args.GetStringList("assets").Count > 0
                    ? "使用元を調べるアセットがありません (指定したパスを確認してください)"
                    : "assets (使用元を調べたいアセットのパス / フォルダー / GUID) を指定するか、Project ウィンドウで選択してください", "NO_INPUT");
            var targetPaths = new HashSet<string>(targets.Values, StringComparer.Ordinal);
            var folderTargets = inputs.Folders.ToList();
            bool IsTarget(string p) => targetPaths.Contains(p) || folderTargets.Any(f => IsUnder(p, f));

            // ---- search roots
            var roots = new List<string>();
            foreach (var raw in args.GetStringList("search_root"))
            {
                var p = CanonicalAssetPath(raw, out var isFolder);
                if (p == null || !isFolder)
                    return FailWith(r, $"search_root '{raw}' は存在するフォルダーではありません (例: Assets, Assets/MyAvatar, Packages/com.example)", "INVALID_ARGUMENT");
                if (!roots.Contains(p)) roots.Add(p);
            }
            if (roots.Count == 0) roots.Add("Assets");

            bool forceText = SerializationIsForceText(ctx);

            // ---- text scan of every file (+ .meta) under the search roots
            var candidates = ctx.AllAssetPaths
                .Where(p => roots.Any(root => IsUnder(p, root)) && !AssetDatabase.IsValidFolder(p))
                .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
            var stats = new ScanStats();
            var users = new Dictionary<string, UserHit>(StringComparer.Ordinal);
            string currentAsset = null;
            bool currentIsMeta = false;
            HexHandler handler = (hex, isRef, line, key) =>
            {
                if (!targets.TryGetValue(hex, out var tp)) return;
                if (tp == currentAsset) return; // the .meta's own "guid:" line (or a self reference)
                if (!users.TryGetValue(currentAsset, out var u)) users[currentAsset] = u = new UserHit { Path = currentAsset };
                u.Occurrences++;
                if (u.FirstLine == 0)
                {
                    u.FirstLine = line;
                    u.FirstInMeta = currentIsMeta;
                }
                if (currentIsMeta) u.ViaMeta = true; else u.ViaMain = true;
                if (isRef) u.AsReference = true; else u.AsString = true;
                u.Targets.Add(tp);
            };
            using (var progress = new Progress("Claude Creator Tools: 使用元を検索中", candidates.Count))
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (ctx.OutOfTime("file_scan")) break;
                    var path = candidates[i];
                    progress.Step(i, path);
                    var ext = Path.GetExtension(path);
                    var physical = ctx.ToPhysical(path);
                    currentAsset = path;
                    if (NoReferenceExtensions.Contains(ext) || (!scanCode && CodeExtensions.Contains(ext))) stats.SkippedByType++;
                    else
                    {
                        currentIsMeta = false;
                        var st = ScanFile(physical, ctx.MaxFileBytes, false, false, handler, out var len, out var err);
                        CountStatus(stats, st, path, ext, false, len, err);
                    }
                    if (!MetaMayHoldReferences(ext)) continue;
                    currentIsMeta = true;
                    var mst = ScanFile(physical + ".meta", 0, false, false, handler, out var mlen, out var merr);
                    if (mst != ScanStatus.NotFound) CountStatus(stats, mst, path + ".meta", ".meta", true, mlen, merr);
                }
            }

            var external = users.Values.Where(u => !IsTarget(u.Path)).OrderBy(u => u.Path, StringComparer.Ordinal).ToList();
            var internalUsers = users.Values.Where(u => IsTarget(u.Path)).OrderBy(u => u.Path, StringComparer.Ordinal).ToList();

            // ---- in-memory references from the loaded scenes (and an open Prefab Mode stage)
            var sceneHits = new Dictionary<string, SceneHit>(StringComparer.Ordinal);
            var scenesScanned = new List<object>();
            int componentsScanned = 0;
            bool componentsCapped = false;
            if (scanScenes)
                ScanScenesForTargets(ctx, IsTarget, sceneHits, scenesScanned, maxComponents, ref componentsScanned, ref componentsCapped);
            // A partial scan cannot prove that nothing uses a target. Without the in-memory scene scan, an open scene with unsaved
            // (or never saved) changes is only seen as its stale file on disk.
            bool unsavedScenesUnchecked = !scanScenes &&
                                          CctTargets.LoadedScenes().Any(s => !IsTransientScene(s) && (s.isDirty || string.IsNullOrEmpty(s.path)));
            bool conclusive = ctx.Complete && forceText && !inputs.Truncated && stats.TooLarge.Count == 0 && stats.Failed.Count == 0 &&
                              !componentsCapped && !unsavedScenesUnchecked;
            var unsure = conclusive ? ""
                : unsavedScenesUnchecked && ctx.Complete && forceText && stats.TooLarge.Count == 0 && stats.Failed.Count == 0
                    ? "【未確定: 未保存のシーンがあり、scan_open_scenes=false のためメモリ上の参照を確認していません】"
                    : "【未確定: 走査が不完全です (SCAN_INCOMPLETE / BINARY_SERIALIZATION 参照)】";
            var sceneNote = scanScenes ? "と読み込み中シーン" : " (scan_open_scenes=false のためシーン内は未確認)";

            // ---- per-target summary. Users inside the target set are counted separately: they do not block deleting the whole
            // set, but a target used only by another target (texture <- material in the same folder) is not "unused".
            var perTarget = new Dictionary<string, int[]>(StringComparer.Ordinal); // [outside asset users, scene refs, users inside the set]
            foreach (var t in targets.Values) perTarget[t] = new int[3];
            foreach (var u in external)
                foreach (var t in u.Targets)
                    if (perTarget.TryGetValue(t, out var c)) c[0]++;
            foreach (var u in internalUsers)
                foreach (var t in u.Targets)
                    if (t != u.Path && perTarget.TryGetValue(t, out var c)) c[2]++;
            foreach (var h in sceneHits.Values)
                if (perTarget.TryGetValue(h.Target, out var c)) c[1] += h.Count;
                else
                {
                    var owner = folderTargets.FirstOrDefault(f => IsUnder(h.Target, f));
                    if (owner != null && perTarget.TryGetValue(owner, out var fc)) fc[1] += h.Count;
                }
            var fileTargets = perTarget.Where(kv => !AssetDatabase.IsValidFolder(kv.Key)).ToList();
            var unused = fileTargets.Where(kv => kv.Value[0] == 0 && kv.Value[1] == 0 && kv.Value[2] == 0).Select(kv => kv.Key)
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            var notCovered = (stats.Binary.Count > 0 ? $"バイナリ形式の {stats.Binary.Count} ファイル (LightingData 等)、" : "バイナリ形式のファイル、") +
                             "スキップしたファイル、コードからの名前参照、他プロジェクトは対象外です";

            if (fileTargets.Count <= MaxPerTargetIssues)
            {
                foreach (var kv in fileTargets.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    var examples = external.Where(u => u.Targets.Contains(kv.Key)).Take(3).Select(u => u.Path).ToList();
                    if (kv.Value[0] > 0)
                        ctx.Issue(CctSeverity.Info, "TARGET_USED", $"{kv.Value[0]} 個のアセットから参照されています (例: {string.Join(", ", examples)})",
                            value: kv.Value[0], asset: kv.Key);
                    else if (kv.Value[2] > 0)
                    {
                        var inner = internalUsers.Where(u => u.Targets.Contains(kv.Key) && u.Path != kv.Key).Take(3).Select(u => u.Path).ToList();
                        ctx.Issue(CctSeverity.Info, "TARGET_USED",
                            $"対象に含まれる {kv.Value[2]} 個のアセットからだけ参照されています (例: {string.Join(", ", inner)})。" +
                            "対象をまとめて削除するなら問題ありませんが、これだけを削除すると参照元が Missing になります",
                            value: kv.Value[2], asset: kv.Key);
                    }
                    if (kv.Value[1] > 0)
                        ctx.Issue(CctSeverity.Info, "USED_IN_OPEN_SCENE", $"読み込み中のシーンのオブジェクトから {kv.Value[1]} 箇所で参照されています (scene_users 参照)",
                            value: kv.Value[1], asset: kv.Key);
                    if (kv.Value[0] == 0 && kv.Value[1] == 0 && kv.Value[2] == 0)
                        ctx.Issue(CctSeverity.Info, "TARGET_UNUSED",
                            unsure + $"search_root ({string.Join(", ", roots)}) のテキスト/.meta{sceneNote}からの参照は見つかりませんでした。" + notCovered,
                            asset: kv.Key);
                }
            }
            else
            {
                int used = fileTargets.Count - unused.Count;
                ctx.Issue(CctSeverity.Info, "TARGET_USED", $"対象 {fileTargets.Count} 個中 {used} 個が参照されています (対象の外の参照元 {external.Count} 件、対象どうしの参照を含む)", value: used);
                if (unused.Count > 0)
                    ctx.Issue(CctSeverity.Info, "TARGET_UNUSED",
                        unsure + $"対象 {fileTargets.Count} 個中 {unused.Count} 個は参照が見つかりませんでした (unused_targets 参照)。" + notCovered, value: unused.Count);
                if (sceneHits.Count > 0)
                    ctx.Issue(CctSeverity.Info, "USED_IN_OPEN_SCENE", $"読み込み中のシーンから {sceneHits.Values.Sum(h => h.Count)} 箇所で参照されています (scene_users 参照)",
                        value: sceneHits.Count);
            }

            var byName = targets.Values.Where(p =>
                    CodeExtensions.Contains(Path.GetExtension(p)) || p.Contains("/Resources/") || p.Contains("/StreamingAssets/") ||
                    p.Contains("/Editor Default Resources/"))
                .Take(5).ToList();
            if (byName.Count > 0)
                ctx.Issue(CctSeverity.Info, "NAME_BASED_REFERENCE_POSSIBLE",
                    $"シェーダー/スクリプト/Resources 等はコードから名前で使われることがあり (Shader.Find, Resources.Load, AddComponent<T> など)、この検索では検出できません: {string.Join(", ", byName)}" +
                    (scanCode ? "" : "。scan_code=true でコード内の GUID 文字列も検索できます"));
            if (stats.TooLarge.Count > 0 || stats.Failed.Count > 0)
                ctx.Issue(CctSeverity.Warning, "SCAN_INCOMPLETE",
                    $"検索できなかったファイルがあります (大きすぎる {stats.TooLarge.Count} 件 / 読み込み失敗 {stats.Failed.Count} 件、例: {string.Join(", ", stats.TooLarge.Concat(stats.Failed).Take(3))})。" +
                    "参照なしと判断する前に確認してください" + (stats.TooLarge.Count > 0 ? " (max_file_mb で上限を変更可)" : ""),
                    value: stats.TooLarge.Count + stats.Failed.Count);
            if (stats.Binary.Count > 0)
                ctx.Issue(CctSeverity.Info, "BINARY_FILES_SKIPPED", $"バイナリ形式のアセット {stats.Binary.Count} 件は検索していません (例: {string.Join(", ", stats.Binary.Take(3))})",
                    value: stats.Binary.Count);
            if (componentsCapped)
                ctx.Issue(CctSeverity.Warning, "SCAN_INCOMPLETE", $"シーン内の確認はコンポーネント {maxComponents} 個で打ち切りました (max_components で変更可)", value: maxComponents);

            // ---- output
            // What was asked for (files named directly + folders); every file found inside a folder is counted in target_count
            // and listed in per_target.
            r.Put("targets", Cap(inputs.ExplicitFiles.Concat(inputs.Folders).Distinct(StringComparer.Ordinal).Select(p => new Dictionary<string, object>
            {
                ["path"] = p,
                ["guid"] = AssetDatabase.AssetPathToGUID(p),
                ["type"] = AssetDatabase.IsValidFolder(p) ? "Folder" : TypeNameOf(p),
            }).OrderBy(d => (string)d["path"], StringComparer.Ordinal), ctx.MaxEntries, out var tOmitted));
            r.Put("target_count", targets.Count);
            if (tOmitted > 0) r.Put("targets_omitted", tOmitted);
            r.Put("search_roots", roots.ToList());
            r.Put("user_count", external.Count);
            bool multi = targets.Count > 1;
            r.Put("users", Cap(external.Select(u => UserRow(u, multi)), ctx.MaxEntries, out var uOmitted));
            if (uOmitted > 0) r.Put("users_omitted", uOmitted);
            r.Put("users_by_type", external.GroupBy(u => TypeNameOf(u.Path), StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (object)new Dictionary<string, object>
                {
                    ["type"] = g.Key,
                    ["count"] = g.Count(),
                    ["examples"] = g.Take(5).Select(u => (object)u.Path).ToList(),
                }).ToList());
            r.Put("internal_reference_count", internalUsers.Count);
            if (internalUsers.Count > 0) PutCapped(r, "internal_users", internalUsers.Select(u => u.Path), Math.Min(20, ctx.MaxEntries));
            if (perTarget.Count > 1)
                PutCapped(r, "per_target", perTarget.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new Dictionary<string, object>
                {
                    ["path"] = kv.Key,
                    ["asset_users"] = kv.Value[0],
                    ["scene_references"] = kv.Value[1],
                    ["users_inside_targets"] = kv.Value[2],
                }), ctx.MaxEntries);
            PutCapped(r, "unused_targets", unused, ctx.MaxEntries);
            r.Put("conclusive", conclusive);
            r.Put("scene_user_count", sceneHits.Values.Sum(h => h.Count));
            r.Put("scene_users", Cap(sceneHits.Values.Select(h => new Dictionary<string, object>
            {
                ["scene"] = h.Scene,
                ["object"] = h.Object,
                ["component"] = h.Component,
                ["property"] = h.Property,
                ["count"] = h.Count,
                ["target"] = h.Target,
                ["scene_unsaved"] = h.SceneDirty,
            }), ctx.MaxEntries, out var shOmitted));
            if (shOmitted > 0) r.Put("scene_users_omitted", shOmitted);
            r.Put("scenes_scanned", scenesScanned);
            var scan = stats.ToPlain(ctx.MaxEntries);
            scan["candidate_files"] = candidates.Count;
            scan["components_scanned"] = componentsScanned;
            scan["components_capped"] = componentsCapped;
            scan["scan_code"] = scanCode;
            r.Put("scan", scan);
            ctx.FlushIssueCaps();
            ctx.FlushTimeBudget();

            var subject = targets.Count == 1 ? targets.Values.First() : $"{targets.Count} 個のアセット";
            r.Message = $"{subject} の使用元: アセット {external.Count} 件 / シーン内参照 {sceneHits.Values.Sum(h => h.Count)} 箇所" +
                        (internalUsers.Count > 0 ? $" (対象同士の参照 {internalUsers.Count} 件は除外)" : "") +
                        (unused.Count > 0 && fileTargets.Count > 1 ? $" / 参照なし {unused.Count} 個" : "") +
                        (conclusive ? "" : " (走査が不完全なため未確定)");
            return r;
        }

        static object UserRow(UserHit u, bool includeTargets)
        {
            var d = new Dictionary<string, object>
            {
                ["path"] = u.Path,
                ["type"] = TypeNameOf(u.Path),
                ["occurrences"] = u.Occurrences,
                ["first_line"] = u.FirstLine,
            };
            if (u.FirstInMeta) d["first_line_in"] = "meta";
            if (u.ViaMeta) d["via_meta"] = true;
            if (u.AsString && !u.AsReference) d["string_only"] = true;
            if (includeTargets) d["targets"] = u.Targets.OrderBy(t => t, StringComparer.Ordinal).Take(5).Cast<object>().ToList();
            return d;
        }

        static void ScanScenesForTargets(Context ctx, Func<string, bool> isTarget, Dictionary<string, SceneHit> hits, List<object> scenesScanned,
            int maxComponents, ref int componentsScanned, ref bool capped)
        {
            var scenes = ScenesToScan(out var stagePath, out var stageScene);
            var pathCache = new Dictionary<int, string>();
            long visits = 0;
            var all = new List<KeyValuePair<UnityEngine.SceneManagement.Scene, GameObject>>();
            foreach (var scene in scenes)
            {
                bool isStage = stagePath != null && scene == stageScene;
                scenesScanned.Add(new Dictionary<string, object>
                {
                    ["name"] = string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name,
                    ["path"] = isStage ? stagePath : (string.IsNullOrEmpty(scene.path) ? "(未保存のシーン)" : scene.path),
                    ["is_dirty"] = scene.isDirty,
                    ["prefab_stage"] = isStage ? (object)true : null,
                });
                foreach (var go in AllGameObjects(scene)) all.Add(new KeyValuePair<UnityEngine.SceneManagement.Scene, GameObject>(scene, go));
            }

            string AssetPathOf(SerializedProperty p)
            {
                var id = p.objectReferenceInstanceIDValue;
                if (id == 0) return "";
                if (pathCache.TryGetValue(id, out var cached)) return cached;
                var v = p.objectReferenceValue;
                var path = v != null && EditorUtility.IsPersistent(v) ? AssetDatabase.GetAssetPath(v) ?? "" : "";
                pathCache[id] = path;
                return path;
            }

            int scanned = componentsScanned;
            using (var progress = new Progress("Claude Creator Tools: シーン内の参照を確認中", all.Count, 500))
            {
                for (int i = 0; i < all.Count && !capped; i++)
                {
                    if (ctx.OutOfTime("scene_scan")) break;
                    var scene = all[i].Key;
                    var go = all[i].Value;
                    if (go == null) continue;
                    progress.Step(i, go.name);
                    var sceneName = stagePath != null && scene == stageScene ? "Prefab Mode: " + stagePath
                        : string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name;
                    bool dirty = scene.isDirty;
                    try
                    {
                        if (PrefabUtility.IsAnyPrefabInstanceRoot(go))
                        {
                            var src = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                            if (!string.IsNullOrEmpty(src) && isTarget(src))
                                AddSceneHit(hits, sceneName, CctTargets.GetPath(go), "(Prefab instance)", "(source prefab)", src, dirty);
                        }
                    }
                    catch (Exception) { /* not a prefab instance after all */ }
                    foreach (var c in go.GetComponents<Component>())
                    {
                        if (c == null) continue;
                        if (++scanned > maxComponents)
                        {
                            capped = true;
                            break;
                        }
                        var comp = c;
                        WalkReferences(c, true, p =>
                        {
                            var ap = AssetPathOf(p);
                            if (ap.Length == 0 || !isTarget(ap)) return;
                            AddSceneHit(hits, sceneName, CctTargets.GetPath(comp.transform), comp.GetType().Name,
                                AggregatePropertyPath(p.propertyPath), ap, dirty);
                        }, ref visits);
                    }
                }
            }
            componentsScanned = Math.Min(scanned, maxComponents);
        }

        static void AddSceneHit(Dictionary<string, SceneHit> hits, string scene, string obj, string component, string property, string target, bool dirty)
        {
            var key = scene + "|" + obj + "|" + component + "|" + property + "|" + target;
            if (!hits.TryGetValue(key, out var h))
                hits[key] = h = new SceneHit { Scene = scene, Object = obj, Component = component, Property = property, Target = target, SceneDirty = dirty };
            h.Count++;
        }
    }
}
