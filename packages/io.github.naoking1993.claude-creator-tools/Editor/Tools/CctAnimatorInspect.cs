using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// cct_animator_inspect: compact dump + VRChat-specific lint of AnimatorControllers. Read-only.
    ///
    /// Inputs: an AnimatorController / AnimatorOverrideController asset (controller = path, GUID or unique asset name), or
    /// an avatar (target) whose VRCAvatarDescriptor playable layers (baseAnimationLayers / specialAnimationLayers) select
    /// the controller(s) for layer = fx (default) | gesture | action | base | additive | sitting | tpose | ikpose | all.
    /// A layer marked isDefault (or customizeAnimationLayers off) uses VRChat's built-in controller; the tool then inspects
    /// the SDK sample controller of that type (Packages/com.vrchat.avatars/Samples/AV3 Demo Assets/Animation/Controllers,
    /// the same stand-in NDMF uses: ndmf VRChatPlatformAnimatorBindings.GetFallbackController) and reports SDK_DEFAULT_LAYER;
    /// issues found in such a controller are downgraded to info. With include_ma (default true when an avatar is given)
    /// Modular Avatar Merge Animator components under the avatar are listed (fields from MA source
    /// Runtime/ModularAvatarMergeAnimator.cs, research/ma-ndmf-aao.md §2.3) and the ones merging into the requested
    /// layer type (all of them for layer=all) are inspected too. Without avatar descriptor, a target with an Animator is
    /// inspected through Animator.runtimeAnimatorController (paths relative to that object), and a part (outfit/gimmick
    /// prefab) with MA Merge Animators is inspected with estimated path roots.
    ///
    /// Binding root rule (check_bindings): curve paths are resolved with Transform.Find from
    ///   - descriptor layers / controller given with target: the avatar root (the object with VRCAvatarDescriptor);
    ///   - MA Merge Animator, pathMode Absolute (1): the avatar root;
    ///   - MA Merge Animator, pathMode Relative (0): relativePathRoot resolved like MA's AvatarObjectReference.Get
    ///     (empty referencePath -> none; targetObject under the avatar; "$$$AVATAR_ROOT$$$" -> avatar root;
    ///     else avatarRoot.Find(referencePath)), falling back to the component's own GameObject (MA
    ///     MergeAnimatorProcessor GetMotionBasePathCallback). Humanoid muscle / root curves (binding type Animator) are not
    ///     path-checked (MA docs: humanoid animations ignore the relative path logic);
    ///   - Animator target: the Animator's GameObject.
    /// Property checks are limited to "component type exists on the object" and "blendShape.X exists on the
    /// SkinnedMeshRenderer mesh".
    ///
    /// Write Defaults rule (WD_MIXED): the SDK 3.10.5 builder rule (VRCSdkControlPanelAvatarBuilder
    /// ScanAvatarForWriteDefaultsMixture): per controller, skip layers with Additive blending and states whose motion is a
    /// Direct blend tree, then any mix of on/off states is "mixed"; docs avatars/index.md:143-167 (keep WD consistent;
    /// None/empty motion with WD off behaves as WD on; WD on recommended for additive layers and direct blend trees).
    /// Synced layers share their source layer's states and are not counted twice. For MA controllers the build-time WD
    /// changes of MergeAnimatorProcessor (forced on for additive / single-state direct-blend-tree layers; others set to
    /// the avatar's uniform value when matchAvatarWriteDefaults) are forecast per layer (ma_wd) and soften the issues.
    ///
    /// Parameter type conversion (PARAM_TYPE_CONVERTED / BUILTIN_PARAM_TYPE_CONVERTED): docs
    /// avatars/animator-parameters/index.md:205-220 "Mismatched Parameter Type Conversion": int->float direct,
    /// int->bool 0=false else true, float->int Mathf.Round, float->bool 0.0=false else true, bool->int/float 1/0.
    /// Lossless conversions (bool->int/float, int->float) are info, lossy ones warning. Driver Copy uses different rules
    /// (state-behaviors.md:158-163: to int rounds down).
    ///
    /// Issue codes (stable):
    /// | code                              | severity      | rule |
    /// |-----------------------------------|---------------|------|
    /// | TARGET_ADJUSTED                   | info          | selection/target moved to the avatar root, or the Project-selected controller was used |
    /// | PLAY_MODE                         | info          | Play mode: the scene avatar may already be NDMF-processed (merged controllers, MA components gone) |
    /// | SDK_DEFAULT_LAYER                 | info          | playable layer uses VRChat's default; the SDK sample controller of that type is inspected (estimate) |
    /// | SDK_DEFAULT_CONTROLLER_NOT_FOUND  | warning       | the SDK sample controller for a default layer is not in the project (com.vrchat.avatars missing?) |
    /// | LAYER_CONTROLLER_MISSING          | warning       | customised playable layer (not default) without controller, or the reference is missing |
    /// | OVERRIDE_CONTROLLER_IN_PLAYABLE_LAYER | warning   | descriptor layer uses an AnimatorOverrideController (docs playable-layers.md:40-43: only Animation Controllers) |
    /// | CONTROLLER_REUSED                 | warning       | the same controller in several playable layers (docs playable-layers.md:39) |
    /// | CONTROLLER_UNREADABLE             | warning       | override controller without base AnimatorController, or not an AnimatorController |
    /// | LAYER_NO_STATE_MACHINE            | warning       | layer without state machine (MA skips such layers: MA-0013) |
    /// | PARAM_DUPLICATE                   | warning       | the same parameter name twice in one controller |
    /// | PARAM_UNDEFINED                   | warning/info  | condition / blend tree / speed / time / mirror / cycle / driver / play-audio parameter not in the controller (info when only a driver destination defined in Expression Parameters, or defined by another controller merged into the same playable layer) |
    /// | PARAM_TYPE_MISMATCH               | warning       | condition mode not valid for the parameter type (Float: Greater/Less; Int: Greater/Less/Equals/NotEqual; Bool: If/IfNot; Trigger: If), blend tree / speed / time / cycle parameter not Float, mirror parameter not Bool |
    /// | PARAM_TYPE_CONVERTED              | warning/info  | Expression Parameter type differs from the animator parameter type (docs conversion table, see above) |
    /// | BUILTIN_PARAM_TYPE_CONVERTED      | warning/info  | animator parameter named like a VRChat built-in with a different type (same table) |
    /// | PARAM_TRIGGER                     | info          | Trigger parameters (docs animator-parameters/index.md:222-224: not recommended, can desync) |
    /// | EXPRESSION_PARAM_NOT_IN_ANIMATOR  | info          | layer=all: Expression Parameter not declared in any inspected controller (MA may still add it) |
    /// | WD_MIXED                          | warning/info  | a layer, or a whole controller (SDK rule), mixes Write Defaults on/off (info when MA will normalise it) |
    /// | WD_MIXED_ACROSS_CONTROLLERS       | info          | inspected controllers are each uniform but differ from each other (docs: consistent across the avatar) |
    /// | EMPTY_STATE_WD_OFF                | warning/info  | WD-off state with no motion / empty clip / empty blend-tree child (SDK warning; docs: behaves as WD on) |
    /// | DIRECT_BLENDTREE_WD_OFF           | warning/info  | WD-off state whose motion is or contains a Direct blend tree (docs: community recommends WD on) |
    /// | ADDITIVE_LAYER_WD_OFF             | warning/info  | WD-off states in an Additive-blending layer (docs: community recommends WD on) |
    /// | MISSING_MOTION                    | warning       | state / blend tree child motion reference points at a deleted asset |
    /// | MISSING_BEHAVIOUR                 | warning       | state machine behaviour with a missing script |
    /// | TRANSITION_NEVER_FIRES            | warning       | non-muted state/AnyState transition with no conditions and no exit time (Unity ignores it) |
    /// | TRANSITION_NO_DESTINATION         | warning       | transition with no destination state / state machine and not an exit |
    /// | ANYSTATE_SELF_LOOP                | warning       | AnyState transition with Can Transition To Self and no Trigger condition: re-enters every frame while true |
    /// | UNREACHABLE_STATE                 | info          | state not reachable from Entry / default state / AnyState / other states (muted/never-firing transitions ignored) |
    /// | LAYER_WEIGHT_ZERO                 | info          | layer index > 0 with weight 0 and no VRCAnimatorLayerControl raising it |
    /// | LAYER_EMPTY                       | info          | layer whose state machine has no states |
    /// | ACTION_WEIGHT_NEVER_RAISED        | info          | layer=all: Action controller has states but no VRCPlayableLayerControl raises Action (docs playable-layers.md:91) |
    /// | DRIVER_BUILTIN_DESTINATION        | warning       | parameter driver writes a VRChat built-in parameter (docs state-behaviors.md:122: cannot be driven) |
    /// | DRIVER_COPY_BUILTIN_SOURCE        | warning       | driver Copy from a built-in parameter (docs state-behaviors.md:153: does not work as a source) |
    /// | DRIVER_ADD_ON_BOOL                | warning       | driver Add on a Bool parameter (docs state-behaviors.md:124: Add works for float/int only) |
    /// | DRIVER_NON_LOCAL                  | info          | Add / Random without Local Only (docs state-behaviors.md:134,141: may differ on remote; use a synced destination + local only) |
    /// | DRIVER_VALUE_OUT_OF_RANGE         | info          | Set value outside the synced range of the destination Expression Parameter (docs state-behaviors.md:118: clamped) |
    /// | DRIVER_COPY_TYPE_CONVERSION       | info          | Copy between different types (docs state-behaviors.md:158-163 rules) |
    /// | LAYER_CONTROL_BASE_LAYER          | warning       | VRCAnimatorLayerControl targets layer 0 (docs state-behaviors.md:36: base layer weight is always 1) |
    /// | LAYER_CONTROL_INDEX_OUT_OF_RANGE  | warning       | VRCAnimatorLayerControl layer index beyond the controller's layers (same playable layer) |
    /// | PLAY_AUDIO_SOURCE_MISSING         | warning       | VRCAnimatorPlayAudio Source Path does not resolve to an AudioSource (docs: the behaviour is disabled) |
    /// | PLAY_AUDIO_PARAM_NOT_IN_EXPRESSIONS | warning     | Playback Order = Parameter but the parameter is not an Expression Parameter (docs state-behaviors.md:203) |
    /// | PLAY_AUDIO_ON_STATE_MACHINE       | info          | VRCAnimatorPlayAudio on a sub-state machine (docs state-behaviors.md:198: not recommended) |
    /// | BINDING_PATH_MISSING              | warning/info  | clip curve path does not exist under the binding root (info for SDK defaults / estimated roots) |
    /// | BINDING_COMPONENT_MISSING         | warning/info  | path exists but the animated component type is not on it |
    /// | BINDING_BLENDSHAPE_MISSING        | warning/info  | blendShape.X not on the SkinnedMeshRenderer's mesh |
    /// | NON_TRANSFORM_IN_NON_FX           | warning       | clip in Base/Additive/Gesture/Action animates non-Transform properties (docs playable-layers.md:46-50,96) |
    /// | FX_TRANSFORM_ANIMATION            | info          | FX clips animate Transforms (docs playable-layers.md:100-102: not recommended; mind the masks) |
    /// | MA_MERGE_ANIMATOR_NO_ANIMATOR     | warning       | MA Merge Animator without controller (MA skips it silently) |
    /// | MA_MULTIPLE_REPLACE               | error         | several MA Merge Animators with Replace for one layer type (MA build error) |
    /// | MA_REPLACE_MODE                   | info          | MA Replace: the descriptor controller of that layer type is discarded at build |
    /// | MA_RELATIVE_ROOT_UNRESOLVED       | warning       | relativePathRoot set but unresolvable: MA falls back to the component's GameObject |
    /// | MA_EDITOR_ONLY                    | info          | MA Merge Animator under an EditorOnly object: removed before merging |
    /// | MA_PATH_MODE_MISMATCH             | warning       | Relative/Absolute path mode looks wrong: missing paths resolve with the other mode's root |
    /// | MA_PARAM_TYPE_COERCED             | warning       | same parameter name with different types in controllers merged into one layer: MA makes it Float |
    /// | MA_WD_FORCED_ON                   | info          | MA forces WD on in this layer at build (additive / single-state direct blend tree layer) |
    /// | MA_WD_NOT_MATCHED                 | warning/info  | matchAvatarWriteDefaults off and the controller's WD differs from the avatar's, or on but the avatar is mixed |
    /// | MA_LAYER_CONTROL_UNSUPPORTED      | warning       | VRCAnimatorLayerControl in an MA controller targeting another playable (MA docs merge-animator.md:73-77) |
    /// | FIND_NO_MATCH                     | info          | find_parameter / find_binding matched nothing |
    /// | TRUNCATED                         | info          | graph output truncated at max_states, or the time budget stopped the scan early |
    /// | SCENE_DIRTIED                     | warning       | a loaded scene became dirty during the call (should not happen; reported, not reverted) |
    /// | INSPECT_SECTION_FAILED            | warning       | one controller / section threw; it is omitted and the rest stays valid |
    /// Per code at most <see cref="MaxIssuesPerCode"/> issues are listed; one extra issue with the same code counts the rest.
    /// Failure codes (success=false): CONTROLLER_NOT_FOUND, AMBIGUOUS_CONTROLLER, TARGET_NOT_FOUND, AMBIGUOUS_TARGET,
    /// NO_TARGET (nothing given/selected and not exactly one avatar loaded), NO_CONTROLLER (target has no descriptor,
    /// Animator controller or MA Merge Animator), LAYER_NOT_FOUND (requested playable layer absent and no MA merges into it).
    /// </summary>
    public static partial class CctAnimatorInspect
    {
        public const string ToolName = "cct_animator_inspect";

        const int MaxIssuesPerCode = 25;
        const int SummaryParamCap = 120;
        const int SummaryLayerCap = 80;
        const int PreviewFullStates = 30;
        const int PreviewLayerCap = 40;
        const int PreviewStatesPerLayer = 12;
        const int MaxBlendTreeDepth = 10;
        const int MaxChildrenShown = 64;
        const int MaxMotionNodesPerState = 4000;
        const int MaxClipsChecked = 5000;

        // ---- VRChat SDK 3.10.5 types read via CctSerialized (verified in tests/vrc-fields/CctAnimatorInspect.txt) ----
        const string TParameterDriver = "VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver";
        const string TParameterDriverBase = "VRC.SDKBase.VRC_AvatarParameterDriver";
        const string TTrackingControl = "VRC.SDK3.Avatars.Components.VRCAnimatorTrackingControl";
        const string TTrackingControlBase = "VRC.SDKBase.VRC_AnimatorTrackingControl";
        const string TLayerControl = "VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl";
        const string TLayerControlBase = "VRC.SDKBase.VRC_AnimatorLayerControl";
        const string TPlayableLayerControl = "VRC.SDK3.Avatars.Components.VRCPlayableLayerControl";
        const string TPlayableLayerControlBase = "VRC.SDKBase.VRC_PlayableLayerControl";
        const string TLocomotionControl = "VRC.SDK3.Avatars.Components.VRCAnimatorLocomotionControl";
        const string TLocomotionControlBase = "VRC.SDKBase.VRC_AnimatorLocomotionControl";
        const string TTemporaryPoseSpace = "VRC.SDK3.Avatars.Components.VRCAnimatorTemporaryPoseSpace";
        const string TTemporaryPoseSpaceBase = "VRC.SDKBase.VRC_AnimatorTemporaryPoseSpace";
        const string TPlayAudio = "VRC.SDK3.Avatars.Components.VRCAnimatorPlayAudio";
        const string TPlayAudioBase = "VRC.SDKBase.VRC_AnimatorPlayAudio";

        // ---- Modular Avatar (field names from MA source Runtime/ModularAvatarMergeAnimator.cs / ModularAvatarMergeBlendTree.cs) ----
        const string TMaMergeAnimator = "nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator";
        const string TMaMergeBlendTree = "nadena.dev.modular_avatar.core.ModularAvatarMergeBlendTree";
        const string MaAvatarRootToken = "$$$AVATAR_ROOT$$$"; // AvatarObjectReference.AVATAR_ROOT

        // VRCAvatarDescriptor+AnimLayerType (FieldCheck enum): Base=0, Deprecated0=1, Additive=2, Gesture=3, Action=4, FX=5,
        // Sitting=6, TPose=7, IKPose=8.
        const int LtBase = 0, LtAdditive = 2, LtGesture = 3, LtAction = 4, LtFx = 5, LtSitting = 6, LtTPose = 7, LtIKPose = 8;

        static readonly Dictionary<string, int> LayerArgToType = new Dictionary<string, int>
        {
            ["base"] = LtBase, ["additive"] = LtAdditive, ["gesture"] = LtGesture, ["action"] = LtAction, ["fx"] = LtFx,
            ["sitting"] = LtSitting, ["tpose"] = LtTPose, ["ikpose"] = LtIKPose,
        };

        static string LayerTypeName(int t)
        {
            switch (t)
            {
                case 0: return "Base";
                case 1: return "Deprecated0";
                case 2: return "Additive";
                case 3: return "Gesture";
                case 4: return "Action";
                case 5: return "FX";
                case 6: return "Sitting";
                case 7: return "TPose";
                case 8: return "IKPose";
                default: return t < 0 ? null : t.ToString(CultureInfo.InvariantCulture);
            }
        }

        // VRC_AnimatorLayerControl+BlendableLayer / VRC_PlayableLayerControl+BlendableLayer (FieldCheck enum):
        // Action=0, FX=1, Gesture=2, Additive=3.
        static string BlendableName(int v)
        {
            switch (v)
            {
                case 0: return "Action";
                case 1: return "FX";
                case 2: return "Gesture";
                case 3: return "Additive";
                default: return v.ToString(CultureInfo.InvariantCulture);
            }
        }

        static int BlendableToLayerType(int v)
        {
            switch (v)
            {
                case 0: return LtAction;
                case 1: return LtFx;
                case 2: return LtGesture;
                case 3: return LtAdditive;
                default: return -1;
            }
        }

        /// <summary>
        /// VRChat built-in avatar parameters and their types (docs avatars/animator-parameters/index.md:32-72, snapshot
        /// 2026-10-20). Built-ins are read-only (docs :28) and cannot be driven (state-behaviors.md:122).
        /// </summary>
        static readonly Dictionary<string, string> BuiltinParams = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["IsLocal"] = "Bool", ["PreviewMode"] = "Int", ["Viseme"] = "Int", ["Voice"] = "Float",
            ["GestureLeft"] = "Int", ["GestureRight"] = "Int", ["GestureLeftWeight"] = "Float", ["GestureRightWeight"] = "Float",
            ["AngularY"] = "Float", ["VelocityX"] = "Float", ["VelocityY"] = "Float", ["VelocityZ"] = "Float",
            ["VelocityMagnitude"] = "Float", ["Upright"] = "Float", ["Grounded"] = "Bool", ["Seated"] = "Bool", ["AFK"] = "Bool",
            ["TrackingType"] = "Int", ["VRMode"] = "Int", ["MuteSelf"] = "Bool", ["InStation"] = "Bool", ["Earmuffs"] = "Bool",
            ["IsOnFriendsList"] = "Bool", ["AvatarVersion"] = "Int", ["IsAnimatorEnabled"] = "Bool",
            ["ScaleModified"] = "Bool", ["ScaleFactor"] = "Float", ["ScaleFactorInverse"] = "Float",
            ["EyeHeightAsMeters"] = "Float", ["EyeHeightAsPercent"] = "Float",
        };

        // =====================================================================================================================
        // Model
        // =====================================================================================================================

        sealed class Ctx
        {
            public CctArgs Args;
            public CctResult R;
            public Stopwatch Clock;
            public long BudgetMs;
            public string LayerArg;
            public bool IncludeMa, IncludeTransitions, CheckBindings;
            public int MaxStates;
            public readonly List<string> LayerFilter = new List<string>();
            public string FindParameter, FindBinding;

            public string Mode;                    // controller | avatar | animator | part
            public RuntimeAnimatorController ExplicitController;
            public GameObject Target;              // resolved object; null in pure controller mode
            public GameObject AvatarRoot;          // object with VRCAvatarDescriptor
            public Component Descriptor;
            public bool CustomizeLayers = true;
            public readonly List<DescriptorLayer> DescriptorLayers = new List<DescriptorLayer>();
            public Dictionary<string, ExprParam> Expression;   // null when no Expression Parameters asset
            public string ExpressionAsset;
            public readonly List<MaMerge> MaMerges = new List<MaMerge>();
            public readonly List<object> MaBlendTrees = new List<object>();
            public readonly List<Unit> Units = new List<Unit>();

            public readonly List<ParamUse> Uses = new List<ParamUse>();
            public readonly List<LayerControlRef> LayerControls = new List<LayerControlRef>();
            public readonly List<PlayableControlRef> PlayableControls = new List<PlayableControlRef>();
            public readonly Dictionary<AnimationClip, ClipUse> Clips = new Dictionary<AnimationClip, ClipUse>();
            public readonly List<string> Notes = new List<string>();
            public int GraphStates, TotalStates, TotalTransitions;
            public bool GraphTruncated, TimeTruncated;

            readonly Dictionary<string, int> _issueCounts = new Dictionary<string, int>();
            readonly Dictionary<string, string> _overflowSeverity = new Dictionary<string, string>();

            public bool OverBudget => Clock.ElapsedMilliseconds > BudgetMs;

            /// <summary>Adds an issue (capped per code). quiet = found in an SDK default controller: downgraded to info.</summary>
            public void Issue(string severity, string code, string message, string path = null, object value = null,
                object limit = null, string asset = null, bool quiet = false)
            {
                if (quiet)
                {
                    severity = CctSeverity.Info;
                    message = "[SDK 既定コントローラ] " + message;
                }
                _issueCounts.TryGetValue(code, out var n);
                _issueCounts[code] = n + 1;
                if (n >= MaxIssuesPerCode)
                {
                    if (!_overflowSeverity.TryGetValue(code, out var s) || CctSeverity.Rank(severity) < CctSeverity.Rank(s))
                        _overflowSeverity[code] = severity;
                    return;
                }
                R.AddIssue(severity, code, message, path, value, limit, string.IsNullOrEmpty(asset) ? null : asset);
            }

            public void FlushOverflow()
            {
                foreach (var kv in _overflowSeverity)
                {
                    var omitted = _issueCounts[kv.Key] - MaxIssuesPerCode;
                    R.AddIssue(kv.Value, kv.Key, $"{kv.Key} は他に {omitted} 件あります (先頭 {MaxIssuesPerCode} 件のみ表示。layers / find_parameter / find_binding で絞り込めます)",
                        value: omitted);
                }
            }
        }

        sealed class DescriptorLayer
        {
            public int Type;
            public bool Special;
            public bool IsDefault;
            public bool IsEnabled;
            public RuntimeAnimatorController Controller;
            public bool ControllerMissing;
            public Object Mask;
            public Unit Unit;
            public string SdkDefaultAsset;
        }

        sealed class ExprParam
        {
            public string Name;
            public int ValueType;   // VRCExpressionParameters+ValueType: Int=0, Float=1, Bool=2
            public bool Synced, Saved;
            public float Default;
        }

        sealed class MaMerge
        {
            public string Id;
            public Component Comp;
            public RuntimeAnimatorController Animator;
            public bool AnimatorMissing;
            public int LayerType = LtFx;
            public int PathMode;        // MergeAnimatorPathMode: Relative=0, Absolute=1
            public bool MatchWd, DeleteAttached;
            public int Priority;
            public int MergeMode;       // MergeAnimatorMode: Append=0, Replace=1
            public string RefPath;
            public GameObject RefTarget;
            public Transform PathRoot;
            public string PathRootRule;
            public bool PathRootEstimate;
            public bool RootUnresolved;
            public bool EditorOnly;
            public int Order;
            public Unit Unit;
        }

        sealed class Unit
        {
            public string Id;
            public string Label;
            public string Origin;       // descriptor | sdk_default | ma_merge_animator | asset | animator
            public int LayerType = -1;
            public RuntimeAnimatorController Source;
            public AnimatorController Controller;
            public Dictionary<AnimationClip, AnimationClip> Overrides;
            public readonly List<string> OverrideChain = new List<string>();
            public string Asset;
            public Transform BindingRoot;
            public string BindingRootRule;
            public bool BindingRootEstimate;
            public Transform AvatarRoot;
            public MaMerge Ma;
            public bool Quiet;
            public readonly List<string> UnitNotes = new List<string>();

            public readonly Dictionary<string, AnimatorControllerParameterType> Params =
                new Dictionary<string, AnimatorControllerParameterType>(StringComparer.Ordinal);
            public readonly List<AnimatorControllerParameter> ParamList = new List<AnimatorControllerParameter>();
            public AnimatorControllerLayer[] Layers = new AnimatorControllerLayer[0];
            public readonly List<LayerData> LayerData = new List<LayerData>();
            public int States, Transitions;
            public bool Failed;
            public bool? UniformWd;     // controller-level WD after the SDK exclusions (null = mixed or no states)
            public int WdOn, WdOff;
        }

        sealed class LayerData
        {
            public Unit Unit;
            public int Index;
            public string Name;
            public AnimatorControllerLayer Layer;
            public AnimatorStateMachine Root;
            public int SyncedFrom = -1;
            public bool Included;
            public bool Additive;
            public int WdOn, WdOff, WdExcluded;
            public readonly List<MachineData> Machines = new List<MachineData>();
            public readonly Dictionary<AnimatorStateMachine, MachineData> MachineByObj = new Dictionary<AnimatorStateMachine, MachineData>();
            public readonly List<StateData> States = new List<StateData>();
            public readonly Dictionary<AnimatorState, StateData> StateByObj = new Dictionary<AnimatorState, StateData>();
            public readonly List<object> GraphStates = new List<object>();
            public readonly List<object> GraphMachines = new List<object>();
            public int StatesOmitted;
            public string MaWd;         // forced_on | match_avatar | unchanged (MA controllers only)
            public readonly List<string> ControlledBy = new List<string>();
        }

        sealed class MachineData
        {
            public AnimatorStateMachine Machine;
            public MachineData Parent;
            public string Path;
            public AnimatorStateTransition[] AnyState = new AnimatorStateTransition[0];
            public AnimatorTransition[] Entry = new AnimatorTransition[0];
            public AnimatorTransition[] Exit = new AnimatorTransition[0];  // parent's transitions from this sub machine
            public readonly List<AnimatorStateMachine> Subs = new List<AnimatorStateMachine>();
        }

        sealed class StateData
        {
            public AnimatorState State;
            public MachineData Owner;
            public string Path;
            public Motion Motion;
            public bool Wd, DirectBt, ContainsDirect, Empty, MissingMotion;
            public AnimatorStateTransition[] Transitions = new AnimatorStateTransition[0];
        }

        sealed class ParamUse
        {
            public Unit Unit;
            public string Layer;
            public string At;
            public string Kind;         // condition | blend_tree | blend_tree_direct | speed | motion_time | mirror | cycle_offset | driver_dest | driver_source | play_audio
            public string Param;
            public string Detail;
            public bool HasMode;
            public AnimatorConditionMode Mode;
        }

        sealed class LayerControlRef
        {
            public Unit Unit;
            public string At;
            public int Playable, Layer;
            public float Goal, Duration;
        }

        sealed class PlayableControlRef
        {
            public Unit Unit;
            public string At;
            public int Layer;
            public float Goal, Duration;
        }

        sealed class ClipUse
        {
            public AnimationClip Clip;
            public readonly List<Unit> Units = new List<Unit>();
            public readonly List<string> Locations = new List<string>();
            public int LocationCount;
            public EditorCurveBinding[] Curves;
            public EditorCurveBinding[] ObjectCurves;
            public int Missing;
        }

        // =====================================================================================================================
        // Entry
        // =====================================================================================================================

        public static CctResult Run(CctArgs args)
        {
            var r = CctResult.Ok("アニメーターを解析しました");
            var ctx = new Ctx { Args = args, R = r, Clock = Stopwatch.StartNew() };
            ctx.LayerArg = args.GetEnum("layer", "fx", "fx", "gesture", "action", "base", "additive", "sitting", "tpose", "ikpose", "all");
            ctx.MaxStates = args.GetInt("max_states", 400, 0, 20000);
            ctx.FindParameter = NullIfEmpty(args.GetString("find_parameter"));
            ctx.FindBinding = NullIfEmpty(args.GetString("find_binding"));
            ctx.LayerFilter.AddRange(args.GetStringList("layers"));
            // Stay well inside the MCP call timeout (default 30 s; the bridge passes timeout_seconds through).
            var timeout = args.GetInt("timeout_seconds", 30, 1, 3600);
            ctx.BudgetMs = Math.Max(8000L, timeout * 600L);

            var returned = r;
            var dirtyBefore = SceneDirtiness();
            try
            {
                if (EditorApplication.isPlaying)
                    ctx.Issue(CctSeverity.Info, "PLAY_MODE",
                        "Play モード中です。NDMF の Apply on Play が有効な場合、シーン上のアバターは処理済み (統合後の Controller、MA コンポーネントは削除済み) の可能性があります");

                var fail = ResolveInputs(ctx);
                if (fail != null)
                {
                    returned = fail;
                    return fail;
                }

                bool hasRoot = ctx.Target != null || ctx.AvatarRoot != null;
                ctx.IncludeMa = args.Has("include_ma") ? args.GetBool("include_ma", true) : hasRoot;
                ctx.IncludeTransitions = args.GetBool("include_transitions", true);
                ctx.CheckBindings = args.Has("check_bindings") ? args.GetBool("check_bindings", true) : hasRoot;

                fail = BuildUnits(ctx);
                if (fail != null)
                {
                    returned = fail;
                    return fail;
                }

                foreach (var u in ctx.Units)
                {
                    if (ctx.OverBudget) { ctx.TimeTruncated = true; break; }
                    try { InspectUnit(ctx, u); }
                    catch (Exception e)
                    {
                        u.Failed = true;
                        ctx.Issue(CctSeverity.Warning, "INSPECT_SECTION_FAILED", $"コントローラ '{u.Label}' の解析中に例外: {e.GetType().Name}: {e.Message}",
                            value: Trim(e.StackTrace, 600), asset: u.Asset);
                    }
                }

                RunSection(ctx, "lint", () => Lint(ctx));
                RunSection(ctx, "bindings", () => CheckClipBindings(ctx));
                RunSection(ctx, "find", () => RunFind(ctx));
                Output(ctx);
            }
            finally
            {
                ctx.FlushOverflow();
                ReportSceneDirtiness(returned, dirtyBefore);
            }
            return r;
        }

        static void RunSection(Ctx ctx, string name, Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                ctx.Issue(CctSeverity.Warning, "INSPECT_SECTION_FAILED", $"{name} の処理中に例外: {e.GetType().Name}: {e.Message}",
                    value: Trim(e.StackTrace, 600));
            }
        }

        // =====================================================================================================================
        // Output
        // =====================================================================================================================

        static void Output(Ctx ctx)
        {
            var r = ctx.R;
            var source = new Dictionary<string, object>
            {
                ["mode"] = ctx.Mode,
                ["layer"] = ctx.LayerArg,
                ["stage"] = EditorApplication.isPlaying ? "play_mode" : "pre_build",
                ["observed_at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            };
            if (ctx.Target != null) source["target"] = CctTargets.Describe(ctx.Target);
            if (ctx.AvatarRoot != null && ctx.AvatarRoot != ctx.Target) source["avatar"] = CctTargets.Describe(ctx.AvatarRoot);
            if (ctx.AvatarRoot != null)
            {
                source["customize_animation_layers"] = ctx.CustomizeLayers;
                source["expression_parameters"] = ctx.ExpressionAsset;
                if (ctx.Expression != null) source["expression_parameter_count"] = ctx.Expression.Count;
            }
            if (ctx.LayerFilter.Count > 0) source["layers_filter"] = ctx.LayerFilter.ToList<object>();
            r.Put("source", source);

            if (ctx.AvatarRoot != null)
            {
                var rows = new List<object>();
                foreach (var dl in ctx.DescriptorLayers)
                {
                    var row = new Dictionary<string, object>
                    {
                        ["type"] = LayerTypeName(dl.Type),
                        ["is_default"] = dl.IsDefault || !ctx.CustomizeLayers,
                    };
                    if (!dl.IsEnabled) row["is_enabled"] = false;
                    if (dl.Controller != null) row["controller"] = AssetLabel(dl.Controller);
                    else if (dl.ControllerMissing) row["controller"] = "(missing)";
                    if (dl.SdkDefaultAsset != null) row["sdk_default_asset"] = dl.SdkDefaultAsset;
                    if (dl.Mask != null) row["mask"] = AssetLabel(dl.Mask);
                    if (dl.Unit != null) row["inspected_as"] = dl.Unit.Id;
                    rows.Add(row);
                }
                r.Put("playable_layers", rows);
            }

            r.Put("controllers", ctx.Units.Select(UnitSummary).ToList<object>());

            // layers table (summary capped; full list in layers_all when capped)
            var layerRows = new List<object>();
            foreach (var u in ctx.Units)
                foreach (var ld in u.LayerData)
                    if (ld.Included) layerRows.Add(LayerRow(ld));
            r.Put("layers", layerRows.Take(SummaryLayerCap).ToList());
            if (layerRows.Count > SummaryLayerCap)
            {
                r.Put("layers_total", layerRows.Count);
                r.Put("layers_omitted", layerRows.Count - SummaryLayerCap);
                r.Put("layers_all", layerRows);
            }

            var parameters = MergedParameterRows(ctx);
            r.Put("parameters", parameters.Take(SummaryParamCap).ToList());
            if (parameters.Count > SummaryParamCap)
            {
                r.Put("parameters_total", parameters.Count);
                r.Put("parameters_omitted", parameters.Count - SummaryParamCap);
                r.Put("parameters_all", parameters);
            }

            var graph = new List<object>();
            foreach (var u in ctx.Units)
            {
                if (u.Failed) continue;
                var layers = new List<object>();
                foreach (var ld in u.LayerData)
                {
                    if (!ld.Included || ld.Root == null) continue;
                    var gl = new Dictionary<string, object>
                    {
                        ["index"] = ld.Index,
                        ["name"] = ld.Name,
                        ["default_state"] = ld.Root.defaultState != null && ld.StateByObj.TryGetValue(ld.Root.defaultState, out var ds) ? ds.Path : null,
                        ["machines"] = ld.GraphMachines,
                        ["states"] = ld.GraphStates,
                    };
                    if (ld.SyncedFrom >= 0) gl["synced_from"] = ld.SyncedFrom;
                    if (ld.StatesOmitted > 0) gl["states_omitted"] = ld.StatesOmitted;
                    layers.Add(gl);
                }
                graph.Add(new Dictionary<string, object> { ["controller"] = u.Id, ["layers"] = layers });
            }
            r.Put("graph", graph);
            r.Put("graph_preview", BuildPreview(ctx, graph));

            if (ctx.MaMerges.Count > 0 || ctx.MaBlendTrees.Count > 0)
            {
                r.Put("ma_merge_animators", ctx.MaMerges.Select(MaRow).ToList<object>());
                if (ctx.MaBlendTrees.Count > 0) r.Put("ma_merge_blend_trees", ctx.MaBlendTrees);
            }

            if (ctx.GraphTruncated)
                ctx.Issue(CctSeverity.Info, "TRUNCATED",
                    $"graph の出力を max_states={ctx.MaxStates} で打ち切りました (解析・検査は全ステートで実施)。layers で絞るか max_states を増やしてください",
                    value: ctx.TotalStates, limit: ctx.MaxStates);
            if (ctx.TimeTruncated)
                ctx.Issue(CctSeverity.Info, "TRUNCATED",
                    $"時間上限 ({ctx.BudgetMs / 1000} 秒) に達したため一部のコントローラ/クリップを解析していません。layers で絞るか timeout_seconds を増やしてください",
                    value: ctx.Clock.ElapsedMilliseconds);

            var notes = new List<string>
            {
                "静的解析です (エディタ上のアセットを読んだ結果)。Gesture Manager・エミュレーター・VRChat 実機での実行時の値ではありません",
            };
            if (ctx.MaMerges.Count > 0)
                notes.Add("Modular Avatar はビルド時に Merge Animator のコントローラを統合します (layerPriority 順、Replace は置換)。同名パラメータの型衝突は Float に変換、matchAvatarWriteDefaults で WD を揃え、加算/単一 Direct BlendTree レイヤーは WD ON に強制します。統合後の Controller はビルド前には存在しません");
            if (ctx.Units.Any(u => u.Origin == "sdk_default"))
                notes.Add("SDK 既定レイヤーは VRChat クライアント内蔵のコントローラが使われます。ここでは SDK 同梱のサンプル (NDMF が代用するもの) を解析しており、内容は推定です");
            if (ctx.Units.Any(u => u.Source is AnimatorOverrideController))
                notes.Add("AnimatorOverrideController はベースのコントローラの構造に、差し替えクリップ (overrides) を当てはめて解析しています");
            notes.AddRange(ctx.Notes);
            r.Put("notes", notes);

            int unitCount = ctx.Units.Count(u => !u.Failed);
            int layerCount = ctx.Units.Sum(u => u.LayerData.Count(l => l.Included));
            r.Message = $"コントローラ {unitCount} 個 ({string.Join(", ", ctx.Units.Take(4).Select(u => u.Label))}{(ctx.Units.Count > 4 ? " ..." : "")}) / " +
                        $"レイヤー {layerCount} / ステート {ctx.TotalStates} / 遷移 {ctx.TotalTransitions} を解析しました " +
                        $"(error {r.CountIssues(CctSeverity.Error)} / warning {r.CountIssues(CctSeverity.Warning)} / info {r.CountIssues(CctSeverity.Info)})";
        }

        static Dictionary<string, object> UnitSummary(Unit u)
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = u.Id,
                ["label"] = u.Label,
                ["origin"] = u.Origin,
            };
            if (u.LayerType >= 0) d["layer_type"] = LayerTypeName(u.LayerType);
            d["asset"] = string.IsNullOrEmpty(u.Asset) ? "(not an asset: " + (u.Source != null ? u.Source.name : "null") + ")" : u.Asset;
            d["controller_type"] = u.Source != null ? u.Source.GetType().Name : null;
            if (u.OverrideChain.Count > 0)
            {
                d["override_chain"] = u.OverrideChain.ToList<object>();
                d["override_base"] = u.Controller != null ? AssetLabel(u.Controller) : null;
                d["overrides"] = u.Overrides?.Count ?? 0;
            }
            if (u.Failed) d["failed"] = true;
            d["parameters"] = u.ParamList.Count;
            d["layers"] = u.Layers.Length;
            d["states"] = u.States;
            d["transitions"] = u.Transitions;
            d["wd"] = new Dictionary<string, object>
            {
                ["on"] = u.WdOn,
                ["off"] = u.WdOff,
                ["mixed"] = u.WdOn > 0 && u.WdOff > 0,
            };
            if (u.BindingRoot != null)
            {
                d["binding_root"] = CctTargets.GetPath(u.BindingRoot);
                d["binding_root_rule"] = u.BindingRootRule;
                if (u.BindingRootEstimate) d["binding_root_estimate"] = true;
            }
            if (u.Ma != null) d["ma"] = u.Ma.Id;
            if (u.UnitNotes.Count > 0) d["notes"] = u.UnitNotes.ToList<object>();
            return d;
        }

        static Dictionary<string, object> LayerRow(LayerData ld)
        {
            var l = ld.Layer;
            var d = new Dictionary<string, object>
            {
                ["controller"] = ld.Unit.Id,
                ["index"] = ld.Index,
                ["name"] = ld.Name,
                ["weight"] = ld.Index == 0 ? 1.0 : Round(l.defaultWeight),
                ["blending"] = l.blendingMode.ToString(),
            };
            if (l.avatarMask != null) d["mask"] = AssetLabel(l.avatarMask);
            if (ld.SyncedFrom >= 0)
            {
                d["sync"] = ld.SyncedFrom;
                if (l.syncedLayerAffectsTiming) d["sync_timing"] = true;
            }
            if (l.iKPass) d["ik"] = true;
            d["wd_on"] = ld.WdOn;
            d["wd_off"] = ld.WdOff;
            if (ld.WdExcluded > 0) d["wd_excluded"] = ld.WdExcluded;
            d["state_count"] = ld.States.Count;
            if (ld.Machines.Count > 1) d["sub_machines"] = ld.Machines.Count - 1;
            if (ld.Root != null && ld.Root.defaultState != null && ld.StateByObj.TryGetValue(ld.Root.defaultState, out var ds))
                d["default_state"] = ds.Path;
            if (ld.MaWd != null) d["ma_wd"] = ld.MaWd;
            if (ld.ControlledBy.Count > 0) d["controlled_by"] = ld.ControlledBy.Take(8).ToList<object>();
            return d;
        }

        static Dictionary<string, object> MaRow(MaMerge m)
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = m.Id,
                ["path"] = CctTargets.GetPath(m.Comp.transform),
                ["layer_type"] = LayerTypeName(m.LayerType),
                ["animator"] = m.Animator != null ? AssetLabel(m.Animator) : (m.AnimatorMissing ? "(missing)" : null),
                ["path_mode"] = m.PathMode == 1 ? "Absolute" : "Relative",
                ["match_avatar_write_defaults"] = m.MatchWd,
                ["merge_mode"] = m.MergeMode == 1 ? "Replace" : "Append",
                ["layer_priority"] = m.Priority,
                ["merge_order"] = m.Order,
            };
            if (m.PathMode == 0)
            {
                d["relative_root"] = m.PathRoot != null ? CctTargets.GetPath(m.PathRoot) : null;
                d["relative_root_rule"] = m.PathRootRule;
                if (!string.IsNullOrEmpty(m.RefPath)) d["relative_path_root_reference"] = m.RefPath;
            }
            if (m.DeleteAttached) d["delete_attached_animator"] = true;
            var behaviour = m.Comp as Behaviour;
            if (behaviour != null && !behaviour.enabled) d["enabled"] = false;
            if (!CctTargets.IsEffectivelyActive(m.Comp.transform)) d["active"] = false;
            if (m.EditorOnly) d["editor_only"] = true;
            d["inspected"] = m.Unit != null && !m.Unit.Failed;
            if (m.Unit != null) d["unit"] = m.Unit.Id;
            return d;
        }

        static object BuildPreview(Ctx ctx, List<object> graph)
        {
            if (ctx.GraphStates <= PreviewFullStates && !ctx.GraphTruncated) return graph;
            var outline = new List<object>();
            int layersShown = 0, layersTotal = 0;
            foreach (var u in ctx.Units)
                foreach (var ld in u.LayerData)
                {
                    if (!ld.Included) continue;
                    layersTotal++;
                    if (layersShown >= PreviewLayerCap) continue;
                    layersShown++;
                    var names = ld.States.Take(PreviewStatesPerLayer).Select(s => s.Path).ToList<object>();
                    var row = new Dictionary<string, object>
                    {
                        ["controller"] = u.Id,
                        ["index"] = ld.Index,
                        ["name"] = ld.Name,
                        ["states"] = names,
                    };
                    if (ld.States.Count > PreviewStatesPerLayer) row["states_omitted"] = ld.States.Count - PreviewStatesPerLayer;
                    outline.Add(row);
                }
            return new Dictionary<string, object>
            {
                ["outline_only"] = true,
                ["reason"] = $"ステート {ctx.TotalStates} 個: 全体は report_path の graph にあります。詳細が必要なレイヤーは layers=[名前] で指定してください",
                ["layers"] = outline,
                ["layers_omitted"] = Math.Max(0, layersTotal - layersShown),
            };
        }

        static List<object> MergedParameterRows(Ctx ctx)
        {
            var order = new List<string>();
            var byName = new Dictionary<string, List<(Unit unit, AnimatorControllerParameter p)>>(StringComparer.Ordinal);
            foreach (var u in ctx.Units)
                foreach (var p in u.ParamList)
                {
                    if (!byName.TryGetValue(p.name, out var list))
                    {
                        list = new List<(Unit, AnimatorControllerParameter)>();
                        byName[p.name] = list;
                        order.Add(p.name);
                    }
                    list.Add((u, p));
                }
            bool multi = ctx.Units.Count > 1;
            var flagged = new List<object>();
            var plain = new List<object>();
            foreach (var name in order)
            {
                var list = byName[name];
                var first = list[0].p;
                var d = new Dictionary<string, object> { ["name"] = name };
                var types = list.Select(x => x.p.type).Distinct().ToList();
                bool conflict = types.Count > 1;
                if (!conflict) d["type"] = first.type.ToString();
                else
                {
                    var t = new Dictionary<string, object>();
                    foreach (var x in list) t[x.unit.Id] = x.p.type.ToString();
                    d["types"] = t;
                }
                d["default"] = ParamDefault(first);
                if (multi) d["controllers"] = list.Select(x => x.unit.Id).Distinct().ToList<object>();
                bool interesting = conflict;
                if (ctx.Expression != null && ctx.Expression.TryGetValue(name, out var ep))
                {
                    var exprType = ExprTypeName(ep.ValueType);
                    d["expression"] = $"{exprType}{(ep.Synced ? " synced" : " local")}{(ep.Saved ? " saved" : "")} default={Round(ep.Default).ToString(CultureInfo.InvariantCulture)}";
                    if (types.Any(t => t.ToString() != exprType)) interesting = true;
                }
                if (BuiltinParams.TryGetValue(name, out var bt))
                {
                    d["builtin"] = bt;
                    if (types.Any(t => t.ToString() != bt)) interesting = true;
                }
                (interesting ? flagged : plain).Add(d);
            }
            flagged.AddRange(plain);
            return flagged;
        }

        // =====================================================================================================================
        // Small helpers
        // =====================================================================================================================

        static object ParamDefault(AnimatorControllerParameter p)
        {
            switch (p.type)
            {
                case AnimatorControllerParameterType.Float: return Round(p.defaultFloat);
                case AnimatorControllerParameterType.Int: return p.defaultInt;
                case AnimatorControllerParameterType.Bool: return p.defaultBool;
                default: return null;
            }
        }

        static string ExprTypeName(int valueType)
        {
            switch (valueType)
            {
                case 0: return "Int";
                case 1: return "Float";
                case 2: return "Bool";
                default: return valueType.ToString(CultureInfo.InvariantCulture);
            }
        }

        static double Round(float f) => float.IsNaN(f) || float.IsInfinity(f) ? 0 : Math.Round(f, 4);

        static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        static string Trim(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max) + "...";

        /// <summary>"Assets/x.controller" for assets, "name (not an asset)" for in-memory objects.</summary>
        static string AssetLabel(Object o)
        {
            if (o == null) return null;
            var p = AssetDatabase.GetAssetPath(o);
            if (string.IsNullOrEmpty(p)) return o.name + " (not an asset)";
            return AssetDatabase.IsMainAsset(o) ? p : p + "#" + o.name;
        }

        static Dictionary<int, bool> SceneDirtiness()
        {
            var d = new Dictionary<int, bool>();
            foreach (var s in CctTargets.LoadedScenes()) d[s.handle] = s.isDirty;
            return d;
        }

        static void ReportSceneDirtiness(CctResult r, Dictionary<int, bool> before)
        {
            try
            {
                foreach (var s in CctTargets.LoadedScenes())
                    if (before.TryGetValue(s.handle, out var was) && !was && s.isDirty)
                        r.AddIssue(CctSeverity.Warning, "SCENE_DIRTIED",
                            $"解析中にシーン '{s.name}' が未保存状態になりました (このツールは書き込みをしないはずです。変更内容を確認してください)", value: s.path);
            }
            catch (Exception) { /* reporting only */ }
        }
    }
}
