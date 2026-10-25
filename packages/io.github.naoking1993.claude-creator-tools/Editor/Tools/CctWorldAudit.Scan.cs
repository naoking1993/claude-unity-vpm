using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Naoking.ClaudeCreatorTools
{
    public static partial class CctWorldAudit
    {
        /// <summary>Component categories (one type may be in several).</summary>
        [Flags]
        enum K : ulong
        {
            None = 0,
            SceneDescriptor = 1UL << 0,
            PipelineManager = 1UL << 1,
            Udon = 1UL << 2,
            USharpProxy = 1UL << 3,
            Pickup = 1UL << 4,
            ObjectSync = 1UL << 5,
            ObjectPool = 1UL << 6,
            Station = 1UL << 7,
            Mirror = 1UL << 8,
            UnityVideo = 1UL << 9,
            AvProVideo = 1UL << 10,
            AvProScreen = 1UL << 11,
            AvProSpeaker = 1UL << 12,
            SpatialAudio = 1UL << 13,
            Portal = 1UL << 14,
            Pedestal = 1UL << 15,
            UiShape = 1UL << 16,
            PhysBone = 1UL << 17,
            PhysBoneCollider = 1UL << 18,
            Contact = 1UL << 19,
            EventHandler = 1UL << 20,
            Sdk2 = 1UL << 21,
            PpLayer = 1UL << 22,
            PpVolume = 1UL << 23,
            PpDebug = 1UL << 24,
            UiGraphic = 1UL << 25,
            UiText = 1UL << 26,
            UiSelectable = 1UL << 27,
            UiScrollRect = 1UL << 28,
            UiRaycaster = 1UL << 29,
            EventSystem = 1UL << 30,
            TmpAny = 1UL << 31,
            TextMesh = 1UL << 32,
            RawVideoPlayer = 1UL << 33,
            Cloth = 1UL << 34,
            DynamicBone = 1UL << 35,
            AvatarDescriptor = 1UL << 36,
            Onsp = 1UL << 37,
            EditorOnlyComponent = 1UL << 38,
            Renderer = 1UL << 39,
            Light = 1UL << 40,
            Camera = 1UL << 41,
            ReflectionProbe = 1UL << 42,
            LightProbeGroup = 1UL << 43,
            AudioSource = 1UL << 44,
            Collider = 1UL << 45,
            Canvas = 1UL << 46,
            ParticleRenderer = 1UL << 47,
            OcclusionArea = 1UL << 48,
            Terrain = 1UL << 49,
            PlayerObject = 1UL << 50,
            LegacyAnimation = 1UL << 51,
        }

        /// <summary>Cached per component type: categories and allowlist verdicts.</summary>
        sealed class TypeVerdict
        {
            public K Kinds;
            public string AllowedVia;      // allowlist entry that matched (the type itself or a base), null = not allowed
            public bool AllowedExact;      // the type itself is listed
            public bool PcOnly;            // matched only through a PC-only entry (post processing)
            public bool ClientAssembly;    // type's assembly is one VRChat ships (estimate, see IsClientAssembly)
            public bool USharp;            // UdonSharpBehaviour proxy: UdonSharp destroys it when building
        }

        static readonly Dictionary<Type, TypeVerdict> Verdicts = new Dictionary<Type, TypeVerdict>();

        // name in the type's base chain -> category
        static readonly Dictionary<string, K> KindByTypeName = new Dictionary<string, K>(StringComparer.Ordinal)
        {
            [TSceneDescriptorBase] = K.SceneDescriptor,
            [TPipelineManager] = K.PipelineManager,
            [TUdonBehaviour] = K.Udon,
            [TUSharpBehaviour] = K.USharpProxy,
            [TPickup] = K.Pickup,
            [TObjectSync] = K.ObjectSync,
            [TObjectPool] = K.ObjectPool,
            [TStation] = K.Station,
            [TMirror] = K.Mirror,
            [TUnityVideo] = K.UnityVideo,
            [TAvProVideo] = K.AvProVideo,
            [TAvProScreen] = K.AvProScreen,
            [TAvProSpeaker] = K.AvProSpeaker,
            [TSpatialAudioBase] = K.SpatialAudio,
            [TPortal] = K.Portal,
            [TPedestal] = K.Pedestal,
            [TUiShape] = K.UiShape,
            [TPhysBoneBase] = K.PhysBone,
            [TPhysBoneColliderBase] = K.PhysBoneCollider,
            [TContactBase] = K.Contact,
            [TEventHandler] = K.EventHandler,
            [TPpLayer] = K.PpLayer,
            [TPpVolume] = K.PpVolume,
            [TPpDebug] = K.PpDebug,
            [TUiGraphic] = K.UiGraphic,
            [TUiText] = K.UiText,
            [TUiSelectable] = K.UiSelectable,
            [TUiScrollRect] = K.UiScrollRect,
            [TUiGraphicRaycaster] = K.UiRaycaster,
            [TEventSystem] = K.EventSystem,
            [TTmpText] = K.TmpAny,
            [TTmpDropdown] = K.TmpAny,
            [TTmpInputField] = K.TmpAny,
            [TTextMesh] = K.TextMesh,
            [TVideoPlayer] = K.RawVideoPlayer,
            [TCloth] = K.Cloth,
            ["DynamicBone"] = K.DynamicBone,
            ["DynamicBoneCollider"] = K.DynamicBone,
            [CctVrcTypes.AvatarDescriptor] = K.AvatarDescriptor,
            ["VRC.SDKBase.VRC_AvatarDescriptor"] = K.AvatarDescriptor,
            [TOnsp] = K.Onsp,
            [TTerrain] = K.Terrain,
            [TPlayerObject] = K.PlayerObject,
            [TLegacyAnimation] = K.LegacyAnimation,
        };

        // ---- SDK3 world component allowlist ([WV] WorldValidation.cs 3.10.5: ComponentTypeWhiteListCommon L46-285 +
        // ComponentTypeWhiteListSdk3 L441-529; machine-checked against the file in research/world-requirements.md 6.1).
        // Matching mirrors ValidationUtils.WhitelistedTypes/AddDerivedClasses: a type is allowed when it or any base type is
        // listed. Names that do not resolve in the project are simply never matched (the SDK ignores them too).

        /// <summary>#if UNITY_STANDALONE entries: allowed on PC only (stripped on Android/iOS).</summary>
        static readonly string[] AllowlistPcOnly =
        {
            "UnityEngine.Rendering.PostProcessing.PostProcessDebug",
            "UnityEngine.Rendering.PostProcessing.PostProcessLayer",
            "UnityEngine.Rendering.PostProcessing.PostProcessVolume",
        };

        static readonly string[] AllowlistCommon =
        {
            "VRC.Core.PipelineManager", "UiInputField", "VRCProjectSettings", "DynamicBone", "DynamicBoneCollider",
            "TMPro.TMP_Dropdown", "TMPro.TMP_InputField", "TMPro.TMP_ScrollbarEventHandler", "TMPro.TMP_SelectionCaret",
            "TMPro.TMP_SpriteAnimator", "TMPro.TMP_SubMesh", "TMPro.TMP_SubMeshUI", "TMPro.TMP_Text", "TMPro.TextMeshPro",
            "TMPro.TextMeshProUGUI", "TMPro.TextContainer", "TMPro.TMP_Dropdown+DropdownItem",
            "UnityEngine.EventSystems.EventSystem", "UnityEngine.EventSystems.EventTrigger", "UnityEngine.EventSystems.UIBehaviour",
            "UnityEngine.EventSystems.BaseInput", "UnityEngine.EventSystems.BaseInputModule", "UnityEngine.EventSystems.PointerInputModule",
            "UnityEngine.EventSystems.StandaloneInputModule", "UnityEngine.EventSystems.TouchInputModule",
            "UnityEngine.EventSystems.BaseRaycaster", "UnityEngine.EventSystems.PhysicsRaycaster",
            "UnityEngine.UI.Button", "UnityEngine.UI.Dropdown", "UnityEngine.UI.Dropdown+DropdownItem", "UnityEngine.UI.Graphic",
            "UnityEngine.UI.GraphicRaycaster", "UnityEngine.UI.Image", "UnityEngine.UI.InputField", "UnityEngine.UI.Mask",
            "UnityEngine.UI.MaskableGraphic", "UnityEngine.UI.RawImage", "UnityEngine.UI.RectMask2D", "UnityEngine.UI.Scrollbar",
            "UnityEngine.UI.ScrollRect", "UnityEngine.UI.Selectable", "UnityEngine.UI.Slider", "UnityEngine.UI.Text",
            "UnityEngine.UI.Toggle", "UnityEngine.UI.ToggleGroup", "UnityEngine.UI.AspectRatioFitter", "UnityEngine.UI.CanvasScaler",
            "UnityEngine.UI.ContentSizeFitter", "UnityEngine.UI.GridLayoutGroup", "UnityEngine.UI.HorizontalLayoutGroup",
            "UnityEngine.UI.HorizontalOrVerticalLayoutGroup", "UnityEngine.UI.LayoutElement", "UnityEngine.UI.LayoutGroup",
            "UnityEngine.UI.VerticalLayoutGroup", "UnityEngine.UI.BaseMeshEffect", "UnityEngine.UI.Outline",
            "UnityEngine.UI.PositionAsUV1", "UnityEngine.UI.Shadow",
            "OVRLipSync", "OVRLipSyncContext", "OVRLipSyncContextBase", "OVRLipSyncContextCanned", "OVRLipSyncContextMorphTarget",
            "OVRLipSyncContextTextureFlip", "ONSPReflectionZone", "OculusSpatializerUnity", "ONSPAmbisonicsNative", "ONSPAudioSource",
            "RootMotion.FinalIK.BipedIK", "RootMotion.FinalIK.FingerRig", "RootMotion.FinalIK.Grounder", "RootMotion.FinalIK.GrounderBipedIK",
            "RootMotion.FinalIK.GrounderFBBIK", "RootMotion.FinalIK.GrounderIK", "RootMotion.FinalIK.GrounderQuadruped",
            "RootMotion.FinalIK.GrounderVRIK", "RootMotion.FinalIK.AimIK", "RootMotion.FinalIK.CCDIK", "RootMotion.FinalIK.FABRIK",
            "RootMotion.FinalIK.FABRIKRoot", "RootMotion.FinalIK.FullBodyBipedIK", "RootMotion.FinalIK.IK",
            "RootMotion.FinalIK.IKExecutionOrder", "RootMotion.FinalIK.LegIK", "RootMotion.FinalIK.LimbIK", "RootMotion.FinalIK.LookAtIK",
            "RootMotion.FinalIK.TrigonometricIK", "RootMotion.FinalIK.VRIK", "RootMotion.FinalIK.FBBIKArmBending",
            "RootMotion.FinalIK.FBBIKHeadEffector", "RootMotion.FinalIK.TwistRelaxer", "RootMotion.FinalIK.InteractionObject",
            "RootMotion.FinalIK.InteractionSystem", "RootMotion.FinalIK.InteractionTarget", "RootMotion.FinalIK.InteractionTrigger",
            "RootMotion.FinalIK.GenericPoser", "RootMotion.FinalIK.HandPoser", "RootMotion.FinalIK.Poser", "RootMotion.FinalIK.RagdollUtility",
            "RootMotion.FinalIK.RotationLimit", "RootMotion.FinalIK.RotationLimitAngle", "RootMotion.FinalIK.RotationLimitHinge",
            "RootMotion.FinalIK.RotationLimitPolygonal", "RootMotion.FinalIK.RotationLimitSpline", "RootMotion.FinalIK.AimPoser",
            "RootMotion.FinalIK.Amplifier", "RootMotion.FinalIK.BodyTilt", "RootMotion.FinalIK.HitReaction", "RootMotion.FinalIK.HitReactionVRIK",
            "RootMotion.FinalIK.Inertia", "RootMotion.FinalIK.OffsetModifier", "RootMotion.FinalIK.OffsetModifierVRIK",
            "RootMotion.FinalIK.OffsetPose", "RootMotion.FinalIK.Recoil", "RootMotion.FinalIK.ShoulderRotator",
            "RootMotion.Dynamics.AnimationBlocker", "RootMotion.Dynamics.BehaviourBase", "RootMotion.Dynamics.BehaviourFall",
            "RootMotion.Dynamics.BehaviourPuppet", "RootMotion.Dynamics.JointBreakBroadcaster", "RootMotion.Dynamics.MuscleCollisionBroadcaster",
            "RootMotion.Dynamics.PressureSensor", "RootMotion.Dynamics.Prop", "RootMotion.Dynamics.PropRoot", "RootMotion.Dynamics.PuppetMaster",
            "RootMotion.Dynamics.PuppetMasterSettings", "RootMotion.Dynamics.BipedRagdollCreator", "RootMotion.Dynamics.RagdollCreator",
            "RootMotion.Dynamics.RagdollEditor", "RootMotion.SolverManager", "RootMotion.TriggerEventBroadcaster",
            "UnityEngine.WindZone", "UnityEngine.Tilemaps.Tilemap", "UnityEngine.Tilemaps.TilemapRenderer", "UnityEngine.Terrain",
            "UnityEngine.Tree", "UnityEngine.SpriteMask", "UnityEngine.Grid", "UnityEngine.GridLayout", "UnityEngine.AudioSource",
            "UnityEngine.AudioReverbZone", "UnityEngine.AudioLowPassFilter", "UnityEngine.AudioHighPassFilter",
            "UnityEngine.AudioDistortionFilter", "UnityEngine.AudioEchoFilter", "UnityEngine.AudioChorusFilter",
            "UnityEngine.AudioReverbFilter", "UnityEngine.AudioListener", "UnityEngine.Playables.PlayableDirector",
            "UnityEngine.TerrainCollider", "UnityEngine.Canvas", "UnityEngine.CanvasGroup", "UnityEngine.CanvasRenderer",
            "UnityEngine.TextMesh", "UnityEngine.Animator", "UnityEngine.AI.NavMeshAgent", "UnityEngine.AI.NavMeshObstacle",
            "UnityEngine.AI.OffMeshLink", // Unity 2022 branch of the #if UNITY_6000_0_OR_NEWER switch (L212-216)
            "UnityEngine.Cloth", "UnityEngine.WheelCollider", "UnityEngine.Rigidbody", "UnityEngine.Joint", "UnityEngine.HingeJoint",
            "UnityEngine.SpringJoint", "UnityEngine.FixedJoint", "UnityEngine.CharacterJoint", "UnityEngine.ConfigurableJoint",
            "UnityEngine.ConstantForce", "UnityEngine.Collider", "UnityEngine.BoxCollider", "UnityEngine.SphereCollider",
            "UnityEngine.MeshCollider", "UnityEngine.CapsuleCollider", "UnityEngine.CharacterController", "UnityEngine.ParticleSystem",
            "UnityEngine.ParticleSystemRenderer", "UnityEngine.BillboardRenderer", "UnityEngine.Camera", "UnityEngine.FlareLayer",
            "UnityEngine.SkinnedMeshRenderer", "UnityEngine.Renderer", "UnityEngine.TrailRenderer", "UnityEngine.LineRenderer",
            "UnityEngine.GUIElement", "UnityEngine.GUILayer", "UnityEngine.Light", "UnityEngine.LightProbeGroup",
            "UnityEngine.LightProbeProxyVolume", "UnityEngine.LODGroup", "UnityEngine.ReflectionProbe", "UnityEngine.SpriteRenderer",
            "UnityEngine.Transform", "UnityEngine.RectTransform", "UnityEngine.Rendering.SortingGroup", "UnityEngine.Projector",
            "UnityEngine.OcclusionPortal", "UnityEngine.OcclusionArea", "UnityEngine.LensFlare", "UnityEngine.Skybox",
            "UnityEngine.MeshFilter", "UnityEngine.Halo", "UnityEngine.MeshRenderer", "UnityEngine.Collider2D", "UnityEngine.Rigidbody2D",
            "UnityEngine.CompositeCollider2D", "UnityEngine.ConstantForce2D", "UnityEngine.AreaEffector2D", "UnityEngine.CapsuleCollider2D",
            "UnityEngine.DistanceJoint2D", "UnityEngine.EdgeCollider2D", "UnityEngine.Effector2D", "UnityEngine.BoxCollider2D",
            "UnityEngine.CircleCollider2D", "UnityEngine.FixedJoint2D", "UnityEngine.HingeJoint2D", "UnityEngine.FrictionJoint2D",
            "UnityEngine.PlatformEffector2D", "UnityEngine.PointEffector2D", "UnityEngine.PolygonCollider2D", "UnityEngine.SliderJoint2D",
            "UnityEngine.SurfaceEffector2D", "UnityEngine.RelativeJoint2D", "UnityEngine.TargetJoint2D", "UnityEngine.WheelJoint2D",
            "UnityEngine.Joint2D", "UnityEngine.ParticleSystemForceField",
        };

        static readonly string[] AllowlistSdk3 =
        {
            "VRC.SDK3.VRCDestructibleStandard", "VRC.SDK3.Components.VRCVisualDamage", "VRC.SDK3.Components.VRCAvatarPedestal",
            "VRC.SDK3.Components.VRCPickup", "VRC.SDK3.Components.VRCPortalMarker", "VRC.SDK3.Components.VRCSpatialAudioSource",
            "VRC.SDK3.Components.VRCMirrorReflection", "VRC.SDK3.Components.VRCSceneDescriptor", "VRC.SDK3.Components.VRCStation",
            "VRC.SDK3.Components.VRCUiShape", "VRC.SDK3.Components.VRCObjectSync", "VRC.SDK3.Components.VRCObjectPool",
            "VRC.SDK3.Components.VRCInputFieldKeyboardOverride",
            // #if VRC_ENABLE_PLAYER_PERSISTENCE (|| VRC_ENABLE_INSTANCE_PERSISTENCE): EnvConfig.SetActiveSDKDefines defines it
            // whenever ClientSim's ClientSimPlayerDataStorage exists, i.e. in every Worlds SDK project (research 6.1).
            "VRC.SDK3.Components.VRCPlayerObject", "VRC.SDK3.Components.VRCEnablePersistence",
            "VRC.SDK3.Video.Components.VRCUnityVideoPlayer", "VRC.SDK3.Video.Components.AVPro.VRCAVProVideoPlayer",
            "VRC.SDK3.Video.Components.AVPro.VRCAVProVideoScreen", "VRC.SDK3.Video.Components.AVPro.VRCAVProVideoSpeaker",
            "VRC.SDK3.Midi.VRCMidiListener", "VRC.SDK3.Midi.VRCMidiPlayer", "VRC.SDK3.Components.VRCCameraDollyAnimation",
            "VRC.SDK3.Components.VRCCameraDollyPath", "VRC.SDK3.Components.VRCCameraDollyPathPoint", "VRC.Udon.UdonBehaviour",
            "VRC.Udon.AbstractUdonBehaviourEventProxy", "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone",
            "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider", "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneRoot",
            "VRC.SDK3.Dynamics.Constraint.Components.VRCAimConstraint", "VRC.SDK3.Dynamics.Constraint.Components.VRCLookAtConstraint",
            "VRC.SDK3.Dynamics.Constraint.Components.VRCParentConstraint", "VRC.SDK3.Dynamics.Constraint.Components.VRCPositionConstraint",
            "VRC.SDK3.Dynamics.Constraint.Components.VRCRotationConstraint", "VRC.SDK3.Dynamics.Constraint.Components.VRCScaleConstraint",
            "VRC.SDK3.Dynamics.Contact.Components.VRCContactSender", "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver",
            "UnityEngine.Animations.AimConstraint", "UnityEngine.Animations.LookAtConstraint", "UnityEngine.Animations.ParentConstraint",
            "UnityEngine.Animations.PositionConstraint", "UnityEngine.Animations.RotationConstraint", "UnityEngine.Animations.ScaleConstraint",
            "UnityEngine.ParticleSystemForceField", "Unity.AI.Navigation.NavMeshSurface", "Unity.AI.Navigation.NavMeshLink",
            "Unity.AI.Navigation.NavMeshModifier", "Unity.AI.Navigation.NavMeshModifierVolume",
            "Cinemachine.Cinemachine3rdPersonAim", "Cinemachine.CinemachineBlendListCamera", "Cinemachine.CinemachineBrain",
            "Cinemachine.CinemachineCameraOffset", "Cinemachine.CinemachineClearShot", "Cinemachine.CinemachineCollider",
            "Cinemachine.CinemachineConfiner", "Cinemachine.CinemachineDollyCart", "Cinemachine.CinemachineExternalCamera",
            "Cinemachine.CinemachineFollowZoom", "Cinemachine.CinemachineFreeLook", "Cinemachine.CinemachineMixingCamera",
            "Cinemachine.CinemachinePath", "Cinemachine.CinemachinePipeline", "Cinemachine.CinemachinePixelPerfect",
            "Cinemachine.CinemachineRecomposer", "Cinemachine.CinemachineSmoothPath", "Cinemachine.CinemachineStateDrivenCamera",
            "Cinemachine.CinemachineStoryboard", "Cinemachine.CinemachineTargetGroup", "Cinemachine.CinemachineVirtualCamera",
            "Cinemachine.Cinemachine3rdPersonFollow", "Cinemachine.CinemachineBasicMultiChannelPerlin", "Cinemachine.CinemachineComposer",
            "Cinemachine.CinemachineFramingTransposer", "Cinemachine.CinemachineGroupComposer", "Cinemachine.CinemachineHardLockToTarget",
            "Cinemachine.CinemachineHardLookAt", "Cinemachine.CinemachineOrbitalTransposer", "Cinemachine.CinemachinePOV",
            "Cinemachine.CinemachineSameAsFollowTarget", "Cinemachine.CinemachineTrackedDolly", "Cinemachine.CinemachineTransposer",
            "Cinemachine.CinemachineCore",
        };

        static readonly HashSet<string> AllowedNames = new HashSet<string>(AllowlistCommon.Concat(AllowlistSdk3), StringComparer.Ordinal);
        static readonly HashSet<string> AllowedPcOnlyNames = new HashSet<string>(AllowlistPcOnly, StringComparer.Ordinal);

        /// <summary>
        /// Assemblies the VRChat client ships (Unity modules/packages, VRChat SDK, Cinemachine). A component that is only
        /// allowed because it derives from an allowed type but lives in another assembly (Assembly-CSharp, a user asmdef)
        /// is not in the uploaded bundle (VRChat does not load custom C#), so it is reported as NOT_WHITELISTED_DERIVED.
        /// This is an estimate: the SDK itself accepts any derived type.
        /// </summary>
        static bool IsClientAssembly(string assemblyName) =>
            assemblyName != null && (assemblyName.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                                     assemblyName.StartsWith("Unity.", StringComparison.Ordinal) ||
                                     assemblyName.StartsWith("VRC", StringComparison.Ordinal) ||
                                     assemblyName.StartsWith("Cinemachine", StringComparison.Ordinal));

        static TypeVerdict Classify(Type type)
        {
            if (Verdicts.TryGetValue(type, out var v)) return v;
            v = new TypeVerdict();
            bool first = true;
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                var name = t.FullName ?? t.Name;
                if (KindByTypeName.TryGetValue(name, out var k)) v.Kinds |= k;
                if (name == TUSharpBehaviour) v.USharp = true;
                if (v.AllowedVia == null)
                {
                    if (AllowedNames.Contains(name)) { v.AllowedVia = name; v.AllowedExact = first; }
                    else if (AllowedPcOnlyNames.Contains(name)) { v.AllowedVia = name; v.AllowedExact = first; v.PcOnly = true; }
                }
                first = false;
            }
            if ((type.Namespace ?? "").StartsWith("VRCSDK2", StringComparison.Ordinal)) v.Kinds |= K.Sdk2;
            try
            {
                foreach (var i in type.GetInterfaces())
                    if (i.FullName == TEditorOnly) { v.Kinds |= K.EditorOnlyComponent; break; }
            }
            catch (Exception) { /* broken interface metadata: treat as not editor-only */ }

            if (typeof(Renderer).IsAssignableFrom(type)) v.Kinds |= K.Renderer;
            if (typeof(ParticleSystemRenderer).IsAssignableFrom(type)) v.Kinds |= K.ParticleRenderer;
            if (typeof(Light).IsAssignableFrom(type)) v.Kinds |= K.Light;
            if (typeof(Camera).IsAssignableFrom(type)) v.Kinds |= K.Camera;
            if (typeof(ReflectionProbe).IsAssignableFrom(type)) v.Kinds |= K.ReflectionProbe;
            if (typeof(LightProbeGroup).IsAssignableFrom(type)) v.Kinds |= K.LightProbeGroup;
            if (typeof(AudioSource).IsAssignableFrom(type)) v.Kinds |= K.AudioSource;
            if (typeof(Collider).IsAssignableFrom(type)) v.Kinds |= K.Collider;
            if (typeof(Canvas).IsAssignableFrom(type)) v.Kinds |= K.Canvas;
            if (typeof(OcclusionArea).IsAssignableFrom(type)) v.Kinds |= K.OcclusionArea;

            string asm = null;
            try { asm = type.Assembly.GetName().Name; }
            catch (Exception) { /* dynamic assembly */ }
            v.ClientAssembly = IsClientAssembly(asm);
            Verdicts[type] = v;
            return v;
        }

        /// <summary>
        /// One pass over every GameObject of every loaded scene (inactive included): categories, missing scripts,
        /// EditorOnly tagging. Objects/components with hideFlags == NotEditable/HideAndDontSave are skipped like the SDK does.
        /// </summary>
        static void Scan(Ctx ctx)
        {
            var comps = new List<Component>(16);
            for (int si = 0; si < ctx.Loaded.Count; si++)
            {
                var scene = ctx.Loaded[si];
                bool inScope = ctx.ScopeIndices.Contains(si);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root == null) continue;
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (t == null) continue;
                        var go = t.gameObject;
                        if (IsSdkExcluded(go.hideFlags)) { ctx.HiddenSkipped++; continue; }

                        bool editorOnly = go.CompareTag("EditorOnly");
                        if (!editorOnly && t.parent != null)
                            editorOnly = ctx.ObjByGo.TryGetValue(t.parent.gameObject, out var parent)
                                ? parent.EditorOnly
                                : AncestorTaggedEditorOnly(t.parent);
                        var o = new Obj { Go = go, Scene = si, InScope = inScope, EditorOnly = editorOnly, Active = go.activeInHierarchy };
                        ctx.Objects.Add(o);
                        ctx.ObjByGo[go] = o;

                        // Count missing scripts with Unity's own counter (never by filtering nulls first).
                        int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                        if (missing > 0) ctx.MissingScripts.Add(new KeyValuePair<Obj, int>(o, missing));

                        go.GetComponents(comps);
                        foreach (var c in comps)
                        {
                            if (c == null || IsSdkExcluded(c.hideFlags)) continue;
                            var type = c.GetType();
                            var verdict = Classify(type);
                            var item = new Item(c, o);
                            ctx.Components.Add(item);
                            ctx.Add(verdict.Kinds, item);
                            if (item.Audited)
                            {
                                ctx.AuditedTypeCounts.TryGetValue(type, out var n);
                                ctx.AuditedTypeCounts[type] = n + 1;
                            }
                        }
                    }
                }
            }
        }

        static bool AncestorTaggedEditorOnly(Transform t)
        {
            for (var cur = t; cur != null; cur = cur.parent)
                if (cur.gameObject.CompareTag("EditorOnly")) return true;
            return false;
        }

        /// <summary>The Obj of a component found outside the scan (e.g. GetComponent results); null when not scanned.</summary>
        static Obj ObjOf(Ctx ctx, Component c) =>
            c != null && ctx.ObjByGo.TryGetValue(c.gameObject, out var o) ? o : null;

        static bool HasComponentOfKind(Ctx ctx, GameObject go, K kind)
        {
            if (go == null) return false;
            var list = new List<Component>(8);
            go.GetComponents(list);
            foreach (var c in list)
                if (c != null && (Classify(c.GetType()).Kinds & kind) != 0) return true;
            return false;
        }

        static List<Component> ComponentsOfKind(GameObject go, K kind)
        {
            var result = new List<Component>();
            if (go == null) return result;
            var list = new List<Component>(8);
            go.GetComponents(list);
            foreach (var c in list)
                if (c != null && (Classify(c.GetType()).Kinds & kind) != 0) result.Add(c);
            return result;
        }
    }
}
