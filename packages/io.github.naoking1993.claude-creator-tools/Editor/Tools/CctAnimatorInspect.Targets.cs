using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>cct_animator_inspect: input resolution (controller / avatar / animator / part) and the controllers to inspect.</summary>
    public static partial class CctAnimatorInspect
    {
        // SDK 3.10.5 sample controllers used as the stand-in for "default" playable layers. File names and GUIDs from
        // com.vrchat.avatars 3.10.5 Samples/AV3 Demo Assets/Animation/Controllers/*.controller.meta; the type -> file mapping
        // is the one NDMF uses (VRChatPlatformAnimatorBindings.GetFallbackController).
        const string SdkControllersDir = "Packages/com.vrchat.avatars/Samples/AV3 Demo Assets/Animation/Controllers/";

        static readonly Dictionary<int, string[]> SdkDefaultControllers = new Dictionary<int, string[]>
        {
            [LtBase] = new[] { "vrc_AvatarV3LocomotionLayer", "4e4e1a372a526074884b7311d6fc686b" },
            [LtAdditive] = new[] { "vrc_AvatarV3IdleLayer", "573a1373059632b4d820876efe2d277f" },
            [LtGesture] = new[] { "vrc_AvatarV3HandsLayer", "404d228aeae421f4590305bc4cdaba16" },
            [LtAction] = new[] { "vrc_AvatarV3ActionLayer", "3e479eeb9db24704a828bffb15406520" },
            [LtFx] = new[] { "vrc_AvatarV3FaceLayer", "d40be620cf6c698439a2f0a5144919fe" },
            [LtSitting] = new[] { "vrc_AvatarV3SittingLayer", "1268460c14f873240981bf15aa88b21a" },
            [LtTPose] = new[] { "vrc_AvatarV3UtilityTPose", "00121b5812372b74f9012473856d8acf" },
            [LtIKPose] = new[] { "vrc_AvatarV3UtilityIKPose", "a9b90a833b3486e4b82834c9d1f7c4ee" },
        };

        // =====================================================================================================================
        // Input resolution
        // =====================================================================================================================

        static CctResult ResolveInputs(Ctx ctx)
        {
            var controllerSpec = NullIfEmpty(ctx.Args.GetString("controller"));
            var targetSpec = NullIfEmpty(ctx.Args.GetString("target"));

            if (controllerSpec != null)
            {
                var rac = LoadController(controllerSpec, out var error, out var candidates);
                if (rac == null)
                {
                    var f = CctResult.Fail(error, candidates.Count > 0 ? "AMBIGUOUS_CONTROLLER" : "CONTROLLER_NOT_FOUND");
                    if (candidates.Count > 0) f.Put("candidates", candidates);
                    return f;
                }
                ctx.Mode = "controller";
                ctx.ExplicitController = rac;
                return targetSpec != null ? ResolveTarget(ctx, targetSpec, false) : null;
            }

            bool auto = targetSpec == null || string.Equals(targetSpec, "selection", StringComparison.OrdinalIgnoreCase);
            if (auto && Selection.activeGameObject == null && Selection.activeObject is RuntimeAnimatorController selected)
            {
                ctx.Mode = "controller";
                ctx.ExplicitController = selected;
                ctx.Issue(CctSeverity.Info, "TARGET_ADJUSTED", $"Project で選択中のコントローラ '{selected.name}' を解析しました", asset: AssetDatabase.GetAssetPath(selected));
                return null;
            }
            return ResolveTarget(ctx, targetSpec, true);
        }

        /// <summary>controller = "Assets/..controller" path, a 32-hex GUID, or a unique asset name.</summary>
        static RuntimeAnimatorController LoadController(string spec, out string error, out List<string> candidates)
        {
            error = null;
            candidates = new List<string>();
            string path = null;
            if (CctPaths.IsAssetPath(spec)) path = spec;
            else if (spec.Length == 32 && spec.All(Uri.IsHexDigit)) path = AssetDatabase.GUIDToAssetPath(spec);

            if (path != null)
            {
                var rac = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(path);
                if (rac == null)
                    rac = AssetDatabase.LoadAllAssetsAtPath(path).OfType<RuntimeAnimatorController>().FirstOrDefault();
                if (rac == null)
                    error = string.IsNullOrEmpty(path)
                        ? $"GUID '{spec}' のアセットが見つかりません"
                        : $"'{path}' は AnimatorController / AnimatorOverrideController として読み込めません";
                return rac;
            }

            var found = new List<string>();
            foreach (var filter in new[] { "t:AnimatorController", "t:AnimatorOverrideController" })
                foreach (var guid in AssetDatabase.FindAssets(spec + " " + filter))
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.Equals(Path.GetFileNameWithoutExtension(p), spec, StringComparison.Ordinal) && !found.Contains(p)) found.Add(p);
                }
            if (found.Count == 1) return AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(found[0]);
            if (found.Count == 0)
                error = $"名前 '{spec}' の AnimatorController が見つかりません (Assets/... のパスか GUID で指定してください)";
            else
            {
                error = $"名前 '{spec}' のコントローラが {found.Count} 個あります。Assets/... のパスで指定してください";
                candidates.AddRange(found.Take(30));
            }
            return null;
        }

        /// <summary>
        /// Resolves the avatar / object. primary=false: context for an explicit controller (binding root, expression
        /// parameters, MA lookup), where any GameObject is accepted.
        /// </summary>
        static CctResult ResolveTarget(Ctx ctx, string spec, bool primary)
        {
            bool auto = spec == null || string.Equals(spec, "selection", StringComparison.OrdinalIgnoreCase);
            GameObject go;
            if (auto && Selection.activeGameObject == null)
            {
                var all = AvatarsInLoadedScenes();
                var active = all.Where(g => g.activeInHierarchy).ToList();
                var pick = active.Count == 1 ? active[0] : (active.Count == 0 && all.Count == 1 ? all[0] : null);
                if (pick == null)
                {
                    var f = CctResult.Fail(all.Count == 0
                            ? "対象がありません: controller (Assets/...controller) か target (アバター) を指定するか、Hierarchy / Project で選択してください"
                            : $"アバターが {all.Count} 体あります (アクティブ {active.Count} 体)。target にパスか instance ID を指定してください",
                        all.Count == 0 ? "NO_TARGET" : "AMBIGUOUS_TARGET");
                    if (all.Count > 0) f.Put("candidates", all.Take(30).Select(DescribeCandidate).ToList());
                    return f;
                }
                go = pick;
                ctx.Issue(CctSeverity.Info, "TARGET_ADJUSTED", $"対象未指定のため、読み込み中のシーンで唯一のアバター '{pick.name}' を解析しました", CctTargets.GetPath(pick));
            }
            else
            {
                var res = CctTargets.Resolve(auto ? null : spec);
                if (!res.Ok)
                {
                    var f = CctResult.Fail(res.Error, res.Candidates.Count > 0 ? "AMBIGUOUS_TARGET" : "TARGET_NOT_FOUND");
                    if (res.Candidates.Count > 0) f.Put("candidates", res.Candidates);
                    return f;
                }
                go = res.GameObject;
            }

            var owner = FindDescriptorOwner(go.transform);
            if (owner == null)
            {
                var below = new List<GameObject>();
                foreach (var c in go.GetComponentsInChildren<Component>(true))
                    if (c != null && CctSerialized.IsType(c, CctVrcTypes.AvatarDescriptor)) below.Add(c.gameObject);
                if (below.Count == 1)
                {
                    owner = below[0].transform;
                    ctx.Issue(CctSeverity.Info, "TARGET_ADJUSTED", $"'{go.name}' の子にあるアバター '{owner.name}' を解析しました", CctTargets.GetPath(owner));
                }
                else if (below.Count > 1)
                {
                    var f = CctResult.Fail($"'{go.name}' の下にアバターが {below.Count} 体あります。どれか 1 体を target に指定してください", "AMBIGUOUS_TARGET");
                    f.Put("candidates", below.Take(30).Select(DescribeCandidate).ToList());
                    return f;
                }
            }
            else if (owner.gameObject != go)
                ctx.Issue(CctSeverity.Info, "TARGET_ADJUSTED", $"'{go.name}' を含むアバタールート '{owner.name}' を対象にしました", CctTargets.GetPath(owner));

            if (owner != null)
            {
                ctx.AvatarRoot = owner.gameObject;
                ctx.Target = owner.gameObject;
                ctx.Descriptor = owner.GetComponents<Component>().FirstOrDefault(c => c != null && CctSerialized.IsType(c, CctVrcTypes.AvatarDescriptor));
                if (primary) ctx.Mode = "avatar";
                return null;
            }

            ctx.Target = go;
            if (!primary) return null;

            var animator = go.GetComponent<Animator>();
            if (animator != null && animator.runtimeAnimatorController != null)
            {
                ctx.Mode = "animator";
                return null;
            }
            bool hasMa = go.GetComponentsInChildren<Component>(true).Any(c => c != null && CctSerialized.IsType(c, TMaMergeAnimator));
            if (hasMa)
            {
                ctx.Mode = "part";
                return null;
            }
            return CctResult.Fail($"'{go.name}' には VRCAvatarDescriptor も Animator のコントローラも MA Merge Animator もありません (controller にアセットパスを指定できます)", "NO_CONTROLLER");
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

        // =====================================================================================================================
        // Units
        // =====================================================================================================================

        static CctResult BuildUnits(Ctx ctx)
        {
            if (ctx.Descriptor != null) ReadDescriptor(ctx);
            var maRoot = ctx.AvatarRoot != null ? ctx.AvatarRoot : ctx.Target;
            if (maRoot != null && (ctx.IncludeMa || ctx.Mode == "part" || ctx.Mode == "controller")) ReadMaComponents(ctx, maRoot);

            switch (ctx.Mode)
            {
                case "controller":
                    AddControllerUnit(ctx);
                    break;
                case "animator":
                {
                    var animator = ctx.Target.GetComponent<Animator>();
                    var u = NewUnit(ctx, "animator", $"Animator {ctx.Target.name}", "animator", animator.runtimeAnimatorController);
                    u.BindingRoot = ctx.Target.transform;
                    u.BindingRootRule = "animator_object";
                    break;
                }
                case "part":
                    foreach (var m in ctx.MaMerges)
                        if (m.Animator != null && !m.EditorOnly && (ctx.LayerArg == "all" || LayerArgToType[ctx.LayerArg] == m.LayerType))
                            AddMaUnit(ctx, m);
                    if (ctx.Units.Count == 0)
                        return CctResult.Fail($"'{ctx.Target.name}' の MA Merge Animator に {ctx.LayerArg} 向けのコントローラがありません (layer=all で全部を対象にできます)", "LAYER_NOT_FOUND");
                    ctx.Notes.Add("VRCAvatarDescriptor の外にある部品として解析しました。Absolute のパスはアバターに組み込むまで検証できず、Relative の基準は推定です");
                    break;
                default:
                    AddAvatarUnits(ctx);
                    if (ctx.Units.Count == 0)
                    {
                        var f = CctResult.Fail(ctx.LayerArg == "all"
                                ? $"アバター '{ctx.AvatarRoot.name}' に解析できるコントローラがありません"
                                : $"アバター '{ctx.AvatarRoot.name}' に {ctx.LayerArg} のプレイアブルレイヤーも、そこへ統合する MA Merge Animator もありません (Generic アバターは Base / Action / FX のみ)",
                            "LAYER_NOT_FOUND");
                        f.Put("playable_layers", ctx.DescriptorLayers.Select(d => LayerTypeName(d.Type)).ToList<object>());
                        return f;
                    }
                    break;
            }

            foreach (var u in ctx.Units) ResolveUnitController(ctx, u);
            CheckMaComponents(ctx);
            return null;
        }

        static Unit NewUnit(Ctx ctx, string id, string label, string origin, RuntimeAnimatorController source)
        {
            var u = new Unit
            {
                Id = id,
                Label = label,
                Origin = origin,
                Source = source,
                Asset = source != null ? AssetDatabase.GetAssetPath(source) : null,
                AvatarRoot = ctx.AvatarRoot != null ? ctx.AvatarRoot.transform : null,
            };
            ctx.Units.Add(u);
            return u;
        }

        static void AddControllerUnit(Ctx ctx)
        {
            var rac = ctx.ExplicitController;
            var u = NewUnit(ctx, "controller", rac.name, "asset", rac);
            if (ctx.Target == null) return;

            var ma = ctx.MaMerges.FirstOrDefault(m => m.Animator == rac);
            var dl = ctx.DescriptorLayers.FirstOrDefault(d => d.Controller == rac && !d.IsDefault && ctx.CustomizeLayers);
            if (ma != null)
            {
                u.Ma = ma;
                ma.Unit = u;
                u.LayerType = ma.LayerType;
                u.BindingRoot = ma.PathRoot;
                u.BindingRootRule = ma.PathRootRule;
                u.BindingRootEstimate = ma.PathRootEstimate;
                u.Label = $"{rac.name} (MA {ma.Id} → {LayerTypeName(ma.LayerType)})";
                if (ctx.MaMerges.Count(m => m.Animator == rac) > 1)
                    u.UnitNotes.Add("このコントローラは複数の MA Merge Animator で使われています。パスの基準は最初の " + ma.Id + " に合わせました");
            }
            else if (dl != null)
            {
                u.LayerType = dl.Type;
                dl.Unit = u;
                u.BindingRoot = ctx.AvatarRoot.transform;
                u.BindingRootRule = "avatar_root";
                u.Label = $"{rac.name} ({LayerTypeName(dl.Type)})";
            }
            else
            {
                u.BindingRoot = (ctx.AvatarRoot != null ? ctx.AvatarRoot : ctx.Target).transform;
                u.BindingRootRule = ctx.AvatarRoot != null ? "avatar_root" : "target";
                u.UnitNotes.Add("このコントローラは対象のプレイアブルレイヤーにも MA Merge Animator にも設定されていません。パスは対象のルートからとして検査しました");
            }
        }

        static void AddAvatarUnits(Ctx ctx)
        {
            bool all = ctx.LayerArg == "all";
            int wanted = all ? -1 : LayerArgToType[ctx.LayerArg];
            foreach (var dl in ctx.DescriptorLayers)
            {
                if (!all && dl.Type != wanted) continue;
                var typeName = LayerTypeName(dl.Type);
                string id = typeName.ToLowerInvariant();
                if (ctx.Units.Any(x => x.Id == id)) id += "_" + ctx.Units.Count;
                if (dl.IsDefault || !ctx.CustomizeLayers)
                {
                    var def = LoadSdkDefault(dl.Type, out var defPath);
                    dl.SdkDefaultAsset = defPath;
                    if (def == null)
                    {
                        ctx.Issue(CctSeverity.Warning, "SDK_DEFAULT_CONTROLLER_NOT_FOUND",
                            $"{typeName} は既定レイヤーですが、SDK 同梱の既定コントローラ ({defPath ?? "不明"}) が見つかりません (com.vrchat.avatars を確認してください)");
                        continue;
                    }
                    var u = NewUnit(ctx, id, $"{typeName} (SDK 既定)", "sdk_default", def);
                    u.LayerType = dl.Type;
                    u.Quiet = true;
                    u.BindingRoot = ctx.AvatarRoot.transform;
                    u.BindingRootRule = "avatar_root";
                    dl.Unit = u;
                    ctx.Issue(CctSeverity.Info, "SDK_DEFAULT_LAYER",
                        $"{typeName} は VRChat の既定レイヤーです{(ctx.CustomizeLayers ? "" : " (Playable Layers のカスタマイズが無効)")}。SDK 同梱の {Path.GetFileName(defPath)} を代わりに解析しました",
                        asset: defPath);
                    continue;
                }
                if (dl.Controller == null)
                {
                    ctx.Issue(CctSeverity.Warning, "LAYER_CONTROLLER_MISSING",
                        dl.ControllerMissing
                            ? $"{typeName} のコントローラ参照が切れています (削除されたアセット)"
                            : $"{typeName} はカスタム (既定ではない) ですがコントローラが未設定です");
                    continue;
                }
                var unit = NewUnit(ctx, id, $"{typeName} ({dl.Controller.name})", "descriptor", dl.Controller);
                unit.LayerType = dl.Type;
                unit.BindingRoot = ctx.AvatarRoot.transform;
                unit.BindingRootRule = "avatar_root";
                dl.Unit = unit;
            }

            if (!ctx.IncludeMa) return;
            foreach (var m in ctx.MaMerges)
            {
                if (m.Animator == null || m.EditorOnly) continue;
                if (!all && m.LayerType != wanted) continue;
                AddMaUnit(ctx, m);
            }
        }

        static void AddMaUnit(Ctx ctx, MaMerge m)
        {
            var u = NewUnit(ctx, m.Id, $"MA {m.Comp.name} → {LayerTypeName(m.LayerType)} ({m.Animator.name})", "ma_merge_animator", m.Animator);
            u.LayerType = m.LayerType;
            u.Ma = m;
            u.BindingRoot = m.PathRoot;
            u.BindingRootRule = m.PathRootRule;
            u.BindingRootEstimate = m.PathRootEstimate;
            m.Unit = u;
            if (m.MergeMode == 1)
                u.UnitNotes.Add($"Replace: ビルド時に記述子の {LayerTypeName(m.LayerType)} コントローラを置き換えます");
        }

        static AnimatorController LoadSdkDefault(int type, out string path)
        {
            path = null;
            if (!SdkDefaultControllers.TryGetValue(type, out var entry)) return null;
            path = SdkControllersDir + entry[0] + ".controller";
            var ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (ac != null) return ac;
            var byGuid = AssetDatabase.GUIDToAssetPath(entry[1]);
            if (!string.IsNullOrEmpty(byGuid))
            {
                ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(byGuid);
                if (ac != null) path = byGuid;
            }
            return ac;
        }

        /// <summary>RuntimeAnimatorController -> AnimatorController, applying AnimatorOverrideController chains (outermost override wins).</summary>
        static void ResolveUnitController(Ctx ctx, Unit u)
        {
            var cur = u.Source;
            var maps = new List<Dictionary<AnimationClip, AnimationClip>>();
            int guard = 0;
            while (cur is AnimatorOverrideController aoc && guard++ < 8)
            {
                u.OverrideChain.Add(AssetLabel(aoc));
                var map = new Dictionary<AnimationClip, AnimationClip>();
                try
                {
                    var list = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                    aoc.GetOverrides(list);
                    foreach (var kv in list)
                        if (kv.Key != null && kv.Value != null && kv.Value != kv.Key) map[kv.Key] = kv.Value;
                }
                catch (Exception e)
                {
                    u.UnitNotes.Add($"{aoc.name} の overrides を読めませんでした: {e.Message}");
                }
                maps.Add(map);
                cur = aoc.runtimeAnimatorController;
            }
            u.Controller = cur as AnimatorController;
            if (maps.Count > 0)
            {
                u.Overrides = new Dictionary<AnimationClip, AnimationClip>();
                foreach (var map in maps)                      // maps[0] is the outermost override controller
                    foreach (var kv in map)
                        if (!u.Overrides.ContainsKey(kv.Key)) u.Overrides[kv.Key] = kv.Value;
            }
            if (u.Controller == null)
            {
                u.Failed = true;
                ctx.Issue(CctSeverity.Warning, "CONTROLLER_UNREADABLE",
                    u.Source is AnimatorOverrideController
                        ? $"'{u.Label}' は AnimatorOverrideController ですが、ベースの AnimatorController がありません"
                        : $"'{u.Label}' は AnimatorController ではありません ({u.Source?.GetType().Name ?? "null"})",
                    asset: u.Asset);
            }
        }

        // =====================================================================================================================
        // VRCAvatarDescriptor + Expression Parameters (SerializedObject; paths in tests/vrc-fields/CctAnimatorInspect.txt)
        // =====================================================================================================================

        static void ReadDescriptor(Ctx ctx)
        {
            using (var so = new SerializedObject(ctx.Descriptor))
            {
                ctx.CustomizeLayers = CctSerialized.GetBool(so.FindProperty("customizeAnimationLayers"), true);
                foreach (var arrayName in new[] { "baseAnimationLayers", "specialAnimationLayers" })
                {
                    foreach (var el in CctSerialized.Elements(so.FindProperty(arrayName)))
                    {
                        var ctrlProp = CctSerialized.Child(el, "animatorController");
                        ctx.DescriptorLayers.Add(new DescriptorLayer
                        {
                            Type = CctSerialized.GetInt(CctSerialized.Child(el, "type"), -1),
                            Special = arrayName == "specialAnimationLayers",
                            IsDefault = CctSerialized.GetBool(CctSerialized.Child(el, "isDefault"), true),
                            IsEnabled = CctSerialized.GetBool(CctSerialized.Child(el, "isEnabled"), true),
                            Controller = CctSerialized.GetObject(ctrlProp) as RuntimeAnimatorController,
                            ControllerMissing = CctSerialized.IsMissingReference(ctrlProp),
                            Mask = CctSerialized.GetObject(CctSerialized.Child(el, "mask")),
                        });
                    }
                }
                var exprProp = so.FindProperty("expressionParameters");
                var expr = CctSerialized.GetObject(exprProp);
                if (expr != null)
                {
                    ctx.ExpressionAsset = AssetLabel(expr);
                    ReadExpressionParameters(ctx, expr);
                }
                else if (CctSerialized.IsMissingReference(exprProp)) ctx.ExpressionAsset = "(missing)";
            }

            // docs playable-layers.md:39 (same controller in several playable layers) and :40-43 (only Animation Controllers)
            if (!ctx.CustomizeLayers) return;
            var custom = ctx.DescriptorLayers.Where(d => !d.IsDefault && d.Controller != null).ToList();
            foreach (var g in custom.GroupBy(d => d.Controller))
                if (g.Count() > 1)
                    ctx.Issue(CctSeverity.Warning, "CONTROLLER_REUSED",
                        $"コントローラ '{g.Key.name}' が複数のプレイアブルレイヤー ({string.Join(", ", g.Select(d => LayerTypeName(d.Type)))}) で使われています。docs: 同じコントローラを複数レイヤーに使わないこと (playable-layers.md)",
                        asset: AssetLabel(g.Key));
            foreach (var d in custom)
                if (d.Controller is AnimatorOverrideController)
                    ctx.Issue(CctSeverity.Warning, "OVERRIDE_CONTROLLER_IN_PLAYABLE_LAYER",
                        $"{LayerTypeName(d.Type)} に AnimatorOverrideController が設定されています。docs: プレイアブルレイヤーには Animation Controller のみ対応 (playable-layers.md、他の種類はエラーやアップロード不可の原因)",
                        asset: AssetLabel(d.Controller));
        }

        static void ReadExpressionParameters(Ctx ctx, Object asset)
        {
            ctx.Expression = new Dictionary<string, ExprParam>(StringComparer.Ordinal);
            using (var so = new SerializedObject(asset))
            {
                foreach (var el in CctSerialized.Elements(so.FindProperty("parameters")))
                {
                    var name = CctSerialized.GetString(CctSerialized.Child(el, "name"), "");
                    if (string.IsNullOrEmpty(name) || ctx.Expression.ContainsKey(name)) continue;
                    ctx.Expression[name] = new ExprParam
                    {
                        Name = name,
                        ValueType = CctSerialized.GetInt(CctSerialized.Child(el, "valueType"), 1),
                        Synced = CctSerialized.GetBool(CctSerialized.Child(el, "networkSynced"), true),
                        Saved = CctSerialized.GetBool(CctSerialized.Child(el, "saved"), true),
                        Default = CctSerialized.GetFloat(CctSerialized.Child(el, "defaultValue"), 0f),
                    };
                }
            }
        }

        // =====================================================================================================================
        // Modular Avatar Merge Animator (SerializedObject; field names from MA source, see header)
        // =====================================================================================================================

        static void ReadMaComponents(Ctx ctx, GameObject root)
        {
            var avatarRoot = ctx.AvatarRoot != null ? ctx.AvatarRoot.transform : null;
            int n = 0;
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                if (CctSerialized.IsType(c, TMaMergeBlendTree))
                {
                    using (var so = new SerializedObject(c))
                    {
                        var motion = CctSerialized.GetObject(so.FindProperty("BlendTree"));
                        ctx.MaBlendTrees.Add(new Dictionary<string, object>
                        {
                            ["path"] = CctTargets.GetPath(c.transform),
                            ["motion"] = motion != null ? AssetLabel(motion) : null,
                            ["path_mode"] = CctSerialized.GetInt(so.FindProperty("PathMode"), 0) == 1 ? "Absolute" : "Relative",
                            ["note"] = "MA Merge Motion: ビルド時に FX へ Direct BlendTree レイヤーとして統合されます (このツールでは未解析)",
                        });
                    }
                    continue;
                }
                if (!CctSerialized.IsType(c, TMaMergeAnimator)) continue;
                var m = new MaMerge { Id = "ma" + (++n), Comp = c, Order = n };
                using (var so = new SerializedObject(c))
                {
                    var animProp = so.FindProperty("animator");
                    m.Animator = CctSerialized.GetObject(animProp) as RuntimeAnimatorController;
                    m.AnimatorMissing = CctSerialized.IsMissingReference(animProp);
                    // Absent keys (older prefabs) keep the C# defaults: FX, Relative, false, 0, Append (research/ma-ndmf-aao.md §2.3).
                    m.LayerType = CctSerialized.GetInt(so.FindProperty("layerType"), LtFx);
                    m.PathMode = CctSerialized.GetInt(so.FindProperty("pathMode"), 0);
                    m.MatchWd = CctSerialized.GetBool(so.FindProperty("matchAvatarWriteDefaults"), false);
                    m.DeleteAttached = CctSerialized.GetBool(so.FindProperty("deleteAttachedAnimator"), false);
                    m.Priority = CctSerialized.GetInt(so.FindProperty("layerPriority"), 0);
                    m.MergeMode = CctSerialized.GetInt(so.FindProperty("mergeAnimatorMode"), 0);
                    var rel = so.FindProperty("relativePathRoot");
                    m.RefPath = CctSerialized.GetString(CctSerialized.Child(rel, "referencePath"), "");
                    m.RefTarget = CctSerialized.GetObject(CctSerialized.Child(rel, "targetObject")) as GameObject;
                }
                m.EditorOnly = UnderEditorOnly(c.transform, root.transform);
                ResolveMaPathRoot(m, avatarRoot);
                ctx.MaMerges.Add(m);
            }

            // Build order within a layer type (NDMF/MA): Replace first, then layerPriority ascending, then hierarchy order.
            foreach (var g in ctx.MaMerges.GroupBy(x => x.LayerType))
            {
                int i = 0;
                foreach (var m in g.OrderBy(x => x.MergeMode == 1 ? 0 : 1).ThenBy(x => x.Priority).ThenBy(x => x.Order)) m.Order = ++i;
            }
        }

        /// <summary>MA MergeAnimatorProcessor.GetMotionBasePathCallback + AvatarObjectReference.Get (see header).</summary>
        static void ResolveMaPathRoot(MaMerge m, Transform avatarRoot)
        {
            if (m.PathMode == 1)
            {
                m.PathRoot = avatarRoot;
                m.PathRootRule = "ma_absolute: avatar_root";
                if (avatarRoot == null) m.PathRootRule = "ma_absolute: avatar root unknown (not under an avatar)";
                return;
            }
            Transform resolved = null;
            if (!string.IsNullOrEmpty(m.RefPath))
            {
                if (avatarRoot != null)
                {
                    if (m.RefTarget != null && CctTargets.IsDescendantOf(m.RefTarget.transform, avatarRoot)) resolved = m.RefTarget.transform;
                    else if (m.RefPath == MaAvatarRootToken) resolved = avatarRoot;
                    else resolved = avatarRoot.Find(m.RefPath);
                }
                else if (m.RefTarget != null)
                {
                    resolved = m.RefTarget.transform;
                    m.PathRootEstimate = true;
                }
                if (resolved == null && avatarRoot != null) m.RootUnresolved = true;
            }
            if (resolved != null)
            {
                m.PathRoot = resolved;
                m.PathRootRule = "ma_relative: relativePathRoot";
            }
            else
            {
                m.PathRoot = m.Comp.transform;
                m.PathRootRule = "ma_relative: component object";
            }
            if (avatarRoot == null) m.PathRootEstimate = true;
        }

        static bool UnderEditorOnly(Transform t, Transform stopAt)
        {
            for (var cur = t; cur != null; cur = cur.parent)
            {
                if (cur.CompareTag("EditorOnly")) return true;
                if (cur == stopAt) break;
            }
            return false;
        }

        static void CheckMaComponents(Ctx ctx)
        {
            foreach (var m in ctx.MaMerges)
            {
                var path = CctTargets.GetPath(m.Comp.transform);
                if (m.Animator == null)
                    ctx.Issue(CctSeverity.Warning, "MA_MERGE_ANIMATOR_NO_ANIMATOR",
                        m.AnimatorMissing
                            ? "MA Merge Animator のコントローラ参照が切れています (ビルド時は何もせずスキップされます)"
                            : "MA Merge Animator にコントローラが設定されていません (ビルド時は何もせずスキップされます)", path);
                if (m.EditorOnly)
                    ctx.Issue(CctSeverity.Info, "MA_EDITOR_ONLY", "EditorOnly タグのオブジェクト配下の MA Merge Animator はビルド前に削除され、統合されません", path);
                if (m.RootUnresolved)
                    ctx.Issue(CctSeverity.Warning, "MA_RELATIVE_ROOT_UNRESOLVED",
                        $"Relative Path Root '{m.RefPath}' をアバター内で解決できません。MA はこのコンポーネントの GameObject を基準にします", path, m.RefPath);
                if (m.MergeMode == 1)
                    ctx.Issue(CctSeverity.Info, "MA_REPLACE_MODE",
                        $"Replace モード: ビルド時に記述子の {LayerTypeName(m.LayerType)} コントローラ (レイヤーとパラメータ) が破棄され、このコントローラに置き換わります", path);
            }
            foreach (var g in ctx.MaMerges.Where(m => m.MergeMode == 1 && !m.EditorOnly && m.Animator != null).GroupBy(m => m.LayerType))
                if (g.Count() > 1)
                    ctx.Issue(CctSeverity.Error, "MA_MULTIPLE_REPLACE",
                        $"{LayerTypeName(g.Key)} に Replace モードの MA Merge Animator が {g.Count()} 個あります (MA のビルドエラー)",
                        value: g.Select(m => CctTargets.GetPath(m.Comp.transform)).ToList<object>());
            foreach (var d in ctx.DescriptorLayers)
                if (d.Unit != null && ctx.MaMerges.Any(m => m.MergeMode == 1 && m.LayerType == d.Type && m.Animator != null && !m.EditorOnly))
                    d.Unit.UnitNotes.Add("MA の Replace によりビルド時に置き換えられます");
        }
    }
}
