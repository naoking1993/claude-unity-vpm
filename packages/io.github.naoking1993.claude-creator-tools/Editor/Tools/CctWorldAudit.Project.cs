using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    public static partial class CctWorldAudit
    {
        // World bundle size limits: VRC.ValidationHelpers.GetAssetBundleSizeLimit (VRCSDKBase.dll 3.10.5): World on mobile
        // (BuildTarget 9 iOS / 13 Android) = 104,857,600 bytes compressed; PC = unlimited (-1). [WB] Upload() refuses
        // "fileSize > limit" (L2823-2829). docs: docs/platforms/android/quest-content-optimization.md L44.
        const long MobileWorldBundleLimitBytes = 104857600L;
        // Community Labs / Public: "under 200MB" recommended (docs/worlds/submitting-a-world-to-be-made-public.md L25).
        const long PcWorldBundleRecommendedBytes = 200L * 1024 * 1024;

        const int EditorLogTailBytes = 4 * 1024 * 1024;

        /// <summary>Avatar-only packages that do not belong in a world project (research/user-skills-context.md 2.3).</summary>
        static readonly string[] AvatarOnlyPackages =
        {
            "com.vrchat.avatars", "nadena.dev.modular-avatar", "nadena.dev.ndmf", "com.anatawa12.avatar-optimizer",
            "vrchat.blackstartx.gesture-manager", "com.vrcfury.vrcfury", "jp.lilxyzw.lilycalinventory",
        };

        static readonly Regex CsErrorRegex = new Regex(@"error CS\d{4}", RegexOptions.Compiled);

        static void AuditProject(Ctx ctx)
        {
            var p = new Dictionary<string, object>();
            ctx.R.Put("project", p);

            // [PB] CheckProjectSetup: Unity version vs RemoteConfig sdkUnityVersion (offline: the docs' required version).
            p["unity_version"] = Application.unityVersion;
            p["required_unity_version"] = RecommendedUnityVersion;
            if (Application.unityVersion != RecommendedUnityVersion)
                ctx.Issue(CctSeverity.Warning, "UNITY_VERSION_MISMATCH",
                    $"Unity {Application.unityVersion} です。VRChat のワールド制作は Unity {RecommendedUnityVersion} が必須で、別の版でアップロードすると読み込めなくなることがあります",
                    value: Application.unityVersion, limit: RecommendedUnityVersion);

            var target = EditorUserBuildSettings.activeBuildTarget;
            p["active_build_target"] = target.ToString();
            // [PB] C2: Android + androidBuildSubtarget != ASTC is a build-blocking error.
            p["android_texture_subtarget"] = EditorUserBuildSettings.androidBuildSubtarget.ToString();
            if (target == BuildTarget.Android && EditorUserBuildSettings.androidBuildSubtarget != MobileTextureSubtarget.ASTC)
                ctx.Issue(CctSeverity.Error, "ANDROID_TEXTURE_FORMAT_NOT_ASTC",
                    "Android ターゲットでテクスチャの既定圧縮が ASTC ではありません。SDK はこの状態ではビルドしません (Build Settings > Texture Compression を ASTC に)",
                    value: EditorUserBuildSettings.androidBuildSubtarget.ToString(), limit: "ASTC");

            // [PB] Lighting auto generate (Iterative) may stall the build.
            bool autoGenerate = Lightmapping.giWorkflowMode == Lightmapping.GIWorkflowMode.Iterative;
            p["lighting_auto_generate"] = autoGenerate;
            if (autoGenerate)
                ctx.Issue(CctSeverity.Warning, "LIGHTING_AUTO_GENERATE",
                    "Lighting ウィンドウの Auto Generate が ON です。SDK はビルドが止まる可能性があると警告します。ビルド前に OFF にして手動でベイクしてください");

            // [WB] OnGUISceneCheck: gravity (same three conditions, same float tests).
            var g = Physics.gravity;
            p["gravity"] = Vec(g);
            if (Math.Abs(g.x) > float.Epsilon || Math.Abs(g.z) > float.Epsilon)
                ctx.Issue(CctSeverity.Warning, "GRAVITY_NOT_STRAIGHT_DOWN", "重力 (Project Settings > Physics) が真下を向いていません。プレイヤーの向きは常に上向きなので意図どおりに動かないことがあります", value: Vec(g));
            if (g.y > 0)
                ctx.Issue(CctSeverity.Warning, "GRAVITY_UPWARD", "重力が上向きです。反転・無重力では歩行がとても難しくなります", value: Vec(g));
            if (Math.Abs(g.y) < float.Epsilon)
                ctx.Issue(CctSeverity.Warning, "GRAVITY_ZERO", "重力の Y が 0 です。無重力では歩行がとても難しくなります", value: Vec(g));

            // [WB] CheckFogSettings via EnvConfig.GetFogSettings (GraphicsSettings.asset m_FogStripping / m_FogKeep*).
            ReadFogStripping(ctx, p);

            // EnvConfig.ConfigurePlayerSettings forces Linear when building (unless VRC_OVERRIDE_COLORSPACE_GAMMA).
            p["color_space"] = PlayerSettings.colorSpace.ToString();
            if (PlayerSettings.colorSpace == ColorSpace.Gamma)
                ctx.Issue(CctSeverity.Info, "COLOR_SPACE_GAMMA", "Color Space が Gamma です。SDK はビルド時に Linear へ切り替えるため、ライティングの見た目が変わります");

            p["script_compilation_failed"] = EditorUtility.scriptCompilationFailed;
            if (EditorUtility.scriptCompilationFailed)
                ctx.Issue(CctSeverity.Error, "SCRIPT_COMPILATION_FAILED",
                    "C# スクリプトのコンパイルエラーが残っています。この状態ではビルドもアップロードもできません (Console / Editor.log の error CS を確認)");

            ReadPackages(ctx, p);
            ReadLastBuild(ctx, p);
        }

        static void ReadFogStripping(Ctx ctx, Dictionary<string, object> p)
        {
            Object graphics = null;
            try
            {
                var all = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
                graphics = all != null && all.Length > 0 ? all[0] : null;
            }
            catch (Exception) { /* ignored: reported as unknown */ }
            using (var so = TrySerializeAsset(graphics))
            {
                if (so == null) { p["fog_stripping"] = "unknown"; return; }
                var mode = so.FindProperty("m_FogStripping");
                // EnvConfig casts enumValueIndex to FogStrippingMode { Automatic, Custom }.
                bool custom = mode != null && mode.propertyType == SerializedPropertyType.Enum ? mode.enumValueIndex == 1 : CctSerialized.GetInt(mode) == 1;
                bool keepLinear = CctSerialized.GetBool(so.FindProperty("m_FogKeepLinear"));
                bool keepExp = CctSerialized.GetBool(so.FindProperty("m_FogKeepExp"));
                bool keepExp2 = CctSerialized.GetBool(so.FindProperty("m_FogKeepExp2"));
                p["fog_stripping"] = custom ? "Custom" : "Automatic";
                if (custom && (keepLinear || keepExp || keepExp2))
                    ctx.Issue(CctSeverity.Warning, "FOG_STRIPPING_CUSTOM",
                        "Graphics 設定の Fog シェーダーストリッピングが Custom です。実行時にフォグのモードを変えないなら Automatic にしてください (SDK の警告と同じ)",
                        value: new Dictionary<string, object> { ["keep_linear"] = keepLinear, ["keep_exp"] = keepExp, ["keep_exp2"] = keepExp2 });
            }
        }

        static void ReadPackages(Ctx ctx, Dictionary<string, object> p)
        {
            var versions = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var info in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                {
                    if (info == null || string.IsNullOrEmpty(info.name)) continue;
                    versions[info.name] = info.version;
                    ctx.Packages.Add(new PackageEntry { Name = info.name, DisplayName = info.displayName, Version = info.version });
                }
            }
            catch (Exception e)
            {
                p["packages_error"] = e.Message;
            }
            versions.TryGetValue("com.vrchat.worlds", out var worlds);
            versions.TryGetValue("com.vrchat.base", out var sdkBase);
            versions.TryGetValue("com.vrchat.udonsharp", out var legacyUSharp);
            p["worlds_sdk_version"] = worlds;
            p["base_sdk_version"] = sdkBase;
            p["udonsharp"] = new Dictionary<string, object>
            {
                ["present"] = CctSerialized.FindType(TUSharpBehaviour) != null,
                // Since SDK 3.4.0 UdonSharp ships inside com.vrchat.worlds (Integrations/UdonSharp); the old package is legacy.
                ["bundled_with_worlds_sdk"] = worlds != null,
                ["legacy_package_version"] = legacyUSharp,
            };
            var avatarPackages = AvatarOnlyPackages.Where(versions.ContainsKey).Select(n => (object)(n + "@" + versions[n])).ToList();
            p["avatar_packages"] = avatarPackages;
            if (avatarPackages.Count > 0)
                ctx.Issue(CctSeverity.Info, "AVATAR_PACKAGES_IN_WORLD_PROJECT",
                    "ワールドプロジェクトにアバター用パッケージが入っています。API の不整合で Assembly-CSharp が生成されなくなった実例があるため、不要なら VCC から外してください",
                    value: avatarPackages);
        }

        /// <summary>
        /// Last SDK build, as the Builder panel reads it: EditorPrefs "lastVRCPath" (UnityWebRequest.UnEscapeURL) [WB L676-691].
        /// EditorPrefs are per machine, so the file name (scene-&lt;target&gt;-&lt;scene&gt;-&lt;guid&gt;.vrcw, [EX] L1728-1765) is
        /// used to tell whether it belongs to the active scene.
        /// </summary>
        static void ReadLastBuild(Ctx ctx, Dictionary<string, object> p)
        {
            string raw = null;
            try { raw = EditorPrefs.GetString("lastVRCPath", ""); }
            catch (Exception) { /* ignored */ }
            if (string.IsNullOrEmpty(raw)) { p["last_build"] = null; return; }

            string path = raw;
            try { path = UnityEngine.Networking.UnityWebRequest.UnEscapeURL(raw); }
            catch (Exception) { /* keep raw */ }
            if (!File.Exists(path) && File.Exists(raw)) path = raw;
            var lb = new Dictionary<string, object> { ["path"] = CctPaths.Normalize(path) };
            p["last_build"] = lb;
            if (!File.Exists(path)) { lb["exists"] = false; return; }

            var fi = new FileInfo(path);
            lb["exists"] = true;
            lb["size_bytes"] = fi.Length;
            lb["size_mb"] = Round(fi.Length / (1024.0 * 1024.0), 2);
            lb["modified"] = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            ParseBundleName(fi.Name, out var bundleTarget, out var bundleScene);
            lb["target_source"] = "file_name";
            if (bundleTarget == null)
            {
                // Unparsable name: decide mobile like the Builder panel does (ValidationEditorHelpers.IsMobilePlatform() = active target).
                bundleTarget = EditorUserBuildSettings.activeBuildTarget.ToString().ToLowerInvariant();
                lb["target_source"] = "active_build_target";
            }
            lb["target"] = bundleTarget;
            lb["scene"] = bundleScene;
            bool matchesScene = bundleScene != null && ctx.Active.IsValid() &&
                                string.Equals(bundleScene, ctx.Active.name.ToLowerInvariant(), StringComparison.Ordinal);
            lb["matches_active_scene"] = matchesScene;
            bool mobile = bundleTarget == "android" || bundleTarget == "ios";
            lb["limit_bytes"] = mobile ? MobileWorldBundleLimitBytes : (object)null;

            var note = matchesScene ? "" : " (ファイル名のシーン名がアクティブシーンと一致しないため、別シーン/別プロジェクトのビルドかもしれません)";
            if (mobile && fi.Length > MobileWorldBundleLimitBytes)
                ctx.Issue(matchesScene ? CctSeverity.Error : CctSeverity.Warning, "BUNDLE_TOO_LARGE_MOBILE",
                    $"直近の {bundleTarget} ビルドは圧縮後 {Round(fi.Length / 1048576.0, 1)} MB で、モバイルの上限 100 MB を超えています。SDK は Publish 時にアップロードを拒否します{note}",
                    value: fi.Length, limit: MobileWorldBundleLimitBytes, asset: CctPaths.Normalize(path));
            else if (!mobile && fi.Length > PcWorldBundleRecommendedBytes)
                ctx.Issue(CctSeverity.Info, "BUNDLE_LARGE_PC",
                    $"直近の PC ビルドは {Round(fi.Length / 1048576.0, 1)} MB です。Community Labs / Public では 200 MB 未満が推奨されています{note}",
                    value: fi.Length, limit: PcWorldBundleRecommendedBytes, asset: CctPaths.Normalize(path));
        }

        /// <summary>"scene-standalonewindows64-my world-1b4e...-....vrcw" -> target "standalonewindows64", scene "my world".</summary>
        static void ParseBundleName(string fileName, out string target, out string scene)
        {
            target = null;
            scene = null;
            if (fileName == null || !fileName.StartsWith("scene-", StringComparison.Ordinal) ||
                !fileName.EndsWith(".vrcw", StringComparison.OrdinalIgnoreCase)) return;
            var body = fileName.Substring("scene-".Length, fileName.Length - "scene-".Length - ".vrcw".Length);
            int dash = body.IndexOf('-');
            if (dash <= 0) return;
            target = body.Substring(0, dash);
            var rest = body.Substring(dash + 1);
            const int guidLength = 36; // Guid.ToString(): 8-4-4-4-12
            if (rest.Length > guidLength + 1 && rest[rest.Length - guidLength - 1] == '-')
                scene = rest.Substring(0, rest.Length - guidLength - 1);
        }

        /// <summary>
        /// Tail of Editor.log: C# compile errors and UdonSharp errors. UdonSharp reports some failures (e.g. "does not belong
        /// to a U# assembly") only there (research/user-skills-context.md 2.3). Old, already-fixed errors may still be in the tail.
        /// </summary>
        static void AuditEditorLog(Ctx ctx)
        {
            var d = new Dictionary<string, object>();
            ctx.R.Put("editor_log", d);
            if (!ctx.CheckEditorLog) { d["checked"] = false; return; }
            string path = null;
            try { path = Application.consoleLogPath; }
            catch (Exception) { /* not available */ }
            d["path"] = CctPaths.Normalize(path);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                ctx.Issue(CctSeverity.Info, "EDITOR_LOG_UNAVAILABLE", "Editor.log が見つからないため、ログのエラーは確認していません", value: CctPaths.Normalize(path));
                return;
            }
            string text;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, fs.Length - EditorLogTailBytes);
                    fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - start];
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = fs.Read(buf, read, buf.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    text = new UTF8Encoding(false, false).GetString(buf, 0, read);
                    d["scanned_bytes"] = read;
                    d["file_bytes"] = fs.Length;
                }
            }
            catch (Exception e)
            {
                ctx.Issue(CctSeverity.Info, "EDITOR_LOG_UNAVAILABLE", "Editor.log を読めませんでした: " + e.Message, value: CctPaths.Normalize(path));
                return;
            }

            var cs = new List<string>();
            var us = new List<string>();
            int csCount = 0, usCount = 0;
            var seenCs = new HashSet<string>(StringComparer.Ordinal);
            var seenUs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0) continue;
                // U# first: its compile errors may quote Roslyn "error CS####" text but are U# (not Unity) failures.
                if (IsUdonSharpErrorLine(line))
                {
                    usCount++;
                    var clean = CleanLogLine(line);
                    if (seenUs.Add(clean) && us.Count < ExampleCap) us.Add(Trim(clean, 400));
                }
                else if (CsErrorRegex.IsMatch(line))
                {
                    csCount++;
                    if (seenCs.Add(line) && cs.Count < ExampleCap) cs.Add(Trim(line, 400));
                }
            }
            d["cs_error_lines"] = csCount;
            d["cs_errors"] = cs.Cast<object>().ToList();
            d["udonsharp_error_lines"] = usCount;
            d["udonsharp_errors"] = us.Cast<object>().ToList();
            if (csCount > 0)
                ctx.Issue(CctSeverity.Info, "EDITOR_LOG_CS_ERRORS",
                    $"Editor.log の末尾に C# のコンパイルエラー行が {csCount} 行あります (修正済みの古いエラーも含みます。現在の状態は SCRIPT_COMPILATION_FAILED を参照)",
                    value: cs.Cast<object>().ToList());
            if (usCount > 0)
                ctx.Issue(CctSeverity.Warning, "EDITOR_LOG_USHARP_ERRORS",
                    $"Editor.log の末尾に UdonSharp のエラー行が {usCount} 行あります。U# のエラーは Console に出ずログだけに残ることがあります (古いエラーを含む場合あり)",
                    value: us.Cast<object>().ToList());
        }

        // UdonSharp writes its log lines with a rich-text prefix, "[<color=#FF00FF>UdonSharp</color>] ..." for warnings and
        // errors (UdonSharpUtils.LogWarning/LogError), and compile errors as "[<color=#FF00FF>UdonSharp</color>] <file>(<line>,
        // <col>): <message>" (UdonSharpUtils.LogBuildError). Editor.log keeps the tags, so a plain "[UdonSharp]" never matches.
        static readonly Regex UdonSharpPrefixRegex = new Regex(@"\[(?:<color=#[0-9A-Fa-f]{6,8}>)?UdonSharp(?:</color>)?\]", RegexOptions.Compiled);
        static readonly Regex UdonSharpCompileErrorRegex = new Regex(@"UdonSharp(?:</color>)?\] .+\(\d+,\d+\): ", RegexOptions.Compiled);
        static readonly Regex ColorTagRegex = new Regex(@"</?color(?:=[^>]*)?>", RegexOptions.Compiled);

        static bool IsUdonSharpErrorLine(string line)
        {
            if (line.IndexOf("does not belong to a U# assembly", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("Failed to compile UdonSharp", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("Compile validation failed", StringComparison.Ordinal) >= 0)
                return true;
            if (line.IndexOf("UdonSharp", StringComparison.Ordinal) < 0 || !UdonSharpPrefixRegex.IsMatch(line)) return false;
            return UdonSharpCompileErrorRegex.IsMatch(line) ||
                   line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("is null", StringComparison.Ordinal) >= 0;
        }

        /// <summary>Log line for the report: rich-text color tags removed.</summary>
        static string CleanLogLine(string line) => ColorTagRegex.Replace(line, "");
    }
}
