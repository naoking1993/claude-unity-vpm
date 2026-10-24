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
    ///   list   - read-only: available samples (name, sync mode, summary), whether UdonSharp / TextMeshPro / uGUI /
    ///            the VRChat Worlds SDK are present, whether each sample is already imported at 'dest', and what the
    ///            next steps are.
    ///   import - copies Samples~/WorldGimmicks (or the 'only' subset) into 'dest' (default Assets/ClaudeWorldGimmicks)
    ///            ONLY when the UdonSharp runtime type exists (an avatar project would get compile errors), with
    ///            confirm=true (otherwise a dry-run plan is returned). Existing files are never overwritten unless
    ///            overwrite=true, and existing .meta files are never replaced (their GUIDs are what the project uses).
    ///            Source .meta files are copied so GUIDs stay stable, unless that GUID is already used by another asset.
    ///            Then AssetDatabase.Refresh(), and (create_program_assets, default true) one UdonSharpProgramAsset per
    ///            script, created exactly like UdonSharp's own "Create > U# Script" does (CreateInstance +
    ///            sourceCsScript + AssetDatabase.CreateAsset) - UdonSharp never creates them for copied scripts.
    ///
    /// UdonSharp / TMP types are looked up by name only (no compile-time dependency).
    ///
    /// Issue codes:
    /// | code                        | severity    | meaning                                                                    |
    /// |-----------------------------|-------------|----------------------------------------------------------------------------|
    /// | NO_UDONSHARP                | fail / info | UdonSharp.UdonSharpBehaviour not found (avatar project?); import refused   |
    /// | MISSING_DEPENDENCY          | fail / warn | TextMeshPro, uGUI or VRChat Worlds SDK types missing; import refused       |
    /// | SAMPLES_NOT_FOUND           | (fail code) | package folder or Samples~/WorldGimmicks not found                         |
    /// | INVALID_DEST                | (fail code) | dest is not a folder path under Assets/ (or contains '..' / bad chars)     |
    /// | DEST_HAS_ASMDEF             | (fail code) | an .asmdef in dest or a parent folder would capture the scripts            |
    /// | UNKNOWN_SAMPLE              | (fail code) | a name in 'only' is not one of the samples                                 |
    /// | PLAY_MODE                   | (fail code) | import refused in Play mode                                                |
    /// | INVALID_ARGUMENT            | (fail code) | unknown action                                                             |
    /// | CONFIRM_REQUIRED            | warning     | import dry run: nothing was written; call again with confirm=true          |
    /// | FILE_EXISTS                 | info        | target file exists and overwrite=false; skipped                            |
    /// | FILE_OVERWRITTEN            | warning     | target file existed and was overwritten (overwrite=true)                   |
    /// | META_KEPT                   | info        | target .meta exists; kept as is (GUID preserved)                           |
    /// | META_MISSING                | info        | the sample file has no .meta; Unity assigns a new GUID on import           |
    /// | GUID_IN_USE                 | warning     | the sample's GUID already belongs to another asset; .meta not copied       |
    /// | COPY_FAILED                 | error       | a file could not be copied                                                 |
    /// | PROGRAM_ASSET_CREATED       | info        | UdonSharpProgramAsset created for a script                                 |
    /// | PROGRAM_ASSET_EXISTS        | info        | a program asset already points at the script (or the .asset path exists)  |
    /// | PROGRAM_ASSET_TYPE_MISSING  | warning     | UdonSharp.UdonSharpProgramAsset (editor) type not found; create manually  |
    /// | PROGRAM_ASSET_FAILED        | warning     | program asset could not be created; create it manually (see next_steps)   |
    /// | ALREADY_IMPORTED            | info        | list: every sample file already exists at dest                             |
    /// | PARTIALLY_IMPORTED          | info        | list: some sample files exist at dest                                      |
    /// | COMPILE_PENDING             | info        | import wrote scripts; Unity and then UdonSharp will compile them           |
    /// </summary>
    public static class CctWorldGimmicks
    {
        public const string ToolName = "cct_world_gimmicks";
        public const string PackageName = "io.github.naoking1993.claude-creator-tools";
        public const string SampleRelativePath = "Samples~/WorldGimmicks";
        public const string DefaultDestination = "Assets/ClaudeWorldGimmicks";

        const string UdonSharpBehaviourType = "UdonSharp.UdonSharpBehaviour";
        const string UdonSharpProgramAssetType = "UdonSharp.UdonSharpProgramAsset";
        const string ProgramAssetSourceField = "sourceCsScript";
        const string TmpTextType = "TMPro.TMP_Text";
        const string UiSliderType = "UnityEngine.UI.Slider";
        const string WorldsPickupType = "VRC.SDK3.Components.VRCPickup";
        const string WorldsObjectSyncType = "VRC.SDK3.Components.VRCObjectSync";
        const string ScriptsFolder = "Scripts";

        /// <summary>One-line Japanese summaries shown by 'list' (the script header comments hold the full setup).</summary>
        static readonly Dictionary<string, string> Summaries = new Dictionary<string, string>
        {
            ["CctLocalToggle"] = "Interact で GameObject を自分だけ ON/OFF (ミラー等)。invert リスト付き",
            ["CctGlobalToggle"] = "全員で共有する ON/OFF。Manual 同期・Late Joiner 対応・マスター限定オプション",
            ["CctMirrorSwitcher"] = "複数ミラー (HQ/LQ/OFF) をローカルで切り替え。UI ボタン用 _Next/_Off/_Select0-3",
            ["CctTeleporter"] = "Interact / トリガー進入で自分を Target へテレポート (lerpOnRemote 対応)",
            ["CctPickupResetter"] = "ピックアップを初期位置へ戻す。ObjectSync 付きは持ち主が Respawn、無しは各自ローカル",
            ["CctAudioVolume"] = "UI スライダーで AudioSource 音量を自分だけ調整、% 表示、PlayerData で保存 (任意)",
            ["CctAreaToggle"] = "自分がトリガーエリアにいる間だけ表示 (部屋 BGM/照明)。重なり・テレポート/リスポーン対策済み",
            ["CctPlayerCounter"] = "現在人数と参加/退出ログ (直近 N 件) を TextMeshPro に表示",
        };

        // Rejected on every OS: the project is opened on Windows (Path.GetInvalidFileNameChars differs on Linux/macOS).
        static readonly char[] WindowsInvalidChars = { ':', '*', '?', '"', '<', '>', '|', '\\' };

        static readonly Regex SyncModeRegex = new Regex(@"BehaviourSyncMode\.(\w+)", RegexOptions.Compiled);
        static readonly Regex GuidRegex = new Regex(@"^guid:\s*([0-9a-fA-F]{32})\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

        public static CctResult Run(CctArgs args)
        {
            var action = args.GetEnum("action", null, "list", "import");
            if (action == null)
                return CctResult.Fail("action は 'list' か 'import' を指定してください", "INVALID_ARGUMENT");

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

        sealed class Env
        {
            public string PackageRoot, SampleRoot, PackageVersion;
            public bool UdonSharp, ProgramAssetType, Tmp, Ugui, WorldsSdk;
            public Type ProgramAsset;
            public List<string> Samples = new List<string>();
            public string WorldsSdkVersion;
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
                    env.Samples = Directory.GetFiles(scripts, "*.cs").Select(Path.GetFileNameWithoutExtension)
                        .OrderBy(n => n, StringComparer.Ordinal).ToList();
            }

            env.UdonSharp = CctSerialized.FindType(UdonSharpBehaviourType) != null;
            env.ProgramAsset = CctSerialized.FindType(UdonSharpProgramAssetType);
            env.ProgramAssetType = env.ProgramAsset != null && typeof(ScriptableObject).IsAssignableFrom(env.ProgramAsset);
            env.Tmp = CctSerialized.FindType(TmpTextType) != null;
            env.Ugui = CctSerialized.FindType(UiSliderType) != null;
            env.WorldsSdk = CctSerialized.FindType(WorldsPickupType) != null && CctSerialized.FindType(WorldsObjectSyncType) != null;
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

        static void PutEnvironment(CctResult r, Env env)
        {
            r.Put("package_root", env.PackageRoot);
            r.Put("package_version", env.PackageVersion);
            r.Put("sample_root", env.SampleRoot);
            r.Put("udonsharp_installed", env.UdonSharp);
            r.Put("udonsharp_program_asset_type", env.ProgramAssetType);
            r.Put("textmeshpro_installed", env.Tmp);
            r.Put("ugui_installed", env.Ugui);
            r.Put("vrchat_worlds_sdk", env.WorldsSdk);
            r.Put("vrchat_worlds_sdk_version", env.WorldsSdkVersion);
        }

        /// <summary>Missing pieces that would make the imported scripts fail to compile (empty = OK).</summary>
        static List<string> MissingDependencies(Env env)
        {
            var missing = new List<string>();
            if (!env.WorldsSdk) missing.Add("VRChat Worlds SDK (" + WorldsPickupType + ")");
            if (!env.Tmp) missing.Add("TextMeshPro (" + TmpTextType + ")");
            if (!env.Ugui) missing.Add("uGUI (" + UiSliderType + ")");
            return missing;
        }

        static string NormalizeDest(string raw, out string error)
        {
            error = null;
            var s = CctPaths.Normalize((raw ?? "").Trim()).TrimEnd('/');
            if (s.Length == 0) s = DefaultDestination;
            var segments = s.Split('/');
            bool bad = Path.IsPathRooted(s) || !s.StartsWith("Assets/", StringComparison.Ordinal) ||
                       segments.Any(seg => seg.Length == 0 || seg == "." || seg == ".." || seg.EndsWith("~", StringComparison.Ordinal) ||
                                           seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || seg.IndexOfAny(WindowsInvalidChars) >= 0);
            if (bad)
            {
                error = $"dest は Assets/ 以下のフォルダパスで指定してください (例: {DefaultDestination})。指定値: '{raw}'";
                return null;
            }
            return s;
        }

        // ------------------------------------------------------------------ list

        static CctResult List(Env env, string dest)
        {
            var r = CctResult.Ok("");
            r.Put("action", "list");
            PutEnvironment(r, env);
            r.Put("dest", dest);
            var destAbs = CctPaths.AssetToAbsolute(dest);
            r.Put("dest_exists", Directory.Exists(destAbs));

            var programAssets = FindProgramAssetScripts(env);
            var samples = new List<object>();
            int imported = 0, withProgramAsset = 0;
            foreach (var name in env.Samples)
            {
                var src = CctPaths.Combine(env.SampleRoot, ScriptsFolder, name + ".cs");
                var target = dest + "/" + ScriptsFolder + "/" + name + ".cs";
                bool exists = File.Exists(CctPaths.AssetToAbsolute(target));
                string programAsset = exists && programAssets.TryGetValue(target, out var pa) ? pa : null;
                if (exists) imported++;
                if (programAsset != null) withProgramAsset++;
                string syncMode = null;
                try
                {
                    var m = SyncModeRegex.Match(File.ReadAllText(src));
                    if (m.Success) syncMode = m.Groups[1].Value;
                }
                catch (Exception)
                {
                    // unreadable sample: leave sync mode unknown
                }
                samples.Add(new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["summary"] = Summaries.TryGetValue(name, out var sum) ? sum : null,
                    ["sync_mode"] = syncMode,
                    ["source"] = src,
                    ["target"] = target,
                    ["imported"] = exists,
                    ["program_asset"] = programAsset,
                });
            }
            r.Put("samples", samples);
            r.Put("sample_count", env.Samples.Count);
            r.Put("imported_count", imported);
            r.Put("program_asset_count", withProgramAsset);

            if (!env.UdonSharp)
                r.AddIssue(CctSeverity.Info, "NO_UDONSHARP", "UdonSharp が見つかりません (アバタープロジェクト?)。ワールドギミックはインポートできません");
            foreach (var m in MissingDependencies(env))
                r.AddIssue(CctSeverity.Warning, "MISSING_DEPENDENCY", "必要なパッケージ/型が見つかりません: " + m);
            if (env.Samples.Count > 0 && imported == env.Samples.Count)
                r.AddIssue(CctSeverity.Info, "ALREADY_IMPORTED", $"全サンプルが {dest} にインポート済みです");
            else if (imported > 0)
                r.AddIssue(CctSeverity.Info, "PARTIALLY_IMPORTED", $"{imported}/{env.Samples.Count} 個のサンプルが {dest} にあります");

            r.Put("next_steps", env.UdonSharp
                ? (imported > 0 ? AfterImportSteps(dest, withProgramAsset == imported) : new List<object> { $"action=import, confirm=true で {dest} に取り込む (既存ファイルは上書きしない)" })
                : new List<object> { "ワールドプロジェクト (VRChat Worlds SDK 3.4.0 以降は UdonSharp 同梱) で実行してください" });
            r.Message = $"ワールドギミック {env.Samples.Count} 種 / UdonSharp {(env.UdonSharp ? "あり" : "なし")} / {dest} にインポート済み {imported} 個";
            return r;
        }

        /// <summary>Project-relative .cs path -> program asset path, for every UdonSharpProgramAsset in the project.</summary>
        static Dictionary<string, string> FindProgramAssetScripts(Env env)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (env.ProgramAsset == null) return map;
            foreach (var guid in AssetDatabase.FindAssets("t:" + env.ProgramAsset.Name))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<Object>(path);
                if (asset == null || asset.GetType() != env.ProgramAsset) continue;
                var so = new SerializedObject(asset);
                var script = CctSerialized.GetObject(so.FindProperty(ProgramAssetSourceField));
                var scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : null;
                if (!string.IsNullOrEmpty(scriptPath) && !map.ContainsKey(scriptPath)) map[scriptPath] = path;
            }
            return map;
        }

        static List<object> AfterImportSteps(string dest, bool programAssetsReady)
        {
            var steps = new List<object>
            {
                "Unity のスクリプトコンパイル完了を待つ。続けて UdonSharp が U# プログラムを自動コンパイルする (Console に UdonSharp のエラーが無いこと)",
            };
            steps.Add(programAssetsReady
                ? $"{dest}/{ScriptsFolder} に各スクリプトと同名の .asset (UdonSharp Program Asset) があることを確認"
                : $"Program Asset が無いスクリプトは手動で作る: Project で {dest}/{ScriptsFolder} を右クリック → Create → VRChat → Udon → Udon C# Program Asset → 作った .asset の Inspector の Source Script に対応する .cs をドラッグ");
            steps.Add("ギミックを置く GameObject を選び Add Component でスクリプト名 (例: CctLocalToggle) を追加。Inspector に表示された時点で UdonSharp が裏の UdonBehaviour を自動で用意する (MCP で追加した場合はその GameObject を選択するか Play に入ると U# がセットアップする)");
            steps.Add($"各スクリプト冒頭のコメントと {dest}/README.md の手順で Collider・レイヤー・UI イベント (UdonBehaviour.SendCustomEvent) を設定");
            steps.Add("Play (ClientSim) でローカル動作を確認 → VRChat SDK > Build & Test (クライアント数 2) で同期を確認");
            steps.Add("U# が自動コンパイルされない場合: VRChat SDK > Udon Sharp > Refresh All UdonSharp Assets、または任意の Program Asset の Inspector > Utilities > Compile All UdonSharp Programs");
            return steps;
        }

        // ------------------------------------------------------------------ import

        sealed class PlanItem
        {
            public string Source;      // absolute
            public string Target;      // project-relative
            public bool IsMeta;
            public string Action;      // write | overwrite | skip_exists | keep_meta | skip_guid_in_use
            public string Guid;
        }

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
            var missing = MissingDependencies(env);
            if (missing.Count > 0)
            {
                var fail = CctResult.Fail("必要なパッケージが見つからないためインポートしません (コンパイルエラーになるため): " + string.Join(", ", missing), "MISSING_DEPENDENCY");
                fail.Put("action", "import");
                PutEnvironment(fail, env);
                return fail;
            }
            if (EditorApplication.isPlaying)
                return CctResult.Fail("Play モード中はインポートできません。Play を終了してから実行してください", "PLAY_MODE").Put("action", "import");

            var asmdef = FindAsmdefCapturing(dest);
            if (asmdef != null)
                return CctResult.Fail($"{asmdef} が {dest} を含むため、スクリプトがその Assembly に入りコンパイルエラーになります (UdonSharp は U# Assembly Definition が必要)。asmdef の外を dest に指定してください", "DEST_HAS_ASMDEF")
                    .Put("action", "import").Put("asmdef", asmdef);

            var only = args.GetStringList("only");
            var unknown = only.Where(n => !env.Samples.Contains(n)).ToList();
            if (unknown.Count > 0)
                return CctResult.Fail("不明なサンプル名: " + string.Join(", ", unknown) + " (有効: " + string.Join(", ", env.Samples) + ")", "UNKNOWN_SAMPLE")
                    .Put("action", "import");

            bool confirm = args.GetBool("confirm", false);
            bool overwrite = args.GetBool("overwrite", false);
            bool createProgramAssets = args.GetBool("create_program_assets", true);

            var r = CctResult.Ok("");
            r.Put("action", "import");
            PutEnvironment(r, env);
            r.Put("dest", dest);
            r.Put("overwrite", overwrite);
            r.Put("only", only.Cast<object>().ToList());

            var plan = BuildPlan(env, dest, only, overwrite, r);
            r.Put("plan", plan.Select(p => (object)new Dictionary<string, object>
            {
                ["target"] = p.Target,
                ["action"] = p.Action,
            }).ToList());

            if (!confirm)
            {
                r.Put("dry_run", true);
                r.AddIssue(CctSeverity.Warning, "CONFIRM_REQUIRED", "ドライランです。何も書き込んでいません。confirm=true で実行します");
                int writes = plan.Count(p => p.Action == "write" || p.Action == "overwrite");
                r.Message = $"ドライラン: {dest} に {writes} ファイルを書き込み予定 / スキップ {plan.Count - writes} (confirm=true で実行)";
                return r;
            }

            r.Put("dry_run", false);
            var written = new List<object>();
            var failed = new List<object>();
            foreach (var item in plan)
            {
                if (item.Action != "write" && item.Action != "overwrite") continue;
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
            r.Put("files_skipped", plan.Where(p => p.Action != "write" && p.Action != "overwrite")
                .Select(p => (object)new Dictionary<string, object> { ["target"] = p.Target, ["reason"] = p.Action }).ToList());

            if (written.Count > 0)
            {
                AssetDatabase.Refresh();
                r.AddIssue(CctSeverity.Info, "COMPILE_PENDING", "スクリプトを書き込みました。Unity のコンパイル → UdonSharp のコンパイルの順に自動で進みます");
            }

            var created = new List<object>();
            if (createProgramAssets)
                CreateProgramAssets(env, dest, only, created, r);
            r.Put("program_assets_created", created);

            bool programAssetsReady = createProgramAssets && env.ProgramAssetType;
            r.Put("next_steps", AfterImportSteps(dest, programAssetsReady));
            r.Message = $"{dest} に {written.Count} ファイルを書き込みました (スキップ {plan.Count - written.Count - failed.Count}, 失敗 {failed.Count}) / Program Asset 作成 {created.Count}";
            return r;
        }

        static List<PlanItem> BuildPlan(Env env, string dest, List<string> only, bool overwrite, CctResult r)
        {
            var plan = new List<PlanItem>();
            var root = env.SampleRoot.TrimEnd('/') + "/";
            var files = Directory.GetFiles(env.SampleRoot, "*", SearchOption.AllDirectories)
                .Select(CctPaths.Normalize)
                .Where(f => !Path.GetFileName(f).StartsWith(".", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            var fileSet = new HashSet<string>(files, StringComparer.Ordinal);

            bool Included(string rel)
            {
                if (only.Count == 0) return true;
                var assetRel = rel.EndsWith(".meta", StringComparison.Ordinal) ? rel.Substring(0, rel.Length - 5) : rel;
                if (!assetRel.StartsWith(ScriptsFolder + "/", StringComparison.Ordinal)) return true; // README, folder metas
                return only.Contains(Path.GetFileNameWithoutExtension(assetRel));
            }

            foreach (var f in files)
            {
                var rel = f.Substring(root.Length);
                if (!Included(rel)) continue;
                var target = dest + "/" + rel;
                var targetAbs = CctPaths.AssetToAbsolute(target);
                bool isMeta = rel.EndsWith(".meta", StringComparison.Ordinal);
                var item = new PlanItem { Source = f, Target = target, IsMeta = isMeta };
                if (!isMeta)
                {
                    if (File.Exists(targetAbs))
                    {
                        item.Action = overwrite ? "overwrite" : "skip_exists";
                        r.AddIssue(overwrite ? CctSeverity.Warning : CctSeverity.Info, overwrite ? "FILE_OVERWRITTEN" : "FILE_EXISTS",
                            overwrite ? "既存ファイルを上書きします" : "既存ファイルがあるためスキップ (overwrite=true で上書き)", value: target);
                    }
                    else item.Action = "write";
                    if (!fileSet.Contains(f + ".meta"))
                        r.AddIssue(CctSeverity.Info, "META_MISSING", ".meta が無いため Unity が新しい GUID を割り当てます", value: target);
                }
                else
                {
                    var assetTarget = target.Substring(0, target.Length - 5);
                    item.Guid = ReadGuid(f);
                    var usedBy = item.Guid != null ? AssetDatabase.GUIDToAssetPath(item.Guid) : null;
                    if (File.Exists(targetAbs))
                    {
                        item.Action = "keep_meta";
                        r.AddIssue(CctSeverity.Info, "META_KEPT", "既存の .meta を保持します (GUID を変えないため)", value: target);
                    }
                    else if (!string.IsNullOrEmpty(usedBy) && usedBy != assetTarget)
                    {
                        item.Action = "skip_guid_in_use";
                        r.AddIssue(CctSeverity.Warning, "GUID_IN_USE", $"GUID {item.Guid} は既に {usedBy} が使っているため .meta をコピーしません (新しい GUID になります)", value: target);
                    }
                    else item.Action = "write";
                }
                plan.Add(item);
            }
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

        /// <summary>Returns the project-relative path of an .asmdef in dest or any parent folder under Assets/, else null.</summary>
        static string FindAsmdefCapturing(string dest)
        {
            var parts = dest.Split('/');
            for (int n = parts.Length; n >= 2; n--)
            {
                var folder = string.Join("/", parts.Take(n));
                var abs = CctPaths.AssetToAbsolute(folder);
                if (!Directory.Exists(abs)) continue;
                var found = Directory.GetFiles(abs, "*.asmdef", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (found != null) return folder + "/" + Path.GetFileName(found);
            }
            return null;
        }

        /// <summary>
        /// Mirrors UdonSharp's UdonSharpBehaviourEditor.CreateUSharpScript: CreateInstance&lt;UdonSharpProgramAsset&gt;(),
        /// sourceCsScript = the MonoScript, AssetDatabase.CreateAsset next to the script as &lt;Name&gt;.asset.
        /// </summary>
        static void CreateProgramAssets(Env env, string dest, List<string> only, List<object> created, CctResult r)
        {
            if (!env.ProgramAssetType)
            {
                r.AddIssue(CctSeverity.Warning, "PROGRAM_ASSET_TYPE_MISSING", "UdonSharp.UdonSharpProgramAsset が見つからないため Program Asset を作れません。next_steps の手動手順で作成してください");
                return;
            }
            var existing = FindProgramAssetScripts(env);
            foreach (var name in env.Samples)
            {
                if (only.Count > 0 && !only.Contains(name)) continue;
                var scriptPath = dest + "/" + ScriptsFolder + "/" + name + ".cs";
                var assetPath = dest + "/" + ScriptsFolder + "/" + name + ".asset";
                if (!File.Exists(CctPaths.AssetToAbsolute(scriptPath))) continue;
                if (existing.TryGetValue(scriptPath, out var already))
                {
                    r.AddIssue(CctSeverity.Info, "PROGRAM_ASSET_EXISTS", "このスクリプトの Program Asset は既にあります", value: already);
                    continue;
                }
                if (File.Exists(CctPaths.AssetToAbsolute(assetPath)))
                {
                    r.AddIssue(CctSeverity.Info, "PROGRAM_ASSET_EXISTS", "同名の .asset が既にあるため作成しません", value: assetPath);
                    continue;
                }
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
                if (script == null)
                {
                    r.AddIssue(CctSeverity.Warning, "PROGRAM_ASSET_FAILED", "MonoScript を読み込めないため Program Asset を作れません (インポート完了後に手動で作成)", value: scriptPath);
                    continue;
                }
                ScriptableObject asset = null;
                try
                {
                    asset = ScriptableObject.CreateInstance(env.ProgramAsset);
                    var so = new SerializedObject(asset);
                    var prop = so.FindProperty(ProgramAssetSourceField);
                    if (prop == null || prop.propertyType != SerializedPropertyType.ObjectReference)
                    {
                        r.AddIssue(CctSeverity.Warning, "PROGRAM_ASSET_FAILED", $"{UdonSharpProgramAssetType}.{ProgramAssetSourceField} が見つかりません (UdonSharp の仕様変更?)。手動で作成してください", value: scriptPath);
                        Object.DestroyImmediate(asset);
                        return;
                    }
                    prop.objectReferenceValue = script;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    AssetDatabase.CreateAsset(asset, assetPath);
                    created.Add(assetPath);
                    r.AddIssue(CctSeverity.Info, "PROGRAM_ASSET_CREATED", "UdonSharp Program Asset を作成しました", value: assetPath);
                }
                catch (Exception e)
                {
                    if (asset != null && !AssetDatabase.Contains(asset)) Object.DestroyImmediate(asset);
                    r.AddIssue(CctSeverity.Warning, "PROGRAM_ASSET_FAILED", "Program Asset の作成に失敗しました: " + e.Message, value: assetPath);
                }
            }
        }
    }
}
