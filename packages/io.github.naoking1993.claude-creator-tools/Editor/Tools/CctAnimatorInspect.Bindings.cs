using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// cct_animator_inspect: clip binding checks (paths / components / blend shapes under each controller's binding root,
    /// Transform-only rule for non-FX layers) and the search modes find_parameter / find_binding.
    /// </summary>
    public static partial class CctAnimatorInspect
    {
        const int ProblemListCap = 30;
        const int ClipListCap = 1000;
        const int FindCap = 2000;        // rows kept in the report (find_all)
        const int FindSummaryCap = 50;   // rows kept in the MCP summary (find)

        sealed class RootCheck
        {
            public readonly Dictionary<string, int> MissingPaths = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly List<string> MissingComponents = new List<string>();
            public readonly List<string> MissingBlendShapes = new List<string>();
            public int Resolved, Humanoid;
        }

        static EditorCurveBinding[] Curves(ClipUse cu)
        {
            if (cu.Curves == null)
            {
                try { cu.Curves = AnimationUtility.GetCurveBindings(cu.Clip) ?? new EditorCurveBinding[0]; }
                catch (Exception) { cu.Curves = new EditorCurveBinding[0]; }
            }
            return cu.Curves;
        }

        static EditorCurveBinding[] ObjectCurves(ClipUse cu)
        {
            if (cu.ObjectCurves == null)
            {
                try { cu.ObjectCurves = AnimationUtility.GetObjectReferenceCurveBindings(cu.Clip) ?? new EditorCurveBinding[0]; }
                catch (Exception) { cu.ObjectCurves = new EditorCurveBinding[0]; }
            }
            return cu.ObjectCurves;
        }

        static Transform FindPath(Transform root, string path, Dictionary<Transform, Dictionary<string, Transform>> cache)
        {
            if (root == null) return null;
            if (string.IsNullOrEmpty(path)) return root;
            if (!cache.TryGetValue(root, out var map)) cache[root] = map = new Dictionary<string, Transform>(StringComparer.Ordinal);
            if (!map.TryGetValue(path, out var t))
            {
                t = root.Find(path);
                map[path] = t;
            }
            return t;
        }

        static void CheckClipBindings(Ctx ctx)
        {
            var summary = new Dictionary<string, object>();
            var roots = ctx.Units.Where(u => !u.Failed && u.BindingRoot != null)
                .Select(u => (object)new Dictionary<string, object>
                {
                    ["controller"] = u.Id,
                    ["root"] = CctTargets.GetPath(u.BindingRoot),
                    ["rule"] = u.BindingRootRule,
                    ["estimate"] = u.BindingRootEstimate,
                }).ToList();
            summary["checked"] = ctx.CheckBindings && roots.Count > 0;
            if (!ctx.CheckBindings) summary["reason"] = ctx.Target == null
                ? "アバター / 対象が無いためパスを検査していません (target を指定すると検査します)"
                : "check_bindings=false";
            else if (roots.Count == 0) summary["reason"] = "パスの基準 (binding root) が決まるコントローラがありません";
            summary["roots"] = roots.Take(SummaryControllerCap).ToList();
            if (roots.Count > SummaryControllerCap) summary["roots_omitted"] = roots.Count - SummaryControllerCap;

            var pathCache = new Dictionary<Transform, Dictionary<string, Transform>>();
            var checks = new Dictionary<AnimationClip, Dictionary<Transform, RootCheck>>();
            var problems = new List<object>();
            int problemCount = 0, clipsChecked = 0, bindingsTotal = 0, humanoid = 0, missingTotal = 0;
            var fxTransformClips = new Dictionary<Unit, int>();
            var maAlt = new Dictionary<Unit, int[]>(); // [missing, resolvable with the other path mode]
            var clipRows = new List<object>();
            var reported = new HashSet<string>(StringComparer.Ordinal);

            foreach (var cu in ctx.Clips.Values)
            {
                if (ctx.OverBudget) { ctx.TimeTruncated = true; break; }
                if (clipsChecked >= MaxClipsChecked) { ctx.TimeTruncated = true; break; }
                var curves = Curves(cu);
                var objCurves = ObjectCurves(cu);
                var all = curves.Concat(objCurves).ToArray();
                bindingsTotal += all.Length;
                clipsChecked++;

                var row = new Dictionary<string, object>
                {
                    ["clip"] = cu.Clip.name,
                    ["asset"] = AssetDatabase.GetAssetPath(cu.Clip),
                    ["length"] = Round(cu.Clip.length),
                    ["curves"] = curves.Length,
                    ["object_curves"] = objCurves.Length,
                    ["paths"] = all.Select(b => b.path).Distinct().Count(),
                    ["used_by"] = cu.LocationCount,
                    ["controllers"] = cu.Units.Select(x => x.Id).ToList<object>(),
                };
                if (cu.Clip.isLooping) row["loop"] = true;

                foreach (var u in cu.Units)
                {
                    // Docs playable-layers.md: Base / Additive / Gesture / Action are Transform-only; FX should not animate Transforms.
                    if (u.LayerType == LtBase || u.LayerType == LtAdditive || u.LayerType == LtGesture || u.LayerType == LtAction)
                    {
                        var nonTransform = all.Where(b => b.type != typeof(Transform) && b.type != typeof(Animator)).ToList();
                        if (nonTransform.Count > 0)
                            ctx.Issue(CctSeverity.Warning, "NON_TRANSFORM_IN_NON_FX",
                                $"{LayerTypeName(u.LayerType)} のクリップ '{cu.Clip.name}' が Transform 以外 ({string.Join(", ", nonTransform.Select(b => (b.type != null ? b.type.Name : "?") + "." + b.propertyName).Distinct().Take(3))} など {nonTransform.Count} 本) を動かしています。docs: FX 以外は Transform アニメーションのみ (マテリアル・BlendShape・オブジェクト切替はミラーのクローンに反映されないので FX へ)",
                                cu.Locations.FirstOrDefault(), nonTransform.Count, asset: AssetDatabase.GetAssetPath(cu.Clip), quiet: u.Quiet);
                    }
                    else if (u.LayerType == LtFx && all.Any(b => b.type == typeof(Transform)))
                    {
                        fxTransformClips.TryGetValue(u, out var n);
                        fxTransformClips[u] = n + 1;
                    }

                    if (!ctx.CheckBindings || u.BindingRoot == null) continue;
                    if (!checks.TryGetValue(cu.Clip, out var perRoot)) checks[cu.Clip] = perRoot = new Dictionary<Transform, RootCheck>();
                    if (!perRoot.TryGetValue(u.BindingRoot, out var rc))
                    {
                        rc = CheckRoot(all, u.BindingRoot, pathCache);
                        perRoot[u.BindingRoot] = rc;
                        humanoid += rc.Humanoid;
                    }

                    if (u.Ma != null && rc.MissingPaths.Count > 0)
                    {
                        var other = u.Ma.AltRoot;
                        if (other != null && other != u.BindingRoot)
                        {
                            if (!maAlt.TryGetValue(u, out var acc)) maAlt[u] = acc = new int[2];
                            foreach (var p in rc.MissingPaths.Keys)
                            {
                                acc[0]++;
                                if (FindPath(other, p, pathCache) != null) acc[1]++;
                            }
                        }
                    }

                    int missingHere = rc.MissingPaths.Count + rc.MissingComponents.Count + rc.MissingBlendShapes.Count;
                    if (missingHere == 0) continue;
                    if (rc.MissingPaths.Count > 0 && !row.ContainsKey("missing_paths"))
                        row["missing_paths"] = rc.MissingPaths.Keys.Take(5).ToList<object>();
                    // One report per (clip, root): the same clip under the same root from several controllers is one problem.
                    if (!reported.Add(cu.Clip.GetInstanceID() + "|" + u.BindingRoot.GetInstanceID())) continue;
                    cu.Missing = Math.Max(cu.Missing, missingHere);
                    missingTotal += rc.MissingPaths.Count;
                    var sev = u.Quiet || u.BindingRootEstimate ? CctSeverity.Info : CctSeverity.Warning;
                    var rootPath = CctTargets.GetPath(u.BindingRoot);
                    var clipAsset = AssetDatabase.GetAssetPath(cu.Clip);
                    var at = cu.Locations.FirstOrDefault();
                    if (rc.MissingPaths.Count > 0)
                        ctx.Issue(sev, "BINDING_PATH_MISSING",
                            $"クリップ '{cu.Clip.name}' が {rootPath} の下に存在しないパス {rc.MissingPaths.Count} 個を動かしています ({string.Join(", ", rc.MissingPaths.Keys.Take(3))}{(rc.MissingPaths.Count > 3 ? " ..." : "")})" +
                            (u.BindingRootEstimate ? " (基準は推定)" : ""),
                            at, rc.MissingPaths.Keys.Take(10).ToList<object>(), asset: clipAsset, quiet: u.Quiet);
                    if (rc.MissingComponents.Count > 0)
                        ctx.Issue(sev, "BINDING_COMPONENT_MISSING",
                            $"クリップ '{cu.Clip.name}' が、パスはあるが対象コンポーネントが無いプロパティを動かしています ({string.Join(", ", rc.MissingComponents.Take(3))})",
                            at, rc.MissingComponents.Take(10).ToList<object>(), asset: clipAsset, quiet: u.Quiet);
                    if (rc.MissingBlendShapes.Count > 0)
                        ctx.Issue(sev, "BINDING_BLENDSHAPE_MISSING",
                            $"クリップ '{cu.Clip.name}' がメッシュに無い BlendShape を動かしています ({string.Join(", ", rc.MissingBlendShapes.Take(3))})",
                            at, rc.MissingBlendShapes.Take(10).ToList<object>(), asset: clipAsset, quiet: u.Quiet);
                    problemCount++;
                    if (problems.Count < ProblemListCap)
                    {
                        var pd = new Dictionary<string, object>
                        {
                            ["clip"] = cu.Clip.name,
                            ["asset"] = clipAsset,
                            ["controller"] = u.Id,
                            ["root"] = rootPath,
                            ["used_at"] = at,
                        };
                        if (rc.MissingPaths.Count > 0) pd["missing_paths"] = rc.MissingPaths.Keys.Take(5).ToList<object>();
                        if (rc.MissingComponents.Count > 0) pd["missing_components"] = rc.MissingComponents.Take(5).ToList<object>();
                        if (rc.MissingBlendShapes.Count > 0) pd["missing_blendshapes"] = rc.MissingBlendShapes.Take(5).ToList<object>();
                        problems.Add(pd);
                    }
                }
                if (cu.Missing > 0) row["missing"] = cu.Missing;
                if (clipRows.Count < ClipListCap) clipRows.Add(row);
            }

            foreach (var kv in fxTransformClips)
                ctx.Issue(CctSeverity.Info, "FX_TRANSFORM_ANIMATION",
                    $"FX のクリップ {kv.Value} 個が Transform を動かしています。docs: FX で Transform を動かすのは非推奨 (Gesture と同じ Transform を動かす場合は FX のマスクで除外が必要)",
                    kv.Key.Id, kv.Value, asset: kv.Key.Asset, quiet: kv.Key.Quiet);

            foreach (var kv in maAlt)
            {
                var u = kv.Key;
                int missing = kv.Value[0], alt = kv.Value[1];
                if (alt == 0) continue;
                bool relative = u.Ma.PathMode == 0;
                ctx.Issue(CctSeverity.Warning, "MA_PATH_MODE_MISMATCH",
                    relative
                        ? $"Path Mode = Relative ですが、見つからないパス {missing} 個のうち {alt} 個はアバタールート基準なら存在します。アバター既存のオブジェクトを動かすなら Absolute にしてください (MA docs)"
                        : $"Path Mode = Absolute ですが、見つからないパス {missing} 個のうち {alt} 個は Relative の基準 ({CctTargets.GetPath(u.Ma.AltRoot)}: Relative Path Root、未設定ならこのコンポーネントのオブジェクト) なら存在します。配下のオブジェクトを動かすなら Relative にしてください (MA docs)",
                    CctTargets.GetPath(u.Ma.Comp.transform), new Dictionary<string, object> { ["missing"] = missing, ["resolvable_with_other_mode"] = alt }, asset: u.Asset);
            }

            summary["clips_checked"] = clipsChecked;
            summary["clips_total"] = ctx.Clips.Count;
            summary["bindings_total"] = bindingsTotal;
            if (humanoid > 0) summary["humanoid_bindings_skipped"] = humanoid;
            summary["missing_paths_total"] = missingTotal;
            summary["clips_with_problems"] = problemCount;
            summary["problems"] = problems;
            if (problemCount > problems.Count) summary["problems_omitted"] = problemCount - problems.Count;
            ctx.R.Put("bindings", summary);
            ctx.R.Put("clips", clipRows);
            if (ctx.Clips.Count > clipRows.Count) ctx.R.Put("clips_omitted", ctx.Clips.Count - clipRows.Count);
        }

        static RootCheck CheckRoot(EditorCurveBinding[] bindings, Transform root, Dictionary<Transform, Dictionary<string, Transform>> cache)
        {
            var rc = new RootCheck();
            var seenComp = new HashSet<string>(StringComparer.Ordinal);
            var seenShape = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in bindings)
            {
                if (b.type == typeof(Animator)) { rc.Humanoid++; continue; } // muscles / root motion / animator parameter curves
                var path = b.path ?? "";
                var t = FindPath(root, path, cache);
                if (t == null)
                {
                    rc.MissingPaths.TryGetValue(path, out var n);
                    rc.MissingPaths[path] = n + 1;
                    continue;
                }
                rc.Resolved++;
                var type = b.type;
                if (type == null || type == typeof(GameObject) || type == typeof(Transform) || !typeof(Component).IsAssignableFrom(type)) continue;
                Component comp;
                try { comp = t.GetComponent(type); }
                catch (Exception) { continue; }
                if (comp == null)
                {
                    var key = (path.Length == 0 ? "(root)" : path) + " (" + type.Name + ")";
                    if (seenComp.Add(key)) rc.MissingComponents.Add(key);
                    continue;
                }
                if (comp is SkinnedMeshRenderer smr && b.propertyName != null && b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                {
                    var shape = b.propertyName.Substring("blendShape.".Length);
                    var mesh = smr.sharedMesh;
                    if (mesh != null && mesh.GetBlendShapeIndex(shape) < 0)
                    {
                        var key = (path.Length == 0 ? "(root)" : path) + ":" + shape;
                        if (seenShape.Add(key)) rc.MissingBlendShapes.Add(key);
                    }
                }
            }
            return rc;
        }

        // =====================================================================================================================
        // find_parameter / find_binding
        // =====================================================================================================================

        static void RunFind(Ctx ctx)
        {
            if (ctx.FindParameter == null && ctx.FindBinding == null) return;
            var find = new Dictionary<string, object>();
            var summary = new Dictionary<string, object>();
            bool cut = false;
            if (ctx.FindParameter != null)
            {
                find["parameter"] = FindParameter(ctx, ctx.FindParameter);
                summary["parameter"] = SummarizeFind((Dictionary<string, object>)find["parameter"], "uses", ref cut);
            }
            if (ctx.FindBinding != null)
            {
                find["binding"] = FindBinding(ctx, ctx.FindBinding);
                summary["binding"] = SummarizeFind((Dictionary<string, object>)find["binding"], "matches", ref cut);
            }
            // The summary stays small enough for an MCP response; the report keeps up to FindCap rows per search.
            ctx.R.Put("find", summary);
            if (cut) ctx.R.Put("find_all", find);
        }

        static Dictionary<string, object> SummarizeFind(Dictionary<string, object> full, string listKey, ref bool cut)
        {
            if (!(full.TryGetValue(listKey, out var v) && v is List<object> list) || list.Count <= FindSummaryCap) return full;
            cut = true;
            var d = new Dictionary<string, object>(full)
            {
                [listKey] = list.Take(FindSummaryCap).ToList(),
                [listKey + "_in_summary"] = FindSummaryCap,
                ["full_list"] = "report_path の find_all にあります (最大 " + FindCap + " 行)",
            };
            return d;
        }

        static Dictionary<string, object> FindParameter(Ctx ctx, string name)
        {
            var d = new Dictionary<string, object> { ["name"] = name };
            var declared = new List<object>();
            foreach (var u in ctx.Units)
            {
                var p = u.ParamList.FirstOrDefault(x => x.name == name);
                if (p != null)
                    declared.Add(new Dictionary<string, object> { ["controller"] = u.Id, ["type"] = p.type.ToString(), ["default"] = ParamDefault(p) });
            }
            d["declared_in"] = declared;
            if (ctx.Expression != null && ctx.Expression.TryGetValue(name, out var ep))
                d["expression"] = new Dictionary<string, object>
                {
                    ["type"] = ExprTypeName(ep.ValueType), ["synced"] = ep.Synced, ["saved"] = ep.Saved, ["default"] = Round(ep.Default),
                };
            if (BuiltinParams.TryGetValue(name, out var bt)) d["builtin"] = bt;

            var uses = ctx.Uses.Where(x => x.Param == name).ToList();
            if (uses.Count == 0)
            {
                var ci = ctx.Uses.Select(x => x.Param).Where(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
                if (ci.Count > 0) d["case_insensitive_matches"] = ci.ToList<object>();
            }
            d["uses_total"] = uses.Count;
            d["uses"] = uses.Take(FindCap).Select(x =>
            {
                var ud = new Dictionary<string, object> { ["controller"] = x.Unit.Id, ["kind"] = x.Kind, ["at"] = x.At };
                if (x.Detail != null) ud["detail"] = x.Detail;
                return (object)ud;
            }).ToList();
            if (uses.Count > FindCap) d["uses_omitted"] = uses.Count - FindCap;
            if (declared.Count == 0 && uses.Count == 0 && !d.ContainsKey("expression"))
                ctx.Issue(CctSeverity.Info, "FIND_NO_MATCH", $"パラメータ '{name}' は解析したコントローラにも Expression Parameters にもありません (大文字小文字は区別されます)", value: name);
            return d;
        }

        /// <summary>
        /// Case-insensitive match against the binding path and "Type.property"; "path:property" requires both parts.
        /// </summary>
        static Dictionary<string, object> FindBinding(Ctx ctx, string query)
        {
            var q = query.ToLowerInvariant();
            string qPath = null, qProp = null;
            var colon = q.IndexOf(':');
            if (colon >= 0)
            {
                qPath = q.Substring(0, colon).Trim();
                qProp = q.Substring(colon + 1).Trim();
            }
            var results = new List<object>();
            int total = 0;
            var pathCache = new Dictionary<Transform, Dictionary<string, Transform>>();
            foreach (var cu in ctx.Clips.Values)
            {
                foreach (var pair in Curves(cu).Select(b => (b, false)).Concat(ObjectCurves(cu).Select(b => (b, true))))
                {
                    var b = pair.Item1;
                    var path = (b.path ?? "").ToLowerInvariant();
                    var prop = ((b.type != null ? b.type.Name : "") + "." + b.propertyName).ToLowerInvariant();
                    bool match = qPath != null
                        ? path.Contains(qPath) && prop.Contains(qProp)
                        : path.Contains(q) || prop.Contains(q) || (path + ":" + prop).Contains(q);
                    if (!match) continue;
                    total++;
                    if (results.Count >= FindCap) continue;
                    var row = new Dictionary<string, object>
                    {
                        ["clip"] = cu.Clip.name,
                        ["asset"] = AssetDatabase.GetAssetPath(cu.Clip),
                        ["path"] = b.path,
                        ["component"] = b.type != null ? b.type.Name : null,
                        ["property"] = b.propertyName,
                    };
                    DescribeCurve(cu.Clip, b, pair.Item2, row);
                    var roots = cu.Units.Where(x => x.BindingRoot != null).Select(x => x.BindingRoot).Distinct().ToList();
                    if (roots.Count > 0 && b.type != typeof(Animator))
                        row["exists"] = roots.All(r => FindPath(r, b.path, pathCache) != null);
                    row["used_by"] = cu.Locations.Take(5).ToList<object>();
                    if (cu.LocationCount > 5) row["used_by_total"] = cu.LocationCount;
                    results.Add(row);
                }
            }
            if (total == 0)
                ctx.Issue(CctSeverity.Info, "FIND_NO_MATCH", $"'{query}' に一致するカーブは解析したコントローラのクリップにありません", value: query);
            return new Dictionary<string, object>
            {
                ["query"] = query,
                ["matches_total"] = total,
                ["matches"] = results,
                ["matches_omitted"] = Math.Max(0, total - results.Count),
                ["clips_searched"] = ctx.Clips.Count,
            };
        }

        static void DescribeCurve(AnimationClip clip, EditorCurveBinding b, bool objectCurve, Dictionary<string, object> row)
        {
            try
            {
                if (objectCurve)
                {
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, b) ?? new ObjectReferenceKeyframe[0];
                    row["curve"] = "object";
                    row["keys"] = keys.Length;
                    var names = keys.Select(k => k.value != null ? k.value.name : "null").Distinct().Take(4).ToList<object>();
                    row["constant"] = names.Count <= 1;
                    row["values"] = names;
                    return;
                }
                var curve = AnimationUtility.GetEditorCurve(clip, b);
                var ks = curve?.keys ?? new Keyframe[0];
                row["curve"] = "float";
                row["keys"] = ks.Length;
                if (ks.Length == 0) return;
                bool constant = ks.All(k => Math.Abs(k.value - ks[0].value) < 1e-6f);
                row["constant"] = constant;
                if (constant) row["value"] = Round(ks[0].value);
                else
                {
                    row["first"] = Round(ks[0].value);
                    row["last"] = Round(ks[ks.Length - 1].value);
                    row["min"] = Round(ks.Min(k => k.value));
                    row["max"] = Round(ks.Max(k => k.value));
                }
            }
            catch (Exception e) { row["curve_error"] = e.Message; }
        }
    }
}
