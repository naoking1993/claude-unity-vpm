using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// cct_avatar_audit: VRChat avatar pre-upload audit. Read-only.
    ///
    /// Performance stats and ranks come from the VRChat SDK's own calculator when the avatars SDK is loaded
    /// (VRC.SDKBase.Validation.Performance.AvatarPerformance.CalculatePerformanceStats via reflection, exactly the call the SDK
    /// Control Panel makes, research/avatar-limits.md §15.1), so values match the SDK panel; data.method.stats = "sdk".
    /// If that fails (types/methods missing, exception) or in Play mode (the SDK scan re-initialises live PhysBone chains),
    /// the tool's own estimator (CctAvatarAudit.Stats.cs, re-implementing the SDK 3.10.5 counting rules of avatar-limits.md §4)
    /// is used, every stat gets "estimate": true and data.method.stats = "estimate". The estimator always runs too, because
    /// the SDK only returns totals: it provides the per-component breakdown lists and a consistency check (BREAKDOWN_MISMATCH).
    /// Rank tables and rating rules: CctAvatarAudit.Ranks.cs (pure, unit-tested). All values are a static estimate of the
    /// avatar as it is in the editor (stage "pre_build"); Modular Avatar / NDMF / AAO / VRCFury change it at build time.
    /// post_build=true additionally processes a temporary clone with NDMF (CctAvatarAudit.PostBuild.cs) and audits that.
    ///
    /// Sources: VRChat SDK 3.10.5 (com.vrchat.base / com.vrchat.avatars) and creator-docs snapshot 2026-10-20, via
    /// research/avatar-limits.md and research/ma-ndmf-aao.md. Where the SDK and docs differ the SDK is followed and the
    /// message says so.
    ///
    /// Issue codes (stable):
    /// | code                              | severity        | rule |
    /// |-----------------------------------|-----------------|------|
    /// | DESCRIPTOR_MISSING                | warning         | explicit target has no VRCAvatarDescriptor (ancestor or single descendant); audited as a plain hierarchy |
    /// | TARGET_ADJUSTED                   | info            | target/selection was moved to the avatar root that owns it |
    /// | PLAY_MODE                         | info            | Play mode: SDK calculator skipped (estimate), the scene avatar may already be NDMF-processed |
    /// | SDK_CALCULATOR_UNAVAILABLE        | info            | SDK calculator missing or threw; estimator used (all stats estimate) |
    /// | SDK_IGNORE_RULE_REPLACED          | info            | AvatarPerformance.ShouldIgnoreComponent was replaced by another tool (SDK values follow that rule) |
    /// | CONSTRAINT_GROUPS_NOT_REFRESHED   | info            | VRC constraint groups could not be refreshed; Constraint Depth may be over-counted (as in the SDK) |
    /// | RANK_TABLE_MISMATCH               | info            | SDK rating differs from this tool's 3.10.5 table for the same value (SDK thresholds changed; SDK wins) |
    /// | BREAKDOWN_MISMATCH                | info            | SDK total differs from the sum of the tool's breakdown (breakdown lists are approximate for that stat) |
    /// | TEXTURE_MEMORY_PLATFORM           | info            | texture memory is computed from the textures imported for the active build target only |
    /// | PREBUILD_ESTIMATE                 | info            | MA / NDMF / AAO / VRCFury components present: values are pre-build |
    /// | RANK_LIMIT                        | warning/info    | stat rated VeryPoor (warning) or Poor (mobile: warning, PC: info); value, limit for the next rank, platform |
    /// | MOBILE_DYNAMICS_REMOVED           | warning         | mobile VeryPoor in PhysBone/Contact/Constraint rows: those components are all removed on mobile (docs) |
    /// | MOBILE_REMOVED_COMPONENTS         | info            | lights/cloth/physics/audio exist: always removed on mobile (docs "Mobile Removed Components") |
    /// | HARD_LIMIT_EXCEEDED               | error           | SDK hard caps: PhysBones 256, colliders 256, contacts incl. local-only 256, constraints 2000, raycasts 80, VRCHeadChop 16 |
    /// | GLOBAL_COLLIDERS_EXCEEDED         | error           | more than 4 PhysBone colliders with globalCollision != False (SDK builder error) |
    /// | PHYSBONE_CHAIN_TOO_LONG           | error           | one PhysBone affects more than 256 transforms: PhysBoneManager does not simulate it (SDK MAX_TRANSFORMS_PER_CHAIN) |
    /// | BOUNDS_LARGE                      | warning         | bounds exceed 5x6x5 m (VeryPoor); lists the renderers that extend furthest |
    /// | FALLBACK_INELIGIBLE               | info            | not usable as a custom fallback on that platform (needs humanoid and overall Good or better) |
    /// | MESH_NOT_READABLE                 | error           | model-imported mesh with Read/Write disabled (SDK upload error; docs: shown as Very Poor in game) |
    /// | BLENDSHAPE_NORMALS_LEGACY_OFF     | error           | model imported with Blendshape Normals = Calculate but Legacy Blendshape Normals off (SDK upload error) |
    /// | TEXTURE_MIP_STREAMING_OFF         | error           | mipmapped texture without Streaming Mip Maps (SDK upload error, Auto Fix exists) |
    /// | TEXTURE_MAX_SIZE_OVER_8192        | error           | TextureImporter.maxTextureSize > 8192 (SDK upload error) |
    /// | TEXTURE_UNCOMPRESSED              | warning/info    | importer compression Uncompressed (warning when the texture costs >= 4 MB) |
    /// | TEXTURE_BOX_MIP_FILTER            | info            | Box mip filter (SDK suggests Kaiser) |
    /// | ANDROID_INCOMPATIBLE_SHADER       | error/warning/info | shader not in the SDK mobile whitelist (error when the active build target is Android/iOS); info for Toon Standard (Outline), which mobile clients replace |
    /// | MISSING_SCRIPT                    | error           | GameObject with missing MonoBehaviours (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount, nulls never filtered first) |
    /// | MISSING_SCRIPT_GUID               | info            | m_Script GUID of a missing script found in the saved scene/prefab YAML (prefab chain of each object), with why it does not resolve (script_not_found / class_not_loadable / class_not_in_dll) |
    /// | MISSING_REFERENCE                 | warning         | ObjectReference pointing at a deleted/unloadable object (SerializedObject scan, capped) |
    /// | NOT_WHITELISTED                   | error           | component not allowed on avatars for the active build target (SDK error "will be removed by the client") |
    /// | NOT_WHITELISTED_MOBILE            | warning/error   | allowed on PC but not on Android/iOS (FinalIK, Cloth, Light, colliders, Rigidbody, joints, Unity constraints, Camera, AudioSource, ...) |
    /// | DYNAMIC_BONE_PRESENT              | error           | DynamicBone / DynamicBoneCollider (SDK requires conversion to PhysBones) |
    /// | UNITY_CONSTRAINTS                 | warning/error   | Unity constraints (PC warning, mobile error per SDK); info when MA Convert Constraints converts them |
    /// | PIPELINE_MANAGER_COUNT            | error           | not exactly one PipelineManager in the avatar (SDK builder error) |
    /// | PARTICLE_STOP_ACTION_DESTROY      | error           | ParticleSystem stopAction = Destroy (SDK error) |
    /// | PARTICLE_ROOT_STOP_DISABLE        | error           | ParticleSystem on the avatar root with stopAction = Disable (SDK error) |
    /// | TRAIL_AUTODESTRUCT                | error           | TrailRenderer.autodestruct (SDK error) |
    /// | PARTICLE_LIGHTS                   | warning         | ParticleSystem lights module has a Light (counts towards Lights even when the module is off) |
    /// | AUDIO_DECOMPRESS_ON_LOAD          | error           | AudioSource clip Decompress On Load without Load In Background (SDK error) |
    /// | ROOT_ANIMATOR_MISSING             | error           | no Animator on the avatar root (SDK error) |
    /// | AVATAR_NOT_HUMANOID               | warning         | Animator avatar is not humanoid |
    /// | AVATAR_INACTIVE                   | error           | humanoid avatar is disabled in the hierarchy (SDK error; warning for prefab assets) |
    /// | HUMANOID_BONES_MISSING            | error           | required humanoid bones unmapped (SDK checks feet / upper arms / head / hands) |
    /// | HUMANOID_OPTIONAL_BONES_MISSING   | info            | optional bones (upper chest, toes, eyes, jaw, fingers) unmapped |
    /// | HUMANOID_SPINE_HIERARCHY          | error           | shoulders / neck are not children of UpperChest (or Chest) (SDK AnalyzeIK error) |
    /// | HUMANOID_LIMB_CHILD_ORDER         | warning         | lower arm/leg not the first child of the upper one, or hand/foot not the first child (SDK warning) |
    /// | HUMANOID_SPLIT_HIERARCHY          | warning         | Hips is not an ancestor of both hands and feet (SDK warning) |
    /// | ARMATURE_NOT_DIRECT_CHILD         | warning         | Animator.avatarRoot is a descendant of the avatar root, not the root itself (SDK error; read from the cached value, so warning) |
    /// | AVATAR_HEIGHT                     | error/warning   | shoulder height < 0.2 m (error), < 1.0 m / > 2.5 m / > 5 m (warning) (SDK builder rules) |
    /// | FEET_BELOW_ORIGIN                 | warning         | left foot below the avatar root (SDK warning) |
    /// | VISEME_MESH_MISSING               | error           | Lip Sync = Viseme Blend Shape without a face mesh (SDK error) |
    /// | VIEWPOINT_SUSPICIOUS              | warning         | View Position at (0,0,0), below the root, or farther from the head bone than 0.35 m x max(root scale, shoulder height / 1.4 m) (heuristic) |
    /// | LAYER_CONTROLLER_MISSING          | warning         | customised playable layer (not default) without controller, or the controller reference is missing |
    /// | GESTURE_MASK_MISSING              | error           | custom humanoid Gesture controller whose first layer has no AvatarMask (SDK error) |
    /// | WD_MIXED                          | warning         | one playable-layer controller mixes Write Defaults on/off (SDK rule: Additive layers and Direct blend tree states excluded) |
    /// | WD_MIXED_ACROSS_CONTROLLERS       | info            | controllers are each uniform but differ from each other (docs: keep WD consistent across the avatar) |
    /// | WD_OFF_EMPTY_MOTION               | warning         | WD-off state with no motion / empty clip / empty blend tree child (SDK warning) |
    /// | WD_MERGE_ANIMATOR_NOT_CHECKED     | info            | MA Merge Animator controllers are merged at build time and are not part of the Write Defaults checks |
    /// | BLUEPRINT_ID_EMPTY                | info            | PipelineManager has no blueprint ID (uploads as a new avatar) |
    /// | EXPRESSIONS_ASSET_MISSING         | error           | only one of Expressions Menu / Parameters is set, or the reference is missing (SDK error) |
    /// | PARAM_BUDGET_EXCEEDED             | error           | synced parameter cost > 256 bits (SDK error) |
    /// | PARAM_COUNT_EXCEEDED              | warning         | more than 8192 parameters (docs limit; the SDK does not check it) |
    /// | PARAM_DUPLICATE                   | warning         | the same parameter name defined more than once (the SDK resolves to the first) |
    /// | PARAM_EMPTY_NAME                  | warning/info    | parameter without a name (warning when synced: it still costs bits) |
    /// | PARAM_BUILTIN_NAME                | warning         | parameter named like a VRChat built-in (docs: do not add built-ins) |
    /// | PARAM_DEFAULT_OUT_OF_RANGE        | info            | default outside the type range (Int 0..255 integer, Float -1..1, Bool 0/1) |
    /// | MENU_TOO_MANY_CONTROLS            | warning         | menu with more than 8 controls (docs limit; the SDK builder does not check it; MA splits its own installs) |
    /// | MENU_PARAM_UNDEFINED              | error           | control parameter not defined in Expression Parameters (SDK error, built-ins are not accepted either) |
    /// | MENU_SUBMENU_MISSING              | warning         | Sub Menu control without a menu (or a missing reference) |
    /// | MENU_PUPPET_BOOL_PARAM            | error           | puppet sub-parameter is a Bool parameter (SDK error) |
    /// | MENU_PUPPET_SUBPARAM_MISSING      | warning         | puppet control has fewer sub-parameters than its type needs |
    /// | MENU_ICON_INVALID                 | error           | menu icon > 256 px with importer max size > 256, or uncompressed (SDK error "Images used for Actions &amp; Moods are too large") |
    /// | MENU_RECURSIVE                    | info            | a menu is reachable from itself (traversal stops there, as in the SDK) |
    /// | POST_BUILD_UNAVAILABLE            | info            | post_build requested but NDMF (or its no-asset-write API) is missing, Play mode, or not an avatar |
    /// | POST_BUILD_FAILED                 | warning         | NDMF processing of the temporary clone threw |
    /// | POST_BUILD_NDMF_ERROR             | error/warning/info | error reported by an NDMF plugin while processing the clone (Error/InternalError block uploads) |
    /// | POST_BUILD_PARTIAL                | info            | the clone was processed by NDMF only (VRCFury / other SDK build hooks did not run) |
    /// | NDMF_APPLY_ON_BUILD_OFF           | warning         | NDMF "Apply on Build" is off: a real upload would skip MA/AAO entirely |
    /// | POST_BUILD_CLEANUP_FAILED         | warning         | the temporary preview scene or clone could not be removed, or an NDMF plugin wrote temporary assets (deleted again) |
    /// | POST_BUILD_STRAY_OBJECTS          | info            | root objects created in the user's scenes during NDMF processing were destroyed so they are not saved |
    /// | SCENE_DIRTIED                     | warning         | a loaded scene became dirty during the audit and could not be restored to clean |
    /// | SCENE_DIRTY_CLEARED               | info            | a scene that was clean before became dirty (temporary clone creation) and its dirty flag was cleared again |
    /// | AUDIT_SECTION_FAILED              | warning         | one section threw unexpectedly; it is omitted (value = stack excerpt) and the rest of the result stays valid |
    /// Per code at most <see cref="MaxIssuesPerCode"/> issues are listed; the rest are summarised in one extra issue.
    /// Failure codes (success=false, data.candidates lists paths + instance IDs): TARGET_NOT_FOUND (spec does not resolve),
    /// AMBIGUOUS_TARGET (several avatars / same-named objects), NO_AVATAR (nothing selected and no avatar in the loaded scenes).
    /// </summary>
    public static partial class CctAvatarAudit
    {
        public const string ToolName = "cct_avatar_audit";

        const int MaxIssuesPerCode = 25;
        const string MobilePlatformNote = "Android/iOS (Meta Quest を含むモバイル) は同じ表を使います";

        // Type names read through CctSerialized / reflection (no compile-time SDK dependency; verified in tests/vrc-fields/CctAvatarAudit.txt).
        const string TPhysBone = CctVrcTypes.PhysBone;
        const string TPhysBoneBase = "VRC.Dynamics.VRCPhysBoneBase";
        const string TPhysBoneColliderBase = "VRC.Dynamics.VRCPhysBoneColliderBase";
        const string TContactBase = "VRC.Dynamics.ContactBase";
        const string TIVrcConstraint = "VRC.SDKBase.Validation.Performance.IVRCConstraint";
        const string TVrcConstraintBase = "VRC.Dynamics.VRCConstraintBase";
        const string TRaycast = "VRC.SDK3.Avatars.Components.VRCRaycast";
        const string TStation = "VRC.SDKBase.VRCStation";
        const string THeadChop = "VRC.SDK3.Avatars.Components.VRCHeadChop";
        const string TIEditorOnly = "VRC.SDKBase.IEditorOnly";
        const string TMaConvertConstraints = "nadena.dev.modular_avatar.core.ModularAvatarConvertConstraints";
        const string TMaMenuInstaller = "nadena.dev.modular_avatar.core.ModularAvatarMenuInstaller";
        const string TMaMenuItem = "nadena.dev.modular_avatar.core.ModularAvatarMenuItem";
        const string TMaParameters = "nadena.dev.modular_avatar.core.ModularAvatarParameters";
        const string TMaMergeAnimator = "nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator";

        public static CctResult Run(CctArgs args)
        {
            var clock = Stopwatch.StartNew();
            var platform = args.GetEnum("platform", "all", "all", "pc", "android", "ios");
            var targetSpec = args.GetString("target");

            var found = ResolveAvatar(targetSpec);
            if (!found.Ok)
            {
                var fail = CctResult.Fail(found.Error, found.ErrorCode);
                if (found.Candidates.Count > 0) fail.Put("candidates", found.Candidates);
                return fail;
            }

            var r = CctResult.Ok("アバターを監査しました");
            GameObject loadedPrefabRoot = null;
            var dirtyBefore = SnapshotSceneDirtiness();
            try
            {
                var root = found.Root;
                if (found.PrefabAssetPath != null && found.PrefabAssetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    // Prefab assets are audited from an isolated preview scene (PrefabUtility.LoadPrefabContents): renderer bounds,
                    // PhysBone chains and constraint groups need scene objects, and nothing is written back.
                    loadedPrefabRoot = PrefabUtility.LoadPrefabContents(found.PrefabAssetPath);
                    root = MapIntoPrefabContents(found, loadedPrefabRoot);
                }

                var ctx = new Ctx(args, r, root, platform, clock)
                {
                    PrefabAssetPath = found.PrefabAssetPath,
                    AssetObject = found.PrefabAssetPath != null ? found.Root : null,
                    FromPrefabContents = loadedPrefabRoot != null,
                    ExplicitTarget = !string.IsNullOrWhiteSpace(targetSpec) && !string.Equals(targetSpec.Trim(), "selection", StringComparison.OrdinalIgnoreCase),
                };
                if (found.Adjusted != null) ctx.Issue(CctSeverity.Info, "TARGET_ADJUSTED", found.Adjusted, CctTargets.GetPath(found.Root));
                Audit(ctx);
            }
            finally
            {
                if (loadedPrefabRoot != null)
                {
                    try { PrefabUtility.UnloadPrefabContents(loadedPrefabRoot); }
                    catch (Exception e) { r.AddIssue(CctSeverity.Warning, "POST_BUILD_CLEANUP_FAILED", "プレハブの一時読み込みを解放できませんでした: " + e.Message, asset: found.PrefabAssetPath); }
                }
                MeshTriangleCache.Clear(); // static cache: do not keep meshes (or destroyed post_build meshes) referenced after the call
                RestoreSceneDirtiness(r, dirtyBefore);
            }
            return r;
        }

        // =====================================================================================================================
        // Target resolution
        // =====================================================================================================================

        sealed class Found
        {
            public GameObject Root;
            public string PrefabAssetPath;
            public string Adjusted;
            public string Error, ErrorCode = "TARGET_NOT_FOUND";
            public readonly List<string> Candidates = new List<string>();
            public bool Ok => Root != null;
        }

        static Found ResolveAvatar(string spec)
        {
            var f = new Found();
            bool auto = string.IsNullOrWhiteSpace(spec) || string.Equals(spec.Trim(), "selection", StringComparison.OrdinalIgnoreCase);
            if (auto && Selection.activeGameObject == null)
            {
                var all = AvatarsInLoadedScenes();
                var active = all.Where(g => g.activeInHierarchy).ToList();
                var pick = active.Count == 1 ? active[0] : (active.Count == 0 && all.Count == 1 ? all[0] : null);
                if (pick != null)
                {
                    f.Root = pick;
                    f.Adjusted = $"対象未指定のため、読み込み中のシーンで唯一の{(pick.activeInHierarchy ? "アクティブな" : "")}アバター '{pick.name}' を監査しました";
                    return f;
                }
                f.ErrorCode = all.Count == 0 ? "NO_AVATAR" : "AMBIGUOUS_TARGET";
                f.Error = all.Count == 0
                    ? "読み込み中のシーンに VRCAvatarDescriptor を持つアバターがありません (target にプレハブのパスか GameObject を指定してください)"
                    : $"アバターが {all.Count} 体あります (アクティブ {active.Count} 体)。target にパスか instance ID を指定してください";
                f.Candidates.AddRange(all.Take(30).Select(DescribeCandidate));
                return f;
            }

            var res = CctTargets.Resolve(auto ? null : spec);
            if (!res.Ok)
            {
                f.Error = res.Error;
                f.ErrorCode = res.Candidates.Count > 0 ? "AMBIGUOUS_TARGET" : "TARGET_NOT_FOUND";
                f.Candidates.AddRange(res.Candidates);
                return f;
            }
            var go = res.GameObject;
            if (res.IsPrefabAsset) f.PrefabAssetPath = AssetDatabase.GetAssetPath(go);

            var owner = FindDescriptorOwner(go.transform);
            if (owner != null)
            {
                f.Root = owner.gameObject;
                if (owner.gameObject != go)
                    f.Adjusted = $"'{go.name}' を含むアバタールート '{owner.name}' を監査しました";
                return f;
            }
            var below = new List<GameObject>();
            foreach (var c in go.GetComponentsInChildren<Component>(true))
                if (c != null && CctSerialized.IsType(c, CctVrcTypes.AvatarDescriptor)) below.Add(c.gameObject);
            if (below.Count == 1)
            {
                f.Root = below[0];
                f.Adjusted = $"'{go.name}' の子にあるアバター '{below[0].name}' を監査しました";
                return f;
            }
            if (below.Count > 1)
            {
                f.ErrorCode = "AMBIGUOUS_TARGET";
                f.Error = $"'{go.name}' の下にアバターが {below.Count} 体あります。どれか 1 体を target に指定してください";
                f.Candidates.AddRange(below.Take(30).Select(DescribeCandidate));
                return f;
            }
            // No descriptor anywhere: audit the hierarchy as a part (outfit / gimmick prefab) - DESCRIPTOR_MISSING is reported later.
            f.Root = go;
            return f;
        }

        static List<GameObject> AvatarsInLoadedScenes()
        {
            var list = new List<GameObject>();
            foreach (var go in CctTargets.AllGameObjectsInLoadedScenes())
                foreach (var c in go.GetComponents<Component>())
                    if (c != null && CctSerialized.IsType(c, CctVrcTypes.AvatarDescriptor)) { list.Add(go); break; }
            return list;
        }

        static Transform FindDescriptorOwner(Transform t)
        {
            for (var cur = t; cur != null; cur = cur.parent)
                foreach (var c in cur.GetComponents<Component>())
                    if (c != null && CctSerialized.IsType(c, CctVrcTypes.AvatarDescriptor)) return cur;
            return null;
        }

        static string DescribeCandidate(GameObject g) =>
            $"{CctTargets.GetPath(g)} (#{g.GetInstanceID()}, {(EditorUtility.IsPersistent(g) ? "asset " + AssetDatabase.GetAssetPath(g) : "scene " + g.scene.name)}{(g.activeInHierarchy ? "" : ", inactive")})";

        /// <summary>Finds the object in the loaded prefab contents that corresponds to the resolved asset object (same relative path).</summary>
        static GameObject MapIntoPrefabContents(Found f, GameObject contentsRoot)
        {
            var assetRoot = AssetDatabase.LoadAssetAtPath<GameObject>(f.PrefabAssetPath);
            if (assetRoot == null || f.Root == assetRoot) return contentsRoot;
            var rel = CctTargets.GetRelativePath(assetRoot.transform, f.Root.transform);
            if (string.IsNullOrEmpty(rel)) return contentsRoot;
            var t = contentsRoot.transform.Find(rel);
            return t != null ? t.gameObject : contentsRoot;
        }

        // =====================================================================================================================
        // Scene dirtiness guard (CONVENTIONS rule 2)
        // =====================================================================================================================

        static Dictionary<Scene, bool> SnapshotSceneDirtiness()
        {
            var d = new Dictionary<Scene, bool>();
            foreach (var s in CctTargets.LoadedScenes()) d[s] = s.isDirty;
            return d;
        }

        static void RestoreSceneDirtiness(CctResult r, Dictionary<Scene, bool> before)
        {
            foreach (var kv in before)
            {
                var s = kv.Key;
                if (kv.Value || !s.IsValid() || !s.isLoaded || !s.isDirty) continue;
                // Only scenes that were clean before: nothing this read-only audit did is meant to persist, so the dirty flag is
                // the only trace left. EditorSceneManager.ClearSceneDirtiness is internal -> reflection, fail soft.
                bool cleared = false;
                try
                {
                    var m = typeof(EditorSceneManager).GetMethod("ClearSceneDirtiness",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                        null, new[] { typeof(Scene) }, null);
                    if (m != null) { m.Invoke(null, new object[] { s }); cleared = !s.isDirty; }
                }
                catch (Exception) { cleared = false; }
                if (!cleared)
                    r.AddIssue(CctSeverity.Warning, "SCENE_DIRTIED",
                        $"監査中にシーン '{s.name}' が未保存状態になりました。内容は変更していませんが、保存せずに閉じても問題ありません", value: s.path);
                else
                    r.AddIssue(CctSeverity.Info, "SCENE_DIRTY_CLEARED",
                        $"監査中 (一時オブジェクトの作成時) にシーン '{s.name}' が変更ありと記録されたため、元の未変更の状態に戻しました", value: s.path);
            }
        }

        // =====================================================================================================================
        // Context shared by the partials
        // =====================================================================================================================

        sealed class Ctx
        {
            public readonly CctArgs Args;
            public readonly CctResult R;
            public readonly GameObject Root;
            public readonly Transform RootT;
            public readonly Stopwatch Clock;
            public readonly string Platform;
            public readonly bool WantPc, WantMobile;
            public readonly string MobileKey;
            public readonly bool ActiveMobile;
            public readonly string ActiveTarget;
            public readonly bool PlayMode;
            public readonly int TopN;
            public readonly bool IncludeLists;
            public string PrefabAssetPath;
            /// <summary>The resolved object inside the prefab asset (stable instance ID) when auditing loaded prefab contents.</summary>
            public GameObject AssetObject;
            public bool FromPrefabContents;
            public bool ExplicitTarget;
            public Component Descriptor;
            public Animator RootAnimator;
            /// <summary>Playable layers read from the descriptor (Descriptor.cs), used by the layer / Write Defaults checks.</summary>
            public readonly List<LayerInfo> Layers = new List<LayerInfo>();
            /// <summary>Humanoid mapping of the root Animator (Descriptor.cs); null when there is no Animator.</summary>
            public HumanInfo Human;
            /// <summary>Rows for data.components.non_whitelisted (Checks.cs).</summary>
            public readonly List<object> NonWhitelisted = new List<object>();
            /// <summary>Expression Parameters of the descriptor (Expressions.cs); compared with the NDMF-processed clone in PostBuild.cs.</summary>
            public List<ParamDef> ParamDefs;
            /// <summary>Short post_build summary appended to the result message ("ビルド後 PC Good / Android VeryPoor").</summary>
            public string PostBuildSummary;

            /// <summary>The one pass over the audited hierarchy (Stats.cs).</summary>
            public HierarchyScan Scan;

            readonly Dictionary<string, int> _issueCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            readonly Dictionary<string, KeyValuePair<string, int>> _suppressed = new Dictionary<string, KeyValuePair<string, int>>(StringComparer.Ordinal);
            readonly Dictionary<Transform, string> _paths = new Dictionary<Transform, string>();

            public Ctx(CctArgs args, CctResult r, GameObject root, string platform, Stopwatch clock)
            {
                Args = args; R = r; Root = root; RootT = root.transform; Platform = platform; Clock = clock;
                WantPc = platform == "all" || platform == "pc";
                WantMobile = platform != "pc";
                MobileKey = platform == "ios" ? "ios" : "android";
                var target = EditorUserBuildSettings.activeBuildTarget;
                ActiveTarget = target.ToString();
                // Same rule as the SDK's ValidationEditorHelpers.IsMobilePlatform(): iOS or Android build target.
                ActiveMobile = target == BuildTarget.Android || target == BuildTarget.iOS;
                PlayMode = EditorApplication.isPlayingOrWillChangePlaymode;
                TopN = args.GetInt("top_n", 15, 1, 200);
                IncludeLists = args.GetBool("include_lists", true);
            }

            public IEnumerable<KeyValuePair<string, bool>> Platforms()
            {
                if (WantPc) yield return new KeyValuePair<string, bool>("pc", false);
                if (WantMobile) yield return new KeyValuePair<string, bool>(MobileKey, true);
            }

            public string PathOf(Transform t)
            {
                if (t == null) return null;
                if (_paths.TryGetValue(t, out var p)) return p;
                p = CctTargets.GetPath(t);
                _paths[t] = p;
                return p;
            }

            public string PathOf(Component c) => c == null ? null : PathOf(c.transform);

            public bool IsEditorOnly(Component c) => Scan != null && Scan.IsEditorOnly(c);

            /// <summary>Adds an issue, at most <see cref="MaxIssuesPerCode"/> per code; the overflow is summarised by <see cref="FlushSuppressed"/>.</summary>
            public void Issue(string severity, string code, string message, string path = null, object value = null, object limit = null, string asset = null)
            {
                _issueCounts.TryGetValue(code, out var n);
                _issueCounts[code] = n + 1;
                if (n < MaxIssuesPerCode)
                {
                    R.AddIssue(severity, code, message, path, value, limit, asset);
                    return;
                }
                _suppressed.TryGetValue(code, out var s);
                var worst = s.Key == null || CctSeverity.Rank(severity) < CctSeverity.Rank(s.Key) ? severity : s.Key;
                _suppressed[code] = new KeyValuePair<string, int>(worst, s.Value + 1);
            }

            public void FlushSuppressed()
            {
                foreach (var kv in _suppressed)
                    R.AddIssue(kv.Value.Key, kv.Key, $"同じ種類の指摘がほかに {kv.Value.Value} 件あります (一覧はレポートの breakdown / 各リストを参照)", value: kv.Value.Value);
                _suppressed.Clear();
            }

            public string PlatformLabel(string key) => key == "pc" ? "PC" : key == "ios" ? "iOS" : "Android";
        }

        // =====================================================================================================================
        // Orchestration
        // =====================================================================================================================

        static void Audit(Ctx ctx)
        {
            var r = ctx.R;
            ctx.Scan = ScanHierarchy(ctx.RootT);             // Stats.cs: one pass over transforms + components
            ctx.Descriptor = FindDescriptorComponent(ctx.Root);
            ctx.RootAnimator = ctx.Root.GetComponent<Animator>();

            if (ctx.Descriptor == null)
                ctx.Issue(CctSeverity.Warning, "DESCRIPTOR_MISSING",
                    "VRCAvatarDescriptor がありません。アバターではなく部品 (衣装・ギミックのプレハブ等) として階層を検査しました。ランクは参考値です",
                    ctx.PathOf(ctx.RootT));
            if (ctx.PlayMode)
                ctx.Issue(CctSeverity.Info, "PLAY_MODE",
                    "Play モード中です。SDK の計算は動作中の PhysBone を初期化し直すため使わず、自前の推定値を出しています。NDMF の Apply on Play 済みなら値はビルド後に近い状態です");

            // 1) SDK calculator first (it refreshes VRC constraint groups that the estimator then reads), 2) estimator.
            // Every section below fails soft (AUDIT_SECTION_FAILED) so one unexpected exception does not hide the rest.
            SdkResult sdk = null;
            if (!ctx.PlayMode) sdk = RunSdkCalculator(ctx.Root, ctx.WantPc, ctx.WantMobile);
            Est est;
            try { est = Estimate(ctx, ctx.Scan); }
            catch (Exception e)
            {
                SectionFailed(ctx, "estimate", e);
                est = new Est();
            }

            r.Put("avatar", Section(ctx, "avatar", () => DescribeAvatar(ctx)));
            var output = BuildStatsOutput(ctx, est, sdk, true);
            r.Put("method", output.Method);
            r.Put("ranks", output.Ranks);
            r.Put("stats", output.Stats);

            if (ctx.Descriptor != null) r.Put("expressions", Section(ctx, "expressions", () => AuditExpressions(ctx)));
            RunChecks(ctx, est, output);
            r.Put("missing_scripts", output.MissingScripts ?? new Dictionary<string, object> { ["total"] = ctx.Scan.MissingScriptTotal });
            r.Put("components", Section(ctx, "components", () => DescribeComponents(ctx)));
            r.Put("notes", Section(ctx, "notes", () => BuildNotes(ctx, output)));
            if (ctx.IncludeLists) r.Put("breakdown", Section(ctx, "breakdown", () => est.Breakdown(ctx.TopN)));
            r.Put("rank_tables", Section(ctx, "rank_tables", () => RankTablesForReport(ctx)));

            if (ctx.Args.GetBool("post_build", false)) r.Put("post_build", Section(ctx, "post_build", () => RunPostBuild(ctx, output)));

            ctx.FlushSuppressed();
            r.Message = BuildMessage(ctx, output);
        }

        static Dictionary<string, object> Section(Ctx ctx, string name, Func<Dictionary<string, object>> build)
        {
            try { return build() ?? new Dictionary<string, object>(); }
            catch (Exception e)
            {
                SectionFailed(ctx, name, e);
                return new Dictionary<string, object> { ["error"] = Unwrap(e).GetType().Name + ": " + Unwrap(e).Message };
            }
        }

        static void Guard(Ctx ctx, string name, Action check)
        {
            try { check(); }
            catch (Exception e) { SectionFailed(ctx, name, e); }
        }

        static void SectionFailed(Ctx ctx, string name, Exception e)
        {
            e = Unwrap(e);
            var stack = e.StackTrace ?? "";
            if (stack.Length > 600) stack = stack.Substring(0, 600) + "...";
            ctx.R.AddIssue(CctSeverity.Warning, "AUDIT_SECTION_FAILED",
                $"{name} の処理中に例外が発生したため、この部分を省略しました ({e.GetType().Name}: {e.Message})。ほかの結果は有効です", value: stack);
        }

        static Component FindDescriptorComponent(GameObject go)
        {
            foreach (var c in go.GetComponents<Component>())
                if (c != null && CctSerialized.IsType(c, CctVrcTypes.AvatarDescriptor)) return c;
            return null;
        }

        static string BuildMessage(Ctx ctx, StatsOutput o)
        {
            var parts = new List<string>();
            foreach (var p in ctx.Platforms())
            {
                if (!o.Overall.TryGetValue(p.Key, out var overall)) continue;
                o.LimitingKeys.TryGetValue(p.Key, out var lim);
                var top = lim == null || lim.Count == 0 ? "" : $" (主因: {string.Join(", ", lim.Take(3))}{(lim.Count > 3 ? " ほか" : "")})";
                parts.Add($"{ctx.PlatformLabel(p.Key)} {Ranks.Name(overall)}{top}");
            }
            var r = ctx.R;
            var head = ctx.Descriptor != null ? $"アバター '{ctx.Root.name}'" : $"階層 '{ctx.Root.name}' (Descriptor なし)";
            var post = string.IsNullOrEmpty(ctx.PostBuildSummary) ? "" : $" / {ctx.PostBuildSummary}";
            return $"{head}: {string.Join(" / ", parts)}{(o.UsedSdk ? "" : " [推定値]")}{post} / error {r.CountIssues(CctSeverity.Error)}, warning {r.CountIssues(CctSeverity.Warning)}";
        }

        // =====================================================================================================================
        // Small shared helpers
        // =====================================================================================================================

        static double Round(double v, int digits = 3) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Round(v, digits);

        static List<object> Vec(Vector3 v, int digits = 3) => new List<object> { Round(v.x, digits), Round(v.y, digits), Round(v.z, digits) };

        static string AssetPathOrName(Object o)
        {
            if (o == null) return null;
            var p = AssetDatabase.GetAssetPath(o);
            if (string.IsNullOrEmpty(p)) return o.name + " (シーン内/生成物)";
            return AssetDatabase.IsSubAsset(o) || p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".blend", StringComparison.OrdinalIgnoreCase)
                ? p + "#" + o.name
                : p;
        }

        static void Inc(Dictionary<string, int> d, string key, int by = 1)
        {
            d.TryGetValue(key, out var n);
            d[key] = n + by;
        }
    }
}
