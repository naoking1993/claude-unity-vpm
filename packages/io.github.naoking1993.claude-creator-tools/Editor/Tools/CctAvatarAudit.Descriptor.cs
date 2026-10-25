using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    // data.avatar: which object was audited (scene object / prefab instance / prefab asset), blueprint ID, the
    // VRCAvatarDescriptor summary (view position, lip sync, eye look, playable layers, expression assets) and the root
    // Animator's humanoid mapping. Descriptor fields are read through SerializedObject by name (SDK 3.10.5 names verified
    // with FieldCheck, listed in tests/vrc-fields/CctAvatarAudit.txt).
    public static partial class CctAvatarAudit
    {
        // VRCAvatarDescriptor+AnimLayerType (FieldCheck enum dump, SDK 3.10.5).
        static readonly string[] AnimLayerTypeNames = { "Base", "Deprecated0", "Additive", "Gesture", "Action", "FX", "Sitting", "TPose", "IKPose" };
        const int LayerTypeGesture = 3;

        // VRC_AvatarDescriptor+LipSyncStyle (FieldCheck enum dump, SDK 3.10.5).
        static readonly string[] LipSyncNames = { "Default", "JawFlapBone", "JawFlapBlendShape", "VisemeBlendShape", "VisemeParameterOnly" };
        const int LipSyncVisemeBlendShape = 3;

        /// <summary>One entry of VRCAvatarDescriptor.baseAnimationLayers / specialAnimationLayers.</summary>
        sealed class LayerInfo
        {
            public string Slot;            // "base" | "special"
            public int Index;
            public int Type;               // AnimLayerType int value
            public string TypeName;
            public RuntimeAnimatorController Controller;
            public bool ControllerMissing; // reference to a deleted asset
            public AvatarMask Mask;
            public bool IsDefault, IsEnabled;
        }

        /// <summary>Root Animator humanoid mapping.</summary>
        sealed class HumanInfo
        {
            public bool IsHuman, AvatarValid;
            public string AvatarAsset;
            /// <summary>"animator" (Animator.GetBoneTransform) or "avatar_description" (names from Avatar.humanDescription).</summary>
            public string Source = "animator";
            public readonly Dictionary<HumanBodyBones, Transform> Bones = new Dictionary<HumanBodyBones, Transform>();
            public Transform Get(HumanBodyBones b) => Bones.TryGetValue(b, out var t) ? t : null;
        }

        /// <summary>Bones the SDK builder requires (feet / upper arms, AnalyzeIK: head, hands, feet, hips, spine, chest, neck, shoulders).</summary>
        static readonly HumanBodyBones[] SdkRequiredBones =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.Neck, HumanBodyBones.Head,
            HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
            HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm, HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
            HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
        };

        static readonly HumanBodyBones[] OptionalBones =
        {
            HumanBodyBones.UpperChest, HumanBodyBones.LeftToes, HumanBodyBones.RightToes, HumanBodyBones.LeftEye, HumanBodyBones.RightEye, HumanBodyBones.Jaw,
        };

        static bool IsLeftFinger(HumanBodyBones b) => b >= HumanBodyBones.LeftThumbProximal && b <= HumanBodyBones.LeftLittleDistal;

        static Dictionary<string, object> DescribeAvatar(Ctx ctx)
        {
            var root = ctx.Root;
            var d = CctTargets.Describe(root) ?? new Dictionary<string, object>();
            if (ctx.FromPrefabContents)
            {
                d.Remove("scene");
                d["asset"] = ctx.PrefabAssetPath;
                d["active_in_hierarchy"] = root.activeSelf;
                if (ctx.AssetObject != null) d["instance_id"] = ctx.AssetObject.GetInstanceID();
                d["audited_from"] = "prefab_contents (読み取り専用で一時的に読み込み、変更は保存しません)";
            }
            d["prefab"] = DescribePrefab(ctx);
            d["blueprint_id"] = CctVrcTypes.ReadBlueprintId(root);
            d["active_build_target"] = ctx.ActiveTarget;
            d["transforms"] = ctx.Scan.Transforms.Count;
            d["components"] = ctx.Scan.Components.Count;
            if (ctx.Scan.RootAboveEditorOnly)
                d["note"] = "アバターの親に EditorOnly タグのオブジェクトがあります。SDK はこのアバターの全コンポーネントを除外して数えます";

            d["animator"] = DescribeAnimator(ctx);
            if (ctx.Descriptor != null) d["descriptor"] = DescribeDescriptor(ctx);
            return d;
        }

        static Dictionary<string, object> DescribePrefab(Ctx ctx)
        {
            var p = new Dictionary<string, object>();
            try
            {
                if (ctx.PrefabAssetPath != null)
                {
                    var asset = ctx.AssetObject != null ? ctx.AssetObject : AssetDatabase.LoadAssetAtPath<GameObject>(ctx.PrefabAssetPath);
                    p["kind"] = "prefab_asset";
                    p["asset"] = ctx.PrefabAssetPath;
                    if (asset != null)
                    {
                        var type = PrefabUtility.GetPrefabAssetType(asset);
                        p["asset_type"] = type.ToString();
                        if (type == PrefabAssetType.Variant)
                        {
                            var baseObj = PrefabUtility.GetCorrespondingObjectFromSource(asset);
                            if (baseObj != null) p["variant_base"] = AssetDatabase.GetAssetPath(baseObj);
                        }
                    }
                    return p;
                }
                var go = ctx.Root;
                if (!PrefabUtility.IsPartOfPrefabInstance(go))
                {
                    p["kind"] = "scene_object";
                    return p;
                }
                p["kind"] = PrefabUtility.IsOutermostPrefabInstanceRoot(go) ? "prefab_instance_root" : "inside_prefab_instance";
                p["status"] = PrefabUtility.GetPrefabInstanceStatus(go).ToString();
                var source = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                if (!string.IsNullOrEmpty(source))
                {
                    p["source_asset"] = source;
                    var sourceRoot = AssetDatabase.LoadAssetAtPath<GameObject>(source);
                    if (sourceRoot != null) p["source_asset_type"] = PrefabUtility.GetPrefabAssetType(sourceRoot).ToString();
                }
            }
            catch (Exception e)
            {
                p["error"] = e.GetType().Name + ": " + e.Message;
            }
            return p;
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // Animator / humanoid
        // ---------------------------------------------------------------------------------------------------------------------

        static Dictionary<string, object> DescribeAnimator(Ctx ctx)
        {
            var a = ctx.RootAnimator;
            var d = new Dictionary<string, object> { ["present"] = a != null };
            if (a == null) return d;
            var human = new HumanInfo();
            ctx.Human = human;
            try
            {
                var avatar = a.avatar;
                human.AvatarAsset = avatar != null ? AssetPathOrName(avatar) : null;
                human.AvatarValid = avatar != null && avatar.isValid;
                human.IsHuman = SafeIsHuman(a);
                d["controller"] = a.runtimeAnimatorController != null ? AssetPathOrName(a.runtimeAnimatorController) : null;
                d["avatar"] = human.AvatarAsset;
                d["avatar_valid"] = human.AvatarValid;
                d["is_human"] = human.IsHuman;
                if (!human.IsHuman) return d;

                ReadHumanBones(ctx, a, human);
                var required = SdkRequiredBones.Where(b => human.Get(b) == null).Select(b => (object)b.ToString()).ToList();
                var optional = OptionalBones.Where(b => human.Get(b) == null).Select(b => (object)b.ToString()).ToList();
                int left = 0, right = 0;
                for (var b = HumanBodyBones.LeftThumbProximal; b <= HumanBodyBones.RightLittleDistal; b++)
                    if (human.Get(b) != null) { if (IsLeftFinger(b)) left++; else right++; }
                var h = new Dictionary<string, object>
                {
                    ["mapping_source"] = human.Source,
                    ["mapped_bones"] = human.Bones.Count,
                    ["required_missing"] = required,
                    ["optional_missing"] = optional,
                    ["fingers_mapped"] = new Dictionary<string, object> { ["left"] = left, ["right"] = right, ["max_per_hand"] = 15 },
                };
                var ls = human.Get(HumanBodyBones.LeftUpperArm);
                if (ls != null) h["shoulder_height_m"] = Round(ls.position.y - ctx.RootT.position.y);
                var head = human.Get(HumanBodyBones.Head);
                if (head != null) h["head_height_m"] = Round(head.position.y - ctx.RootT.position.y);
                d["humanoid"] = h;
            }
            catch (Exception e)
            {
                d["error"] = e.GetType().Name + ": " + e.Message;
            }
            return d;
        }

        /// <summary>
        /// Animator.GetBoneTransform for every human bone. When the Animator is not initialised (inactive object, prefab contents)
        /// it can return null for everything; then the mapping is rebuilt from Avatar.humanDescription bone names (read-only).
        /// </summary>
        static void ReadHumanBones(Ctx ctx, Animator a, HumanInfo human)
        {
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                Transform t = null;
                try { t = a.GetBoneTransform((HumanBodyBones)i); }
                catch (Exception) { /* not initialised */ }
                if (t != null) human.Bones[(HumanBodyBones)i] = t;
            }
            if (human.Get(HumanBodyBones.Hips) != null || a.avatar == null) return;

            human.Bones.Clear();
            human.Source = "avatar_description";
            var byName = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (var t in ctx.Scan.Transforms)
                if (t != null && !byName.ContainsKey(t.name)) byName[t.name] = t;
            var boneNames = HumanTrait.BoneName;
            foreach (var hb in a.avatar.humanDescription.human)
            {
                int index = Array.IndexOf(boneNames, hb.humanName);
                if (index < 0 || index >= (int)HumanBodyBones.LastBone || string.IsNullOrEmpty(hb.boneName)) continue;
                if (byName.TryGetValue(hb.boneName, out var t)) human.Bones[(HumanBodyBones)index] = t;
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // Descriptor
        // ---------------------------------------------------------------------------------------------------------------------

        static Dictionary<string, object> DescribeDescriptor(Ctx ctx)
        {
            var d = new Dictionary<string, object>();
            using (var so = new SerializedObject(ctx.Descriptor))
            {
                var view = so.FindProperty("ViewPosition");
                if (view != null && view.propertyType == SerializedPropertyType.Vector3) d["view_position"] = Vec(view.vector3Value);

                int lip = CctSerialized.GetInt(so.FindProperty("lipSync"));
                d["lip_sync"] = EnumName(LipSyncNames, lip);
                var visemeMesh = CctSerialized.GetObject(so.FindProperty("VisemeSkinnedMesh")) as SkinnedMeshRenderer;
                d["viseme_mesh"] = visemeMesh != null ? ctx.PathOf(visemeMesh) : null;
                if (lip == LipSyncVisemeBlendShape)
                    d["viseme_blendshapes_set"] = CctSerialized.Elements(so.FindProperty("VisemeBlendShapes"))
                        .Count(p => !string.IsNullOrEmpty(CctSerialized.GetString(p, "")));

                d["eye_look"] = CctSerialized.GetBool(so.FindProperty("enableEyeLook"));
                d["auto_footsteps"] = CctSerialized.GetBool(so.FindProperty("autoFootsteps"), true);
                d["auto_locomotion"] = CctSerialized.GetBool(so.FindProperty("autoLocomotion"), true);

                bool customize = CctSerialized.GetBool(so.FindProperty("customizeAnimationLayers"));
                d["customize_animation_layers"] = customize;
                ctx.Layers.Clear();
                ReadLayers(so.FindProperty("baseAnimationLayers"), "base", ctx.Layers);
                ReadLayers(so.FindProperty("specialAnimationLayers"), "special", ctx.Layers);
                d["layers"] = ctx.Layers.Select(l => (object)DescribeLayer(l)).ToList();

                d["custom_expressions"] = CctSerialized.GetBool(so.FindProperty("customExpressions"));
                var menuProp = so.FindProperty("expressionsMenu");
                var paramsProp = so.FindProperty("expressionParameters");
                d["expressions_menu"] = DescribeRef(menuProp);
                d["expression_parameters"] = DescribeRef(paramsProp);
            }
            return d;
        }

        static void ReadLayers(SerializedProperty array, string slot, List<LayerInfo> into)
        {
            int i = 0;
            foreach (var e in CctSerialized.Elements(array))
            {
                var controllerProp = CctSerialized.Child(e, "animatorController");
                int type = CctSerialized.GetInt(CctSerialized.Child(e, "type"));
                into.Add(new LayerInfo
                {
                    Slot = slot,
                    Index = i++,
                    Type = type,
                    TypeName = EnumName(AnimLayerTypeNames, type),
                    Controller = CctSerialized.GetObject(controllerProp) as RuntimeAnimatorController,
                    ControllerMissing = CctSerialized.IsMissingReference(controllerProp),
                    Mask = CctSerialized.GetObject(CctSerialized.Child(e, "mask")) as AvatarMask,
                    IsDefault = CctSerialized.GetBool(CctSerialized.Child(e, "isDefault")),
                    IsEnabled = CctSerialized.GetBool(CctSerialized.Child(e, "isEnabled")),
                });
            }
        }

        static Dictionary<string, object> DescribeLayer(LayerInfo l)
        {
            var d = new Dictionary<string, object>
            {
                ["slot"] = l.Slot,
                ["index"] = l.Index,
                ["type"] = l.TypeName,
                ["is_default"] = l.IsDefault,
                ["is_enabled"] = l.IsEnabled,
                ["controller"] = l.Controller != null ? AssetPathOrName(l.Controller) : null,
                ["mask"] = l.Mask != null ? AssetPathOrName(l.Mask) : null,
            };
            if (l.ControllerMissing) d["controller_missing"] = true;
            return d;
        }

        /// <summary>Asset path of an object reference, "(missing)" for a deleted asset, null when unset.</summary>
        static string DescribeRef(SerializedProperty p)
        {
            var o = CctSerialized.GetObject(p);
            if (o != null) return AssetPathOrName(o);
            return CctSerialized.IsMissingReference(p) ? "(missing)" : null;
        }
    }
}
