using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// cct_capture_views: renders reproducible PNG images of a GameObject (usually a VRChat avatar) from fixed angles
    /// relative to its forward (+Z), so Claude can judge appearance and compare before/after edits under identical
    /// camera / pose / lighting. Nothing in the user's scenes or assets is modified: "isolated" renders a script-free
    /// clone inside a temporary preview scene with neutral studio lighting, "scene" renders the real scene with a
    /// temporary HideAndDontSave camera. Everything temporary is destroyed in finally.
    ///
    /// Output folder: Library/ClaudeCreatorTools/Captures/&lt;id&gt;_&lt;label&gt;/ with &lt;view&gt;.png, contact_sheet.png,
    /// manifest.json (all parameters + per-view camera pose, also relative to the target, so a later capture with
    /// compare_with reuses them) and compare_&lt;view&gt;.png (old | new) + diff_&lt;view&gt;.png when comparing.
    ///
    /// Rendering notes (Built-in pipeline): camera renders into an MSAA half-float target (ARGB32 sRGB fallback), is
    /// blitted to an sRGB ARGB32 target and read back raw into RGB24, so PNG bytes match what a Game view camera shows
    /// in Linear projects. The camera clear colour convention is measured once per run (background_calibration).
    /// Isolated lighting = preview-scene RenderSettings applied through Unsupported.SetOverrideLightingSettings around
    /// each Camera.Render (same mechanism as PreviewRenderUtility / Prefab Mode): flat ambient, uniform gray reflection
    /// cubemap, key + fill directional lights living only in the preview scene (camera.scene limits culling to it).
    /// Known limits: OnValidate / ISerializationCallbackReceiver of scripts can run during Instantiate; shaders animated
    /// by _Time (lilToon scroll/blink etc.) differ between captures; no post-processing; NDMF/MA edit-mode previews are
    /// not reproduced in isolated mode; scene mode shows whatever stands between camera and target.
    ///
    /// Issue codes:
    /// | code                      | severity | meaning |
    /// |---------------------------|----------|---------|
    /// | TARGET_NOT_FOUND          | error    | target spec did not resolve (result fails) |
    /// | INVALID_ARGS              | error    | no valid view requested (result fails) |
    /// | UNKNOWN_VIEW              | warning  | a requested view / preset name was ignored |
    /// | PARAM_ADJUSTED            | info/warn| a parameter was clamped, unknown, or replaced by its default |
    /// | MODE_FALLBACK             | warning  | scene mode impossible (prefab asset / Prefab Mode) or skybox in isolated -> changed |
    /// | TARGET_INACTIVE           | warning  | target inactive: isolated clone root force-activated (scene mode fails instead) |
    /// | NO_RENDERERS              | error    | no enabled Mesh/SkinnedMeshRenderer under the target (result fails) |
    /// | NO_HUMANOID_HEAD          | warning  | face/upper framing fell back to bounds fractions (no humanoid Animator / head bone) |
    /// | FRAMING_OUTLIERS_EXCLUDED | info     | renderers far from the humanoid skeleton were ignored for auto framing (still rendered) |
    /// | SCRIPTS_STRIPPED          | info     | MonoBehaviours removed from the isolated clone before it was activated |
    /// | SCRIPTS_KEPT              | warning  | keep_scripts=true: [ExecuteAlways] scripts on the clone may have run |
    /// | STRIP_INCOMPLETE          | warning  | some MonoBehaviours could not be removed from the clone |
    /// | BAKE_FAILED               | warning  | a skinned mesh could not be baked; rendered as SkinnedMeshRenderer instead |
    /// | TARGET_HAS_LIGHTS         | info     | the target contains enabled Lights that also light the capture |
    /// | EDITOR_ONLY_VISIBLE       | info     | renderers under EditorOnly-tagged objects are in the image (VRChat strips them on upload) |
    /// | LIGHTING_OVERRIDE_FAILED  | warning  | preview-scene lighting override failed; ambient/reflection may come from the open scene |
    /// | PLAY_MODE                 | info     | captured during Play mode (runtime state is captured) |
    /// | SRP_ACTIVE                | warning  | a scriptable render pipeline is active; this tool targets the Built-in pipeline |
    /// | MSAA_UNAVAILABLE          | info     | render texture MSAA not supported; edges are aliased |
    /// | RENDER_FAILED             | error    | setup or a view's render failed (result fails if nothing was captured) |
    /// | WRITE_FAILED              | error    | a PNG / manifest could not be written |
    /// | CLEANUP_FAILED            | warning  | a temporary object could not be cleaned up |
    /// | SCENE_DIRTIED             | warning  | a loaded scene became dirty during the capture (should not happen) |
    /// | ASSET_DIRTIED             | warning  | the prefab asset target became dirty during the capture (should not happen) |
    /// | COMPARE_NOT_FOUND         | error    | compare_with did not resolve to a readable capture manifest; no comparison |
    /// | COMPARE_MISMATCH          | error    | compare_with used different resolution/framing/lighting params; no comparison |
    /// | COMPARE_MISSING_VIEW      | warning  | a view exists in only one of the two captures |
    /// | COMPARE_FILE_MISSING      | warning  | the previous capture's PNG for a view is missing/unreadable/different size |
    /// | COMPARE_CAMERA_REUSED     | info     | framing changed since the previous capture; its camera pose was reused for alignment |
    /// | COMPARE_CAMERA_DIFFERS    | warning  | reuse_cameras=false and the camera moved: pixel differences include the framing change |
    /// </summary>
    public static partial class CctCaptureViews
    {
        public const string ToolName = "cct_capture_views";
        public const string ManifestFormat = "cct_capture_views/1";
        public const string ManifestFileName = "manifest.json";
        public const string ContactSheetFileName = "contact_sheet.png";
        public const string DefaultViewList = "front,back,left,right";
        public const int MinResolution = 256;
        public const int MaxResolution = 2048;
        public const int DefaultResolution = 768;
        const float DefaultFov = 25f;
        const float DefaultPadding = 0.08f;
        const string ProgressTitle = "Claude Creator Tools";
        static readonly Color32 DefaultBackground = new Color32(128, 128, 128, 255);

        static readonly string[] Modes = { "isolated", "scene" };
        static readonly string[] Backgrounds = { "solid", "transparent", "skybox" };
        static readonly string[] Framings = { "auto", "upper", "face" };

        /// <summary>Parameters that must match for a pixel comparison to be meaningful.</summary>
        static readonly string[] CompareKeys =
        {
            "resolution", "mode", "background", "background_color", "fov", "padding", "framing",
            "light_follow_camera", "keep_scripts",
        };

        sealed class ViewDef
        {
            public readonly string Name;
            public readonly float Azimuth;   // degrees, + = towards the target's right (+X), 0 = in front (+Z)
            public readonly float Elevation; // degrees, + = above
            public readonly bool Face;       // always uses head framing

            public ViewDef(string name, float azimuth, float elevation, bool face)
            {
                Name = name;
                Azimuth = azimuth;
                Elevation = elevation;
                Face = face;
            }
        }

        static readonly ViewDef[] ViewDefs =
        {
            new ViewDef("front", 0f, 0f, false),
            new ViewDef("back", 180f, 0f, false),
            new ViewDef("left", -90f, 0f, false),
            new ViewDef("right", 90f, 0f, false),
            new ViewDef("front_left", -45f, 0f, false),
            new ViewDef("front_right", 45f, 0f, false),
            new ViewDef("back_left", -135f, 0f, false),
            new ViewDef("back_right", 135f, 0f, false),
            new ViewDef("top", 0f, 90f, false),
            new ViewDef("face_front", 0f, 0f, true),
            new ViewDef("face_left45", -45f, 0f, true),
            new ViewDef("face_right45", 45f, 0f, true),
        };

        static readonly Dictionary<string, string[]> Presets = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["turnaround"] = new[] { "front", "front_right", "right", "back", "left", "front_left" },
            ["face"] = new[] { "face_front", "face_left45", "face_right45" },
        };

        /// <summary>Accepted view names (presets 'turnaround' and 'face' are also accepted by the views argument).</summary>
        public static IEnumerable<string> ViewNames => ViewDefs.Select(v => v.Name);

        sealed class Options
        {
            public readonly List<ViewDef> Views = new List<ViewDef>();
            public int Resolution = DefaultResolution;
            public string Mode = "isolated";
            public string Background = "solid";
            public Color32 BackgroundColor = DefaultBackground;
            public float Fov = DefaultFov;
            public float Padding = DefaultPadding;
            public string Framing = "auto";
            public bool LightFollowCamera;
            public bool KeepScripts;
            public bool ContactSheet = true;
            public bool ReuseCameras = true;
            public string Label;

            public bool Transparent => Background == "transparent";

            public Dictionary<string, object> ToPlain() => new Dictionary<string, object>
            {
                ["views"] = Views.Select(v => (object)v.Name).ToList(),
                ["resolution"] = Resolution,
                ["mode"] = Mode,
                ["background"] = Background,
                ["background_color"] = Hex(BackgroundColor),
                ["fov"] = Math.Round(Fov, 4),
                ["padding"] = Math.Round(Padding, 4),
                ["framing"] = Framing,
                ["light_follow_camera"] = LightFollowCamera,
                ["keep_scripts"] = KeepScripts,
                ["contact_sheet"] = ContactSheet,
            };
        }

        public static CctResult Run(CctArgs args)
        {
            var r = CctResult.Ok("");

            // compare_with first: unspecified parameters are inherited from the previous capture.
            PreviousCapture prev = null;
            var compareSpec = args.GetString("compare_with");
            if (!string.IsNullOrWhiteSpace(compareSpec))
            {
                prev = PreviousCapture.Load(compareSpec, out var loadError);
                if (prev == null)
                    r.AddIssue(CctSeverity.Error, "COMPARE_NOT_FOUND", loadError + " (比較は行わず撮影のみ行います)", value: compareSpec);
            }

            var opt = ParseOptions(args, prev, r, out var optError);
            if (opt == null) return CctResult.Fail(optError, "INVALID_ARGS");

            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return CctResult.Fail("グラフィックスデバイスが無い状態 (-nographics / batchmode) では描画できません", "RENDER_FAILED");

            var resolved = CctTargets.Resolve(args.GetString("target"));
            if (!resolved.Ok)
                return CctResult.Fail(resolved.Error, "TARGET_NOT_FOUND", resolved.Candidates.Count > 0 ? resolved.Candidates : null);
            var target = resolved.GameObject;
            bool isAsset = resolved.IsPrefabAsset || EditorUtility.IsPersistent(target);
            bool inPreviewScene = !isAsset && target.scene.IsValid() && EditorSceneManager.IsPreviewScene(target.scene);

            if (opt.Mode == "scene" && (isAsset || inPreviewScene))
            {
                opt.Mode = "isolated";
                r.AddIssue(CctSeverity.Warning, "MODE_FALLBACK", isAsset
                    ? "プレハブアセットはシーンに置かれていないため isolated モードで撮影しました"
                    : "Prefab Mode 内のオブジェクトはシーン照明で撮影できないため isolated モードで撮影しました");
            }
            if (opt.Mode == "isolated" && opt.Background == "skybox")
            {
                opt.Background = "solid";
                r.AddIssue(CctSeverity.Warning, "MODE_FALLBACK", "background=skybox は scene モード専用です。単色背景で撮影しました");
            }

            bool active = isAsset ? CctTargets.IsEffectivelyActive(target.transform) : target.activeInHierarchy;
            if (!active)
            {
                if (opt.Mode == "scene")
                    return CctResult.Fail("対象が非アクティブなのでシーン上では写りません。mode=isolated (クローンをアクティブにして撮影) を使ってください", "TARGET_INACTIVE");
                r.AddIssue(CctSeverity.Warning, "TARGET_INACTIVE",
                    "対象が非アクティブです。isolated のクローン上でルートだけをアクティブにして撮影しました (非アクティブな子はそのまま)",
                    path: CctTargets.GetPath(target));
            }
            if (EditorApplication.isPlaying)
                r.AddIssue(CctSeverity.Info, "PLAY_MODE", "Play モード中の状態を撮影しました (編集モードとは姿勢・表示が異なる場合があります)");
            if (GraphicsSettings.currentRenderPipeline != null)
                r.AddIssue(CctSeverity.Warning, "SRP_ACTIVE", "スクリプタブルレンダーパイプラインが有効です。このツールは Built-in パイプライン前提のため照明が再現されない可能性があります");

            bool compareEnabled = prev != null;
            if (prev != null)
            {
                var mismatches = FindMismatches(opt, prev);
                if (mismatches.Count > 0)
                {
                    compareEnabled = false;
                    r.AddIssue(CctSeverity.Error, "COMPARE_MISMATCH",
                        "比較元と撮影条件が違うため画素比較をしませんでした: " + string.Join(", ", mismatches.Keys) +
                        " (未指定にすれば比較元の値を引き継ぎます)", value: mismatches);
                }
                else ReportViewSetDifferences(opt, prev, r);
            }

            var dirtyBefore = SnapshotDirty(target, inPreviewScene);
            bool assetDirtyBefore = isAsset && EditorUtility.IsDirty(target);

            // Output folder.
            var labelText = string.IsNullOrWhiteSpace(opt.Label) ? target.name : opt.Label;
            var folderName = CctPaths.NewId() + "_" + CctPaths.SafeName(labelText, 40);
            var dir = CctPaths.Combine(CctPaths.CapturesDir, folderName);
            if (Directory.Exists(dir))
            {
                folderName += "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                dir = CctPaths.Combine(CctPaths.CapturesDir, folderName);
            }
            CctPaths.EnsureDir(dir);

            int res = opt.Resolution;
            ContactLayout(opt.Views.Count, res, out int sheetCols, out int sheetRows, out int sheetCell);
            var files = new List<string>();
            var viewResults = new List<object>();
            var manifestViews = new List<object>();
            var comparisons = new List<object>();
            var sheetCells = new List<Color32[]>();
            var sheetLabels = new List<string>();
            var reusedViews = new List<string>();
            var movedViews = new List<string>();
            var missingFiles = new List<string>();
            FramingInfo framing = null;
            CaptureSession session = null;
            Dictionary<string, object> renderInfo = null;
            CctResult failure = null;
            bool writeFailed = false;

            try
            {
                EditorUtility.DisplayProgressBar(ProgressTitle, "撮影の準備中: " + target.name, 0f);
                try
                {
                    session = new CaptureSession(opt.Mode == "isolated", target);
                    if (session.Isolated) session.InitIsolated(opt, r);
                    else session.InitScene(r);
                }
                catch (Exception e)
                {
                    failure = FailWith(r, "撮影の準備に失敗しました: " + e.GetType().Name + ": " + e.Message, "RENDER_FAILED", e);
                }

                if (failure == null && session.FramingRenderers.Count == 0)
                {
                    failure = FailWith(r, session.RenderedRenderers == 0
                        ? "対象の下に有効な Renderer がありません (非アクティブ・無効を除く)"
                        : "対象の下に MeshRenderer / SkinnedMeshRenderer がありません (パーティクル等だけでは構図を決められません)",
                        "NO_RENDERERS", null);
                }

                if (failure == null)
                {
                    if (session.EditorOnlyRenderers > 0)
                        r.AddIssue(CctSeverity.Info, "EDITOR_ONLY_VISIBLE",
                            $"EditorOnly タグの下の Renderer {session.EditorOnlyRenderers} 個も写っています (VRChat へのアップロードでは除外されます)",
                            value: session.EditorOnlyRenderers);
                    framing = ComputeFraming(session, opt, r);
                    try
                    {
                        session.PrepareRendering(opt, r);
                        renderInfo = session.DescribeRendering(opt);
                    }
                    catch (Exception e)
                    {
                        failure = FailWith(r, "カメラ / RenderTexture の準備に失敗しました: " + e.GetType().Name + ": " + e.Message, "RENDER_FAILED", e);
                    }
                }

                for (int i = 0; failure == null && i < opt.Views.Count && !writeFailed; i++)
                {
                    var view = opt.Views[i];
                    EditorUtility.DisplayProgressBar(ProgressTitle, $"撮影中: {view.Name} ({i + 1}/{opt.Views.Count})", (float)i / opt.Views.Count);

                    var framingKind = view.Face ? "face" : opt.Framing;
                    var pose = FitCamera(framing.BoxFor(framingKind), framing.Full, view, opt.Fov, opt.Padding, opt.Mode == "scene");
                    var cameraSource = "computed";
                    Dictionary<string, object> prevView = null;
                    if (compareEnabled && prev.Views.TryGetValue(view.Name, out prevView) && TryReadPose(prevView, out var oldPose))
                    {
                        if (SamePose(pose, oldPose)) cameraSource = "computed_matches_previous";
                        else if (opt.ReuseCameras)
                        {
                            pose = oldPose;
                            cameraSource = "reused_from_previous";
                            reusedViews.Add(view.Name);
                        }
                        else
                        {
                            cameraSource = "computed_differs_from_previous";
                            movedViews.Add(view.Name);
                        }
                    }

                    var worldPos = session.Frame.ToWorld(pose.Position);
                    var worldRot = session.Frame.Rotation * pose.Rotation;
                    Color32[] pixels;
                    try
                    {
                        session.AimLights(view, opt.LightFollowCamera);
                        pixels = session.Render(worldPos, worldRot, pose, opt);
                    }
                    catch (Exception e)
                    {
                        r.AddIssue(CctSeverity.Error, "RENDER_FAILED", $"{view.Name} の描画に失敗しました: {e.GetType().Name}: {e.Message}", value: view.Name);
                        continue;
                    }

                    var fileName = view.Name + ".png";
                    var filePath = CctPaths.Combine(dir, fileName);
                    if (!TryWrite(filePath, EncodePng(pixels, res, res, opt.Transparent), r)) { writeFailed = true; break; }
                    files.Add(filePath);

                    var flat = opt.Transparent ? Composite(pixels, opt.BackgroundColor) : pixels;
                    if (opt.ContactSheet)
                    {
                        sheetCells.Add(sheetCell == res ? flat : Downscale(flat, res, res, sheetCell, sheetCell));
                        sheetLabels.Add(view.Name);
                    }

                    var cam = new Dictionary<string, object>
                    {
                        ["position"] = Vec(worldPos),
                        ["rotation"] = Quat(worldRot),
                        ["euler"] = Vec(worldRot.eulerAngles),
                        ["look_at"] = Vec(session.Frame.ToWorld(pose.LookAt)),
                        ["fov"] = (double)pose.Fov,
                        ["near"] = (double)pose.Near,
                        ["far"] = (double)pose.Far,
                        ["distance"] = (double)pose.Distance,
                        ["local_position"] = Vec(pose.Position),
                        ["local_rotation"] = Quat(pose.Rotation),
                        ["local_look_at"] = Vec(pose.LookAt),
                    };
                    manifestViews.Add(new Dictionary<string, object>
                    {
                        ["name"] = view.Name,
                        ["file"] = fileName,
                        ["framing"] = framingKind,
                        ["camera_source"] = cameraSource,
                        ["camera"] = cam,
                    });
                    viewResults.Add(new Dictionary<string, object>
                    {
                        ["view"] = view.Name,
                        ["file"] = CctPaths.ToDisplayPath(filePath),
                        ["framing"] = framingKind,
                        ["camera_source"] = cameraSource,
                        ["camera"] = new Dictionary<string, object>
                        {
                            ["position"] = Rounded(worldPos, 4),
                            ["euler"] = Rounded(worldRot.eulerAngles, 2),
                            ["fov"] = Math.Round(pose.Fov, 3),
                            ["distance"] = Math.Round(pose.Distance, 4),
                            ["near"] = Math.Round(pose.Near, 4),
                            ["far"] = Math.Round(pose.Far, 3),
                        },
                    });

                    if (compareEnabled && prevView != null)
                    {
                        var cmp = CompareWithPrevious(view.Name, flat, res, opt, prev, prevView, dir, files, r, missingFiles);
                        if (cmp != null)
                        {
                            cmp["camera_source"] = cameraSource;
                            comparisons.Add(cmp);
                        }
                    }
                }
            }
            finally
            {
                session?.Dispose();
                EditorUtility.ClearProgressBar();
            }

            var sink = failure ?? r;
            if (session != null)
                foreach (var err in session.CleanupErrors)
                    sink.AddIssue(CctSeverity.Warning, "CLEANUP_FAILED", "一時オブジェクトの後片付けで問題がありました: " + err);
            CheckDirty(dirtyBefore, sink);
            if (isAsset && !assetDirtyBefore && target != null && EditorUtility.IsDirty(target))
                sink.AddIssue(CctSeverity.Warning, "ASSET_DIRTIED", "撮影中にプレハブアセットが変更扱いになりました (保存せずに再読み込みしてください)",
                    asset: AssetDatabase.GetAssetPath(target));
            if (failure != null)
            {
                TryDeleteEmptyDir(dir);
                return failure;
            }

            if (session != null && session.LightingOverrideFailed)
                r.AddIssue(CctSeverity.Warning, "LIGHTING_OVERRIDE_FAILED",
                    "プレビューシーンの照明設定に切り替えられませんでした。環境光・反射が開いているシーンのものになっている可能性があります");
            if (reusedViews.Count > 0)
                r.AddIssue(CctSeverity.Info, "COMPARE_CAMERA_REUSED",
                    "比較元から構図 (境界) が変わったため、比較元のカメラ位置を再利用して位置を揃えました: " + string.Join(", ", reusedViews));
            if (movedViews.Count > 0)
                r.AddIssue(CctSeverity.Warning, "COMPARE_CAMERA_DIFFERS",
                    "reuse_cameras=false でカメラ位置が比較元と異なります。差分には構図のずれが含まれます: " + string.Join(", ", movedViews));
            if (missingFiles.Count > 0)
                r.AddIssue(CctSeverity.Warning, "COMPARE_FILE_MISSING",
                    "比較元の画像を読めなかった (または解像度が違う) view があります: " + string.Join(", ", missingFiles));

            if (manifestViews.Count == 0)
            {
                TryDeleteEmptyDir(dir);
                return FailWith(r, writeFailed ? "画像を書き込めませんでした" : "どの view も描画できませんでした", writeFailed ? "WRITE_FAILED" : "RENDER_FAILED", null);
            }

            string sheetPath = null;
            if (opt.ContactSheet && sheetCells.Count > 0 && !writeFailed)
            {
                var sheet = BuildContactSheet(sheetCells, sheetLabels, sheetCell, sheetCols, sheetRows, out int sw, out int sh);
                var p = CctPaths.Combine(dir, ContactSheetFileName);
                if (TryWrite(p, EncodePng(sheet, sw, sh, false), r))
                {
                    sheetPath = p;
                    files.Add(p);
                }
            }

            var framingPlain = framing?.ToPlain();
            var targetInfo = CctTargets.Describe(target);
            var manifestPath = CctPaths.Combine(dir, ManifestFileName);
            var manifest = new Dictionary<string, object>
            {
                ["format"] = ManifestFormat,
                ["id"] = folderName,
                ["label"] = opt.Label,
                ["created_at"] = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                ["tool_version"] = CctRunner.PackageVersion,
                ["unity_version"] = Application.unityVersion,
                ["color_space"] = QualitySettings.activeColorSpace.ToString(),
                ["graphics_device"] = SystemInfo.graphicsDeviceType.ToString(),
                ["target"] = targetInfo,
                ["frame"] = session?.Frame.ToPlain(),
                ["params"] = opt.ToPlain(),
                ["render"] = renderInfo,
                ["framing"] = framingPlain,
                ["views"] = manifestViews,
                ["contact_sheet"] = sheetPath != null ? ContactSheetFileName : null,
                ["compare_with"] = prev?.Dir,
                ["comparisons"] = comparisons,
                ["files"] = files.Select(f => (object)Path.GetFileName(f)).Concat(new object[] { ManifestFileName }).ToList(),
            };
            if (TryWrite(manifestPath, Encoding.UTF8.GetBytes(CctJson.Serialize(manifest, true)), r)) files.Add(manifestPath);

            r.Put("capture_dir", CctPaths.ToDisplayPath(dir));
            r.Put("contact_sheet", sheetPath != null ? CctPaths.ToDisplayPath(sheetPath) : null);
            r.Put("manifest", CctPaths.ToDisplayPath(manifestPath));
            r.Put("files", files.Select(f => (object)CctPaths.ToDisplayPath(f)).ToList());
            r.Put("views", viewResults);
            if (prev != null) r.Put("compare_with", prev.Dir);
            if (comparisons.Count > 0 || prev != null) r.Put("comparisons", comparisons);
            r.Put("target", targetInfo);
            r.Put("params", opt.ToPlain());
            r.Put("framing", framingPlain);
            r.Put("render", renderInfo);

            var msg = new StringBuilder();
            msg.Append($"{manifestViews.Count} 方向を撮影しました ({opt.Mode}, {res}px, {opt.Background})");
            if (manifestViews.Count < opt.Views.Count) msg.Append($" ※{opt.Views.Count - manifestViews.Count} 方向は失敗");
            msg.Append(sheetPath != null ? $"。一覧画像: {CctPaths.ToDisplayPath(sheetPath)}" : $"。保存先: {CctPaths.ToDisplayPath(dir)}");
            if (comparisons.Count > 0)
            {
                msg.Append(" / 比較: ");
                msg.Append(string.Join(", ", comparisons.Take(4).Select(c =>
                {
                    var d = (Dictionary<string, object>)c;
                    return $"{d["view"]} 平均差{Convert.ToDouble(d["mean_abs_diff"], CultureInfo.InvariantCulture):0.##} 変化{Convert.ToDouble(d["changed_pct"], CultureInfo.InvariantCulture):0.##}%";
                })));
                if (comparisons.Count > 4) msg.Append($" ほか {comparisons.Count - 4} 件");
            }
            r.Message = msg.ToString();
            return r;
        }

        // ------------------------------------------------------------------ options

        static Options ParseOptions(CctArgs args, PreviousCapture prev, CctResult r, out string error)
        {
            error = null;
            var o = new Options();
            var p = prev?.Params;

            List<string> requested;
            if (Given(args, "views")) requested = args.GetStringList("views");
            else if (p != null && TryGetStringList(p, "views", out var inherited) && inherited.Count > 0) requested = inherited;
            else requested = DefaultViewList.Split(',').ToList();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in requested)
            {
                var key = (raw ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
                if (key.Length == 0) continue;
                var names = Presets.TryGetValue(key, out var preset) ? preset : new[] { key };
                foreach (var n in names)
                {
                    var def = ViewDefs.FirstOrDefault(v => v.Name == n);
                    if (def == null)
                    {
                        r.AddIssue(CctSeverity.Warning, "UNKNOWN_VIEW",
                            $"不明な view '{raw}' を無視しました (使用可: {string.Join(", ", ViewNames)} / プリセット: turnaround, face)", value: raw);
                        continue;
                    }
                    if (seen.Add(def.Name)) o.Views.Add(def);
                }
            }
            if (o.Views.Count == 0)
            {
                error = $"有効な view がありません。使用可: {string.Join(", ", ViewNames)} / プリセット: turnaround, face";
                return null;
            }

            o.Resolution = ReadInt(args, p, "resolution", DefaultResolution, MinResolution, MaxResolution, r, zeroMeansUnset: true);
            o.Mode = ReadEnum(args, p, "mode", "isolated", Modes, r);
            o.Background = ReadEnum(args, p, "background", "solid", Backgrounds, r);
            o.Framing = ReadEnum(args, p, "framing", "auto", Framings, r);
            o.Fov = ReadFloat(args, p, "fov", DefaultFov, 5f, 90f, r, zeroMeansUnset: true);
            o.Padding = ReadFloat(args, p, "padding", DefaultPadding, 0f, 0.4f, r);
            o.LightFollowCamera = ReadBool(args, p, "light_follow_camera", false);
            o.KeepScripts = ReadBool(args, p, "keep_scripts", false);
            o.ContactSheet = args.GetBool("contact_sheet", true);
            o.ReuseCameras = args.GetBool("reuse_cameras", true);
            o.Label = Given(args, "label") ? args.GetString("label").Trim() : null;

            string colorText = Given(args, "background_color") ? args.GetString("background_color")
                : p != null && p.TryGetValue("background_color", out var pc) && pc is string pcs ? pcs : null;
            if (colorText != null)
            {
                if (TryParseHex(colorText, out var c)) o.BackgroundColor = c;
                else r.AddIssue(CctSeverity.Warning, "PARAM_ADJUSTED",
                    $"background_color '{colorText}' を解釈できないため {Hex(DefaultBackground)} を使いました (#RRGGBB 形式)", value: colorText);
            }
            return o;
        }

        /// <summary>Present and not an empty string (MCP clients may send "" for omitted optional strings).</summary>
        static bool Given(CctArgs args, string key)
        {
            if (!args.TryGetRaw(key, out var v) || v == null) return false;
            return !(v is string s) || s.Trim().Length > 0;
        }

        /// <param name="zeroMeansUnset">0 is not a meaningful value for this key: treat it as omitted (a client may zero-fill).</param>
        static int ReadInt(CctArgs args, Dictionary<string, object> inherited, string key, int fallback, int min, int max, CctResult r,
            bool zeroMeansUnset = false)
        {
            long value = fallback;
            if (Given(args, key) && !(zeroMeansUnset && args.GetLong(key, -1) == 0))
            {
                value = args.GetLong(key, long.MinValue);
                if (value == long.MinValue)
                {
                    r.AddIssue(CctSeverity.Warning, "PARAM_ADJUSTED", $"{key} を数値として読めないため既定値 {fallback} を使いました", value: args.GetString(key));
                    value = fallback;
                }
            }
            else if (inherited != null && inherited.TryGetValue(key, out var iv) && IsNumber(iv))
                value = Convert.ToInt64(iv, CultureInfo.InvariantCulture);
            if (value < min || value > max)
            {
                var clamped = Math.Max(min, Math.Min(max, value));
                r.AddIssue(CctSeverity.Info, "PARAM_ADJUSTED", $"{key}={value} を範囲 {min}..{max} に収めて {clamped} にしました", value: value, limit: clamped);
                value = clamped;
            }
            return (int)value;
        }

        static float ReadFloat(CctArgs args, Dictionary<string, object> inherited, string key, float fallback, float min, float max, CctResult r,
            bool zeroMeansUnset = false)
        {
            double value = fallback;
            if (Given(args, key) && !(zeroMeansUnset && Math.Abs(args.GetDouble(key, -1)) < 1e-9))
            {
                value = args.GetDouble(key, double.NaN);
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    r.AddIssue(CctSeverity.Warning, "PARAM_ADJUSTED", $"{key} を数値として読めないため既定値 {fallback} を使いました", value: args.GetString(key));
                    value = fallback;
                }
            }
            else if (inherited != null && inherited.TryGetValue(key, out var iv) && IsNumber(iv))
                value = Convert.ToDouble(iv, CultureInfo.InvariantCulture);
            if (value < min || value > max)
            {
                var clamped = Math.Max(min, Math.Min(max, value));
                r.AddIssue(CctSeverity.Info, "PARAM_ADJUSTED", $"{key}={value.ToString(CultureInfo.InvariantCulture)} を範囲 {min}..{max} に収めました", value: value, limit: clamped);
                value = clamped;
            }
            return (float)value;
        }

        static string ReadEnum(CctArgs args, Dictionary<string, object> inherited, string key, string fallback, string[] allowed, CctResult r)
        {
            if (inherited != null && inherited.TryGetValue(key, out var iv) && iv is string ivs && allowed.Contains(ivs)) fallback = ivs;
            if (!Given(args, key)) return fallback;
            var s = args.GetString(key).Trim().ToLowerInvariant();
            if (allowed.Contains(s)) return s;
            r.AddIssue(CctSeverity.Warning, "PARAM_ADJUSTED", $"{key}='{s}' は不明な値です ({string.Join(" / ", allowed)})。'{fallback}' を使いました", value: s);
            return fallback;
        }

        static bool ReadBool(CctArgs args, Dictionary<string, object> inherited, string key, bool fallback)
        {
            if (Given(args, key)) return args.GetBool(key, fallback);
            if (inherited != null && inherited.TryGetValue(key, out var iv) && iv is bool b) return b;
            return fallback;
        }

        static Dictionary<string, object> FindMismatches(Options o, PreviousCapture prev)
        {
            var current = o.ToPlain();
            var result = new Dictionary<string, object>();
            foreach (var key in CompareKeys)
            {
                if (!prev.Params.TryGetValue(key, out var pv)) continue; // older manifest without the key
                current.TryGetValue(key, out var cv);
                if (SameValue(pv, cv)) continue;
                result[key] = new Dictionary<string, object> { ["previous"] = pv, ["current"] = cv };
            }
            return result;
        }

        static void ReportViewSetDifferences(Options o, PreviousCapture prev, CctResult r)
        {
            var current = o.Views.Select(v => v.Name).ToList();
            var onlyNew = current.Where(v => !prev.Views.ContainsKey(v)).ToList();
            var onlyOld = prev.Views.Keys.Where(v => !current.Contains(v)).ToList();
            if (onlyNew.Count > 0)
                r.AddIssue(CctSeverity.Warning, "COMPARE_MISSING_VIEW", "比較元に無い view は比較していません: " + string.Join(", ", onlyNew), value: onlyNew);
            if (onlyOld.Count > 0)
                r.AddIssue(CctSeverity.Warning, "COMPARE_MISSING_VIEW", "比較元にあるが今回撮影しなかった view: " + string.Join(", ", onlyOld), value: onlyOld);
        }

        // ------------------------------------------------------------------ scene dirtiness

        static List<KeyValuePair<Scene, bool>> SnapshotDirty(GameObject target, bool includeTargetScene)
        {
            var list = CctTargets.LoadedScenes().Select(s => new KeyValuePair<Scene, bool>(s, s.isDirty)).ToList();
            if (includeTargetScene && target != null && target.scene.IsValid() && list.All(kv => kv.Key != target.scene))
                list.Add(new KeyValuePair<Scene, bool>(target.scene, target.scene.isDirty));
            return list;
        }

        static void CheckDirty(List<KeyValuePair<Scene, bool>> before, CctResult r)
        {
            foreach (var kv in before)
            {
                var scene = kv.Key;
                if (kv.Value || !scene.IsValid() || !scene.isLoaded || !scene.isDirty) continue;
                r.AddIssue(CctSeverity.Warning, "SCENE_DIRTIED",
                    $"撮影中にシーン '{scene.name}' が未保存扱いになりました。このツールはシーンを変更しないはずなので、保存前に変更内容を確認してください",
                    value: scene.path);
            }
        }

        // ------------------------------------------------------------------ small helpers

        static CctResult FailWith(CctResult collected, string message, string code, Exception e)
        {
            var details = new Dictionary<string, object>();
            if (e != null) details["exception"] = e.GetType().FullName + ": " + e.Message;
            if (e?.StackTrace != null) details["stack"] = e.StackTrace.Length > 1500 ? e.StackTrace.Substring(0, 1500) + "..." : e.StackTrace;
            var fail = CctResult.Fail(message, code, details.Count > 0 ? details : null);
            foreach (var issue in collected.Issues) fail.Issues.Add(issue);
            fail.AddIssue(CctSeverity.Error, code, message);
            return fail;
        }

        static bool TryWrite(string path, byte[] bytes, CctResult r)
        {
            try
            {
                File.WriteAllBytes(path, bytes);
                return true;
            }
            catch (Exception e)
            {
                r.AddIssue(CctSeverity.Error, "WRITE_FAILED", $"ファイルを書き込めませんでした: {e.Message}", value: CctPaths.ToDisplayPath(path));
                return false;
            }
        }

        static void TryDeleteEmptyDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (Exception) { /* leaving an empty folder is harmless */ }
        }

        static bool IsNumber(object v) =>
            v is long || v is int || v is double || v is float || v is short || v is byte || v is decimal || v is uint || v is ulong;

        static bool SameValue(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (IsNumber(a) && IsNumber(b))
                return Math.Abs(Convert.ToDouble(a, CultureInfo.InvariantCulture) - Convert.ToDouble(b, CultureInfo.InvariantCulture)) < 1e-4;
            return string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase);
        }

        static bool TryGetStringList(Dictionary<string, object> d, string key, out List<string> list)
        {
            list = null;
            if (d == null || !d.TryGetValue(key, out var v) || !(v is List<object> l)) return false;
            list = l.Where(x => x != null).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToList();
            return true;
        }

        static double[] Vec(Vector3 v) => new double[] { v.x, v.y, v.z };
        static double[] Quat(Quaternion q) => new double[] { q.x, q.y, q.z, q.w };
        static double[] Rounded(Vector3 v, int digits) =>
            new[] { Math.Round(v.x, digits), Math.Round(v.y, digits), Math.Round(v.z, digits) };

        static string Hex(Color32 c) => $"#{c.r:X2}{c.g:X2}{c.b:X2}";

        static bool TryParseHex(string text, out Color32 color)
        {
            color = DefaultBackground;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var s = text.Trim().TrimStart('#');
            if (s.Length == 3) s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
            if (s.Length != 6 && s.Length != 8) return false;
            if (!uint.TryParse(s.Substring(0, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
            color = new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
            return true;
        }
    }
}
