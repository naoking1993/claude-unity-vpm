using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    public static partial class CctDependencyReport
    {
        /// <summary>Editable Unity-serialized types that are loaded and SerializedObject-scanned for Missing references.</summary>
        static readonly HashSet<string> LoadScanExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".prefab", ".mat", ".controller", ".overrideController", ".anim", ".asset", ".mask", ".playable", ".signal",
            ".physicMaterial", ".physicsMaterial2D", ".lighting", ".preset", ".mixer", ".spriteatlas", ".terrainlayer",
        };

        /// <summary>Types stored as Unity YAML under Force Text; a binary one means the GUID scan missed it.</summary>
        static readonly HashSet<string> UnityYamlExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".unity", ".prefab", ".mat", ".controller", ".overrideController", ".anim", ".asset", ".mask", ".playable", ".signal",
            ".physicMaterial", ".physicsMaterial2D", ".lighting", ".preset", ".mixer", ".spriteatlas", ".terrainlayer",
            ".renderTexture", ".cubemap", ".flare", ".brush", ".guiskin", ".fontsettings", ".giparams", ".shadervariants",
        };

        /// <summary>Files that cannot depend on other assets (skipped when building the direct-referrer graph).</summary>
        static readonly HashSet<string> LeafExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".bmp", ".gif", ".exr", ".hdr", ".dds",
            ".wav", ".mp3", ".ogg", ".aif", ".aiff", ".flac", ".m4a", ".mp4", ".mov", ".webm", ".avi",
            ".ttf", ".otf", ".cs", ".dll", ".hlsl", ".cginc", ".txt", ".json", ".bytes",
        };

        const int MaxLoadScanFiles = 1000;
        const int MaxReferrerNodes = 3000;
        const int MaxPerInputDependencyCalls = 200;
        const int MaxLargeFileIssues = 20;

        sealed class DepFile
        {
            public string Path;
            public string Type;
            public long Bytes;
            public bool IsScript;
            public bool Inside;
        }

        sealed class PackageUse
        {
            public string Name;
            public int Files;
            public int Scripts;
            public readonly HashSet<string> Assemblies = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<string> Samples = new List<string>();
            public readonly List<string> ReferencingInputs = new List<string>();
            public int ReferencedBy;
        }

        sealed class MissingGuid
        {
            public string Guid;
            public int Count;
            public readonly HashSet<string> Files = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<object> Refs = new List<object>();
        }

        sealed class LoadFinding
        {
            public string Asset;
            public int Count;
            public readonly List<string> Samples = new List<string>();

            public void Add(string sample)
            {
                Count++;
                if (Samples.Count < 10) Samples.Add(sample);
            }

            public Dictionary<string, object> ToPlain() => new Dictionary<string, object>
            {
                ["asset"] = Asset,
                ["count"] = Count,
                ["examples"] = Samples.ToList(),
            };
        }

        static CctResult RunDependencies(CctArgs args)
        {
            var r = CctResult.Ok("");
            var ctx = new Context(r, args);
            r.Put("mode", ModeDependencies);
            bool includeScripts = args.GetBool("include_scripts", true);
            var largeMb = args.GetDouble("large_file_mb", DefaultLargeFileMb);
            long largeBytes = largeMb <= 0 || double.IsNaN(largeMb) ? long.MaxValue : (long)(largeMb * 1048576);
            var declared = args.GetStringList("declared_packages");

            // ---- inputs: asset paths/folders/GUIDs, plus the assets a scene target references directly
            var targetSpec = args.GetString("target", null);
            bool hasTarget = !string.IsNullOrWhiteSpace(targetSpec);
            var inputs = ResolveInputs(ctx, args.GetStringList("assets"), !hasTarget);
            var targetAssets = new List<string>();
            var targetBuiltin = 0;
            if (hasTarget)
            {
                var res = CctTargets.Resolve(targetSpec);
                if (!res.Ok)
                    return FailWith(r, res.Error, "TARGET_NOT_FOUND", res.Candidates.Count > 0 ? res.Candidates.Cast<object>().ToList() : null);
                r.Put("target", CctTargets.Describe(res.GameObject));
                targetAssets = CollectHierarchyAssets(res.GameObject, out targetBuiltin);
                r.Put("target_assets", Cap(targetAssets, ctx.MaxEntries, out var taOmitted));
                if (taOmitted > 0) r.Put("target_assets_omitted", taOmitted);
            }
            var depRoots = inputs.Files.Concat(targetAssets).Distinct(StringComparer.Ordinal).ToList();
            if (depRoots.Count == 0)
                return FailWith(r, inputs.FromSelection || args.GetStringList("assets").Count > 0
                    ? "解析できるアセットがありません (指定したパスを確認してください)"
                    : "assets (アセットパス / フォルダー / GUID) か target を指定するか、Project ウィンドウでアセットを選択してください", "NO_INPUT");
            var rootSet = new HashSet<string>(depRoots, StringComparer.Ordinal);

            // ---- export roots (explicit, else the input folders)
            var exportRoots = new List<string>();
            foreach (var raw in args.GetStringList("export_root"))
            {
                var p = CanonicalAssetPath(raw, out var isFolder);
                if (p == null || !isFolder)
                    return FailWith(r, $"export_root '{raw}' は存在するフォルダーではありません", "INVALID_ARGUMENT");
                if (!exportRoots.Contains(p)) exportRoots.Add(p);
            }
            if (exportRoots.Count == 0) exportRoots.AddRange(inputs.Folders);
            bool haveRoot = exportRoots.Count > 0;
            bool InExport(string path) => inputs.FileSet.Contains(path) || exportRoots.Any(root => IsUnder(path, root));

            SerializationIsForceText(ctx);

            // ---- dependencies
            string[] deps;
            try { deps = AssetDatabase.GetDependencies(depRoots.ToArray(), true); }
            catch (Exception e) { return FailWith(r, "AssetDatabase.GetDependencies に失敗しました: " + e.Message, "DEPENDENCIES_FAILED"); }
            var depList = deps.Concat(depRoots).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

            var inside = new List<DepFile>();
            var outside = new List<DepFile>();
            var packages = new Dictionary<string, PackageUse>(StringComparer.Ordinal);
            var builtinPaths = new List<string>();
            var byType = new Dictionary<string, long[]>(StringComparer.Ordinal);
            var scripts = new List<object>();
            var asmdefs = new Dictionary<string, bool>(StringComparer.Ordinal);
            var large = new List<DepFile>();
            int scriptsExcluded = 0;

            using (var progress = new Progress("Claude Creator Tools: 依存関係を分類中", depList.Count, 300))
            {
                for (int i = 0; i < depList.Count; i++)
                {
                    var d = depList[i];
                    progress.Step(i, d);
                    var ext = Path.GetExtension(d);
                    bool isScript = string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase);
                    if (isScript) CheckScript(ctx, d, ext, scripts, asmdefs, InExport);

                    if (d.StartsWith("Assets/", StringComparison.Ordinal))
                    {
                        if (isScript && !includeScripts && !rootSet.Contains(d)) { scriptsExcluded++; continue; }
                        var f = new DepFile { Path = d, Type = TypeNameOf(d), Bytes = FileSize(ctx.ToPhysical(d)), IsScript = isScript };
                        f.Inside = InExport(d);
                        (f.Inside ? inside : outside).Add(f);
                        if (!byType.TryGetValue(f.Type, out var tb)) byType[f.Type] = tb = new long[2];
                        tb[0]++;
                        tb[1] += Math.Max(0, f.Bytes);
                        if (f.Bytes > largeBytes) large.Add(f);
                        if (string.Equals(ext, ".unity", StringComparison.OrdinalIgnoreCase) && !rootSet.Contains(d))
                            ctx.Issue(CctSeverity.Warning, "SCENE_IN_DEPENDENCIES",
                                "シーンファイルが依存に含まれています。ギミック/衣装の書き出しにシーン (アバター本体を含むことが多い) を同梱しないでください", asset: d);
                    }
                    else if (d.StartsWith("Packages/", StringComparison.Ordinal))
                    {
                        var name = PackageNameOf(d);
                        if (!packages.TryGetValue(name, out var pu)) packages[name] = pu = new PackageUse { Name = name };
                        pu.Files++;
                        if (isScript) pu.Scripts++;
                        if (pu.Samples.Count < 5) pu.Samples.Add(d);
                        if (isScript)
                        {
                            var asm = AssemblyOf(d, ext);
                            if (!string.IsNullOrEmpty(asm)) pu.Assemblies.Add(asm);
                        }
                    }
                    else builtinPaths.Add(d);
                }
            }

            // ---- direct-referrer graph (who pulls each file in) and per-input package usage
            var referrers = BuildReferrers(ctx, depRoots, depList);
            if (depRoots.Count <= MaxPerInputDependencyCalls)
            {
                foreach (var root in depRoots)
                {
                    string[] own;
                    try { own = AssetDatabase.GetDependencies(root, true); }
                    catch (Exception) { continue; }
                    foreach (var name in own.Select(PackageNameOf).Where(n => n != null).Distinct(StringComparer.Ordinal))
                    {
                        if (!packages.TryGetValue(name, out var pu)) continue;
                        pu.ReferencedBy++;
                        if (pu.ReferencingInputs.Count < 3) pu.ReferencingInputs.Add(root);
                    }
                }
            }

            // ---- GUID scan of text-serialized files (+ .meta) for references that resolve to nothing
            var stats = new ScanStats();
            var missingGuids = new Dictionary<string, MissingGuid>(StringComparer.Ordinal);
            var builtinRefs = new Dictionary<string, int>(StringComparer.Ordinal);
            var scanList = depRoots.Concat(depList.Where(p => p.StartsWith("Assets/", StringComparison.Ordinal)))
                .Distinct(StringComparer.Ordinal).ToList();
            ScanForMissingGuids(ctx, scanList, rootSet, stats, missingGuids, builtinRefs);

            // ---- load-based scan: Missing Script / Missing Prefab / Missing references (also wrong fileIDs)
            var missingScripts = new List<LoadFinding>();
            var missingPrefabs = new List<LoadFinding>();
            var missingRefs = new List<LoadFinding>();
            var unsaved = new List<string>();
            int loadScanned = LoadScan(ctx, scanList, rootSet, missingScripts, missingPrefabs, missingRefs, unsaved, out bool loadCapped);
            ScanLoadedInputScenes(ctx, depRoots, missingScripts, unsaved);
            CheckPrefabStage(depList, unsaved);
            foreach (var u in unsaved.Distinct(StringComparer.Ordinal))
                ctx.Issue(CctSeverity.Warning, "UNSAVED_CHANGES",
                    "未保存の変更があります。依存関係と GUID 走査はディスク上の保存済み内容に基づくため、保存してから再実行してください", asset: u);

            // ---- issues + output
            ReportMissing(ctx, missingGuids, missingScripts, missingPrefabs, missingRefs);
            var outsideGroups = ReportOutside(ctx, outside, haveRoot, referrers, InExport);
            var packageRows = ReportPackages(ctx, packages, declared, referrers, InExport, depRoots.Count <= MaxPerInputDependencyCalls);
            foreach (var kv in asmdefs.Where(kv => !kv.Value).Take(MaxIssuesPerCode))
                ctx.Issue(CctSeverity.Warning, "ASMDEF_REQUIRED",
                    "依存スクリプトはこの Assembly Definition でコンパイルされます。書き出し範囲外なので一緒に同梱してください (GetDependencies には現れません)", asset: kv.Key);
            foreach (var f in large.OrderByDescending(x => x.Bytes).Take(MaxLargeFileIssues))
                ctx.Issue(CctSeverity.Info, "LARGE_FILE", $"大きなファイル {Fmt(Mb(f.Bytes))} MB ({f.Type}): 書き出しサイズに影響します",
                    value: Mb(f.Bytes), asset: f.Path);
            if (!haveRoot)
                ctx.Issue(CctSeverity.Info, "NO_EXPORT_ROOT",
                    "export_root (書き出すフォルダー) が指定されていないため、範囲内/範囲外の判定はしていません (入力ファイルだけを範囲内として集計)");
            ReportScanStats(ctx, stats, loadCapped, depList.Count);

            long insideBytes = inside.Sum(f => Math.Max(0, f.Bytes));
            long outsideBytes = outside.Sum(f => Math.Max(0, f.Bytes));
            r.Put("inputs", Cap(inputs.Files.Select(p => new Dictionary<string, object> { ["path"] = p, ["type"] = TypeNameOf(p) }), ctx.MaxEntries, out var inOmitted));
            r.Put("input_count", inputs.Files.Count);
            if (inOmitted > 0) r.Put("inputs_omitted", inOmitted);
            if (inputs.FromFolders > 0) r.Put("inputs_from_folders", inputs.FromFolders);
            if (inputs.FromSelection) r.Put("inputs_from_selection", true);
            r.Put("export_roots", exportRoots.ToList());
            r.Put("totals", new Dictionary<string, object>
            {
                ["dependencies"] = depList.Count,
                ["inside_export_root"] = inside.Count,
                ["assets_outside_root"] = outside.Count,
                ["package_files"] = packages.Values.Sum(p => p.Files),
                ["packages"] = packages.Count,
                ["builtin"] = builtinPaths.Count + targetBuiltin,
                ["inside_mb"] = Mb(insideBytes),
                ["outside_mb"] = Mb(outsideBytes),
                ["assets_total_mb"] = Mb(insideBytes + outsideBytes),
                ["scripts_excluded"] = scriptsExcluded,
            });
            r.Put("by_type", byType.OrderByDescending(kv => kv.Value[1]).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (object)new Dictionary<string, object> { ["type"] = kv.Key, ["count"] = kv.Value[0], ["mb"] = Mb(kv.Value[1]) }).ToList());
            r.Put("inside_export_root", new Dictionary<string, object>
            {
                ["count"] = inside.Count,
                ["mb"] = Mb(insideBytes),
                ["folders"] = inside.GroupBy(f => TopFolder(f.Path), StringComparer.Ordinal).OrderByDescending(g => g.Count())
                    .Take(ctx.MaxEntries)
                    .Select(g => (object)new Dictionary<string, object> { ["folder"] = g.Key, ["count"] = g.Count(), ["mb"] = Mb(g.Sum(f => Math.Max(0, f.Bytes))) })
                    .ToList(),
            });
            r.Put("inside_files", Cap(inside.Select(FileRow), ctx.MaxEntries, out var insideOmitted));
            if (insideOmitted > 0) r.Put("inside_files_omitted", insideOmitted);
            r.Put("assets_outside_root", Cap(outsideGroups, ctx.MaxEntries, out var groupsOmitted));
            if (groupsOmitted > 0) r.Put("assets_outside_root_omitted", groupsOmitted);
            r.Put("packages", packageRows.Item1);
            r.Put("required_packages", packageRows.Item2);
            r.Put("builtin", new Dictionary<string, object>
            {
                ["dependency_paths"] = builtinPaths.Distinct(StringComparer.Ordinal).ToList(),
                ["yaml_references"] = builtinRefs.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            });
            r.Put("missing_guids", Cap(missingGuids.Values.OrderByDescending(m => m.Files.Count).Select(MissingRow), ctx.MaxEntries, out var mgOmitted));
            if (mgOmitted > 0) r.Put("missing_guids_omitted", mgOmitted);
            r.Put("missing_scripts", Cap(missingScripts.Select(m => m.ToPlain()), ctx.MaxEntries, out _));
            r.Put("missing_prefabs", Cap(missingPrefabs.Select(m => m.ToPlain()), ctx.MaxEntries, out _));
            r.Put("missing_references", Cap(missingRefs.Select(m => m.ToPlain()), ctx.MaxEntries, out _));
            r.Put("scripts", Cap(scripts, ctx.MaxEntries, out var scriptsOmitted));
            if (scriptsOmitted > 0) r.Put("scripts_omitted", scriptsOmitted);
            r.Put("asmdefs", asmdefs.Select(kv => (object)new Dictionary<string, object> { ["path"] = kv.Key, ["inside_export"] = kv.Value }).ToList());
            r.Put("large_files", large.OrderByDescending(f => f.Bytes).Take(ctx.MaxEntries).Select(FileRow).ToList());
            var scan = stats.ToPlain(ctx.MaxEntries);
            scan["assets_load_scanned"] = loadScanned;
            scan["load_scan_capped"] = loadCapped;
            scan["referrer_graph_nodes"] = referrers.Nodes;
            r.Put("scan", scan);
            if (unsaved.Count > 0) r.Put("unsaved", unsaved.Distinct(StringComparer.Ordinal).ToList());
            ctx.FlushIssueCaps();

            int missingScriptCount = missingScripts.Sum(m => m.Count);
            r.Message = $"依存 {depList.Count} 件: 範囲内 {inside.Count} ({Fmt(Mb(insideBytes))} MB) / 範囲外 {outside.Count} ({Fmt(Mb(outsideBytes))} MB) / " +
                        $"パッケージ {packages.Count} 種 / 組み込み {builtinPaths.Count}。欠落 GUID {missingGuids.Count} / Missing Script {missingScriptCount} / " +
                        $"Missing 参照 {missingRefs.Sum(m => m.Count)}";
            return r;
        }

        static object FileRow(DepFile f) => new Dictionary<string, object>
        {
            ["path"] = f.Path,
            ["type"] = f.Type,
            ["mb"] = Mb(f.Bytes),
        };

        static object MissingRow(MissingGuid m) => new Dictionary<string, object>
        {
            ["guid"] = m.Guid,
            ["fields"] = m.Keys.Take(10).ToList(),
            ["file_count"] = m.Files.Count,
            ["reference_count"] = m.Count,
            ["references"] = m.Refs.ToList(),
        };

        /// <summary>
        /// Assets referenced directly by a scene hierarchy (SerializedObject walk, m_Script included), plus the source prefab
        /// of every prefab instance root inside it (those stay nested when the hierarchy is saved as a prefab). Uses
        /// GetAssetPath(instanceID) so referenced assets are not loaded just to be listed.
        /// </summary>
        static List<string> CollectHierarchyAssets(GameObject root, out int builtinCount)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var seenIds = new HashSet<int>();
            long visits = 0;
            int builtin = 0;
            void AddPath(string path)
            {
                if (string.IsNullOrEmpty(path)) return;
                if (!CctPaths.IsAssetPath(path)) { builtin++; return; }
                if (seen.Add(path)) list.Add(path);
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;
                try
                {
                    if (PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject))
                        AddPath(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject));
                }
                catch (Exception) { /* not a prefab instance */ }
                foreach (var c in t.gameObject.GetComponents<Component>())
                {
                    if (c == null) continue;
                    WalkReferences(c, true, p =>
                    {
                        var id = p.objectReferenceInstanceIDValue;
                        if (id == 0 || !seenIds.Add(id)) return;
                        AddPath(AssetDatabase.GetAssetPath(id));
                    }, ref visits);
                }
            }
            builtinCount = builtin;
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        static void CheckScript(Context ctx, string path, string ext, List<object> scripts, Dictionary<string, bool> asmdefs, Func<string, bool> inExport)
        {
            var row = new Dictionary<string, object> { ["path"] = path };
            var asm = AssemblyOf(path, ext);
            if (!string.IsNullOrEmpty(asm)) row["assembly"] = asm;
            var pkg = PackageNameOf(path);
            if (pkg != null) row["package"] = pkg;
            if (string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                    if (ms != null && ms.GetClass() == null)
                    {
                        row["class_found"] = false;
                        ctx.Issue(CctSeverity.Warning, "SCRIPT_CLASS_NOT_FOUND",
                            "依存スクリプトのクラスを解決できません (コンパイルエラー、またはクラス名とファイル名の不一致)。このスクリプトを使うコンポーネントは Missing Script になります", asset: path);
                    }
                }
                catch (Exception) { /* unloadable script: the GUID scan / load scan reports the effect */ }
                if (path.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    try
                    {
                        var asmdef = CompilationPipeline.GetAssemblyDefinitionFilePathFromScriptPath(path);
                        if (!string.IsNullOrEmpty(asmdef))
                        {
                            asmdef = CctPaths.Normalize(asmdef);
                            row["asmdef"] = asmdef;
                            if (asmdef.StartsWith("Assets/", StringComparison.Ordinal) && !asmdefs.ContainsKey(asmdef))
                                asmdefs[asmdef] = inExport(asmdef);
                        }
                    }
                    catch (Exception) { /* no asmdef info */ }
                }
            }
            scripts.Add(row);
        }

        static string AssemblyOf(string path, string ext)
        {
            if (string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase)) return Path.GetFileNameWithoutExtension(path);
            try
            {
                var name = CompilationPipeline.GetAssemblyNameFromScriptPath(path);
                return string.IsNullOrEmpty(name) ? null : (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name);
            }
            catch (Exception) { return null; }
        }

        sealed class Referrers
        {
            public readonly Dictionary<string, List<string>> Map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            public int Nodes;
            public bool Capped;

            public IEnumerable<string> Of(string path) =>
                Map.TryGetValue(path, out var l) ? l : Enumerable.Empty<string>();
        }

        /// <summary>Direct (non-recursive) dependency edges reversed: which file in the set pulls each dependency in.</summary>
        static Referrers BuildReferrers(Context ctx, List<string> roots, List<string> deps)
        {
            var result = new Referrers();
            var nodes = roots.Concat(deps.Where(p => p.StartsWith("Assets/", StringComparison.Ordinal)))
                .Distinct(StringComparer.Ordinal)
                .Where(p => !LeafExtensions.Contains(Path.GetExtension(p)))
                .ToList();
            if (nodes.Count > MaxReferrerNodes)
            {
                result.Capped = true;
                nodes = nodes.Take(MaxReferrerNodes).ToList();
            }
            using (var progress = new Progress("Claude Creator Tools: 参照元を集計中", nodes.Count, 200))
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    progress.Step(i, node);
                    string[] direct;
                    try { direct = AssetDatabase.GetDependencies(node, false); }
                    catch (Exception) { continue; }
                    result.Nodes++;
                    foreach (var d in direct)
                    {
                        if (d == node) continue;
                        if (!result.Map.TryGetValue(d, out var list)) result.Map[d] = list = new List<string>();
                        if (list.Count < 20) list.Add(node);
                    }
                }
            }
            return result;
        }

        static void ScanForMissingGuids(Context ctx, List<string> files, HashSet<string> roots, ScanStats stats,
            Dictionary<string, MissingGuid> missing, Dictionary<string, int> builtinRefs)
        {
            string current = null;
            HexHandler handler = (hex, isRef, line, key) =>
            {
                if (!isRef) return;
                if (BuiltinGuids.TryGetValue(hex, out var builtinName))
                {
                    builtinRefs.TryGetValue(builtinName, out var n);
                    builtinRefs[builtinName] = n + 1;
                    return;
                }
                if (ctx.GuidResolves(hex)) return;
                if (!missing.TryGetValue(hex, out var m)) missing[hex] = m = new MissingGuid { Guid = hex };
                m.Count++;
                m.Files.Add(current);
                if (!string.IsNullOrEmpty(key)) m.Keys.Add(key);
                if (m.Refs.Count < 10)
                    m.Refs.Add(new Dictionary<string, object> { ["file"] = current, ["line"] = line, ["field"] = key });
            };

            using (var progress = new Progress("Claude Creator Tools: GUID 参照を走査中", files.Count))
            {
                for (int i = 0; i < files.Count; i++)
                {
                    var path = files[i];
                    progress.Step(i, path);
                    var ext = Path.GetExtension(path);
                    var physical = ctx.ToPhysical(path);
                    long limit = roots.Contains(path) ? 0 : ctx.MaxFileBytes;
                    if (NoReferenceExtensions.Contains(ext) || CodeExtensions.Contains(ext)) stats.SkippedByType++;
                    else
                    {
                        current = path;
                        var st = ScanFile(physical, limit, true, true, handler, out var len, out var err);
                        CountStatus(stats, st, path, ext, false, len, err);
                    }
                    if (!MetaMayHoldReferences(ext)) continue;
                    current = path + ".meta";
                    var mst = ScanFile(physical + ".meta", 0, true, true, handler, out var mlen, out var merr);
                    if (mst != ScanStatus.NotFound) CountStatus(stats, mst, current, ".meta", true, mlen, merr);
                }
            }
        }

        static void CountStatus(ScanStats stats, ScanStatus st, string path, string ext, bool isMeta, long len, string err)
        {
            if ((st == ScanStatus.Binary || st == ScanStatus.NotUnityText) && !isMeta && !UnityYamlExtensions.Contains(ext))
            {
                stats.SkippedByType++; // TextAsset / binary data: holds no Unity object references
                return;
            }
            if (st == ScanStatus.NotFound) return;
            stats.Count(st, path, isMeta, len, err);
        }

        /// <summary>Loads prefabs and editable assets (read-only) and checks for Missing Script / Prefab / references.</summary>
        static int LoadScan(Context ctx, List<string> files, HashSet<string> roots, List<LoadFinding> missingScripts,
            List<LoadFinding> missingPrefabs, List<LoadFinding> missingRefs, List<string> unsaved, out bool capped)
        {
            var list = files.Where(p => LoadScanExtensions.Contains(Path.GetExtension(p)) &&
                                        (roots.Contains(p) || p.StartsWith("Assets/", StringComparison.Ordinal))).ToList();
            capped = list.Count > MaxLoadScanFiles;
            if (capped) list = list.Take(MaxLoadScanFiles).ToList();
            int scanned = 0;
            long visits = 0;
            using (var progress = new Progress("Claude Creator Tools: Missing 参照を確認中", list.Count))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var path = list[i];
                    progress.Step(i, path);
                    try
                    {
                        if (string.Equals(Path.GetExtension(path), ".prefab", StringComparison.OrdinalIgnoreCase))
                            ScanPrefabAsset(path, missingScripts, missingPrefabs, missingRefs, unsaved, ref visits);
                        else
                            ScanAssetObjects(path, missingRefs, unsaved, ref visits);
                        scanned++;
                    }
                    catch (Exception e)
                    {
                        ctx.Issue(CctSeverity.Warning, "SCAN_INCOMPLETE", $"アセットを読み込めず Missing 確認できませんでした: {e.GetType().Name}: {e.Message}", asset: path);
                    }
                }
            }
            return scanned;
        }

        static void ScanPrefabAsset(string path, List<LoadFinding> missingScripts, List<LoadFinding> missingPrefabs,
            List<LoadFinding> missingRefs, List<string> unsaved, ref long visits)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null) return;
            if (EditorUtility.IsDirty(root)) unsaved.Add(path);
            var ms = new LoadFinding { Asset = path };
            var mp = new LoadFinding { Asset = path };
            var mr = new LoadFinding { Asset = path };
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;
                var go = t.gameObject;
                var rel = t == root.transform ? root.name : root.name + "/" + CctTargets.GetRelativePath(root.transform, t);
                int m = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (m > 0)
                {
                    ms.Count += m - 1;
                    ms.Add(m > 1 ? $"{rel} ({m})" : rel);
                }
                try
                {
                    if (PrefabUtility.IsPrefabAssetMissing(go)) mp.Add(rel);
                }
                catch (Exception) { /* API refused this object: skip */ }
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var comp = c;
                    WalkReferences(c, false, p =>
                    {
                        if (IsMissing(p)) mr.Add($"{rel} ({comp.GetType().Name}).{PrettyPropertyPath(p.propertyPath)}");
                    }, ref visits);
                }
            }
            if (ms.Count > 0) missingScripts.Add(ms);
            if (mp.Count > 0) missingPrefabs.Add(mp);
            if (mr.Count > 0) missingRefs.Add(mr);
        }

        static void ScanAssetObjects(string path, List<LoadFinding> missingRefs, List<string> unsaved, ref long visits)
        {
            var objs = AssetDatabase.LoadAllAssetsAtPath(path);
            if (objs == null) return;
            var mr = new LoadFinding { Asset = path };
            bool dirty = false;
            foreach (var o in objs)
            {
                if (o == null || o is GameObject || o is Component) continue;
                if (!dirty && EditorUtility.IsDirty(o)) dirty = true;
                var owner = o;
                WalkReferences(o, false, p =>
                {
                    if (IsMissing(p))
                        mr.Add($"{owner.GetType().Name} '{owner.name}'.{PrettyPropertyPath(p.propertyPath)}");
                }, ref visits);
            }
            if (dirty) unsaved.Add(path);
            if (mr.Count > 0) missingRefs.Add(mr);
        }

        /// <summary>Input scenes that are open: count Missing Scripts in memory (GetDependencies only sees the saved file).</summary>
        static void ScanLoadedInputScenes(Context ctx, List<string> roots, List<LoadFinding> missingScripts, List<string> unsaved)
        {
            var rootSet = new HashSet<string>(roots, StringComparer.Ordinal);
            foreach (var scene in CctTargets.LoadedScenes())
            {
                if (string.IsNullOrEmpty(scene.path) || !rootSet.Contains(scene.path)) continue;
                if (scene.isDirty) unsaved.Add(scene.path);
                var ms = new LoadFinding { Asset = scene.path };
                foreach (var go in AllGameObjects(scene))
                {
                    int m = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                    if (m <= 0) continue;
                    ms.Count += m - 1;
                    ms.Add(m > 1 ? $"{CctTargets.GetPath(go)} ({m})" : CctTargets.GetPath(go));
                }
                if (ms.Count > 0) missingScripts.Add(ms);
            }
        }

        static void CheckPrefabStage(List<string> deps, List<string> unsaved)
        {
            ScenesToScan(out var stagePath);
            if (string.IsNullOrEmpty(stagePath) || !deps.Contains(stagePath)) return;
            try
            {
                var stage = UnityEditor.SceneManagement.StageUtility.GetCurrentStage() as UnityEditor.SceneManagement.PreviewSceneStage;
                if (stage != null && stage.scene.IsValid() && stage.scene.isDirty) unsaved.Add(stagePath);
            }
            catch (Exception) { /* no stage info */ }
        }

        static readonly Dictionary<string, string> FieldHints = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["m_Script"] = "スクリプト (Missing Script)",
            ["m_SourcePrefab"] = "ネストしたプレハブ (Missing Prefab)",
            ["m_Shader"] = "シェーダー",
            ["m_Texture"] = "テクスチャ",
            ["m_Materials"] = "マテリアル",
            ["m_Mesh"] = "メッシュ",
            ["m_Avatar"] = "Avatar",
            ["m_Controller"] = "Animator Controller",
            ["m_Motion"] = "モーション",
            ["m_Sprite"] = "スプライト",
            ["second"] = "インポート設定の外部オブジェクト (.meta)",
        };

        static void ReportMissing(Context ctx, Dictionary<string, MissingGuid> missingGuids, List<LoadFinding> missingScripts,
            List<LoadFinding> missingPrefabs, List<LoadFinding> missingRefs)
        {
            foreach (var m in missingGuids.Values.OrderByDescending(x => x.Files.Count))
            {
                var first = m.Refs.Count > 0 ? (Dictionary<string, object>)m.Refs[0] : null;
                var file = first != null ? first["file"] as string : m.Files.FirstOrDefault();
                var line = first != null ? first["line"] : null;
                var hint = string.Join(", ", m.Keys.Take(3).Select(k => FieldHints.TryGetValue(k, out var h) ? $"{k}: {h}" : k));
                var more = m.Files.Count > 1 ? $" ほか {m.Files.Count - 1} ファイル" : "";
                // Importer settings (.meta: script icons, remapped model materials) degrade gracefully; asset bodies break.
                bool onlyMeta = m.Files.All(f => f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase));
                ctx.Issue(onlyMeta ? CctSeverity.Warning : CctSeverity.Error, "MISSING_GUID",
                    $"GUID {m.Guid} の参照先がこのプロジェクトにありません ({(hint.Length > 0 ? hint : "参照")})。参照元 {file}:{line}{more}",
                    value: m.Guid, asset: file);
            }
            foreach (var f in missingScripts)
                ctx.Issue(CctSeverity.Error, "MISSING_SCRIPT",
                    $"Missing Script {f.Count} 個 (例: {string.Join(", ", f.Samples.Take(3))})。導入先でも壊れたコンポーネントになります", value: f.Count, asset: f.Asset);
            foreach (var f in missingPrefabs)
                ctx.Issue(CctSeverity.Error, "MISSING_PREFAB",
                    $"元プレハブが見つからないネストプレハブ {f.Count} 個 (例: {string.Join(", ", f.Samples.Take(3))})", value: f.Count, asset: f.Asset);
            foreach (var f in missingRefs)
                ctx.Issue(CctSeverity.Warning, "MISSING_REFERENCE",
                    $"Missing 参照 {f.Count} 個 (削除されたアセット、または GUID は有効でもサブアセットの fileID が無効)。例: {string.Join(", ", f.Samples.Take(3))}",
                    value: f.Count, asset: f.Asset);
        }

        static List<object> ReportOutside(Context ctx, List<DepFile> outside, bool haveRoot, Referrers referrers, Func<string, bool> inExport)
        {
            var rows = new List<object>();
            foreach (var g in outside.GroupBy(f => TopFolder(f.Path), StringComparer.Ordinal)
                         .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                var files = g.ToList();
                long bytes = files.Sum(f => Math.Max(0, f.Bytes));
                var from = files.SelectMany(f => referrers.Of(f.Path)).Where(inExport).Distinct(StringComparer.Ordinal).Take(5).ToList();
                if (from.Count == 0)
                    from = files.SelectMany(f => referrers.Of(f.Path)).Distinct(StringComparer.Ordinal).Take(5).ToList();
                var row = new Dictionary<string, object>
                {
                    ["folder"] = g.Key,
                    ["count"] = files.Count,
                    ["mb"] = Mb(bytes),
                    ["types"] = files.GroupBy(f => f.Type).OrderByDescending(x => x.Count()).Take(6)
                        .ToDictionary(x => x.Key, x => (object)x.Count()),
                    ["files"] = files.Take(10).Select(f => (object)f.Path).ToList(),
                    ["referenced_from"] = from.Cast<object>().ToList(),
                };
                if (files.Count > 10) row["files_omitted"] = files.Count - 10;
                rows.Add(row);
                if (haveRoot)
                    ctx.Issue(CctSeverity.Warning, "OUTSIDE_EXPORT_ROOT",
                        $"書き出し範囲外の依存 {files.Count} 件 ({Fmt(Mb(bytes))} MB): {g.Key}/" +
                        (from.Count > 0 ? $" (参照元 {from[0]})" : "") +
                        "。同梱するか、導入先に同じ GUID のアセットがあることを確認してください", path: g.Key, value: files.Count);
            }
            return rows;
        }

        static Tuple<List<object>, List<object>> ReportPackages(Context ctx, Dictionary<string, PackageUse> packages, List<string> declared,
            Referrers referrers, Func<string, bool> inExport, bool haveReferencedBy)
        {
            var detailed = new List<object>();
            var compact = new List<object>();
            var vpm = ctx.VpmManaged;
            foreach (var pu in packages.Values.OrderBy(p => p.Name.StartsWith("com.unity.", StringComparison.Ordinal) ? 1 : 0)
                         .ThenBy(p => p.Name, StringComparer.Ordinal))
            {
                var info = ctx.FindPackage(pu.Name);
                var source = info?.Source ?? "unknown";
                bool vpmManaged = vpm != null && vpm.Contains(pu.Name);
                var installedVia = vpmManaged ? "vpm" : source;
                var usedFrom = pu.Samples.SelectMany(referrers.Of).Where(p => !p.StartsWith("Packages/", StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal).Take(3).ToList();
                var row = new Dictionary<string, object>
                {
                    ["name"] = pu.Name,
                    ["display_name"] = info?.DisplayName,
                    ["version_installed"] = info?.Version,
                    ["installed_via"] = installedVia,
                    ["files"] = pu.Files,
                    ["scripts"] = pu.Scripts,
                    ["assemblies"] = pu.Assemblies.OrderBy(a => a, StringComparer.Ordinal).Cast<object>().ToList(),
                    ["examples"] = pu.Samples.Cast<object>().ToList(),
                };
                if (usedFrom.Count > 0) row["used_from"] = usedFrom.Cast<object>().ToList();
                if (haveReferencedBy) row["referencing_inputs"] = pu.ReferencingInputs.Cast<object>().ToList();
                detailed.Add(row);
                var c = new Dictionary<string, object>
                {
                    ["name"] = pu.Name,
                    ["version_installed"] = info?.Version,
                    ["installed_via"] = installedVia,
                    ["referenced_by"] = haveReferencedBy ? (object)pu.ReferencedBy : null,
                    ["files"] = pu.Files,
                };
                compact.Add(c);

                if (source == "builtin") continue; // com.unity.modules.*: part of every Unity install
                bool isDeclared = declared.Count > 0 && declared.Any(d => PackageMatches(pu.Name, d));
                string severity = CctSeverity.Info;
                string note = "";
                if (declared.Count > 0 && !isDeclared)
                {
                    severity = CctSeverity.Warning;
                    note = "。declared_packages に含まれていない外部依存です";
                }
                else if (vpm != null && !vpmManaged && (source == "embedded" || source == "local" || source == "unregistered"))
                {
                    severity = CctSeverity.Warning;
                    note = "。VPM (vpm-manifest.json) 管理外の埋め込みパッケージなので、導入先へは手動で入れる必要があります";
                }
                var label = string.IsNullOrEmpty(info?.DisplayName) ? pu.Name : $"{pu.Name} ({info.DisplayName})";
                var users = haveReferencedBy ? $"入力 {pu.ReferencedBy} 件から参照、" : "";
                ctx.Issue(severity, "PACKAGE_DEPENDENCY",
                    $"パッケージ {label} が必要です ({users}依存ファイル {pu.Files} 件、このプロジェクトの版 {info?.Version ?? "不明"} / {installedVia})。" +
                    "導入先にも同じパッケージを入れてください (版は検証済みの版で、最低対応版ではありません)" + note,
                    path: "Packages/" + pu.Name, value: info?.Version);
            }
            return Tuple.Create(detailed, compact);
        }

        /// <summary>"nadena.dev.modular-avatar" matches itself; "com.vrchat.*" matches by prefix.</summary>
        static bool PackageMatches(string name, string pattern)
        {
            pattern = pattern?.Trim();
            if (string.IsNullOrEmpty(pattern)) return false;
            if (pattern.EndsWith("*", StringComparison.Ordinal))
                return name.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.OrdinalIgnoreCase);
            return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
        }

        static void ReportScanStats(Context ctx, ScanStats stats, bool loadCapped, int depCount)
        {
            if (stats.TooLarge.Count > 0 || stats.Failed.Count > 0)
            {
                var examples = stats.TooLarge.Concat(stats.Failed).Take(5).ToList();
                ctx.Issue(CctSeverity.Warning, "SCAN_INCOMPLETE",
                    $"GUID 走査できなかったファイルがあります (大きすぎる {stats.TooLarge.Count} 件 / 読み込み失敗 {stats.Failed.Count} 件)。例: {string.Join(", ", examples)}" +
                    (stats.TooLarge.Count > 0 ? "。max_file_mb を増やすと対象になります" : ""),
                    value: stats.TooLarge.Count + stats.Failed.Count);
            }
            if (stats.Binary.Count > 0)
                ctx.Issue(CctSeverity.Info, "BINARY_FILES_SKIPPED",
                    $"バイナリ形式のアセット {stats.Binary.Count} 件は GUID 走査していません (例: {string.Join(", ", stats.Binary.Take(3))})。LightingData 等は常にバイナリです",
                    value: stats.Binary.Count);
            if (loadCapped)
                ctx.Issue(CctSeverity.Warning, "SCAN_INCOMPLETE",
                    $"Missing 参照の読み込み確認は先頭 {MaxLoadScanFiles} ファイルまでです (依存 {depCount} 件)。対象を絞ると全件確認できます");
        }
    }
}
