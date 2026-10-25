using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// cct_world_audit: read-only pre-upload audit of a VRChat WORLD scene (Worlds SDK 3.10.5, Unity 2022.3.22f1).
    /// It mirrors the SDK's own world build/upload validations (VRCSdkControlPanelWorldBuilder.OnGUISceneCheck /
    /// VerifySpawnPlacement, VRCSdkControlPanelBuilder.CheckProjectSetup, VRCWorldAssetExporter, VRCWorldPostBuildValidator,
    /// UpdateLayers, WorldValidation allowlists) and adds the docs' recommendations (creator docs snapshot 2026-10-20) and a
    /// few clearly labelled community/estimate checks. Everything is a static, pre-build estimate made in the editor: it does
    /// not build, upload, run ClientSim or touch the VRChat API.
    ///
    /// Sources (all relative to the research corpora; see research/world-requirements.md 2.4 / 2.6 / 11 for the full list):
    ///   [WB]  com.vrchat.worlds 3.10.5 Editor/VRCSDK/SDK3/VRCSdkControlPanelWorldBuilder.cs
    ///   [PB]  com.vrchat.base 3.10.5 Editor/VRCSDK/Dependencies/VRChat/ControlPanel/VRCSdkControlPanelBuilder.cs
    ///   [WV]  com.vrchat.base 3.10.5 Runtime/VRCSDK/Dependencies/VRChat/Scripts/Validation/WorldValidation.cs
    ///   [EX]  VRCSDK3-Editor.dll VRCWorldAssetExporter.ExportCurrentSceneResource (IL), VRCWorldPostBuildValidator.cs
    ///   [UL]  VRCCore-Editor.dll UpdateLayers + Runtime/VRCSDK/Dependencies/VRChat/Resources/VRCProjectSettings.prefab
    ///   [UB]  com.vrchat.worlds 3.10.5 Runtime/Udon/UdonBehaviour.cs, Integrations/UdonSharp (source)
    ///   [CD]  creator docs: docs/worlds/*.md, docs/platforms/android/*.md, docs/platforms/iOS/*.md
    ///   [VRWT] VRWorld Toolkit 3.5.1 (MIT, community, NOT official): used only for checks marked "community".
    ///
    /// Scope rules: the SDK counts Scene Descriptors / PipelineManagers and gathers components with
    /// Resources.FindObjectsOfTypeAll over ALL loaded scenes (inactive included, prefab assets and hideFlags ==
    /// NotEditable/HideAndDontSave excluded), but the exporter builds only the ACTIVE scene. So the SDK-blocking checks
    /// (descriptor/pipeline counts, VRC_EventHandler, ObjectSync conflicts, PhysBone limits) use every loaded scene, and the
    /// content audit uses the 'scene' scope (default: active scene). Objects tagged EditorOnly (self or ancestor) are stripped
    /// by Unity at build and are excluded from the content audit (but not from the SDK-mirrored counts). FindObjectsOfTypeAll
    /// also returns an open Prefab Mode stage, so descriptors/PipelineManagers there are reported (PREFAB_STAGE_SDK_COMPONENTS).
    /// The component allowlist and the mobile shader list are read from the installed SDK (WorldValidation) by reflection, with
    /// the embedded 3.10.5 copies as fallback (data: components.allowlist_source, android.shader_list_source). SDK fields that
    /// a different SDK version lacks are reported as SDK_FIELD_NOT_FOUND and the dependent checks are skipped.
    ///
    /// Arguments: scene ('active' default | 'all' | path or name of a loaded scene), platform ('all' default | 'pc' | 'android' |
    /// 'ios'; android and ios share the mobile limits), top_n (15, 1-500), check_references (true), max_reference_scan (5000),
    /// check_editor_log (true), timeout_seconds (30; the missing-reference scan stops when the audit has run timeout - 10 s,
    /// the UdonSharp program-asset scan after a quarter of that), plus the common detail / max_issues / save_report.
    ///
    /// Estimates (the output says so): triangles from index counts; texture memory from TextureUtil (current build target);
    /// mobile texture size from the imported size and the platform override; "not whitelisted" because the SDK never checks the
    /// allowlist itself (research 2.6-F: the client strips unknown components when loading); lightmap / probe / camera /
    /// post-processing advice marked "community" comes from VRWorld Toolkit, not from VRChat.
    ///
    /// Issue codes (severity / origin: SDK = the SDK reports it the same way, DOC = creator docs, COM = community knowledge,
    /// EST = this tool's estimate):
    /// | code                              | sev     | origin | rule                                                                 |
    /// |-----------------------------------|---------|--------|----------------------------------------------------------------------|
    /// | WORLDS_SDK_MISSING                | fail    | -      | VRC.SDK3.Components.VRCSceneDescriptor type not loaded               |
    /// | INVALID_ARGUMENT / SCENE_NOT_LOADED / NO_LOADED_SCENE | fail | - | bad platform/scene argument, or nothing loaded             |
    /// | PLAY_MODE                         | warning | -      | Play mode: runtime objects (ClientSim etc.) are included             |
    /// | MULTIPLE_SCENES_LOADED            | warning | EST    | another loaded scene has GameObjects: the SDK counts descriptors /   |
    /// |                                   | (info)  |        | PipelineManagers over all loaded scenes but builds only the active   |
    /// |                                   |         |        | one (info when the extra scenes are empty; NDMF's preview scene is   |
    /// |                                   |         |        | never counted). The SDK itself has no multi-scene check              |
    /// | SCENE_UNSAVED                     | info    | SDK    | audited scene dirty (the SDK saves open scenes before building)      |
    /// | SCENE_DIRTIED                     | warning | -      | a scene became dirty during this read-only audit (should not happen) |
    /// | SECTION_FAILED                    | warning | -      | one audit section threw; its data is incomplete                      |
    /// | UNITY_VERSION_MISMATCH            | warning | SDK    | Application.unityVersion != 2022.3.22f1 (SDK uses RemoteConfig)      |
    /// | SCRIPT_COMPILATION_FAILED         | error   | EST    | EditorUtility.scriptCompilationFailed (nothing can be built)         |
    /// | ANDROID_TEXTURE_FORMAT_NOT_ASTC   | error   | SDK    | active target Android and androidBuildSubtarget != ASTC [PB C2]      |
    /// | LIGHTING_AUTO_GENERATE            | warning | SDK    | Lightmapping.giWorkflowMode == Iterative [PB]                        |
    /// | GRAVITY_NOT_STRAIGHT_DOWN / GRAVITY_UPWARD / GRAVITY_ZERO | warning | SDK | Physics.gravity x/z != 0, y > 0, y == 0 [WB]       |
    /// | FOG_STRIPPING_CUSTOM              | warning | SDK    | GraphicsSettings m_FogStripping Custom with a keep flag [WB]         |
    /// | COLOR_SPACE_GAMMA                 | info    | SDK    | Gamma: EnvConfig switches to Linear when building                    |
    /// | BUNDLE_TOO_LARGE_MOBILE           | error   | SDK    | last Android/iOS build > 104,857,600 B: Upload() refuses it [WB E6]  |
    /// | BUNDLE_LARGE_PC                   | info    | DOC    | last PC build > 200 MB (Labs/Public recommendation)                  |
    /// | AVATAR_PACKAGES_IN_WORLD_PROJECT  | info    | COM    | avatar-only packages registered in a world project (and the installed|
    /// |                                   |         |        | packages whose package.json requires them)                           |
    /// | SDK2_EVENT_HANDLER                | error   | SDK    | VRC_EventHandler present [WB C6]                                     |
    /// | SDK2_COMPONENT                    | error   | SDK    | a VRCSDK2.* component present [PB C1]                                |
    /// | EDITOR_LOG_CS_ERRORS              | info    | EST    | "error CS####" lines in the Editor.log tail (may be already fixed)   |
    /// | EDITOR_LOG_USHARP_ERRORS          | warning | EST    | UdonSharp error lines in the Editor.log tail                         |
    /// | EDITOR_LOG_UNAVAILABLE            | info    | -      | Editor.log could not be read                                         |
    /// | NO_SCENE_DESCRIPTOR               | error   | SDK    | 0 VRC_SceneDescriptor in loaded scenes [WB A2, EX D1]                |
    /// | MULTIPLE_SCENE_DESCRIPTORS        | error   | SDK    | >1 descriptor in loaded scenes [WB A3, EX D1]                        |
    /// | DESCRIPTOR_NOT_IN_UPLOAD_SCENE    | error   | SDK    | the descriptor is not in the active scene [EX D5a/D5b]               |
    /// | DESCRIPTOR_EDITOR_ONLY            | error   | EST    | descriptor object tagged EditorOnly (stripped from the bundle)       |
    /// | PIPELINE_MANAGER_MISSING          | error   | SDK    | 0 PipelineManager [WB C5, E1, PostBuildValidator]                    |
    /// | MULTIPLE_PIPELINE_MANAGERS        | error   | SDK    | >1 PipelineManager [WB A1/C4]                                        |
    /// | PIPELINE_MANAGER_NOT_ON_DESCRIPTOR| warning | SDK    | the PipelineManager is not on the descriptor's GameObject            |
    /// | PREFAB_STAGE_SDK_COMPONENTS       | error / warning | SDK | open Prefab Mode stage holds descriptors/PipelineManagers: the |
    /// |                                   |         |        | SDK's FindObjectsOfTypeAll counts include it (error if total > 1)    |
    /// | NO_SPAWNS                         | error   | SDK    | GetValidatedSpawnList() empty [WB C9, EX D3]                         |
    /// | SPAWN_NULL                        | warning | EST    | null / missing entries in spawns (ignored by the SDK)                |
    /// | SPAWN_TOO_FAR                     | warning | SDK    | a spawn axis >= 1000 [WB VerifySpawnPlacement]                       |
    /// | RESPAWN_HEIGHT_ABOVE_SPAWN        | warning | SDK    | spawn y < RespawnHeightY [WB VerifySpawnPlacement]                   |
    /// | SPAWN_NO_GROUND                   | info    | EST    | editor raycast finds no player-colliding ground under a spawn        |
    /// | SPAWN_RADIUS_ZERO                 | info    | DOC    | spawnRadius 0: simultaneous arrivals overlap                         |
    /// | AUTO_SPATIALIZE_DEPRECATED        | warning | SDK    | autoSpatializeAudioSources true [WB]                                 |
    /// | REFERENCE_CAMERA_MISSING          | info    | DOC    | no Reference Camera (clip planes / post processing not applied)      |
    /// | REFERENCE_CAMERA_NO_CAMERA        | warning | COM    | Reference Camera object has no Camera                                |
    /// | REFERENCE_CAMERA_NEAR_CLAMPED     | info    | EST    | near outside 0.01..0.05 (ClientSim clamps it; docs: Udon 0.001..0.05)|
    /// | REFERENCE_CAMERA_OBJECT_DISABLED  | warning | SDK/EST| non-camera components on/under the Reference Camera's GameObject,   |
    /// |                                   |         |        | which is deactivated at world start (SDK text, ClientSim)            |
    /// | REFERENCE_CAMERA_TAA              | warning | SDK    | its PostProcessLayer uses TAA [WB/PB]                                |
    /// | LAYERS_NOT_SETUP                  | error   | SDK    | layer names 3..21 != VRCProjectSettings.layers [WB A4/C10]           |
    /// | COLLISION_MATRIX_NOT_SETUP        | error   | SDK    | collision matrix 0..21 != VRCProjectSettings [WB A4/C10]             |
    /// | LAYER_SDK_CHECK_UNAVAILABLE       | info    | -      | UpdateLayers could not be called; tool's own comparison used         |
    /// | RESERVED_LAYER_USED               | warning | DOC    | objects on Item/reserved/InternalUI/HardwareObjects/31               |
    /// | MIRROR_REFLECTION_LAYER_RENDERER  | info    | DOC    | renderers on MirrorReflection only appear in mirrors                 |
    /// | LIGHTMAP_NOT_BAKED                | warning | DOC    | ContributeGI renderers exist but no lightmaps                        |
    /// | LIGHTMAP_OUTDATED                 | info    | EST    | lightmaps exist but some ContributeGI renderers have none            |
    /// | LIGHTMAP_UV_MISSING               | info    | COM    | ContributeGI mesh without UV2 and no "Generate Lightmap UVs", whose  |
    /// |                                   |         |        | UV0 is not a clean 0..1 unwrap (built-in Quad etc. are not reported) |
    /// | REALTIME_SHADOW_LIGHTS            | warning | DOC/EST| > 1 active realtime light with shadows                               |
    /// | BAKED_LIGHT_NOT_BAKED             | warning | COM    | Baked/Mixed light without bake output (acts realtime in game)        |
    /// | NO_LIGHT_PROBES                   | info    | COM    | baked lighting without light probes (players/pickups unlit)          |
    /// | REFLECTION_PROBE_NOT_BAKED        | info    | COM    | Baked reflection probe without baked texture                         |
    /// | REFLECTION_PROBE_EVERY_FRAME      | warning | EST    | active realtime reflection probe refreshing every frame              |
    /// | OCCLUSION_NOT_BAKED               | info / warning (mobile) | DOC | StaticOcclusionCulling.umbraDataSize == 0            |
    /// | MISSING_SHADER                    | warning | EST    | material uses Hidden/InternalErrorShader (pink)                      |
    /// | MATERIAL_SLOT_EMPTY               | info    | EST    | renderer has empty material slots                                    |
    /// | LARGE_TEXTURE                     | warning | SDK    | TextureImporter.maxTextureSize > 8192 [WB/PB]                        |
    /// | TEXTURE_MIP_BOX_FILTER            | info    | SDK    | mipmaps with Box filter (Kaiser recommended) [WB/PB]                 |
    /// | SUBSTANCE_MATERIAL                | warning | SDK    | material asset is .sbsar [WB/PB] (3.10.5 never shows it: its scan  |
    /// |                                   |         |        | GetSubstanceObjects(null) returns early)                             |
    /// | PARTICLE_BILLBOARD_ROLL           | info    | SDK    | Billboard particle renderer with allowRoll [WB]                      |
    /// | TMP_RECOMMENDED                   | info    | SDK    | UI.Text/TextMesh used and no TextMeshPro [WB]                        |
    /// | UI_DEFAULT_SHADER                 | info    | SDK    | uGUI graphic with UI/Default while Supersampled UI exists [WB]       |
    /// | EXTRA_ACTIVE_CAMERA               | warning | COM/EST| active non-reference camera rendering to the screen                  |
    /// | CAMERA_RENDER_TEXTURE_ACTIVE      | info    | COM    | active camera rendering into a RenderTexture every frame             |
    /// | PP_VOLUME_ON_MAIN_CAMERA          | warning | SDK    | PostProcessVolume under Camera.main [WB; hidden by ResetIssues]      |
    /// | PP_LAYER_MISSING                  | info    | COM    | volumes exist but Reference Camera has no PostProcessLayer           |
    /// | PP_VOLUME_LAYER_EMPTY             | info    | COM    | Reference Camera's PostProcessLayer volumeLayer is Nothing           |
    /// | PP_VOLUME_NOT_IN_LAYER            | info    | EST    | active volume whose layer is outside that volumeLayer (never applied)|
    /// | PP_HEAVY_EFFECT                   | info    | DOC    | active volume enables chromatic aberration / SSR / AO                |
    /// | MIRROR_ENABLED_AT_START           | warning | DOC    | mirror active+enabled in the saved scene                             |
    /// | MIRROR_MULTIPLE_ENABLED           | warning | DOC    | >= 2 mirrors enabled at start                                        |
    /// | MIRROR_NO_RENDERER                | warning | SDK    | no Renderer/material on the mirror object (it disables itself)       |
    /// | MIRROR_REFLECT_EVERYTHING         | info    | DOC    | Reflect Layers = Everything                                          |
    /// | MIRROR_REFLECT_UIMENU             | info    | COM    | Reflect Layers include UiMenu                                        |
    /// | MIRROR_NO_LOCAL_PLAYER            | info    | DOC    | Reflect Layers exclude MirrorReflection (you are not reflected)      |
    /// | MIRROR_LAYER_NOT_RENDERED         | warning | DOC    | mirror object on MirrorReflection (not drawn by the main camera)     |
    /// | UDON_NO_PROGRAM                   | error   | UB     | no program source and no serializedProgramAsset (nothing runs)       |
    /// | UDON_PROGRAM_MISSING_REFERENCE    | error   | UB     | program source is a deleted asset and no serializedProgramAsset      |
    /// | UDON_PROGRAM_SOURCE_MISSING       | warning | UB     | program source null/deleted but serializedProgramAsset kept: the    |
    /// |                                   |         |        | last compiled program runs and edits are not picked up               |
    /// | UDON_PROGRAM_NOT_COMPILED         | warning | UB     | program source set, but neither the behaviour nor the program source |
    /// |                                   |         |        | has a compiled asset (a stale behaviour link is relinked on save)    |
    /// | UDON_SYNC_CONFLICT                | warning | UB     | Manual and Continuous UdonBehaviours on one GameObject (U# warning)  |
    /// | UDON_BUILTIN_MESH_REFERENCE       | warning | SDK    | Udon public variable references a builtin primitive mesh [WB]        |
    /// | OBJECT_SYNC_MANUAL_UDON           | error   | SDK    | VRCObjectSync with a Manual UdonBehaviour on the same object [WB C7] |
    /// | OBJECT_SYNC_WITH_POOL             | error   | SDK    | VRCObjectSync with VRCObjectPool on the same object [WB C8]          |
    /// | USHARP_PROXY_NO_BACKING           | warning | UB     | UdonSharpBehaviour without backing UdonBehaviour (U# normally repairs|
    /// |                                   |         |        | it on scene open/Play/compile; left only if its program asset fails) |
    /// | USHARP_PROGRAM_NO_SOURCE          | error   | UB     | UdonSharpProgramAsset.sourceCsScript is null                         |
    /// | USHARP_SCRIPT_NOT_IN_USHARP_ASSEMBLY | error | UB    | program asset's script in an asmdef without U# Assembly Definition   |
    /// | USHARP_SCRIPT_IN_NON_USHARP_ASSEMBLY | info | UB     | same, but no program asset found (U# compile is not blocked)         |
    /// | USHARP_ASSEMBLY_DEFINITION_NO_SOURCE | warning | UB  | U# Assembly Definition asset with no sourceAssembly (U# ignores it)  |
    /// | USHARP_DUPLICATE_PROGRAM_ASSET    | error   | UB     | one script referenced by >1 UdonSharpProgramAsset (U# aborts compile)|
    /// | USHARP_SCAN_TRUNCATED             | info    | -      | program-asset scan stopped at the time budget                        |
    /// | PICKUP_NO_RIGIDBODY               | error   | SDK    | VRCPickup without Rigidbody (RequireComponent)                       |
    /// | PICKUP_NO_COLLIDER                | warning | SDK    | VRCPickup without Collider on the same object (inspector warning)    |
    /// | PICKUP_NOT_SYNCED                 | info    | DOC    | pickup without VRCObjectSync (or obsolete Udon SynchronizePosition); |
    /// |                                   |         |        | not for Udon-placed ones (own Udon + not pickupable at start, or a   |
    /// |                                   |         |        | Manual/Continuous UdonBehaviour on an ancestor)                      |
    /// | PICKUP_COLLIDES_WITH_PLAYER       | info    | DOC    | non-kinematic pickup on a layer that collides with players           |
    /// | STATION_NO_COLLIDER               | info    | DOC    | VRCStation without Collider (cannot be interacted with itself); not  |
    /// |                                   |         |        | when an Udon on another object references it (seats via UseStation)  |
    /// | STATION_NO_INTERACT_UDON          | info    | DOC    | VRCStation without an UdonBehaviour on the same object               |
    /// | MESH_COLLIDER_NONCONVEX_RIGIDBODY | warning | EST    | non-convex MeshCollider under a non-kinematic Rigidbody (unsupported)|
    /// | OBJECT_POOL_EMPTY_ENTRY           | warning | EST    | VRCObjectPool.Pool has null entries                                  |
    /// | PORTAL_NOT_AT_ROOT                | warning | DOC    | VRCPortalMarker not at the scene root (instance not synced)          |
    /// | PORTAL_NO_DESTINATION             | warning | DOC    | portal with world None and empty World ID                            |
    /// | PEDESTAL_NO_BLUEPRINT             | warning | DOC    | VRCAvatarPedestal without blueprintId                                |
    /// | VIDEO_PLAYER_TOO_MANY             | warning | DOC    | > 2 VRChat video players                                             |
    /// | VIDEO_PLAYER_AUTOPLAY_MULTIPLE    | warning | DOC    | >= 2 players auto-play a URL (5 s per-user URL rate limit)           |
    /// | VIDEO_PLAYER_NO_OUTPUT            | warning | EST    | Unity player without target texture/renderer; AVPro without screen   |
    /// | VIDEO_PLAYER_NO_AUDIO             | info    | EST    | Unity player without audio sources; AVPro without speaker. Not for a |
    /// |                                   |         |        | Unity player under iwaSync3 whose Speakers it assigns at build        |
    /// | VIDEO_PLAYER_URL_NOT_ALLOWLISTED  | info    | DOC    | default URL host not in the docs' allowlist                          |
    /// | VIDEO_PLAYER_HTTP_MOBILE          | warning | DOC    | http:// URL (platform all/android): Android plays only HTTPS video   |
    /// | AUDIO_NO_SPATIAL                  | warning | SDK    | AudioSource without VRC_SpatialAudioSource [WB]                      |
    /// | AUDIO_ONSP_DEPRECATED             | warning | SDK    | AudioSource with ONSPAudioSource [WB]                                |
    /// | PHYSBONE_NO_PARENT                | warning | SDK    | PhysBone root transform has no parent [WB]                           |
    /// | PHYSBONE_SHAPES_OVER_LIMIT        | warning | SDK    | PhysBones + colliders > 1024 [WB]                                    |
    /// | CONTACTS_OVER_LIMIT               | warning | SDK    | contacts > 1024 [WB]                                                 |
    /// | UI_SHAPE_NO_CANVAS / UI_SHAPE_ON_UI_LAYER / UI_SHAPE_NOT_WORLD_SPACE / UI_SHAPE_NO_RAYCASTER | warning | DOC | VRCUiShape setup |
    /// | UI_NO_EVENT_SYSTEM                | info    | DOC    | VRCUiShape present but no EventSystem in the scope (ClientSim and,   |
    /// |                                   |         |        | apparently, the client supply their own)                             |
    /// | UI_CANVAS_NO_UI_SHAPE             | info    | DOC    | interactive world-space canvas without VRCUiShape                    |
    /// | UI_NAVIGATION_NOT_NONE            | info    | DOC    | Selectable navigation not None                                       |
    /// | UI_SCROLL_SENSITIVITY             | info    | DOC    | ScrollRect scroll sensitivity != 0                                   |
    /// | MISSING_SCRIPT                    | error   | EST    | GameObjectUtility.GetMonoBehavioursWithMissingScriptCount > 0        |
    /// | MISSING_REFERENCE                 | warning | EST    | object reference to a deleted asset/object (SerializedObject scan)   |
    /// | MISSING_PREFAB                    | warning | EST    | prefab instance whose asset is missing (outermost object reported)   |
    /// | NOT_WHITELISTED                   | warning | WV     | component type not in the SDK3 world allowlist (client strips it);  |
    /// |                                   | (info)  |        | info for UnityEngine.ProBuilder.* (ProBuilder strips them at build), |
    /// |                                   |         |        | for types a known scene build processor destroys (iwaSync3), and for |
    /// |                                   |         |        | types whose package has a scene build processor (estimate). Never    |
    /// |                                   |         |        | suggests the EditorOnly tag over shipping children (Udon, audio, ...)|
    /// | NOT_WHITELISTED_DERIVED           | warning | WV/EST | derives from an allowed type but lives in a project assembly         |
    /// | EDITOR_ONLY_COMPONENTS            | info    | WV     | IEditorOnly components (exempt from the allowlist)                   |
    /// | REFERENCE_SCAN_TRUNCATED          | info    | -      | missing-reference scan stopped at max_reference_scan / time budget   |
    /// | SERIALIZE_FAILED                  | info    | -      | a component could not be read via SerializedObject                   |
    /// | SDK_FIELD_NOT_FOUND               | info    | -      | an expected SDK serialized field is absent (other SDK version); the  |
    /// |                                   |         |        | checks that need it are skipped instead of reporting false errors    |
    /// | ISSUES_TRUNCATED                  | info    | -      | more than 20 issues of one code; the rest are counted only           |
    /// | ANDROID_SHADER_NOT_SUPPORTED      | warning | SDK    | mobile: Renderer shaders not in WorldValidation.ShaderWhiteList [WB] |
    /// |                                   | (info)  |        | (one issue for all of them, table in value); one info for the docs   |
    /// |                                   |         |        | mobile shaders the SDK list misses (Toon Standard, Worlds/Supersampled UI)|
    /// | ANDROID_POST_PROCESSING           | warning | DOC/WV | mobile: post processing is disabled/stripped                         |
    /// | ANDROID_REALTIME_LIGHT            | warning | DOC    | mobile: active realtime lights ("bake lighting is essential")        |
    /// | ANDROID_LARGE_TEXTURE             | info    | DOC    | mobile: textures above 1024 px (after Non-Power of 2 scaling)        |
    /// | ANDROID_TRIANGLE_BUDGET           | warning | DOC    | mobile: > 250,000 triangles in the scope                             |
    /// | ANDROID_UNSUPPORTED_COMPONENT     | warning | DOC    | mobile: Cloth / Dynamic Bone are disabled                            |
    /// | ANDROID_TRANSPARENT_MATERIALS     | info    | DOC    | mobile: cutout/transparent materials (render queue >= 2450)          |
    /// | ANDROID_GPU_INSTANCING_OFF        | info    | DOC    | mobile: materials with GPU Instancing disabled                       |
    /// "mobile" = platform 'android' or 'ios' (or 'all'): the docs give iOS the Android limits
    /// (docs/platforms/iOS/setting-up-unity-for-creating-ios-content.md L38) and the SDK uses #if UNITY_ANDROID || UNITY_IOS.
    /// CctRunner may add EDITOR_BUSY / EXCEPTION (fail) and REPORT_WRITE_FAILED (warning).
    ///
    /// Data keys (* = kept in the MCP summary): verdict* (build_blockers = error codes whose rule is an SDK/UdonSharp stop
    /// condition: SdkBuildBlockerCodes in CctWorldAudit.Gimmicks.cs; upload_blockers; other_errors), scenes*, platforms*,
    /// counts*, project*, descriptor*, layers*, lighting*, rendering*, mirrors*, udon*, gimmicks* (VRC video players, U#
    /// namespaces, community packages by name: estimate), android*, not_checked*, notes*, interactables, colliders, ui,
    /// components, hotspots, editor_log.
    /// </summary>
    public static partial class CctWorldAudit
    {
        public const string ToolName = "cct_world_audit";

        // Unity version VRChat requires (docs/sdk/upgrade/current-unity-version.md L25; SDK 3.10.5, docs 2026-10-20).
        const string RecommendedUnityVersion = "2022.3.22f1";

        const int PerCodeIssueCap = 20;
        const int ExampleCap = 10;

        // Full type names (no compile-time SDK dependency; verified with FieldCheck, see tests/vrc-fields/CctWorldAudit.txt).
        const string TSceneDescriptorBase = "VRC.SDKBase.VRC_SceneDescriptor";
        const string TSceneDescriptor = CctVrcTypes.SceneDescriptor;
        const string TPipelineManager = CctVrcTypes.PipelineManager;
        const string TUdonBehaviour = CctVrcTypes.UdonBehaviour;
        const string TUSharpBehaviour = "UdonSharp.UdonSharpBehaviour";
        const string TUSharpProgramAsset = "UdonSharp.UdonSharpProgramAsset";
        const string TUSharpAssemblyDefinition = "UdonSharpEditor.UdonSharpAssemblyDefinition";
        const string TPickup = "VRC.SDK3.Components.VRCPickup";
        const string TObjectSync = "VRC.SDK3.Components.VRCObjectSync";
        const string TObjectPool = "VRC.SDK3.Components.VRCObjectPool";
        const string TStation = "VRC.SDK3.Components.VRCStation";
        const string TMirror = "VRC.SDK3.Components.VRCMirrorReflection";
        const string TUnityVideo = "VRC.SDK3.Video.Components.VRCUnityVideoPlayer";
        const string TAvProVideo = "VRC.SDK3.Video.Components.AVPro.VRCAVProVideoPlayer";
        const string TAvProScreen = "VRC.SDK3.Video.Components.AVPro.VRCAVProVideoScreen";
        const string TAvProSpeaker = "VRC.SDK3.Video.Components.AVPro.VRCAVProVideoSpeaker";
        const string TSpatialAudioBase = "VRC.SDKBase.VRC_SpatialAudioSource";
        const string TPortal = "VRC.SDK3.Components.VRCPortalMarker";
        const string TPedestal = "VRC.SDK3.Components.VRCAvatarPedestal";
        const string TUiShape = "VRC.SDK3.Components.VRCUiShape";
        const string TPhysBoneBase = "VRC.Dynamics.VRCPhysBoneBase";
        const string TPhysBoneColliderBase = "VRC.Dynamics.VRCPhysBoneColliderBase";
        const string TContactBase = "VRC.Dynamics.ContactBase";
        const string TEventHandler = "VRC.SDKBase.VRC_EventHandler";
        const string TEditorOnly = "VRC.SDKBase.IEditorOnly";
        const string TPlayerObject = "VRC.SDK3.Components.VRCPlayerObject";
        const string TOnsp = "ONSPAudioSource";
        const string TProjectSettings = "VRCProjectSettings";
        const string TUpdateLayers = "UpdateLayers";
        // Unity packages / optional modules, by name (not referenced by the core assembly).
        const string TPpLayer = "UnityEngine.Rendering.PostProcessing.PostProcessLayer";
        const string TPpVolume = "UnityEngine.Rendering.PostProcessing.PostProcessVolume";
        const string TPpDebug = "UnityEngine.Rendering.PostProcessing.PostProcessDebug";
        const string TUiGraphic = "UnityEngine.UI.Graphic";
        const string TUiText = "UnityEngine.UI.Text";
        const string TUiSelectable = "UnityEngine.UI.Selectable";
        const string TUiScrollRect = "UnityEngine.UI.ScrollRect";
        const string TUiGraphicRaycaster = "UnityEngine.UI.GraphicRaycaster";
        const string TEventSystem = "UnityEngine.EventSystems.EventSystem";
        const string TTmpText = "TMPro.TMP_Text";
        const string TTmpDropdown = "TMPro.TMP_Dropdown";
        const string TTmpInputField = "TMPro.TMP_InputField";
        const string TTextMesh = "UnityEngine.TextMesh";
        const string TVideoPlayer = "UnityEngine.Video.VideoPlayer";
        const string TCloth = "UnityEngine.Cloth";
        const string TTerrain = "UnityEngine.Terrain";
        const string TLegacyAnimation = "UnityEngine.Animation";
        // Third-party packages, by name (read from their source: iwaSync3 Assets/HoshinoLabs/iwaSync3/Editor/IwaSync3BuildProcessor.cs).
        const string TIwaSync3 = "HoshinoLabs.IwaSync3.IwaSync3";
        const string TIwaSync3Speaker = "HoshinoLabs.IwaSync3.Speaker";
        const string TIwaSync3BuildProcessor = "HoshinoLabs.IwaSync3.IwaSync3BuildProcessor";
        // NDMF's hidden preview scene (nadena.dev.ndmf.preview.NDMFPreviewSceneManager, NDMF 1.14.3).
        const string TNdmfPreviewSceneManager = "nadena.dev.ndmf.preview.NDMFPreviewSceneManager";
        const string NdmfPreviewSceneName = "___NDMF Preview___";

        /// <summary>Runs the audit. Read-only; see the class comment for arguments, codes and data keys.</summary>
        public static CctResult Run(CctArgs args)
        {
            var platform = string.IsNullOrWhiteSpace(args.GetString("platform")) ? "all" : args.GetEnum("platform", null, "all", "pc", "android", "ios");
            if (platform == null)
                return CctResult.Fail($"platform は 'all' / 'pc' / 'android' / 'ios' のいずれかを指定してください (Quest は 'android'。指定値: '{args.GetString("platform")}')", "INVALID_ARGUMENT");

            if (CctSerialized.FindType(TSceneDescriptor) == null)
            {
                var fail = CctResult.Fail("VRChat Worlds SDK (VRCSceneDescriptor) が見つかりません。ワールドプロジェクトで実行してください", "WORLDS_SDK_MISSING");
                fail.Put("avatars_sdk_present", CctSerialized.FindType(CctVrcTypes.AvatarDescriptor) != null);
                return fail;
            }

            var ctx = new Ctx
            {
                Args = args,
                Platform = platform,
                TopN = args.GetInt("top_n", 15, 1, 500),
                CheckReferences = args.GetBool("check_references", true),
                MaxReferenceScan = args.GetInt("max_reference_scan", 5000, 0, 1000000),
                CheckEditorLog = args.GetBool("check_editor_log", true),
            };
            // Bounded work for the default 30 s MCP call: the missing-reference scan stops once the whole audit has run for
            // timeout_seconds - 10 s, the UdonSharp program-asset scan after a quarter of that (both report a *_TRUNCATED info).
            int timeout = args.GetInt("timeout_seconds", 30, 5, 3600);
            ctx.RefDeadlineMs = Math.Max(3000L, (timeout - 10) * 1000L);
            ctx.UdonSharpBudgetMs = Math.Max(2000L, (timeout - 10) * 250L);
            ctx.CheckPc = platform == "all" || platform == "pc";
            ctx.CheckMobile = platform == "all" || platform == "android" || platform == "ios";

            ctx.Loaded.AddRange(CctTargets.LoadedScenes());
            if (ctx.Loaded.Count == 0)
                return CctResult.Fail("読み込まれているシーンがありません", "NO_LOADED_SCENE");
            ctx.Active = SceneManager.GetActiveScene();
            ctx.ActiveIndex = ctx.Loaded.FindIndex(s => s == ctx.Active);

            var sceneSpec = (args.GetString("scene", "active") ?? "active").Trim();
            if (sceneSpec.Length == 0 || string.Equals(sceneSpec, "active", StringComparison.OrdinalIgnoreCase))
            {
                if (ctx.ActiveIndex < 0) ctx.ActiveIndex = 0;
                ctx.ScopeIndices.Add(ctx.ActiveIndex);
                ctx.ScopeLabel = "active";
            }
            else if (string.Equals(sceneSpec, "all", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < ctx.Loaded.Count; i++) ctx.ScopeIndices.Add(i);
                ctx.ScopeLabel = "all";
            }
            else
            {
                var spec = CctPaths.Normalize(sceneSpec);
                int idx = ctx.Loaded.FindIndex(s => string.Equals(s.path, spec, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) idx = ctx.Loaded.FindIndex(s => string.Equals(s.name, spec, StringComparison.OrdinalIgnoreCase));
                if (idx < 0)
                {
                    var fail = CctResult.Fail($"シーン '{sceneSpec}' は読み込まれていません (scene は 'active' / 'all' / 読み込み中シーンのパスか名前)", "SCENE_NOT_LOADED");
                    fail.Put("loaded_scenes", ctx.Loaded.Select(s => (object)s.path).ToList());
                    return fail;
                }
                ctx.ScopeIndices.Add(idx);
                ctx.ScopeLabel = ctx.Loaded[idx].path;
            }

            var r = CctResult.Ok("");
            ctx.R = r;
            var dirtyBefore = ctx.Loaded.Select(s => s.isDirty).ToArray();
            ctx.Watch = Stopwatch.StartNew();

            if (EditorApplication.isPlayingOrWillChangePlaymode)
                ctx.Issue(CctSeverity.Warning, "PLAY_MODE", "Play モード中です。ClientSim などが実行時に作ったオブジェクトも数えています。アップロード前の状態を見るには Play を止めて再実行してください");

            Section(ctx, "scan", () => Scan(ctx));
            Section(ctx, "project", () => AuditProject(ctx));
            Section(ctx, "layers_setup", () => AuditLayerSetup(ctx));
            Section(ctx, "descriptor", () => AuditDescriptor(ctx));
            Section(ctx, "layers", () => AuditLayerUsage(ctx));
            Section(ctx, "rendering", () => AuditRendering(ctx));
            Section(ctx, "lighting", () => AuditLighting(ctx));
            Section(ctx, "cameras", () => AuditCamerasAndPostProcessing(ctx));
            Section(ctx, "mirrors", () => AuditMirrors(ctx));
            Section(ctx, "udon", () => AuditUdon(ctx));
            Section(ctx, "interactables", () => AuditInteractables(ctx));
            Section(ctx, "ui", () => AuditUi(ctx));
            Section(ctx, "components", () => AuditComponents(ctx));
            Section(ctx, "gimmicks", () => AuditGimmicks(ctx));
            Section(ctx, "android", () => AuditMobile(ctx));
            Section(ctx, "editor_log", () => AuditEditorLog(ctx));

            for (int i = 0; i < ctx.Loaded.Count && i < dirtyBefore.Length; i++)
            {
                var s = ctx.Loaded[i];
                if (s.IsValid() && s.isDirty && !dirtyBefore[i])
                    ctx.Issue(CctSeverity.Warning, "SCENE_DIRTIED", $"監査中にシーン '{s.name}' が変更済み (dirty) になりました。保存せずに内容を確認してください (このツールは書き込みを行わない設計です)", value: s.path);
            }

            PutScenes(ctx);
            PutCounts(ctx);
            PutNotes(ctx);
            Section(ctx, "verdict", () => PutVerdict(ctx));
            ctx.FinishIssues();

            var scopeName = ctx.ScopeLabel == "active" ? ctx.Active.name : ctx.ScopeLabel;
            r.Message = $"ワールド監査 ({scopeName}, {string.Join("/", ctx.CheckedPlatforms())}): " +
                        (ctx.VerdictText != null ? ctx.VerdictText + "。" : "") +
                        $"error {r.CountIssues(CctSeverity.Error)} / warning {r.CountIssues(CctSeverity.Warning)} / info {r.CountIssues(CctSeverity.Info)}。" +
                        $"記述子 {ctx.Get(K.SceneDescriptor).Count} / 三角形 {ctx.TotalTriangles:N0} / Udon {ctx.Get(K.Udon).Count(i => i.Audited)} / " +
                        $"ミラー {ctx.Get(K.Mirror).Count(i => i.Audited)} (起動時 ON {ctx.MirrorsEnabledAtStart})。ビルド前の静的な推定です";
            return r;
        }

        // ------------------------------------------------------------------ shared state

        /// <summary>One GameObject of a loaded scene.</summary>
        sealed class Obj
        {
            public GameObject Go;
            public int Scene;          // index into Ctx.Loaded
            public bool InScope;
            public bool EditorOnly;    // tagged EditorOnly (self or ancestor): Unity strips it from the build
            public bool Active;        // activeInHierarchy now (= at world start when in Edit mode)
            string _path;
            public string Path => _path ?? (_path = CctTargets.GetPath(Go));
        }

        readonly struct Item
        {
            public readonly Component C;
            public readonly Obj O;
            public Item(Component c, Obj o) { C = c; O = o; }
            public bool IsValid => O != null;
            /// <summary>Part of the content audit: in the requested scope and not stripped by the EditorOnly tag.</summary>
            public bool Audited => O != null && O.InScope && !O.EditorOnly;
        }

        sealed class Ctx
        {
            public CctArgs Args;
            public CctResult R;
            public string Platform;
            public bool CheckPc, CheckMobile, CheckReferences, CheckEditorLog;
            public int TopN, MaxReferenceScan;
            public long RefDeadlineMs, UdonSharpBudgetMs;
            public Stopwatch Watch;

            public readonly List<Scene> Loaded = new List<Scene>();
            public Scene Active;
            public int ActiveIndex = -1;
            public readonly HashSet<int> ScopeIndices = new HashSet<int>();
            public string ScopeLabel;

            public readonly List<Obj> Objects = new List<Obj>(4096);
            public readonly Dictionary<GameObject, Obj> ObjByGo = new Dictionary<GameObject, Obj>(4096);
            public readonly List<Item> Components = new List<Item>(8192);
            public readonly List<KeyValuePair<Obj, int>> MissingScripts = new List<KeyValuePair<Obj, int>>();
            public readonly Dictionary<Type, int> AuditedTypeCounts = new Dictionary<Type, int>();
            public int HiddenSkipped;
            readonly Dictionary<K, List<Item>> _buckets = new Dictionary<K, List<Item>>();
            static readonly List<Item> Empty = new List<Item>();

            // Filled by sections and read by later ones.
            public Item Descriptor;
            public GameObject ReferenceCamera;
            public float RespawnHeightY = -100f;
            public int[] ExpectedCollisionRows;      // [layer] -> bit mask of layers it collides with (0..21), null if unknown
            public long TotalTriangles, ActiveTriangles;
            public int MirrorsEnabledAtStart;
            public readonly Dictionary<Component, int> UdonSyncType = new Dictionary<Component, int>(); // effective Networking.SyncType
            public readonly HashSet<Component> UdonNoVariableSync = new HashSet<Component>();
            public readonly HashSet<Component> UdonPositionSync = new HashSet<Component>();      // obsolete SynchronizePosition
            public readonly List<RendererInfo> Renderers = new List<RendererInfo>();
            public readonly Dictionary<Material, MaterialInfo> Materials = new Dictionary<Material, MaterialInfo>();
            public readonly Dictionary<Texture, TextureInfo> Textures = new Dictionary<Texture, TextureInfo>();

            // Missing-reference scan state.
            public readonly HashSet<Component> RefScanned = new HashSet<Component>();
            public readonly List<Dictionary<string, object>> MissingReferences = new List<Dictionary<string, object>>();
            public int RefScanCount, RefScanSkipped, MissingReferenceTotal;
            public Stopwatch RefWatch;

            public readonly HashSet<string> MissingFields = new HashSet<string>(StringComparer.Ordinal); // "Type|field" reported once
            public readonly List<PackageEntry> Packages = new List<PackageEntry>();               // registered UPM/VPM packages
            public string VerdictText;                                                             // first sentence of the message

            readonly Dictionary<string, int> _issueCounts = new Dictionary<string, int>();

            /// <summary>Items of one category, or the distinct union when several flags are OR-ed (buckets hold single flags).</summary>
            public List<Item> Get(K kind)
            {
                if (_buckets.TryGetValue(kind, out var l)) return l;
                ulong bits = (ulong)kind;
                if (bits == 0 || (bits & (bits - 1)) == 0) return Empty;
                var result = new List<Item>();
                var seen = new HashSet<Component>();
                for (int bit = 0; bit < 64; bit++)
                {
                    var k = (K)(1UL << bit);
                    if ((kind & k) == 0 || !_buckets.TryGetValue(k, out var bucket)) continue;
                    foreach (var i in bucket)
                        if (seen.Add(i.C)) result.Add(i);
                }
                return result;
            }

            public IEnumerable<Item> Audited(K kind) => Get(kind).Where(i => i.Audited);

            public void Add(K kinds, Item item)
            {
                // Called once per component of every loaded scene: visit only the set bits (usually 0-2).
                for (ulong bits = (ulong)kinds; bits != 0; bits &= bits - 1)
                {
                    var k = (K)(bits & (~bits + 1));
                    if (!_buckets.TryGetValue(k, out var list)) _buckets[k] = list = new List<Item>();
                    list.Add(item);
                }
            }

            public IEnumerable<string> CheckedPlatforms()
            {
                if (CheckPc) yield return "pc";
                if (Platform == "all" || Platform == "android") yield return "android";
                if (Platform == "all" || Platform == "ios") yield return "ios";
            }

            /// <summary>Adds an issue; after <see cref="PerCodeIssueCap"/> of one code the rest are only counted.</summary>
            public void Issue(string severity, string code, string message, string path = null, object value = null,
                object limit = null, string asset = null)
            {
                _issueCounts.TryGetValue(code, out var n);
                _issueCounts[code] = n + 1;
                if (n >= PerCodeIssueCap) return;
                R.AddIssue(severity, code, message, path, value, limit, asset);
            }

            /// <summary>Issues of this code so far, including the ones beyond the per-code cap.</summary>
            public int IssueCount(string code) => _issueCounts.TryGetValue(code, out var n) ? n : 0;

            public void FinishIssues()
            {
                foreach (var kv in _issueCounts.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (kv.Value <= PerCodeIssueCap) continue;
                    R.AddIssue(CctSeverity.Info, "ISSUES_TRUNCATED",
                        $"{kv.Key} は {kv.Value} 件あります。先頭 {PerCodeIssueCap} 件だけ issues に載せ、残り {kv.Value - PerCodeIssueCap} 件は省略しました (該当セクションの件数を参照)",
                        value: new Dictionary<string, object> { ["code"] = kv.Key, ["total"] = kv.Value, ["omitted"] = kv.Value - PerCodeIssueCap });
                }
            }
        }

        sealed class PackageEntry
        {
            public string Name, DisplayName, Version;
        }

        sealed class RendererInfo
        {
            public Renderer Renderer;
            public Item Item;
            public bool Active;          // GameObject active and renderer enabled
            public Mesh Mesh;
            public long Triangles;
            public Material[] Materials;
            public StaticEditorFlags Flags;
        }

        sealed class MaterialInfo
        {
            public Material Material;
            public string Shader;
            public int Renderers;
            public string AssetPath;
        }

        sealed class TextureInfo
        {
            public Texture Texture;
            public string AssetPath;
            public TextureImporter Importer;
            public int Width, Height, Materials;
            public long Bytes = -1;
        }

        static void Section(Ctx ctx, string name, Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                ctx.Issue(CctSeverity.Warning, "SECTION_FAILED",
                    $"'{name}' の検査中に例外が発生したため、この項目の結果は不完全です: {e.GetType().Name}: {e.Message}",
                    value: new Dictionary<string, object> { ["section"] = name, ["stack"] = Trim(e.StackTrace, 1200) });
            }
        }

        // ------------------------------------------------------------------ output helpers

        static void PutScenes(Ctx ctx)
        {
            var list = new List<object>();
            var extraWithContent = new List<string>();
            var extraEmpty = new List<string>();
            for (int i = 0; i < ctx.Loaded.Count; i++)
            {
                var s = ctx.Loaded[i];
                bool ndmfPreview = IsNdmfPreviewScene(s);
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = s.name,
                    ["path"] = s.path,
                    ["active"] = i == ctx.ActiveIndex,
                    ["in_scope"] = ctx.ScopeIndices.Contains(i),
                    ["is_dirty"] = s.isDirty,
                    ["root_count"] = s.rootCount,
                    ["gameobjects"] = ctx.Objects.Count(o => o.Scene == i),
                    ["editor_only_gameobjects"] = ctx.Objects.Count(o => o.Scene == i && o.EditorOnly),
                    ["ndmf_preview"] = ndmfPreview,
                });
                if (ctx.ScopeIndices.Contains(i) && s.isDirty)
                    ctx.Issue(CctSeverity.Info, "SCENE_UNSAVED", $"シーン '{s.name}' に未保存の変更があります。SDK はビルド前に開いているシーンを保存します", value: s.path);
                if (i == ctx.ActiveIndex || ndmfPreview) continue;
                var content = SceneContent(ctx, i);
                (content != null ? extraWithContent : extraEmpty).Add(content != null ? $"{s.path} ({content})" : s.path);
            }
            ctx.R.Put("scenes", new Dictionary<string, object>
            {
                ["scope"] = ctx.ScopeLabel,
                ["uploaded_scene"] = ctx.Active.IsValid() ? ctx.Active.path : null,
                ["loaded"] = list,
                ["hidden_objects_skipped"] = ctx.HiddenSkipped,
            });
            // NDMF's preview scene is reopened by NDMF itself (NDMFPreviewSceneManager.cs L99-100, 144) and holds no world content,
            // so it is never counted. The SDK has no multi-scene check of its own; it only counts descriptors / PipelineManagers
            // over every loaded scene (VRC.Tools.FindSceneObjectsOfTypeAll) and exports the active scene.
            if (extraWithContent.Count > 0)
                ctx.Issue(CctSeverity.Warning, "MULTIPLE_SCENES_LOADED",
                    $"アクティブシーン '{ctx.Active.name}' のほかに、中身のあるシーンが {extraWithContent.Count} 個読み込まれています: {string.Join(", ", extraWithContent.Take(5))}。SDK は記述子・PipelineManager などを読み込み中の全シーンで数えますが、ビルドされるのはアクティブシーンだけです。アップロード時は対象シーンだけを開いてください",
                    value: extraWithContent.Cast<object>().ToList());
            else if (extraEmpty.Count > 0)
                ctx.Issue(CctSeverity.Info, "MULTIPLE_SCENES_LOADED",
                    $"アクティブシーンのほかに、空のシーン (オブジェクト 0 個) が {extraEmpty.Count} 個読み込まれています。ビルドされるのはアクティブシーンだけなので影響はありません",
                    value: extraEmpty.Cast<object>().ToList());

            ctx.R.Put("platforms", new Dictionary<string, object>
            {
                ["requested"] = ctx.Platform,
                ["checked"] = ctx.CheckedPlatforms().Cast<object>().ToList(),
                ["active_build_target"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["active_target_is_mobile"] = IsMobileTarget(EditorUserBuildSettings.activeBuildTarget),
            });
        }

        static void PutCounts(Ctx ctx)
        {
            int Count(K k) => ctx.Get(k).Count(i => i.Audited);
            ctx.R.Put("counts", new Dictionary<string, object>
            {
                ["gameobjects"] = ctx.Objects.Count(o => o.InScope && !o.EditorOnly),
                ["components"] = ctx.Components.Count(i => i.Audited),
                ["renderers"] = Count(K.Renderer),
                ["triangles"] = ctx.TotalTriangles,
                ["triangles_active"] = ctx.ActiveTriangles,
                ["materials"] = ctx.Materials.Count,
                ["textures"] = ctx.Textures.Count,
                ["lights"] = Count(K.Light),
                ["cameras"] = Count(K.Camera),
                ["mirrors"] = Count(K.Mirror),
                ["udon_behaviours"] = Count(K.Udon),
                ["pickups"] = Count(K.Pickup),
                ["stations"] = Count(K.Station),
                ["object_syncs"] = Count(K.ObjectSync),
                ["video_players"] = Count(K.UnityVideo) + Count(K.AvProVideo),
                ["audio_sources"] = Count(K.AudioSource),
                ["physbones"] = Count(K.PhysBone),
                ["contacts"] = Count(K.Contact),
                ["elapsed_scan_ms"] = ctx.Watch?.ElapsedMilliseconds ?? 0,
            });
        }

        static void PutNotes(Ctx ctx)
        {
            ctx.R.Put("not_checked", new List<object>
            {
                "SDK の Build / Build & Test / アップロードそのもの (バンドルサイズ・Udon 署名・Network ID の割当エラーは実ビルドでのみ確定)",
                "UdonSharp のコンパイル結果そのもの (Editor.log の末尾とコンパイル失敗フラグだけを見ています)",
                "ClientSim・VRChat 実機 (PC / Android / iOS) での動作、同期・オーナー移譲の実挙動",
                "VRChat サーバー側の設定 (Video Player Allowed Domains、Items、World Debugging、名前・サムネイル・権限・著作権同意)",
                "アカウント側の条件 (アップロード権限 = Trust Rank New User 以上、iOS ビルドを許可されたアカウントか): SDK が API で確認します",
                "クライアントが実際に除去するコンポーネント (SDK はビルド時に許可リストを検査しないため、許可リスト判定は推定)",
                "フレームレート (公開審査の目安: スポーン地点で VR 1 人 45 FPS 以上)",
            });
            ctx.R.Put("notes", new List<object>
            {
                "ビルド前の静的な推定です。SDK と同じ条件で判定できる項目は issue の説明に SDK と書いています",
                "記述子と PipelineManager の数、VRC_EventHandler、ObjectSync の同居、PhysBone 上限は SDK と同じく読み込み中の全シーンで判定し、それ以外は scene の範囲 (既定はアクティブシーン) を見ています",
                "EditorOnly タグ (自身か親) の付いたオブジェクトは Unity がビルド時に除去するため、内容の集計から除いています",
                "三角形数はメッシュのインデックス数からの概算です (Terrain・パーティクル・UI は含みません)",
            });
        }

        /// <summary>What a non-active loaded scene holds ("記述子 1 / オブジェクト 12 個"), or null when it has no GameObjects.</summary>
        static string SceneContent(Ctx ctx, int sceneIndex)
        {
            int objects = ctx.Objects.Count(o => o.Scene == sceneIndex);
            if (objects == 0) return null;
            int descriptors = ctx.Get(K.SceneDescriptor).Count(i => i.O != null && i.O.Scene == sceneIndex);
            int pipelines = ctx.Get(K.PipelineManager).Count(i => i.O != null && i.O.Scene == sceneIndex);
            var parts = new List<string>();
            if (descriptors > 0) parts.Add($"記述子 {descriptors}");
            if (pipelines > 0) parts.Add($"PipelineManager {pipelines}");
            parts.Add($"オブジェクト {objects} 個");
            return string.Join(" / ", parts);
        }

        static System.Reflection.MethodInfo _ndmfIsPreviewScene;
        static bool _ndmfLooked;

        /// <summary>NDMF's hidden preview scene: its name, or NDMFPreviewSceneManager.IsPreviewScene(Scene) when NDMF has it.</summary>
        static bool IsNdmfPreviewScene(Scene s)
        {
            if (s.name == NdmfPreviewSceneName) return true;
            if (!_ndmfLooked)
            {
                _ndmfLooked = true;
                try
                {
                    _ndmfIsPreviewScene = CctSerialized.FindType(TNdmfPreviewSceneManager)?.GetMethod("IsPreviewScene",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                        null, new[] { typeof(Scene) }, null);
                }
                catch (Exception) { _ndmfIsPreviewScene = null; }
            }
            if (_ndmfIsPreviewScene == null || _ndmfIsPreviewScene.ReturnType != typeof(bool)) return false;
            try { return (bool)_ndmfIsPreviewScene.Invoke(null, new object[] { s }); }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------ small helpers

        static bool IsMobileTarget(BuildTarget t) => t == BuildTarget.Android || t == BuildTarget.iOS;

        /// <summary>SDK excludes objects whose hideFlags equal NotEditable or HideAndDontSave ([WB] GatherComponentsOfTypeInScene).</summary>
        static bool IsSdkExcluded(HideFlags f) => f == HideFlags.NotEditable || f == HideFlags.HideAndDontSave;

        static string LayerName(int layer)
        {
            var n = layer >= 0 && layer < 32 ? LayerMask.LayerToName(layer) : null;
            return string.IsNullOrEmpty(n) ? "#" + layer.ToString(CultureInfo.InvariantCulture) : n;
        }

        static object MaskToNames(int mask)
        {
            if (mask == -1) return "Everything";
            if (mask == 0) return "Nothing";
            var names = new List<object>();
            for (int i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0) names.Add(LayerName(i));
            return names;
        }

        static List<object> Examples(IEnumerable<string> paths, int cap = ExampleCap) =>
            paths.Where(p => p != null).Take(cap).Cast<object>().ToList();

        static Dictionary<string, object> Grouped(int count, IEnumerable<string> paths) => new Dictionary<string, object>
        {
            ["count"] = count,
            ["examples"] = Examples(paths),
        };

        static double Round(double v, int digits = 2) => Math.Round(v, digits);

        static double[] Vec(Vector3 v) => new[] { Round(v.x, 3), Round(v.y, 3), Round(v.z, 3) };

        static string Trim(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max) + "...";

        static double Percent(int part, int total) => total <= 0 ? 0 : Round(100.0 * part / total, 1);

        static string AssetPathOf(Object o) => o == null ? null : NullIfEmpty(AssetDatabase.GetAssetPath(o));

        static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        /// <summary>Component's own path plus its type, for issue paths.</summary>
        static string PathOf(Item i) => i.O?.Path;

        static Dictionary<string, object> DescribeItem(Item i) => new Dictionary<string, object>
        {
            ["path"] = i.O?.Path,
            ["scene"] = i.O != null && i.C != null ? i.C.gameObject.scene.path : null,
            ["instance_id"] = i.C != null ? i.C.GetInstanceID() : 0,
            ["active"] = i.O != null && i.O.Active,
        };

        /// <summary>Is a Behaviour enabled (non-Behaviour components count as enabled)?</summary>
        static bool Enabled(Component c) => !(c is Behaviour b) || b.enabled;

        /// <summary>SerializedObject access that never throws into the caller (missing scripts, odd custom serializers).</summary>
        static SerializedObject TrySerialize(Ctx ctx, Component c, string what)
        {
            if (c == null) return null;
            try { return new SerializedObject(c); }
            catch (Exception e)
            {
                ctx.Issue(CctSeverity.Info, "SERIALIZE_FAILED", $"{what} を SerializedObject で読めませんでした: {e.GetType().Name}: {e.Message}",
                    path: CctTargets.GetPath(c.transform), value: c.GetType().FullName);
                return null;
            }
        }

        /// <summary>
        /// True when every serialized property exists. Otherwise (a different SDK version renamed/removed a field) reports
        /// SDK_FIELD_NOT_FOUND once per type/field and returns false, so the caller skips the check instead of treating the
        /// missing field as an empty value (which would turn into false errors such as NO_SPAWNS or UDON_NO_PROGRAM).
        /// </summary>
        static bool HasProps(Ctx ctx, SerializedObject so, Object target, params string[] names)
        {
            bool ok = true;
            foreach (var n in names)
            {
                if (so.FindProperty(n) != null) continue;
                ok = false;
                var typeName = target != null ? target.GetType().FullName : "?";
                if (ctx.MissingFields.Add(typeName + "|" + n))
                    ctx.Issue(CctSeverity.Info, "SDK_FIELD_NOT_FOUND",
                        $"{(target != null ? target.GetType().Name : "?")} にプロパティ '{n}' がありません。SDK のバージョンが想定 (3.10.5) と違う可能性があるため、この値を使う判定を省略しました",
                        value: typeName + "|" + n);
            }
            return ok;
        }

        static SerializedObject TrySerializeAsset(Object o)
        {
            if (o == null) return null;
            try { return new SerializedObject(o); }
            catch (Exception) { return null; }
        }

        static string FindTypeName(Type t) => t == null ? null : t.FullName ?? t.Name;
    }
}
