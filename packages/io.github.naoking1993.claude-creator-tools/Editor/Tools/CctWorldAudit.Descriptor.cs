using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    public static partial class CctWorldAudit
    {
        // VRC_SceneDescriptor enums (FieldCheck dump, VRCSDKBase.dll 3.10.5): stored as their int values.
        static readonly string[] SpawnOrderNames = { "First", "Sequential", "Random", "Demo" };
        static readonly string[] SpawnOrientationNames = { "Default", "AlignPlayerWithSpawnPoint", "AlignRoomWithSpawnPoint" };
        static readonly string[] RespawnBehaviourNames = { "Respawn", "Destroy" };

        // [WB] WORLD_MAX_SPAWN_OFFSET = 1000f (VRCSdkControlPanelWorldBuilder.cs L57).
        const float WorldMaxSpawnOffset = 1000f;

        // docs/worlds/udon/vrc-graphics/vrc-camera-settings.md L61-65: NearClipPlane clamped to 0.001..0.05, "similar
        // restrictions to setting them via the Reference Camera" (so applying it to the Reference Camera is an estimate).
        const float ReferenceNearMin = 0.001f, ReferenceNearMax = 0.05f;

        // VRChat layer indices (docs/worlds/layers.md L17-44; VRCProjectSettings.prefab layers[]).
        const int LayerItem = 3, LayerUi = 5, LayerReserved6 = 6, LayerReserved7 = 7, LayerPlayerLocal = 10, LayerUiMenu = 12,
            LayerMirrorReflection = 18, LayerInternalUi = 19, LayerHardwareObjects = 20, LayerReserved4 = 21, LayerEditorPreview = 31;

        // Fallback copy of the SDK's VRCProjectSettings.prefab (com.vrchat.base 3.10.5 Runtime/VRCSDK/Dependencies/VRChat/
        // Resources/VRCProjectSettings.prefab): layer names 0..21 and the decoded 32x32 layerCollisionArr rows 0..21 as bit masks
        // (bit j = collides with layer j). Used only when the prefab cannot be loaded at runtime.
        static readonly string[] FallbackLayerNames =
        {
            "Default", "TransparentFX", "Ignore Raycast", "Item", "Water", "UI", "reserved6", "reserved7", "Interactive", "Player",
            "PlayerLocal", "Environment", "UiMenu", "Pickup", "PickupNoEnvironment", "StereoLeft", "StereoRight", "Walkthrough",
            "MirrorReflection", "InternalUI", "HardwareObjects", "reserved4",
        };

        static readonly int[] FallbackCollisionRows =
        {
            3649495, 3649495, 3649495, 8, 3649495, 192, 4194295, 4194295, 3649495, 3410391, 3410391, 3649495, 192, 256471, 8384,
            3647959, 3647959, 3647959, 3641303, 192, 3641303, 3641303,
        };

        // ------------------------------------------------------------------ descriptor

        static void AuditDescriptor(Ctx ctx)
        {
            var d = new Dictionary<string, object>();
            ctx.R.Put("descriptor", d);
            // SDK: Tools.FindSceneObjectsOfTypeAll<VRC_SceneDescriptor>() = every loaded scene, inactive included [WB A2/A3, EX D1].
            var descs = ctx.Get(K.SceneDescriptor);
            d["count_in_loaded_scenes"] = descs.Count;
            d["instances"] = descs.Take(ctx.TopN).Select(i => (object)DescribeItem(i)).ToList();

            if (descs.Count == 0)
                ctx.Issue(CctSeverity.Error, "NO_SCENE_DESCRIPTOR",
                    "VRC Scene Descriptor がありません。SDK はビルドできません。Packages/com.vrchat.worlds/Samples/UdonExampleScene/Prefabs/VRCWorld.prefab をシーンに置くか、Control Panel の「Add a VRCSceneDescriptor」を使ってください");
            else if (descs.Count > 1)
                ctx.Issue(CctSeverity.Error, "MULTIPLE_SCENE_DESCRIPTORS",
                    $"VRC Scene Descriptor が読み込み中のシーン全体で {descs.Count} 個あります。SDK はちょうど 1 個でないとビルドしません (エクスポーターも \"Scene needs exactly one VRC_SceneDescriptor\" で中止)",
                    path: PathOf(descs[0]), value: Examples(descs.Select(PathOf)));

            AuditPipelineManagers(ctx, d, descs);
            if (descs.Count == 0) return;

            var primary = descs.FirstOrDefault(i => i.O.Scene == ctx.ActiveIndex);
            if (!primary.IsValid)
            {
                primary = descs[0];
                ctx.Issue(CctSeverity.Error, "DESCRIPTOR_NOT_IN_UPLOAD_SCENE",
                    $"VRC Scene Descriptor がアクティブシーン '{ctx.Active.name}' にありません。SDK はアクティブシーンをビルドし、その中で記述子を探すため失敗します (記述子のあるシーンをアクティブにしてください)",
                    path: PathOf(primary), value: primary.C.gameObject.scene.path);
            }
            ctx.Descriptor = primary;
            d["path"] = primary.O.Path;
            d["scene"] = primary.C.gameObject.scene.path;
            d["instance_id"] = primary.C.GetInstanceID();
            d["active"] = primary.O.Active;

            using (var so = TrySerialize(ctx, primary.C, "VRC Scene Descriptor"))
            {
                if (so == null) return;
                ReadSpawns(ctx, d, primary, so);
                ReadReferenceCamera(ctx, d, primary, so);

                int interactThru = CctSerialized.GetInt(so.FindProperty("interactThruLayers"));
                // ClientSim: _interactiveLayersDefault &= ~(interactThruLayers << 22) -> bit i = layer 22 + i (research 2.2).
                var passthrough = new List<object>();
                for (int i = 0; i < 10; i++)
                    if ((interactThru & (1 << i)) != 0) passthrough.Add(LayerName(22 + i));
                d["interact_passthrough_layers"] = passthrough;
                d["forbid_user_portals"] = CctSerialized.GetBool(so.FindProperty("ForbidUserPortals"));
                d["object_behaviour_at_respawn_height"] = EnumName(RespawnBehaviourNames, CctSerialized.GetInt(so.FindProperty("ObjectBehaviourAtRespawnHeight")));

                bool autoSpatialize = CctSerialized.GetBool(so.FindProperty("autoSpatializeAudioSources"));
                d["auto_spatialize_audio_sources"] = autoSpatialize;
                if (autoSpatialize)
                    ctx.Issue(CctSeverity.Warning, "AUTO_SPATIALIZE_DEPRECATED",
                        "Scene Descriptor の 'Auto Spatialize Audio Sources' (非推奨) が有効です。SDK の Fix で無効化し、各 AudioSource に VRC Spatial Audio Source を付けてください",
                        path: primary.O.Path);
                ScanMissingReferences(ctx, primary, so);
            }
        }

        static string EnumName(string[] names, int value) =>
            value >= 0 && value < names.Length ? names[value] : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        static void AuditPipelineManagers(Ctx ctx, Dictionary<string, object> d, List<Item> descs)
        {
            // [WB] C4/C5 GatherComponentsOfTypeInScene<PipelineManager>() and [WB] A1 FindSceneObjectsOfTypeAll: all loaded scenes.
            var pms = ctx.Get(K.PipelineManager);
            var pm = new Dictionary<string, object> { ["count_in_loaded_scenes"] = pms.Count };
            d["pipeline_manager"] = pm;
            pm["paths"] = Examples(pms.Select(PathOf));
            if (pms.Count == 0)
            {
                if (descs.Count > 0)
                    ctx.Issue(CctSeverity.Error, "PIPELINE_MANAGER_MISSING",
                        "PipelineManager がありません。SDK はビルド/アップロードを止めます。Scene Descriptor と同じ GameObject に VRC.Core.PipelineManager が必要です (Descriptor のインスペクタを表示すると自動追加されます)",
                        path: PathOf(descs[0]));
                return;
            }
            if (pms.Count > 1)
                ctx.Issue(CctSeverity.Error, "MULTIPLE_PIPELINE_MANAGERS",
                    $"PipelineManager が {pms.Count} 個あります。SDK はちょうど 1 個 (Scene Descriptor と同じ GameObject) でないとビルダーを開かず、ビルドも失敗します",
                    path: PathOf(pms[0]), value: Examples(pms.Select(PathOf)));

            var onDescriptor = pms.FirstOrDefault(p => descs.Any(s => s.C != null && p.C != null && s.C.gameObject == p.C.gameObject));
            pm["on_descriptor_object"] = onDescriptor.IsValid;
            var chosen = onDescriptor.IsValid ? onDescriptor : pms[0];
            if (!onDescriptor.IsValid && descs.Count > 0)
                ctx.Issue(CctSeverity.Warning, "PIPELINE_MANAGER_NOT_ON_DESCRIPTOR",
                    "PipelineManager が Scene Descriptor と別の GameObject にあります。SDK のエラー文は「Scene Descriptor と同じ GameObject に 1 つ」を求めています",
                    path: PathOf(chosen));
            using (var so = TrySerialize(ctx, chosen.C, "PipelineManager"))
            {
                if (so == null) return;
                var id = CctSerialized.GetString(so.FindProperty("blueprintId"), "");
                pm["blueprint_id"] = id;
                pm["is_new_world"] = string.IsNullOrEmpty(id);
            }
        }

        static void ReadSpawns(Ctx ctx, Dictionary<string, object> d, Item descriptor, SerializedObject so)
        {
            var spawnsProp = so.FindProperty("spawns");
            var valid = new List<Transform>();
            int nullCount = 0, missingCount = 0;
            foreach (var el in CctSerialized.Elements(spawnsProp))
            {
                var t = CctSerialized.GetObject(el) as Transform;
                if (t != null) { valid.Add(t); continue; }
                if (CctSerialized.IsMissingReference(el)) missingCount++;
                else nullCount++;
            }
            float respawnY = CctSerialized.GetFloat(so.FindProperty("RespawnHeightY"), -100f);
            ctx.RespawnHeightY = respawnY;
            int order = CctSerialized.GetInt(so.FindProperty("spawnOrder"), 2);
            float radius = CctSerialized.GetFloat(so.FindProperty("spawnRadius"));

            var s = new Dictionary<string, object>
            {
                ["count"] = CctSerialized.ArraySize(spawnsProp),
                ["valid"] = valid.Count,
                ["null_entries"] = nullCount,
                ["missing_entries"] = missingCount,
                ["spawn_order"] = EnumName(SpawnOrderNames, order),
                ["spawn_orientation"] = EnumName(SpawnOrientationNames, CctSerialized.GetInt(so.FindProperty("spawnOrientation"))),
                ["spawn_radius"] = Round(radius, 3),
            };
            d["spawns"] = s;
            d["respawn_height_y"] = Round(respawnY, 3);

            // [WB] C9 / [EX] D3: GetValidatedSpawnList() (non-null entries of spawns) must not be empty.
            if (valid.Count == 0)
            {
                ctx.Issue(CctSeverity.Error, "NO_SPAWNS",
                    "有効なスポーン地点がありません。SDK は \"You must add at least one valid spawn point\" でビルドを止めます。Scene Descriptor の Spawns に Transform を登録してください",
                    path: descriptor.O.Path);
                s["items"] = new List<object>();
                return;
            }
            if (nullCount + missingCount > 0)
                ctx.Issue(CctSeverity.Warning, "SPAWN_NULL",
                    $"Spawns に空の要素が {nullCount} 個、削除済みオブジェクトを指す要素が {missingCount} 個あります。SDK はこれらを無視しますが、消えたスポーン地点が意図どおりか確認してください",
                    path: descriptor.O.Path, value: new Dictionary<string, object> { ["null"] = nullCount, ["missing"] = missingCount });
            if (radius <= 0f)
                ctx.Issue(CctSeverity.Info, "SPAWN_RADIUS_ZERO",
                    "Spawn Radius が 0 です。同時に入室したプレイヤーが同じ位置に重なります (docs は 0 より大きい値を推奨。新規シーンの既定は 0.2)",
                    path: descriptor.O.Path, value: Round(radius, 3));

            // [WB] VerifySpawnPlacement: First/Demo check only the first spawn; otherwise the first failing spawn is reported.
            bool firstOnly = order == 0 || order == 3;
            var toCheck = firstOnly ? valid.Take(1) : valid;
            foreach (var t in toCheck)
            {
                var pos = t.position;
                if (!(Mathf.Abs(pos.x) < WorldMaxSpawnOffset && Mathf.Abs(pos.y) < WorldMaxSpawnOffset && Mathf.Abs(pos.z) < WorldMaxSpawnOffset))
                {
                    ctx.Issue(CctSeverity.Warning, "SPAWN_TOO_FAR",
                        $"スポーン地点が原点から遠すぎます ({Vec3Text(pos)})。SDK は座標のいずれかの軸が 1000 以上だとメニューや PhysBone に問題が出ると警告します",
                        path: CctTargets.GetPath(t), value: Vec(pos), limit: WorldMaxSpawnOffset);
                    break;
                }
                if (!(pos.y >= respawnY))
                {
                    ctx.Issue(CctSeverity.Warning, "RESPAWN_HEIGHT_ABOVE_SPAWN",
                        $"スポーン地点 (y={Round(pos.y, 2)}) が Respawn Height Y ({Round(respawnY, 2)}) より下です。SDK はこのスポーン地点が正しく機能しないと警告します",
                        path: CctTargets.GetPath(t), value: Round(pos.y, 3), limit: Round(respawnY, 3));
                    break;
                }
            }

            // Ground under each spawn (estimate): ray down against layers the local player collides with.
            int mask = PlayerCollisionMask(ctx);
            try { Physics.SyncTransforms(); }
            catch (Exception) { /* physics module unavailable: rays may use stale poses */ }
            var items = new List<object>();
            int noGround = 0;
            var noGroundPaths = new List<string>();
            foreach (var t in valid.Take(Math.Max(ctx.TopN, 1)))
            {
                var pos = t.position;
                bool tooFar = !(Mathf.Abs(pos.x) < WorldMaxSpawnOffset && Mathf.Abs(pos.y) < WorldMaxSpawnOffset && Mathf.Abs(pos.z) < WorldMaxSpawnOffset);
                var entry = new Dictionary<string, object>
                {
                    ["path"] = CctTargets.GetPath(t),
                    ["position"] = Vec(pos),
                    ["too_far"] = tooFar,
                    ["below_respawn_height"] = !(pos.y >= respawnY),
                };
                float distance = Mathf.Clamp(pos.y - respawnY + 0.5f, 1f, 5000f);
                if (TryRaycast(pos + Vector3.up * 0.5f, distance, mask, out var hit))
                {
                    entry["ground"] = new Dictionary<string, object>
                    {
                        ["collider"] = hit.collider != null ? CctTargets.GetPath(hit.collider.transform) : null,
                        ["distance_below"] = Round(hit.distance - 0.5f, 3),
                        ["layer"] = hit.collider != null ? LayerName(hit.collider.gameObject.layer) : null,
                    };
                }
                else
                {
                    entry["ground"] = null;
                    noGround++;
                    noGroundPaths.Add(CctTargets.GetPath(t));
                }
                items.Add(entry);
            }
            s["items"] = items;
            if (valid.Count > items.Count) s["items_omitted"] = valid.Count - items.Count;
            if (noGround > 0)
                ctx.Issue(CctSeverity.Info, "SPAWN_NO_GROUND",
                    $"スポーン地点 {noGround} 箇所の真下に、プレイヤーが乗れる (Trigger でない) コライダーが見つかりません。落下してリスポーンを繰り返すおそれがあります (エディタ上の Raycast による推定)",
                    path: noGroundPaths[0], value: Grouped(noGround, noGroundPaths));
        }

        static bool TryRaycast(Vector3 origin, float distance, int mask, out RaycastHit hit)
        {
            try
            {
                return Physics.Raycast(origin, Vector3.down, out hit, distance, mask, QueryTriggerInteraction.Ignore);
            }
            catch (Exception)
            {
                hit = default;
                return false;
            }
        }

        static string Vec3Text(Vector3 v) =>
            $"{Round(v.x, 1)}, {Round(v.y, 1)}, {Round(v.z, 1)}";

        /// <summary>Layers the local player (PlayerLocal) collides with: VRChat's matrix for 0..21, the project's for 22..31.</summary>
        static int PlayerCollisionMask(Ctx ctx)
        {
            int mask = 0;
            for (int l = 0; l < 32; l++)
            {
                bool collides = l < 22 && ctx.ExpectedCollisionRows != null && ctx.ExpectedCollisionRows.Length > LayerPlayerLocal
                    ? (ctx.ExpectedCollisionRows[LayerPlayerLocal] & (1 << l)) != 0
                    : !Physics.GetIgnoreLayerCollision(LayerPlayerLocal, l);
                if (collides) mask |= 1 << l;
            }
            return mask;
        }

        static void ReadReferenceCamera(Ctx ctx, Dictionary<string, object> d, Item descriptor, SerializedObject so)
        {
            var prop = so.FindProperty("ReferenceCamera");
            var go = CctSerialized.GetObject(prop) as GameObject;
            if (go == null)
            {
                d["reference_camera"] = null;
                bool missing = CctSerialized.IsMissingReference(prop);
                ctx.Issue(CctSeverity.Info, "REFERENCE_CAMERA_MISSING",
                    missing
                        ? "Reference Camera が削除済みのオブジェクトを指しています。クリップ面やポストプロセスの設定がプレイヤーのカメラに反映されません"
                        : "Reference Camera が未設定です。クリップ面 (Near/Far) やポストプロセスをプレイヤーのカメラに反映させたい場合は設定してください",
                    path: descriptor.O.Path);
                return;
            }
            ctx.ReferenceCamera = go;
            var rc = new Dictionary<string, object>
            {
                ["path"] = EditorUtility.IsPersistent(go) ? null : CctTargets.GetPath(go),
                ["asset"] = EditorUtility.IsPersistent(go) ? AssetDatabase.GetAssetPath(go) : null,
                ["active"] = go.activeInHierarchy,
            };
            d["reference_camera"] = rc;
            if (!go.TryGetComponent<Camera>(out var cam))
            {
                rc["has_camera"] = false;
                ctx.Issue(CctSeverity.Warning, "REFERENCE_CAMERA_NO_CAMERA",
                    "Reference Camera に指定したオブジェクトに Camera コンポーネントがありません。設定が反映されません",
                    path: CctTargets.GetPath(go));
                return;
            }
            rc["has_camera"] = true;
            rc["near_clip"] = Round(cam.nearClipPlane, 4);
            rc["far_clip"] = Round(cam.farClipPlane, 2);
            rc["clear_flags"] = cam.clearFlags.ToString();
            rc["culling_mask"] = MaskToNames(cam.cullingMask);
            rc["allow_hdr"] = cam.allowHDR;
            rc["allow_msaa"] = cam.allowMSAA;
            if (cam.nearClipPlane < ReferenceNearMin || cam.nearClipPlane > ReferenceNearMax)
                ctx.Issue(CctSeverity.Info, "REFERENCE_CAMERA_NEAR_CLAMPED",
                    $"Reference Camera の Near Clip ({Round(cam.nearClipPlane, 4)}) は VRChat で 0.001〜0.05 にクランプされると推定されます (docs は Udon の VRCCameraSettings について「Reference Camera と同様の制約」と記載)",
                    path: CctTargets.GetPath(go), value: Round(cam.nearClipPlane, 4), limit: new object[] { ReferenceNearMin, ReferenceNearMax });
        }

        // ------------------------------------------------------------------ layers

        /// <summary>SDK project layer setup: UpdateLayers.AreLayersSetup / IsCollisionLayerMatrixSetup ([WB] A4/C10).</summary>
        static void AuditLayerSetup(Ctx ctx)
        {
            var layers = new Dictionary<string, object>();
            ctx.R.Put("layers", layers);
            var setup = new Dictionary<string, object>();
            layers["setup"] = setup;

            LoadExpectedLayers(out var expectedNames, out int numLayers, out var collision, out var source);
            setup["expected_source"] = source;
            setup["num_layers"] = numLayers;

            // Collision rows 0..numLayers-1 (layerCollisionArr is 32x32; the SDK compares only i,j < numLayers).
            var rows = new int[numLayers];
            for (int i = 0; i < numLayers; i++)
                for (int j = 0; j < numLayers; j++)
                    if (collision[i * 32 + j]) rows[i] |= 1 << j;
            ctx.ExpectedCollisionRows = rows;

            var nameMismatches = new List<object>();
            for (int i = 3; i < numLayers && i < expectedNames.Length; i++)
            {
                var actual = LayerMask.LayerToName(i);
                if (actual != expectedNames[i])
                    nameMismatches.Add(new Dictionary<string, object> { ["index"] = i, ["expected"] = expectedNames[i], ["actual"] = actual });
            }
            var pairMismatches = new List<object>();
            int pairCount = 0;
            for (int i = 0; i < numLayers; i++)
                for (int j = i; j < numLayers; j++)
                {
                    bool expected = collision[i * 32 + j];
                    bool actual = !Physics.GetIgnoreLayerCollision(i, j);
                    if (expected == actual) continue;
                    pairCount++;
                    if (pairMismatches.Count < ctx.TopN)
                        pairMismatches.Add(new Dictionary<string, object>
                        {
                            ["a"] = $"{i} {LayerName(i)}", ["b"] = $"{j} {LayerName(j)}", ["expected_collide"] = expected, ["actual_collide"] = actual,
                        });
                }
            bool namesOk = nameMismatches.Count == 0, matrixOk = pairCount == 0;

            // Ask the SDK itself (VRCCore-Editor.dll, global type UpdateLayers): read-only static methods.
            bool? sdkNames = InvokeSdkBool(TUpdateLayers, "AreLayersSetup");
            bool? sdkMatrix = InvokeSdkBool(TUpdateLayers, "IsCollisionLayerMatrixSetup");
            setup["sdk_layers_ok"] = sdkNames;
            setup["sdk_collision_matrix_ok"] = sdkMatrix;
            setup["layers_ok"] = namesOk;
            setup["collision_matrix_ok"] = matrixOk;
            setup["mismatched_layers"] = nameMismatches;
            setup["mismatched_pairs_count"] = pairCount;
            setup["mismatched_pairs"] = pairMismatches;
            if (sdkNames == null || sdkMatrix == null)
                ctx.Issue(CctSeverity.Info, "LAYER_SDK_CHECK_UNAVAILABLE",
                    "SDK の UpdateLayers を呼べなかったため、レイヤー/衝突マトリクスはこのツール自身の比較で判定しました");

            bool layersBad = sdkNames.HasValue ? !sdkNames.Value : !namesOk;
            bool matrixBad = sdkMatrix.HasValue ? !sdkMatrix.Value : !matrixOk;
            if (layersBad)
                ctx.Issue(CctSeverity.Error, "LAYERS_NOT_SETUP",
                    "プロジェクトのレイヤー名 (3〜21) が VRChat の設定と一致しません。SDK はこの状態ではビルドしません。VRChat SDK > Show Control Panel > Builder の「Setup Layers for VRChat」を実行してください (既存のカスタムレイヤーは下へずれるので付け直しが必要)",
                    value: nameMismatches.Take(ExampleCap).ToList());
            if (matrixBad)
                ctx.Issue(CctSeverity.Error, "COLLISION_MATRIX_NOT_SETUP",
                    $"Physics の衝突マトリクス (レイヤー 0〜21) が VRChat の設定と {pairCount} 組で異なります。SDK はこの状態ではビルドしません。Control Panel の「Setup Collision Matrix」を実行してください",
                    value: pairMismatches.Take(ExampleCap).ToList());
            if (sdkNames.HasValue && sdkNames.Value != namesOk || sdkMatrix.HasValue && sdkMatrix.Value != matrixOk)
                setup["note"] = "SDK の判定とこのツールの比較が一致しません。SDK の判定 (sdk_*) を優先しています";
        }

        static void LoadExpectedLayers(out string[] names, out int numLayers, out bool[] collision, out string source)
        {
            names = null;
            numLayers = 0;
            collision = null;
            source = null;
            try
            {
                // UpdateLayers.ProjectSettings: Resources.Load<VRCProjectSettings>("VRCProjectSettings") ([UL]).
                var prefab = Resources.Load<GameObject>(TProjectSettings);
                Component settings = null;
                if (prefab != null)
                    foreach (var c in prefab.GetComponents<Component>())
                        if (c != null && CctSerialized.IsType(c, TProjectSettings)) { settings = c; break; }
                using (var so = TrySerializeAsset(settings))
                {
                    if (so != null)
                    {
                        var layersProp = so.FindProperty("layers");
                        var arrProp = so.FindProperty("layerCollisionArr");
                        int n = CctSerialized.GetInt(so.FindProperty("numLayers"));
                        if (layersProp != null && layersProp.isArray && arrProp != null && arrProp.isArray && arrProp.arraySize >= 1024 && n > 0 && n <= 32)
                        {
                            names = CctSerialized.Elements(layersProp).Select(p => CctSerialized.GetString(p, "")).ToArray();
                            collision = new bool[1024];
                            for (int i = 0; i < 1024; i++) collision[i] = arrProp.GetArrayElementAtIndex(i).boolValue;
                            numLayers = Math.Min(n, names.Length);
                            source = AssetDatabase.GetAssetPath(prefab);
                        }
                    }
                }
            }
            catch (Exception) { names = null; }

            if (names != null && collision != null && numLayers > 0) return;
            names = FallbackLayerNames;
            numLayers = FallbackLayerNames.Length;
            collision = new bool[1024];
            for (int i = 0; i < FallbackCollisionRows.Length; i++)
                for (int j = 0; j < 32; j++)
                    if ((FallbackCollisionRows[i] & (1 << j)) != 0) collision[i * 32 + j] = true;
            source = "embedded copy of VRCProjectSettings.prefab (SDK 3.10.5)";
        }

        /// <summary>Calls a public static parameterless bool method of an SDK editor type; null when unavailable or it throws.</summary>
        static bool? InvokeSdkBool(string typeName, string method)
        {
            try
            {
                var t = CctSerialized.FindType(typeName);
                if (t == null) return null;
                var asm = t.Assembly.GetName().Name ?? "";
                if (!asm.StartsWith("VRC", StringComparison.Ordinal)) return null;
                var m = t.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (m == null || m.ReturnType != typeof(bool)) return null;
                return (bool)m.Invoke(null, null);
            }
            catch (Exception) { return null; }
        }

        /// <summary>Objects per layer, reserved layers in use, MirrorReflection renderers (content scope).</summary>
        static void AuditLayerUsage(Ctx ctx)
        {
            if (!(ctx.R.Data.TryGetValue("layers", out var lo) && lo is Dictionary<string, object> layers))
                ctx.R.Put("layers", layers = new Dictionary<string, object>());
            var counts = new int[32];
            var examples = new List<string>[32];
            foreach (var o in ctx.Objects)
            {
                if (!o.InScope || o.EditorOnly) continue;
                int l = o.Go.layer;
                if (l < 0 || l > 31) continue;
                counts[l]++;
                if (IsReservedLayer(l))
                {
                    if (examples[l] == null) examples[l] = new List<string>();
                    if (examples[l].Count < ExampleCap) examples[l].Add(o.Path);
                }
            }
            var perLayer = new Dictionary<string, object>();
            for (int l = 0; l < 32; l++)
                if (counts[l] > 0) perLayer[$"{l} {LayerName(l)}"] = counts[l];
            layers["objects_per_layer"] = perLayer;

            var reserved = new List<object>();
            for (int l = 0; l < 32; l++)
            {
                if (!IsReservedLayer(l) || counts[l] == 0) continue;
                string why = l == LayerItem || l == LayerReserved6 || l == LayerReserved7 || l == LayerReserved4
                    ? "アップロード時に Default レイヤーへ移されます"
                    : l == LayerEditorPreview
                        ? "Unity エディタのプレビューが使うレイヤーです"
                        : "VRChat 内部用 (メニュー・ネームプレート / コントローラー表示) のレイヤーです";
                reserved.Add(new Dictionary<string, object> { ["layer"] = $"{l} {LayerName(l)}", ["count"] = counts[l], ["examples"] = Examples(examples[l]) });
                ctx.Issue(CctSeverity.Warning, "RESERVED_LAYER_USED",
                    $"レイヤー {l} ({LayerName(l)}) に {counts[l]} 個のオブジェクトがあります。{why}。docs は使用を避けるよう案内しています",
                    path: examples[l]?.FirstOrDefault(), value: Grouped(counts[l], examples[l] ?? new List<string>()));
            }
            layers["reserved_layer_objects"] = reserved;

            var user = new List<object>();
            for (int l = 22; l < 32; l++)
                if (counts[l] > 0 || !string.IsNullOrEmpty(LayerMask.LayerToName(l)))
                    user.Add(new Dictionary<string, object> { ["index"] = l, ["name"] = LayerMask.LayerToName(l), ["objects"] = counts[l] });
            layers["user_layers"] = user;

            var mirrorLayerRenderers = ctx.Audited(K.Renderer).Where(i => i.C.gameObject.layer == LayerMirrorReflection).ToList();
            if (mirrorLayerRenderers.Count > 0)
                ctx.Issue(CctSeverity.Info, "MIRROR_REFLECTION_LAYER_RENDERER",
                    $"MirrorReflection レイヤーに Renderer が {mirrorLayerRenderers.Count} 個あります。このレイヤーの Renderer はミラーの中にだけ映り、通常のカメラには映りません (意図どおりか確認)",
                    path: PathOf(mirrorLayerRenderers[0]), value: Grouped(mirrorLayerRenderers.Count, mirrorLayerRenderers.Select(PathOf)));
        }

        static bool IsReservedLayer(int l) =>
            l == LayerItem || l == LayerReserved6 || l == LayerReserved7 || l == LayerReserved4 ||
            l == LayerInternalUi || l == LayerHardwareObjects || l == LayerEditorPreview;
    }
}
