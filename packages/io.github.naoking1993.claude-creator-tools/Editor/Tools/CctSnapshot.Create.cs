using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    public static partial class CctSnapshot
    {
        sealed class CaptureRequest
        {
            public string Label = "";
            public string Kind = "manual";
            public string RestoreOf;
            public List<string> Specs = new List<string>();
            public bool IncludeOpenScenes;
            public bool IncludeDependencies;
            public string DependencyFilter = "editable";
            public double MaxTotalMb = 500;
            public List<Dictionary<string, object>> PreSkipped;
        }

        sealed class Candidate
        {
            public string AssetPath, Source, Abs;
            public bool Exists, IsLoadedScene, CaptureFromMemory, SceneDirty;
            public Scene Scene;
            public long EstimatedBytes;
        }

        /// <summary>Everything gathered before copying; issues go straight into the run's result.</summary>
        sealed class CaptureContext
        {
            public CctResult Result;
            public Dictionary<string, Scene> Scenes;
            public readonly Dictionary<string, Candidate> Candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Order = new List<string>();
            public readonly HashSet<string> Folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> FolderRoots = new List<string>();
            public readonly List<Dictionary<string, object>> Skipped = new List<Dictionary<string, object>>();
            public int DependencyFilteredOut;
            public int DependencyPackages;

            public void Skip(string path, string code, string message, string severity)
            {
                Skipped.Add(new Dictionary<string, object> { ["asset_path"] = path, ["code"] = code, ["reason"] = message });
                if (severity != null) Result.AddIssue(severity, code, message, asset: path);
            }

            public void Add(string assetPath, string source)
            {
                if (Candidates.ContainsKey(assetPath)) return;
                Candidates[assetPath] = new Candidate { AssetPath = assetPath, Source = source };
                Order.Add(assetPath);
            }
        }

        static CctResult Create(CctArgs args)
        {
            var rawFilter = args.GetString("dependency_filter");
            var filter = args.GetEnum("dependency_filter", "editable", "editable", "all");
            if (!string.IsNullOrWhiteSpace(rawFilter) && !string.Equals(rawFilter.Trim(), filter, StringComparison.OrdinalIgnoreCase))
                return CctResult.Fail("dependency_filter は 'editable' か 'all' を指定してください", "INVALID_ARGUMENT",
                    new Dictionary<string, object> { ["dependency_filter"] = rawFilter });
            var maxMb = args.GetDouble("max_total_mb", 500);
            if (!(maxMb > 0))
                return CctResult.Fail("max_total_mb は正の数を指定してください", "INVALID_ARGUMENT", new Dictionary<string, object> { ["max_total_mb"] = maxMb });

            var req = new CaptureRequest
            {
                Label = args.GetString("label", "") ?? "",
                Specs = args.GetStringList("assets"),
                IncludeOpenScenes = args.GetBool("include_open_scenes", true),
                IncludeDependencies = args.GetBool("include_dependencies", false),
                DependencyFilter = filter,
                MaxTotalMb = maxMb,
            };

            int maxEntries = args.GetInt("max_entries", DefaultMaxEntries, 1, 5000);
            var r = CctResult.Ok("");
            var m = Capture(req, r, out var dir, out var failure);
            if (m == null)
            {
                CopyIssues(r, failure);
                failure.Put("action", "create");
                return failure;
            }

            r.Put("action", "create");
            r.Put("id", m.Id);
            r.Put("label", m.Label);
            r.Put("created_at", m.CreatedAt);
            r.Put("snapshot_dir", dir);
            r.Put("manifest_path", CctPaths.Combine(dir, ManifestName));
            r.Put("file_count", m.Files.Count);
            r.Put("folder_count", m.Folders.Count);
            r.Put("total_bytes", m.TotalBytes);
            r.Put("total_mb", Mb(m.TotalBytes));
            r.Put("incomplete", m.Incomplete);
            r.Put("scenes", m.Files.Where(f => f.SceneState != null).Select(f => (object)new Dictionary<string, object>
            {
                ["asset_path"] = f.AssetPath,
                ["scene_was_dirty"] = f.SceneWasDirty,
                ["scene_state"] = f.SceneState,
            }).ToList());
            // Per extension, so Claude can see at a glance whether materials / clips / prefabs are covered.
            var fileTypes = new Dictionary<string, object>();
            foreach (var g in m.Files.GroupBy(f => Path.GetExtension(f.AssetPath).ToLowerInvariant()).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
                fileTypes[g.Key.Length == 0 ? "(none)" : g.Key] = g.Count();
            r.Put("file_types", fileTypes);
            // Packages/ dependencies (SDK / MA / lilToon scripts and shaders) and hidden helper scenes stay in the manifest only.
            var full = new Dictionary<string, object>();
            PutList(r, "skipped", VisibleSkipped(m.Skipped), maxEntries, full);
            r.Put("files", m.Files.Select(f => (object)f.AssetPath).Take(FullListMax).ToList()); // report only (not a summary key)
            PutFullLists(r, full);
            bool scenesOnly = req.Specs.Count == 0 && !req.IncludeDependencies;
            r.Message = $"スナップショット '{m.Id}' を作成しました: {m.Files.Count} ファイル / {Mb(m.TotalBytes)} MB" +
                        (m.Incomplete ? " (一部コピー失敗あり)" : "") +
                        (scenesOnly ? "。シーンファイルのみです。共有マテリアル・アニメーション等も戻せるようにするには assets に指定するか include_dependencies=true で作成してください" : "");
            return r;
        }

        /// <summary>
        /// Collects files for the request, checks the size limit, then copies file + .meta into a new snapshot folder.
        /// Shared by create and restore's automatic pre-restore snapshot. Returns null (and a failed result) when
        /// nothing was written.
        /// </summary>
        static Manifest Capture(CaptureRequest req, CctResult r, out string dir, out CctResult failure)
        {
            dir = null;
            failure = null;
            var ctx = new CaptureContext { Result = r, Scenes = LoadedScenesByPath() };
            if (req.PreSkipped != null) ctx.Skipped.AddRange(req.PreSkipped);

            foreach (var spec in req.Specs) AddSpec(ctx, spec, "explicit");

            if (req.IncludeOpenScenes)
            {
                foreach (var s in CctTargets.LoadedScenes())
                {
                    // Hidden helper scenes of packages (NDMF's "___NDMF Preview___" is always loaded with Modular Avatar) are not
                    // the user's work: record them in the manifest only.
                    if (s.isSubScene && !string.IsNullOrEmpty(s.path) && s.path.StartsWith("Packages/", StringComparison.Ordinal))
                    {
                        ctx.Skipped.Add(new Dictionary<string, object>
                        {
                            ["asset_path"] = s.path, ["code"] = "SKIPPED_PACKAGE", ["reason"] = "hidden package scene", ["source"] = "hidden_scene",
                        });
                        continue;
                    }
                    if (string.IsNullOrEmpty(s.path))
                    {
                        ctx.Skip("(Untitled)", "UNTITLED_SCENE_SKIPPED",
                            "保存されていない新規シーン (Untitled) はファイルが無いためスナップショットできません。先に名前を付けて保存してください", CctSeverity.Warning);
                        continue;
                    }
                    AddSpec(ctx, s.path, "open_scene");
                }
            }

            if (req.IncludeDependencies && ctx.Order.Count > 0) AddDependencies(ctx, req.DependencyFilter);

            // Resolve existence / scene state, drop what cannot be captured, estimate size.
            // SaveScene throws in Play mode and while entering it, so scenes then come from disk.
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
            bool warnedPlay = false;
            var list = new List<Candidate>();
            foreach (var path in ctx.Order)
            {
                var c = ctx.Candidates[path];
                c.Abs = AbsOf(c.AssetPath);
                c.Exists = File.Exists(c.Abs);
                if (ctx.Scenes.TryGetValue(c.AssetPath, out var scene))
                {
                    c.IsLoadedScene = true;
                    c.Scene = scene;
                    c.SceneDirty = scene.isDirty;
                    if (playing)
                    {
                        if (!warnedPlay)
                        {
                            r.AddIssue(CctSeverity.Warning, "PLAY_MODE_SCENE_FROM_DISK", "Play モード中のため、シーンはディスク上のファイルを保存します (Play 中の状態は保存しません)");
                            warnedPlay = true;
                        }
                    }
                    else c.CaptureFromMemory = c.SceneDirty || !c.Exists;
                }
                if (!c.Exists && !c.CaptureFromMemory)
                {
                    ctx.Skip(c.AssetPath, "ASSET_NOT_FOUND", $"ファイルが見つかりません: {c.AssetPath}", CctSeverity.Warning);
                    continue;
                }
                try
                {
                    c.EstimatedBytes = (c.Exists ? new FileInfo(c.Abs).Length : 0) +
                                       (File.Exists(c.Abs + ".meta") ? new FileInfo(c.Abs + ".meta").Length : 0);
                }
                catch (Exception) { c.EstimatedBytes = 0; }
                list.Add(c);
            }

            if (list.Count == 0)
            {
                failure = CctResult.Fail("スナップショットするファイルがありません (assets を指定するか、保存済みのシーンを開いてください)", "NOTHING_TO_SNAPSHOT",
                    new Dictionary<string, object> { ["skipped"] = VisibleSkipped(ctx.Skipped).Take(50).ToList() });
                return null;
            }

            long estimated = list.Sum(c => c.EstimatedBytes);
            if (estimated > req.MaxTotalMb * 1024.0 * 1024.0)
            {
                failure = CctResult.Fail($"推定サイズ {Mb(estimated)} MB が上限 max_total_mb={req.MaxTotalMb} を超えるため中止しました (何もコピーしていません)。" +
                                         "details.largest の大きいファイルを assets から外すか、dependency_filter='editable' にするか、max_total_mb を上げて再実行してください",
                    "SIZE_LIMIT", new Dictionary<string, object>
                    {
                        ["estimated_mb"] = Mb(estimated),
                        ["max_total_mb"] = req.MaxTotalMb,
                        ["file_count"] = list.Count,
                        ["largest"] = list.OrderByDescending(c => c.EstimatedBytes).Take(20)
                            .Select(c => (object)new Dictionary<string, object> { ["asset_path"] = c.AssetPath, ["mb"] = Mb(c.EstimatedBytes) }).ToList(),
                    });
                return null;
            }

            // The snapshot stores files as they are on disk; say so when memory holds something newer.
            foreach (var p in FindDirtyLoadedAssets(list.Select(c => c.AssetPath)).Take(30))
                r.AddIssue(CctSeverity.Warning, "ASSET_HAS_UNSAVED_CHANGES",
                    $"'{p}' はメモリ上に未保存の変更があります。スナップショットはディスク上の内容です (変更も残すなら先に保存してください)", asset: p);
            var stagePath = CurrentPrefabStage(out var stageUnsaved);
            if (stagePath != null && stageUnsaved && ctx.Candidates.ContainsKey(stagePath))
                r.AddIssue(CctSeverity.Warning, "PREFAB_STAGE_UNSAVED",
                    $"'{stagePath}' は Prefab モードで未保存の変更があります。スナップショットはディスク上の内容です", asset: stagePath);

            // Folder .meta files of every parent too, so a deleted folder tree comes back with its own GUIDs. (No early exit
            // on an already known folder: folder specs add their own tree but not the folders above them.)
            foreach (var c in list)
                for (var f = Parent(c.AssetPath); f != null && f.Length > "Assets".Length; f = Parent(f))
                    ctx.Folders.Add(f);

            var m = new Manifest
            {
                Label = req.Label ?? "",
                Kind = req.Kind,
                RestoreOf = req.RestoreOf,
                CreatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                UnityVersion = Application.unityVersion,
                ProjectRoot = CctPaths.ProjectRoot,
                ToolVersion = CctRunner.PackageVersion,
                FolderRoots = ctx.FolderRoots,
                Skipped = ctx.Skipped,
                Options = new Dictionary<string, object>
                {
                    ["include_open_scenes"] = req.IncludeOpenScenes,
                    ["include_dependencies"] = req.IncludeDependencies,
                    ["dependency_filter"] = req.DependencyFilter,
                    ["max_total_mb"] = req.MaxTotalMb,
                    ["dependencies_filtered_out"] = ctx.DependencyFilteredOut,
                    ["dependencies_in_packages"] = ctx.DependencyPackages,
                },
            };
            m.Id = NewSnapshotId(req.Kind == "pre-restore" ? null : req.Label, req.Kind == "pre-restore" ? "pre-restore" : null);
            dir = CctPaths.EnsureDir(SnapshotDir(m.Id));
            var filesDir = CctPaths.EnsureDir(CctPaths.Combine(dir, "files"));

            bool tempCreated = false;
            bool completed = false;
            try
            {
                using (var progress = new Progress("Claude Creator Tools: スナップショット作成", list.Count))
                {
                    int index = 0;
                    foreach (var c in list)
                    {
                        index++;
                        progress.Step(index, c.AssetPath);
                        CopyCandidate(c, index, m, filesDir, ref tempCreated, r);
                    }
                }
                if (m.Files.Count == 0)
                {
                    // A manifest with no files would look like a usable backup; the folder is removed in finally.
                    failure = CctResult.Fail("ファイルを 1 つもコピーできなかったため、スナップショットを作成しませんでした (issues を確認してください)", "COPY_FAILED",
                        new Dictionary<string, object> { ["skipped"] = VisibleSkipped(m.Skipped).Take(50).ToList() });
                    return null;
                }
                CopyFolderMetas(ctx, m, dir, r);
                WriteManifest(dir, m);
                completed = true;
            }
            finally
            {
                CleanupTempFolder(tempCreated, r);
                if (!completed)
                {
                    try { Directory.Delete(dir, true); }
                    catch (Exception) { /* partial folder without manifest is ignored by list */ }
                }
            }
            return m;
        }

        /// <summary>Skipped entries worth showing: Packages/ dependencies and hidden package scenes stay in the manifest only.</summary>
        static IEnumerable<object> VisibleSkipped(IEnumerable<Dictionary<string, object>> skipped) =>
            skipped.Where(x => !(x.TryGetValue("source", out var src) && ((src as string) == "dependency" || (src as string) == "hidden_scene"))).Cast<object>();

        static void AddSpec(CaptureContext ctx, string raw, string source)
        {
            if (!TryNormalizeAssetPath(raw, out var path, out var code))
            {
                switch (code)
                {
                    case "SKIPPED_PACKAGE":
                        ctx.Skip(path ?? raw, code, $"Packages/ 以下は VCC が管理するためスナップショットしません: {raw}", CctSeverity.Info);
                        break;
                    case "NOT_ASSET_PATH":
                        ctx.Skip(raw, code, $"Assets/ 以下のパスではありません ('Assets/...' から始まるパスで指定してください): {raw}", CctSeverity.Error);
                        break;
                    case "INVALID_PATH":
                        ctx.Skip(raw, code, $"パスに使えない文字が含まれています: {raw}", CctSeverity.Error);
                        break;
                    default:
                        ctx.Skip(raw, "PATH_OUTSIDE_PROJECT", $"絶対パスや '..' を含むパスは使えません。'Assets/...' から始まるプロジェクト内のパスで指定してください: {raw}", CctSeverity.Error);
                        break;
                }
                return;
            }
            if (IsInTempFolder(path)) return;
            path = CanonicalCase(path);
            var abs = AbsOf(path);
            if (Directory.Exists(abs))
            {
                if (!ctx.FolderRoots.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase))) ctx.FolderRoots.Add(path);
                if (path != "Assets") ctx.Folders.Add(path);
                WalkAssetFolder(path, abs,
                    file => ctx.Add(file, source == "explicit" ? "folder" : source),
                    folder => ctx.Folders.Add(folder),
                    (p, e) => ctx.Skip(p, "COPY_FAILED", $"フォルダを読めません: {p} ({e.Message})", CctSeverity.Error));
                return;
            }
            // A loaded scene whose file was deleted can still be captured from memory, so let it through.
            if (File.Exists(abs) || ctx.Scenes.ContainsKey(path))
            {
                ctx.Add(path, source);
                return;
            }
            ctx.Skip(path, "ASSET_NOT_FOUND", $"見つかりません: {path}", CctSeverity.Warning);
        }

        /// <summary>
        /// On-disk spelling of an existing asset path. Windows finds "assets/avatar/body.mat" for "Assets/Avatar/Body.mat",
        /// but Unity's asset paths are case-sensitive, so a typed path is corrected before it reaches the manifest.
        /// </summary>
        static string CanonicalCase(string assetPath)
        {
            try
            {
                var segments = assetPath.Split('/');
                var abs = AbsOf("Assets");
                var result = "Assets";
                for (int i = 1; i < segments.Length; i++)
                {
                    var match = Directory.EnumerateFileSystemEntries(abs, segments[i]).FirstOrDefault();
                    if (match == null) return assetPath;
                    var name = Path.GetFileName(match);
                    if (!string.Equals(name, segments[i], StringComparison.OrdinalIgnoreCase)) return assetPath; // 8.3 alias etc.
                    result += "/" + name;
                    abs = CctPaths.Combine(abs, name);
                }
                return result;
            }
            catch (Exception) { return assetPath; }
        }

        static void AddDependencies(CaptureContext ctx, string filter)
        {
            var r = ctx.Result;
            var deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = ctx.Order.Where(p => File.Exists(AbsOf(p))).ToArray();
            try
            {
                if (roots.Length > 0)
                    foreach (var d in AssetDatabase.GetDependencies(roots, true)) deps.Add(d);
            }
            catch (Exception e)
            {
                r.AddIssue(CctSeverity.Warning, "DEPENDENCIES_FAILED", "依存アセットの取得に失敗しました: " + e.Message);
            }

            // GetDependencies reads the saved scene file; references added in memory since the last save are found
            // through the live objects instead.
            if (!EditorApplication.isPlaying)
            {
                foreach (var path in ctx.Order.ToList())
                {
                    if (!ctx.Scenes.TryGetValue(path, out var scene) || !scene.isDirty) continue;
                    try
                    {
                        var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var o in EditorUtility.CollectDependencies(scene.GetRootGameObjects().Cast<Object>().ToArray()))
                        {
                            if (o == null || !EditorUtility.IsPersistent(o)) continue;
                            // Built-in resources report "Library/unity default resources" / "Resources/unity_builtin_extra".
                            var p = AssetDatabase.GetAssetPath(o);
                            if (!string.IsNullOrEmpty(p) && (p.StartsWith("Assets/", StringComparison.Ordinal) || p.StartsWith("Packages/", StringComparison.Ordinal)))
                                direct.Add(p);
                        }
                        if (direct.Count > 0)
                            foreach (var d in AssetDatabase.GetDependencies(direct.ToArray(), true)) deps.Add(d);
                    }
                    catch (Exception e)
                    {
                        r.AddIssue(CctSeverity.Warning, "DEPENDENCIES_FAILED", $"シーン '{path}' のメモリ上の参照を取得できませんでした: {e.Message}", asset: path);
                    }
                }
            }

            foreach (var d in deps.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (ctx.Candidates.ContainsKey(d)) continue;
                if (d.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    ctx.DependencyPackages++;
                    ctx.Skipped.Add(new Dictionary<string, object>
                    {
                        ["asset_path"] = d, ["code"] = "SKIPPED_PACKAGE", ["reason"] = "依存先が Packages/ 以下 (VCC 管理)", ["source"] = "dependency",
                    });
                    continue;
                }
                if (!d.StartsWith("Assets/", StringComparison.Ordinal)) continue; // built-in resources
                if (!TryNormalizeAssetPath(d, out var path, out _) || IsInTempFolder(path)) continue;
                if (filter == "editable" && !EditableExtensions.Contains(Path.GetExtension(path)))
                {
                    ctx.DependencyFilteredOut++;
                    continue;
                }
                if (Directory.Exists(AbsOf(path))) continue;
                ctx.Add(path, "dependency");
            }
            if (ctx.DependencyPackages > 0)
                r.AddIssue(CctSeverity.Info, "SKIPPED_PACKAGE",
                    $"依存アセットのうち Packages/ 以下の {ctx.DependencyPackages} 件は VCC 管理のため対象外です", value: ctx.DependencyPackages);
        }

        static void CopyCandidate(Candidate c, int index, Manifest m, string filesDir, ref bool tempCreated, CctResult r)
        {
            var storedName = StoredName(index, c.AssetPath);
            var storedAbs = CctPaths.Combine(filesDir, storedName);
            var storedMetaAbs = storedAbs + ".meta";
            var entry = new FileEntry { AssetPath = c.AssetPath, Source = c.Source };
            string tempScene = null;
            try
            {
                var srcAbs = c.Abs;
                if (c.IsLoadedScene)
                {
                    entry.SceneState = "disk";
                    entry.SceneWasDirty = c.SceneDirty;
                }
                if (c.CaptureFromMemory)
                {
                    tempScene = SaveSceneCopy(c.Scene, m.Id, index, ref tempCreated, r);
                    if (tempScene != null)
                    {
                        srcAbs = AbsOf(tempScene);
                        entry.SceneState = "in_memory";
                        r.AddIssue(CctSeverity.Info, "SCENE_CAPTURED_FROM_MEMORY",
                            $"シーン '{c.AssetPath}' の未保存の変更を含むメモリ上の状態を保存しました (scene_was_dirty=true)", asset: c.AssetPath);
                    }
                    else if (c.Exists)
                    {
                        r.AddIssue(CctSeverity.Warning, "SCENE_CAPTURE_FAILED",
                            $"シーン '{c.AssetPath}' のメモリ上の状態を保存できなかったため、ディスク上のファイルを保存しました (未保存の変更は含まれません)", asset: c.AssetPath);
                    }
                    else
                    {
                        m.Skipped.Add(new Dictionary<string, object> { ["asset_path"] = c.AssetPath, ["code"] = "SCENE_CAPTURE_FAILED", ["reason"] = "in-memory capture failed and no file on disk" });
                        r.AddIssue(CctSeverity.Warning, "SCENE_CAPTURE_FAILED",
                            $"シーン '{c.AssetPath}' はディスクにファイルが無く、メモリ上の状態も保存できませんでした", asset: c.AssetPath);
                        return;
                    }
                }

                entry.Sha256 = CopyWithHash(srcAbs, storedAbs, out entry.Size);
                entry.Stored = "files/" + storedName;
                var metaAbs = c.Abs + ".meta";
                if (File.Exists(metaAbs))
                {
                    entry.MetaSha256 = CopyWithHash(metaAbs, storedMetaAbs, out entry.MetaSize);
                    entry.MetaStored = "files/" + storedName + ".meta";
                }
                entry.Guid = AssetDatabase.AssetPathToGUID(c.AssetPath);
                if (string.IsNullOrEmpty(entry.Guid)) entry.Guid = ReadGuidFromMeta(metaAbs);
                m.Files.Add(entry);
                m.TotalBytes += entry.Size + entry.MetaSize;
            }
            catch (Exception e)
            {
                TryDeleteFile(storedAbs);
                TryDeleteFile(storedMetaAbs);
                m.Incomplete = true;
                m.Skipped.Add(new Dictionary<string, object> { ["asset_path"] = c.AssetPath, ["code"] = "COPY_FAILED", ["reason"] = e.Message });
                r.AddIssue(CctSeverity.Error, "COPY_FAILED", $"'{c.AssetPath}' をコピーできませんでした: {e.Message}", asset: c.AssetPath);
            }
            finally
            {
                if (tempScene != null) DeleteTempScene(tempScene, r);
            }
        }

        static void CopyFolderMetas(CaptureContext ctx, Manifest m, string dir, CctResult r)
        {
            if (ctx.Folders.Count == 0) return;
            var foldersDir = CctPaths.EnsureDir(CctPaths.Combine(dir, "folders"));
            int index = 0;
            foreach (var folder in ctx.Folders.OrderBy(x => x, StringComparer.Ordinal))
            {
                var metaAbs = AbsOf(folder) + ".meta";
                if (!File.Exists(metaAbs)) continue;
                index++;
                var name = index.ToString("D5", CultureInfo.InvariantCulture) + "-" + CctPaths.SafeName(folder.Substring(folder.LastIndexOf('/') + 1), 48) + ".meta";
                var stored = CctPaths.Combine(foldersDir, name);
                try
                {
                    var sha = CopyWithHash(metaAbs, stored, out var size);
                    var guid = AssetDatabase.AssetPathToGUID(folder);
                    if (string.IsNullOrEmpty(guid)) guid = ReadGuidFromMeta(metaAbs);
                    m.Folders.Add(new FolderEntry { AssetPath = folder, Guid = guid, MetaSha256 = sha, MetaStored = "folders/" + name });
                    m.TotalBytes += size;
                }
                catch (Exception e)
                {
                    m.Incomplete = true;
                    r.AddIssue(CctSeverity.Error, "COPY_FAILED", $"フォルダ '{folder}' の .meta をコピーできませんでした: {e.Message}", asset: folder);
                }
            }
        }

        /// <summary>
        /// Writes the scene's current in-memory state to a temporary asset with SaveScene(saveAsCopy: true), which is
        /// documented to leave the open scene's path and unsaved-changes flag untouched (the native implementation is not
        /// in the C# reference source, so both are checked afterwards). Scene-saving callbacks of other packages run
        /// exactly as for Ctrl+S. Returns the temp asset path, or null on failure.
        /// </summary>
        static string SaveSceneCopy(Scene scene, string id, int index, ref bool tempCreated, CctResult r)
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            if (!EnsureTempFolder(ref tempCreated, r)) return null;
            var tempPath = TempFolder + "/" + id + "-" + index.ToString("D5", CultureInfo.InvariantCulture) + ".unity";
            var pathBefore = scene.path;
            var dirtyBefore = scene.isDirty;
            bool ok;
            try { ok = EditorSceneManager.SaveScene(scene, tempPath, true); }
            catch (Exception e)
            {
                ok = false;
                r.AddIssue(CctSeverity.Warning, "SCENE_CAPTURE_FAILED", $"シーン '{pathBefore}' の一時保存で例外: {e.Message}", asset: pathBefore);
            }
            if (scene.path != pathBefore || scene.isDirty != dirtyBefore)
                r.AddIssue(CctSeverity.Warning, "SCENE_STATE_CHANGED",
                    $"一時コピー保存でシーンの状態が変わりました (path: '{pathBefore}' → '{scene.path}', 未保存: {dirtyBefore} → {scene.isDirty})", asset: pathBefore);
            if (ok && File.Exists(AbsOf(tempPath))) return tempPath;
            if (File.Exists(AbsOf(tempPath))) DeleteTempScene(tempPath, r);
            return null;
        }

        static bool EnsureTempFolder(ref bool created, CctResult r)
        {
            if (created) return true;
            if (AssetDatabase.IsValidFolder(TempFolder) || Directory.Exists(AbsOf(TempFolder)))
            {
                // Not created by this run (e.g. left by an editor crash mid-capture): usable, but never removed by the tool.
                try
                {
                    bool reported = r.Issues.Any(i => (i.TryGetValue("code", out var c) ? c as string : null) == "TEMP_CLEANUP_FAILED");
                    if (!reported && Directory.EnumerateFileSystemEntries(AbsOf(TempFolder)).Any())
                        r.AddIssue(CctSeverity.Warning, "TEMP_CLEANUP_FAILED",
                            $"以前の中断で一時フォルダ '{TempFolder}' に一時シーンが残っています。このツールは自分で作っていないフォルダを削除しないため、不要なら Unity 上で削除してください", asset: TempFolder);
                }
                catch (Exception) { /* informational */ }
                return true;
            }
            string guid = null;
            try { guid = AssetDatabase.CreateFolder("Assets", TempFolderName); }
            catch (Exception) { /* reported below */ }
            if (string.IsNullOrEmpty(guid) || !AssetDatabase.IsValidFolder(TempFolder))
            {
                r.AddIssue(CctSeverity.Warning, "SCENE_CAPTURE_FAILED", $"一時フォルダ '{TempFolder}' を作成できませんでした");
                return false;
            }
            created = true;
            return true;
        }

        /// <summary>Only ever deletes this tool's own temporary scene copy.</summary>
        static void DeleteTempScene(string tempPath, CctResult r)
        {
            if (!IsInTempFolder(tempPath)) return;
            bool ok = false;
            try { ok = AssetDatabase.DeleteAsset(tempPath); }
            catch (Exception) { ok = false; }
            if (ok && !File.Exists(AbsOf(tempPath))) return;
            TryDeleteFile(AbsOf(tempPath));
            TryDeleteFile(AbsOf(tempPath) + ".meta");
            if (File.Exists(AbsOf(tempPath)))
                r.AddIssue(CctSeverity.Warning, "TEMP_CLEANUP_FAILED", $"一時シーン '{tempPath}' を削除できませんでした。手動で削除してください", asset: tempPath);
        }

        static void CleanupTempFolder(bool created, CctResult r)
        {
            if (!created) return;
            var abs = AbsOf(TempFolder);
            try
            {
                if (!Directory.Exists(abs)) return;
                if (Directory.EnumerateFileSystemEntries(abs).Any())
                {
                    r.AddIssue(CctSeverity.Warning, "TEMP_CLEANUP_FAILED", $"一時フォルダ '{TempFolder}' が空でないため残しました", asset: TempFolder);
                    return;
                }
                if (!AssetDatabase.DeleteAsset(TempFolder))
                    r.AddIssue(CctSeverity.Warning, "TEMP_CLEANUP_FAILED", $"一時フォルダ '{TempFolder}' を削除できませんでした", asset: TempFolder);
            }
            catch (Exception e)
            {
                r.AddIssue(CctSeverity.Warning, "TEMP_CLEANUP_FAILED", $"一時フォルダ '{TempFolder}' の削除で例外: {e.Message}", asset: TempFolder);
            }
        }
    }
}
