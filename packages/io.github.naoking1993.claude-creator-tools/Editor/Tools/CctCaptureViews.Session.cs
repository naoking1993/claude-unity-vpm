using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    public static partial class CctCaptureViews
    {
        /// <summary>
        /// Yaw-only reference frame: origin = target pivot, +Y = world up, +Z = forward of the orientation source (the target
        /// itself, or the enclosing avatar root when the target is a part of an avatar; see FindOrientationSource).
        /// </summary>
        sealed class CaptureFrame
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public float Yaw;
            public string OrientationSource;

            /// <param name="pivot">origin (the target, or its clone at the same world pose)</param>
            /// <param name="orientation">transform whose forward is "front"; null = the pivot</param>
            public static CaptureFrame From(Transform pivot, Transform orientation)
            {
                var t = orientation != null ? orientation : pivot;
                var f = t.forward;
                f.y = 0f;
                if (f.sqrMagnitude < 1e-6f)
                {
                    // Root pitched by 90 degrees, typically the unapplied Blender FBX rotation (-90, 0, 0): its local -Y
                    // (Blender's front) points to world +Z, so -up is the model's front (+up would capture its back).
                    f = -t.up;
                    f.y = 0f;
                }
                if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
                var yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
                return new CaptureFrame
                {
                    Position = pivot.position,
                    Yaw = yaw,
                    Rotation = Quaternion.Euler(0f, yaw, 0f),
                    OrientationSource = CctTargets.GetPath(t),
                };
            }

            public Vector3 ToLocal(Vector3 world) => Quaternion.Inverse(Rotation) * (world - Position);
            public Vector3 ToWorld(Vector3 local) => Position + Rotation * local;

            public Dictionary<string, object> ToPlain() => new Dictionary<string, object>
            {
                ["position"] = Vec(Position),
                ["yaw"] = (double)Yaw,
                ["orientation_source"] = OrientationSource,
                ["note"] = "camera local_* values are relative to this frame (origin = target pivot, +Z = forward of " +
                           "orientation_source, +Y = world up)",
            };
        }

        /// <summary>
        /// The transform whose forward defines "front": the nearest self-or-ancestor with a VRCAvatarDescriptor, else with a
        /// humanoid Animator, else the target. Capturing a part of an avatar (outfit, hair, a mesh child with an unapplied
        /// FBX rotation) therefore still uses the avatar's front, and before/after captures of that part line up.
        /// </summary>
        static Transform FindOrientationSource(Transform target)
        {
            for (var t = target; t != null; t = t.parent)
                foreach (var c in t.GetComponents<Component>())
                    if (c != null && CctSerialized.IsType(c, CctVrcTypes.AvatarDescriptor)) return t;
            for (var t = target; t != null; t = t.parent)
                if (IsHumanAnimator(t.GetComponent<Animator>())) return t;
            return target;
        }

        /// <summary>World-space corner points of one renderer's geometry, used for framing.</summary>
        sealed class FramingRenderer
        {
            public readonly string Path;
            public readonly Vector3[] Corners;

            public FramingRenderer(string path, Vector3[] corners)
            {
                Path = path;
                Corners = corners;
            }
        }

        /// <summary>
        /// Owns every temporary object of one capture. Isolated mode: a preview scene holding a script-free clone
        /// (skinned meshes baked to static meshes so the current pose/BlendShapes are exact within this call),
        /// a key + fill light and the camera. Scene mode: only a HideAndDontSave camera in the open scenes.
        /// </summary>
        sealed class CaptureSession : IDisposable
        {
            // Neutral studio lighting (frame-relative, or view-relative with light_follow_camera).
            const float KeyAzimuth = -35f, KeyElevation = 40f, KeyIntensity = 1.0f;
            const float FillAzimuth = 145f, FillElevation = 20f, FillIntensity = 0.35f;
            static readonly Color FillColor = new Color(0.92f, 0.95f, 1f, 1f);
            static readonly Color AmbientColor = new Color(0.45f, 0.45f, 0.45f, 1f);
            const float ReflectionGray = 0.5f;

            public readonly bool Isolated;
            public readonly GameObject Source;
            public GameObject Root;
            public Scene PreviewScene;
            public CaptureFrame Frame;
            public HumanoidInfo Humanoid;
            public readonly List<FramingRenderer> FramingRenderers = new List<FramingRenderer>();
            public int RenderedRenderers;
            public int BakedMeshes;
            public int ScriptsStripped;
            public int ScriptsRemaining;
            public int TargetLights;
            public int EditorOnlyRenderers;
            public int StaticBatchedRenderers;
            public bool LightingOverrideFailed;
            public readonly List<string> CleanupErrors = new List<string>();
            readonly List<string> _bakeFailedPaths = new List<string>();
            string _bakeFailedReason;

            public Camera Camera;
            public int Resolution;
            public int Msaa = 1;
            public bool HdrTarget;
            public bool Linear;
            public Color CameraBackground;
            public string BackgroundCalibration;

            readonly List<Object> _owned = new List<Object>();
            GameObject _holder;
            Light _key, _fill;
            Cubemap _reflection;
            RenderTexture _rtMain, _rtResolve;
            Texture2D _readTex;
            bool _disposed;

            /// <summary>Construct, then call <see cref="InitIsolated"/> or <see cref="InitScene"/>; always Dispose (also after a failed init).</summary>
            public CaptureSession(bool isolated, GameObject source)
            {
                Isolated = isolated;
                Source = source;
            }

            // ---------------------------------------------------------- creation

            public void InitIsolated(Options opt, CctResult r)
            {
                PreviewScene = EditorSceneManager.NewPreviewScene();
                if (!PreviewScene.IsValid()) throw new InvalidOperationException("プレビューシーンを作成できませんでした");

                // The clone is created under an inactive holder, so no Awake/OnEnable of any script runs before the
                // scripts are stripped. Instantiate with a parent creates it directly in the preview scene (never in
                // the user's scene), at the source's world pose.
                _holder = NewObject("CCT Capture Holder");
                _holder.SetActive(false);
                var clone = Object.Instantiate(Source, _holder.transform, true);
                clone.name = Source.name;
                if (!clone.activeSelf) clone.SetActive(true);
                Root = clone;

                // Same hierarchy and component order, so index i maps a clone renderer to its source renderer.
                var sourceRenderers = Source.GetComponentsInChildren<Renderer>(true);
                var cloneRenderers = clone.GetComponentsInChildren<Renderer>(true);
                var sameShape = sourceRenderers.Length == cloneRenderers.Length;

                StripClone(opt.KeepScripts);
                if (opt.KeepScripts)
                    r.AddIssue(CctSeverity.Warning, "SCRIPTS_KEPT",
                        "keep_scripts=true: クローン上のスクリプトを残しました (" +
                        (EditorApplication.isPlaying
                            ? "Play モード中なので全スクリプトの Awake / OnEnable がクローン上で動きました。Udon 等が実行中の状態に影響した可能性があります"
                            : "[ExecuteAlways] 等が編集モードで動いた可能性があります") +
                        $")。VRChat の PhysBone / Contact / Constraint は元オブジェクトを動かさないよう常に除去します (除去 {ScriptsStripped} 個)");
                else if (ScriptsStripped > 0)
                    r.AddIssue(CctSeverity.Info, "SCRIPTS_STRIPPED",
                        $"撮影用クローンからスクリプト {ScriptsStripped} 個を有効化前に取り除きました (PhysBone・Constraint・MA 等は動作しません。元のオブジェクトは無変更)",
                        value: ScriptsStripped);
                if (ScriptsRemaining > 0)
                    r.AddIssue(CctSeverity.Warning, "STRIP_INCOMPLETE",
                        $"クローンから取り除けなかったスクリプトが {ScriptsRemaining} 個あります (有効化時に動作した可能性があります)", value: ScriptsRemaining);

                _holder.SetActive(true);
                // The clone sits at the source's world pose, so the source-side avatar root gives the same orientation.
                Frame = CaptureFrame.From(clone.transform, FindOrientationSource(Source.transform));
                Humanoid = FindHumanoid(Source, EditorUtility.IsPersistent(Source));

                for (int i = 0; i < cloneRenderers.Length; i++)
                    CollectIsolatedRenderer(cloneRenderers[i], sameShape ? sourceRenderers[i] : null, r);
                ReportBakeFailures(r);
                if (StaticBatchedRenderers > 0)
                    r.AddIssue(CctSeverity.Warning, "STATIC_BATCHED",
                        $"静的バッチ済みの Renderer が {StaticBatchedRenderers} 個あります (Play モード)。isolated のクローンでは結合メッシュの都合で正しく描画されない場合があるため、mode=scene か編集モードで撮影してください",
                        value: StaticBatchedRenderers);

                TargetLights = clone.GetComponentsInChildren<Light>(false).Count(l => l != null && l.enabled);
                if (TargetLights > 0)
                    r.AddIssue(CctSeverity.Info, "TARGET_HAS_LIGHTS",
                        $"対象に有効な Light が {TargetLights} 個あり、撮影の照明にも加わっています", value: TargetLights);

                _key = NewLight("CCT Key Light", Color.white, KeyIntensity);
                _fill = NewLight("CCT Fill Light", FillColor, FillIntensity);
            }

            public void InitScene(CctResult r)
            {
                Root = Source;
                Frame = CaptureFrame.From(Source.transform, FindOrientationSource(Source.transform));
                Humanoid = FindHumanoid(Source, false);
                foreach (var rend in Source.GetComponentsInChildren<Renderer>(false))
                    CollectSceneRenderer(rend, r);
                ReportBakeFailures(r);
            }

            /// <summary>One BAKE_FAILED issue for all skinned meshes (Play mode can fail for every non-readable mesh at once).</summary>
            void ReportBakeFailures(CctResult r)
            {
                if (_bakeFailedPaths.Count == 0) return;
                var list = string.Join(", ", _bakeFailedPaths.Take(5)) + (_bakeFailedPaths.Count > 5 ? $" ほか {_bakeFailedPaths.Count - 5} 個" : "");
                var effect = Isolated
                    ? "SkinnedMeshRenderer のまま描画しました (同じ呼び出し内では姿勢や BlendShape が古い・写らない場合があります)"
                    : "メッシュの元の境界で構図を決めました (描画はそのまま)";
                var hint = EditorApplication.isPlaying
                    ? "。Play モード中は Read/Write が無効なメッシュを焼き込めないため、編集モードで撮影してください"
                    : "";
                r.AddIssue(CctSeverity.Warning, "BAKE_FAILED",
                    $"スキンメッシュ {_bakeFailedPaths.Count} 個の現在の形状を取得できず、{effect}: {list} (理由: {_bakeFailedReason}){hint}",
                    path: _bakeFailedPaths[0], value: _bakeFailedPaths.Count);
            }

            T Own<T>(T o) where T : Object
            {
                if (o != null) _owned.Add(o);
                return o;
            }

            /// <summary>
            /// HideAndDontSave GameObject. In isolated mode it is moved into the preview scene while still empty and the
            /// components are added afterwards, so no Light/Camera/Renderer ever exists in the user's scene (not even
            /// for an instant, which could otherwise poke realtime GI or scene callbacks).
            /// </summary>
            GameObject NewObject(string name, params Type[] components)
            {
                var go = Own(EditorUtility.CreateGameObjectWithHideFlags(name, HideFlags.HideAndDontSave));
                if (PreviewScene.IsValid()) SceneManager.MoveGameObjectToScene(go, PreviewScene);
                foreach (var type in components)
                {
                    var c = go.AddComponent(type);
                    if (c != null) c.hideFlags = HideFlags.HideAndDontSave;
                }
                return go;
            }

            Light NewLight(string name, Color color, float intensity)
            {
                var go = NewObject(name, typeof(Light));
                var l = go.GetComponent<Light>();
                l.type = LightType.Directional;
                l.color = color;
                l.intensity = intensity;
                l.bounceIntensity = 0f;
                l.shadows = LightShadows.None;
                l.renderMode = LightRenderMode.ForcePixel;
                l.cullingMask = ~0;
                l.lightmapBakeType = LightmapBakeType.Realtime;
                return l;
            }

            // ---------------------------------------------------------- clone preparation

            void StripClone(bool keepScripts)
            {
                // Built-in behaviours that act on their own (mostly in Play mode) are disabled on the clone.
                foreach (var a in Root.GetComponentsInChildren<Animator>(true)) a.enabled = false;
                foreach (var a in Root.GetComponentsInChildren<Animation>(true)) a.enabled = false;
                foreach (var c in Root.GetComponentsInChildren<Camera>(true)) c.enabled = false;
                foreach (var a in Root.GetComponentsInChildren<AudioSource>(true)) a.enabled = false;
                foreach (var b in Root.GetComponentsInChildren<Behaviour>(true))
                    if (b is UnityEngine.Animations.IConstraint) b.enabled = false;
                foreach (var ps in Root.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var main = ps.main;
                    main.playOnAwake = false;
                }

                var requireCache = new Dictionary<Type, RequireComponent[]>();
                foreach (var t in Root.GetComponentsInChildren<Transform>(true))
                {
                    var go = t.gameObject;
                    if (!keepScripts)
                    {
                        try
                        {
                            if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go) > 0)
                                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                        }
                        catch (Exception) { /* missing scripts never run; leaving them is harmless */ }
                    }

                    // VRChat dynamics (PhysBone, contacts, VRC constraints) are removed even with keep_scripts: a VRC
                    // constraint's Target Transform may point outside the cloned hierarchy and would move the original.
                    var pending = go.GetComponents<MonoBehaviour>()
                        .Where(m => m != null && (!keepScripts || IsVrcDynamics(m)))
                        .ToList();
                    for (int pass = 0; pass < 16 && pending.Count > 0; pass++)
                    {
                        var all = go.GetComponents<Component>();
                        var removable = pending.Where(m => !IsRequiredByOther(m, all, requireCache)).ToList();
                        if (removable.Count == 0) break;
                        foreach (var m in removable)
                        {
                            try
                            {
                                Object.DestroyImmediate(m);
                                ScriptsStripped++;
                            }
                            catch (Exception) { /* counted below if it survived */ }
                        }
                        pending = pending.Where(m => m != null).ToList();
                    }
                    ScriptsRemaining += pending.Count(m => m != null);
                }
            }

            static bool IsRequiredByOther(Component c, Component[] all, Dictionary<Type, RequireComponent[]> cache)
            {
                var ct = c.GetType();
                foreach (var other in all)
                {
                    if (other == null || ReferenceEquals(other, c)) continue;
                    var ot = other.GetType();
                    if (!cache.TryGetValue(ot, out var reqs))
                    {
                        reqs = ot.GetCustomAttributes(typeof(RequireComponent), true).Cast<RequireComponent>().ToArray();
                        cache[ot] = reqs;
                    }
                    foreach (var rc in reqs)
                    {
                        if (Requires(rc.m_Type0, ct) || Requires(rc.m_Type1, ct) || Requires(rc.m_Type2, ct)) return true;
                    }
                }
                return false;
            }

            static bool Requires(Type required, Type candidate) => required != null && required.IsAssignableFrom(candidate);

            static bool IsVrcDynamics(MonoBehaviour m) =>
                CctSerialized.NamespaceStartsWith(m, "VRC.SDK3.Dynamics") || CctSerialized.NamespaceStartsWith(m, "VRC.Dynamics");

            static bool IsDrawn(Renderer rend) =>
                rend != null && rend.enabled && rend.gameObject.activeInHierarchy && !rend.forceRenderingOff;

            /// <summary>Counts drawn renderers under an EditorOnly-tagged object (visible here, stripped from VRChat uploads).</summary>
            void CountEditorOnly(Renderer rend)
            {
                for (var t = rend.transform; t != null; t = t.parent)
                {
                    if (t.CompareTag("EditorOnly"))
                    {
                        EditorOnlyRenderers++;
                        return;
                    }
                    if (t == Root.transform) return;
                }
            }

            string RelPath(Transform t)
            {
                var rel = CctTargets.GetRelativePath(Root.transform, t);
                return string.IsNullOrEmpty(rel) ? Root.name : rel;
            }

            void CollectIsolatedRenderer(Renderer rend, Renderer source, CctResult r)
            {
                if (!IsDrawn(rend)) return;
                RenderedRenderers++;
                CountEditorOnly(rend);
                // No light/reflection probes: the shader gets the preview scene's flat ambient and neutral cubemap.
                rend.lightProbeUsage = LightProbeUsage.Off;
                rend.reflectionProbeUsage = ReflectionProbeUsage.Off;
                var path = RelPath(rend.transform);

                if (rend is SkinnedMeshRenderer smr)
                {
                    if (smr.sharedMesh == null) return;
                    var corners = BakeForRender(smr, source as SkinnedMeshRenderer, path, r);
                    if (corners != null) FramingRenderers.Add(new FramingRenderer(path, corners));
                }
                else if (rend is MeshRenderer)
                {
                    var mf = rend.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) return;
                    CopyPropertyBlock(source, rend);
                    // Lightmap indices copied from a baked (world) object point into the open scene's lightmaps, which
                    // the preview scene does not have; clear them so the clone is lit like everything else (clone only).
                    rend.lightmapIndex = -1;
                    rend.realtimeLightmapIndex = -1;
                    if (rend.isPartOfStaticBatch || (source != null && source.isPartOfStaticBatch))
                    {
                        StaticBatchedRenderers++;
                        FramingRenderers.Add(new FramingRenderer(path, Corners((source != null ? source : rend).bounds, Matrix4x4.identity)));
                    }
                    else FramingRenderers.Add(new FramingRenderer(path, Corners(mf.sharedMesh.bounds, rend.transform.localToWorldMatrix)));
                }
            }

            void CollectSceneRenderer(Renderer rend, CctResult r)
            {
                if (!IsDrawn(rend)) return;
                RenderedRenderers++;
                CountEditorOnly(rend);
                var path = RelPath(rend.transform);
                if (rend is SkinnedMeshRenderer smr)
                {
                    if (smr.sharedMesh == null) return;
                    Mesh temp = null;
                    try
                    {
                        temp = NewTempMesh(smr.sharedMesh);
                        smr.BakeMesh(temp);
                        CheckBaked(smr, temp);
                        temp.RecalculateBounds();
                        var m = BakedMeshToWorld(smr, temp, out _);
                        FramingRenderers.Add(new FramingRenderer(path, Corners(temp.bounds, m)));
                    }
                    catch (Exception e)
                    {
                        _bakeFailedPaths.Add(path);
                        if (_bakeFailedReason == null) _bakeFailedReason = e.Message;
                        FramingRenderers.Add(new FramingRenderer(path, Corners(smr.sharedMesh.bounds, smr.transform.localToWorldMatrix)));
                    }
                    finally
                    {
                        if (temp != null)
                        {
                            _owned.Remove(temp);
                            Object.DestroyImmediate(temp);
                        }
                    }
                }
                else if (rend is MeshRenderer)
                {
                    var mf = rend.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) return;
                    // Static batching (Play mode) swaps in a world-space combined mesh; its renderer bounds stay correct.
                    FramingRenderers.Add(new FramingRenderer(path, rend.isPartOfStaticBatch
                        ? Corners(rend.bounds, Matrix4x4.identity)
                        : Corners(mf.sharedMesh.bounds, rend.transform.localToWorldMatrix)));
                }
            }

            Mesh NewTempMesh(Mesh shared)
            {
                var m = Own(new Mesh { name = shared.name + " (CCT baked)", hideFlags = HideFlags.HideAndDontSave });
                m.indexFormat = shared.indexFormat;
                return m;
            }

            /// <summary>
            /// Replaces a clone SkinnedMeshRenderer by a static snapshot (BakeMesh = CPU skinning of the current pose
            /// and BlendShape weights). A freshly created SkinnedMeshRenderer is not reliably skinned before the next
            /// editor frame, so rendering it directly inside this call could show a stale or empty mesh.
            /// </summary>
            Vector3[] BakeForRender(SkinnedMeshRenderer smr, SkinnedMeshRenderer source, string path, CctResult r)
            {
                Mesh baked;
                Matrix4x4 toWorld;
                bool parentToRenderer;
                try
                {
                    baked = NewTempMesh(smr.sharedMesh);
                    smr.BakeMesh(baked);
                    CheckBaked(smr, baked);
                    baked.RecalculateBounds();
                    toWorld = BakedMeshToWorld(smr, baked, out parentToRenderer);
                }
                catch (Exception e)
                {
                    _bakeFailedPaths.Add(path);
                    if (_bakeFailedReason == null) _bakeFailedReason = e.Message;
                    smr.updateWhenOffscreen = true; // clone only: never culled by stale bounds
                    return Corners(smr.sharedMesh.bounds, smr.transform.localToWorldMatrix);
                }

                var go = NewObject(smr.name + " (CCT baked)", typeof(MeshFilter), typeof(MeshRenderer));
                go.layer = smr.gameObject.layer;
                if (parentToRenderer)
                {
                    go.transform.SetParent(smr.transform, false);
                    go.transform.localPosition = Vector3.zero;
                    go.transform.localRotation = Quaternion.identity;
                    go.transform.localScale = Vector3.one;
                }
                else
                {
                    go.transform.SetParent(_holder.transform, false);
                    go.transform.SetPositionAndRotation(smr.transform.position, smr.transform.rotation);
                    go.transform.localScale = Vector3.one;
                }
                go.GetComponent<MeshFilter>().sharedMesh = baked;
                var mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterials = smr.sharedMaterials;
                mr.shadowCastingMode = smr.shadowCastingMode;
                mr.receiveShadows = smr.receiveShadows;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                mr.sortingLayerID = smr.sortingLayerID;
                mr.sortingOrder = smr.sortingOrder;
                mr.rendererPriority = smr.rendererPriority;
                mr.allowOcclusionWhenDynamic = false;
                CopyPropertyBlock(source, mr);
                smr.enabled = false;
                BakedMeshes++;
                return Corners(baked.bounds, toWorld);
            }

            /// <summary>
            /// BakeMesh on a mesh without CPU-readable data (Read/Write off, notably in Play mode) can log an error and
            /// leave the target empty instead of throwing; treat that as a failure so the renderer is not dropped.
            /// </summary>
            static void CheckBaked(SkinnedMeshRenderer smr, Mesh baked)
            {
                int expected = smr.sharedMesh.vertexCount;
                if (baked.vertexCount == 0 || baked.vertexCount != expected)
                    throw new InvalidOperationException(
                        $"BakeMesh の結果が不正です (頂点 {baked.vertexCount} / 期待 {expected})。メッシュの Read/Write が無効の可能性があります");
            }

            static void CopyPropertyBlock(Renderer from, Renderer to)
            {
                if (from == null || to == null || !from.HasPropertyBlock()) return;
                var block = new MaterialPropertyBlock();
                from.GetPropertyBlock(block);
                to.SetPropertyBlock(block);
            }

            /// <summary>
            /// Where BakeMesh output lives in world space. Unity documents the baked vertices as relative to the
            /// renderer transform, with or without its scale depending on the overload/version, so when the renderer
            /// is scaled we pick the interpretation whose size matches the bind-pose bounds (unambiguous).
            /// </summary>
            static Matrix4x4 BakedMeshToWorld(SkinnedMeshRenderer smr, Mesh baked, out bool parentToRenderer)
            {
                var t = smr.transform;
                var withScale = t.localToWorldMatrix;
                parentToRenderer = true;
                var s = t.lossyScale;
                if (Mathf.Abs(s.x - 1f) < 1e-3f && Mathf.Abs(s.y - 1f) < 1e-3f && Mathf.Abs(s.z - 1f) < 1e-3f) return withScale;
                var unscaled = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                float bind = BoxDiagonal(Corners(smr.sharedMesh.bounds, withScale));
                float a = BoxDiagonal(Corners(baked.bounds, withScale));
                float b = BoxDiagonal(Corners(baked.bounds, unscaled));
                if (bind <= 1e-6f || a <= 1e-6f || b <= 1e-6f) return withScale;
                if (Mathf.Abs(Mathf.Log(a / bind)) <= Mathf.Abs(Mathf.Log(b / bind))) return withScale;
                parentToRenderer = false;
                return unscaled;
            }

            // ---------------------------------------------------------- rendering

            public void PrepareRendering(Options opt, CctResult r)
            {
                Resolution = opt.Resolution;
                Linear = QualitySettings.activeColorSpace == ColorSpace.Linear;

                CreateMainTarget();
                if (Msaa <= 1)
                    r.AddIssue(CctSeverity.Info, "MSAA_UNAVAILABLE", "この GPU では RenderTexture の MSAA が使えないため、輪郭がギザギザになります");
                _rtResolve = Own(new RenderTexture(Resolution, Resolution, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = "CCT Capture Resolve",
                    hideFlags = HideFlags.HideAndDontSave,
                });
                if (!_rtResolve.Create()) throw new InvalidOperationException("読み出し用 RenderTexture を作成できませんでした");
                _readTex = Own(new Texture2D(Resolution, Resolution, TextureFormat.RGB24, false, false)
                {
                    name = "CCT Capture Readback",
                    hideFlags = HideFlags.HideAndDontSave,
                });

                var camGo = NewObject("CCT Capture Camera", typeof(Camera));
                Camera = camGo.GetComponent<Camera>();
                Camera.enabled = false;
                Camera.cameraType = Isolated ? CameraType.Preview : CameraType.Game;
                Camera.renderingPath = RenderingPath.Forward;
                Camera.allowHDR = HdrTarget;
                Camera.allowMSAA = Msaa > 1;
                Camera.allowDynamicResolution = false;
                Camera.useOcclusionCulling = false;
                Camera.orthographic = false;
                Camera.cullingMask = ~0;
                Camera.depthTextureMode = DepthTextureMode.None;
                Camera.stereoTargetEye = StereoTargetEyeMask.None;
                Camera.targetTexture = _rtMain;
                Camera.aspect = 1f;
                if (Isolated) Camera.scene = PreviewScene;

                if (Isolated)
                {
                    _reflection = Own(new Cubemap(8, TextureFormat.RGBA32, false)
                    {
                        name = "CCT Neutral Reflection",
                        hideFlags = HideFlags.HideAndDontSave,
                    });
                    var gray = Enumerable.Repeat(new Color(ReflectionGray, ReflectionGray, ReflectionGray, 1f), 8 * 8).ToArray();
                    foreach (CubemapFace face in new[] { CubemapFace.PositiveX, CubemapFace.NegativeX, CubemapFace.PositiveY,
                                 CubemapFace.NegativeY, CubemapFace.PositiveZ, CubemapFace.NegativeZ })
                        _reflection.SetPixels(gray, face);
                    _reflection.Apply(false, false);
                }

                CalibrateBackground(opt.BackgroundColor);
            }

            void CreateMainTarget()
            {
                bool halfOk = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf);
                foreach (var half in halfOk ? new[] { true, false } : new[] { false })
                {
                    var desc = new RenderTextureDescriptor(Resolution, Resolution,
                        half ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32, 24)
                    {
                        sRGB = !half && QualitySettings.activeColorSpace == ColorSpace.Linear,
                        useMipMap = false,
                        autoGenerateMips = false,
                        msaaSamples = 8,
                    };
                    int supported;
                    try { supported = SystemInfo.GetRenderTextureSupportedMSAASampleCount(desc); }
                    catch (Exception) { supported = 1; }
                    // 8x on a 2048 half-float target would need ~400 MB of VRAM; 4x is plenty above 1024 px.
                    int wanted = Resolution > 1024 ? 4 : 8;
                    int msaa = supported >= wanted ? wanted : supported >= 4 ? 4 : supported >= 2 ? 2 : 1;
                    foreach (var samples in msaa > 1 ? new[] { msaa, 1 } : new[] { 1 })
                    {
                        desc.msaaSamples = samples;
                        var rt = new RenderTexture(desc) { name = "CCT Capture Target", hideFlags = HideFlags.HideAndDontSave };
                        if (rt.Create())
                        {
                            _rtMain = Own(rt);
                            Msaa = samples;
                            HdrTarget = half;
                            return;
                        }
                        Object.DestroyImmediate(rt);
                    }
                }
                throw new InvalidOperationException("描画用 RenderTexture を作成できませんでした");
            }

            /// <summary>
            /// Whether Camera.backgroundColor is taken as sRGB (converted to linear by Unity) or used raw differs between
            /// code paths, so it is measured once: clear-only render of sRGB 0.5 gray, read back 128 (sRGB) or 188 (raw).
            /// </summary>
            void CalibrateBackground(Color32 hex)
            {
                Color gamma = hex;
                if (!Linear)
                {
                    CameraBackground = gamma;
                    BackgroundCalibration = "gamma_color_space";
                    return;
                }
                int mask = Camera.cullingMask;
                try
                {
                    Camera.cullingMask = 0;
                    var px = RenderOnce(CameraClearFlags.SolidColor, new Color(0.5f, 0.5f, 0.5f, 1f));
                    int v = px[(Resolution / 2) * Resolution + Resolution / 2].r;
                    bool convertsFromSrgb = Math.Abs(v - 128) <= Math.Abs(v - 188);
                    CameraBackground = convertsFromSrgb ? gamma : gamma.linear;
                    BackgroundCalibration = (convertsFromSrgb ? "camera_converts_srgb" : "camera_uses_linear") + " (probe 0.5 -> " + v + ")";
                }
                finally
                {
                    Camera.cullingMask = mask;
                }
            }

            public void AimLights(ViewDef view, bool followCamera)
            {
                if (_key == null || _fill == null) return;
                float az = followCamera ? view.Azimuth : 0f;
                float el = followCamera ? view.Elevation : 0f;
                _key.transform.rotation = Frame.Rotation * LightRotation(az + KeyAzimuth, Mathf.Clamp(el + KeyElevation, -85f, 85f));
                _fill.transform.rotation = Frame.Rotation * LightRotation(az + FillAzimuth, FillElevation);
            }

            static Quaternion LightRotation(float azimuth, float elevation) =>
                Quaternion.LookRotation(-Direction(azimuth, elevation), Vector3.up);

            public Color32[] Render(Vector3 position, Quaternion rotation, CameraPose pose, Options opt)
            {
                Camera.transform.SetPositionAndRotation(position, rotation);
                Camera.fieldOfView = pose.Fov;
                Camera.nearClipPlane = pose.Near;
                Camera.farClipPlane = pose.Far;
                Camera.aspect = 1f;
                if (opt.Transparent)
                {
                    // Difference matting: alpha and colour recovered from a black and a white background render,
                    // which is correct for any blend mode (shader alpha output is not trustworthy).
                    var black = RenderOnce(CameraClearFlags.SolidColor, Color.black);
                    var white = RenderOnce(CameraClearFlags.SolidColor, Color.white);
                    return Matte(black, white, Linear);
                }
                if (opt.Background == "skybox") return RenderOnce(CameraClearFlags.Skybox, CameraBackground);
                return RenderOnce(CameraClearFlags.SolidColor, CameraBackground);
            }

            Color32[] RenderOnce(CameraClearFlags clear, Color background)
            {
                Camera.clearFlags = clear;
                Camera.backgroundColor = background;
                var previousActive = RenderTexture.active;
                bool previousSrgbWrite = GL.sRGBWrite;
                try
                {
                    GL.sRGBWrite = Linear;
                    RenderCameraWithLighting();
                    GL.sRGBWrite = Linear; // Camera.Render may leave it changed; Blit relies on it for the linear -> sRGB write
                    Graphics.Blit(_rtMain, _rtResolve); // resolves MSAA, converts half-float linear -> sRGB bytes
                    RenderTexture.active = _rtResolve;
                    _readTex.ReadPixels(new Rect(0, 0, Resolution, Resolution), 0, 0, false);
                    return _readTex.GetPixels32();
                }
                finally
                {
                    RenderTexture.active = previousActive;
                    GL.sRGBWrite = previousSrgbWrite;
                }
            }

            /// <summary>
            /// Preview scenes have their own lighting settings, but the renderer only uses them while the override is
            /// active (PreviewRenderUtility/SceneView do the same). Set right before Render and restored right after, so
            /// no repaint of the user's windows can happen in between. Never touches the user's scene settings.
            /// Async shader compilation is switched off for this call site (as SceneView does around its own render):
            /// otherwise a variant not compiled yet in this session (lilToon under the studio lights) is drawn with the
            /// cyan placeholder shader and the PNG / comparison is wrong. The first capture can therefore take longer.
            /// </summary>
            void RenderCameraWithLighting()
            {
                bool overridden = false;
                bool oldAsync = ShaderUtil.allowAsyncCompilation;
                try
                {
                    ShaderUtil.allowAsyncCompilation = false;
                    if (Isolated)
                    {
                        overridden = Unsupported.SetOverrideLightingSettings(PreviewScene);
                        if (overridden) ApplyNeutralRenderSettings();
                        else LightingOverrideFailed = true;
                    }
                    Camera.Render();
                }
                finally
                {
                    if (overridden)
                    {
                        try { Unsupported.RestoreOverrideLightingSettings(); }
                        catch (Exception e) { CleanupErrors.Add("RestoreOverrideLightingSettings: " + e.Message); }
                    }
                    ShaderUtil.allowAsyncCompilation = oldAsync;
                }
            }

            void ApplyNeutralRenderSettings()
            {
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = AmbientColor;
                RenderSettings.ambientIntensity = 1f;
                RenderSettings.fog = false;
                RenderSettings.skybox = null;
                RenderSettings.sun = _key;
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
#pragma warning disable 618 // customReflection: the only setter that exists in both 2021.x and 2022.3
                RenderSettings.customReflection = _reflection;
#pragma warning restore 618
                RenderSettings.reflectionIntensity = 1f;
            }

            static Color32[] Matte(Color32[] black, Color32[] white, bool linear)
            {
                var result = new Color32[black.Length];
                for (int i = 0; i < black.Length; i++)
                {
                    var b = black[i];
                    var w = white[i];
                    float br = Lin(b.r, linear), bg = Lin(b.g, linear), bb = Lin(b.b, linear);
                    float wr = Lin(w.r, linear), wg = Lin(w.g, linear), wb = Lin(w.b, linear);
                    float a = Mathf.Clamp01(1f - ((wr - br) + (wg - bg) + (wb - bb)) / 3f);
                    if (a < 0.5f / 255f)
                    {
                        result[i] = new Color32(0, 0, 0, 0);
                        continue;
                    }
                    result[i] = new Color32(Enc(br / a, linear), Enc(bg / a, linear), Enc(bb / a, linear),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255));
                }
                return result;
            }

            public Dictionary<string, object> DescribeRendering(Options opt)
            {
                var d = new Dictionary<string, object>
                {
                    ["mode"] = Isolated ? "isolated" : "scene",
                    ["resolution"] = Resolution,
                    ["msaa"] = Msaa,
                    ["hdr_target"] = HdrTarget,
                    ["color_space"] = Linear ? "Linear" : "Gamma",
                    ["background_calibration"] = BackgroundCalibration,
                    ["renderers_drawn"] = RenderedRenderers,
                    ["renderers_for_framing"] = FramingRenderers.Count,
                    ["editor_only_renderers"] = EditorOnlyRenderers,
                    ["texture_streaming"] = QualitySettings.streamingMipmapsActive,
                    ["async_shader_compilation"] = "disabled during capture",
                };
                if (Isolated)
                {
                    d["skinned_meshes_baked"] = BakedMeshes;
                    d["static_batched_renderers"] = StaticBatchedRenderers;
                    d["scripts_stripped"] = ScriptsStripped;
                    d["target_lights"] = TargetLights;
                    d["lighting"] = new Dictionary<string, object>
                    {
                        ["kind"] = opt.LightFollowCamera ? "studio_follow_camera" : "studio_fixed_to_target",
                        ["key"] = new Dictionary<string, object> { ["azimuth"] = KeyAzimuth, ["elevation"] = KeyElevation, ["intensity"] = KeyIntensity, ["color"] = "#FFFFFF" },
                        ["fill"] = new Dictionary<string, object> { ["azimuth"] = FillAzimuth, ["elevation"] = FillElevation, ["intensity"] = FillIntensity, ["color"] = Hex(FillColor) },
                        ["ambient"] = Hex(AmbientColor) + " flat",
                        ["reflection"] = "uniform gray cubemap " + ReflectionGray.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["shadows"] = "none",
                        ["probes"] = "light/reflection probes off on the clone",
                    };
                }
                else d["lighting"] = "scene (the open scenes' lights, ambient, skybox and probes)";
                return d;
            }

            // ---------------------------------------------------------- cleanup

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (Camera != null)
                {
                    try { Camera.targetTexture = null; }
                    catch (Exception e) { CleanupErrors.Add("camera: " + e.Message); }
                }
                for (int i = _owned.Count - 1; i >= 0; i--)
                {
                    var o = _owned[i];
                    if (o == null) continue; // already destroyed (e.g. baked objects parented under the clone)
                    try
                    {
                        if (o is RenderTexture rt)
                        {
                            if (RenderTexture.active == rt) RenderTexture.active = null;
                            rt.Release();
                        }
                        Object.DestroyImmediate(o);
                    }
                    catch (Exception e) { CleanupErrors.Add(o.GetType().Name + ": " + e.Message); }
                }
                _owned.Clear();
                if (PreviewScene.IsValid())
                {
                    try { EditorSceneManager.ClosePreviewScene(PreviewScene); }
                    catch (Exception e) { CleanupErrors.Add("ClosePreviewScene: " + e.Message); }
                }
                PreviewScene = default;
            }
        }

        // ---------------------------------------------------------------- colour helpers

        static readonly float[] SrgbToLinearTable = BuildSrgbTable();

        static float[] BuildSrgbTable()
        {
            var t = new float[256];
            for (int i = 0; i < 256; i++)
            {
                double c = i / 255.0;
                t[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
            }
            return t;
        }

        static float Lin(byte v, bool linear) => linear ? SrgbToLinearTable[v] : v / 255f;

        // Linear 0..1 quantised to 1/65535 -> sRGB byte. Max slope is 12.92*255/65535 = 0.05 levels per step, so 8-bit values
        // round-trip exactly; replaces ~12M Math.Pow calls per 2048px transparent view (about a second in Mono).
        static byte[] _linearToSrgb8;

        static byte Enc(float v, bool linear)
        {
            v = Mathf.Clamp01(v);
            if (!linear) return (byte)Mathf.Clamp((int)Math.Round(v * 255.0), 0, 255);
            if (_linearToSrgb8 == null)
            {
                var t = new byte[65536];
                for (int i = 0; i < t.Length; i++)
                {
                    double c = i / 65535.0;
                    double s = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
                    t[i] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(s * 255.0)));
                }
                _linearToSrgb8 = t;
            }
            return _linearToSrgb8[(int)(v * 65535f + 0.5f)];
        }

        static string Hex(Color c) => Hex((Color32)c);

        static Vector3 Direction(float azimuthDeg, float elevationDeg)
        {
            float az = azimuthDeg * Mathf.Deg2Rad, el = elevationDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Cos(az) * Mathf.Cos(el));
        }

        static Vector3[] Corners(Bounds b, Matrix4x4 m)
        {
            var min = b.min;
            var max = b.max;
            var c = new Vector3[8];
            int k = 0;
            for (int x = 0; x < 2; x++)
            for (int y = 0; y < 2; y++)
            for (int z = 0; z < 2; z++)
                c[k++] = m.MultiplyPoint3x4(new Vector3(x == 0 ? min.x : max.x, y == 0 ? min.y : max.y, z == 0 ? min.z : max.z));
            return c;
        }

        static float BoxDiagonal(Vector3[] points)
        {
            if (points == null || points.Length == 0) return 0f;
            var min = points[0];
            var max = points[0];
            foreach (var p in points)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            return (max - min).magnitude;
        }
    }
}
