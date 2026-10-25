using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// cct_animator_inspect: walks one controller (layers, state machines, states, motions, behaviours, transitions),
    /// builds the graph dump (bounded by max_states) and records parameter uses / clips / layer controls for the lints.
    /// Graph format: per layer a flat "machines" list (path "" = root) and a flat "states" list (path = "Sub/State");
    /// fields at their Unity default are omitted (speed 1, mirror false, cycle offset 0, interruption None, ordered
    /// interruption true, mute/solo false).
    /// </summary>
    public static partial class CctAnimatorInspect
    {
        sealed class MotionFlags
        {
            public bool ContainsDirect;
            public bool Empty;
            public int Nodes;
        }

        static void InspectUnit(Ctx ctx, Unit u)
        {
            if (u.Failed || u.Controller == null) return;
            var ac = u.Controller;
            foreach (var p in ac.parameters ?? new AnimatorControllerParameter[0])
            {
                if (p == null || p.name == null) continue;
                if (u.Params.ContainsKey(p.name))
                {
                    ctx.Issue(CctSeverity.Warning, "PARAM_DUPLICATE", $"パラメータ '{p.name}' が '{u.Label}' に重複して定義されています", u.Id, p.name, asset: u.Asset, quiet: u.Quiet);
                    continue;
                }
                u.Params[p.name] = p.type;
                u.ParamList.Add(p);
            }

            u.Layers = ac.layers ?? new AnimatorControllerLayer[0];
            for (int i = 0; i < u.Layers.Length; i++)
            {
                if (ctx.OverBudget) { ctx.TimeTruncated = true; break; }
                var layer = u.Layers[i];
                if (layer == null) continue;
                var ld = new LayerData { Unit = u, Index = i, Name = layer.name ?? "", Layer = layer };
                ld.Included = LayerIncluded(ctx, i, ld.Name);
                ld.Additive = layer.blendingMode == AnimatorLayerBlendingMode.Additive;
                u.LayerData.Add(ld);

                var machine = layer.stateMachine;
                if (layer.syncedLayerIndex >= 0)
                {
                    ld.SyncedFrom = layer.syncedLayerIndex;
                    machine = EffectiveMachine(u.Layers, i);
                }
                ld.Root = machine;
                if (machine == null)
                {
                    if (ld.Included)
                        ctx.Issue(CctSeverity.Warning, "LAYER_NO_STATE_MACHINE", $"レイヤー '{ld.Name}' にステートマシンがありません (MA はこのレイヤーをスキップします)",
                            Loc(u, ld, null), asset: u.Asset, quiet: u.Quiet);
                    continue;
                }
                CollectMachine(ld, machine, null, "", 0, new HashSet<AnimatorStateMachine>());
                AnalyzeLayer(ctx, u, ld);
            }
        }

        static bool LayerIncluded(Ctx ctx, int index, string name)
        {
            if (ctx.LayerFilter.Count == 0) return true;
            foreach (var f in ctx.LayerFilter)
            {
                if (int.TryParse(f, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && idx == index) return true;
                if (string.Equals(f, name, StringComparison.Ordinal)) return true;
            }
            foreach (var f in ctx.LayerFilter)
                if (string.Equals(f, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static AnimatorStateMachine EffectiveMachine(AnimatorControllerLayer[] layers, int index)
        {
            var cur = layers[index];
            for (int hop = 0; hop < 16 && cur != null && cur.syncedLayerIndex >= 0; hop++)
            {
                var next = cur.syncedLayerIndex;
                if (next < 0 || next >= layers.Length || next == index) return null;
                cur = layers[next];
            }
            return cur != null && cur.syncedLayerIndex < 0 ? cur.stateMachine : null;
        }

        static void CollectMachine(LayerData ld, AnimatorStateMachine m, MachineData parent, string path, int depth, HashSet<AnimatorStateMachine> seen)
        {
            if (m == null || depth > 48 || !seen.Add(m)) return;
            var md = new MachineData { Machine = m, Parent = parent, Path = path };
            ld.Machines.Add(md);
            ld.MachineByObj[m] = md;
            foreach (var cs in m.states ?? new ChildAnimatorState[0])
            {
                var s = cs.state;
                if (s == null || ld.StateByObj.ContainsKey(s)) continue;
                var sd = new StateData { State = s, Owner = md, Path = path.Length == 0 ? s.name : path + "/" + s.name };
                ld.States.Add(sd);
                ld.StateByObj[s] = sd;
            }
            foreach (var csm in m.stateMachines ?? new ChildAnimatorStateMachine[0])
            {
                var sub = csm.stateMachine;
                if (sub == null) continue;
                md.Subs.Add(sub);
                CollectMachine(ld, sub, md, path.Length == 0 ? sub.name : path + "/" + sub.name, depth + 1, seen);
            }
        }

        static string Loc(Unit u, LayerData ld, string inner) =>
            ld == null ? u.Id : $"{u.Id}:{ld.Name}" + (string.IsNullOrEmpty(inner) ? "" : "/" + inner);

        // =====================================================================================================================
        // Layer analysis
        // =====================================================================================================================

        static void AnalyzeLayer(Ctx ctx, Unit u, LayerData ld)
        {
            bool synced = ld.SyncedFrom >= 0;
            var layer = ld.Layer;
            foreach (var sd in ld.States)
            {
                var s = sd.State;
                sd.Wd = s.writeDefaultValues;
                Motion motion = null;
                try { motion = synced ? layer.GetOverrideMotion(s) : s.motion; }
                catch (Exception) { /* corrupted override pairs */ }
                sd.Motion = motion;
                sd.DirectBt = motion is BlendTree topBt && topBt.blendType == BlendTreeType.Direct;

                if (!synced)
                {
                    // SDK 3.10.5 mixed-WD rule: Additive layers and Direct blend tree states do not count.
                    // Counted for every layer (the controller-level rule ignores the layers filter).
                    if (ld.Additive || sd.DirectBt) ld.WdExcluded++;
                    else if (sd.Wd) ld.WdOn++;
                    else ld.WdOff++;
                }
                if (!ld.Included) continue;
                if (!synced)
                {
                    u.States++;
                    ctx.TotalStates++;
                }

                string where = Loc(u, ld, sd.Path);
                bool emit = ctx.GraphStates < ctx.MaxStates && !ctx.OverBudget;
                var flags = new MotionFlags();
                object motionDesc = null;
                if (motion == null)
                {
                    if (!synced && IsMissingRef(s, "m_Motion"))
                    {
                        sd.MissingMotion = true;
                        ctx.Issue(CctSeverity.Warning, "MISSING_MOTION", "ステートの Motion 参照が切れています (削除されたアセット)", where, asset: u.Asset, quiet: u.Quiet);
                        motionDesc = new Dictionary<string, object> { ["type"] = "missing" };
                    }
                }
                else motionDesc = DescribeMotion(ctx, u, ld, motion, where, 0, flags, emit, new HashSet<BlendTree>());
                sd.ContainsDirect = flags.ContainsDirect;
                sd.Empty = motion == null || flags.Empty;

                if (s.speedParameterActive && !string.IsNullOrEmpty(s.speedParameter)) AddUse(ctx, u, ld, where, "speed", s.speedParameter);
                if (s.timeParameterActive && !string.IsNullOrEmpty(s.timeParameter)) AddUse(ctx, u, ld, where, "motion_time", s.timeParameter);
                if (s.mirrorParameterActive && !string.IsNullOrEmpty(s.mirrorParameter)) AddUse(ctx, u, ld, where, "mirror", s.mirrorParameter);
                if (s.cycleOffsetParameterActive && !string.IsNullOrEmpty(s.cycleOffsetParameter)) AddUse(ctx, u, ld, where, "cycle_offset", s.cycleOffsetParameter);

                var behaviours = synced ? SyncedBehaviours(u, ld, s) : GetBehaviours(s, () => s.behaviours);
                var bdesc = new List<object>();
                foreach (var b in behaviours)
                {
                    var d = DescribeBehaviour(ctx, u, ld, where, b, false, sd);
                    if (emit) bdesc.Add(d);
                }

                var tdesc = new List<object>();
                if (!synced)
                {
                    sd.Transitions = (s.transitions ?? new AnimatorStateTransition[0]).Where(t => t != null).ToArray();
                    foreach (var t in sd.Transitions)
                    {
                        u.Transitions++;
                        ctx.TotalTransitions++;
                        AnalyzeTransition(ctx, u, ld, t, where, "state");
                        if (emit && ctx.IncludeTransitions) tdesc.Add(DescribeTransition(ld, t, false));
                    }
                }

                if (!emit)
                {
                    ld.StatesOmitted++;
                    ctx.GraphTruncated = true;
                    continue;
                }
                ctx.GraphStates++;
                var sdict = new Dictionary<string, object> { ["name"] = s.name, ["path"] = sd.Path };
                if (sd.Owner.Machine.defaultState == s) sdict["default"] = true;
                sdict["motion"] = motionDesc;
                if (Math.Abs(s.speed - 1f) > 1e-6f) sdict["speed"] = Round(s.speed);
                if (s.speedParameterActive) sdict["speed_param"] = s.speedParameter;
                if (s.timeParameterActive) sdict["motion_time_param"] = s.timeParameter;
                if (s.mirror) sdict["mirror"] = true;
                if (s.mirrorParameterActive) sdict["mirror_param"] = s.mirrorParameter;
                if (Math.Abs(s.cycleOffset) > 1e-6f) sdict["cycle_offset"] = Round(s.cycleOffset);
                if (s.cycleOffsetParameterActive) sdict["cycle_offset_param"] = s.cycleOffsetParameter;
                sdict["wd"] = sd.Wd;
                if (!string.IsNullOrEmpty(s.tag)) sdict["tag"] = s.tag;
                if (s.iKOnFeet) sdict["ik_on_feet"] = true;
                if (bdesc.Count > 0) sdict["behaviours"] = bdesc;
                if (tdesc.Count > 0) sdict["transitions"] = tdesc;
                else if (sd.Transitions.Length > 0 && !ctx.IncludeTransitions) sdict["transition_count"] = sd.Transitions.Length;
                ld.GraphStates.Add(sdict);
            }

            if (!ld.Included) return;
            if (ld.States.Count == 0)
                ctx.Issue(CctSeverity.Info, "LAYER_EMPTY",
                    $"レイヤー '{ld.Name}' にステートがありません" +
                    // ndmf GlobalTransformations.RemoveEmptyLayers (run by MA): no states / sub machines, not synced, not first.
                    (ctx.MaInstalled && ld.Index > 0 && !synced && ld.Machines.Count <= 1 ? " (Modular Avatar 導入済みのため、ビルド時に削除されます)" : ""),
                    Loc(u, ld, null), asset: u.Asset, quiet: u.Quiet);
            if (synced) return; // transitions / machine behaviours belong to the source layer

            foreach (var md in ld.Machines)
            {
                var m = md.Machine;
                string mwhere = Loc(u, ld, md.Path.Length == 0 ? "(root)" : md.Path);
                md.AnyState = (m.anyStateTransitions ?? new AnimatorStateTransition[0]).Where(t => t != null).ToArray();
                md.Entry = (m.entryTransitions ?? new AnimatorTransition[0]).Where(t => t != null).ToArray();
                if (md.Parent != null)
                {
                    try { md.Exit = (md.Parent.Machine.GetStateMachineTransitions(m) ?? new AnimatorTransition[0]).Where(t => t != null).ToArray(); }
                    catch (Exception) { md.Exit = new AnimatorTransition[0]; }
                }
                var mdict = new Dictionary<string, object> { ["path"] = md.Path };
                if (m.defaultState != null) mdict["default_state"] = ld.StateByObj.TryGetValue(m.defaultState, out var dsd) ? dsd.Path : m.defaultState.name;

                var any = new List<object>();
                foreach (var t in md.AnyState)
                {
                    u.Transitions++;
                    ctx.TotalTransitions++;
                    AnalyzeTransition(ctx, u, ld, t, mwhere + " [AnyState]", "any");
                    if (ctx.IncludeTransitions) any.Add(DescribeTransition(ld, t, true));
                }
                var entry = new List<object>();
                foreach (var t in md.Entry)
                {
                    AnalyzeTransition(ctx, u, ld, t, mwhere + " [Entry]", "entry");
                    if (ctx.IncludeTransitions) entry.Add(DescribeTransition(ld, t, false));
                }
                var exit = new List<object>();
                foreach (var t in md.Exit)
                {
                    AnalyzeTransition(ctx, u, ld, t, mwhere + " [Exit]", "exit");
                    if (ctx.IncludeTransitions) exit.Add(DescribeTransition(ld, t, false));
                }
                if (any.Count > 0) mdict["any_state_transitions"] = any;
                else if (md.AnyState.Length > 0) mdict["any_state_transition_count"] = md.AnyState.Length;
                if (entry.Count > 0) mdict["entry_transitions"] = entry;
                if (exit.Count > 0) mdict["exit_transitions"] = exit;

                var mb = new List<object>();
                foreach (var b in GetBehaviours(m, () => m.behaviours))
                    mb.Add(DescribeBehaviour(ctx, u, ld, mwhere, b, true));
                if (mb.Count > 0) mdict["behaviours"] = mb;
                ld.GraphMachines.Add(mdict);
            }
        }

        static bool IsMissingRef(Object owner, string property)
        {
            try
            {
                using (var so = new SerializedObject(owner))
                    return CctSerialized.IsMissingReference(so.FindProperty(property));
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// state.behaviours casts every entry to StateMachineBehaviour and throws when a behaviour's script is missing,
        /// so fall back to the serialized m_StateMachineBehaviours list (missing scripts become null entries).
        /// </summary>
        static List<Object> GetBehaviours(Object owner, Func<StateMachineBehaviour[]> getter)
        {
            try
            {
                var arr = getter();
                return arr == null ? new List<Object>() : arr.Select(b => (Object)b).ToList();
            }
            catch (Exception) { /* InvalidCastException for missing scripts: read serialized list instead */ }
            var list = new List<Object>();
            try
            {
                using (var so = new SerializedObject(owner))
                    foreach (var el in CctSerialized.Elements(so.FindProperty("m_StateMachineBehaviours")))
                        list.Add(CctSerialized.GetObject(el) as StateMachineBehaviour);
            }
            catch (Exception) { /* leave what we have */ }
            return list;
        }

        /// <summary>
        /// Behaviours of a state in a synced layer (the layer's own override list, not the source state's).
        /// AnimatorControllerLayer.GetOverrideBehaviours (what NDMF / AAO use) and AnimatorController.GetStateEffectiveBehaviours
        /// both return "ScriptableObject[] as StateMachineBehaviour[]" (UnityCsReference), which can be null; then the
        /// serialized m_AnimatorLayers[i].m_Behaviours[] {m_State, m_StateMachineBehaviours[]} list is read instead
        /// (missing scripts become null entries -> MISSING_BEHAVIOUR).
        /// </summary>
        static List<Object> SyncedBehaviours(Unit u, LayerData ld, AnimatorState s)
        {
            StateMachineBehaviour[] arr = null;
            try { arr = ld.Layer.GetOverrideBehaviours(s); }
            catch (Exception) { /* fall through */ }
            if (arr == null)
            {
                try { arr = u.Controller.GetStateEffectiveBehaviours(s, ld.Index); }
                catch (Exception) { /* fall through */ }
            }
            if (arr != null) return arr.Select(b => (Object)b).ToList();

            if (ld.SerializedOverrideBehaviours == null)
            {
                ld.SerializedOverrideBehaviours = new Dictionary<AnimatorState, List<Object>>();
                try
                {
                    using (var so = new SerializedObject(u.Controller))
                    {
                        var layers = so.FindProperty("m_AnimatorLayers");
                        var layerProp = layers != null && layers.isArray && ld.Index < layers.arraySize ? layers.GetArrayElementAtIndex(ld.Index) : null;
                        foreach (var pair in CctSerialized.Elements(CctSerialized.Child(layerProp, "m_Behaviours")))
                        {
                            if (!(CctSerialized.GetObject(CctSerialized.Child(pair, "m_State")) is AnimatorState state)) continue;
                            ld.SerializedOverrideBehaviours[state] = CctSerialized.Elements(CctSerialized.Child(pair, "m_StateMachineBehaviours"))
                                .Select(el => (Object)(CctSerialized.GetObject(el) as StateMachineBehaviour)).ToList();
                        }
                    }
                }
                catch (Exception) { /* leave empty */ }
            }
            return ld.SerializedOverrideBehaviours.TryGetValue(s, out var list) ? list : new List<Object>();
        }

        // =====================================================================================================================
        // Motions
        // =====================================================================================================================

        static object DescribeMotion(Ctx ctx, Unit u, LayerData ld, Motion m, string where, int depth, MotionFlags f, bool emit, HashSet<BlendTree> stack)
        {
            if (m == null) return null;
            if (m is AnimationClip clip)
            {
                var eff = clip;
                if (u.Overrides != null && u.Overrides.TryGetValue(clip, out var ov) && ov != null) eff = ov;
                RegisterClip(ctx, u, eff, where);
                if (eff.empty) f.Empty = true;
                if (!emit) return null;
                var d = new Dictionary<string, object> { ["type"] = "clip", ["name"] = eff.name };
                var path = AssetDatabase.GetAssetPath(eff);
                if (!string.IsNullOrEmpty(path) && path != u.Asset) d["asset"] = path;
                d["length"] = Round(eff.length);
                if (eff.isLooping) d["loop"] = true;
                if (eff.empty) d["empty"] = true;
                if (eff != clip) d["overrides"] = clip.name;
                return d;
            }
            if (m is BlendTree bt)
            {
                if (depth > MaxBlendTreeDepth || !stack.Add(bt))
                    return emit ? new Dictionary<string, object> { ["type"] = "blend_tree", ["name"] = bt.name, ["truncated"] = true } : null;
                try
                {
                    var type = bt.blendType;
                    if (type == BlendTreeType.Direct) f.ContainsDirect = true;
                    var children = bt.children ?? new ChildMotion[0];
                    Dictionary<string, object> d = null;
                    if (emit) d = new Dictionary<string, object> { ["type"] = "blend_tree", ["name"] = bt.name, ["blend_type"] = type.ToString() };
                    bool twoD = type == BlendTreeType.SimpleDirectional2D || type == BlendTreeType.FreeformDirectional2D || type == BlendTreeType.FreeformCartesian2D;
                    if (type != BlendTreeType.Direct)
                    {
                        if (!string.IsNullOrEmpty(bt.blendParameter)) AddUse(ctx, u, ld, where, "blend_tree", bt.blendParameter, detail: bt.name);
                        if (twoD && !string.IsNullOrEmpty(bt.blendParameterY)) AddUse(ctx, u, ld, where, "blend_tree", bt.blendParameterY, detail: bt.name);
                        if (emit)
                        {
                            d["parameter"] = bt.blendParameter;
                            if (twoD) d["parameter_y"] = bt.blendParameterY;
                        }
                    }
                    var list = emit ? new List<object>() : null;
                    SerializedObject btSo = null;
                    try
                    {
                        for (int i = 0; i < children.Length; i++)
                        {
                            if (++f.Nodes > MaxMotionNodesPerState)
                            {
                                if (emit) d["truncated"] = true;
                                break;
                            }
                            var c = children[i];
                            if (type == BlendTreeType.Direct && !string.IsNullOrEmpty(c.directBlendParameter))
                                AddUse(ctx, u, ld, where, "blend_tree_direct", c.directBlendParameter, detail: bt.name);
                            bool show = emit && i < MaxChildrenShown;
                            object md = null;
                            if (c.motion == null)
                            {
                                f.Empty = true;
                                if (btSo == null) btSo = new SerializedObject(bt);
                                var childs = btSo.FindProperty("m_Childs");
                                var childProp = childs != null && childs.isArray && i < childs.arraySize
                                    ? CctSerialized.Child(childs.GetArrayElementAtIndex(i), "m_Motion")
                                    : null;
                                if (CctSerialized.IsMissingReference(childProp))
                                {
                                    ctx.Issue(CctSeverity.Warning, "MISSING_MOTION", $"BlendTree '{bt.name}' の子 {i} の Motion 参照が切れています", where, asset: u.Asset, quiet: u.Quiet);
                                    md = new Dictionary<string, object> { ["type"] = "missing" };
                                }
                            }
                            else md = DescribeMotion(ctx, u, ld, c.motion, where, depth + 1, f, show, stack);
                            if (!show) continue;
                            var cd = new Dictionary<string, object> { ["motion"] = md };
                            if (type == BlendTreeType.Direct) cd["param"] = c.directBlendParameter;
                            else if (twoD) cd["position"] = new List<object> { Round(c.position.x), Round(c.position.y) };
                            else cd["threshold"] = Round(c.threshold);
                            if (Math.Abs(c.timeScale - 1f) > 1e-6f) cd["time_scale"] = Round(c.timeScale);
                            if (c.mirror) cd["mirror"] = true;
                            list.Add(cd);
                        }
                    }
                    finally { btSo?.Dispose(); }
                    if (emit)
                    {
                        d["children"] = list;
                        if (children.Length > MaxChildrenShown) d["children_omitted"] = children.Length - MaxChildrenShown;
                    }
                    return d;
                }
                finally { stack.Remove(bt); }
            }
            return emit ? new Dictionary<string, object> { ["type"] = m.GetType().Name, ["name"] = m.name } : null;
        }

        static void RegisterClip(Ctx ctx, Unit u, AnimationClip clip, string where)
        {
            if (!ctx.Clips.TryGetValue(clip, out var cu))
            {
                cu = new ClipUse { Clip = clip };
                ctx.Clips[clip] = cu;
            }
            if (!cu.Units.Contains(u)) cu.Units.Add(u);
            cu.LocationCount++;
            if (cu.Locations.Count < 10 && !cu.Locations.Contains(where)) cu.Locations.Add(where);
        }

        static void AddUse(Ctx ctx, Unit u, LayerData ld, string at, string kind, string param, string detail = null,
            bool hasMode = false, AnimatorConditionMode mode = AnimatorConditionMode.If)
        {
            ctx.Uses.Add(new ParamUse
            {
                Unit = u, Layer = ld?.Name, At = at, Kind = kind, Param = param ?? "", Detail = detail, HasMode = hasMode, Mode = mode,
            });
        }

        // =====================================================================================================================
        // Transitions
        // =====================================================================================================================

        static void AnalyzeTransition(Ctx ctx, Unit u, LayerData ld, AnimatorTransitionBase t, string from, string kind)
        {
            var conds = t.conditions ?? new AnimatorCondition[0];
            string at = from + " -> " + DestLabel(ld, t);
            foreach (var c in conds)
                AddUse(ctx, u, ld, at, "condition", c.parameter, ConditionText(c), true, c.mode);
            if (t.mute) return;
            if (t.destinationState == null && t.destinationStateMachine == null && !t.isExit)
                ctx.Issue(CctSeverity.Warning, "TRANSITION_NO_DESTINATION", "遷移先 (ステート / ステートマシン / Exit) がありません", at, asset: u.Asset, quiet: u.Quiet);
            if (!(t is AnimatorStateTransition st)) return;
            if (conds.Length == 0 && !st.hasExitTime)
                ctx.Issue(CctSeverity.Warning, "TRANSITION_NEVER_FIRES",
                    "条件も Exit Time も無い遷移です。Unity はこの遷移を無視します (条件を足すか Has Exit Time を有効に)", at, asset: u.Asset, quiet: u.Quiet);
            if (kind == "any" && st.canTransitionToSelf && !conds.Any(c => u.Params.TryGetValue(c.parameter ?? "", out var pt) && pt == AnimatorControllerParameterType.Trigger))
            {
                // Self-resetting pattern: the destination's Parameter Driver rewrites a condition parameter on entry, so the
                // condition usually turns false after one entry (then this is not a loop).
                var resets = t.destinationState != null && ld.StateByObj.TryGetValue(t.destinationState, out var dsd)
                    ? conds.Select(c => c.parameter).Where(p => !string.IsNullOrEmpty(p) && dsd.DriverWrites.Contains(p)).Distinct().ToList()
                    : new List<string>();
                if (resets.Count > 0)
                    ctx.Issue(CctSeverity.Info, "ANYSTATE_SELF_LOOP",
                        $"AnyState からの遷移で Can Transition To Self が有効です。遷移先の Parameter Driver が条件のパラメータ ({string.Join(", ", resets)}) を書き換えるので、通常は 1 回で止まります。書き換え後も条件が成立する値なら入り直しを繰り返します",
                        at, ConditionsSummary(conds), asset: u.Asset, quiet: u.Quiet);
                else
                    ctx.Issue(CctSeverity.Warning, "ANYSTATE_SELF_LOOP",
                        "AnyState からの遷移で Can Transition To Self が有効、かつ Trigger 以外の条件です。条件が成立している間は遷移が繰り返され、ステートが毎回最初から再生されます (Behaviour も毎回実行)。通常は Can Transition To Self を外します",
                        at, ConditionsSummary(conds), asset: u.Asset, quiet: u.Quiet);
            }
        }

        static string DestLabel(LayerData ld, AnimatorTransitionBase t)
        {
            if (t.isExit) return "Exit";
            if (t.destinationState != null)
                return ld.StateByObj.TryGetValue(t.destinationState, out var sd) ? sd.Path : t.destinationState.name;
            if (t.destinationStateMachine != null)
                return "[" + (ld.MachineByObj.TryGetValue(t.destinationStateMachine, out var md) ? (md.Path.Length == 0 ? "(root)" : md.Path) : t.destinationStateMachine.name) + "]";
            return "(なし)";
        }

        static Dictionary<string, object> DescribeTransition(LayerData ld, AnimatorTransitionBase t, bool anyState)
        {
            var d = new Dictionary<string, object> { ["to"] = DestLabel(ld, t) };
            var conds = t.conditions ?? new AnimatorCondition[0];
            if (conds.Length > 0)
                d["conditions"] = conds.Select(c =>
                {
                    var cd = new Dictionary<string, object> { ["param"] = c.parameter, ["mode"] = c.mode.ToString() };
                    if (c.mode != AnimatorConditionMode.If && c.mode != AnimatorConditionMode.IfNot) cd["threshold"] = Round(c.threshold);
                    return (object)cd;
                }).ToList();
            if (t is AnimatorStateTransition st)
            {
                d["has_exit_time"] = st.hasExitTime;
                if (st.hasExitTime) d["exit_time"] = Round(st.exitTime);
                d["duration"] = Round(st.duration);
                d["fixed"] = st.hasFixedDuration;
                if (Math.Abs(st.offset) > 1e-6f) d["offset"] = Round(st.offset);
                if (st.interruptionSource != TransitionInterruptionSource.None) d["interruption"] = st.interruptionSource.ToString();
                if (!st.orderedInterruption) d["ordered_interruption"] = false;
                if (anyState) d["can_transition_to_self"] = st.canTransitionToSelf;
            }
            if (t.mute) d["mute"] = true;
            if (t.solo) d["solo"] = true;
            return d;
        }

        static string ConditionText(AnimatorCondition c)
        {
            switch (c.mode)
            {
                case AnimatorConditionMode.If: return "If (true)";
                case AnimatorConditionMode.IfNot: return "IfNot (false)";
                default: return c.mode + " " + Round(c.threshold).ToString(CultureInfo.InvariantCulture);
            }
        }

        static string ConditionsSummary(AnimatorCondition[] conds) =>
            conds.Length == 0 ? "(条件なし)" : string.Join(" && ", conds.Select(c => c.parameter + " " + ConditionText(c)));

        // =====================================================================================================================
        // State machine behaviours (VRChat SDK fields from FieldCheck dumps; enums mapped by underlying int)
        // =====================================================================================================================

        static readonly string[][] TrackingParts =
        {
            new[] { "head", "trackingHead" }, new[] { "left_hand", "trackingLeftHand" }, new[] { "right_hand", "trackingRightHand" },
            new[] { "hip", "trackingHip" }, new[] { "left_foot", "trackingLeftFoot" }, new[] { "right_foot", "trackingRightFoot" },
            new[] { "left_fingers", "trackingLeftFingers" }, new[] { "right_fingers", "trackingRightFingers" },
            new[] { "eyes", "trackingEyes" }, new[] { "mouth", "trackingMouth" },
        };

        static Dictionary<string, object> DescribeBehaviour(Ctx ctx, Unit u, LayerData ld, string at, Object b, bool onMachine,
            StateData state = null)
        {
            if (b == null)
            {
                ctx.Issue(CctSeverity.Warning, "MISSING_BEHAVIOUR", "スクリプトが見つからない State Machine Behaviour があります (Missing Script)", at, asset: u.Asset, quiet: u.Quiet);
                return new Dictionary<string, object> { ["type"] = "(missing script)" };
            }
            var d = new Dictionary<string, object> { ["type"] = b.GetType().Name };
            try
            {
                using (var so = new SerializedObject(b))
                {
                    if (IsAny(b, TParameterDriver, TParameterDriverBase)) ReadDriver(ctx, u, ld, at, so, d, state);
                    else if (IsAny(b, TTrackingControl, TTrackingControlBase))
                    {
                        var parts = new Dictionary<string, object>();
                        foreach (var p in TrackingParts)
                        {
                            // VRC_AnimatorTrackingControl+TrackingType: NoChange=0, Tracking=1, Animation=2
                            var v = CctSerialized.GetInt(so.FindProperty(p[1]), 0);
                            if (v != 0) parts[p[0]] = v == 1 ? "Tracking" : v == 2 ? "Animation" : v.ToString(CultureInfo.InvariantCulture);
                            if (v == 1 || v == 2) AddPersist(ctx, u, at, "tracking:" + p[0], v == 2);
                        }
                        d["tracking"] = parts;
                    }
                    else if (IsAny(b, TLayerControl, TLayerControlBase)) ReadLayerControl(ctx, u, ld, at, so, d);
                    else if (IsAny(b, TPlayableLayerControl, TPlayableLayerControlBase))
                    {
                        var layer = CctSerialized.GetInt(so.FindProperty("layer"), 0);
                        var goal = CctSerialized.GetFloat(so.FindProperty("goalWeight"), 0f);
                        var dur = CctSerialized.GetFloat(so.FindProperty("blendDuration"), 0f);
                        d["layer"] = BlendableName(layer);
                        d["goal_weight"] = Round(goal);
                        d["blend_duration"] = Round(dur);
                        ctx.PlayableControls.Add(new PlayableControlRef { Unit = u, At = at, Layer = layer, Goal = goal, Duration = dur });
                        if (layer == 0) AddPersist(ctx, u, at, "action_weight", goal > 0f); // BlendableLayer Action=0
                    }
                    else if (IsAny(b, TLocomotionControl, TLocomotionControlBase))
                    {
                        var disable = CctSerialized.GetBool(so.FindProperty("disableLocomotion"));
                        d["disable_locomotion"] = disable;
                        AddPersist(ctx, u, at, "locomotion", disable);
                    }
                    else if (IsAny(b, TTemporaryPoseSpace, TTemporaryPoseSpaceBase))
                    {
                        var enter = CctSerialized.GetBool(so.FindProperty("enterPoseSpace"));
                        d["enter_pose_space"] = enter;
                        AddPersist(ctx, u, at, "pose_space", enter);
                        d["fixed_delay"] = CctSerialized.GetBool(so.FindProperty("fixedDelay"));
                        d["delay_time"] = Round(CctSerialized.GetFloat(so.FindProperty("delayTime")));
                    }
                    else if (IsAny(b, TPlayAudio, TPlayAudioBase))
                    {
                        ReadPlayAudio(ctx, u, ld, at, so, d, onMachine);
                        return d; // VRC_AnimatorPlayAudio has no debugString
                    }
                    else
                    {
                        d["type"] = b.GetType().FullName;
                        return d;
                    }

                    // debugString: declared by the parameter driver, tracking / layer / playable layer / locomotion control
                    // and temporary pose space behaviours (FieldCheck dumps).
                    var dbg = CctSerialized.GetString(so.FindProperty("debugString"));
                    if (!string.IsNullOrEmpty(dbg)) d["debug"] = dbg;
                }
            }
            catch (Exception e) { d["error"] = e.GetType().Name + ": " + e.Message; }
            return d;
        }

        static bool IsAny(Object o, string a, string b) => CctSerialized.IsType(o, a) || CctSerialized.IsType(o, b);

        // VRC_AvatarParameterDriver+ChangeType: Set=0, Add=1, Random=2, Copy=3
        static string ChangeTypeName(int v)
        {
            switch (v)
            {
                case 0: return "Set";
                case 1: return "Add";
                case 2: return "Random";
                case 3: return "Copy";
                default: return v.ToString(CultureInfo.InvariantCulture);
            }
        }

        static void ReadDriver(Ctx ctx, Unit u, LayerData ld, string at, SerializedObject so, Dictionary<string, object> d, StateData state)
        {
            bool local = CctSerialized.GetBool(so.FindProperty("localOnly"));
            d["local_only"] = local;
            var entries = new List<object>();
            foreach (var el in CctSerialized.Elements(so.FindProperty("parameters")))
            {
                int type = CctSerialized.GetInt(CctSerialized.Child(el, "type"), 0);
                var typeName = ChangeTypeName(type);
                var name = CctSerialized.GetString(CctSerialized.Child(el, "name"), "");
                var value = CctSerialized.GetFloat(CctSerialized.Child(el, "value"));
                var e = new Dictionary<string, object> { ["type"] = typeName, ["name"] = name };
                var destType = ParamTypeName(ctx, u, name);
                string where = at + " [Driver " + typeName + " " + name + "]";
                AddUse(ctx, u, ld, at, "driver_dest", name, typeName);
                if (state != null && !string.IsNullOrEmpty(name)) state.DriverWrites.Add(name);
                switch (type)
                {
                    case 0:
                    case 1:
                        e["value"] = Round(value);
                        break;
                    case 2:
                        if (destType != "Bool")
                        {
                            e["min"] = Round(CctSerialized.GetFloat(CctSerialized.Child(el, "valueMin")));
                            e["max"] = Round(CctSerialized.GetFloat(CctSerialized.Child(el, "valueMax")));
                            if (CctSerialized.GetBool(CctSerialized.Child(el, "preventRepeats"))) e["prevent_repeats"] = true;
                        }
                        if (destType == "Bool" || destType == null) e["chance"] = Round(CctSerialized.GetFloat(CctSerialized.Child(el, "chance")));
                        break;
                    case 3:
                        var source = CctSerialized.GetString(CctSerialized.Child(el, "source"), "");
                        e["source"] = source;
                        if (CctSerialized.GetBool(CctSerialized.Child(el, "convertRange")))
                        {
                            e["source_range"] = new List<object> { Round(CctSerialized.GetFloat(CctSerialized.Child(el, "sourceMin"))), Round(CctSerialized.GetFloat(CctSerialized.Child(el, "sourceMax"))) };
                            e["dest_range"] = new List<object> { Round(CctSerialized.GetFloat(CctSerialized.Child(el, "destMin"))), Round(CctSerialized.GetFloat(CctSerialized.Child(el, "destMax"))) };
                        }
                        AddUse(ctx, u, ld, at, "driver_source", source, "Copy -> " + name);
                        if (BuiltinParams.ContainsKey(source))
                            ctx.Issue(CctSeverity.Warning, "DRIVER_COPY_BUILTIN_SOURCE",
                                $"Copy の Source に VRChat 組み込みパラメータ '{source}' が指定されています。docs: 組み込みパラメータは Source として機能しません (state-behaviors.md)",
                                where, source, asset: u.Asset, quiet: u.Quiet);
                        var srcType = ParamTypeName(ctx, u, source);
                        if (srcType != null && destType != null && srcType != destType)
                            ctx.Issue(CctSeverity.Info, "DRIVER_COPY_TYPE_CONVERSION",
                                $"Copy で {srcType} '{source}' → {destType} '{name}' に型変換されます。docs (state-behaviors.md): bool からは false=0/true=1、bool へは 0 が false それ以外 true、int へは常に切り捨て、float へはそのままコピー (同期範囲外でも)",
                                where, srcType + "->" + destType, asset: u.Asset, quiet: u.Quiet);
                        break;
                }
                entries.Add(e);

                if (BuiltinParams.ContainsKey(name))
                    ctx.Issue(CctSeverity.Warning, "DRIVER_BUILTIN_DESTINATION",
                        $"Parameter Driver が VRChat 組み込みパラメータ '{name}' を書き込もうとしています。docs: 組み込みパラメータは駆動できません (state-behaviors.md)",
                        where, name, asset: u.Asset, quiet: u.Quiet);
                if (type == 1 && destType == "Bool")
                    ctx.Issue(CctSeverity.Warning, "DRIVER_ADD_ON_BOOL",
                        $"Bool パラメータ '{name}' に Add を使っています。docs: Add は float / int のみ (bool は Set / Random / Copy)",
                        where, name, asset: u.Asset, quiet: u.Quiet);
                if ((type == 1 || type == 2) && !local)
                    ctx.Issue(CctSeverity.Info, "DRIVER_IN_NON_LOCAL",
                        $"{typeName} が Local Only なしで動きます。docs: Add / Random はリモートで結果が変わり得るため、同期パラメータを宛先にしてローカルだけで実行することが推奨されています",
                        where, name, asset: u.Asset, quiet: u.Quiet);
                // docs state-behaviors.md:118-120: anything defined in VRCExpressionParameters is clamped to Int [0,255] /
                // Float [-1,1]; parameters only in the controller are not. Checked for Set values and Random bounds.
                if ((type == 0 || type == 2) && ctx.Expression != null && ctx.Expression.TryGetValue(name, out var ep) &&
                    (ep.ValueType == 0 || ep.ValueType == 1))
                {
                    float lo = ep.ValueType == 0 ? 0f : -1f, hi = ep.ValueType == 0 ? 255f : 1f;
                    var values = type == 0
                        ? new[] { value }
                        : new[] { CctSerialized.GetFloat(CctSerialized.Child(el, "valueMin")), CctSerialized.GetFloat(CctSerialized.Child(el, "valueMax")) };
                    var bad = values.Where(x => x < lo || x > hi).ToList();
                    if (bad.Count > 0)
                        ctx.Issue(CctSeverity.Info, "DRIVER_VALUE_OUT_OF_RANGE",
                            $"{typeName} の値 {string.Join(" / ", bad.Select(x => Round(x).ToString(CultureInfo.InvariantCulture)))} は Expression Parameter '{name}' ({ExprTypeName(ep.ValueType)}) の範囲外です。docs: Expression Parameters にあるパラメータは Int 0〜255 / Float -1〜1 に丸められます (clamp)",
                            where, Round(bad[0]), ep.ValueType == 0 ? "0..255" : "-1..1", u.Asset, u.Quiet);
                }
            }
            d["entries"] = entries;
        }

        static void ReadLayerControl(Ctx ctx, Unit u, LayerData ld, string at, SerializedObject so, Dictionary<string, object> d)
        {
            var playable = CctSerialized.GetInt(so.FindProperty("playable"), 0);
            var layer = CctSerialized.GetInt(so.FindProperty("layer"), 0);
            var goal = CctSerialized.GetFloat(so.FindProperty("goalWeight"), 0f);
            var dur = CctSerialized.GetFloat(so.FindProperty("blendDuration"), 0f);
            d["playable"] = BlendableName(playable);
            d["layer"] = layer;
            d["goal_weight"] = Round(goal);
            d["blend_duration"] = Round(dur);
            ctx.LayerControls.Add(new LayerControlRef { Unit = u, At = at, Playable = playable, Layer = layer, Goal = goal, Duration = dur });

            var targetType = BlendableToLayerType(playable);
            bool sameController = u.LayerType < 0 || u.LayerType == targetType;
            if (sameController)
            {
                if (layer >= 0 && layer < u.Layers.Length) d["target_layer"] = u.Layers[layer]?.name;
                else
                    ctx.Issue(CctSeverity.Warning, "LAYER_CONTROL_INDEX_OUT_OF_RANGE",
                        $"Animator Layer Control の Layer {layer} は '{u.Label}' のレイヤー数 {u.Layers.Length} を超えています", at, layer, u.Layers.Length, u.Asset, u.Quiet);
                if (u.LayerType < 0) d["target_layer_assumed"] = true;
            }
            if (layer == 0)
                ctx.Issue(CctSeverity.Warning, "LAYER_CONTROL_BASE_LAYER",
                    "Animator Layer Control が Layer 0 を対象にしています。docs: 0 番目 (ベース) レイヤーの重みは変更できず常に 1.0 です", at, layer, asset: u.Asset, quiet: u.Quiet);
            if (u.Ma != null && targetType != u.LayerType)
                ctx.Issue(CctSeverity.Warning, "MA_LAYER_CONTROL_UNSUPPORTED",
                    $"MA Merge Animator ({LayerTypeName(u.LayerType)}) のコントローラ内の Animator Layer Control が {BlendableName(playable)} を対象にしています。MA は同じアニメーター内のレイヤー参照だけに対応します (Playable を Merge Animator の Layer Type に合わせ、Layer はこのコントローラ内の番号に)",
                    at, BlendableName(playable), asset: u.Asset);
        }

        // VRC_AnimatorPlayAudio+Order: Random=0, UniqueRandom=1, Roundabout=2, Parameter=3
        static void ReadPlayAudio(Ctx ctx, Unit u, LayerData ld, string at, SerializedObject so, Dictionary<string, object> d, bool onMachine)
        {
            var sourcePath = CctSerialized.GetString(so.FindProperty("SourcePath"), "");
            var order = CctSerialized.GetInt(so.FindProperty("PlaybackOrder"), 0);
            d["source_path"] = sourcePath;
            d["playback_order"] = order == 0 ? "Random" : order == 1 ? "UniqueRandom" : order == 2 ? "Roundabout" : order == 3 ? "Parameter" : order.ToString(CultureInfo.InvariantCulture);
            d["clips"] = CctSerialized.ArraySize(so.FindProperty("Clips"));
            d["play_on_enter"] = CctSerialized.GetBool(so.FindProperty("PlayOnEnter"));
            d["stop_on_enter"] = CctSerialized.GetBool(so.FindProperty("StopOnEnter"));
            if (CctSerialized.GetBool(so.FindProperty("PlayOnExit"))) d["play_on_exit"] = true;
            if (CctSerialized.GetBool(so.FindProperty("StopOnExit"))) d["stop_on_exit"] = true;
            if (CctSerialized.GetBool(so.FindProperty("Loop"))) d["loop"] = true;
            var delay = CctSerialized.GetFloat(so.FindProperty("DelayInSeconds"));
            if (delay > 0f) d["delay_seconds"] = Round(delay);
            if (order == 3)
            {
                var param = CctSerialized.GetString(so.FindProperty("ParameterName"), "");
                d["parameter"] = param;
                AddUse(ctx, u, ld, at, "play_audio", param);
                // docs state-behaviors.md:203: an int avatar parameter that must be defined in Expression Parameters (the
                // client reads it; the SDK has no implementation to show whether the animator declaration matters).
                ExprParam pep = null;
                if (ctx.AvatarRoot != null && !string.IsNullOrEmpty(param) && (ctx.Expression == null || !ctx.Expression.TryGetValue(param, out pep)))
                    ctx.Issue(CctSeverity.Warning, "PLAY_AUDIO_PARAM_NOT_IN_EXPRESSIONS",
                        $"Animator Play Audio の Playback Order = Parameter の '{param}' が Expression Parameters にありません{(ctx.Expression == null ? " (Expression Parameters が未設定)" : "")}。docs: Expression Parameters に int で定義が必要で、見つからない場合は最初のクリップが使われます",
                        at, param, asset: u.Asset, quiet: u.Quiet);
                else if (pep != null && pep.ValueType != 0)
                    ctx.Issue(u.Quiet ? CctSeverity.Info : CctSeverity.Warning, "PARAM_TYPE_MISMATCH",
                        $"Animator Play Audio の Parameter '{param}' は Expression Parameters で {ExprTypeName(pep.ValueType)} です。docs: Playback Order = Parameter は int 型のパラメータで何番目のクリップかを選びます",
                        at, new Dictionary<string, object> { ["source"] = "usage", ["param"] = param, ["type"] = ExprTypeName(pep.ValueType), ["uses"] = 1, ["first"] = "play_audio" },
                        asset: u.Asset, quiet: u.Quiet);
            }
            if (u.BindingRoot != null)
            {
                // docs write Source Path with a trailing slash ("Armature/Hips/Spine/"); Transform.Find needs it trimmed.
                var trimmed = sourcePath.Trim('/');
                var t = trimmed.Length == 0 ? u.BindingRoot : u.BindingRoot.Find(trimmed);
                if (t == null || t.GetComponent<AudioSource>() == null)
                    ctx.Issue(u.BindingRootEstimate ? CctSeverity.Info : CctSeverity.Warning, "PLAY_AUDIO_SOURCE_MISSING",
                        $"Animator Play Audio の Source Path '{sourcePath}' に AudioSource がありません ({CctTargets.GetPath(u.BindingRoot)} 基準)。docs: 見つからない場合この Behaviour は無効になります",
                        at, sourcePath, asset: u.Asset, quiet: u.Quiet);
            }
            if (onMachine)
                ctx.Issue(CctSeverity.Info, "PLAY_AUDIO_ON_STATE_MACHINE",
                    "Animator Play Audio がサブステートマシン自体に付いています。docs: 内部の全遷移で実行されるため非推奨 (ステートに付ける)", at, asset: u.Asset, quiet: u.Quiet);
        }

        static void AddPersist(Ctx ctx, Unit u, string at, string kind, bool away) =>
            ctx.Persistent.Add(new PersistRef { Unit = u, At = at, Kind = kind, Away = away });

        /// <summary>Type of a parameter as seen by this controller: its own declaration, else the Expression Parameter type.</summary>
        static string ParamTypeName(Ctx ctx, Unit u, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (u.Params.TryGetValue(name, out var t)) return t.ToString();
            if (ctx.Expression != null && ctx.Expression.TryGetValue(name, out var ep)) return ExprTypeName(ep.ValueType);
            return null;
        }
    }
}
