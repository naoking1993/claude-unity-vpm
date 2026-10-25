using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// cct_dependency_report: what an asset set needs and who uses an asset, for packaging a modified gimmick/outfit for
    /// another avatar or project (no Missing Script, no references back into the source scene, every reference resolving
    /// inside the export set / a declared package / Unity built-ins, the VPM packages it needs) and for "who uses this?"
    /// questions before deleting or replacing assets. Read-only in every mode: nothing is saved, imported or dirtied (the
    /// loaded scenes' dirty flags are compared before/after and a change is reported as SCENE_DIRTIED).
    ///
    /// Modes:
    ///   dependencies     (default) AssetDatabase.GetDependencies of 'assets' (folders expanded) and/or the assets referenced
    ///                    by a scene 'target', grouped into inside_export_root / assets_outside_root (by Assets/A/B) /
    ///                    packages + required_packages (installed version, VPM status, referenced_by) / builtin, with file
    ///                    sizes, a YAML GUID scan for references that do not resolve, and a load-based scan of prefabs and
    ///                    other editable assets for Missing Script / Missing references (also catches wrong fileIDs that a
    ///                    GUID scan cannot).
    ///   reverse          every file under search_root (default Assets) whose text (or .meta) contains the GUID of one of
    ///                    'assets', grouped by type, plus in-memory references from objects in the loaded scenes.
    ///   scene_references SerializedObject scan of every component under a scene 'target' for references to scene objects
    ///                    outside the target (they become None when the target is saved as a standalone prefab), Missing
    ///                    references/scripts/prefabs, references from outside into the target, and prefab-override status.
    ///
    /// Every long loop is bounded by a time budget of timeout_seconds - 5 (25 s by default; menu runs pass timeout_seconds 305)
    /// so an MCP call returns before its timeout; time_budget_seconds can only shorten it. When it runs out the remaining scans
    /// are skipped and SCAN_INCOMPLETE says where. The essential checks (GUID scan, Missing scan) run before the optional
    /// enrichment (referrer graph, per-input packages). Every mode outputs 'conclusive': false = some scan was partial, so a
    /// "nothing missing" / "nothing uses it" verdict must not be trusted.
    ///
    /// Side effects: nothing in Assets/, Packages/ or the scenes changes. The report JSON and, in dependencies mode, the complete
    /// dependency list (dependency_list_path: TSV of group, input, path, guid, type, bytes) are written under
    /// Library/ClaudeCreatorTools/Reports unless save_report=false. The Missing scan loads up to 1000 prefabs/assets (and whatever
    /// they reference) into editor memory like the Inspector would; Unity unloads them at the next UnloadUnusedAssets / scene change.
    ///
    /// Issue codes:
    /// | code                          | severity        | meaning                                                                          |
    /// |-------------------------------|-----------------|----------------------------------------------------------------------------------|
    /// | MISSING_GUID                  | error / warning | a text-serialized file references a GUID no asset in this project has (warning   |
    /// |                               |                 | when only .meta importer settings reference it, e.g. a script icon)              |
    /// | MISSING_SCRIPT                | error           | GameObject(s) with a MonoBehaviour whose script is missing                        |
    /// | MISSING_PREFAB                | error           | nested prefab instance whose prefab asset is missing                              |
    /// | MISSING_REFERENCE             | warning         | ObjectReference shown as "Missing" (deleted object, or GUID ok but wrong fileID)  |
    /// | SCRIPT_CLASS_NOT_FOUND        | warning / info  | dependency script whose class Unity cannot resolve (compile error / name mismatch)|
    /// |                               |                 | (info: an input script nothing in the set references, e.g. a utility class file) |
    /// | EXTERNAL_SCENE_REFERENCE      | error           | reference from inside target to a scene object outside it (lost in a prefab)      |
    /// | PATH_RESOLVED_REFERENCE       | info            | same, but a Modular Avatar AvatarObjectReference that MA re-resolves by path      |
    /// | SCENE_ONLY_OBJECT             | warning         | reference to a Mesh/Material/... that exists only in the scene or in memory       |
    /// | INBOUND_SCENE_REFERENCE       | info            | object outside target references into target (that link is not exported)          |
    /// | PREFAB_HAS_OVERRIDES          | info            | target is (part of) a prefab instance with overrides; the source prefab lacks them|
    /// | PART_OF_PREFAB_INSTANCE       | info            | target is a child inside a prefab instance (no overrides): save it as a new prefab|
    /// | OUTSIDE_EXPORT_ROOT           | warning         | dependency under Assets/ outside export_root (grouped by Assets/A/B)              |
    /// | ASMDEF_REQUIRED               | warning         | a script dependency compiles in an .asmdef outside the export set                 |
    /// | PACKAGE_DEPENDENCY            | info / warning  | dependency on a package (warning: not in declared_packages, or not VPM-managed)   |
    /// | SCENE_IN_DEPENDENCIES         | warning         | a .unity scene is pulled in as a dependency of the export set                     |
    /// | NO_EXPORT_ROOT                | info            | no export_root and no folder input: inside/outside classification skipped         |
    /// | LARGE_FILE                    | info            | dependency file larger than large_file_mb (export weight)                         |
    /// | BINARY_SERIALIZATION          | warning         | Asset Serialization is not Force Text: binary assets cannot be GUID-scanned       |
    /// | BINARY_FILES_SKIPPED          | info            | some files are binary (or not Unity YAML) and were not GUID-scanned               |
    /// | SCAN_INCOMPLETE               | warning         | files skipped (too large / unreadable), a cap or the time budget was hit, or an   |
    /// |                               |                 | asset could not be loaded; results may be partial                                 |
    /// | UNSAVED_CHANGES               | warning         | input/dependency has unsaved in-memory changes; file-based results show disk state|
    /// |                               |                 | (scene_references: the Prefab Mode root being checked is not saved yet)          |
    /// | INPUT_NOT_FOUND               | error           | requested asset path / GUID does not exist (skipped)                              |
    /// | TARGET_USED                   | info            | reverse: target is referenced by N assets (or only by other files of the target  |
    /// |                               |                 | set: deleting the whole set is fine, deleting just this file is not)             |
    /// | TARGET_UNUSED                 | info            | reverse: no reference from search_root, other targets or loaded scenes (caveats: |
    /// |                               |                 | binary files, name-based use from code; check 'conclusive')                      |
    /// | USED_IN_OPEN_SCENE            | info            | reverse: a loaded scene object references the target in memory                    |
    /// | NAME_BASED_REFERENCE_POSSIBLE | info            | reverse: shaders/scripts/Resources can be used by name from code (not detectable) |
    /// | PACKAGE_LIST_FAILED           | warning         | the Package Manager list could not be read; package versions may be missing       |
    /// | ISSUES_CAPPED                 | info            | more than 100 issues of one code: the rest are counted in issues_capped           |
    /// | FILE_LIST_WRITE_FAILED        | warning         | dependencies: the dependency list TSV could not be written (report still returned)|
    /// | SCENE_DIRTIED                 | warning         | a loaded scene became dirty during the run (should never happen; check the scene) |
    /// | PLAY_MODE                     | warning         | run in Play mode: scene objects are the runtime copies (e.g. after NDMF / Modular |
    /// |                               |                 | Avatar "Apply on Play"), not the saved/edit-mode scene                            |
    /// | INVALID_ARGUMENT              | (fail code)     | bad mode / export_root / search_root                                              |
    /// | NO_INPUT                      | (fail code)     | no usable asset (and no target) to analyse                                        |
    /// | TARGET_NOT_FOUND              | (fail code)     | target spec did not resolve to a GameObject                                       |
    /// | DEPENDENCIES_FAILED           | (fail code)     | AssetDatabase.GetDependencies threw                                               |
    /// </summary>
    public static partial class CctDependencyReport
    {
        public const string ToolName = "cct_dependency_report";

        public const string ModeDependencies = "dependencies";
        public const string ModeReverse = "reverse";
        public const string ModeSceneReferences = "scene_references";

        const int DefaultMaxEntries = 50;
        const double DefaultMaxFileMb = 50;
        const double DefaultLargeFileMb = 4;
        const int MaxIssuesPerCode = 100;
        const int TimeBudgetMarginSeconds = 5;

        /// <summary>
        /// timeout_seconds the menu items pass: no MCP call is waiting, but the progress bar cannot be cancelled, so the editor
        /// never freezes indefinitely (the scans stop after <see cref="MenuTimeBudgetSeconds"/>).
        /// </summary>
        internal const int MenuTimeoutSeconds = 305;
        internal const double MenuTimeBudgetSeconds = MenuTimeoutSeconds - TimeBudgetMarginSeconds;

        /// <summary>Japanese names of the time-budget phases for SCAN_INCOMPLETE (the code stays in time_budget.stopped_at).</summary>
        static readonly Dictionary<string, string> PhaseLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["target_scan"] = "対象オブジェクトの参照収集",
            ["guid_scan"] = "GUID 参照の走査",
            ["load_scan"] = "Missing の読み込み確認",
            ["referrer_graph"] = "参照元の集計",
            ["package_usage"] = "パッケージごとの使用元の集計",
            ["file_scan"] = "使用元ファイルの検索",
            ["scene_scan"] = "シーン内の参照確認",
            ["outbound_scan"] = "対象内の参照確認",
            ["inbound_scan"] = "対象への参照確認",
        };
        const int MaxVisitsPerObject = 200000;

        /// <summary>
        /// NDMF (installed with Modular Avatar) keeps this scene loaded additively for its preview: proxy renderers and bones with
        /// HideFlags.DontSave, named like the originals. It is never saved, so its objects are not users or exportable content.
        /// </summary>
        const string NdmfPreviewSceneName = "___NDMF Preview___";
        const int ArrayPeekThreshold = 64;

        /// <summary>GUIDs of Unity's built-in resource files: references to them always resolve.</summary>
        static readonly Dictionary<string, string> BuiltinGuids = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["0000000000000000e000000000000000"] = "Resources/unity_builtin_extra",
            ["0000000000000000f000000000000000"] = "Library/unity default resources",
            ["0000000000000000d000000000000000"] = "Library/unity editor resources",
            ["00000000000000000000000000000000"] = "(none)",
        };

        /// <summary>Main files of these types never hold GUID references (their .meta is still scanned).</summary>
        static readonly HashSet<string> NoReferenceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".bmp", ".gif", ".exr", ".hdr", ".iff", ".pict", ".dds", ".ktx",
            ".fbx", ".obj", ".blend", ".dae", ".3ds", ".max", ".ma", ".mb", ".dxf", ".c4d", ".vrm",
            ".wav", ".mp3", ".ogg", ".aif", ".aiff", ".flac", ".m4a", ".mod", ".it", ".s3m", ".xm",
            ".mp4", ".mov", ".webm", ".avi", ".m4v", ".mpg", ".mpeg", ".ogv", ".wmv",
            ".ttf", ".otf", ".ttc", ".dll", ".so", ".dylib", ".bundle", ".a", ".aar", ".jar", ".zip", ".7z", ".unitypackage", ".pdf",
        };

        /// <summary>Source code: excluded from reverse scans unless scan_code=true (references by GUID string are rare).</summary>
        static readonly HashSet<string> CodeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".shader", ".hlsl", ".cginc", ".glslinc", ".compute", ".raytrace", ".js", ".boo",
        };

        public static CctResult Run(CctArgs args)
        {
            var raw = args.GetString("mode", null);
            var mode = args.GetEnum("mode", ModeDependencies, ModeDependencies, ModeReverse, ModeSceneReferences);
            if (!string.IsNullOrWhiteSpace(raw) && !string.Equals(raw.Trim(), mode, StringComparison.OrdinalIgnoreCase))
                return CctResult.Fail($"mode '{raw}' は指定できません。dependencies / reverse / scene_references のいずれかを指定してください", "INVALID_ARGUMENT");

            // Read-only guarantee: remember which loaded scenes were clean, and report any that the run dirtied (NDMF's preview
            // scene is excluded: NDMF itself dirties and cleans it).
            var cleanScenes = new HashSet<int>(CctTargets.LoadedScenes().Where(s => !s.isDirty && !IsTransientScene(s)).Select(s => s.handle));
            CctResult r;
            switch (mode)
            {
                case ModeReverse: r = RunReverse(args); break;
                case ModeSceneReferences: r = RunSceneReferences(args); break;
                default: r = RunDependencies(args); break;
            }
            // In Play mode the scene holds runtime copies (NDMF/MA "Apply on Play" has already merged armatures, removed MA
            // components and baked meshes), so in-memory results do not describe what would be exported.
            var targetSpec = args.GetString("target", null)?.Trim();
            bool readsScene = mode == ModeSceneReferences ||
                              (mode == ModeReverse && args.GetBool("scan_open_scenes", true)) ||
                              (mode == ModeDependencies && !string.IsNullOrEmpty(targetSpec) &&
                               !(CctPaths.IsAssetPath(targetSpec) && targetSpec.IndexOf(".unity:", StringComparison.OrdinalIgnoreCase) < 0));
            if (readsScene && EditorApplication.isPlayingOrWillChangePlaymode)
                r.AddIssue(CctSeverity.Warning, "PLAY_MODE",
                    "Play モード中に実行しました。シーン内の結果は実行時の状態 (NDMF / Modular Avatar の Apply on Play 適用後など) で、保存・書き出しされる内容とは異なります。Play モードを終了してから再実行してください");
            foreach (var s in CctTargets.LoadedScenes())
                if (s.isDirty && cleanScenes.Contains(s.handle))
                    r.AddIssue(CctSeverity.Warning, "SCENE_DIRTIED",
                        $"実行中にシーン '{s.name}' が未保存状態になりました (このツールは変更しないはずです)。意図しない変更がないか確認し、不要なら保存せずに再読み込みしてください",
                        value: s.path);
            return r;
        }

        // ------------------------------------------------------------------ shared context

        sealed class PackageEntry
        {
            public string Name;
            public string DisplayName;
            public string Version;
            public string Source;
            public string ResolvedPath;
            public List<string> DependsOn; // null until read (see Context.DependsOn)
        }

        /// <summary>Per-run state (nothing static survives between runs, so domain reloads cannot leave stale caches).</summary>
        sealed class Context
        {
            public readonly CctResult R;
            public readonly int MaxEntries;
            public readonly long MaxFileBytes;
            Dictionary<string, PackageEntry> _packages;
            HashSet<string> _vpmManaged;
            string[] _allAssetPaths;
            readonly Dictionary<string, bool> _guidOk = new Dictionary<string, bool>(StringComparer.Ordinal);
            readonly Dictionary<string, int> _issueCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            readonly Dictionary<string, int> _issuesOmitted = new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>"C:/.../Project/" computed once: CctPaths.ProjectRoot re-reads Application.dataPath on every call, and a
            /// reverse scan resolves two paths per file.</summary>
            readonly string _projectRootSlash = CctPaths.ProjectRoot.TrimEnd('/') + "/";

            /// <summary>Seconds the scans may take before they stop: timeout_seconds - 5, or less when time_budget_seconds asks for less.</summary>
            public readonly double BudgetSeconds;
            readonly Stopwatch _clock = Stopwatch.StartNew();

            /// <summary>Scan phase at which the time budget ran out (null = every scan ran to completion).</summary>
            public string StoppedAt { get; private set; }

            /// <summary>time_budget_seconds asked for more than the call may take, so it was lowered to timeout_seconds - 5.</summary>
            readonly bool _limitedByTimeout;

            public Context(CctResult r, CctArgs args)
            {
                R = r;
                MaxEntries = args.GetInt("max_entries", DefaultMaxEntries, 1, 2000);
                var mb = args.GetDouble("max_file_mb", DefaultMaxFileMb);
                MaxFileBytes = mb <= 0 || double.IsNaN(mb) ? 0 : (long)(mb * 1024 * 1024);
                // The MCP server (HTTP) waits max(30, min(timeout_seconds, 3600)) s and cannot interrupt a synchronous handler:
                // a call that overruns returns a timeout error while Unity stays blocked, and a blind retry runs it twice. So the
                // scans always stop a few seconds before that limit; time_budget_seconds can only shorten it (an LLM passing
                // time_budget_seconds=120 without timeout_seconds would otherwise outlive its own call). Menu runs pass a large
                // timeout_seconds for the same reason.
                var timeout = args.GetInt("timeout_seconds", 0, 0, 3600);
                double callLimit = Math.Max(30, timeout) - TimeBudgetMarginSeconds;
                var requested = args.GetDouble("time_budget_seconds", 0);
                if (requested > 0 && !double.IsNaN(requested) && !double.IsInfinity(requested))
                {
                    BudgetSeconds = Math.Min(requested, callLimit);
                    _limitedByTimeout = requested > callLimit;
                }
                else BudgetSeconds = callLimit;
            }

            /// <summary>True once the time budget is used up; the first call that notices records <paramref name="phase"/>.</summary>
            public bool OutOfTime(string phase)
            {
                if (StoppedAt != null) return true;
                if (BudgetSeconds <= 0 || _clock.Elapsed.TotalSeconds < BudgetSeconds) return false;
                StoppedAt = phase;
                return true;
            }

            public bool Complete => StoppedAt == null;

            /// <summary>Every scan ran to the end, or the budget ran out only in one of <paramref name="optionalPhases"/>.</summary>
            public bool CompleteExcept(params string[] optionalPhases) => StoppedAt == null || Array.IndexOf(optionalPhases, StoppedAt) >= 0;

            /// <summary>Call once at the end: records the budget and, if it ran out, a SCAN_INCOMPLETE warning.</summary>
            public void FlushTimeBudget()
            {
                var d = new Dictionary<string, object>
                {
                    ["seconds"] = Math.Round(BudgetSeconds, 1),
                    ["exceeded"] = StoppedAt != null,
                };
                if (StoppedAt != null) d["stopped_at"] = StoppedAt;
                if (_limitedByTimeout) d["limited_by_timeout_seconds"] = true;
                R.Put("time_budget", d);
                if (StoppedAt == null) return;
                var label = PhaseLabels.TryGetValue(StoppedAt, out var l) ? l : StoppedAt;
                R.AddIssue(CctSeverity.Warning, "SCAN_INCOMPLETE",
                    $"時間制限 {Fmt(BudgetSeconds)} 秒に達したため「{label}」の途中で打ち切りました (この走査と、これより後の走査の結果は不完全です)。" +
                    "MCP からは timeout_seconds を増やして (例: 240) 再実行し、それでも足りなければ対象のフォルダーや search_root を絞ってください" +
                    $" (メニューからの実行は {Fmt(MenuTimeBudgetSeconds)} 秒まで)",
                    value: StoppedAt);
            }

            public Dictionary<string, PackageEntry> Packages
            {
                get
                {
                    if (_packages == null) _packages = LoadPackages(R, out _vpmManaged);
                    return _packages;
                }
            }

            /// <summary>Null when the project has no Packages/vpm-manifest.json (not a VCC project).</summary>
            public HashSet<string> VpmManaged
            {
                get
                {
                    var _ = Packages;
                    return _vpmManaged;
                }
            }

            public string[] AllAssetPaths => _allAssetPaths ?? (_allAssetPaths = AssetDatabase.GetAllAssetPaths());

            public PackageEntry FindPackage(string name)
            {
                if (string.IsNullOrEmpty(name)) return null;
                if (Packages.TryGetValue(name, out var p)) return p;
                // Not registered (e.g. a broken embedded package): read its package.json directly.
                p = new PackageEntry { Name = name, Source = "unregistered" };
                try
                {
                    var json = CctPaths.Combine(CctPaths.ProjectRoot, "Packages", name, "package.json");
                    if (File.Exists(json) && CctJson.Parse(File.ReadAllText(json, Encoding.UTF8)) is Dictionary<string, object> d)
                    {
                        p.Version = d.TryGetValue("version", out var v) ? v as string : null;
                        p.DisplayName = d.TryGetValue("displayName", out var dn) ? dn as string : null;
                        p.ResolvedPath = CctPaths.Combine(CctPaths.ProjectRoot, "Packages", name);
                        p.Source = "embedded";
                    }
                }
                catch (Exception) { /* leave version unknown */ }
                Packages[name] = p;
                return p;
            }

            /// <summary>
            /// "name range" entries from the package's package.json (vpmDependencies first, then UPM dependencies; com.unity.modules.*
            /// left out): what the destination must also install, e.g. Modular Avatar -> nadena.dev.ndmf. Empty when unreadable.
            /// </summary>
            public List<string> DependsOn(PackageEntry p)
            {
                if (p == null) return new List<string>();
                if (p.DependsOn != null) return p.DependsOn;
                var list = new List<string>();
                try
                {
                    var dir = !string.IsNullOrEmpty(p.ResolvedPath) ? p.ResolvedPath : CctPaths.Combine(CctPaths.ProjectRoot, "Packages", p.Name);
                    var json = CctPaths.Combine(dir, "package.json");
                    if (File.Exists(json) && CctJson.Parse(File.ReadAllText(json, Encoding.UTF8).TrimStart('\uFEFF')) is Dictionary<string, object> d)
                        foreach (var section in new[] { "vpmDependencies", "dependencies" })
                        {
                            if (!d.TryGetValue(section, out var v) || !(v is Dictionary<string, object> deps)) continue;
                            foreach (var kv in deps.OrderBy(x => x.Key, StringComparer.Ordinal))
                            {
                                if (kv.Key.StartsWith("com.unity.modules.", StringComparison.Ordinal)) continue;
                                if (list.Any(e => e == kv.Key || e.StartsWith(kv.Key + " ", StringComparison.Ordinal))) continue;
                                var range = kv.Value as string;
                                list.Add(string.IsNullOrWhiteSpace(range) ? kv.Key : kv.Key + " " + range.Trim());
                            }
                        }
                }
                catch (Exception) { /* unreadable package.json: no dependency info */ }
                p.DependsOn = list;
                return list;
            }

            /// <summary>Asset path -> file on disk (Packages/ resolved through the Package Manager, e.g. Library/PackageCache).</summary>
            public string ToPhysical(string assetPath)
            {
                if (assetPath.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    var name = PackageNameOf(assetPath);
                    var pkg = FindPackage(name);
                    if (pkg != null && !string.IsNullOrEmpty(pkg.ResolvedPath))
                        return CctPaths.Normalize(pkg.ResolvedPath).TrimEnd('/') + assetPath.Substring("Packages/".Length + name.Length);
                }
                return _projectRootSlash + assetPath;
            }

            /// <summary>
            /// Does this GUID name an asset that exists now? GUIDToAssetPath also returns recently deleted assets, so the path
            /// must additionally be a folder, have an imported main asset, or exist on disk.
            /// </summary>
            public bool GuidResolves(string guid)
            {
                if (BuiltinGuids.ContainsKey(guid)) return true;
                if (_guidOk.TryGetValue(guid, out var ok)) return ok;
                ok = false;
                try
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(p))
                        ok = AssetDatabase.IsValidFolder(p) || AssetDatabase.GetMainAssetTypeAtPath(p) != null || File.Exists(ToPhysical(p));
                }
                catch (Exception) { ok = false; }
                _guidOk[guid] = ok;
                return ok;
            }

            /// <summary>AddIssue with a per-code cap so a broken project cannot produce thousands of issues.</summary>
            public void Issue(string severity, string code, string message, string path = null, object value = null, string asset = null)
            {
                _issueCounts.TryGetValue(code, out var n);
                _issueCounts[code] = n + 1;
                if (n >= MaxIssuesPerCode)
                {
                    _issuesOmitted.TryGetValue(code, out var o);
                    _issuesOmitted[code] = o + 1;
                    return;
                }
                R.AddIssue(severity, code, message, path, value, null, asset);
            }

            /// <summary>Call once at the end: records how many issues per code were dropped by the cap.</summary>
            public void FlushIssueCaps()
            {
                if (_issuesOmitted.Count == 0) return;
                var d = new Dictionary<string, object>();
                foreach (var kv in _issuesOmitted) d[kv.Key] = kv.Value;
                R.Put("issues_capped", d);
                R.AddIssue(CctSeverity.Info, "ISSUES_CAPPED",
                    $"同じコードの問題が多いため、1 コードあたり {MaxIssuesPerCode} 件までに省略しました (件数は issues_capped / 各リストを参照)", value: d);
            }
        }

        static Dictionary<string, PackageEntry> LoadPackages(CctResult r, out HashSet<string> vpmManaged)
        {
            var map = new Dictionary<string, PackageEntry>(StringComparer.Ordinal);
            try
            {
                foreach (var p in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                {
                    if (p == null || string.IsNullOrEmpty(p.name)) continue;
                    map[p.name] = new PackageEntry
                    {
                        Name = p.name,
                        DisplayName = p.displayName,
                        Version = p.version,
                        Source = p.source.ToString().ToLowerInvariant(),
                        ResolvedPath = CctPaths.Normalize(p.resolvedPath),
                    };
                }
            }
            catch (Exception e)
            {
                r.AddIssue(CctSeverity.Warning, "PACKAGE_LIST_FAILED", "パッケージ一覧を取得できませんでした (バージョンが空欄になります): " + e.Message);
            }

            vpmManaged = null;
            try
            {
                var manifest = CctPaths.Combine(CctPaths.ProjectRoot, "Packages", "vpm-manifest.json");
                if (File.Exists(manifest))
                {
                    vpmManaged = new HashSet<string>(StringComparer.Ordinal);
                    if (CctJson.Parse(File.ReadAllText(manifest, Encoding.UTF8)) is Dictionary<string, object> root)
                        foreach (var section in new[] { "locked", "dependencies" })
                            if (root.TryGetValue(section, out var v) && v is Dictionary<string, object> d)
                                foreach (var k in d.Keys) vpmManaged.Add(k);
                }
            }
            catch (Exception) { vpmManaged = null; }
            return map;
        }

        // ------------------------------------------------------------------ inputs

        sealed class InputSet
        {
            public readonly List<string> Files = new List<string>();
            public readonly List<string> Folders = new List<string>();
            /// <summary>Files named directly (not found by expanding a folder): what the summary lists, the TSV/report has the rest.</summary>
            public readonly List<string> ExplicitFiles = new List<string>();
            public readonly HashSet<string> FileSet = new HashSet<string>(StringComparer.Ordinal);
            public int FromFolders;
            public bool FromSelection;
            public bool Truncated;

            public void AddFile(string p)
            {
                if (FileSet.Add(p)) Files.Add(p);
            }

            public void AddExplicitFile(string p)
            {
                AddFile(p);
                if (!ExplicitFiles.Contains(p)) ExplicitFiles.Add(p);
            }
        }

        const int MaxInputFiles = 20000;

        static InputSet ResolveInputs(Context ctx, List<string> requested, bool selectionFallback)
        {
            var set = new InputSet();
            if (requested.Count == 0 && selectionFallback)
            {
                requested = SelectedAssetPaths();
                set.FromSelection = requested.Count > 0;
            }
            foreach (var raw in requested)
            {
                var p = CanonicalAssetPath(raw, out var isFolder);
                if (p == null)
                {
                    ctx.Issue(CctSeverity.Error, "INPUT_NOT_FOUND", $"アセット '{raw}' が見つかりません (Assets/... か Packages/... のパス、または GUID を指定してください)", value: raw);
                    continue;
                }
                if (isFolder)
                {
                    if (!set.Folders.Contains(p)) set.Folders.Add(p);
                    var prefix = p + "/";
                    foreach (var a in ctx.AllAssetPaths)
                    {
                        if (!a.StartsWith(prefix, StringComparison.Ordinal) || AssetDatabase.IsValidFolder(a)) continue;
                        if (set.Files.Count >= MaxInputFiles) { set.Truncated = true; break; }
                        if (!set.FileSet.Contains(a)) set.FromFolders++;
                        set.AddFile(a);
                    }
                }
                else if (set.Files.Count < MaxInputFiles) set.AddExplicitFile(p);
                else set.Truncated = true;
            }
            set.Files.Sort(StringComparer.Ordinal);
            if (set.Truncated)
                ctx.Issue(CctSeverity.Warning, "SCAN_INCOMPLETE", $"入力アセットが多すぎるため先頭 {MaxInputFiles} 件だけを対象にしました。フォルダーを絞ってください");
            return set;
        }

        /// <summary>
        /// Accepts "Assets/..." / "Packages/..." (backslashes, quotes, trailing slash and absolute paths inside the project are
        /// tolerated) or a 32-hex GUID, and returns the AssetDatabase spelling of an existing asset or folder (null if none).
        /// </summary>
        static string CanonicalAssetPath(string raw, out bool isFolder)
        {
            var p = CanonicalAssetPathExact(raw, out isFolder);
            if (p != null || string.IsNullOrWhiteSpace(raw)) return p;
            // Japanese names from archives made on macOS are often NFD (dakuten stored separately, e.g. "カ\u3099"), while a path
            // retyped by Claude or the user is usually NFC; the AssetDatabase matches exact code points, so try both forms.
            foreach (var form in new[] { NormalizationForm.FormC, NormalizationForm.FormD })
            {
                string alt;
                try { alt = raw.Normalize(form); }
                catch (ArgumentException) { continue; }
                if (alt == raw) continue;
                p = CanonicalAssetPathExact(alt, out isFolder);
                if (p != null) return p;
            }
            return null;
        }

        static string CanonicalAssetPathExact(string raw, out bool isFolder)
        {
            isFolder = false;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var p = CctPaths.Normalize(raw.Trim().Trim('"', '\'')).TrimEnd('/');
            try
            {
                if (IsGuidString(p))
                {
                    p = AssetDatabase.GUIDToAssetPath(p.ToLowerInvariant());
                    if (string.IsNullOrEmpty(p)) return null;
                }
                var root = CctPaths.ProjectRoot.TrimEnd('/') + "/";
                if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) p = p.Substring(root.Length);
                if (p.StartsWith("./", StringComparison.Ordinal)) p = p.Substring(2);
                // "assets/..." typed in lower case (Windows paths are case-insensitive): fix the root folder's spelling, the
                // canonical spelling of the rest comes back from GUIDToAssetPath below.
                if (string.Equals(p, "Assets", StringComparison.OrdinalIgnoreCase)) p = "Assets";
                else if (p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) p = "Assets/" + p.Substring(7);
                else if (p.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) p = "Packages/" + p.Substring(9);
                if (!CctPaths.IsAssetPath(p) || p.Split('/').Any(s => s == "..")) return null;
                if (AssetDatabase.IsValidFolder(p))
                {
                    isFolder = true;
                    var fg = AssetDatabase.AssetPathToGUID(p);
                    var fp = string.IsNullOrEmpty(fg) ? null : AssetDatabase.GUIDToAssetPath(fg);
                    return string.IsNullOrEmpty(fp) ? p : fp;
                }
                var guid = AssetDatabase.AssetPathToGUID(p);
                if (string.IsNullOrEmpty(guid)) return null;
                var canonical = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(canonical)) return null;
                if (AssetDatabase.IsValidFolder(canonical)) { isFolder = true; return canonical; }
                return AssetDatabase.GetMainAssetTypeAtPath(canonical) != null ? canonical : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Project-window selection as asset paths (folders included), in selection order.</summary>
        internal static List<string> SelectedAssetPaths()
        {
            var list = new List<string>();
            var guids = Selection.assetGUIDs;
            if (guids == null) return list;
            foreach (var g in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (!string.IsNullOrEmpty(p) && !list.Contains(p)) list.Add(p);
            }
            return list;
        }

        // ------------------------------------------------------------------ path helpers

        static bool IsGuidString(string s)
        {
            if (s == null || s.Length != 32) return false;
            foreach (var c in s) if (!IsHex(c)) return false;
            return true;
        }

        static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        static bool IsWordChar(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';

        static bool IsUnder(string path, string root) =>
            string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

        /// <summary>"Packages/com.x.y/Runtime/a.cs" -> "com.x.y".</summary>
        static string PackageNameOf(string assetPath)
        {
            if (!assetPath.StartsWith("Packages/", StringComparison.Ordinal)) return null;
            var rest = assetPath.Substring("Packages/".Length);
            var slash = rest.IndexOf('/');
            return slash < 0 ? rest : rest.Substring(0, slash);
        }

        /// <summary>Grouping folder for reports: "Assets/A/B" (or shallower for files near the root).</summary>
        static string TopFolder(string assetPath)
        {
            var parts = assetPath.Split('/');
            if (parts.Length >= 4) return parts[0] + "/" + parts[1] + "/" + parts[2];
            if (parts.Length == 3) return parts[0] + "/" + parts[1];
            return parts[0];
        }

        static string TypeNameOf(string assetPath)
        {
            try
            {
                var t = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                if (t != null) return t.Name;
            }
            catch (Exception) { /* fall through */ }
            var ext = Path.GetExtension(assetPath);
            return string.IsNullOrEmpty(ext) ? "Unknown" : ext;
        }

        static long FileSize(string physicalPath)
        {
            try
            {
                var fi = new FileInfo(physicalPath);
                return fi.Exists ? fi.Length : -1;
            }
            catch (Exception) { return -1; }
        }

        static double Mb(long bytes) => Math.Round(Math.Max(0, bytes) / 1048576.0, 2);

        static List<object> Cap<T>(IEnumerable<T> items, int max, out int omitted)
        {
            var all = items.ToList();
            omitted = Math.Max(0, all.Count - max);
            return all.Take(max).Cast<object>().ToList();
        }

        /// <summary>Puts a capped list under <paramref name="key"/> and, when entries were dropped, "&lt;key&gt;_omitted".</summary>
        static void PutCapped<T>(CctResult r, string key, IEnumerable<T> items, int max)
        {
            r.Put(key, Cap(items, max, out var omitted));
            if (omitted > 0) r.Put(key + "_omitted", omitted);
        }

        /// <summary>"m_Bones.Array.data[12]" -> "m_Bones[12]" (readable); aggregate form uses "[]".</summary>
        static string PrettyPropertyPath(string propertyPath) => propertyPath?.Replace(".Array.data[", "[");

        static string AggregatePropertyPath(string propertyPath)
        {
            if (propertyPath == null) return null;
            var sb = new StringBuilder(propertyPath.Length);
            var p = PrettyPropertyPath(propertyPath);
            for (int i = 0; i < p.Length; i++)
            {
                if (p[i] == '[')
                {
                    sb.Append("[]");
                    while (i < p.Length && p[i] != ']') i++;
                    continue;
                }
                sb.Append(p[i]);
            }
            return sb.ToString();
        }

        /// <summary>"/Root/Child" or "/Root/Child (Transform)" for components; scene name prefixed when not the expected scene.</summary>
        static string DescribeSceneObject(Object o, UnityEngine.SceneManagement.Scene? expectedScene = null)
        {
            if (o == null) return null;
            GameObject go = o as GameObject ?? (o as Component)?.gameObject;
            if (go == null) return $"{o.GetType().Name} '{o.name}'";
            var path = CctTargets.GetPath(go);
            if (o is Component c) path += " (" + c.GetType().Name + ")";
            if (expectedScene.HasValue && go.scene != expectedScene.Value)
                path = (go.scene.IsValid() ? go.scene.name : "(シーン外)") + ":" + path;
            return path;
        }

        static string ComponentLabel(Object owner)
        {
            if (owner is Component c) return CctTargets.GetPath(c.transform) + " (" + c.GetType().Name + ")";
            if (owner is GameObject g) return CctTargets.GetPath(g);
            return owner != null ? owner.GetType().Name + " '" + owner.name + "'" : "?";
        }

        /// <summary>
        /// The loaded scenes plus the Prefab Mode stage scene (if one is open), for in-memory scans. <paramref name="stageScene"/>
        /// is that preview scene (default = none): compare scenes with it, not by an empty path, because an Untitled (never saved)
        /// main scene has an empty path too.
        /// </summary>
        static List<UnityEngine.SceneManagement.Scene> ScenesToScan(out string stageAssetPath, out UnityEngine.SceneManagement.Scene stageScene)
        {
            var list = CctTargets.LoadedScenes().Where(s => !IsTransientScene(s)).ToList();
            stageAssetPath = CurrentStageScene(out stageScene);
            if (stageAssetPath != null) list.Add(stageScene);
            return list;
        }

        /// <summary>Asset path of the open Prefab Mode stage and its preview scene; null when only the main stage is open.</summary>
        static string CurrentStageScene(out UnityEngine.SceneManagement.Scene scene)
        {
            scene = default;
            try
            {
                var stage = UnityEditor.SceneManagement.StageUtility.GetCurrentStage();
                if (stage is UnityEditor.SceneManagement.PreviewSceneStage ps && ps.scene.IsValid() && ps.scene.isLoaded &&
                    stage != UnityEditor.SceneManagement.StageUtility.GetMainStage())
                {
                    scene = ps.scene;
                    return stage.assetPath ?? "";
                }
            }
            catch (Exception) { /* no prefab stage support: main scenes only */ }
            return null;
        }

        /// <summary>
        /// Root of a prefab instance whose prefab asset is missing. IsPrefabAssetMissing is true for every object of such an
        /// instance (with a scene backup its children are kept), so only the topmost object of each instance is counted.
        /// </summary>
        static bool IsMissingPrefabInstanceRoot(GameObject go)
        {
            try
            {
                if (!PrefabUtility.IsPrefabAssetMissing(go)) return false;
                var parent = go.transform.parent;
                if (parent == null || !PrefabUtility.IsPrefabAssetMissing(parent.gameObject)) return true;
                return PrefabUtility.GetPrefabInstanceHandle(parent.gameObject) != PrefabUtility.GetPrefabInstanceHandle(go);
            }
            catch (Exception) { return false; /* not a prefab instance */ }
        }

        /// <summary>Every GameObject of a scene except transient ones (see <see cref="IsTransient"/>).</summary>
        static IEnumerable<GameObject> AllGameObjects(UnityEngine.SceneManagement.Scene scene)
        {
            if (IsTransientScene(scene)) yield break;
            GameObject[] roots;
            try { roots = scene.GetRootGameObjects(); }
            catch (Exception) { yield break; }
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (t != null && !IsTransient(t.gameObject)) yield return t.gameObject;
            }
        }

        static bool IsTransientScene(UnityEngine.SceneManagement.Scene scene) => scene.IsValid() && scene.name == NdmfPreviewSceneName;

        /// <summary>Editor-only objects that are never saved (DontSave, e.g. NDMF/MA preview proxies): not users, not exported.</summary>
        static bool IsTransient(GameObject go) =>
            go != null && ((go.hideFlags & HideFlags.DontSaveInEditor) != 0 || IsTransientScene(go.scene));

        static CctResult FailWith(CctResult partial, string message, string code, object details = null)
        {
            var f = CctResult.Fail(message, code, details);
            foreach (var i in partial.Issues) f.Issues.Add(i);
            foreach (var kv in partial.Data) if (!f.Data.ContainsKey(kv.Key)) f.Data[kv.Key] = kv.Value;
            return f;
        }

        static bool SerializationIsForceText(Context ctx)
        {
            SerializationMode mode;
            try { mode = EditorSettings.serializationMode; }
            catch (Exception) { return true; }
            ctx.R.Put("asset_serialization", mode.ToString());
            if (mode == SerializationMode.ForceText) return true;
            ctx.Issue(CctSeverity.Warning, "BINARY_SERIALIZATION",
                $"Asset Serialization が {mode} です (Force Text ではありません)。バイナリ保存のアセットは GUID 走査できないため、結果が不完全な可能性があります (Project Settings > Editor > Asset Serialization)",
                value: mode.ToString());
            return false;
        }

        // ------------------------------------------------------------------ progress

        /// <summary>Non-cancelable progress bar for long scans; always cleared by Dispose (use in a using block).</summary>
        sealed class Progress : IDisposable
        {
            readonly string _title;
            readonly int _total;
            readonly bool _show;
            int _lastPercent = -1;
            bool _shown;

            public Progress(string title, int total, int minToShow = 50)
            {
                _title = title;
                _total = Math.Max(1, total);
                _show = total >= minToShow && !Application.isBatchMode;
            }

            public void Step(int index, string info)
            {
                if (!_show) return;
                int pct = (int)(100L * index / _total);
                if (pct == _lastPercent) return;
                _lastPercent = pct;
                _shown = true;
                EditorUtility.DisplayProgressBar(_title, info ?? "", Mathf.Clamp01(index / (float)_total));
            }

            public void Dispose()
            {
                if (_shown) EditorUtility.ClearProgressBar();
            }
        }

        // ------------------------------------------------------------------ text GUID scanner

        enum ScanStatus { Ok, NotFound, TooLarge, Binary, NotUnityText, Failed }

        /// <summary>hex = lower-case 32-hex token; isRef = it follows "guid: " (a YAML object reference); key = YAML key of the line.</summary>
        delegate void HexHandler(string hex, bool isRef, int line, string key);

        sealed class ScanStats
        {
            public int TextFiles;
            public int MetaFiles;
            public long BytesScanned;
            public readonly List<string> Binary = new List<string>();
            public readonly List<string> TooLarge = new List<string>();
            public readonly List<string> Failed = new List<string>();
            public int SkippedByType;

            public void Count(ScanStatus status, string assetPath, bool isMeta, long bytes, string error)
            {
                switch (status)
                {
                    case ScanStatus.Ok:
                        if (isMeta) MetaFiles++; else TextFiles++;
                        BytesScanned += Math.Max(0, bytes);
                        break;
                    case ScanStatus.Binary:
                    case ScanStatus.NotUnityText:
                        Binary.Add(assetPath);
                        break;
                    case ScanStatus.TooLarge:
                        TooLarge.Add(assetPath);
                        break;
                    case ScanStatus.Failed:
                        Failed.Add(assetPath + (error != null ? " (" + error + ")" : ""));
                        break;
                }
            }

            public Dictionary<string, object> ToPlain(int max)
            {
                var d = new Dictionary<string, object>
                {
                    ["text_files_scanned"] = TextFiles,
                    ["meta_files_scanned"] = MetaFiles,
                    ["mb_scanned"] = Mb(BytesScanned),
                    ["skipped_by_type"] = SkippedByType,
                    ["binary_or_not_yaml"] = Binary.Count,
                    ["too_large"] = TooLarge.Count,
                    ["read_failed"] = Failed.Count,
                };
                if (TooLarge.Count > 0) d["too_large_files"] = Cap(TooLarge, max, out _);
                if (Failed.Count > 0) d["read_failed_files"] = Cap(Failed, max, out _);
                if (Binary.Count > 0) d["binary_examples"] = Cap(Binary, Math.Min(10, max), out _);
                return d;
            }
        }

        /// <summary>
        /// Streams a text file and reports every standalone 32-hex token (bounded by non-word characters). Files with NUL
        /// bytes in the first 8 KB are binary and skipped; with <paramref name="requireUnityText"/> only Unity YAML ("%YAML")
        /// and .meta ("fileFormatVersion") files are read. Opened with FileShare.ReadWrite|Delete so Unity / VCS writers are
        /// never blocked, and closed before returning.
        /// </summary>
        static ScanStatus ScanFile(string physicalPath, long maxBytes, bool requireUnityText, bool wantKeys, HexHandler onHex,
            out long length, out string error)
        {
            error = null;
            length = 0;
            try
            {
                var fi = new FileInfo(physicalPath);
                if (!fi.Exists) return ScanStatus.NotFound;
                length = fi.Length;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return ScanStatus.Failed;
            }
            if (maxBytes > 0 && length > maxBytes) return ScanStatus.TooLarge;
            if (length == 0) return ScanStatus.Ok;
            try
            {
                using (var fs = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                {
                    var head = new byte[(int)Math.Min(8192, length)];
                    int n = 0;
                    while (n < head.Length)
                    {
                        int k = fs.Read(head, n, head.Length - n);
                        if (k <= 0) break;
                        n += k;
                    }
                    for (int i = 0; i < n; i++)
                        if (head[i] == 0) return ScanStatus.Binary;
                    if (requireUnityText && !LooksLikeUnityText(head, n)) return ScanStatus.NotUnityText;
                    fs.Position = 0;
                    using (var reader = new StreamReader(fs, new UTF8Encoding(false), true, 1 << 16))
                        ScanChars(reader, wantKeys, onHex);
                }
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return ScanStatus.Failed;
            }
            return ScanStatus.Ok;
        }

        static bool LooksLikeUnityText(byte[] head, int n)
        {
            int start = n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF ? 3 : 0;
            return StartsWithAscii(head, n, start, "%YAML") || StartsWithAscii(head, n, start, "fileFormatVersion");
        }

        static bool StartsWithAscii(byte[] b, int n, int start, string s)
        {
            if (n - start < s.Length) return false;
            for (int i = 0; i < s.Length; i++)
                if (b[start + i] != (byte)s[i]) return false;
            return true;
        }

        static void ScanChars(TextReader reader, bool wantKeys, HexHandler onHex)
        {
            const string RefPrefix = "guid: ";
            const int TailLength = 7; // ring of the last 7 characters: "guid: " and the one before it
            var buf = new char[1 << 15];
            var run = new char[32];
            var tail = new char[TailLength];
            int tailPos = 0;
            int runLen = 0;
            bool runStartOk = false, runIsRef = false;
            char prev = '\n';
            int line = 1;
            var lineHead = wantKeys ? new StringBuilder(128) : null;
            string blockKey = null;
            char firstNonSpace = '\0', lastNonSpace = '\0';
            int read;
            while ((read = reader.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    char c = buf[i];
                    if (IsHex(c))
                    {
                        if (runLen == 0)
                        {
                            runStartOk = !IsWordChar(prev);
                            runIsRef = false;
                            if (runStartOk && prev == ' ')
                            {
                                // "guid: " must be a whole key (not the end of e.g. "m_SourceGuid: "); tail[tailPos] is the oldest char.
                                runIsRef = !IsWordChar(tail[tailPos]);
                                for (int k = 0; k < RefPrefix.Length && runIsRef; k++)
                                    if (tail[(tailPos + 1 + k) % TailLength] != RefPrefix[k]) runIsRef = false;
                            }
                        }
                        if (runLen < 32) run[runLen] = c;
                        if (runLen < 33) runLen++;
                    }
                    else
                    {
                        if (runLen == 32 && runStartOk && !IsWordChar(c))
                            Emit(run, runIsRef, line, wantKeys, lineHead, blockKey, onHex);
                        runLen = 0;
                    }

                    tail[tailPos] = c;
                    if (++tailPos == TailLength) tailPos = 0;
                    prev = c;
                    if (c == '\n')
                    {
                        if (wantKeys)
                        {
                            if (lastNonSpace == ':' && firstNonSpace != '-' && firstNonSpace != '\0')
                            {
                                var s = lineHead.ToString().Trim();
                                if (s.Length > 1 && s.Length < 100) blockKey = s.Substring(0, s.Length - 1);
                            }
                            lineHead.Length = 0;
                            firstNonSpace = lastNonSpace = '\0';
                        }
                        line++;
                    }
                    else if (wantKeys)
                    {
                        if (c != ' ' && c != '\t' && c != '\r')
                        {
                            if (firstNonSpace == '\0') firstNonSpace = c;
                            lastNonSpace = c;
                        }
                        if (lineHead.Length < 128) lineHead.Append(c);
                    }
                }
            }
            if (runLen == 32 && runStartOk) Emit(run, runIsRef, line, wantKeys, lineHead, blockKey, onHex);
        }

        static void Emit(char[] run, bool isRef, int line, bool wantKeys, StringBuilder lineHead, string blockKey, HexHandler onHex)
        {
            var hex = new string(run, 0, 32).ToLowerInvariant();
            string key = null;
            if (wantKeys && isRef)
            {
                var s = lineHead.ToString().TrimStart();
                if (s.StartsWith("- ", StringComparison.Ordinal)) s = s.Substring(2).TrimStart();
                int colon = s.IndexOf(':');
                key = colon > 0 && s[0] != '{' ? s.Substring(0, colon) : blockKey;
            }
            onHex(hex, isRef, line, key);
        }

        // ------------------------------------------------------------------ SerializedObject reference walker

        delegate void RefVisitor(SerializedProperty p);

        /// <summary>Hierarchy/prefab bookkeeping references: never portability problems (handled by dedicated checks).</summary>
        static bool IsBookkeeping(Object owner, string path)
        {
            switch (path)
            {
                case "m_GameObject":
                case "m_PrefabInstance":
                case "m_PrefabAsset":
                case "m_CorrespondingSourceObject":
                case "m_PrefabInternal":
                case "m_PrefabParentObject":
                case "m_Father":
                    return true;
            }
            if (owner is Transform && path.StartsWith("m_Children", StringComparison.Ordinal)) return true;
            if (owner is GameObject && path.StartsWith("m_Component", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// Visits every ObjectReference property of <paramref name="owner"/>, hidden ones included, skipping bookkeeping
        /// fields, strings, value types and large arrays whose elements cannot hold references. Returns false when the visit
        /// cap was hit (the object was only partly scanned). The SerializedObject is disposed before returning.
        /// </summary>
        static bool WalkReferences(Object owner, bool includeScriptField, RefVisitor visit, ref long visitsTotal)
        {
            if (owner == null) return true;
            int visits = 0;
            try
            {
                return WalkReferencesCore(owner, includeScriptField, visit, ref visits);
            }
            catch (Exception)
            {
                return false; // an object SerializedObject cannot read (or a visitor failure): report as partially scanned
            }
            finally
            {
                visitsTotal += visits;
            }
        }

        static bool WalkReferencesCore(Object owner, bool includeScriptField, RefVisitor visit, ref int visits)
        {
            using (var so = new SerializedObject(owner))
            {
                var it = so.GetIterator();
                bool enter = true;
                while (it.Next(enter))
                {
                    if (++visits > MaxVisitsPerObject) return false;
                    enter = true;
                    var type = it.propertyType;
                    if (type == SerializedPropertyType.ObjectReference)
                    {
                        enter = false;
                        var path = it.propertyPath;
                        bool wanted = path == "m_Script" ? includeScriptField : !IsBookkeeping(owner, path);
                        if (wanted) visit(it);
                        continue;
                    }
                    if (type != SerializedPropertyType.Generic && type != SerializedPropertyType.ManagedReference &&
                        type != SerializedPropertyType.ExposedReference)
                    {
                        enter = false; // strings, numbers, vectors, curves...: no object references inside
                        continue;
                    }
                    if (it.isArray && it.arraySize > ArrayPeekThreshold && !ArrayMayHoldReferences(it)) enter = false;
                }
            }
            return true;
        }

        /// <summary>Audio/video importers never reference other assets; native-format .meta files only hold their own GUID.</summary>
        static readonly HashSet<string> MetaWithoutReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".mp3", ".ogg", ".aif", ".aiff", ".flac", ".m4a", ".mod", ".it", ".s3m", ".xm",
            ".mp4", ".mov", ".webm", ".avi", ".m4v", ".mpg", ".mpeg", ".ogv", ".wmv", ".txt", ".bytes",
        };

        /// <summary>Is the .meta of a file with this extension worth scanning for GUID references?</summary>
        static bool MetaMayHoldReferences(string ext) =>
            !MetaWithoutReferences.Contains(ext) && !UnityYamlExtensions.Contains(ext);

        static readonly HashSet<string> ValueElementTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "int", "unsigned int", "float", "double", "bool", "char", "string", "UInt8", "SInt8", "UInt16", "SInt16", "UInt32", "SInt32",
            "UInt64", "SInt64", "long", "unsigned long", "short", "unsigned short", "Vector2f", "Vector3f", "Vector4f", "ColorRGBA",
            "Quaternionf", "Matrix4x4f", "Keyframe", "BoneWeights4", "AABB", "Hash128", "Rectf", "float2", "float3", "float4",
            "int2", "int3", "int4", "Vector2Int", "Vector3Int", "Vector2", "Vector3", "Vector4", "Color", "Quaternion", "Bounds",
        };

        /// <summary>Peeks at element 0 of a large array: can its elements contain object references? (conservative: true if unsure)</summary>
        static bool ArrayMayHoldReferences(SerializedProperty array)
        {
            try
            {
                var elementType = array.arrayElementType ?? "";
                if (elementType.StartsWith("PPtr<", StringComparison.Ordinal) || elementType.StartsWith("managedReference", StringComparison.Ordinal))
                    return true;
                if (ValueElementTypes.Contains(elementType)) return false;
                var e = array.GetArrayElementAtIndex(0);
                var t = e.propertyType;
                if (t == SerializedPropertyType.ObjectReference || t == SerializedPropertyType.ManagedReference ||
                    t == SerializedPropertyType.ExposedReference) return true;
                if (t != SerializedPropertyType.Generic) return false;
                var end = e.GetEndProperty();
                var c = e.Copy();
                int guard = 0;
                bool enter = true;
                while (c.Next(enter) && !SerializedProperty.EqualContents(c, end))
                {
                    if (++guard > 512) return true;
                    var ct = c.propertyType;
                    if (ct == SerializedPropertyType.ObjectReference || ct == SerializedPropertyType.ManagedReference ||
                        ct == SerializedPropertyType.ExposedReference) return true;
                    if (c.isArray && ct == SerializedPropertyType.Generic)
                    {
                        var nested = c.arrayElementType ?? "";
                        if (!ValueElementTypes.Contains(nested)) return true; // nested list of structs/PPtrs: walk it
                    }
                    enter = ct == SerializedPropertyType.Generic;
                }
                return false;
            }
            catch (Exception) { return true; }
        }

        /// <summary>A reference Unity shows as "Missing": no object, but a non-zero serialized instance ID.</summary>
        static bool IsMissing(SerializedProperty p) =>
            p.objectReferenceValue == null && p.objectReferenceInstanceIDValue != 0;

        static string Fmt(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
