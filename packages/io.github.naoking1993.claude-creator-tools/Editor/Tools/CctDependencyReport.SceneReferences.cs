using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    public static partial class CctDependencyReport
    {
        // Modular Avatar nadena.dev.modular_avatar.core.AvatarObjectReference: { string referencePath; [SerializeField] GameObject
        // targetObject; }. MA uses targetObject only while it is inside the avatar and otherwise resolves referencePath (relative
        // to the avatar root, or the AVATAR_ROOT sentinel) on the avatar the prefab is installed on (verified in MA's source).
        const string MaReferenceTypeName = "AvatarObjectReference";
        const string MaReferencePathField = "referencePath";
        const string MaTargetObjectField = "targetObject";
        const string MaAvatarRootPath = "$$$AVATAR_ROOT$$$";

        sealed class RefAggregate
        {
            public string Object;
            public string Component;
            public string ComponentFullName;
            public string Property;
            public int Count;
            public string ReferencePath;
            public readonly List<string> Referenced = new List<string>();

            public void Add(string referenced)
            {
                Count++;
                if (Referenced.Count < 5 && !Referenced.Contains(referenced)) Referenced.Add(referenced);
            }

            public Dictionary<string, object> ToPlain()
            {
                var d = new Dictionary<string, object>
                {
                    ["object"] = Object,
                    ["component"] = Component,
                    ["component_type"] = ComponentFullName,
                    ["property"] = Property,
                    ["count"] = Count,
                    ["referenced"] = Referenced.Cast<object>().ToList(),
                };
                if (ReferencePath != null) d["reference_path"] = ReferencePath;
                return d;
            }
        }

        static void Aggregate(Dictionary<string, RefAggregate> map, Component owner, string propertyPath, string referenced, string referencePath = null)
        {
            var prop = AggregatePropertyPath(propertyPath);
            var key = owner.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + prop;
            if (!map.TryGetValue(key, out var a))
                map[key] = a = new RefAggregate
                {
                    Object = CctTargets.GetPath(owner.transform),
                    Component = owner.GetType().Name,
                    ComponentFullName = owner.GetType().FullName,
                    Property = prop,
                    ReferencePath = referencePath,
                };
            a.Add(referenced);
        }

        static GameObject GameObjectOf(Object o) => o as GameObject ?? (o as Component)?.gameObject;

        static bool InHierarchy(GameObject go, Transform root) =>
            go != null && (go.transform == root || CctTargets.IsDescendantOf(go.transform, root));

        static CctResult RunSceneReferences(CctArgs args)
        {
            var r = CctResult.Ok("");
            var ctx = new Context(r, args);
            r.Put("mode", ModeSceneReferences);
            bool includeInbound = args.GetBool("include_inbound", true);
            int maxComponents = args.GetInt("max_components", DefaultMaxComponents, 100, 2000000);

            var res = CctTargets.Resolve(args.GetString("target", null));
            if (!res.Ok)
                return FailWith(r, res.Error, "TARGET_NOT_FOUND", res.Candidates.Count > 0 ? res.Candidates.Cast<object>().ToList() : null);
            var target = res.GameObject;
            if (IsTransient(target))
                return FailWith(r, $"'{target.name}' はプレビュー用の一時オブジェクト (NDMF / Modular Avatar のプレビュー等、保存されない) です。Hierarchy で元のオブジェクトを指定してください",
                    "TARGET_NOT_FOUND");
            var root = target.transform;
            bool isAsset = EditorUtility.IsPersistent(target);
            r.Put("target", CctTargets.Describe(target));
            r.Put("is_prefab_asset", isAsset);

            var external = new Dictionary<string, RefAggregate>(StringComparer.Ordinal);
            var pathResolved = new Dictionary<string, RefAggregate>(StringComparer.Ordinal);
            var sceneOnly = new Dictionary<string, RefAggregate>(StringComparer.Ordinal);
            var inbound = new Dictionary<string, RefAggregate>(StringComparer.Ordinal);
            var missingRefs = new List<string>();
            var missingScripts = new List<string>();
            var missingPrefabs = new List<string>();
            int missingRefCount = 0, missingScriptCount = 0, missingPrefabCount = 0, assetRefs = 0, internalRefs = 0, components = 0;
            long visits = 0;
            bool partial = false;
            var targetScene = target.scene;

            // ---- outbound: every component under the target
            var transforms = root.GetComponentsInChildren<Transform>(true);
            using (var progress = new Progress("Claude Creator Tools: 対象の参照を確認中", transforms.Length, 300))
            {
                for (int i = 0; i < transforms.Length; i++)
                {
                    if (ctx.OutOfTime("outbound_scan")) break;
                    var t = transforms[i];
                    if (t == null || IsTransient(t.gameObject)) continue; // never saved, so never part of the export
                    var go = t.gameObject;
                    progress.Step(i, go.name);
                    int m = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                    if (m > 0)
                    {
                        missingScriptCount += m;
                        if (missingScripts.Count < ctx.MaxEntries) missingScripts.Add(m > 1 ? $"{CctTargets.GetPath(go)} ({m})" : CctTargets.GetPath(go));
                    }
                    if (IsMissingPrefabInstanceRoot(go))
                    {
                        missingPrefabCount++;
                        if (missingPrefabs.Count < ctx.MaxEntries) missingPrefabs.Add(CctTargets.GetPath(go));
                    }
                    foreach (var c in go.GetComponents<Component>())
                    {
                        if (c == null) continue;
                        components++;
                        var comp = c;
                        bool complete = WalkReferences(c, false, p =>
                        {
                            var v = p.objectReferenceValue;
                            if (v == null)
                            {
                                if (p.objectReferenceInstanceIDValue == 0) return;
                                missingRefCount++;
                                if (missingRefs.Count < ctx.MaxEntries) missingRefs.Add($"{ComponentLabel(comp)}.{PrettyPropertyPath(p.propertyPath)}");
                                return;
                            }
                            var refGo = GameObjectOf(v);
                            // Inside the target first: in a prefab asset target, internal references are persistent too.
                            if (InHierarchy(refGo, root)) { internalRefs++; return; }
                            if (EditorUtility.IsPersistent(v)) { assetRefs++; return; }
                            if (refGo == null)
                            {
                                Aggregate(sceneOnly, comp, p.propertyPath, $"{v.GetType().Name} '{v.name}'");
                                return;
                            }
                            var described = DescribeSceneObject(v, targetScene);
                            var refPath = MaReferencePath(p);
                            if (refPath != null) Aggregate(pathResolved, comp, p.propertyPath, described, refPath);
                            else Aggregate(external, comp, p.propertyPath, described);
                        }, ref visits);
                        if (!complete) partial = true;
                    }
                }
            }

            // ---- inbound: objects elsewhere in the target's scene that point into the target
            int inboundComponents = 0;
            bool inboundCapped = false;
            if (includeInbound && !isAsset && targetScene.IsValid() && targetScene.isLoaded)
            {
                var others = AllGameObjects(targetScene).Where(g => !InHierarchy(g, root)).ToList();
                using (var progress = new Progress("Claude Creator Tools: 対象への参照を確認中", others.Count, 500))
                {
                    for (int i = 0; i < others.Count && !inboundCapped; i++)
                    {
                        if (ctx.OutOfTime("inbound_scan")) break;
                        var go = others[i];
                        if (go == null) continue;
                        progress.Step(i, go.name);
                        foreach (var c in go.GetComponents<Component>())
                        {
                            if (c == null) continue;
                            if (++inboundComponents > maxComponents) { inboundCapped = true; break; }
                            var comp = c;
                            WalkReferences(c, false, p =>
                            {
                                var v = p.objectReferenceValue;
                                if (v == null || EditorUtility.IsPersistent(v)) return;
                                var refGo = GameObjectOf(v);
                                if (!InHierarchy(refGo, root)) return;
                                Aggregate(inbound, comp, p.propertyPath, DescribeSceneObject(v, targetScene), MaReferencePath(p));
                            }, ref visits);
                        }
                    }
                }
            }

            // ---- prefab status
            var prefab = PrefabStatus(ctx, target, isAsset);

            // ---- issues
            foreach (var a in external.Values)
                ctx.Issue(CctSeverity.Error, "EXTERNAL_SCENE_REFERENCE",
                    $"{a.Component}.{a.Property} が対象の外のシーンオブジェクトを参照しています ({a.Count} 件、例: {a.Referenced.FirstOrDefault()})。" +
                    "単独のプレハブにすると None になります。対象の内側に移す / 導入時に再設定する / 参照しない設計にしてください",
                    path: a.Object, value: a.Count);
            foreach (var a in pathResolved.Values)
                ctx.Issue(CctSeverity.Info, "PATH_RESOLVED_REFERENCE",
                    $"{a.Component}.{a.Property} は対象外の {a.Referenced.FirstOrDefault()} を指していますが、Modular Avatar が " +
                    (a.ReferencePath == MaAvatarRootPath ? "アバターのルートとして" : $"アバタールートからのパス '{a.ReferencePath}' で") + "再解決します。" +
                    "導入先アバターに同じパスのオブジェクトがあることを確認してください",
                    path: a.Object, value: a.ReferencePath);
            foreach (var a in sceneOnly.Values)
                ctx.Issue(CctSeverity.Warning, "SCENE_ONLY_OBJECT",
                    $"{a.Component}.{a.Property} がアセットとして保存されていないオブジェクト ({a.Referenced.FirstOrDefault()}) を参照しています。プレハブ化すると失われるので、先にアセットとして保存してください",
                    path: a.Object, value: a.Count);
            if (missingScriptCount > 0)
                ctx.Issue(CctSeverity.Error, "MISSING_SCRIPT", $"対象内に Missing Script が {missingScriptCount} 個あります (例: {string.Join(", ", missingScripts.Take(3))})。" +
                                                              "必要なスクリプト/パッケージを導入するか、不要なら Missing のコンポーネントを削除してからプレハブにしてください",
                    path: CctTargets.GetPath(target), value: missingScriptCount);
            if (missingPrefabCount > 0)
                ctx.Issue(CctSeverity.Error, "MISSING_PREFAB", $"元プレハブが見つからないネストプレハブが {missingPrefabCount} 個あります (例: {string.Join(", ", missingPrefabs.Take(3))})。" +
                                                              "元プレハブ (またはそのパッケージ) を導入し直すか、そのインスタンスを削除してください",
                    path: missingPrefabs[0], value: missingPrefabCount);
            if (missingRefCount > 0)
                ctx.Issue(CctSeverity.Warning, "MISSING_REFERENCE", $"対象内に Missing 参照が {missingRefCount} 個あります (例: {string.Join(", ", missingRefs.Take(3))})。" +
                                                                   "参照を設定し直すか、不要なら None にしてください",
                    path: CctTargets.GetPath(target), value: missingRefCount);
            foreach (var a in inbound.Values)
                ctx.Issue(CctSeverity.Info, "INBOUND_SCENE_REFERENCE",
                    $"対象の外の {a.Component}.{a.Property} が対象内の {a.Referenced.FirstOrDefault()} を参照しています。この繋がりはプレハブに含まれないため、導入先で再設定が必要です",
                    path: a.Object, value: a.Count);
            if (partial)
                ctx.Issue(CctSeverity.Warning, "SCAN_INCOMPLETE", $"プロパティ数が多すぎて一部のコンポーネントは途中までしか確認できませんでした (1 オブジェクト {MaxVisitsPerObject} 件まで)");
            if (inboundCapped)
                ctx.Issue(CctSeverity.Warning, "SCAN_INCOMPLETE", $"対象への参照の確認はコンポーネント {maxComponents} 個で打ち切りました (max_components で変更可)", value: maxComponents);

            // ---- output
            r.Put("components_scanned", components);
            r.Put("asset_reference_count", assetRefs);
            r.Put("internal_reference_count", internalRefs);
            r.Put("external_reference_count", external.Values.Sum(a => a.Count));
            PutCapped(r, "external_references", external.Values.Select(a => a.ToPlain()), ctx.MaxEntries);
            PutCapped(r, "path_resolved_references", pathResolved.Values.Select(a => a.ToPlain()), ctx.MaxEntries);
            PutCapped(r, "scene_only_objects", sceneOnly.Values.Select(a => a.ToPlain()), ctx.MaxEntries);
            r.Put("missing_reference_count", missingRefCount);
            r.Put("missing_references", missingRefs.Cast<object>().ToList());
            r.Put("missing_script_count", missingScriptCount);
            r.Put("missing_scripts", missingScripts.Cast<object>().ToList());
            r.Put("missing_prefab_count", missingPrefabCount);
            r.Put("missing_prefabs", missingPrefabs.Cast<object>().ToList());
            r.Put("inbound_reference_count", inbound.Values.Sum(a => a.Count));
            PutCapped(r, "inbound_references", inbound.Values.Select(a => a.ToPlain()), ctx.MaxEntries);
            r.Put("prefab", prefab);
            r.Put("scan", new Dictionary<string, object>
            {
                ["components_scanned"] = components,
                ["inbound_components_scanned"] = Math.Min(inboundComponents, maxComponents),
                ["inbound_capped"] = inboundCapped,
                ["properties_visited"] = visits,
                ["partial_objects"] = partial,
                ["in_memory_state"] = true,
            });
            r.Put("conclusive", ctx.Complete && !partial && !inboundCapped);
            ctx.FlushIssueCaps();
            ctx.FlushTimeBudget();

            int ext = external.Values.Sum(a => a.Count);
            r.Message = $"{target.name}: シーン外参照 {ext} 件" +
                        (pathResolved.Count > 0 ? $" (MA パス解決 {pathResolved.Values.Sum(a => a.Count)} 件は別)" : "") +
                        $" / 未保存オブジェクト参照 {sceneOnly.Values.Sum(a => a.Count)} / Missing 参照 {missingRefCount} / Missing Script {missingScriptCount} / " +
                        $"外部からの参照 {inbound.Values.Sum(a => a.Count)}" +
                        (prefab.TryGetValue("has_overrides", out var ho) && ho is bool b && b ? " / プレハブのオーバーライドあり" : "") +
                        (ctx.Complete && !partial && !inboundCapped ? "" : " (走査が途中までのため未確定: SCAN_INCOMPLETE 参照)");
            return r;
        }

        /// <summary>
        /// For the targetObject of a Modular Avatar AvatarObjectReference with a non-empty referencePath, the path MA will resolve
        /// at build time; null for any other reference. Looks up the parent/sibling properties without moving the iterator.
        /// </summary>
        static string MaReferencePath(SerializedProperty p)
        {
            var path = p.propertyPath;
            if (!path.EndsWith("." + MaTargetObjectField, StringComparison.Ordinal)) return null;
            var parent = path.Substring(0, path.Length - MaTargetObjectField.Length - 1);
            try
            {
                var so = p.serializedObject;
                var holder = so.FindProperty(parent);
                if (holder == null || holder.type != MaReferenceTypeName) return null;
                var rp = so.FindProperty(parent + "." + MaReferencePathField);
                if (rp == null || rp.propertyType != SerializedPropertyType.String) return null;
                var s = rp.stringValue;
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// PrefabStage.prefabContentsRoot of the open Prefab Mode stage, read by reflection because the PrefabStage type moved from
        /// UnityEditor.Experimental.SceneManagement (2021.1) to UnityEditor.SceneManagement (2021.2+). Null when unavailable.
        /// </summary>
        static GameObject StageContentsRoot()
        {
            try
            {
                var stage = UnityEditor.SceneManagement.StageUtility.GetCurrentStage();
                return stage?.GetType().GetProperty("prefabContentsRoot")?.GetValue(stage, null) as GameObject;
            }
            catch (Exception) { return null; }
        }

        static Dictionary<string, object> PrefabStatus(Context ctx, GameObject target, bool isAsset)
        {
            var d = new Dictionary<string, object>();
            try
            {
                if (isAsset)
                {
                    d["status"] = "prefab_asset";
                    d["asset"] = AssetDatabase.GetAssetPath(target);
                    return d;
                }
                // Prefab Mode: the root being edited is the prefab asset itself (for a variant it is also an instance of the base,
                // but saving the stage keeps those overrides), so instance-override advice applies only below the root.
                var stagePath = CurrentStageScene(out var stageScene);
                if (stagePath != null && target.scene == stageScene)
                {
                    d["prefab_stage"] = stagePath;
                    // A UI prefab sits under a "Canvas (Environment)" root in Prefab Mode, so ask the stage for its contents root.
                    var contentsRoot = StageContentsRoot();
                    if (contentsRoot != null ? contentsRoot == target : target.transform.parent == null)
                    {
                        d["status"] = "prefab_stage_root";
                        d["asset"] = stagePath;
                        d["has_overrides"] = false;
                        d["stage_unsaved"] = stageScene.isDirty;
                        if (stageScene.isDirty)
                            ctx.Issue(CctSeverity.Warning, "UNSAVED_CHANGES",
                                "Prefab Mode に未保存の変更があります。この結果は編集中の内容ですが、プレハブアセット (書き出される内容) にはまだ入っていません。保存してから書き出してください",
                                asset: stagePath);
                        return d;
                    }
                }
                bool part = PrefabUtility.IsPartOfPrefabInstance(target);
                if (!part)
                {
                    bool added = false;
                    try { added = target.transform.parent != null && PrefabUtility.IsAddedGameObjectOverride(target); }
                    catch (Exception) { added = false; }
                    d["status"] = added ? "added_to_prefab_instance" : "not_prefab";
                    if (added)
                        d["parent_instance_root"] = CctTargets.GetPath(PrefabUtility.GetOutermostPrefabInstanceRoot(target.transform.parent.gameObject));
                    d["has_overrides"] = false;
                    return d;
                }
                var nearest = PrefabUtility.GetNearestPrefabInstanceRoot(target);
                var outer = PrefabUtility.GetOutermostPrefabInstanceRoot(target);
                var source = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(target);
                bool isRoot = nearest == target;
                d["status"] = isRoot ? "prefab_instance_root" : "inside_prefab_instance";
                d["source_prefab"] = source;
                d["asset_type"] = PrefabUtility.GetPrefabAssetType(target).ToString();
                d["nearest_instance_root"] = CctTargets.GetPath(nearest);
                d["outermost_instance_root"] = CctTargets.GetPath(outer);
                if (outer == null) return d;
                var rootT = target.transform;
                int objectOverrides = PrefabUtility.GetObjectOverrides(outer, false).Count(o => InHierarchy(GameObjectOf(o.instanceObject), rootT));
                int addedComponents = PrefabUtility.GetAddedComponents(outer).Count(a => a.instanceComponent != null && InHierarchy(a.instanceComponent.gameObject, rootT));
                int addedObjects = PrefabUtility.GetAddedGameObjects(outer).Count(a => InHierarchy(a.instanceGameObject, rootT));
                int removedComponents = PrefabUtility.GetRemovedComponents(outer).Count(a => InHierarchy(a.containingInstanceGameObject, rootT));
                var mods = PrefabUtility.GetPropertyModifications(outer) ?? new PropertyModification[0];
                int nonDefault = 0;
                foreach (var m in mods)
                {
                    if (m == null) continue;
                    bool isDefault;
                    try { isDefault = PrefabUtility.IsDefaultOverride(m); }
                    catch (Exception) { isDefault = false; }
                    if (!isDefault) nonDefault++;
                }
                int total = objectOverrides + addedComponents + addedObjects + removedComponents;
                d["overridden_objects"] = objectOverrides;
                d["added_components"] = addedComponents;
                d["added_game_objects"] = addedObjects;
                d["removed_components"] = removedComponents;
                d["outermost_instance_property_modifications"] = nonDefault;
                d["has_overrides"] = total > 0;
                if (total > 0)
                    ctx.Issue(CctSeverity.Info, "PREFAB_HAS_OVERRIDES",
                        $"対象はプレハブ {source} の{(isRoot ? "インスタンス" : "インスタンスの一部")}で、オーバーライドがあります " +
                        $"(変更オブジェクト {objectOverrides} / 追加コンポーネント {addedComponents} / 追加 GameObject {addedObjects} / 削除コンポーネント {removedComponents})。" +
                        "元プレハブをそのまま書き出すとこの改変は含まれません。シーンの対象を複製し、その複製を新しいプレハブ (独立プレハブまたは Prefab Variant) として保存してから書き出してください" +
                        " (元のプレハブへ Apply すると配布元の製品アセットを書き換えます)",
                        path: CctTargets.GetPath(target), value: total, asset: source);
                else if (!isRoot)
                    ctx.Issue(CctSeverity.Info, "PART_OF_PREFAB_INSTANCE",
                        $"対象はプレハブ {source} の一部です (オーバーライドなし)。単独で書き出すには新しいプレハブとして保存する必要があります",
                        path: CctTargets.GetPath(target), asset: source);
            }
            catch (Exception e)
            {
                d["error"] = e.GetType().Name + ": " + e.Message;
            }
            return d;
        }
    }
}
