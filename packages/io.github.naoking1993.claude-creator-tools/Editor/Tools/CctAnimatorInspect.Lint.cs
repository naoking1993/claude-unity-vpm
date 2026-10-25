using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor.Animations;
using UnityEngine;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>cct_animator_inspect: lints that need the whole walk (WD, reachability, layer weights, parameters, MA).</summary>
    public static partial class CctAnimatorInspect
    {
        static void Lint(Ctx ctx)
        {
            LintWriteDefaults(ctx);
            LintReachability(ctx);
            LintLayerWeights(ctx);
            LintParameterUses(ctx);
            LintParameterTypes(ctx);
            LintMaParameterTypes(ctx);
            LintAction(ctx);
            LintPersistent(ctx);
        }

        // =====================================================================================================================
        // Settings VRChat keeps until another state changes them back
        // =====================================================================================================================

        static readonly Dictionary<string, string> TrackingPartLabels = new Dictionary<string, string>
        {
            ["head"] = "頭 (Head)", ["left_hand"] = "左手 (Left Hand)", ["right_hand"] = "右手 (Right Hand)", ["hip"] = "腰 (Hip)",
            ["left_foot"] = "左足 (Left Foot)", ["right_foot"] = "右足 (Right Foot)", ["left_fingers"] = "左手の指 (Left Fingers)",
            ["right_fingers"] = "右手の指 (Right Fingers)", ["eyes"] = "目とまぶた (Eyes & Eyelid: 瞬き・視線)", ["mouth"] = "口とあご (Mouth & Jaw: リップシンク)",
        };

        /// <summary>
        /// Tracking Control (docs state-behaviors.md:101), Locomotion Control (:46) and Temporary Pose Space (:61) keep their
        /// setting until another state runs the behaviour again; the Action playable layer must be blended back to 0
        /// (playable-layers.md:91, state-behaviors.md:176). A change away from the default with no state restoring it in any
        /// inspected controller is reported: warning when every playable layer and MA Merge Animator of the avatar was
        /// inspected, else info (the restoring state may be elsewhere). SDK default controllers are ignored on both sides.
        /// </summary>
        static void LintPersistent(Ctx ctx)
        {
            bool complete = ctx.Mode == "avatar" && ctx.LayerArg == "all" && ctx.LayerFilter.Count == 0 && ctx.IncludeMa && !ctx.TimeTruncated;
            string scopeNote = complete ? "" : " (解析したコントローラの範囲で。layer=all でアバター全体を確認できます)";
            // SDK default controllers count on neither side: their restoring states (e.g. the default Action's) only run
            // when that layer's own animation plays, so they do not undo the user's change.
            foreach (var g in ctx.Persistent.Where(p => !p.Unit.Failed && !p.Unit.Quiet).GroupBy(p => p.Kind))
            {
                var away = g.Where(p => p.Away).ToList();
                if (away.Count == 0 || g.Any(p => !p.Away)) continue;
                var at = away.Select(p => p.At).Distinct().ToList();
                var value = new Dictionary<string, object> { ["kind"] = g.Key, ["set_at"] = at.Take(10).ToList<object>(), ["count"] = at.Count };
                var first = away[0];
                var sev = complete ? CctSeverity.Warning : CctSeverity.Info;
                if (g.Key.StartsWith("tracking:", StringComparison.Ordinal))
                {
                    var part = g.Key.Substring("tracking:".Length);
                    // Custom eye / mouth / finger systems often keep these on Animation on purpose; IK parts almost never.
                    if (part == "eyes" || part == "mouth" || part == "left_fingers" || part == "right_fingers") sev = CctSeverity.Info;
                    ctx.Issue(sev, "TRACKING_NOT_RESTORED",
                        $"Tracking Control で {(TrackingPartLabels.TryGetValue(part, out var label) ? label : part)} を Animation にするステートが {at.Count} 個ありますが、Tracking に戻すステートが見つかりません{scopeNote}。docs: この設定は別のステートが戻すまで保持されます (瞬き・リップシンク・IK が戻らない原因になります)" +
                        (sev == CctSeverity.Info && complete ? "。独自の目・口・指の制御で意図して固定しているなら問題ありません" : ""),
                        first.At, value, asset: first.Unit.Asset);
                }
                else if (g.Key == "locomotion")
                    ctx.Issue(sev, "LOCOMOTION_NOT_RESTORED",
                        $"Locomotion Control で移動を無効にするステートが {at.Count} 個ありますが、有効に戻すステートが見つかりません{scopeNote}。docs: この設定は別のステートが戻すまで保持されます (歩けなくなる原因になります)",
                        first.At, value, asset: first.Unit.Asset);
                else if (g.Key == "pose_space")
                    ctx.Issue(sev, "POSE_SPACE_NOT_RESTORED",
                        $"Temporary Pose Space で視点を移す (Enter) ステートが {at.Count} 個ありますが、戻す (Exit) ステートが見つかりません{scopeNote}。docs: 視点は別のステートが戻すまでそのままです",
                        first.At, value, asset: first.Unit.Asset);
                else if (g.Key == "action_weight")
                    ctx.Issue(sev, "ACTION_WEIGHT_NOT_LOWERED",
                        $"Playable Layer Control で Action の重みを上げるステートが {at.Count} 個ありますが、0 に戻すステートが見つかりません{scopeNote}。docs: Action は使い終わったら重みを 0 に戻すこと (エモートが止まらない・他の動きが上書きされたままになる原因)",
                        first.At, value, asset: first.Unit.Asset);
            }
        }

        // =====================================================================================================================
        // Write Defaults
        // =====================================================================================================================

        static void LintWriteDefaults(Ctx ctx)
        {
            foreach (var u in ctx.Units)
            {
                if (u.Failed) continue;
                var counted = u.LayerData.Where(l => l.Root != null && l.SyncedFrom < 0).ToList();
                u.WdOn = counted.Sum(l => l.WdOn);
                u.WdOff = counted.Sum(l => l.WdOff);
                u.UniformWd = u.WdOn > 0 && u.WdOff == 0 ? true : u.WdOff > 0 && u.WdOn == 0 ? (bool?)false : null;

                bool? avatarWd = null;
                bool avatarKnown = false;
                // A Replace controller is merged first with no WD matching (MergeAnimatorProcessor.ProcessLayerType).
                bool replacer = u.Ma != null && u.Ma.MergeMode == 1;
                if (u.Ma != null && !replacer) avatarWd = AvatarWdFor(ctx, u.LayerType, out avatarKnown);

                int nonRequiredOn = 0, nonRequiredOff = 0;
                bool anyNormalized = false;
                foreach (var ld in counted)
                {
                    bool forced = u.Ma != null && IsWdRequiredLayer(ld);
                    bool normalized = u.Ma != null && !forced && u.Ma.MatchWd && avatarWd.HasValue;
                    if (u.Ma != null)
                    {
                        ld.MaWd = forced ? "forced_on"
                            : normalized ? "match_avatar:" + (avatarWd.Value ? "on" : "off")
                            : replacer ? "unchanged (replace)"
                            : u.Ma.MatchWd ? (avatarKnown ? "match_avatar:avatar_mixed_unchanged" : "match_avatar:avatar_not_inspected")
                            : "unchanged";
                        if (!forced)
                            foreach (var sd in ld.States)
                                if (sd.Wd) nonRequiredOn++; else nonRequiredOff++;
                        anyNormalized |= normalized;
                    }
                    if (!ld.Included) continue;
                    var where = Loc(u, ld, null);
                    if (ld.WdOn > 0 && ld.WdOff > 0)
                        ctx.Issue(normalized ? CctSeverity.Info : CctSeverity.Warning, "WD_MIXED",
                            $"レイヤー '{ld.Name}' で Write Defaults が混在しています (ON {ld.WdOn} / OFF {ld.WdOff}、Additive レイヤーと Direct BlendTree のステートは除外)" +
                            (normalized ? $"。MA がビルド時にアバターに合わせて {(avatarWd.Value ? "ON" : "OFF")} に揃えます" : ""),
                            where, new Dictionary<string, object> { ["on"] = ld.WdOn, ["off"] = ld.WdOff }, asset: u.Asset, quiet: u.Quiet);
                    if (forced && ld.States.Any(s => !s.Wd))
                        ctx.Issue(CctSeverity.Info, "MA_WD_FORCED_ON",
                            $"レイヤー '{ld.Name}' は {(ld.Additive ? "Additive ブレンド" : "単一ステートの Direct BlendTree")} のため、MA がビルド時に全ステートを WD ON にします",
                            where, asset: u.Asset);
                    foreach (var sd in ld.States)
                    {
                        if (sd.Wd) continue;
                        bool willBeOn = forced || (normalized && avatarWd == true);
                        var sev = willBeOn ? CctSeverity.Info : CctSeverity.Warning;
                        var tail = willBeOn ? " (MA がビルド時に WD ON に変更します)" : "";
                        var swhere = Loc(u, ld, sd.Path);
                        if (sd.Empty && !sd.MissingMotion)
                            ctx.Issue(sev, "EMPTY_STATE_WD_OFF",
                                "WD OFF のステートの Motion が無い / 空のクリップ / BlendTree の子が空です。docs: この状態は WD ON のように振る舞い、レイヤーが動かした値が既定値に戻ります (SDK のビルドパネルでも警告)。何か 1 つでもプロパティを持つクリップを入れてください" + tail,
                                swhere, sd.Motion != null ? sd.Motion.name : "(none)", asset: u.Asset, quiet: u.Quiet);
                        if (sd.ContainsDirect)
                            ctx.Issue(sev, "DIRECT_BLENDTREE_WD_OFF",
                                "WD OFF のステートが Direct BlendTree を使っています。docs: Direct BlendTree は WD ON が推奨 (コミュニティ推奨、SDK は混在警告から除外)" + tail,
                                swhere, sd.Motion != null ? sd.Motion.name : null, asset: u.Asset, quiet: u.Quiet);
                    }
                    if (ld.Additive && ld.States.Any(s => !s.Wd))
                        ctx.Issue(forced ? CctSeverity.Info : CctSeverity.Warning, "ADDITIVE_LAYER_WD_OFF",
                            $"Additive ブレンドのレイヤー '{ld.Name}' に WD OFF のステートがあります。docs: Additive レイヤーは WD ON が推奨 (コミュニティ推奨)" +
                            (forced ? " (MA がビルド時に ON にします)" : ""),
                            where, ld.States.Count(s => !s.Wd), asset: u.Asset, quiet: u.Quiet);
                }

                if (u.WdOn > 0 && u.WdOff > 0)
                {
                    var onLayers = counted.Where(l => l.WdOn > 0).Select(l => l.Name).Take(8).ToList<object>();
                    var offLayers = counted.Where(l => l.WdOff > 0).Select(l => l.Name).Take(8).ToList<object>();
                    ctx.Issue(anyNormalized ? CctSeverity.Info : CctSeverity.Warning, "WD_MIXED",
                        $"コントローラ '{u.Label}' 全体で Write Defaults が混在しています (ON {u.WdOn} / OFF {u.WdOff}。SDK と同じ判定: Additive レイヤーと Direct BlendTree のステートを除外)。docs: アバター全体で統一を推奨" +
                        (anyNormalized ? "。MA がビルド時に一部を揃えます (ma_wd 参照)" : ""),
                        u.Id, new Dictionary<string, object> { ["on"] = u.WdOn, ["off"] = u.WdOff, ["layers_with_on"] = onLayers, ["layers_with_off"] = offLayers },
                        asset: u.Asset, quiet: u.Quiet);
                }

                if (u.Ma != null)
                {
                    bool? own = nonRequiredOn > 0 && nonRequiredOff == 0 ? true : nonRequiredOff > 0 && nonRequiredOn == 0 ? (bool?)false : null;
                    if (!u.Ma.MatchWd && avatarWd.HasValue && own.HasValue && own.Value != avatarWd.Value)
                        ctx.Issue(CctSeverity.Warning, "MA_WD_NOT_MATCHED",
                            $"Match Avatar Write Defaults が OFF で、このコントローラは WD {(own.Value ? "ON" : "OFF")}、アバターの {LayerTypeName(u.LayerType)} は WD {(avatarWd.Value ? "ON" : "OFF")} です。統合後に WD が混在します (Match Avatar Write Defaults を ON にするか WD を揃える)",
                            CctTargets.GetPath(u.Ma.Comp.transform), asset: u.Asset);
                    else if (u.Ma.MatchWd && avatarKnown && !avatarWd.HasValue)
                        ctx.Issue(CctSeverity.Info, "MA_WD_NOT_MATCHED",
                            $"Match Avatar Write Defaults は ON ですが、アバターの {LayerTypeName(u.LayerType)} 側の WD が一様でない (またはステートが無い) ため、MA は WD を変更しません",
                            CctTargets.GetPath(u.Ma.Comp.transform), asset: u.Asset);
                }
            }

            // MA controllers with Match Avatar Write Defaults are adjusted to the avatar at build, so they do not count here.
            var uniform = ctx.Units.Where(x => !x.Failed && !x.Quiet && x.LayerType >= 0 && x.UniformWd.HasValue && !(x.Ma != null && x.Ma.MatchWd)).ToList();
            if (uniform.Select(x => x.UniformWd.Value).Distinct().Count() > 1)
                ctx.Issue(CctSeverity.Info, "WD_MIXED_ACROSS_CONTROLLERS",
                    "コントローラごとには WD が揃っていますが、コントローラ間で ON / OFF が異なります。docs: アバター全体で統一を推奨 (SDK の警告はコントローラ単位なので出ません)",
                    value: uniform.ToDictionary(x => x.Id, x => (object)(x.UniformWd.Value ? "on" : "off")));
        }

        /// <summary>
        /// The avatar-side WD MA matches against (MergeAnimatorProcessor.AnalyzeLayerWriteDefaults): every state of the
        /// target controller's layers that are not WD-required; uniform -> that value, else null (MA leaves WD alone).
        /// </summary>
        static bool? AvatarWdFor(Ctx ctx, int layerType, out bool known)
        {
            var target = ctx.Units.FirstOrDefault(x => x.Ma != null && x.Ma.MergeMode == 1 && !x.Ma.EditorOnly && x.LayerType == layerType)
                         ?? ctx.Units.FirstOrDefault(x => x.Ma == null && x.LayerType == layerType && (x.Origin == "descriptor" || x.Origin == "sdk_default"));
            known = target != null && !target.Failed && target.Controller != null;
            if (!known) return null;
            int on = 0, off = 0;
            foreach (var ld in target.LayerData)
            {
                if (ld.Root == null || ld.SyncedFrom >= 0 || IsWdRequiredLayer(ld)) continue;
                foreach (var sd in ld.States)
                    if (sd.Wd) on++; else off++;
            }
            if (on > 0 && off == 0) return true;
            if (off > 0 && on == 0) return false;
            return null;
        }

        /// <summary>MA MergeAnimatorProcessor.IsWriteDefaultsRequiredLayer: additive, or a lone default state (no sub machines,
        /// no AnyState transitions, no transitions) whose motion is a blend tree containing a Direct blend tree.</summary>
        static bool IsWdRequiredLayer(LayerData ld)
        {
            if (ld.Additive) return true;
            var root = ld.Root;
            if (root == null || ld.Machines.Count != 1 || ld.States.Count != 1) return false;
            if ((root.anyStateTransitions?.Length ?? 0) != 0) return false;
            var def = root.defaultState;
            if (def == null || (def.transitions?.Length ?? 0) != 0) return false;
            return def.motion is BlendTree bt && ContainsDirectBlendTree(bt, 0);
        }

        static bool ContainsDirectBlendTree(BlendTree bt, int depth)
        {
            if (bt == null || depth > MaxBlendTreeDepth) return false;
            if (bt.blendType == BlendTreeType.Direct) return true;
            foreach (var c in bt.children ?? new ChildMotion[0])
                if (c.motion is BlendTree child && ContainsDirectBlendTree(child, depth + 1)) return true;
            return false;
        }

        // =====================================================================================================================
        // Reachability
        // =====================================================================================================================

        static IEnumerable<T> Effective<T>(T[] transitions) where T : AnimatorTransitionBase
        {
            if (transitions == null) yield break;
            bool anySolo = transitions.Any(t => t != null && t.solo && !t.mute);
            foreach (var t in transitions)
            {
                if (t == null || t.mute || (anySolo && !t.solo)) continue;
                if (t is AnimatorStateTransition st && (st.conditions?.Length ?? 0) == 0 && !st.hasExitTime) continue; // ignored by Unity
                yield return t;
            }
        }

        static void LintReachability(Ctx ctx)
        {
            foreach (var u in ctx.Units)
            {
                if (u.Failed) continue;
                foreach (var ld in u.LayerData)
                {
                    if (!ld.Included || ld.Root == null || ld.SyncedFrom >= 0 || ld.States.Count == 0) continue;
                    var visited = new HashSet<AnimatorState>();
                    var queue = new Queue<AnimatorState>();
                    var entered = new HashSet<AnimatorStateMachine>();

                    void Visit(AnimatorState s)
                    {
                        if (s != null && visited.Add(s)) queue.Enqueue(s);
                    }
                    void Enter(MachineData md)
                    {
                        if (md == null || !entered.Add(md.Machine)) return;
                        Visit(md.Machine.defaultState);
                        foreach (var t in Effective(md.Entry)) Follow(t, md);
                    }
                    void Exit(MachineData md)
                    {
                        if (md == null) return;
                        if (md.Parent == null) { Enter(md); return; }
                        var exits = Effective(md.Exit).ToList();
                        if (exits.Count == 0) Enter(md.Parent);
                        foreach (var t in exits) Follow(t, md.Parent);
                    }
                    void Follow(AnimatorTransitionBase t, MachineData owner)
                    {
                        if (t.destinationState != null) Visit(t.destinationState);
                        else if (t.destinationStateMachine != null)
                        {
                            if (ld.MachineByObj.TryGetValue(t.destinationStateMachine, out var md)) Enter(md);
                        }
                        else if (t.isExit) Exit(owner);
                    }

                    if (ld.MachineByObj.TryGetValue(ld.Root, out var rootMd)) Enter(rootMd);
                    foreach (var md in ld.Machines)
                        foreach (var t in Effective(md.AnyState)) Follow(t, md);
                    while (queue.Count > 0)
                    {
                        var s = queue.Dequeue();
                        if (!ld.StateByObj.TryGetValue(s, out var sd)) continue;
                        foreach (var t in Effective(sd.Transitions)) Follow(t, sd.Owner);
                    }
                    foreach (var sd in ld.States)
                        if (!visited.Contains(sd.State))
                            ctx.Issue(CctSeverity.Info, "UNREACHABLE_STATE",
                                "到達できないステートです (デフォルトでなく、Entry / AnyState / 他ステートからの有効な遷移がありません。Mute・条件も Exit Time も無い遷移は除外)",
                                Loc(u, ld, sd.Path), asset: u.Asset, quiet: u.Quiet);
                }
            }
        }

        // =====================================================================================================================
        // Layer weights / Action layer
        // =====================================================================================================================

        static void LintLayerWeights(Ctx ctx)
        {
            foreach (var u in ctx.Units)
            {
                if (u.Failed) continue;
                foreach (var ld in u.LayerData)
                {
                    if (!ld.Included || ld.Index == 0) continue;
                    var controls = ctx.LayerControls.Where(c => c.Layer == ld.Index && ControlTargetsUnit(c, u)).ToList();
                    foreach (var c in controls.Take(8))
                        ld.ControlledBy.Add($"{c.At} goal={Round(c.Goal).ToString(CultureInfo.InvariantCulture)} duration={Round(c.Duration).ToString(CultureInfo.InvariantCulture)}");
                    if (ld.Layer.defaultWeight <= 0f && !controls.Any(c => c.Goal > 0f))
                        ctx.Issue(CctSeverity.Info, "LAYER_WEIGHT_ZERO",
                            $"レイヤー '{ld.Name}' の Weight が 0 で、VRCAnimatorLayerControl で上げるステートも見つかりません。このレイヤーのアニメーションは反映されません (遷移と Parameter Driver などの Behaviour は重み 0 でも動きます: Unity の仕様)。アニメーションさせるなら Weight を 1 にしてください" +
                            (ctx.LayerFilter.Count > 0 ? " (layers で絞り込んでいるため、他のレイヤーの Layer Control は見ていません)" : ""),
                            Loc(u, ld, null), 0, asset: u.Asset, quiet: u.Quiet);
                }
            }
        }

        /// <summary>
        /// A layer control in the same controller targets it when its playable matches the controller's layer type (or the type
        /// is unknown); controls in other descriptor controllers count when the playable matches (indices are only stable for
        /// descriptor controllers; MA controllers are re-indexed when merged).
        /// </summary>
        static bool ControlTargetsUnit(LayerControlRef c, Unit u)
        {
            var type = BlendableToLayerType(c.Playable);
            if (c.Unit == u) return u.LayerType < 0 || type == u.LayerType;
            return u.Ma == null && c.Unit.Ma == null && u.LayerType >= 0 && type == u.LayerType;
        }

        static void LintAction(Ctx ctx)
        {
            if (ctx.LayerArg != "all" || ctx.LayerFilter.Count > 0) return;
            var action = ctx.Units.FirstOrDefault(x => x.Origin == "descriptor" && x.LayerType == LtAction && !x.Failed);
            if (action == null || action.States == 0) return;
            if (!ctx.PlayableControls.Any(p => p.Layer == 0 && p.Goal > 0f))
                ctx.Issue(CctSeverity.Info, "ACTION_WEIGHT_NEVER_RAISED",
                    "Action レイヤーにステートがありますが、Action の重みを上げる VRCPlayableLayerControl が見つかりません。docs: Action は既定で重み 0 なので、Playable Layer Control で上げてから動作させ、終わったら 0 に戻す必要があります",
                    action.Id, asset: action.Asset);
        }

        // =====================================================================================================================
        // Parameter uses (undefined / wrong type)
        // =====================================================================================================================

        static void LintParameterUses(Ctx ctx)
        {
            // Parameters visible after MA merges controllers into the same playable layer type.
            var merged = new Dictionary<int, Dictionary<string, AnimatorControllerParameterType>>();
            foreach (var u in ctx.Units)
            {
                if (u.Failed || u.LayerType < 0) continue;
                if (!merged.TryGetValue(u.LayerType, out var m)) merged[u.LayerType] = m = new Dictionary<string, AnimatorControllerParameterType>(StringComparer.Ordinal);
                foreach (var kv in u.Params)
                    if (!m.ContainsKey(kv.Key)) m[kv.Key] = kv.Value;
            }

            foreach (var g in ctx.Uses.GroupBy(x => new { x.Unit, x.Param }))
            {
                var u = g.Key.Unit;
                var name = g.Key.Param;
                var first = g.First();
                var kinds = g.Select(x => x.Kind).Distinct().ToList();
                if (string.IsNullOrEmpty(name))
                {
                    if (kinds.Any(k => k == "condition" || k == "driver_dest" || k == "driver_source"))
                        ctx.Issue(CctSeverity.Warning, "PARAM_UNDEFINED", $"パラメータ名が空の {string.Join("/", kinds)} が {g.Count()} 箇所あります", first.At,
                            asset: u.Asset, quiet: u.Quiet);
                    continue;
                }
                var value = new Dictionary<string, object> { ["param"] = name, ["uses"] = g.Count(), ["kinds"] = kinds.ToList<object>() };
                // Animator Play Audio reads an Expression Parameter (docs state-behaviors.md:203; checked in ReadPlayAudio), so
                // its use alone is not an animator declaration problem.
                bool inExprParams = ctx.Expression != null && ctx.Expression.ContainsKey(name);
                if (kinds.All(k => k == "play_audio") && (inExprParams || ctx.AvatarRoot != null)) continue;
                if (!u.Params.TryGetValue(name, out var type))
                {
                    bool inMerged = u.LayerType >= 0 && merged.TryGetValue(u.LayerType, out var m) && m.TryGetValue(name, out type);
                    if (inMerged)
                    {
                        ctx.Issue(CctSeverity.Info, "PARAM_UNDEFINED",
                            $"'{name}' はこのコントローラに無く、同じ {LayerTypeName(u.LayerType)} に統合される別のコントローラで定義されています (MA の統合後には存在します)",
                            first.At, value, asset: u.Asset, quiet: u.Quiet);
                    }
                    else
                    {
                        bool onlyDriverDest = kinds.All(k => k == "driver_dest");
                        bool inExpr = inExprParams;
                        // Controllers of other playable layers that declare it (each controller needs its own declaration
                        // to read the value; whether a driver's write reaches another layer is not documented).
                        var elsewhere = ctx.Units.Where(x => x != u && !x.Failed && x.LayerType != u.LayerType && x.Params.ContainsKey(name))
                            .Select(x => x.Id).Distinct().Take(4).ToList();
                        if (elsewhere.Count > 0) value["declared_in_other_layers"] = elsewhere.ToList<object>();
                        var elsewhereText = elsewhere.Count > 0 ? $"。別のプレイアブルレイヤーのコントローラ ({string.Join(", ", elsewhere)}) では宣言されています" : "";
                        if (onlyDriverDest && inExpr)
                            ctx.Issue(CctSeverity.Info, "PARAM_UNDEFINED",
                                $"Parameter Driver の宛先 '{name}' はこのコントローラに無く Expression Parameters にだけあります (値は設定されますが、このコントローラでは読めません)",
                                first.At, value, asset: u.Asset, quiet: u.Quiet);
                        else if (onlyDriverDest)
                            ctx.Issue(elsewhere.Count > 0 ? CctSeverity.Info : CctSeverity.Warning, "PARAM_UNDEFINED",
                                $"Parameter Driver の宛先 '{name}' がコントローラ '{u.Label}' にも Expression Parameters にもありません ({g.Count()} 箇所)。このコントローラには書き込んだ値を読む宣言がありません (名前の綴りを確認してください)" + elsewhereText,
                                first.At, value, asset: u.Asset, quiet: u.Quiet);
                        else if (kinds.All(k => k == "play_audio"))
                            ctx.Issue(CctSeverity.Info, "PARAM_UNDEFINED",
                                $"Animator Play Audio の Parameter '{name}' はコントローラ '{u.Label}' にありません。docs: このパラメータは Expression Parameters に int で定義が必要です (アバターを target に指定すると確認できます)",
                                first.At, value, asset: u.Asset, quiet: u.Quiet);
                        else
                            ctx.Issue(CctSeverity.Warning, "PARAM_UNDEFINED",
                                $"'{name}' がコントローラ '{u.Label}' のパラメータにありません ({string.Join(", ", kinds)}、{g.Count()} 箇所)。Unity はこの値を読めず条件・ブレンドが働きません" +
                                (BuiltinApplies(ctx, u) && BuiltinParams.ContainsKey(name) ? "。VRChat 組み込みパラメータもコントローラ側に同名のパラメータ宣言が必要です" : "") +
                                (inExpr ? "。Expression Parameters には定義があります" : "") + elsewhereText,
                                first.At, value, asset: u.Asset, quiet: u.Quiet);
                        continue;
                    }
                }

                // Play Audio's type is the Expression Parameter's (checked in ReadPlayAudio), not the animator declaration's.
                foreach (var pg in g.Where(x => !(x.Kind == "play_audio" && inExprParams)).GroupBy(x => UseTypeProblem(x, type)).Where(pg => pg.Key != null))
                {
                    var f = pg.First();
                    // With MA installed, NDMF's HarmonizeParameterTypes (run by MA on every avatar build) rewrites conditions that
                    // do not fit the type (If -> Greater 0.5, Greater on Bool -> If, Equals on Float -> a +-0.1 window, ...;
                    // Trigger parameters excepted) and, from NDMF 1.14.5, makes blend tree parameters Float. Speed / time /
                    // cycle / mirror parameters are not harmonized, and Equals / NotEqual on a Bool becomes Greater / Less on
                    // the same Bool (still invalid).
                    bool boolEquality = type == AnimatorControllerParameterType.Bool && f.HasMode &&
                                        (f.Mode == AnimatorConditionMode.Equals || f.Mode == AnimatorConditionMode.NotEqual);
                    bool harmonized = ctx.MaInstalled &&
                                      (f.Kind == "condition" && type != AnimatorControllerParameterType.Trigger && !boolEquality ||
                                       (f.Kind == "blend_tree" || f.Kind == "blend_tree_direct") && ctx.NdmfBlendTreeFloat);
                    ctx.Issue(harmonized ? CctSeverity.Info : CctSeverity.Warning, "PARAM_TYPE_MISMATCH",
                        $"'{name}' ({type}) の使い方が型と合いません: {pg.Key} ({pg.Count()} 箇所)。" +
                        (harmonized
                            ? "Modular Avatar 導入済みのため、ビルド時に NDMF のパラメータ型統一で Float に揃えられ条件も書き換えられます (推定)。MA を使わない構成でも動くようにするならコントローラ側を直してください"
                            : "Unity の Inspector では選べない組み合わせで、実行時の評価は保証されません"),
                        f.At, new Dictionary<string, object> { ["source"] = "usage", ["param"] = name, ["type"] = type.ToString(), ["uses"] = pg.Count(), ["first"] = f.Detail ?? f.Kind, ["harmonized_by_ma"] = harmonized },
                        asset: u.Asset, quiet: u.Quiet);
                }
            }
        }

        /// <summary>Null when the use fits the parameter type (Unity's Inspector choices), else a short Japanese reason.</summary>
        static string UseTypeProblem(ParamUse use, AnimatorControllerParameterType type)
        {
            switch (use.Kind)
            {
                case "condition":
                    if (!use.HasMode) return null;
                    var mode = use.Mode;
                    switch (type)
                    {
                        case AnimatorControllerParameterType.Float:
                            return mode == AnimatorConditionMode.Greater || mode == AnimatorConditionMode.Less ? null : $"Float に {mode} (Greater / Less のみ)";
                        case AnimatorControllerParameterType.Int:
                            return mode == AnimatorConditionMode.Greater || mode == AnimatorConditionMode.Less ||
                                   mode == AnimatorConditionMode.Equals || mode == AnimatorConditionMode.NotEqual ? null : $"Int に {mode} (Greater / Less / Equals / NotEqual のみ)";
                        case AnimatorControllerParameterType.Bool:
                            return mode == AnimatorConditionMode.If || mode == AnimatorConditionMode.IfNot ? null : $"Bool に {mode} (If / IfNot のみ)";
                        case AnimatorControllerParameterType.Trigger:
                            return mode == AnimatorConditionMode.If ? null : $"Trigger に {mode} (If のみ)";
                    }
                    return null;
                case "blend_tree":
                case "blend_tree_direct":
                case "speed":
                case "motion_time":
                case "cycle_offset":
                    return type == AnimatorControllerParameterType.Float ? null : $"{use.Kind} のパラメータが {type} (Float のみ)";
                case "mirror":
                    return type == AnimatorControllerParameterType.Bool ? null : $"mirror のパラメータが {type} (Bool のみ)";
                case "play_audio":
                    return type == AnimatorControllerParameterType.Int ? null : $"Animator Play Audio の Parameter が {type} (docs: int 型)";
            }
            return null;
        }

        // =====================================================================================================================
        // Parameter types vs Expression Parameters / built-ins (docs conversion table)
        // =====================================================================================================================

        /// <summary>docs avatars/animator-parameters/index.md:205-220 "Mismatched Parameter Type Conversion".</summary>
        static bool Conversion(string source, string animator, out string rule)
        {
            switch (source + "->" + animator)
            {
                case "Int->Float": rule = "int → float はそのまま変換 (1 → 1.0)"; return false;
                case "Int->Bool": rule = "int → bool は 0 が false、それ以外は true"; return true;
                case "Float->Int": rule = "float → int は最も近い整数に丸め (Mathf.Round: 0.5→0, 0.6→1, 1.5→2)"; return true;
                case "Float->Bool": rule = "float → bool は 0.0 が false、それ以外は true"; return true;
                case "Bool->Int": rule = "bool → int は true が 1、false が 0"; return false;
                case "Bool->Float": rule = "bool → float は true が 1.0、false が 0.0"; return false;
            }
            rule = $"{source} → {animator} は docs の変換表にありません";
            return true;
        }

        static void LintParameterTypes(Ctx ctx)
        {
            var seen = new HashSet<string>();
            foreach (var u in ctx.Units)
            {
                if (u.Failed) continue;
                var triggers = new List<object>();
                var lossless = new List<object>(); // intended conversions (bool -> float for blend trees, ...): one info per controller
                foreach (var p in u.ParamList)
                {
                    var animType = p.type.ToString();
                    if (p.type == AnimatorControllerParameterType.Trigger) triggers.Add(p.name);
                    if (ctx.Expression != null && ctx.Expression.TryGetValue(p.name, out var ep))
                    {
                        var src = ExprTypeName(ep.ValueType);
                        if (src != animType && seen.Add("e|" + p.name + "|" + animType))
                        {
                            bool lossy = Conversion(src, animType, out var rule);
                            if (!lossy) lossless.Add($"{p.name} ({src}→{animType})");
                            else
                                ctx.Issue(CctSeverity.Warning, "PARAM_TYPE_MISMATCH",
                                    $"'{p.name}' は Expression Parameters では {src}、コントローラ '{u.Label}' では {animType} です。VRChat が変換します: {rule} (docs animator-parameters「Mismatched Parameter Type Conversion」)。値が丸められるので意図した変換か確認してください",
                                    u.Id, new Dictionary<string, object> { ["source"] = "expression", ["param"] = p.name, ["expression"] = src, ["animator"] = animType }, asset: u.Asset, quiet: u.Quiet);
                        }
                    }
                    if (BuiltinApplies(ctx, u) && BuiltinParams.TryGetValue(p.name, out var bt) && bt != animType && seen.Add("b|" + p.name + "|" + animType))
                    {
                        bool lossy = Conversion(bt, animType, out var rule);
                        if (!lossy) lossless.Add($"{p.name} (組み込み {bt}→{animType})");
                        else
                            ctx.Issue(CctSeverity.Warning, "PARAM_TYPE_MISMATCH",
                                $"組み込みパラメータ '{p.name}' ({bt}) をコントローラ '{u.Label}' で {animType} として宣言しています。VRChat が変換します: {rule} (docs animator-parameters)。値が丸められるので意図した変換か確認してください",
                                u.Id, new Dictionary<string, object> { ["source"] = "builtin", ["param"] = p.name, ["builtin"] = bt, ["animator"] = animType }, asset: u.Asset, quiet: u.Quiet);
                    }
                }
                if (lossless.Count > 0)
                    ctx.Issue(CctSeverity.Info, "PARAM_TYPE_MISMATCH",
                        $"コントローラ '{u.Label}' の {lossless.Count} 個のパラメータは Expression Parameters / 組み込みと型が違いますが、VRChat が値を失わずに変換します (bool→int/float は 1/0、int→float はそのまま。docs animator-parameters「Mismatched Parameter Type Conversion」): {string.Join(", ", lossless.Take(8))}{(lossless.Count > 8 ? " ..." : "")}",
                        u.Id, new Dictionary<string, object> { ["source"] = "conversion_lossless", ["params"] = lossless.Take(60).ToList() }, asset: u.Asset, quiet: u.Quiet);
                if (triggers.Count > 0)
                    ctx.Issue(CctSeverity.Info, "PARAM_TRIGGER",
                        $"Trigger 型のパラメータが {triggers.Count} 個あります。docs: Trigger はリモートやミラーで同期がずれることがあり非推奨 (Int / Float / Bool を推奨)",
                        u.Id, triggers.Take(20).ToList(), asset: u.Asset, quiet: u.Quiet);
            }

            if (ctx.Expression == null || ctx.LayerArg != "all" || ctx.Mode != "avatar" || ctx.LayerFilter.Count > 0) return;
            var unused = ctx.Expression.Keys.Where(n => !ctx.Units.Any(x => !x.Failed && x.Params.ContainsKey(n))).ToList();
            if (unused.Count > 0)
                ctx.Issue(CctSeverity.Info, "EXPRESSION_PARAM_NOT_IN_ANIMATOR",
                    $"Expression Parameters の {unused.Count} 個がどの解析済みコントローラにも宣言されていません (メニューで操作しても何も動かない可能性。MA Parameters 等がビルド時に追加する場合を除く)",
                    value: unused.Take(30).ToList<object>(), asset: ctx.ExpressionAsset);
        }

        static void LintMaParameterTypes(Ctx ctx)
        {
            foreach (var group in ctx.Units.Where(x => !x.Failed && x.LayerType >= 0).GroupBy(x => x.LayerType))
            {
                var units = group.ToList();
                if (units.Count < 2 || !units.Any(x => x.Ma != null)) continue;
                var byName = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
                var typesByName = new Dictionary<string, HashSet<AnimatorControllerParameterType>>(StringComparer.Ordinal);
                foreach (var u in units)
                    foreach (var kv in u.Params)
                    {
                        if (!typesByName.TryGetValue(kv.Key, out var set))
                        {
                            typesByName[kv.Key] = set = new HashSet<AnimatorControllerParameterType>();
                            byName[kv.Key] = new Dictionary<string, object>();
                        }
                        set.Add(kv.Value);
                        byName[kv.Key][u.Id] = kv.Value.ToString();
                    }
                foreach (var kv in typesByName)
                {
                    if (kv.Value.Count < 2) continue;
                    // MA MergeAnimatorProcessor.MergeSingle forces the parameter to Float; NDMF HarmonizeParameterTypes (run by MA)
                    // then rewrites incompatible conditions and VirtualAnimatorController adjusts parameter drivers
                    // (ndmf CHANGELOG 1.9.0 #693, MA 1.14.0 #1726). A Trigger that becomes Float no longer resets itself.
                    bool trigger = kv.Value.Contains(AnimatorControllerParameterType.Trigger);
                    ctx.Issue(trigger ? CctSeverity.Warning : CctSeverity.Info, "MA_PARAM_TYPE_COERCED",
                        $"'{kv.Key}' の型が {LayerTypeName(group.Key)} に統合されるコントローラ間で異なります ({string.Join(", ", byName[kv.Key].Select(x => x.Key + ": " + x.Value))})。MA はビルド時にこのパラメータを Float に統一し、If / IfNot / Equals などの条件と Parameter Driver を自動で書き換えます (推定)" +
                        (trigger ? "。Trigger は Float になると自動で false に戻らなくなるので、Trigger をやめて Bool / Int にしてください" : "。意図した型の不一致か確認してください (Expression Parameters との変換は docs の変換表どおり)"),
                        LayerTypeName(group.Key), byName[kv.Key]);
                }
            }
        }
    }
}
