using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// cct_world_gimmicks: lists and imports the UdonSharp (U#) world gimmick samples shipped in this package's
    /// Samples~/WorldGimmicks folder (Unity does not import "~" folders, so nothing compiles until imported).
    ///
    /// Actions:
    ///   list   - (default) read-only: available samples (name, sync mode, summary, required packages), whether
    ///            UdonSharp / the VRChat Worlds SDK / each optional dependency is present, whether each sample is
    ///            already imported at 'dest' (and has a U# program asset) or exists elsewhere in the project (and
    ///            whether that copy has a program asset), whether 'dest' is usable, which samples an import would
    ///            add ('importable', ready to pass as 'only'), and the next steps.
    ///   import - copies Samples~/WorldGimmicks (or the 'only' subset) into 'dest' (default Assets/ClaudeWorldGimmicks)
    ///            ONLY when the UdonSharp runtime type exists (an avatar project would get compile errors) and every
    ///            selected sample's dependencies exist, with confirm=true (otherwise a dry-run plan is returned).
    ///            Existing files are never overwritten unless overwrite=true, existing .meta files are never replaced
    ///            (their GUIDs are what the project uses), and a sample whose script already exists elsewhere in the
    ///            project is skipped (a second copy of the class would not compile). Source .meta files are copied so
    ///            GUIDs stay stable, unless that GUID is already used by another asset. Then AssetDatabase.Refresh(),
    ///            and (create_program_assets, default true) one UdonSharpProgramAsset per script, created exactly like
    ///            UdonSharp's own "Assets/Create/U# Script" does (CreateInstance + sourceCsScript +
    ///            AssetDatabase.CreateAsset next to the script) - UdonSharp never creates them for copied scripts.
    ///            A copy found elsewhere under Assets/ (e.g. a Package Manager Samples import) that has no program
    ///            asset gets one next to it too, so a skipped sample is still usable; it is listed in the dry run.
    ///            Paths are compared case-insensitively (the project is opened on Windows).
    ///
    /// UdonSharp / TMP / uGUI / VRChat types are looked up by full name only (no compile-time dependency).
    /// A sample's optional dependencies are derived from its 'using' directives (see <see cref="Requirements"/>).
    ///
    /// Issue codes (fail = returned as the failure code of the result):
    /// | code                        | severity     | meaning                                                                       |
    /// |-----------------------------|--------------|-------------------------------------------------------------------------------|
    /// | INVALID_ARGUMENT            | fail         | action is not 'list' or 'import'                                              |
    /// | SAMPLES_NOT_FOUND           | fail         | package folder or Samples~/WorldGimmicks not found                            |
    /// | INVALID_DEST                | fail         | dest is not a folder path under Assets/ (absolute, '..', hidden/'~', bad chars)|
    /// | NO_UDONSHARP                | fail / info  | UdonSharp.UdonSharpBehaviour not found (avatar project?); import refused      |
    /// | MISSING_DEPENDENCY          | fail / warn. | Worlds SDK, or a package a selected sample uses (TMP, uGUI, PlayerData,       |
    /// |                             |              | NetworkCallable) is missing; import refused (exclude those samples via 'only')|
    /// | PLAY_MODE                   | fail         | import refused in Play mode                                                   |
    /// | DEST_HAS_ASMDEF             | fail / warn. | an .asmdef/.asmref in dest or a parent folder would capture the scripts       |
    /// | DEST_SPECIAL_FOLDER         | fail / warn. | dest is inside an Editor folder or Assets/Plugins (or Standard Assets): those |
    /// |                             |              | scripts are not in Assembly-CSharp, so UdonSharp would not compile them       |
    /// | UNKNOWN_SAMPLE              | fail         | a name in 'only' is not one of the samples                                    |
    /// | CONFIRM_REQUIRED            | warning      | import dry run: nothing was written; call again with confirm=true             |
    /// | SAMPLE_EXISTS_ELSEWHERE     | warning      | the sample script already exists at another path (e.g. Package Manager        |
    /// |                             |              | Samples import); list: reported, import: that sample is skipped               |
    /// | FILE_EXISTS                 | info         | target files exist and overwrite=false; skipped (one issue, value = paths)    |
    /// | FILE_OVERWRITTEN            | warning      | target file exists and is overwritten (overwrite=true)                        |
    /// | META_KEPT                   | info         | target .meta files exist; kept as is (GUID preserved) (one issue, value=paths)|
    /// | META_MISSING                | info         | sample files without .meta; Unity assigns new GUIDs (one issue, value = paths)|
    /// | GUID_IN_USE                 | warning      | the sample's GUID already belongs to another asset; .meta not copied          |
    /// | COPY_FAILED                 | error        | a file could not be copied                                                    |
    /// | COMPILE_PENDING             | info         | import wrote files; Unity and then UdonSharp compile them after this call     |
    /// | PROGRAM_ASSET_CREATED       | info         | UdonSharpProgramAssets created (one issue, value = paths)                     |
    /// | PROGRAM_ASSET_EXISTS        | info         | a program asset already points at the script, or the .asset path exists       |
    /// |                             |              | (one issue, value = paths)                                                    |
    /// | PROGRAM_ASSET_TYPE_MISSING  | warning      | UdonSharp.UdonSharpProgramAsset (editor) type not found; create manually      |
    /// | PROGRAM_ASSET_FAILED        | warning      | program asset could not be created; create it manually (see next_steps)       |
    /// | ALREADY_IMPORTED            | info         | list: every sample script already exists at dest                              |
    /// | PARTIALLY_IMPORTED          | info         | list: some sample scripts exist at dest                                       |
    /// CctRunner may add EDITOR_BUSY / EXCEPTION (fail) and REPORT_WRITE_FAILED (warning) to any run.
    ///
    /// Data keys: action, package_root, package_version, sample_root, udonsharp_installed,
    /// udonsharp_program_asset_type, vrchat_worlds_sdk, vrchat_worlds_sdk_version, requirements {name: bool}, dest,
    /// dest_exists, dest_problem; list: samples [{name, summary, sync_mode, requires, missing_requirements, source,
    /// target, imported, program_asset, found_elsewhere, found_elsewhere_program_asset}], sample_count, imported_count,
    /// program_asset_count, importable, next_steps; import: overwrite, only, plan [{target, action}], dry_run,
    /// program_assets_planned, files_written, files_failed, files_skipped [{target, reason}], program_assets_created,
    /// next_steps; MISSING_DEPENDENCY failure: missing, importable.
    /// </summary>
    public static class CctWorldGimmicks
    {
        public const string ToolName = "cct_world_gimmicks";
        public const string PackageName = "io.github.naoking1993.claude-creator-tools";
        public const string SampleRelativePath = "Samples~/WorldGimmicks";
        public const string DefaultDestination = "Assets/ClaudeWorldGimmicks";

        /// <summary>Namespace every sample script declares (used to recognise copies elsewhere in the project).</summary>
        public const string SampleNamespace = "Naoking.ClaudeWorldGimmicks";

        const string UdonSharpBehaviourType = "UdonSharp.UdonSharpBehaviour";
        const string UdonSharpProgramAssetType = "UdonSharp.UdonSharpProgramAsset";
        const string ProgramAssetSourceField = "sourceCsScript";
        const string WorldsPickupType = "VRC.SDK3.Components.VRCPickup";
        const string WorldsObjectSyncType = "VRC.SDK3.Components.VRCObjectSync";
        const string ScriptsFolder = "Scripts";

        /// <summary>A package a sample needs: required when the sample has 'using UsingNamespace;', present when ProbeType exists.</summary>
        sealed class Requirement
        {
            public string Name, UsingNamespace, ProbeType;
        }

        static readonly Requirement[] Requirements =
        {
            new Requirement { Name = "TextMeshPro", UsingNamespace = "TMPro", ProbeType = "TMPro.TMP_Text" },
            new Requirement { Name = "uGUI", UsingNamespace = "UnityEngine.UI", ProbeType = "UnityEngine.UI.Slider" },
            new Requirement { Name = "PlayerData (VRChat Persistence)", UsingNamespace = "VRC.SDK3.Persistence", ProbeType = "VRC.SDK3.Persistence.PlayerData" },
            new Requirement { Name = "NetworkCallable (VRChat SDK 3.8.1+)", UsingNamespace = "VRC.SDK3.UdonNetworkCalling", ProbeType = "VRC.SDK3.UdonNetworkCalling.NetworkCallableAttribute" },
        };

        /// <summary>One-line Japanese summaries shown by 'list' (the script header comments hold the full setup).</summary>
        static readonly Dictionary<string, string> Summaries = new Dictionary<string, string>
        {
            ["CctLocalToggle"] = "Interact で GameObject を自分だけ ON/OFF (ミラー等)。invert リスト付き",
            ["CctGlobalToggle"] = "全員で共有する ON/OFF。Manual 同期・Late Joiner 対応・マスター限定オプション",
            ["CctMirrorSwitcher"] = "複数ミラー (HQ/LQ/OFF) をローカルで切り替え。UI ボタン用 _Next/_Off/_Select0-3",
            ["CctTeleporter"] = "Interact / トリガー進入で自分を Target へテレポート (lerpOnRemote 対応)",
            ["CctPickupResetter"] = "ピックアップを初期位置へ戻す。置いてある ObjectSync 付きは押した人が持ち主になって Respawn、持たれている物は持っている人が Drop→Respawn、ObjectSync 無しは各自ローカル",
            ["CctAudioVolume"] = "UI スライダーで AudioSource 音量を自分だけ調整、% 表示、PlayerData で保存 (任意)",
            ["CctAreaToggle"] = "自分がトリガーエリアにいる間だけ表示 (部屋 BGM/照明)。重なり対応、Enter/Exit の取りこぼし (テレポート/リスポーン/エリア内スポーン) を位置の定期確認で補正",
            ["CctPlayerCounter"] = "現在人数と参加/退出ログ (直近 N 件) を TextMeshPro に表示",
        };

        // Rejected on every OS: the project is opened on Windows (Path.GetInvalidFileNameChars differs on Linux/macOS).
        static readonly char[] WindowsInvalidChars = { ':', '*', '?', '"', '<', '>', '|', '\\' };

        // Top-level folders whose scripts go to Assembly-CSharp-firstpass (EditorBuildRules.FilterAssemblyInFirstpassFolder).
        static readonly string[] FirstpassFolders = { "Plugins", "Standard Assets", "Pro Standard Assets", "iPhone Standard Assets" };

        static readonly Regex SyncModeRegex = new Regex(@"BehaviourSyncMode\.(\w+)", RegexOptions.Compiled);
        static readonly Regex UsingRegex = new Regex(@"^\s*using\s+([A-Za-z0-9_.]+)\s*;", RegexOptions.Compiled | RegexOptions.Multiline);
        static readonly Regex GuidRegex = new Regex(@"^guid:\s*([0-9a-fA-F]{32})\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

        public static CctResult Run(CctArgs args)
        {
            var action = args.Has("action") ? args.GetEnum("action", null, "list", "import") : "list";
            if (action == null)
                return CctResult.Fail($"action は 'list' か 'import' を指定してください (指定値: '{args.GetString("action")}')", "INVALID_ARGUMENT");

            var env = Inspect();
            if (env.SampleRoot == null)
            {
                var fail = CctResult.Fail("パッケージ内のサンプルフォルダ (Samples~/WorldGimmicks) が見つかりません", "SAMPLES_NOT_FOUND");
                fail.Put("action", action).Put("package_root", env.PackageRoot);
                return fail;
            }

            var dest = NormalizeDest(args.GetString("dest", DefaultDestination), out var destError);
            if (dest == null)
                return CctResult.Fail(destError, "INVALID_DEST").Put("action", action);

            return action == "list" ? List(env, dest) : Import(args, env, dest);
        }

        // ------------------------------------------------------------------ environment

        sealed class Sample
        {
            public string Name;      // class name = file name without .cs
            public string Source;    // absolute path in Samples~
            public string SyncMode;  // from [UdonBehaviourSyncMode(BehaviourSyncMode.X)], null if not found
            public List<string> Requires = new List<string>(); // Requirement.Name values
        }

        sealed class Env
        {
            public string PackageRoot, SampleRoot, PackageVersion, WorldsSdkVersion;
            public bool UdonSharp, ProgramAssetType, WorldsSdk;
            public Type ProgramAsset;
            public List<Sample> Samples = new List<Sample>();
            public Dictionary<string, bool> Available = new Dictionary<string, bool>(StringComparer.Ordinal); // Requirement.Name -> present
        }

        static Env Inspect()
        {
            var env = new Env();
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(CctWorldGimmicks).Assembly);
                if (info != null && !string.IsNullOrEmpty(info.resolvedPath) && Directory.Exists(info.resolvedPath))
                {
                    env.PackageRoot = CctPaths.Normalize(info.resolvedPath);
                    env.PackageVersion = info.version;
                }
            }
            catch (Exception)
            {
                // fall back to the conventional embedded/VPM location below
            }
            if (env.PackageRoot == null)
            {
                var fallback = CctPaths.Combine(CctPaths.ProjectRoot, "Packages", PackageName);
                if (Directory.Exists(fallback)) env.PackageRoot = fallback;
            }
            if (env.PackageRoot != null)
            {
                var root = CctPaths.Combine(env.PackageRoot, SampleRelativePath);
                if (Directory.Exists(root)) env.SampleRoot = root;
            }
            if (env.SampleRoot != null)
            {
                var scripts = CctPaths.Combine(env.SampleRoot, ScriptsFolder);
                if (Directory.Exists(scripts))
                {
                    foreach (var file in Directory.GetFiles(scripts, "*.cs").Select(CctPaths.Normalize).OrderBy(f => f, StringComparer.Ordinal))
                        env.Samples.Add(ReadSample(file));
                }
            }

            env.UdonSharp = CctSerialized.FindType(UdonSharpBehaviourType) != null;
            env.ProgramAsset = CctSerialized.FindType(UdonSharpProgramAssetType);
            env.ProgramAssetType = env.ProgramAsset != null && typeof(ScriptableObject).IsAssignableFrom(env.ProgramAsset);
            env.WorldsSdk = CctSerialized.FindType(WorldsPickupType) != null && CctSerialized.FindType(WorldsObjectSyncType) != null;
            foreach (var req in Requirements)
                env.Available[req.Name] = CctSerialized.FindType(req.ProbeType) != null;
            try
            {
                foreach (var p in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                    if (p.name == "com.vrchat.worlds") env.WorldsSdkVersion = p.version;
            }
            catch (Exception)
            {
                // version is informational only
            }
            return env;
        }

        static Sample ReadSample(string file)
        {
            var s = new Sample { Name = Path.GetFileNameWithoutExtension(file), Source = file };
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception) { return s; } // unreadable sample: sync mode / requirements unknown
            var m = SyncModeRegex.Match(text);
            if (m.Success) s.SyncMode = m.Groups[1].Value;
            var usings = new HashSet<string>(UsingRegex.Matches(text).Cast<Match>().Select(u => u.Groups[1].Value), StringComparer.Ordinal);
            foreach (var req in Requirements)
                if (usings.Contains(req.UsingNamespace)) s.Requires.Add(req.Name);
            return s;
        }

        static void PutEnvironment(CctResult r, Env env)
        {
            r.Put("package_root", env.PackageRoot);
            r.Put("package_version", env.PackageVersion);
            r.Put("sample_root", env.SampleRoot);
            r.Put("udonsharp_installed", env.UdonSharp);
            r.Put("udonsharp_program_asset_type", env.ProgramAssetType);
            r.Put("vrchat_worlds_sdk", env.WorldsSdk);
            r.Put("vrchat_worlds_sdk_version", env.WorldsSdkVersion);
            r.Put("requirements", env.Available.ToDictionary(kv => kv.Key, kv => (object)kv.Value));
        }

        static List<string> MissingRequirements(Env env, Sample s) =>
            s.Requires.Where(n => !env.Available.TryGetValue(n, out var ok) || !ok).ToList();

        /// <summary>"TextMeshPro (TMPro.TMP_Text): CctA, CctB" per missing package (plus the Worlds SDK itself); empty = OK.</summary>
        static List<string> DescribeMissing(Env env, IEnumerable<Sample> samples)
        {
            var lines = new List<string>();
            if (!env.WorldsSdk) lines.Add($"VRChat Worlds SDK ({WorldsPickupType}): 全サンプル");
            var list = samples.ToList();
            foreach (var req in Requirements)
            {
                if (env.Available.TryGetValue(req.Name, out var ok) && ok) continue;
                var users = list.Where(s => s.Requires.Contains(req.Name)).Select(s => s.Name).ToList();
                if (users.Count > 0) lines.Add($"{req.Name} ({req.ProbeType}): {string.Join(", ", users)}");
            }
            return lines;
        }

        static string NormalizeDest(string raw, out string error)
        {
            error = null;
            var s = CctPaths.Normalize((raw ?? "").Trim()).TrimEnd('/');
            if (s.Length == 0) s = DefaultDestination;
            var segments = s.Split('/');
            bool bad = Path.IsPathRooted(s) || !s.StartsWith("Assets/", StringComparison.Ordinal) ||
                       segments.Any(seg => seg.Length == 0 || seg.Trim() != seg || seg.StartsWith(".", StringComparison.Ordinal) ||
                                           seg.EndsWith(".", StringComparison.Ordinal) || seg.EndsWith("~", StringComparison.Ordinal) ||
                                           seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || seg.IndexOfAny(WindowsInvalidChars) >= 0);
            if (bad)
            {
                error = $"dest は Assets/ 以下のフォルダパスで指定してください (例: {DefaultDestination}。'..'、'.' や '~' で始まる/終わるフォルダ、使えない文字は不可)。指定値: '{raw}'";
                return null;
            }
            return s;
        }

        /// <summary>Why scripts under dest would not reach Assembly-CSharp (and so not be compiled by UdonSharp), or null.</summary>
        static string SpecialFolderProblem(string dest)
        {
            var segments = dest.Split('/');
            if (segments.Skip(1).Any(seg => string.Equals(seg, "Editor", StringComparison.OrdinalIgnoreCase)))
                return $"{dest} は Editor フォルダの中です。中のスクリプトはエディタ専用アセンブリ (Assembly-CSharp-Editor) に入り、UdonSharp がコンパイルしません";
            if (segments.Length > 1 && FirstpassFolders.Any(f => string.Equals(segments[1], f, StringComparison.OrdinalIgnoreCase)))
                return $"{dest} は Assets/{segments[1]} の中です。中のスクリプトは Assembly-CSharp-firstpass に入り、UdonSharp がコンパイルしません";
            return null;
        }

        /// <summary>Project-relative path of an .asmdef/.asmref in dest or any parent folder (Assets/ included), else null.</summary>
        static string FindCapturingAssemblyFile(string dest)
        {
            var parts = dest.Split('/');
            for (int n = parts.Length; n >= 1; n--)
            {
                var folder = string.Join("/", parts.Take(n));
                var abs = CctPaths.AssetToAbsolute(folder);
                if (!Directory.Exists(abs)) continue;
                var found = Directory.GetFiles(abs, "*.asmdef", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetFiles(abs, "*.asmref", SearchOption.TopDirectoryOnly))
                    .OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
                if (found != null) return folder + "/" + Path.GetFileName(found);
            }
            return null;
        }

        static string ScriptTarget(string dest, string name) => dest + "/" + ScriptsFolder + "/" + name + ".cs";

        static string ProgramAssetTarget(string dest, string name) => dest + "/" + ScriptsFolder + "/" + name + ".asset";

        /// <summary>
        /// Sample name -> project-relative path of a copy of that script outside dest (a file &lt;Name&gt;.cs declaring
        /// <see cref="SampleNamespace"/>, e.g. from Package Manager's Samples import). Importing a second copy would
        /// declare the same class twice (CS0101) and stop all script compilation.
        /// </summary>
        static Dictionary<string, string> FindCopiesElsewhere(Env env, string dest)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in env.Samples)
            {
                var target = ScriptTarget(dest, s.Name);
                foreach (var guid in AssetDatabase.FindAssets(s.Name + " t:MonoScript"))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    // Windows paths are case-insensitive: dest typed as "Assets/claudeworldgimmicks" is the same folder.
                    if (string.IsNullOrEmpty(path) || SamePath(path, target)) continue;
                    if (!path.EndsWith("/" + s.Name + ".cs", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!DeclaresSampleNamespace(path)) continue;
                    map[s.Name] = path;
                    break;
                }
            }
            return map;
        }

        static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>Where a program asset for a copy found elsewhere would go (next to it), or null when it cannot be
        /// created there (outside Assets/: CreateAsset cannot write into Packages/).</summary>
        static string ElsewhereProgramAssetTarget(string scriptPath) =>
            scriptPath != null && scriptPath.StartsWith("Assets/", StringComparison.Ordinal) && scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                ? scriptPath.Substring(0, scriptPath.Length - 3) + ".asset"
                : null;

        static bool DeclaresSampleNamespace(string assetPath)
        {
            try { return File.ReadAllText(CctPaths.AssetToAbsolute(assetPath)).Contains("namespace " + SampleNamespace); }
            catch (Exception) { return false; }
        }

        /// <summary>Project-relative .cs path -> program asset path, for every UdonSharpProgramAsset in the project.</summary>
        static Dictionary<string, string> FindProgramAssetScripts(Env env)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (env.ProgramAsset == null) return map;
            foreach (var guid in AssetDatabase.FindAssets("t:" + env.ProgramAsset.Name))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<Object>(path);
                if (asset == null || asset.GetType() != env.ProgramAsset) continue;
                using (var so = new SerializedObject(asset))
                {
                    var script = CctSerialized.GetObject(so.FindProperty(ProgramAssetSourceField));
                    var scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : null;
                    if (!string.IsNullOrEmpty(scriptPath) && !map.ContainsKey(scriptPath)) map[scriptPath] = path;
                }
            }
            return map;
        }

        // ------------------------------------------------------------------ list

        static CctResult List(Env env, string dest)
        {
            var r = CctResult.Ok("");
            r.Put("action", "list");
            PutEnvironment(r, env);
            r.Put("dest", dest);
            r.Put("dest_exists", Directory.Exists(CctPaths.AssetToAbsolute(dest)));

            var destProblem = SpecialFolderProblem(dest);
            if (destProblem != null)
                r.AddIssue(CctSeverity.Warning, "DEST_SPECIAL_FOLDER", destProblem + "。import は拒否されます", value: dest);
            var asmFile = FindCapturingAssemblyFile(dest);
            if (asmFile != null)
            {
                destProblem = destProblem ?? $"{asmFile} が {dest} を含みます";
                r.AddIssue(CctSeverity.Warning, "DEST_HAS_ASMDEF", $"{asmFile} が {dest} を含むため import は拒否されます (asmdef の外を dest に指定)", value: asmFile);
            }
            r.Put("dest_problem", destProblem);

            var programAssets = FindProgramAssetScripts(env);
            var elsewhere = FindCopiesElsewhere(env, dest);
            var samples = new List<object>();
            var importable = new List<object>();
            var blocked = new List<string>();
            int imported = 0, withProgramAsset = 0;
            foreach (var s in env.Samples)
            {
                var target = ScriptTarget(dest, s.Name);
                bool exists = File.Exists(CctPaths.AssetToAbsolute(target));
                string programAsset = exists && programAssets.TryGetValue(target, out var pa) ? pa : null;
                if (exists) imported++;
                if (programAsset != null) withProgramAsset++;
                var missing = MissingRequirements(env, s);
                elsewhere.TryGetValue(s.Name, out var other);
                string otherProgramAsset = other != null && programAssets.TryGetValue(other, out var opa) ? opa : null;
                if (other != null)
                {
                    var fix = otherProgramAsset != null ? ""
                        : ElsewhereProgramAssetTarget(other) != null ? $"。そのコピーには Program Asset が無いため、import (create_program_assets=true) で {ElsewhereProgramAssetTarget(other)} を作ります"
                        : "。そのコピーには Program Asset が無く、Assets/ の外なので自動では作れません";
                    r.AddIssue(CctSeverity.Warning, "SAMPLE_EXISTS_ELSEWHERE",
                        $"{s.Name} は既に {other} にあります。{dest} にも取り込むと同じクラスが 2 つになりコンパイルエラーになるため、import ではこのサンプルを飛ばします{fix}", value: other);
                }
                if (env.UdonSharp && !exists && other == null)
                {
                    if (missing.Count == 0 && env.WorldsSdk) importable.Add(s.Name);
                    else blocked.Add(s.Name);
                }
                samples.Add(new Dictionary<string, object>
                {
                    ["name"] = s.Name,
                    ["summary"] = Summaries.TryGetValue(s.Name, out var sum) ? sum : null,
                    ["sync_mode"] = s.SyncMode,
                    ["requires"] = s.Requires.Cast<object>().ToList(),
                    ["missing_requirements"] = missing.Cast<object>().ToList(),
                    ["source"] = s.Source,
                    ["target"] = target,
                    ["imported"] = exists,
                    ["program_asset"] = programAsset,
                    ["found_elsewhere"] = other,
                    ["found_elsewhere_program_asset"] = otherProgramAsset,
                });
            }
            r.Put("samples", samples);
            r.Put("sample_count", env.Samples.Count);
            r.Put("imported_count", imported);
            r.Put("program_asset_count", withProgramAsset);
            r.Put("importable", importable);

            if (!env.UdonSharp)
                r.AddIssue(CctSeverity.Info, "NO_UDONSHARP", "UdonSharp が見つかりません (アバタープロジェクト?)。ワールドギミックはインポートできません");
            foreach (var line in DescribeMissing(env, env.Samples))
                r.AddIssue(CctSeverity.Warning, "MISSING_DEPENDENCY", "必要なパッケージ/型が見つかりません (使うサンプルはインポートできません): " + line);
            if (env.Samples.Count > 0 && imported == env.Samples.Count)
                r.AddIssue(CctSeverity.Info, "ALREADY_IMPORTED", $"全サンプルが {dest} にインポート済みです");
            else if (imported > 0)
                r.AddIssue(CctSeverity.Info, "PARTIALLY_IMPORTED", $"{imported}/{env.Samples.Count} 個のサンプルが {dest} にあります");

            var steps = new List<object>();
            if (!env.UdonSharp)
                steps.Add("ワールドプロジェクト (VRChat Worlds SDK 3.4.0 以降は UdonSharp 同梱) で実行してください。アバタープロジェクトには入れないでください");
            else
            {
                if (destProblem != null && importable.Count > 0)
                    steps.Add($"dest ({dest}) には取り込めません ({destProblem})。asmdef / Editor / Plugins の外のフォルダ (例: {DefaultDestination}) を dest に指定してください");
                if (importable.Count > 0)
                {
                    var onlyHint = blocked.Count > 0 || imported > 0 || elsewhere.Count > 0
                        ? $" (only=[{string.Join(", ", importable.Select(n => "\"" + n + "\""))}] で取り込めるものだけを指定)"
                        : "";
                    steps.Add($"action=import (confirm なし) で書き込み予定 (plan) を確認 → confirm=true で {dest} に取り込みます{onlyHint}。既存ファイルは上書きしません");
                }
                if (blocked.Count > 0)
                    steps.Add($"{string.Join(", ", blocked)} は必要なパッケージが無いため取り込めません (MISSING_DEPENDENCY の警告を参照。入れれば取り込めます)");
                if (imported > 0 || elsewhere.Count > 0)
                    steps.AddRange(AfterImportSteps(dest,
                        withProgramAsset == imported && elsewhere.Keys.All(n => programAssets.ContainsKey(elsewhere[n])),
                        env.ProgramAssetType));
            }
            r.Put("next_steps", steps);
            r.Message = $"ワールドギミック {env.Samples.Count} 種 / UdonSharp {(env.UdonSharp ? "あり" : "なし")} / {dest} にインポート済み {imported} 個" +
                        (elsewhere.Count > 0 ? $" / 別の場所にあるもの {elsewhere.Count} 個" : "");
            return r;
        }

        /// <param name="programAssetsReady">every imported (or found elsewhere) script already has its program asset</param>
        /// <param name="canAutoCreate">a (repeated) import with create_program_assets=true would create the missing ones</param>
        static List<object> AfterImportSteps(string dest, bool programAssetsReady, bool canAutoCreate)
        {
            var scripts = dest + "/" + ScriptsFolder;
            var manual = $"手動で作る場合: Project で .cs のあるフォルダ ({scripts} など) を右クリック → Create → VRChat → Udon → Udon C# Program Asset → " +
                         "作った .asset の Inspector の Source Script に対応する .cs をドラッグ (1 スクリプトにつき 1 個だけ)";
            var steps = new List<object>
            {
                "Unity のスクリプトコンパイル完了を待ちます。続けて UdonSharp が U# プログラムを自動コンパイルします (Console に UdonSharp のエラーが無いことを確認)",
                programAssetsReady
                    ? $"各スクリプトと同じフォルダ ({scripts} など) に同名の .asset (UdonSharp Program Asset) があることを確認"
                    : canAutoCreate
                        ? "Program Asset が無いスクリプトがあります。action=import, confirm=true (create_program_assets は既定で true) をもう一度実行すると作成します (既存ファイルは上書きしません)。" + manual
                        : "Program Asset が無いスクリプトがあります。" + manual,
                "ギミックを置く GameObject を選び Add Component でスクリプト名 (例: CctLocalToggle) を追加。Inspector に表示された時点で UdonSharp が裏の UdonBehaviour を自動で用意します (MCP で追加した場合は、その GameObject を選択して Inspector を表示するか、Play に入る / シーンを開き直すと U# がセットアップします)",
                $"各スクリプト冒頭のコメントと {dest}/README.md の手順で Collider・レイヤー・UI イベント (UdonBehaviour.SendCustomEvent) を設定",
                "Play (ClientSim) でローカル動作を確認 → VRChat SDK > Show Control Panel > Builder > Build & Test (Number of Clients = 2) で同期を確認",
                "U# が自動コンパイルされない場合: VRChat SDK > Udon Sharp > Refresh All UdonSharp Assets、または任意の Program Asset の Inspector > Utilities > Compile All UdonSharp Programs",
            };
            return steps;
        }

        // ------------------------------------------------------------------ import

        sealed class PlanItem
        {
            public string Source;      // absolute
            public string Target;      // project-relative
            public string Action;      // write | overwrite | skip_exists | keep_meta | skip_guid_in_use | skip_exists_elsewhere
        }

        static bool IsWrite(PlanItem p) => p.Action == "write" || p.Action == "overwrite";

        static CctResult Import(CctArgs args, Env env, string dest)
        {
            if (!env.UdonSharp)
            {
                var fail = CctResult.Fail(
                    "UdonSharp (UdonSharp.UdonSharpBehaviour) が見つからないためインポートしません。アバタープロジェクトに入れるとコンパイルエラーになります。" +
                    "VRChat Worlds SDK (UdonSharp 同梱) が入ったワールドプロジェクトで実行してください", "NO_UDONSHARP");
                fail.Put("action", "import");
                PutEnvironment(fail, env);
                return fail;
            }

            var requested = args.GetStringList("only");
            var selected = new List<Sample>();
            var unknown = new List<string>();
            foreach (var raw in requested)
            {
                var n = raw.Trim();
                if (n.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 3);
                var s = env.Samples.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
                if (s == null) unknown.Add(raw);
                else if (!selected.Contains(s)) selected.Add(s);
            }
            if (unknown.Count > 0)
                return CctResult.Fail("不明なサンプル名: " + string.Join(", ", unknown) + " (有効: " + string.Join(", ", env.Samples.Select(s => s.Name)) + ")", "UNKNOWN_SAMPLE")
                    .Put("action", "import");
            if (requested.Count == 0) selected = env.Samples.ToList();

            var missing = DescribeMissing(env, selected);
            if (missing.Count > 0)
            {
                var fail = CctResult.Fail("必要なパッケージが見つからないためインポートしません (コンパイルエラーになるため): " + string.Join(" / ", missing) +
                                          "。そのサンプルを除いて only で指定すれば残りは取り込めます", "MISSING_DEPENDENCY");
                fail.Put("action", "import");
                fail.Put("missing", missing.Cast<object>().ToList());
                fail.Put("importable", env.WorldsSdk
                    ? selected.Where(s => MissingRequirements(env, s).Count == 0).Select(s => (object)s.Name).ToList()
                    : new List<object>());
                PutEnvironment(fail, env);
                return fail;
            }
            // Also while entering Play mode: writing scripts + Refresh during the transition would compile into the play session.
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return CctResult.Fail("Play モード中はインポートできません。Play を終了してから実行してください", "PLAY_MODE").Put("action", "import");

            var special = SpecialFolderProblem(dest);
            if (special != null)
                return CctResult.Fail(special + "。Assets/ClaudeWorldGimmicks など通常のフォルダを dest に指定してください", "DEST_SPECIAL_FOLDER")
                    .Put("action", "import").Put("dest", dest);
            var asmFile = FindCapturingAssemblyFile(dest);
            if (asmFile != null)
                return CctResult.Fail($"{asmFile} が {dest} を含むため、スクリプトがその Assembly に入りコンパイルできません (UdonSharp は U# Assembly Definition が無い asmdef 内の U# を拒否します)。asmdef の外を dest に指定してください", "DEST_HAS_ASMDEF")
                    .Put("action", "import").Put("dest", dest).Put("asmdef", asmFile);

            bool confirm = args.GetBool("confirm", false);
            bool overwrite = args.GetBool("overwrite", false);
            bool createProgramAssets = args.GetBool("create_program_assets", true);

            var r = CctResult.Ok("");
            r.Put("action", "import");
            PutEnvironment(r, env);
            r.Put("dest", dest);
            r.Put("overwrite", overwrite);
            r.Put("only", selected.Count == env.Samples.Count ? new List<object>() : selected.Select(s => (object)s.Name).ToList());

            var elsewhere = FindCopiesElsewhere(env, dest);
            var plan = BuildPlan(env, dest, selected, elsewhere, overwrite, r);
            r.Put("plan", plan.Select(p => (object)new Dictionary<string, object>
            {
                ["target"] = p.Target,
                ["action"] = p.Action,
            }).ToList());

            if (!confirm)
            {
                r.Put("dry_run", true);
                var planned = new List<object>();
                if (createProgramAssets && env.ProgramAssetType)
                {
                    var existingAssets = FindProgramAssetScripts(env);
                    foreach (var s in selected)
                    {
                        var script = ScriptTarget(dest, s.Name);
                        bool scriptWillExist = plan.Any(p => p.Target == script && (IsWrite(p) || p.Action == "skip_exists"));
                        string assetPath = ProgramAssetTarget(dest, s.Name);
                        if (!scriptWillExist && elsewhere.TryGetValue(s.Name, out var other))
                        {
                            script = other;
                            assetPath = ElsewhereProgramAssetTarget(other);
                            scriptWillExist = assetPath != null;
                        }
                        if (scriptWillExist && !existingAssets.ContainsKey(script) && !File.Exists(CctPaths.AssetToAbsolute(assetPath)))
                            planned.Add(assetPath);
                    }
                }
                r.Put("program_assets_planned", planned);
                r.AddIssue(CctSeverity.Warning, "CONFIRM_REQUIRED", "確認用のドライランです。まだ何も書き込んでいません。plan をユーザーに見せて了承を得てから confirm=true を付けて再実行してください");
                int writes = plan.Count(IsWrite);
                r.Message = $"[ドライラン] {dest} に {writes} ファイルを書き込み予定 / スキップ {plan.Count - writes} / Program Asset 作成予定 {planned.Count} (confirm=true で実行)";
                return r;
            }

            r.Put("dry_run", false);
            var written = new List<object>();
            var failed = new List<object>();
            foreach (var item in plan)
            {
                if (!IsWrite(item)) continue;
                var targetAbs = CctPaths.AssetToAbsolute(item.Target);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetAbs));
                    File.Copy(item.Source, targetAbs, item.Action == "overwrite");
                    written.Add(item.Target);
                }
                catch (Exception e)
                {
                    failed.Add(item.Target);
                    r.AddIssue(CctSeverity.Error, "COPY_FAILED", "コピーに失敗しました: " + e.Message, value: item.Target);
                }
            }
            r.Put("files_written", written);
            r.Put("files_failed", failed);
            r.Put("files_skipped", plan.Where(p => !IsWrite(p))
                .Select(p => (object)new Dictionary<string, object> { ["target"] = p.Target, ["reason"] = p.Action }).ToList());

            if (written.Count > 0)
            {
                AssetDatabase.Refresh();
                r.AddIssue(CctSeverity.Info, "COMPILE_PENDING", "ファイルを書き込みました。この呼び出しの後、Unity のコンパイル → UdonSharp のコンパイルの順に自動で進みます。次の操作はコンパイル完了後に (EDITOR_BUSY が返ったら少し待って再実行)");
            }

            var created = new List<object>();
            if (createProgramAssets)
                CreateProgramAssets(env, dest, selected, elsewhere, created, r);
            r.Put("program_assets_created", created);

            bool assetsReady = createProgramAssets && env.ProgramAssetType &&
                               !r.Issues.Any(i => (i["code"] as string) == "PROGRAM_ASSET_FAILED");
            r.Put("next_steps", AfterImportSteps(dest, assetsReady, !createProgramAssets && env.ProgramAssetType));
            r.Message = $"{dest} に {written.Count} ファイルを書き込みました (スキップ {plan.Count - written.Count - failed.Count}, 失敗 {failed.Count}) / Program Asset 作成 {created.Count}";
            return r;
        }

        static List<PlanItem> BuildPlan(Env env, string dest, List<Sample> selected, Dictionary<string, string> elsewhere, bool overwrite, CctResult r)
        {
            var plan = new List<PlanItem>();
            var root = env.SampleRoot.TrimEnd('/') + "/";
            var files = Directory.GetFiles(env.SampleRoot, "*", SearchOption.AllDirectories)
                .Select(CctPaths.Normalize)
                .Where(f => !Path.GetFileName(f).StartsWith(".", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            var fileSet = new HashSet<string>(files, StringComparer.Ordinal);
            var selectedNames = new HashSet<string>(selected.Select(s => s.Name), StringComparer.Ordinal);
            // Per-file info findings are reported as one issue per code (the paths are in value and in plan),
            // so a re-import does not push the real warnings out of a max_issues-limited summary.
            var existing = new List<object>();
            var metaKept = new List<object>();
            var metaMissing = new List<object>();

            // Scripts/<Name>.cs(.meta) -> <Name>; anything else (README, folder metas, future extras) -> null = always included.
            string SampleOf(string rel)
            {
                var assetRel = rel.EndsWith(".meta", StringComparison.Ordinal) ? rel.Substring(0, rel.Length - 5) : rel;
                if (!assetRel.StartsWith(ScriptsFolder + "/", StringComparison.Ordinal)) return null;
                return Path.GetFileNameWithoutExtension(assetRel);
            }

            foreach (var f in files)
            {
                var rel = f.Substring(root.Length);
                var sample = SampleOf(rel);
                if (sample != null && !selectedNames.Contains(sample)) continue;
                var target = dest + "/" + rel;
                var targetAbs = CctPaths.AssetToAbsolute(target);
                bool isMeta = rel.EndsWith(".meta", StringComparison.Ordinal);
                var item = new PlanItem { Source = f, Target = target };
                if (sample != null && elsewhere.TryGetValue(sample, out var other))
                {
                    item.Action = "skip_exists_elsewhere";
                    if (!isMeta)
                        r.AddIssue(CctSeverity.Warning, "SAMPLE_EXISTS_ELSEWHERE",
                            $"{sample} は既に {other} にあるため取り込みません (同じクラスが 2 つになるとコンパイルエラー)。{dest} に置きたい場合は先に {other} を削除してください", value: other);
                }
                else if (!isMeta)
                {
                    if (File.Exists(targetAbs))
                    {
                        item.Action = overwrite ? "overwrite" : "skip_exists";
                        if (overwrite) r.AddIssue(CctSeverity.Warning, "FILE_OVERWRITTEN", "既存ファイルを上書きします", value: target);
                        else existing.Add(target);
                    }
                    else item.Action = "write";
                    if (!fileSet.Contains(f + ".meta")) metaMissing.Add(target);
                }
                else
                {
                    var assetTarget = target.Substring(0, target.Length - 5);
                    var guid = ReadGuid(f);
                    var usedBy = guid != null ? AssetDatabase.GUIDToAssetPath(guid) : null;
                    if (File.Exists(targetAbs))
                    {
                        item.Action = "keep_meta";
                        metaKept.Add(target);
                    }
                    else if (!string.IsNullOrEmpty(usedBy) && !SamePath(usedBy, assetTarget))
                    {
                        item.Action = "skip_guid_in_use";
                        r.AddIssue(CctSeverity.Warning, "GUID_IN_USE", $"GUID {guid} は既に {usedBy} が使っているため .meta をコピーしません (新しい GUID になります)", value: target);
                    }
                    else item.Action = "write";
                }
                plan.Add(item);
            }
            if (existing.Count > 0)
                r.AddIssue(CctSeverity.Info, "FILE_EXISTS", $"既存ファイル {existing.Count} 個はスキップします (overwrite=true で上書き)", value: existing);
            if (metaKept.Count > 0)
                r.AddIssue(CctSeverity.Info, "META_KEPT", $"既存の .meta {metaKept.Count} 個はそのまま保持します (GUID を変えないため)", value: metaKept);
            if (metaMissing.Count > 0)
                r.AddIssue(CctSeverity.Info, "META_MISSING", $".meta の無いサンプルファイル {metaMissing.Count} 個は、Unity が新しい GUID を割り当てます", value: metaMissing);
            return plan;
        }

        static string ReadGuid(string metaPath)
        {
            try
            {
                var m = GuidRegex.Match(File.ReadAllText(metaPath));
                return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Mirrors UdonSharp's UdonSharpBehaviourEditor.CreateUSharpScript: CreateInstance&lt;UdonSharpProgramAsset&gt;(),
        /// sourceCsScript = the MonoScript, AssetDatabase.CreateAsset next to the script as &lt;Name&gt;.asset.
        /// The script is the copy at dest, or (when the sample was skipped) the copy found elsewhere under Assets/.
        /// </summary>
        static void CreateProgramAssets(Env env, string dest, List<Sample> selected, Dictionary<string, string> elsewhere,
            List<object> created, CctResult r)
        {
            if (!env.ProgramAssetType)
            {
                r.AddIssue(CctSeverity.Warning, "PROGRAM_ASSET_TYPE_MISSING", "UdonSharp.UdonSharpProgramAsset が見つからないため Program Asset を作れません。next_steps の手動手順で作成してください");
                return;
            }
            var existing = FindProgramAssetScripts(env);
            var alreadyThere = new List<object>();
            foreach (var s in selected)
            {
                var scriptPath = ScriptTarget(dest, s.Name);
                var assetPath = ProgramAssetTarget(dest, s.Name);
                if (!File.Exists(CctPaths.AssetToAbsolute(scriptPath)))
                {
                    if (!elsewhere.TryGetValue(s.Name, out var other) || ElsewhereProgramAssetTarget(other) == null) continue;
                    scriptPath = other;
                    assetPath = ElsewhereProgramAssetTarget(other);
                }
                if (existing.TryGetValue(scriptPath, out var already))
                {
                    alreadyThere.Add(already);
                    continue;
                }
                if (File.Exists(CctPaths.AssetToAbsolute(assetPath)))
                {
                    alreadyThere.Add(assetPath); // same-named .asset (maybe not a program asset): never overwritten
                    continue;
                }
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
                if (script == null)
                {
                    r.AddIssue(CctSeverity.Warning, "PROGRAM_ASSET_FAILED", "MonoScript を読み込めないため Program Asset を作れません。コンパイル完了後に action=import, confirm=true をもう一度実行すると作成します (または next_steps の手動手順)", value: scriptPath);
                    continue;
                }
                ScriptableObject asset = null;
                try
                {
                    asset = ScriptableObject.CreateInstance(env.ProgramAsset);
                    using (var so = new SerializedObject(asset))
                    {
                        var prop = so.FindProperty(ProgramAssetSourceField);
                        if (prop == null || prop.propertyType != SerializedPropertyType.ObjectReference)
                        {
                            r.AddIssue(CctSeverity.Warning, "PROGRAM_ASSET_FAILED", $"{UdonSharpProgramAssetType}.{ProgramAssetSourceField} が見つかりません (UdonSharp の仕様変更?)。手動で作成してください", value: scriptPath);
                            Object.DestroyImmediate(asset);
                            break; // same for every script
                        }
                        prop.objectReferenceValue = script;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                    AssetDatabase.CreateAsset(asset, assetPath);
                    created.Add(assetPath);
                }
                catch (Exception e)
                {
                    if (asset != null && !AssetDatabase.Contains(asset)) Object.DestroyImmediate(asset);
                    r.AddIssue(CctSeverity.Warning, "PROGRAM_ASSET_FAILED", "Program Asset の作成に失敗しました: " + e.Message, value: assetPath);
                }
            }
            if (alreadyThere.Count > 0)
                r.AddIssue(CctSeverity.Info, "PROGRAM_ASSET_EXISTS", $"Program Asset (または同名の .asset) が既にある {alreadyThere.Count} 個は作成しません (上書きしません)", value: alreadyThere);
            if (created.Count > 0)
                r.AddIssue(CctSeverity.Info, "PROGRAM_ASSET_CREATED", $"UdonSharp Program Asset を {created.Count} 個作成しました", value: created.ToList());
        }
    }
}
