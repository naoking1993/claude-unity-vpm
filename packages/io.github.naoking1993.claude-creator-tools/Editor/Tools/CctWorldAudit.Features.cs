using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    public static partial class CctWorldAudit
    {
        // VRC.SDKBase.Networking+SyncType (FieldCheck enum, VRCSDKBase.dll 3.10.5): Unknown=0, None=1, Continuous=2, Manual=3.
        // UdonBehaviour.SyncMethod resolves Unknown (old scenes) as Reliable ? Manual : Continuous (Runtime/Udon/UdonBehaviour.cs L73-85).
        const int SyncUnknown = 0, SyncNone = 1, SyncContinuous = 2, SyncManual = 3;
        static readonly string[] SyncNames = { "Unknown", "None", "Continuous", "Manual" };
        // UdonSharp.BehaviourSyncMode (Integrations/UdonSharp/Runtime/UdonSharpAttributes.cs L49-71): Any, None, NoVariableSync, Continuous, Manual.
        const int USharpNoVariableSync = 2;
        static readonly string[] BehaviourSyncModeNames = { "Any", "None", "NoVariableSync", "Continuous", "Manual" };

        // VRC_Pickup enums (FieldCheck dump): AutoHoldMode AutoDetect=0, Sometimes=1, No=2, Yes=3; PickupOrientation Any=0, Grip=1,
        // Gun=2; VRCPickup.Version Version_1_0=0, Version_1_1=1. VRCStation.Mobility Mobile=0, Immobilize=1, ImmobilizeForVehicle=2.
        // VRC_PortalMarker.VRChatWorld None=0, Hub=1, Home=2.
        static readonly string[] AutoHoldNames = { "AutoDetect", "Sometimes", "No", "Yes" };
        static readonly string[] OrientationNames = { "Any", "Grip", "Gun" };
        static readonly string[] PickupVersionNames = { "Version_1_0", "Version_1_1" };
        static readonly string[] MobilityNames = { "Mobile", "Immobilize", "ImmobilizeForVehicle" };
        // VRCUnityVideoPlayer private enum VideoRenderMode { RenderTexture, MaterialOverride } (decompiled reference only).
        const int VideoRenderModeRenderTexture = 0;

        // "Bad things happen when you put more than 2 video players in a room" / "more than 1 mirror"
        // (docs/worlds/submitting-a-world-to-be-made-public.md L42-44).
        const int MaxRecommendedVideoPlayers = 2;
        // CollisionScene.MAX_SHAPES_WORLD = 0x400 (VRC.Dynamics.dll 3.10.5), used by [WB] L599-612.
        const int MaxWorldShapes = 1024;

        // Video URL allowlist (docs/worlds/udon/video-players/www-whitelist.md L26-40, snapshot 2026-10-20).
        static readonly string[] VideoAllowedHosts =
        {
            "vod-progressive.akamaized.net", "*.facebook.com", "*.fbcdn.net", "*.googlevideo.com", "*.hyperbeam.com", "*.hyperbeam.dev",
            "*.mixcloud.com", "*.nicovideo.jp", "soundcloud.com", "*.sndcdn.com", "*.topaz.chat", "*.twitch.tv", "*.ttvnw.net",
            "*.twitchcdn.net", "*.vrcdn.live", "*.vrcdn.video", "*.vrcdn.cloud", "*.vimeo.com", "*.youku.com", "*.youtube.com", "youtu.be",
        };

        static readonly HashSet<string> PrimitiveMeshNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad",
        };
        // Built-in primitive meshes live in this pseudo asset path. Checking it replaces the SDK's GameObject.CreatePrimitive
        // (which would add objects to the scene).
        const string BuiltinResourcesPath = "Library/unity default resources";

        // ------------------------------------------------------------------ mirrors

        static void AuditMirrors(Ctx ctx)
        {
            var mirrors = new Dictionary<string, object>();
            ctx.R.Put("mirrors", mirrors);
            var items = new List<object>();
            var enabledPaths = new List<string>();
            int count = 0;
            foreach (var item in ctx.Audited(K.Mirror))
            {
                count++;
                var go = item.C.gameObject;
                var renderer = go.GetComponent<Renderer>();
                Material mat = renderer != null ? renderer.sharedMaterial : null;
                bool on = item.O.Active && Enabled(item.C) && renderer != null && renderer.enabled;
                var entry = new Dictionary<string, object>
                {
                    ["path"] = item.O.Path,
                    ["enabled_at_start"] = on,
                    ["layer"] = LayerName(go.layer),
                };

                // VRC_MirrorReflection.TrySetupMaterial: no Renderer or no sharedMaterial -> the mirror disables itself in Start.
                if (renderer == null || mat == null)
                    ctx.Issue(CctSeverity.Warning, "MIRROR_NO_RENDERER",
                        renderer == null
                            ? "VRC Mirror Reflection と同じ GameObject に Renderer がありません。ミラーは開始時に自分を無効化し、何も映りません"
                            : "ミラーの Renderer にマテリアルがありません。ミラーは開始時に自分を無効化し、何も映りません",
                        path: item.O.Path);

                using (var so = TrySerialize(ctx, item.C, "VRC Mirror Reflection"))
                {
                    if (so != null)
                    {
                        int mask = CctSerialized.GetInt(so.FindProperty("m_ReflectLayers"), -1);
                        // At runtime: Start() adds HardwareObjects; the mirror camera masks out Water and PlayerLocal (research 3.4).
                        int effective = (mask | (1 << LayerHardwareObjects)) & ~(1 << 4) & ~(1 << LayerPlayerLocal);
                        entry["reflect_layers"] = MaskToNames(mask);
                        entry["reflects_players"] = (effective & (1 << 9)) != 0;
                        entry["reflects_local_player"] = (effective & (1 << LayerMirrorReflection)) != 0;
                        int res = CctSerialized.GetInt(so.FindProperty("mirrorResolution"));
                        entry["resolution"] = res == 0 ? "Auto" : "X" + res;
                        entry["maximum_antialiasing"] = "X" + CctSerialized.GetInt(so.FindProperty("maximumAntialiasing"), 4);
                        var shader = CctSerialized.GetObject(so.FindProperty("customShader")) as Shader;
                        entry["custom_shader"] = shader != null ? shader.name : null;
                        entry["disable_pixel_lights"] = CctSerialized.GetBool(so.FindProperty("m_DisablePixelLights"), true);
                        entry["turn_off_mirror_occlusion"] = CctSerialized.GetBool(so.FindProperty("TurnOffMirrorOcclusion"), true);

                        // Everything, or Everything minus the layers the mirror camera always drops (Water, PlayerLocal).
                        bool everything = (mask | (1 << 4) | (1 << LayerPlayerLocal)) == -1;
                        if (everything)
                            ctx.Issue(CctSeverity.Info, "MIRROR_REFLECT_EVERYTHING",
                                "ミラーの Reflect Layers が Everything です。docs は全レイヤーを映さず、必要なレイヤーだけ (例: Player, MirrorReflection, 必要なら Default/Environment/Pickup) にするよう勧めています",
                                path: item.O.Path);
                        else if ((mask & (1 << LayerUiMenu)) != 0)
                            ctx.Issue(CctSeverity.Info, "MIRROR_REFLECT_UIMENU",
                                "ミラーの Reflect Layers に UiMenu が入っています。VRChat のメニューがミラーにも描画されて重くなります (コミュニティ知見)",
                                path: item.O.Path);
                        if ((mask & (1 << LayerMirrorReflection)) == 0)
                            ctx.Issue(CctSeverity.Info, "MIRROR_NO_LOCAL_PLAYER",
                                "ミラーの Reflect Layers に MirrorReflection が入っていないため、自分の姿が映りません (意図どおりか確認)",
                                path: item.O.Path);
                    }
                }

                // The mirror renders only when its renderer's layer is in the current camera's culling mask (VRC_MirrorReflection
                // CameraOnPreCull / IsLayerRendered). MirrorReflection is "not rendered in VRChat's main camera" (docs layers.md L37).
                // The Reference Camera's culling mask is NOT used for the player camera: ClientSim's CopyCameraValues copies only
                // far/near/clearFlags/backgroundColor/allowHDR and the PostProcessLayer (ClientSimSceneManager.cs L150-188), and
                // the main camera's mask is changed only through Udon (VRCCameraSettings.CullingMask, docs vrc-camera-settings.md
                // L70). The UI layer is NOT hidden from the main view: docs L24 says only VRChat's (photo) camera ignores it
                // unless 'UI' is enabled there, so a mirror on UI is visible in the world but not in photos (not reported).
                if (go.layer == LayerMirrorReflection)
                    ctx.Issue(CctSeverity.Warning, "MIRROR_LAYER_NOT_RENDERED",
                        $"ミラー本体が MirrorReflection レイヤー ({LayerMirrorReflection}) にあります。このレイヤーの Renderer は VRChat のメインカメラに描画されないため、ミラー自体が表示されません。VRCMirror プレハブと同じ Water などにしてください",
                        path: item.O.Path, value: LayerName(go.layer));

                if (on) enabledPaths.Add(item.O.Path);
                if (items.Count < ctx.TopN) items.Add(entry);
            }
            ctx.MirrorsEnabledAtStart = enabledPaths.Count;
            foreach (var p in enabledPaths)
                ctx.Issue(CctSeverity.Warning, "MIRROR_ENABLED_AT_START",
                    "ミラーが最初から有効です。docs はミラーを既定で OFF にし、トグルか近づいたときに ON にすることを強く勧めています",
                    path: p);
            if (enabledPaths.Count >= 2)
                ctx.Issue(CctSeverity.Warning, "MIRROR_MULTIPLE_ENABLED",
                    $"開始時に有効なミラーが {enabledPaths.Count} 枚あります。docs は 1 つの空間に 2 枚以上のミラーを置くと性能が大きく落ちると案内しています",
                    path: enabledPaths[0], value: Grouped(enabledPaths.Count, enabledPaths), limit: 1);
            mirrors["count"] = count;
            mirrors["enabled_at_start"] = enabledPaths.Count;
            mirrors["items"] = items;
            if (count > items.Count) mirrors["items_omitted"] = count - items.Count;
        }

        // ------------------------------------------------------------------ udon

        sealed class ProgramStats
        {
            public Object Program;
            public string Type;
            public int Behaviours;
            public readonly Dictionary<string, object> Sync = new Dictionary<string, object>();
        }

        static void AuditUdon(Ctx ctx)
        {
            var udon = new Dictionary<string, object>();
            ctx.R.Put("udon", udon);
            var programs = new Dictionary<Object, ProgramStats>();
            var usharpSyncModes = new Dictionary<Object, int>();
            var positionSync = ctx.UdonPositionSync;
            var syncCounts = new Dictionary<string, object>();
            int audited = 0, withProgram = 0;
            var noProgram = new List<string>();
            var missingProgram = new List<string>();
            var sourceMissingCompiled = new List<string>();
            var notCompiled = new List<string>();
            var builtinMesh = new List<string>();
            var sourceCompiled = new Dictionary<Object, bool>();

            // All loaded scenes (ObjectSync conflicts are checked by the SDK over every loaded scene).
            foreach (var item in ctx.Get(K.Udon))
            {
                using (var so = TrySerialize(ctx, item.C, "UdonBehaviour"))
                {
                    if (so == null) continue;
                    // Without these fields (other SDK version) the sync/program checks would invent Continuous / "no program".
                    bool syncKnown = HasProps(ctx, so, item.C, "_syncMethod");
                    bool programKnown = HasProps(ctx, so, item.C, "programSource");
                    int raw = CctSerialized.GetInt(so.FindProperty("_syncMethod"));
                    int sync = raw == SyncUnknown ? (CctSerialized.GetBool(so.FindProperty("Reliable")) ? SyncManual : SyncContinuous) : raw;
                    if (syncKnown) ctx.UdonSyncType[item.C] = sync;
                    if (CctSerialized.GetBool(so.FindProperty("SynchronizePosition"))) positionSync.Add(item.C);

                    var programProp = so.FindProperty("programSource");
                    var program = CctSerialized.GetObject(programProp);
                    if (program != null && program.GetType().Name == "UdonSharpProgramAsset")
                    {
                        if (!usharpSyncModes.TryGetValue(program, out var mode))
                        {
                            using (var pso = TrySerializeAsset(program))
                                mode = pso != null ? CctSerialized.GetInt(pso.FindProperty("behaviourSyncMode")) : 0;
                            usharpSyncModes[program] = mode;
                        }
                        if (mode == USharpNoVariableSync) ctx.UdonNoVariableSync.Add(item.C);
                    }

                    if (!item.Audited) continue;
                    audited++;
                    Inc(syncCounts, syncKnown ? EnumName(SyncNames, sync) : "Unknown (field not found)");
                    // serializedProgramAsset is what VRChat runs; programSource exists only in the editor (#if UNITY_EDITOR &&
                    // !VRC_CLIENT, Runtime/Udon/UdonBehaviour.cs L124-129). UdonSharpEditorManager.RepairProgramAssetLinks
                    // (L1320-1378) relinks a lost programSource from it and only calls a behaviour with neither "Empty".
                    bool compiledKnown = HasProps(ctx, so, item.C, "serializedProgramAsset");
                    bool hasCompiled = compiledKnown && CctSerialized.GetObject(so.FindProperty("serializedProgramAsset")) != null;
                    if (!programKnown) { /* reported as SDK_FIELD_NOT_FOUND */ }
                    else if (program == null)
                    {
                        if (hasCompiled) sourceMissingCompiled.Add(item.O.Path);
                        else if (CctSerialized.IsMissingReference(programProp)) missingProgram.Add(item.O.Path);
                        else noProgram.Add(item.O.Path);
                    }
                    else
                    {
                        withProgram++;
                        if (!programs.TryGetValue(program, out var ps))
                            programs[program] = ps = new ProgramStats { Program = program, Type = program.GetType().Name };
                        ps.Behaviours++;
                        Inc(ps.Sync, EnumName(SyncNames, sync));
                        // A stale behaviour-side reference is relinked from the program source when the scene is opened or
                        // saved (UdonEditorManager.PopulateSerializedProgramAssetReference, L294-345; the SDK saves the open
                        // scenes before building), so only a program source without its own compiled asset is reported.
                        if (compiledKnown && !hasCompiled && !ProgramSourceHasCompiledAsset(program, sourceCompiled)) notCompiled.Add(item.O.Path);
                    }

                    // [WB] ShouldShowPrimitivesWarning: public variables of ACTIVE UdonBehaviours referencing builtin primitive meshes.
                    // Read from the serialized Unity-object list instead of publicVariables (its getter can throw in the editor).
                    if (item.O.Active)
                        foreach (var el in CctSerialized.Elements(so.FindProperty("publicVariablesUnityEngineObjects")))
                        {
                            if (!(CctSerialized.GetObject(el) is Mesh mesh)) continue;
                            if (PrimitiveMeshNames.Contains(mesh.name) && AssetPathOf(mesh) == BuiltinResourcesPath)
                            {
                                builtinMesh.Add(item.O.Path);
                                break;
                            }
                        }

                    // Reuse this SerializedObject for the missing-reference scan: creating one runs UdonBehaviour.OnBeforeSerialize
                    // (Odin-serializes the public variable table), which is the expensive part on worlds with many behaviours.
                    ScanMissingReferencesWithinBudget(ctx, item, so);
                }
            }

            foreach (var p in noProgram)
                ctx.Issue(CctSeverity.Error, "UDON_NO_PROGRAM",
                    "UdonBehaviour に Program Source もコンパイル済みプログラムもありません。何も実行されません (U# スクリプトや Udon Graph を割り当てるか、不要なら削除してください)",
                    path: p);
            foreach (var p in missingProgram)
                ctx.Issue(CctSeverity.Error, "UDON_PROGRAM_MISSING_REFERENCE",
                    "UdonBehaviour の Program Source が削除済みのアセットを指していて、コンパイル済みプログラムもありません。何も実行されません。U# のプログラムアセット (.asset) やスクリプトが消えていないか確認してください",
                    path: p);
            foreach (var p in sourceMissingCompiled)
                ctx.Issue(CctSeverity.Warning, "UDON_PROGRAM_SOURCE_MISSING",
                    "UdonBehaviour の Program Source が空 (または削除済み) ですが、コンパイル済みプログラムが残っているため、VRChat では最後にコンパイルした内容のまま動きます。スクリプトを直しても反映されないので、元の U# スクリプトのプログラムアセットか Udon Graph を割り当て直してください (U# は対応するアセットが見つかれば自動で付け直します)",
                    path: p);
            if (notCompiled.Count > 0)
                ctx.Issue(CctSeverity.Warning, "UDON_PROGRAM_NOT_COMPILED",
                    $"Program Source はありますが、そのプログラムアセットにコンパイル結果 (Assets/SerializedUdonPrograms) が無い UdonBehaviour が {notCompiled.Count} 個あります。U# のコンパイルエラーか未コンパイルの可能性があります (VRChat SDK > Udon Sharp > Compile All UdonSharp Programs)",
                    path: notCompiled[0], value: Grouped(notCompiled.Count, notCompiled));
            if (builtinMesh.Count > 0)
                ctx.Issue(CctSeverity.Warning, "UDON_BUILTIN_MESH_REFERENCE",
                    $"Udon の public 変数が Unity 組み込みのプリミティブ Mesh (Cube など) を参照しているオブジェクトが {builtinMesh.Count} 個あります。VRChat では動かないため Mesh を複製して使ってください (SDK のコードにある警告。パネルには通常表示されません)",
                    path: builtinMesh[0], value: Grouped(builtinMesh.Count, builtinMesh));

            // UdonSharp warns about mixed sync methods on one GameObject (UdonSharpEditorManager.UpdateSyncModes L1441-1480).
            var byObject = ctx.Audited(K.Udon).GroupBy(i => i.C.gameObject);
            foreach (var g in byObject)
            {
                bool manual = false, continuous = false, posSync = false;
                foreach (var i in g)
                {
                    if (ctx.UdonNoVariableSync.Contains(i.C)) continue;
                    if (!ctx.UdonSyncType.TryGetValue(i.C, out var s)) continue;
                    if (s == SyncManual) manual = true;
                    else if (s == SyncContinuous) continuous = true;
                    if (positionSync.Contains(i.C)) posSync = true;
                }
                if (manual && (continuous || posSync))
                    ctx.Issue(CctSeverity.Warning, "UDON_SYNC_CONFLICT",
                        continuous
                            ? "同じ GameObject に Manual 同期と Continuous 同期の UdonBehaviour が混在しています。同期が期待どおりに動かないことがあります (UdonSharp の警告と同じ)"
                            : "Manual 同期の UdonBehaviour と、位置同期 (Synchronize Position) が有効な UdonBehaviour が同じ GameObject にあります (UdonSharp の警告と同じ)",
                        path: g.First().O.Path);
            }

            // [WB] L868-876: VRCObjectSync with a Manual UdonBehaviour / an object pool on the same GameObject (all loaded scenes).
            foreach (var os in ctx.Get(K.ObjectSync))
            {
                var go = os.C.gameObject;
                if (ComponentsOfKind(go, K.Udon).Any(ub => ctx.UdonSyncType.TryGetValue(ub, out var s) && s == SyncManual))
                    ctx.Issue(CctSeverity.Error, "OBJECT_SYNC_MANUAL_UDON",
                        "VRC Object Sync と Manual 同期の UdonBehaviour が同じ GameObject にあります。SDK はこの状態ではビルドしません。UdonBehaviour を Continuous/None にするか、別の GameObject に分けてください",
                        path: os.O.Path);
                if (HasComponentOfKind(ctx, go, K.ObjectPool))
                    ctx.Issue(CctSeverity.Error, "OBJECT_SYNC_WITH_POOL",
                        "VRC Object Sync と VRC Object Pool が同じ GameObject にあります。SDK はこの状態ではビルドしません。プールは別の GameObject に置いてください",
                        path: os.O.Path);
            }

            udon["udon_behaviours"] = audited;
            udon["with_program"] = withProgram;
            udon["without_program"] = noProgram.Count + missingProgram.Count;
            udon["program_source_missing_but_compiled"] = sourceMissingCompiled.Count;
            udon["not_compiled"] = notCompiled.Count;
            udon["sync_methods"] = syncCounts;
            udon["program_count"] = programs.Count;
            udon["programs"] = programs.Values.OrderByDescending(p => p.Behaviours).ThenBy(p => p.Program.name, StringComparer.Ordinal)
                .Take(ctx.TopN).Select(p =>
                {
                    var d = new Dictionary<string, object>
                    {
                        ["name"] = p.Program.name,
                        ["asset"] = AssetPathOf(p.Program),
                        ["type"] = p.Type,
                        ["behaviours"] = p.Behaviours,
                        ["sync"] = p.Sync,
                    };
                    if (usharpSyncModes.TryGetValue(p.Program, out var mode)) d["usharp_sync_mode"] = EnumName(BehaviourSyncModeNames, mode);
                    return (object)d;
                }).ToList();
            if (programs.Count > ctx.TopN) udon["programs_omitted"] = programs.Count - ctx.TopN;

            AuditUdonSharp(ctx, udon);
        }

        /// <summary>
        /// UdonSharp: scene proxies without their backing UdonBehaviour, and the project-wide validation U# runs before every
        /// compile (UdonSharpCompilerV1.Compile L280-345): a program asset without source script, a script referenced by more than
        /// one program asset, and a script outside Assembly-CSharp / U# assemblies ("does not belong to a U# assembly"). Any of
        /// these stops ALL U# compilation silently (Console may stay empty), which then blocks the SDK build (UdonSharpBuildCompile).
        /// </summary>
        static void AuditUdonSharp(Ctx ctx, Dictionary<string, object> udon)
        {
            var usharpType = CctSerialized.FindType(TUSharpBehaviour);
            var us = new Dictionary<string, object> { ["present"] = usharpType != null };
            udon["udonsharp"] = us;
            if (usharpType == null) return;

            // Scene proxies (UdonSharpBehaviour components): _udonSharpBackingUdonBehaviour (UdonSharpBehaviour.cs L503-504).
            int proxies = 0;
            var noBacking = new List<KeyValuePair<string, string>>(); // hierarchy path, U# class
            foreach (var item in ctx.Audited(K.USharpProxy))
            {
                proxies++;
                using (var so = TrySerialize(ctx, item.C, "UdonSharpBehaviour"))
                {
                    if (so == null) continue;
                    var prop = so.FindProperty("_udonSharpBackingUdonBehaviour");
                    if (prop != null && CctSerialized.GetObject(prop) == null) noBacking.Add(new KeyValuePair<string, string>(item.O.Path, item.C.GetType().FullName));
                    ScanMissingReferencesWithinBudget(ctx, item, so);
                }
            }
            us["proxies"] = proxies;
            us["proxies_without_backing"] = noBacking.Count;
            // U# repairs this itself (UdonSharpEditorManager.RunAllUpdates -> SanitizeProxyBehaviours -> RunBehaviourSetup on
            // scene open, entering Play mode and after compiles); it stays only when the repair is skipped, e.g. because the
            // script has no valid program asset ("Program asset on ... is not valid"). Until then nothing runs in VRChat.
            foreach (var p in noBacking)
                ctx.Issue(CctSeverity.Warning, "USHARP_PROXY_NO_BACKING",
                    $"UdonSharpBehaviour ({p.Value}) に対応する (裏側の) UdonBehaviour がありません。このままでは VRChat で動きません。通常は U# がシーンを開いたとき・Play 時・コンパイル後に自動で作り直すので、残っている場合はスクリプトに有効な UdonSharpProgramAsset があるか確認し、シーンを開き直すかコンポーネントを付け直してください",
                    path: p.Key, value: p.Value);

            // Project-wide U# validation.
            var budget = Stopwatch.StartNew();
            var asmNames = new HashSet<string>(StringComparer.Ordinal) { "Assembly-CSharp" };
            var asmDefs = new List<object>();
            foreach (var guid in SafeFindAssets("t:UdonSharpAssemblyDefinition"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null || !CctSerialized.IsType(asset, TUSharpAssemblyDefinition)) continue;
                using (var so = TrySerializeAsset(asset))
                {
                    if (so == null || !HasProps(ctx, so, asset, "sourceAssembly")) continue;
                    var source = CctSerialized.GetObject(so.FindProperty("sourceAssembly"));
                    asmDefs.Add(new Dictionary<string, object> { ["asset"] = path, ["source_assembly"] = source != null ? source.name : null });
                    if (source != null) asmNames.Add(source.name);
                    else
                        ctx.Issue(CctSeverity.Warning, "USHARP_ASSEMBLY_DEFINITION_NO_SOURCE",
                            "U# Assembly Definition に Source Assembly が設定されていません。U# はこの定義を無視するため、対象 asmdef 内の U# スクリプトはコンパイルされません",
                            asset: path);
                }
            }
            us["assembly_definition_count"] = asmDefs.Count;
            us["assembly_definitions"] = asmDefs.Take(ctx.TopN).ToList();
            if (asmDefs.Count > ctx.TopN) us["assembly_definitions_omitted"] = asmDefs.Count - ctx.TopN;

            var scriptToPrograms = new Dictionary<MonoScript, List<string>>();
            var programClasses = new HashSet<Type>();
            int programCount = 0, scanned = 0;
            bool truncated = false;
            var notInAssembly = new List<string>();
            foreach (var guid in SafeFindAssets("t:UdonSharpProgramAsset"))
            {
                programCount++;
                if (budget.ElapsedMilliseconds > ctx.UdonSharpBudgetMs) { truncated = true; continue; }
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null || !CctSerialized.IsType(asset, TUSharpProgramAsset)) continue;
                scanned++;
                MonoScript script;
                using (var so = TrySerializeAsset(asset))
                {
                    if (so == null || !HasProps(ctx, so, asset, "sourceCsScript")) continue;
                    script = CctSerialized.GetObject(so.FindProperty("sourceCsScript")) as MonoScript;
                }
                var scriptPath = script != null ? AssetPathOf(script) : null;
                if (scriptPath == null)
                {
                    ctx.Issue(CctSeverity.Error, "USHARP_PROGRAM_NO_SOURCE",
                        "UdonSharpProgramAsset の Source Script が空 (または削除済み) です。U# はこの状態だとプロジェクト全体のコンパイルを中止します。スクリプトを割り当てるか、不要な .asset を削除してください",
                        asset: path);
                    continue;
                }
                if (!scriptToPrograms.TryGetValue(script, out var list)) scriptToPrograms[script] = list = new List<string>();
                list.Add(path);
                try
                {
                    var cls = script.GetClass();
                    if (cls != null) programClasses.Add(cls);
                }
                catch (Exception) { /* script not compiled */ }

                string asmName = null;
                try { asmName = CompilationPipeline.GetAssemblyNameFromScriptPath(scriptPath); }
                catch (Exception) { /* unknown */ }
                if (asmName != null && asmName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) asmName = asmName.Substring(0, asmName.Length - 4);
                if (asmName != null && !asmNames.Contains(asmName))
                {
                    notInAssembly.Add(scriptPath);
                    // UdonSharpCompilerV1.Compile returns before compiling anything (L334-335) and, unlike the other validation
                    // failures, leaves LastCompileDiagnostics as it was: UdonSharpBuildCompile then blocks the build only if an
                    // error is already cached, otherwise the world is built with the previously compiled programs.
                    bool specialFolder = asmName.StartsWith("Assembly-CSharp", StringComparison.Ordinal); // firstpass / Editor
                    ctx.Issue(CctSeverity.Error, "USHARP_SCRIPT_NOT_IN_USHARP_ASSEMBLY",
                        $"U# スクリプト '{scriptPath}' はアセンブリ '{asmName}' に属していますが、U# がコンパイルする対象 (Assembly-CSharp か U# Assembly Definition のある asmdef) ではありません。" +
                        "U# は \"does not belong to a U# assembly\" でプロジェクト全体のコンパイルを中止します (Console に出ず Editor.log だけに残ることがあります)。ビルドが止まるか、前回のコンパイル結果のままアップロードされます。" +
                        (specialFolder
                            ? "Plugins / Standard Assets / Editor フォルダの外 (Assets 直下など) へスクリプトを移してください"
                            : "その asmdef と同じフォルダに Create > U# Assembly Definition を作り、Source Assembly に asmdef を設定してください"),
                        asset: scriptPath, value: asmName);
                }
            }
            foreach (var kv in scriptToPrograms.Where(kv => kv.Value.Count > 1))
                ctx.Issue(CctSeverity.Error, "USHARP_DUPLICATE_PROGRAM_ASSET",
                    $"スクリプト '{AssetPathOf(kv.Key)}' を {kv.Value.Count} 個の UdonSharpProgramAsset が参照しています。U# は 1 スクリプトにつき 1 アセットしか許さず、プロジェクト全体のコンパイルを中止します",
                    asset: AssetPathOf(kv.Key), value: kv.Value.Cast<object>().ToList());
            us["program_assets"] = programCount;
            us["program_assets_scanned"] = scanned;
            us["scripts_outside_usharp_assemblies"] = notInAssembly.Count;
            if (truncated)
            {
                us["program_assets_truncated"] = true;
                ctx.Issue(CctSeverity.Info, "USHARP_SCAN_TRUNCATED",
                    $"UdonSharpProgramAsset が多いため {scanned}/{programCount} 個で確認を打ち切りました (timeout_seconds を増やすと全件確認します)",
                    value: new Dictionary<string, object> { ["scanned"] = scanned, ["total"] = programCount });
            }

            // U# classes in assemblies U# does not compile and without a program asset: not usable in worlds (info).
            var orphans = new List<string>();
            try
            {
                foreach (var t in TypeCache.GetTypesDerivedFrom(usharpType))
                {
                    if (t == null || t.IsAbstract || t.IsGenericTypeDefinition || programClasses.Contains(t)) continue;
                    string asm = null;
                    try { asm = t.Assembly.GetName().Name; }
                    catch (Exception) { /* dynamic */ }
                    if (asm == null || asmNames.Contains(asm) || asm.StartsWith("UdonSharp", StringComparison.Ordinal)) continue;
                    orphans.Add(t.FullName + " (" + asm + ")");
                }
            }
            catch (Exception) { /* TypeCache unavailable */ }
            if (orphans.Count > 0)
                ctx.Issue(CctSeverity.Info, "USHARP_SCRIPT_IN_NON_USHARP_ASSEMBLY",
                    $"U# Assembly Definition の無いアセンブリに UdonSharpBehaviour のクラスが {orphans.Count} 個あります (プログラムアセットが無いのでコンパイルは止まりませんが、ワールドでは使えません)",
                    value: Grouped(orphans.Count, orphans));
        }

        static string[] SafeFindAssets(string filter)
        {
            try { return AssetDatabase.FindAssets(filter) ?? new string[0]; }
            catch (Exception) { return new string[0]; }
        }

        /// <summary>
        /// Does the program source keep a compiled asset? UdonProgramAsset (base of the Udon Graph / Assembly / UdonSharp
        /// program assets) stores it in [SerializeField] protected serializedUdonProgramAsset (Editor/Udon/ProgramSources/
        /// UdonProgram/UdonProgramAsset.cs L28-29). Read through SerializedObject: the SerializedProgramAsset getter would CREATE
        /// the asset and compile. Unknown program source types (field absent) count as "no" so the old warning is kept.
        /// </summary>
        static bool ProgramSourceHasCompiledAsset(Object programSource, Dictionary<Object, bool> cache)
        {
            if (programSource == null) return false;
            if (cache.TryGetValue(programSource, out var known)) return known;
            bool has = false;
            using (var so = TrySerializeAsset(programSource))
            {
                var p = so?.FindProperty("serializedUdonProgramAsset");
                has = p != null && CctSerialized.GetObject(p) != null;
            }
            cache[programSource] = has;
            return has;
        }

        // ------------------------------------------------------------------ interactables, audio, dynamics, colliders

        static void AuditInteractables(Ctx ctx)
        {
            var d = new Dictionary<string, object>();
            ctx.R.Put("interactables", d);
            AuditPickups(ctx, d);
            AuditStationsPoolsPortals(ctx, d);
            AuditVideoPlayers(ctx, d);
            AuditAudio(ctx, d);
            AuditDynamics(ctx, d);
            AuditColliders(ctx);
        }

        static void AuditPickups(Ctx ctx, Dictionary<string, object> d)
        {
            int count = 0, synced = 0, notPickupable = 0, unsyncedWithUdon = 0;
            var autoHold = new Dictionary<string, object>();
            var orientation = new Dictionary<string, object>();
            var version = new Dictionary<string, object>();
            var unsynced = new List<string>();
            var collidesWithPlayer = new List<string>();
            var items = new List<object>();
            foreach (var item in ctx.Audited(K.Pickup))
            {
                count++;
                var go = item.C.gameObject;
                bool hasRb = go.TryGetComponent<Rigidbody>(out var rb);
                bool hasCollider = go.TryGetComponent<Collider>(out _);
                bool hasSync = HasComponentOfKind(ctx, go, K.ObjectSync);
                // An UdonBehaviour alone does not sync the position (docs vrc_pickup.md L15-17: add VRC Object Sync); only the
                // obsolete UdonBehaviour.SynchronizePosition does. Pickups with Udon are still listed (it may sync by itself).
                var udons = ComponentsOfKind(go, K.Udon);
                bool legacyPositionSync = udons.Any(u => UdonSynchronizesPosition(ctx, u));
                if (hasSync || legacyPositionSync) synced++;
                else
                {
                    unsynced.Add(item.O.Path);
                    if (udons.Count > 0) unsyncedWithUdon++;
                }

                // VRC_Pickup has [RequireComponent(Rigidbody)]; VRCPickupEditor3 L84-90 warns when there is no Collider.
                if (!hasRb)
                    ctx.Issue(CctSeverity.Error, "PICKUP_NO_RIGIDBODY",
                        "VRC Pickup に Rigidbody がありません (SDK は Rigidbody を必須にしています)。Rigidbody を追加してください",
                        path: item.O.Path);
                if (!hasCollider)
                    ctx.Issue(CctSeverity.Warning, "PICKUP_NO_COLLIDER",
                        "VRC Pickup と同じ GameObject に Collider がないため、掴めません (SDK のインスペクタ警告と同じ)",
                        path: item.O.Path);
                // docs/worlds/layers.md L32: the Pickup layer "Does not collide with players".
                if (hasRb && !rb.isKinematic && LayerCollidesWithPlayer(ctx, go.layer)) collidesWithPlayer.Add(item.O.Path);

                using (var so = TrySerialize(ctx, item.C, "VRC Pickup"))
                {
                    if (so == null) continue;
                    bool pickupable = CctSerialized.GetBool(so.FindProperty("pickupable"), true);
                    if (!pickupable) notPickupable++;
                    var ah = EnumName(AutoHoldNames, CctSerialized.GetInt(so.FindProperty("AutoHold")));
                    var or = EnumName(OrientationNames, CctSerialized.GetInt(so.FindProperty("orientation")));
                    var ver = EnumName(PickupVersionNames, CctSerialized.GetInt(so.FindProperty("version")));
                    Inc(autoHold, ah);
                    Inc(orientation, or);
                    Inc(version, ver);
                    if (items.Count < ctx.TopN)
                        items.Add(new Dictionary<string, object>
                        {
                            ["path"] = item.O.Path,
                            ["active"] = item.O.Active,
                            ["object_sync"] = hasSync,
                            ["pickupable"] = pickupable,
                            ["auto_hold"] = ah,
                            ["orientation"] = or,
                            ["version"] = ver,
                            ["proximity"] = Round(CctSerialized.GetFloat(so.FindProperty("proximity"), 2f), 2),
                            ["layer"] = LayerName(go.layer),
                            ["kinematic"] = hasRb && rb.isKinematic,
                        });
                }
            }
            if (unsynced.Count > 0)
                ctx.Issue(CctSeverity.Info, "PICKUP_NOT_SYNCED",
                    $"VRC Object Sync の無いピックアップが {unsynced.Count} 個あります (うち UdonBehaviour 付き {unsyncedWithUdon} 個)。持って動かしても他のプレイヤーには位置が同期されません。同期したい物には VRC Object Sync を追加してください (Udon で位置を独自に同期しているなら不要)",
                    path: unsynced[0], value: new Dictionary<string, object>
                    {
                        ["count"] = unsynced.Count, ["with_udon"] = unsyncedWithUdon, ["examples"] = Examples(unsynced),
                    });
            if (collidesWithPlayer.Count > 0)
                ctx.Issue(CctSeverity.Info, "PICKUP_COLLIDES_WITH_PLAYER",
                    $"プレイヤーと衝突するレイヤーにある (Kinematic でない) ピックアップが {collidesWithPlayer.Count} 個あります。プレイヤーを押したり乗れたりします。通常は Pickup レイヤー (13) にします",
                    path: collidesWithPlayer[0], value: Grouped(collidesWithPlayer.Count, collidesWithPlayer));
            d["pickups"] = new Dictionary<string, object>
            {
                ["count"] = count,
                ["synced"] = synced,
                ["not_synced"] = unsynced.Count,
                ["not_pickupable"] = notPickupable,
                ["auto_hold"] = autoHold,
                ["orientation"] = orientation,
                ["version"] = version,
                ["items"] = items,
            };
        }

        /// <summary>Obsolete UdonBehaviour.SynchronizePosition (read in AuditUdon, which runs before the interactables).</summary>
        static bool UdonSynchronizesPosition(Ctx ctx, Component udon) => udon != null && ctx.UdonPositionSync.Contains(udon);

        static bool LayerCollidesWithPlayer(Ctx ctx, int layer)
        {
            if (layer < 0 || layer > 31) return false;
            if (layer < 22 && ctx.ExpectedCollisionRows != null && ctx.ExpectedCollisionRows.Length > LayerPlayerLocal)
                return (ctx.ExpectedCollisionRows[LayerPlayerLocal] & (1 << layer)) != 0;
            return !Physics.GetIgnoreLayerCollision(LayerPlayerLocal, layer);
        }

        static void AuditStationsPoolsPortals(Ctx ctx, Dictionary<string, object> d)
        {
            // Stations: docs/worlds/components/vrc_station.md L25-74 (Collider + UdonBehaviour calling UseAttachedStation).
            int stations = 0;
            var mobility = new Dictionary<string, object>();
            var noUdon = new List<string>();
            foreach (var item in ctx.Audited(K.Station))
            {
                stations++;
                var go = item.C.gameObject;
                if (!go.TryGetComponent<Collider>(out _))
                    ctx.Issue(CctSeverity.Info, "STATION_NO_COLLIDER",
                        "VRC Station と同じ GameObject に Collider がありません。Interact で座れません (docs: 通常は Is Trigger の Collider を付ける)",
                        path: item.O.Path);
                if (!HasComponentOfKind(ctx, go, K.Udon)) noUdon.Add(item.O.Path);
                using (var so = TrySerialize(ctx, item.C, "VRC Station"))
                    if (so != null) Inc(mobility, EnumName(MobilityNames, CctSerialized.GetInt(so.FindProperty("PlayerMobility"), 1)));
            }
            if (noUdon.Count > 0)
                ctx.Issue(CctSeverity.Info, "STATION_NO_INTERACT_UDON",
                    $"同じ GameObject に UdonBehaviour が無い VRC Station が {noUdon.Count} 個あります。別の Udon から UseStation しない限り座れません (docs: Interact で UseAttachedStation を呼ぶ)",
                    path: noUdon[0], value: Grouped(noUdon.Count, noUdon));
            d["stations"] = new Dictionary<string, object> { ["count"] = stations, ["player_mobility"] = mobility };

            d["object_syncs"] = ctx.Audited(K.ObjectSync).Count();
            int pools = 0, pooled = 0;
            foreach (var item in ctx.Audited(K.ObjectPool))
            {
                pools++;
                using (var so = TrySerialize(ctx, item.C, "VRC Object Pool"))
                {
                    if (so == null) continue;
                    int empty = 0, size = 0;
                    foreach (var el in CctSerialized.Elements(so.FindProperty("Pool")))
                    {
                        size++;
                        if (CctSerialized.GetObject(el) == null) empty++;
                    }
                    pooled += size - empty;
                    if (empty > 0)
                        ctx.Issue(CctSeverity.Warning, "OBJECT_POOL_EMPTY_ENTRY",
                            $"VRC Object Pool の Pool に空の要素が {empty} 個あります (全 {size} 個)。TryToSpawn が null を返す・エラーになる原因になります",
                            path: item.O.Path, value: empty);
                }
            }
            d["object_pools"] = new Dictionary<string, object> { ["count"] = pools, ["pooled_objects"] = pooled };

            int portals = 0;
            foreach (var item in ctx.Audited(K.Portal))
            {
                portals++;
                // docs/worlds/sdk-prefabs.md L30: "must be at the root of your scene hierarchy" for the destination instance to sync.
                if (item.C.transform.parent != null)
                    ctx.Issue(CctSeverity.Warning, "PORTAL_NOT_AT_ROOT",
                        "VRC Portal Marker がシーンのルートにありません。docs は行き先インスタンスを他のプレイヤーと同期するためルートに置くよう求めています",
                        path: item.O.Path);
                using (var so = TrySerialize(ctx, item.C, "VRC Portal Marker"))
                {
                    if (so == null || !HasProps(ctx, so, item.C, "world", "roomId")) continue;
                    int world = CctSerialized.GetInt(so.FindProperty("world"));
                    var roomId = CctSerialized.GetString(so.FindProperty("roomId"), "");
                    if (world == 0 && string.IsNullOrWhiteSpace(roomId))
                        ctx.Issue(CctSeverity.Warning, "PORTAL_NO_DESTINATION",
                            "VRC Portal Marker の行き先 (World ID / World) が設定されていません",
                            path: item.O.Path);
                }
            }
            d["portals"] = portals;

            int pedestals = 0;
            foreach (var item in ctx.Audited(K.Pedestal))
            {
                pedestals++;
                using (var so = TrySerialize(ctx, item.C, "VRC Avatar Pedestal"))
                {
                    if (so == null || !HasProps(ctx, so, item.C, "blueprintId")) continue;
                    if (string.IsNullOrWhiteSpace(CctSerialized.GetString(so.FindProperty("blueprintId"), "")))
                        ctx.Issue(CctSeverity.Warning, "PEDESTAL_NO_BLUEPRINT",
                            "VRC Avatar Pedestal の Blueprint ID (avtr_...) が空です。何も表示されません",
                            path: item.O.Path);
                }
            }
            d["avatar_pedestals"] = pedestals;
        }

        static void AuditVideoPlayers(Ctx ctx, Dictionary<string, object> d)
        {
            // Screens / speakers reference their player through a private serialized field (decompiled reference: videoPlayer).
            var screensFor = new Dictionary<Object, int>();
            var speakersFor = new Dictionary<Object, int>();
            bool avproLinksKnown = true;
            foreach (var item in ctx.Get(K.AvProScreen).Concat(ctx.Get(K.AvProSpeaker)))
            {
                using (var so = TrySerialize(ctx, item.C, "AVPro Video Screen/Speaker"))
                {
                    if (so != null && !HasProps(ctx, so, item.C, "videoPlayer")) avproLinksKnown = false;
                    var player = so != null ? CctSerialized.GetObject(so.FindProperty("videoPlayer")) : null;
                    if (player == null) continue;
                    var map = (Classify(item.C.GetType()).Kinds & K.AvProScreen) != 0 ? screensFor : speakersFor;
                    map.TryGetValue(player, out var n);
                    map[player] = n + 1;
                }
            }

            var items = new List<object>();
            var autoPlayUrls = new List<string>();
            int count = 0;
            foreach (var item in ctx.Audited(K.UnityVideo | K.AvProVideo))
            {
                count++;
                bool avpro = (Classify(item.C.GetType()).Kinds & K.AvProVideo) != 0;
                var entry = new Dictionary<string, object>
                {
                    ["path"] = item.O.Path,
                    ["type"] = avpro ? "AVPro" : "Unity",
                    ["active"] = item.O.Active && Enabled(item.C),
                };
                using (var so = TrySerialize(ctx, item.C, "VRC Video Player"))
                {
                    if (so == null) { items.Add(entry); continue; }
                    var url = CctSerialized.GetString(so.FindProperty("videoURL.url"), "") ?? "";
                    bool autoPlay = CctSerialized.GetBool(so.FindProperty("autoPlay"), true);
                    entry["url"] = url.Length > 0 ? Trim(url, 200) : null;
                    entry["auto_play"] = autoPlay;
                    entry["loop"] = CctSerialized.GetBool(so.FindProperty("loop"), true);
                    entry["maximum_resolution"] = CctSerialized.GetInt(so.FindProperty("maximumResolution"), 720);
                    if (autoPlay && url.Length > 0 && (bool)entry["active"]) autoPlayUrls.Add(item.O.Path);

                    bool noOutput, noAudio;
                    if (avpro)
                    {
                        entry["low_latency"] = CctSerialized.GetBool(so.FindProperty("useLowLatency"));
                        screensFor.TryGetValue(item.C, out var screens);
                        speakersFor.TryGetValue(item.C, out var speakers);
                        entry["screens"] = screens;
                        entry["speakers"] = speakers;
                        noOutput = avproLinksKnown && screens == 0;
                        noAudio = avproLinksKnown && speakers == 0;
                    }
                    else if (!HasProps(ctx, so, item.C, "renderMode", "targetTexture", "targetMaterialRenderer", "targetAudioSources"))
                    {
                        noOutput = noAudio = false;
                    }
                    else
                    {
                        int mode = CctSerialized.GetInt(so.FindProperty("renderMode"));
                        entry["render_mode"] = mode == VideoRenderModeRenderTexture ? "RenderTexture" : "MaterialOverride";
                        var target = mode == VideoRenderModeRenderTexture
                            ? CctSerialized.GetObject(so.FindProperty("targetTexture"))
                            : CctSerialized.GetObject(so.FindProperty("targetMaterialRenderer"));
                        noOutput = target == null;
                        int audio = CctSerialized.Elements(so.FindProperty("targetAudioSources")).Count(e => CctSerialized.GetObject(e) != null);
                        entry["audio_sources"] = audio;
                        noAudio = audio == 0;
                    }
                    if (noOutput)
                        ctx.Issue(CctSeverity.Warning, "VIDEO_PLAYER_NO_OUTPUT",
                            avpro
                                ? "AVPro Video Player を参照する VRC AVPro Video Screen がありません。映像が表示されません"
                                : "VRC Unity Video Player の出力先 (Target Texture / Target Material Renderer) が未設定です。映像が表示されません",
                            path: item.O.Path);
                    if (noAudio)
                        ctx.Issue(CctSeverity.Info, "VIDEO_PLAYER_NO_AUDIO",
                            avpro
                                ? "AVPro Video Player を参照する VRC AVPro Video Speaker がありません。音が出ません"
                                : "VRC Unity Video Player の Target Audio Sources が空です。音が出ません",
                            path: item.O.Path);

                    if (url.Length > 0)
                    {
                        var host = UrlHost(url);
                        if (host != null && !IsAllowedVideoHost(host))
                            ctx.Issue(CctSeverity.Info, "VIDEO_PLAYER_URL_NOT_ALLOWLISTED",
                                $"既定 URL のドメイン '{host}' は VRChat の動画許可リストにありません。\"Allow Untrusted URLs\" を有効にしたユーザーにしか再生されず、Public インスタンスでは VRChat.com の \"Video Player Allowed Domains\" への登録も必要です",
                                path: item.O.Path, value: host);
                        // docs www-whitelist.md L10 states this for Android only.
                        if ((ctx.Platform == "all" || ctx.Platform == "android") && url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                            ctx.Issue(CctSeverity.Warning, "VIDEO_PLAYER_HTTP_MOBILE",
                                "既定 URL が http:// です。Android 版 VRChat は HTTPS でない動画を再生しません",
                                path: item.O.Path, value: Trim(url, 200));
                    }
                }
                if (items.Count < ctx.TopN) items.Add(entry);
            }
            if (count > MaxRecommendedVideoPlayers)
                ctx.Issue(CctSeverity.Warning, "VIDEO_PLAYER_TOO_MANY",
                    $"ビデオプレイヤーが {count} 台あります。docs は 1 つの空間に 3 台以上置くと性能に悪影響があると案内しています",
                    value: count, limit: MaxRecommendedVideoPlayers);
            if (autoPlayUrls.Count >= 2)
                ctx.Issue(CctSeverity.Warning, "VIDEO_PLAYER_AUTOPLAY_MULTIPLE",
                    $"既定 URL を自動再生するビデオプレイヤーが {autoPlayUrls.Count} 台あります。新しい URL の読み込みは 1 ユーザー 5 秒に 1 回までなので、同時に読み込むと失敗します",
                    path: autoPlayUrls[0], value: Grouped(autoPlayUrls.Count, autoPlayUrls));
            d["video_players"] = new Dictionary<string, object> { ["count"] = count, ["items"] = items };
        }

        static string UrlHost(string url)
        {
            try
            {
                if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return uri.Host.ToLowerInvariant();
            }
            catch (Exception) { /* invalid */ }
            return null;
        }

        static bool IsAllowedVideoHost(string host)
        {
            foreach (var pattern in VideoAllowedHosts)
            {
                if (pattern.StartsWith("*.", StringComparison.Ordinal))
                {
                    var suffix = pattern.Substring(1); // ".youtube.com"
                    if (host.EndsWith(suffix, StringComparison.Ordinal) || host == pattern.Substring(2)) return true;
                }
                else if (host == pattern) return true;
            }
            return false;
        }

        static void AuditAudio(Ctx ctx, Dictionary<string, object> d)
        {
            int count = 0, playOnAwake = 0, twoD = 0, noSpatial2D = 0;
            var noSpatial = new List<string>();
            var onsp = new List<string>();
            foreach (var item in ctx.Audited(K.AudioSource))
            {
                var a = item.C as AudioSource;
                if (a == null) continue;
                count++;
                if (a.playOnAwake) playOnAwake++;
                bool is2D = IsAudioSource2D(a);
                if (is2D) twoD++;
                var go = a.gameObject;
                // [WB] L614-648: ONSPAudioSource first, then a missing VRC_SpatialAudioSource (the SDK stops at the first hit).
                if (HasComponentOfKind(ctx, go, K.Onsp)) onsp.Add(item.O.Path);
                else if (!HasComponentOfKind(ctx, go, K.SpatialAudio))
                {
                    noSpatial.Add(item.O.Path);
                    if (is2D) noSpatial2D++;
                }
            }
            if (onsp.Count > 0)
                ctx.Issue(CctSeverity.Warning, "AUDIO_ONSP_DEPRECATED",
                    $"ONSP Audio Source (非推奨) を使う AudioSource が {onsp.Count} 個あります。SDK の Fix で VRC Spatial Audio Source に変換してください",
                    path: onsp[0], value: Grouped(onsp.Count, onsp));
            // [WB] Fix = AutoAddSpatialAudioComponents.AddVRCSpatialToBareAudioSource: adds VRCSpatialAudioSource (Gain/Far
            // defaults) with EnableSpatialization false for 2D sources (com.vrchat.base Editor/.../AutoAddSpatialAudioComponents.cs L194-228).
            if (noSpatial.Count > 0)
                ctx.Issue(CctSeverity.Warning, "AUDIO_NO_SPATIAL",
                    $"VRC Spatial Audio Source が無い AudioSource が {noSpatial.Count} 個 (うち 2D {noSpatial2D} 個) あります。SDK はこれを非推奨として警告します。Control Panel の Fix で追加できます (2D の音は Enable Spatialization を OFF にして追加されます)",
                    path: noSpatial[0], value: new Dictionary<string, object>
                    {
                        ["count"] = noSpatial.Count, ["two_d"] = noSpatial2D, ["examples"] = Examples(noSpatial),
                    });
            d["audio"] = new Dictionary<string, object>
            {
                ["audio_sources"] = count,
                ["play_on_awake"] = playOnAwake,
                ["two_d"] = twoD,
                ["without_vrc_spatial_audio"] = noSpatial.Count,
                ["onsp"] = onsp.Count,
                ["vrc_spatial_audio_sources"] = ctx.Audited(K.SpatialAudio).Count(),
            };
        }

        /// <summary>[WB] IsAudioSource2D: spatialBlend ~ 0 and the custom SpatialBlend curve has at most one key.</summary>
        static bool IsAudioSource2D(AudioSource a)
        {
            try
            {
                var curve = a.GetCustomCurve(AudioSourceCurveType.SpatialBlend);
                return Math.Abs(a.spatialBlend) < float.Epsilon && (curve == null || curve.keys.Length <= 1);
            }
            catch (Exception) { return false; }
        }

        static void AuditDynamics(Ctx ctx, Dictionary<string, object> d)
        {
            // [WB] L579-612 over every loaded scene (GatherComponentsOfTypeInScene).
            var physBones = ctx.Get(K.PhysBone);
            var colliders = ctx.Get(K.PhysBoneCollider);
            var contacts = ctx.Get(K.Contact);
            var parentless = new List<string>();
            foreach (var item in physBones)
            {
                Transform root = null;
                using (var so = TrySerialize(ctx, item.C, "VRC PhysBone"))
                {
                    if (so == null || !HasProps(ctx, so, item.C, "rootTransform")) continue;
                    root = CctSerialized.GetObject(so.FindProperty("rootTransform")) as Transform;
                }
                if (root == null) root = item.C.transform; // VRCPhysBoneBase.GetRootTransform()
                if (root.parent == null) parentless.Add(item.O.Path);
            }
            if (parentless.Count > 0)
                ctx.Issue(CctSeverity.Warning, "PHYSBONE_NO_PARENT",
                    $"ルートに親が無い PhysBone が {parentless.Count} 個あります。SDK は予期しない動きになるため、別の GameObject の子にするよう警告します",
                    path: parentless[0], value: Grouped(parentless.Count, parentless));
            int shapes = physBones.Count + colliders.Count;
            if (shapes > MaxWorldShapes)
                ctx.Issue(CctSeverity.Warning, "PHYSBONE_SHAPES_OVER_LIMIT",
                    $"PhysBone と PhysBone Collider が合計 {shapes} 個あります。クライアントで同時に有効にできるのは {MaxWorldShapes} 個までです (SDK の警告と同じ)",
                    value: shapes, limit: MaxWorldShapes);
            if (contacts.Count > MaxWorldShapes)
                ctx.Issue(CctSeverity.Warning, "CONTACTS_OVER_LIMIT",
                    $"Contact が {contacts.Count} 個あります。クライアントで同時に有効にできるのは {MaxWorldShapes} 個までです (SDK の警告と同じ)",
                    value: contacts.Count, limit: MaxWorldShapes);
            d["dynamics"] = new Dictionary<string, object>
            {
                ["physbones"] = physBones.Count,
                ["physbone_colliders"] = colliders.Count,
                ["contacts"] = contacts.Count,
                ["limit_each"] = MaxWorldShapes,
                ["scope"] = "all_loaded_scenes",
            };
        }

        static void AuditColliders(Ctx ctx)
        {
            var byType = new Dictionary<string, object>();
            int total = 0, triggers = 0, nonConvexMesh = 0;
            foreach (var item in ctx.Audited(K.Collider))
            {
                var c = item.C as Collider;
                if (c == null) continue;
                total++;
                Inc(byType, c.GetType().Name);
                if (c.isTrigger) triggers++;
                if (c is MeshCollider mc && !mc.convex)
                {
                    nonConvexMesh++;
                    var rb = FindRigidbodyInParents(c.transform);
                    // Unity: non-convex MeshColliders are not supported on non-kinematic Rigidbodies (estimate: physics warns at runtime).
                    if (rb != null && !rb.isKinematic)
                        ctx.Issue(CctSeverity.Warning, "MESH_COLLIDER_NONCONVEX_RIGIDBODY",
                            "Kinematic でない Rigidbody の下に Convex でない Mesh Collider があります。Unity はこの組み合わせをサポートしないため、Convex を ON にするか Box/Sphere/Capsule に置き換えてください",
                            path: item.O.Path, value: CctTargets.GetPath(rb.transform));
                }
            }
            ctx.R.Put("colliders", new Dictionary<string, object>
            {
                ["total"] = total,
                ["triggers"] = triggers,
                ["non_convex_mesh_colliders"] = nonConvexMesh,
                ["by_type"] = byType,
            });
        }

        static Rigidbody FindRigidbodyInParents(Transform t)
        {
            for (var cur = t; cur != null; cur = cur.parent)
                if (cur.TryGetComponent<Rigidbody>(out var rb)) return rb;
            return null;
        }

        // ------------------------------------------------------------------ UI

        static void AuditUi(Ctx ctx)
        {
            var ui = new Dictionary<string, object>();
            ctx.R.Put("ui", ui);
            int shapes = 0;
            foreach (var item in ctx.Audited(K.UiShape))
            {
                shapes++;
                var go = item.C.gameObject;
                // docs/worlds/components/vrc_uishape.md L22-48.
                if (!go.TryGetComponent<Canvas>(out var canvas))
                {
                    ctx.Issue(CctSeverity.Warning, "UI_SHAPE_NO_CANVAS",
                        "VRC Ui Shape と同じ GameObject に Canvas がありません。docs は Canvas と同じ GameObject に付けるよう求めています",
                        path: item.O.Path);
                    continue;
                }
                if (go.layer == LayerUi)
                    ctx.Issue(CctSeverity.Warning, "UI_SHAPE_ON_UI_LAYER",
                        "VRC Ui Shape の Canvas が UI レイヤーにあります。VRChat のメニューを開いているときしか操作できません。Default など別のレイヤーにしてください",
                        path: item.O.Path);
                if (canvas.renderMode != RenderMode.WorldSpace)
                    ctx.Issue(CctSeverity.Warning, "UI_SHAPE_NOT_WORLD_SPACE",
                        $"VRC Ui Shape の Canvas の Render Mode が {canvas.renderMode} です。World Space にしてください",
                        path: item.O.Path);
                if (!HasComponentOfKind(ctx, go, K.UiRaycaster))
                    ctx.Issue(CctSeverity.Warning, "UI_SHAPE_NO_RAYCASTER",
                        "VRC Ui Shape の Canvas に Graphic Raycaster がありません。ポインタは出ても UI が反応しません",
                        path: item.O.Path);
            }
            int eventSystems = ctx.Audited(K.EventSystem).Count();
            if (shapes > 0 && eventSystems == 0)
                ctx.Issue(CctSeverity.Warning, "UI_NO_EVENT_SYSTEM",
                    "VRC Ui Shape がありますが EventSystem がシーンにありません。docs は EventSystem が必要としています (Canvas を作ると自動で追加されます)");

            // World-space root canvases with interactive elements but no VRC Ui Shape.
            var selectableType = CctSerialized.FindType(TUiSelectable);
            int canvases = 0, worldCanvases = 0;
            var noShape = new List<string>();
            foreach (var item in ctx.Audited(K.Canvas))
            {
                var canvas = item.C as Canvas;
                if (canvas == null) continue;
                canvases++;
                if (canvas.renderMode != RenderMode.WorldSpace) continue;
                worldCanvases++;
                if (!canvas.isRootCanvas || HasComponentOfKind(ctx, canvas.gameObject, K.UiShape) || selectableType == null) continue;
                Component[] selectables;
                try { selectables = canvas.GetComponentsInChildren(selectableType, true); }
                catch (Exception) { continue; }
                if (selectables.Length > 0) noShape.Add(item.O.Path);
            }
            if (noShape.Count > 0)
                ctx.Issue(CctSeverity.Info, "UI_CANVAS_NO_UI_SHAPE",
                    $"ボタンなどがある World Space の Canvas {noShape.Count} 個に VRC Ui Shape がありません。VRChat のポインタで操作できません (表示だけなら不要)",
                    path: noShape[0], value: Grouped(noShape.Count, noShape));

            // Navigation / scroll sensitivity (docs vrc_uishape.md L36-37, L66-69).
            var navigation = new List<string>();
            foreach (var item in ctx.Audited(K.UiSelectable))
                using (var so = TrySerialize(ctx, item.C, "UI Selectable"))
                {
                    var mode = so?.FindProperty("m_Navigation.m_Mode");
                    if (mode != null && CctSerialized.GetInt(mode) != 0) navigation.Add(item.O.Path);
                }
            if (navigation.Count > 0)
                ctx.Issue(CctSeverity.Info, "UI_NAVIGATION_NOT_NONE",
                    $"Navigation が None でない UI 要素が {navigation.Count} 個あります。移動キー/スティックで UI が勝手に操作されることがあるため、docs は None を推奨しています",
                    path: navigation[0], value: Grouped(navigation.Count, navigation));
            var scroll = new List<string>();
            foreach (var item in ctx.Audited(K.UiScrollRect))
                using (var so = TrySerialize(ctx, item.C, "UI ScrollRect"))
                {
                    var p = so?.FindProperty("m_ScrollSensitivity");
                    if (p != null && Math.Abs(CctSerialized.GetFloat(p)) > float.Epsilon) scroll.Add(item.O.Path);
                }
            if (scroll.Count > 0)
                ctx.Issue(CctSeverity.Info, "UI_SCROLL_SENSITIVITY",
                    $"Scroll Sensitivity が 0 でない ScrollRect が {scroll.Count} 個あります。移動中に勝手にスクロールするため、docs は 0 を推奨しています",
                    path: scroll[0], value: Grouped(scroll.Count, scroll));

            // [WB] L707-819: uGUI graphics using UI/Default when the Supersampled UI shader exists. Read via SerializedObject:
            // Graphic.material would instantiate TMP materials (TMP_Text.GetMaterial), so m_Material / m_sharedMaterial are read.
            int defaultUi = 0;
            var defaultUiPaths = new List<string>();
            bool ssAvailable = false;
            try { ssAvailable = Shader.Find("VRChat/Mobile/Worlds/Supersampled UI") != null; }
            catch (Exception) { /* ignored */ }
            if (ssAvailable)
                foreach (var item in ctx.Audited(K.UiGraphic))
                {
                    bool tmp = (Classify(item.C.GetType()).Kinds & K.TmpAny) != 0 || (item.C.GetType().FullName ?? "").StartsWith("TMPro.", StringComparison.Ordinal);
                    using (var so = TrySerialize(ctx, item.C, "UI Graphic"))
                    {
                        if (so == null) continue;
                        var prop = so.FindProperty(tmp ? "m_sharedMaterial" : "m_Material");
                        if (prop == null) continue;
                        var mat = CctSerialized.GetObject(prop) as Material;
                        // Graphic.defaultMaterial (Canvas.GetDefaultCanvasMaterial) uses UI/Default; TMP without material is unknown.
                        string shader = mat != null ? (mat.shader != null ? mat.shader.name : null) : tmp ? null : "UI/Default";
                        if (shader != "UI/Default") continue;
                        defaultUi++;
                        defaultUiPaths.Add(item.O.Path);
                    }
                }
            if (defaultUi > 0)
                ctx.Issue(CctSeverity.Info, "UI_DEFAULT_SHADER",
                    $"Unity 標準の UI シェーダー (UI/Default) を使う UI が {defaultUi} 個あります。SDK は VR で読みやすい Supersampled UI シェーダーへの切り替え (Auto Fix) を勧めています",
                    path: defaultUiPaths[0], value: Grouped(defaultUi, defaultUiPaths));

            // [WB] L527-541: Unity text components but no TextMeshPro at all.
            int legacyText = ctx.Audited(K.UiText).Count() + ctx.Audited(K.TextMesh).Count();
            int tmpCount = ctx.Audited(K.TmpAny).Count();
            if (legacyText > 0 && tmpCount == 0)
                ctx.Issue(CctSeverity.Info, "TMP_RECOMMENDED",
                    $"Unity 標準のテキスト (UI Text / TextMesh) が {legacyText} 個あり、TextMeshPro がありません。SDK は読みやすい TextMeshPro を勧めています",
                    value: legacyText);

            ui["canvases"] = canvases;
            ui["world_space_canvases"] = worldCanvases;
            ui["ui_shapes"] = shapes;
            ui["event_systems"] = eventSystems;
            ui["graphics"] = ctx.Audited(K.UiGraphic).Count();
            ui["selectables"] = ctx.Audited(K.UiSelectable).Count();
            ui["textmeshpro"] = tmpCount;
            ui["legacy_text"] = legacyText;
            ui["ui_default_shader"] = ssAvailable ? defaultUi : (object)null;
        }
    }
}
