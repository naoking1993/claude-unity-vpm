using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Naoking.ClaudeCreatorTools
{
    public static partial class CctWorldAudit
    {
        // Mobile (Android / iOS share the limits: docs/platforms/iOS/setting-up-unity-for-creating-ios-content.md L38).
        // "budget approximately 250,000 triangles for your world in total" (docs/platforms/android/quest-content-optimization.md L58).
        const long MobileTriangleBudget = 250000;
        // "aim for using 1k (1024x1024) resolution textures at maximum" (quest-content-optimization.md L104).
        const int MobileTextureMax = 1024;
        // "alpha cutout counts as transparency" (cross-platform-setup.md L107): render queue AlphaTest (2450) and above.
        const int AlphaTestQueue = 2450;
        // [WV] WorldValidation.ShaderWhiteList (3.10.5 L531-546), compared by exact shader name on every Renderer's
        // sharedMaterials under the descriptor scene's roots, inactive included (#if UNITY_ANDROID || UNITY_IOS, [WB] L693-705).
        // The list is read from the installed SDK (public static string[]) when possible; this 3.10.5 copy is the fallback.
        static readonly string[] EmbeddedMobileShaderWhiteList =
        {
            "VRChat/Mobile/Standard Lite", "VRChat/Mobile/Diffuse", "VRChat/Mobile/Bumped Diffuse",
            "VRChat/Mobile/Bumped Mapped Specular", "VRChat/Mobile/Toon Lit", "VRChat/Mobile/MatCap Lit",
            "VRChat/Mobile/Lightmapped", "VRChat/Mobile/Skybox", "VRChat/Mobile/Particles/Additive",
            "VRChat/Mobile/Particles/Multiply", "VRChat/Mobile/World/Supersampled UI", "FX/MirrorReflection", "UI/Default",
        };
        // Shipped mobile shaders the SDK list does not contain (name mismatch / added later): the SDK still warns, but the
        // docs list them as mobile shaders (docs/platforms/android/quest-content-limitations.md L28-39). The Outline variant
        // is "only supported on PC, mobile devices will fall back to the non-outline variant automatically" (same table).
        const string ToonStandardOutlineShader = "VRChat/Mobile/Toon Standard (Outline)";
        static readonly HashSet<string> DocMobileShadersMissingFromSdkList = new HashSet<string>(StringComparer.Ordinal)
        {
            "VRChat/Mobile/Worlds/Supersampled UI", "VRChat/Mobile/Toon Standard", ToonStandardOutlineShader,
        };

        const int MaxPropertiesPerComponent = 20000;
        const int MissingReferenceListCap = 100;

        // ------------------------------------------------------------------ components

        static void AuditComponents(Ctx ctx)
        {
            var d = new Dictionary<string, object>();
            ctx.R.Put("components", d);

            // Missing scripts (counted with GameObjectUtility.GetMonoBehavioursWithMissingScriptCount during the scan).
            int missingObjects = 0, missingTotal = 0, missingEditorOnly = 0;
            var missingExamples = new List<string>();
            foreach (var kv in ctx.MissingScripts)
            {
                var o = kv.Key;
                if (!o.InScope) continue;
                if (o.EditorOnly) { missingEditorOnly += kv.Value; continue; }
                missingObjects++;
                missingTotal += kv.Value;
                if (missingExamples.Count < ExampleCap) missingExamples.Add(o.Path);
                ctx.Issue(CctSeverity.Error, "MISSING_SCRIPT",
                    $"スクリプトが見つからないコンポーネント (Missing Script) が {kv.Value} 個あります。削除済みのスクリプトか、コンパイルできていないパッケージの残骸です",
                    path: o.Path, value: kv.Value);
            }
            d["missing_scripts"] = new Dictionary<string, object>
            {
                ["objects"] = missingObjects, ["components"] = missingTotal, ["in_editor_only_objects"] = missingEditorOnly, ["examples"] = Examples(missingExamples),
            };

            // Prefab instances whose prefab asset is gone ("Missing Prefab" placeholders): report the outermost one only.
            var missingPrefabs = new List<string>();
            foreach (var o in ctx.Objects)
            {
                if (!o.InScope || o.EditorOnly) continue;
                bool missing;
                try { missing = PrefabUtility.IsPrefabAssetMissing(o.Go); }
                catch (Exception) { missing = false; }
                if (!missing) continue;
                var parent = o.Go.transform.parent;
                bool parentMissing = false;
                if (parent != null)
                {
                    try { parentMissing = PrefabUtility.IsPrefabAssetMissing(parent.gameObject); }
                    catch (Exception) { parentMissing = false; }
                }
                if (!parentMissing) missingPrefabs.Add(o.Path);
            }
            foreach (var p in missingPrefabs)
                ctx.Issue(CctSeverity.Warning, "MISSING_PREFAB",
                    "プレハブアセットが見つからないプレハブインスタンス (Missing Prefab) です。元のアセットを削除・移動したか、インポートが不完全です。中身は失われているので、アセットを戻すか削除してください",
                    path: p);
            d["missing_prefabs"] = Grouped(missingPrefabs.Count, missingPrefabs);

            // SDK2 leftovers ([WB] C6 VRC_EventHandler, [PB] C1 SDK2 + SDK3 mix) over every loaded scene.
            var handlers = ctx.Get(K.EventHandler);
            if (handlers.Count > 0)
                ctx.Issue(CctSeverity.Error, "SDK2_EVENT_HANDLER",
                    $"SDK2 の VRC_EventHandler が {handlers.Count} 個あります。SDK はこの状態ではビルドしません (削除してください)",
                    path: PathOf(handlers[0]), value: Grouped(handlers.Count, handlers.Select(PathOf)));
            var sdk2 = ctx.Get(K.Sdk2);
            if (sdk2.Count > 0)
                ctx.Issue(CctSeverity.Error, "SDK2_COMPONENT",
                    $"VRChat SDK2 のコンポーネントが {sdk2.Count} 個あります。SDK3 ワールドとは混在できず、SDK はビルドしません",
                    path: PathOf(sdk2[0]), value: Grouped(sdk2.Count, sdk2.Select(PathOf)));

            // Allowlist ([WV] ComponentTypeWhiteListCommon + ComponentTypeWhiteListSdk3; the SDK does NOT check this when building,
            // the client is expected to strip unknown components when loading the world: research 2.6-F / 6.1).
            var byType = new List<object>();
            var notAllowed = new List<object>();
            int editorOnlyComponents = 0;
            var examplesByType = new Dictionary<Type, List<string>>();
            foreach (var item in ctx.Components)
            {
                if (!item.Audited) continue;
                var t = item.C.GetType();
                if (!examplesByType.TryGetValue(t, out var list)) examplesByType[t] = list = new List<string>();
                if (list.Count < ExampleCap) list.Add(item.O.Path);
            }
            foreach (var kv in ctx.AuditedTypeCounts.OrderByDescending(k => k.Value).ThenBy(k => k.Key.FullName, StringComparer.Ordinal))
            {
                var type = kv.Key;
                var v = Classify(type);
                if (byType.Count < ctx.TopN * 2)
                    byType.Add(new Dictionary<string, object> { ["type"] = FindTypeName(type), ["count"] = kv.Value });
                if ((v.Kinds & K.EditorOnlyComponent) != 0) { editorOnlyComponents += kv.Value; continue; }
                if (v.USharp) continue; // UdonSharpBehaviour proxies: the backing UdonBehaviour is what runs in VRChat.
                // ClientSim's runtime objects (Play mode only; ClientSim is force-disabled for uploads).
                if ((type.Namespace ?? "").StartsWith("VRC.SDK3.ClientSim", StringComparison.Ordinal)) continue;
                examplesByType.TryGetValue(type, out var examples);
                string asm = SafeAssemblyName(type);
                if (v.AllowedVia == null)
                {
                    // ProBuilder removes its own components from the build by default (Preferences > ProBuilder >
                    // "Strip ProBuilder Scripts on Build"); the MeshFilter/MeshRenderer it generated stay.
                    bool probuilder = (type.Namespace ?? "").StartsWith("UnityEngine.ProBuilder", StringComparison.Ordinal);
                    var strippedBy = probuilder ? null : KnownBuildStripper(type);
                    var processors = probuilder || strippedBy != null ? null : SceneProcessorsReferencing(asm);
                    // Tagging the object EditorOnly strips its whole subtree, so never suggest it over shipping content.
                    var shipping = probuilder ? null : ShippingComponentUnder(ctx, type);
                    var entry = new Dictionary<string, object>
                    {
                        ["type"] = FindTypeName(type), ["assembly"] = asm, ["count"] = kv.Value, ["examples"] = Examples(examples ?? new List<string>()),
                        ["reason"] = probuilder ? "probuilder_stripped_at_build"
                            : strippedBy != null ? "stripped_at_build"
                            : processors != null ? "scene_processor_may_strip" : "not_in_allowlist",
                    };
                    if (strippedBy != null) entry["stripped_by"] = strippedBy.Value.Processor;
                    if (processors != null) entry["scene_processors"] = processors.Cast<object>().ToList();
                    if (shipping != null) entry["shipping_component_in_subtree"] = shipping;
                    notAllowed.Add(entry);
                    string message;
                    if (probuilder)
                        message = $"'{FindTypeName(type)}' ({kv.Value} 個) は許可リストにありませんが、ProBuilder の既定設定 (Strip ProBuilder Scripts on Build) でビルド時に除去され、メッシュだけが残るため通常は問題ありません (設定を OFF にしている場合は除去されずクライアントで消えます)";
                    else if (strippedBy != null)
                        message = $"'{FindTypeName(type)}' ({kv.Value} 個) は許可リストにありませんが、ビルド時に {strippedBy.Value.Label}ため、アップロードされるワールドには含まれません (対応不要)。" +
                                  (shipping != null ? $"EditorOnly タグは付けないでください (子の '{shipping}' などもビルドから消えて動かなくなります)" : "");
                    else if (processors != null)
                        message = $"'{FindTypeName(type)}' ({kv.Value} 個) は許可リストにありません。同じパッケージの '{processors[0]}' がビルド時にシーンを処理するので、そこで除去・変換される可能性があります (推定。処理の中身は確認していません)。ビルド後も残っていれば、VRChat がワールドを読み込むときに除去すると推定されます。" +
                                  NotAllowedHint(type, v, asm, shipping);
                    else
                        message = $"'{FindTypeName(type)}' ({kv.Value} 個) は SDK3 ワールドの許可リストにありません。VRChat がワールドを読み込むときに除去されると推定されます (SDK はビルド時にこれを検査しないため、アップロードはできてしまいます)。" +
                                  NotAllowedHint(type, v, asm, shipping);
                    ctx.Issue(probuilder || strippedBy != null || processors != null ? CctSeverity.Info : CctSeverity.Warning, "NOT_WHITELISTED", message,
                        path: examples?.FirstOrDefault(), value: Grouped(kv.Value, examples ?? new List<string>()));
                }
                else if (!v.AllowedExact && !v.ClientAssembly)
                {
                    notAllowed.Add(new Dictionary<string, object>
                    {
                        ["type"] = FindTypeName(type), ["assembly"] = asm, ["count"] = kv.Value, ["examples"] = Examples(examples ?? new List<string>()),
                        ["reason"] = "derived_in_project_assembly", ["allowed_base"] = v.AllowedVia,
                    });
                    ctx.Issue(CctSeverity.Warning, "NOT_WHITELISTED_DERIVED",
                        $"'{FindTypeName(type)}' ({kv.Value} 個) は許可された型 {v.AllowedVia} の派生ですが、プロジェクトのアセンブリ '{asm}' にあります。VRChat は独自の C# スクリプトを読み込まないため動作しないと推定されます (UdonSharp で実装するか、VRChat 対応版のアセットを使ってください)",
                        path: examples?.FirstOrDefault(), value: Grouped(kv.Value, examples ?? new List<string>()));
                }
            }
            if (editorOnlyComponents > 0)
                ctx.Issue(CctSeverity.Info, "EDITOR_ONLY_COMPONENTS",
                    $"IEditorOnly のコンポーネントが {editorOnlyComponents} 個あります。エディタ専用なので許可リストの対象外です (VRChat では動きません)",
                    value: editorOnlyComponents);

            d["total"] = ctx.Components.Count(i => i.Audited);
            d["types"] = ctx.AuditedTypeCounts.Count;
            d["by_type"] = byType;
            d["not_whitelisted"] = notAllowed.Take(ctx.TopN * 2).ToList();
            d["not_whitelisted_types"] = notAllowed.Count;
            d["allowlist_source"] = Lists.AllowlistSource;
            d["editor_only"] = new Dictionary<string, object>
            {
                ["tagged_objects"] = ctx.Objects.Count(o => o.InScope && o.EditorOnly),
                ["ieditoronly_components"] = editorOnlyComponents,
            };

            ScanAllMissingReferences(ctx, d);
        }

        static string SafeAssemblyName(Type t)
        {
            try { return t.Assembly.GetName().Name; }
            catch (Exception) { return null; }
        }

        static string NotAllowedHint(Type type, TypeVerdict v, string asm, string shipping)
        {
            if ((v.Kinds & K.RawVideoPlayer) != 0) return "動画には VRC Unity Video Player / VRC AVPro Video Player を使ってください";
            if ((v.Kinds & K.LegacyAnimation) != 0) return "レガシー Animation の代わりに Animator を使ってください";
            if ((v.Kinds & K.AvatarDescriptor) != 0) return "アバター用のコンポーネントです。ワールドでは使えません";
            if ((v.Kinds & K.EventHandler) != 0 || (v.Kinds & K.Sdk2) != 0) return "SDK2 のコンポーネントです";
            // VRWorld Toolkit (community) reports Bakery lights the same way: "external scripts get removed in the upload
            // process" and the client logs errors for them; tagging the object EditorOnly removes them at build instead.
            if (asm != null && (asm.StartsWith("Assembly-CSharp", StringComparison.Ordinal) || !IsClientAssembly(asm)))
                return shipping != null
                    ? $"独自の C# スクリプト (MonoBehaviour) はワールドに含まれません。この GameObject か子に VRChat で使われる '{shipping}' があるので、EditorOnly タグは付けないでください (子ごとビルドから消えます)。VRChat 内で動かしたい処理は UdonSharp で実装してください"
                    : "独自の C# スクリプト (MonoBehaviour) はワールドに含まれません。ベイク用などエディタでだけ使うものなら、その GameObject のタグを EditorOnly にするとビルド時に除去されます (読み込み時のエラーも出なくなります)。VRChat 内で動かしたい処理は UdonSharp で実装してください";
            return "";
        }

        // ------------------------------------------------------------------ scene build processors

        /// <summary>
        /// Components a third-party scene build processor destroys before the bundle is written (checked in its source), so the
        /// client never sees them. iwaSync3: IwaSync3BuildProcessor (IProcessSceneWithReport) copies each component's settings
        /// into its Udon children, moves them out and runs DestroyImmediate on every IwaSync3 / Playlist / Screen / Speaker of
        /// the built scene (IwaSync3BuildProcessor.cs L14-53, Cleaning L99-103). Used only while the processor is compiled.
        /// </summary>
        const string IwaSync3StripLabel = "iwaSync3 の IwaSync3BuildProcessor が設定を Udon に写してから削除する";

        static readonly Dictionary<string, (string Processor, string Label)> KnownBuildStrippedTypes =
            new Dictionary<string, (string Processor, string Label)>(StringComparer.Ordinal)
            {
                [TIwaSync3] = (TIwaSync3BuildProcessor, IwaSync3StripLabel),
                ["HoshinoLabs.IwaSync3.Playlist"] = (TIwaSync3BuildProcessor, IwaSync3StripLabel),
                ["HoshinoLabs.IwaSync3.Screen"] = (TIwaSync3BuildProcessor, IwaSync3StripLabel),
                [TIwaSync3Speaker] = (TIwaSync3BuildProcessor, IwaSync3StripLabel),
            };

        static (string Processor, string Label)? KnownBuildStripper(Type type)
        {
            if (!KnownBuildStrippedTypes.TryGetValue(FindTypeName(type) ?? "", out var known)) return null;
            return CctSerialized.FindType(known.Processor) != null ? known : ((string, string)?)null;
        }

        sealed class SceneProcessor
        {
            public string Name, Assembly;
            public HashSet<string> References;
        }

        static List<SceneProcessor> _sceneProcessors;

        /// <summary>
        /// Scene build processors (IProcessSceneWithReport implementers and [PostProcessScene] methods) compiled in package or
        /// asmdef editor assemblies. Unity/VRChat processors are left out, and so are the predefined Assembly-CSharp* assemblies,
        /// which reference every runtime assembly and so would match everything.
        /// </summary>
        static List<SceneProcessor> SceneProcessors()
        {
            if (_sceneProcessors != null) return _sceneProcessors;
            var types = new List<Type>();
            try { types.AddRange(TypeCache.GetTypesDerivedFrom<UnityEditor.Build.IProcessSceneWithReport>().Where(t => !t.IsAbstract && !t.IsInterface)); }
            catch (Exception) { /* TypeCache unavailable */ }
            try { types.AddRange(TypeCache.GetMethodsWithAttribute<UnityEditor.Callbacks.PostProcessSceneAttribute>().Select(m => m.DeclaringType)); }
            catch (Exception) { /* TypeCache unavailable */ }
            var list = new List<SceneProcessor>();
            foreach (var t in types.Where(t => t != null).Distinct())
            {
                var asm = SafeAssemblyName(t);
                if (asm == null || IsPredefinedAssembly(asm) || IsClientAssembly(asm) || asm.StartsWith("UnityEditor", StringComparison.Ordinal)) continue;
                HashSet<string> refs;
                try { refs = new HashSet<string>(t.Assembly.GetReferencedAssemblies().Select(a => a.Name), StringComparer.Ordinal); }
                catch (Exception) { continue; }
                list.Add(new SceneProcessor { Name = FindTypeName(t), Assembly = asm, References = refs });
            }
            return _sceneProcessors = list;
        }

        static bool IsPredefinedAssembly(string asm) => asm.StartsWith("Assembly-CSharp", StringComparison.Ordinal);

        /// <summary>Scene processors whose assembly is, or references, <paramref name="asm"/> (null when none: estimate only).</summary>
        static List<string> SceneProcessorsReferencing(string asm)
        {
            if (asm == null || IsPredefinedAssembly(asm)) return null;
            var names = SceneProcessors().Where(p => p.Assembly == asm || p.References.Contains(asm)).Select(p => p.Name).ToList();
            return names.Count > 0 ? names : null;
        }

        /// <summary>
        /// A component that ships and runs in VRChat (allowlisted, not a Transform, not on an EditorOnly object) on an audited
        /// instance of <paramref name="type"/> or below it: tagging that GameObject EditorOnly would strip it too. Null if none.
        /// </summary>
        static string ShippingComponentUnder(Ctx ctx, Type type)
        {
            var seen = new HashSet<GameObject>();
            foreach (var item in ctx.Components)
            {
                if (!item.Audited || item.C == null || item.C.GetType() != type || !seen.Add(item.C.gameObject)) continue;
                if (seen.Count > ExampleCap * 5) break;
                foreach (var c in item.C.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c is Transform) continue;
                    var o = ObjOf(ctx, c);
                    if (o != null && o.EditorOnly) continue;
                    var cv = Classify(c.GetType());
                    if ((cv.Kinds & K.EditorOnlyComponent) != 0 || cv.AllowedVia == null || (!cv.AllowedExact && !cv.ClientAssembly)) continue;
                    return FindTypeName(c.GetType());
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ missing references

        /// <summary>Scans one component's SerializedObject for references to deleted assets/objects (once per component).</summary>
        static void ScanMissingReferences(Ctx ctx, Item item, SerializedObject so)
        {
            if (!ctx.CheckReferences || so == null || item.C == null || !ctx.RefScanned.Add(item.C)) return;
            ctx.RefScanCount++;
            List<string> missing = null;
            try
            {
                var it = so.GetIterator();
                bool enter = true;
                int visited = 0;
                while (it.Next(enter))
                {
                    enter = true;
                    if (++visited > MaxPropertiesPerComponent) break;
                    // Strings are char arrays to Next(true); long ones (UdonBehaviour's serialized public variables) would
                    // exhaust the property cap before the references after them are reached.
                    if (it.propertyType == SerializedPropertyType.String)
                    {
                        enter = false;
                        continue;
                    }
                    if (it.propertyType == SerializedPropertyType.Generic && it.isArray && !ArrayMayHoldReferences(it))
                    {
                        enter = false;
                        continue;
                    }
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var path = it.propertyPath;
                    if (path == "m_Script" || path == "m_GameObject" || path == "m_PrefabInstance" || path == "m_PrefabAsset" ||
                        path == "m_CorrespondingSourceObject" || path == "m_PrefabParentObject" || path == "m_PrefabInternal") continue;
                    if (it.objectReferenceValue == null && it.objectReferenceInstanceIDValue != 0)
                    {
                        if (missing == null) missing = new List<string>();
                        if (missing.Count < ExampleCap) missing.Add(path);
                        else missing.Add(null);
                    }
                }
            }
            catch (Exception) { return; }
            if (missing == null) return;
            int count = missing.Count;
            var paths = missing.Where(p => p != null).Cast<object>().ToList();
            ctx.MissingReferenceTotal += count;
            if (ctx.MissingReferences.Count < MissingReferenceListCap)
                ctx.MissingReferences.Add(new Dictionary<string, object>
                {
                    ["path"] = item.O.Path, ["component"] = item.C.GetType().Name, ["properties"] = paths, ["count"] = count,
                });
            ctx.Issue(CctSeverity.Warning, "MISSING_REFERENCE",
                $"{item.C.GetType().Name} の参照 {count} 個が削除済みのアセット/オブジェクトを指しています (Missing): {string.Join(", ", paths.Take(5))}",
                path: item.O.Path, value: paths);
        }

        /// <summary>
        /// Missing-reference scan of a SerializedObject a section already opened, under the same caps as ScanAllMissingReferences
        /// (max_reference_scan / time budget). Over budget it does nothing, so the final pass counts the component as skipped.
        /// </summary>
        static void ScanMissingReferencesWithinBudget(Ctx ctx, Item item, SerializedObject so)
        {
            if (!ctx.CheckReferences || !item.Audited || item.C == null || ctx.RefScanned.Contains(item.C)) return;
            if (ctx.RefScanCount >= ctx.MaxReferenceScan || ctx.Watch.ElapsedMilliseconds > ctx.RefDeadlineMs) return;
            ScanMissingReferences(ctx, item, so);
        }

        /// <summary>False for arrays whose elements are plain values (cheap to skip, e.g. float[] or Vector3[] with many entries).</summary>
        static bool ArrayMayHoldReferences(SerializedProperty array)
        {
            try
            {
                if (array.arraySize == 0) return false;
                var first = array.GetArrayElementAtIndex(0);
                return first.propertyType == SerializedPropertyType.ObjectReference ||
                       first.propertyType == SerializedPropertyType.Generic ||
                       first.propertyType == SerializedPropertyType.ExposedReference ||
                       first.propertyType == SerializedPropertyType.ManagedReference;
            }
            catch (Exception) { return true; }
        }

        /// <summary>
        /// The <paramref name="targets"/> that serialized object references of <paramref name="so"/> point at (UdonBehaviour
        /// public variables, U# proxy fields, ...). Walks like the missing-reference scan; m_GameObject / m_Script skipped.
        /// </summary>
        static List<UnityEngine.Object> FindReferencesTo(SerializedObject so, HashSet<UnityEngine.Object> targets)
        {
            var found = new List<UnityEngine.Object>();
            if (so == null || targets == null || targets.Count == 0) return found;
            try
            {
                var it = so.GetIterator();
                bool enter = true;
                int visited = 0;
                while (it.Next(enter))
                {
                    enter = true;
                    if (++visited > MaxPropertiesPerComponent) break;
                    if (it.propertyType == SerializedPropertyType.String) { enter = false; continue; }
                    if (it.propertyType == SerializedPropertyType.Generic && it.isArray && !ArrayMayHoldReferences(it)) { enter = false; continue; }
                    if (it.propertyType != SerializedPropertyType.ObjectReference || it.propertyPath == "m_GameObject" || it.propertyPath == "m_Script") continue;
                    var value = it.objectReferenceValue;
                    if (value != null && targets.Contains(value) && !found.Contains(value)) found.Add(value);
                }
            }
            catch (Exception) { /* unreadable: keep what was found */ }
            return found;
        }

        static void ScanAllMissingReferences(Ctx ctx, Dictionary<string, object> d)
        {
            var refs = new Dictionary<string, object> { ["checked"] = ctx.CheckReferences };
            d["missing_references"] = refs;
            if (!ctx.CheckReferences) return;
            ctx.RefWatch = Stopwatch.StartNew();
            bool truncated = false;
            foreach (var item in ctx.Components)
            {
                if (!item.Audited || item.C == null || item.C is Transform || ctx.RefScanned.Contains(item.C)) continue;
                if (ctx.RefScanCount >= ctx.MaxReferenceScan || ctx.Watch.ElapsedMilliseconds > ctx.RefDeadlineMs)
                {
                    truncated = true;
                    ctx.RefScanSkipped++;
                    continue;
                }
                SerializedObject so;
                try { so = new SerializedObject(item.C); }
                catch (Exception) { continue; }
                using (so) ScanMissingReferences(ctx, item, so);
            }
            refs["scanned_components"] = ctx.RefScanCount;
            refs["skipped_components"] = ctx.RefScanSkipped;
            refs["components_with_missing"] = ctx.MissingReferences.Count;
            refs["missing_total"] = ctx.MissingReferenceTotal;
            refs["items"] = ctx.MissingReferences.Cast<object>().ToList();
            refs["elapsed_ms"] = ctx.RefWatch.ElapsedMilliseconds;
            if (truncated)
                ctx.Issue(CctSeverity.Info, "REFERENCE_SCAN_TRUNCATED",
                    $"Missing 参照の確認を {ctx.RefScanCount} コンポーネントで打ち切りました (残り {ctx.RefScanSkipped} 個は未確認)。max_reference_scan か timeout_seconds を増やすと全件確認します",
                    value: new Dictionary<string, object> { ["scanned"] = ctx.RefScanCount, ["skipped"] = ctx.RefScanSkipped, ["max_reference_scan"] = ctx.MaxReferenceScan });
        }

        // ------------------------------------------------------------------ Android / iOS

        static void AuditMobile(Ctx ctx)
        {
            var m = new Dictionary<string, object> { ["checked"] = ctx.CheckMobile };
            ctx.R.Put("android", m);
            if (!ctx.CheckMobile) return;
            var platforms = ctx.CheckedPlatforms().Where(p => p != "pc").ToList();
            m["platforms"] = platforms.Cast<object>().ToList();
            m["note"] = "Android と iOS は同じ制限です (docs)。ワールドのシェーダーは制限されませんが、SDK はモバイルで下記の許可リスト外を警告します";

            // [WB] L693-705 mirror: renderers of the descriptor's scene (all roots, inactive and EditorOnly included).
            int sceneIndex = ctx.Descriptor.IsValid ? ctx.Descriptor.O.Scene : -1;
            var shaderUse = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var item in ctx.Get(K.Renderer))
            {
                if (sceneIndex >= 0 ? item.O.Scene != sceneIndex : !item.O.InScope) continue;
                var r = item.C as Renderer;
                if (r == null) continue;
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null || mat.shader == null) continue;
                    var name = mat.shader.name;
                    if (Lists.MobileShaders.Contains(name)) continue;
                    if (!shaderUse.TryGetValue(name, out var list)) shaderUse[name] = list = new List<string>();
                    if (list.Count == 0 || list[list.Count - 1] != item.O.Path) list.Add(item.O.Path);
                }
            }
            // One issue for all shaders outside the list (the SDK prints one warning per shader name); the per-shader table is
            // android.shaders_outside_sdk_list. Docs mobile shaders missing from the SDK list get one separate info.
            var ordered = shaderUse.OrderByDescending(k => k.Value.Count).ThenBy(k => k.Key, StringComparer.Ordinal).ToList();
            var outsideList = ordered.Where(k => !DocMobileShadersMissingFromSdkList.Contains(k.Key)).ToList();
            var docOnly = ordered.Where(k => DocMobileShadersMissingFromSdkList.Contains(k.Key)).ToList();
            if (outsideList.Count > 0)
                ctx.Issue(CctSeverity.Warning, "ANDROID_SHADER_NOT_SUPPORTED",
                    $"モバイル (Android/iOS) 用の許可リストに無いシェーダーが {outsideList.Count} 種類あります ({string.Join(", ", outsideList.Take(5).Select(k => $"{k.Key} ×{k.Value.Count}"))}{(outsideList.Count > 5 ? " ほか" : "")})。" +
                    "SDK はモバイル向けビルドでシェーダーごとに \"World uses unsupported shader\" と警告します。ワールドでは禁止ではありませんが、性能のため VRChat/Mobile/* (ベイクしたライティングなら Lightmapped) が推奨です",
                    path: outsideList[0].Value[0], value: ShaderTable(outsideList, ctx.TopN));
            if (docOnly.Count > 0)
                ctx.Issue(CctSeverity.Info, "ANDROID_SHADER_NOT_SUPPORTED",
                    $"{string.Join(", ", docOnly.Select(k => k.Key))} は SDK のモバイル用許可リストに名前が無いため SDK が警告しますが、docs ではモバイル用シェーダーです (対応は不要)" +
                    (docOnly.Any(k => k.Key == ToonStandardOutlineShader) ? "。ただし Outline 版は PC 専用で、モバイルでは自動的に Outline なしの Toon Standard になります (輪郭線は表示されません)" : ""),
                    path: docOnly[0].Value[0], value: ShaderTable(docOnly, ctx.TopN));
            m["shader_list_source"] = Lists.ShaderListSource;
            m["shaders_outside_sdk_list"] = shaderUse.OrderByDescending(k => k.Value.Count).Take(ctx.TopN)
                .Select(kv => (object)new Dictionary<string, object> { ["shader"] = kv.Key, ["renderers"] = kv.Value.Count }).ToList();

            // Post processing: disabled on Android ("Post processing systems are disabled completely", quest-content-limitations.md
            // L52) and allowlisted only #if UNITY_STANDALONE ([WV] L48-52).
            var pp = ctx.Audited(K.PpLayer | K.PpVolume | K.PpDebug).Select(PathOf).ToList();
            m["post_processing_components"] = pp.Count;
            if (pp.Count > 0)
                ctx.Issue(CctSeverity.Warning, "ANDROID_POST_PROCESSING",
                    $"ポストプロセスのコンポーネントが {pp.Count} 個あります。Android/iOS ではポストプロセスは完全に無効です (モバイル版では見た目が変わる前提で確認してください)",
                    path: pp[0], value: Grouped(pp.Count, pp));

            // Realtime lights ("Baking lighting for your world is essential... write off real-time lights", optimization.md L114).
            var realtime = new List<string>();
            int mixed = 0;
            foreach (var item in ctx.Audited(K.Light))
            {
                var l = item.C as Light;
                if (l == null || !(item.O.Active && l.enabled) || l.type == LightType.Area || l.type == LightType.Disc) continue;
                var mode = SafeBakeType(l);
                if (mode == LightmapBakeType.Realtime) realtime.Add(item.O.Path);
                else if (mode == LightmapBakeType.Mixed) mixed++;
            }
            m["realtime_lights"] = realtime.Count;
            m["mixed_lights"] = mixed;
            if (realtime.Count > 0)
                ctx.Issue(CctSeverity.Warning, "ANDROID_REALTIME_LIGHT",
                    $"Realtime のライトが {realtime.Count} 個有効です。docs はモバイルではライトのベイクが必須で、リアルタイムライトは使わないくらいでよいとしています",
                    path: realtime[0], value: Grouped(realtime.Count, realtime));

            // Textures above 1k on the mobile platform (estimate from the imported size and the platform override).
            var large = new List<string>();
            foreach (var ti in ctx.Textures.Values)
            {
                int size = 0;
                foreach (var p in platforms) size = Math.Max(size, MobileTextureSize(ti, p == "ios" ? "iPhone" : "Android"));
                if (size > MobileTextureMax && ti.AssetPath != null) large.Add(ti.AssetPath + " (" + size + ")");
            }
            m["textures_over_1024"] = large.Count;
            if (large.Count > 0)
                ctx.Issue(CctSeverity.Info, "ANDROID_LARGE_TEXTURE",
                    $"モバイルで 1024 px を超えると推定されるテクスチャが {large.Count} 枚あります。docs はモバイルでは最大 1k を目安にするよう案内しています (Android/iOS の Override で Max Size を下げられます)",
                    asset: large[0].Substring(0, large[0].LastIndexOf(" (", StringComparison.Ordinal)), value: Grouped(large.Count, large), limit: MobileTextureMax);

            m["triangles"] = ctx.TotalTriangles;
            m["triangle_budget"] = MobileTriangleBudget;
            if (ctx.TotalTriangles > MobileTriangleBudget)
                ctx.Issue(CctSeverity.Warning, "ANDROID_TRIANGLE_BUDGET",
                    $"三角形数が {ctx.TotalTriangles:N0} (うち開始時に表示 {ctx.ActiveTriangles:N0}) です。docs はモバイルのワールド全体で約 250,000 を目安にしています (アバターの分を残すため)",
                    value: ctx.TotalTriangles, limit: MobileTriangleBudget);

            // Components disabled on Android (quest-content-limitations.md L47-48).
            var unsupportedItems = ctx.Audited(K.Cloth | K.DynamicBone).ToList();
            m["unsupported_components"] = unsupportedItems.Count;
            if (unsupportedItems.Count > 0)
                ctx.Issue(CctSeverity.Warning, "ANDROID_UNSUPPORTED_COMPONENT",
                    $"Android/iOS で完全に無効なコンポーネント ({string.Join(" / ", unsupportedItems.Select(i => i.C.GetType().Name).Distinct())}) が {unsupportedItems.Count} 個あります。モバイル版では動かない前提で見た目を確認してください (揺れ物は PhysBone なら動きます)",
                    path: PathOf(unsupportedItems[0]), value: Grouped(unsupportedItems.Count, unsupportedItems.Select(PathOf)));

            // Materials: transparency (incl. cutout) and GPU instancing (optimization.md L98, L145-147).
            var transparent = new List<string>();
            var noInstancing = new List<string>();
            foreach (var mi in ctx.Materials.Values)
            {
                var label = mi.AssetPath ?? mi.Material.name;
                int queue;
                try { queue = mi.Material.renderQueue; }
                catch (Exception) { queue = 2000; }
                if (queue >= AlphaTestQueue && queue < 4000 && mi.Shader != "UI/Default") transparent.Add(label);
                bool instancing;
                try { instancing = mi.Material.enableInstancing; }
                catch (Exception) { instancing = true; }
                if (!instancing && mi.AssetPath != null) noInstancing.Add(label);
            }
            m["transparent_or_cutout_materials"] = transparent.Count;
            m["materials_without_gpu_instancing"] = noInstancing.Count;
            if (transparent.Count > 0)
                ctx.Issue(CctSeverity.Info, "ANDROID_TRANSPARENT_MATERIALS",
                    $"半透明/カットアウト (Render Queue 2450 以上) のマテリアルが {transparent.Count} 個あります。docs はモバイルではアルファ (カットアウト含む) をなるべく避けるよう案内しています",
                    asset: transparent[0], value: Grouped(transparent.Count, transparent));
            if (noInstancing.Count > 0)
                ctx.Issue(CctSeverity.Info, "ANDROID_GPU_INSTANCING_OFF",
                    $"Enable GPU Instancing が OFF のマテリアルが {noInstancing.Count} 個あります。docs はモバイルでは全マテリアルで ON にするよう案内しています",
                    asset: noInstancing[0], value: Grouped(noInstancing.Count, noInstancing));
        }

        static List<object> ShaderTable(List<KeyValuePair<string, List<string>>> shaders, int cap) =>
            shaders.Take(cap).Select(k => (object)new Dictionary<string, object>
            {
                ["shader"] = k.Key, ["renderers"] = k.Value.Count, ["example"] = k.Value[0],
            }).ToList();

        static System.Reflection.MethodInfo _sourceSizeMethod;
        static bool _sourceSizeLooked;

        /// <summary>
        /// Source image size from TextureImporter.GetSourceTextureWidthAndHeight (public in 2022.3, read-only; absent from the
        /// 2021.1 API this package compiles against, so it is called by reflection). -1 when unavailable.
        /// </summary>
        static int SourceTextureMaxDimension(TextureImporter importer)
        {
            if (importer == null) return -1;
            if (!_sourceSizeLooked)
            {
                _sourceSizeLooked = true;
                try
                {
                    _sourceSizeMethod = typeof(TextureImporter).GetMethod("GetSourceTextureWidthAndHeight",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null,
                        new[] { typeof(int).MakeByRefType(), typeof(int).MakeByRefType() }, null);
                }
                catch (Exception) { _sourceSizeMethod = null; }
            }
            if (_sourceSizeMethod == null) return -1;
            try
            {
                var a = new object[] { 0, 0 };
                _sourceSizeMethod.Invoke(importer, a);
                return Math.Max((int)a[0], (int)a[1]);
            }
            catch (Exception) { return -1; }
        }

        /// <summary>
        /// Estimated max dimension on a mobile platform: the source size (or, if unknown, the size imported for the current
        /// build target) scaled by the importer's Non-Power of 2 setting (shared by every platform), then capped by that
        /// platform's override Max Size, else the default Max Size.
        /// </summary>
        static int MobileTextureSize(TextureInfo ti, string platformName)
        {
            int current = Math.Max(ti.Width, ti.Height);
            if (ti.Importer == null) return current;
            int source = SourceTextureMaxDimension(ti.Importer);
            if (source > 0) current = NpotScaled(source, ti.Importer);
            int max = ti.Importer.maxTextureSize;
            try
            {
                var ps = ti.Importer.GetPlatformTextureSettings(platformName);
                if (ps != null && ps.overridden) max = ps.maxTextureSize;
            }
            catch (Exception) { /* keep default */ }
            return max > 0 ? Math.Min(current, max) : current;
        }

        /// <summary>
        /// A non-power-of-two source size as the importer rounds it (TextureImporter.npotScale: ToNearest / ToLarger /
        /// ToSmaller; None keeps it). Rounding is monotonic, so rounding the larger dimension gives the larger imported one.
        /// </summary>
        static int NpotScaled(int size, TextureImporter importer)
        {
            if (size <= 0 || Mathf.IsPowerOfTwo(size)) return size;
            TextureImporterNPOTScale scale;
            try { scale = importer.npotScale; }
            catch (Exception) { return size; }
            switch (scale)
            {
                case TextureImporterNPOTScale.ToNearest: return Mathf.ClosestPowerOfTwo(size);
                case TextureImporterNPOTScale.ToLarger: return Mathf.NextPowerOfTwo(size);
                case TextureImporterNPOTScale.ToSmaller: return Mathf.NextPowerOfTwo(size) / 2;
                default: return size;
            }
        }
    }
}
