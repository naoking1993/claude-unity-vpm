using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Naoking.ClaudeCreatorTools
{
    // Merges the SDK calculator result with the estimator into data.method / data.stats / data.ranks, raises the rank related
    // issues, and builds data.notes and the report-only rank tables.
    public static partial class CctAvatarAudit
    {
        sealed class StatsOutput
        {
            public bool UsedSdk;
            public readonly Dictionary<string, object> Method = new Dictionary<string, object>();
            public readonly Dictionary<string, object> Ranks = new Dictionary<string, object>();
            public readonly Dictionary<string, object> Stats = new Dictionary<string, object>();
            /// <summary>Final value per stat key (SDK value, or the estimate).</summary>
            public readonly Dictionary<string, object> Values = new Dictionary<string, object>(StringComparer.Ordinal);
            /// <summary>Output platform key ("pc" / "android" / "ios") -> stat key -> rating.</summary>
            public readonly Dictionary<string, Dictionary<string, int>> Ratings = new Dictionary<string, Dictionary<string, int>>();
            public readonly Dictionary<string, int> Overall = new Dictionary<string, int>();
            public readonly Dictionary<string, List<string>> LimitingKeys = new Dictionary<string, List<string>>();
            public readonly HashSet<string> EstimatedKeys = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<string> RankMismatches = new List<string>();
            public readonly List<string> ValueMismatches = new List<string>();
            public Dictionary<string, object> MissingScripts;
            public int? ContactsAll;
        }

        static StatsOutput BuildStatsOutput(Ctx ctx, Est est, SdkResult sdk, bool raiseIssues)
        {
            var o = new StatsOutput();
            bool sdkOk = sdk != null && sdk.Ok;
            o.UsedSdk = sdkOk;
            o.ContactsAll = sdkOk && sdk.ContactCompleteCount.HasValue ? sdk.ContactCompleteCount : est.ContactsAll;

            foreach (var p in ctx.Platforms()) o.Ratings[p.Key] = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var def in Ranks.All)
            {
                object value;
                bool estimate;
                est.Values.TryGetValue(def.Key, out var estValue);
                if (sdkOk && sdk.Values.TryGetValue(def.Key, out var sdkValue))
                {
                    value = sdkValue;
                    estimate = false;
                    if (!SameValue(def, sdkValue, estValue))
                        o.ValueMismatches.Add($"{def.Key}: SDK {FormatValue(def, sdkValue)} / 内訳の合計 {FormatValue(def, estValue)}");
                }
                else
                {
                    value = estValue;
                    estimate = true;
                }
                o.Values[def.Key] = value;
                if (estimate) o.EstimatedKeys.Add(def.Key);

                var entry = new Dictionary<string, object> { ["value"] = DisplayValue(def, value) };
                if (def.Kind != Ranks.Kind.Count) entry["unit"] = def.Unit;
                foreach (var p in ctx.Platforms())
                {
                    int own = Ranks.Rate(def, p.Value, value);
                    int rating = own;
                    if (!estimate && sdk.Ratings.TryGetValue(p.Value ? "mobile" : "pc", out var sdkRatings) &&
                        sdkRatings.TryGetValue(def.Key, out var sdkRating) && sdkRating != Ranks.None)
                    {
                        rating = sdkRating;
                        if (own != Ranks.None && own != sdkRating)
                            o.RankMismatches.Add($"{ctx.PlatformLabel(p.Key)} {def.Key}: SDK {Ranks.Name(sdkRating)} / 表 {Ranks.Name(own)}");
                    }
                    o.Ratings[p.Key][def.Key] = rating;
                    entry[p.Key] = Ranks.Name(rating);
                }
                if (estimate)
                {
                    entry["estimate"] = true;
                    if (sdkOk) entry["note"] = "SDK が値を返さなかったため自前の推定値です";
                }
                AddStatNotes(ctx, est, sdk, def, entry);
                o.Stats[def.Key] = entry;
            }

            foreach (var p in ctx.Platforms())
            {
                var ratings = o.Ratings[p.Key];
                int overall = Ranks.Overall(ratings.Values);
                // The SDK's own Overall covers every category of the installed SDK. A newer SDK may rate a category this tool does
                // not list; then its Overall is worse than ours and wins, so the result still matches the SDK Control Panel.
                string sdkOverallNote = null;
                if (sdkOk && sdk.Overall.TryGetValue(p.Value ? "mobile" : "pc", out var sdkOverall) && sdkOverall > overall && sdkOverall <= Ranks.VeryPoor)
                {
                    sdkOverallNote = $"SDK の総合ランク {Ranks.Name(sdkOverall)} を採用しました (この表に無い SDK の項目が原因です。SDK Control Panel で確認してください)";
                    o.RankMismatches.Add($"{ctx.PlatformLabel(p.Key)} overall: SDK {Ranks.Name(sdkOverall)} / 表 {Ranks.Name(overall)}");
                    overall = sdkOverall;
                }
                o.Overall[p.Key] = overall;
                var limiting = Ranks.Limiting(ratings, overall);
                o.LimitingKeys[p.Key] = limiting;
                var limitingRows = new List<object>();
                foreach (var key in limiting)
                {
                    var def = Ranks.Find(key);
                    var row = new Dictionary<string, object>
                    {
                        ["stat"] = key,
                        ["value"] = DisplayValue(def, o.Values[key]),
                        ["rank"] = Ranks.Name(overall),
                        // docs "Minimum Displayed Performance Rank": avatar_fallback | <component>_removed | none (bounds)
                        ["min_displayed_rank_effect"] = Ranks.MinRankEffect(key),
                    };
                    if (Ranks.NextImprovement(def, p.Value, overall, out var limit, out var atLimit))
                    {
                        row["limit_for_better_rank"] = def.Kind == Ranks.Kind.Megabytes ? (object)limit : (long)limit;
                        row["rank_at_limit"] = Ranks.Name(atLimit);
                    }
                    else if (def.Kind == Ranks.Kind.Bounds)
                    {
                        var size = Ranks.BoundsSizeLimit(def, p.Value, Math.Max(Ranks.Excellent, overall - 1));
                        if (size != null) row["limit_for_better_rank"] = size.Select(x => (object)x).ToList();
                    }
                    else if (def.Kind == Ranks.Kind.Flag) row["limit_for_better_rank"] = false;
                    limitingRows.Add(row);
                }
                var rank = new Dictionary<string, object>
                {
                    ["overall"] = Ranks.Name(overall),
                    ["limiting_stats"] = limitingRows,
                    // true for the platform of the active build target (the one the SDK Control Panel would rate and upload).
                    ["active_build_target"] = p.Value == ctx.ActiveMobile,
                };
                bool humanoid = ctx.RootAnimator != null && SafeIsHuman(ctx.RootAnimator);
                if (ctx.Descriptor != null) rank["fallback_eligible"] = humanoid && overall != Ranks.None && overall <= Ranks.Good;
                if (p.Value) rank["note"] = MobilePlatformNote;
                if (sdkOverallNote != null) rank["sdk_overall_note"] = sdkOverallNote;
                if (o.EstimatedKeys.Count > 0) rank["estimate"] = true;
                o.Ranks[p.Key] = rank;
            }

            o.Method["stats"] = sdkOk ? "sdk" : "estimate";
            o.Method["stage"] = ctx.PlayMode ? "play_mode" : "pre_build";
            o.Method["active_build_target"] = ctx.ActiveTarget;
            o.Method["platforms"] = ctx.Platforms().Select(p => (object)p.Key).ToList();
            if (sdk != null)
            {
                if (sdk.Version != null) o.Method["sdk_version"] = sdk.Version;
                o.Method["sdk_elapsed_ms"] = sdk.ElapsedMs;
                if (sdk.ConstraintRefresh != "not_needed") o.Method["constraint_groups"] = sdk.ConstraintRefresh;
                if (!sdk.Ok) o.Method["fallback_reason"] = sdk.Error;
                // Stats this SDK cannot rate (category missing in an older SDK, or the rating call threw): rated with the tool's table.
                if (sdk.Ok && sdk.UnratedKeys.Count > 0) o.Method["sdk_unrated_stats"] = sdk.UnratedKeys.Cast<object>().ToList();
                if (sdk.Ok && sdk.RatingFailures > 0) o.Method["sdk_rating_failures"] = $"{sdk.RatingFailures} ({sdk.RatingError})";
            }
            else o.Method["fallback_reason"] = ctx.PlayMode ? "play_mode" : "not_run";
            if (o.EstimatedKeys.Count > 0 && sdkOk) o.Method["estimated_stats"] = o.EstimatedKeys.Cast<object>().ToList();

            if (raiseIssues) RaiseRankIssues(ctx, est, sdk, o);
            return o;
        }

        static bool SafeIsHuman(Animator a)
        {
            try { return a != null && a.isHuman; }
            catch (Exception) { return false; }
        }

        static void AddStatNotes(Ctx ctx, Est est, SdkResult sdk, Ranks.Stat def, Dictionary<string, object> entry)
        {
            switch (def.Key)
            {
                case "texture_memory":
                    var other = new List<object>();
                    if (ctx.WantMobile && !ctx.ActiveMobile) other.Add(ctx.MobileKey);
                    if (ctx.WantPc && ctx.ActiveMobile) other.Add("pc");
                    if (other.Count > 0)
                    {
                        entry["estimate_for"] = other;
                        entry["note"] = $"テクスチャはアクティブなビルドターゲット ({ctx.ActiveTarget}) のインポート結果で計算しています。{string.Join("/", other.Select(x => ctx.PlatformLabel((string)x)))} の値はそのプラットフォームの圧縮形式・最大サイズで変わるため推定です";
                    }
                    break;
                case "contacts":
                    if (est.ContactsEditorOnly > 0)
                        entry["note"] = $"EditorOnly の Contact {est.ContactsEditorOnly} 個を含みます (SDK もランク計算では数えますが、ビルド時に削除されるためアップロード後は {est.ContactsEditorOnly} 個少なくなります)";
                    break;
                case "constraint_depth":
                    if (sdk != null && (sdk.ConstraintRefresh.StartsWith("failed", StringComparison.Ordinal) || sdk.ConstraintRefresh == "not_initialized"))
                        entry["note"] = "VRC Constraint の実行グループを更新できなかったため、未計算の制約は 1 個ごとに深さ +1 として数えています (SDK と同じ扱い)";
                    break;
                case "triangles":
                    if (est.Lists.ContainsKey("lod_groups")) entry["note"] = "LODGroup は最も重い LOD レベルの三角形数で数えます (SDK と同じ)";
                    break;
                case "lights":
                    if (est.Particles.Any(ps => ps != null && ps.lights.light != null))
                        entry["note"] = "Particle System の Lights モジュールに Light が設定されている分も数えます (モジュールが無効でも)";
                    break;
            }
        }

        static object DisplayValue(Ranks.Stat def, object value)
        {
            if (value == null) return null;
            switch (def.Kind)
            {
                case Ranks.Kind.Bounds:
                    return value is double[] e && e.Length >= 3
                        ? new List<object> { Round(e[0] * 2), Round(e[1] * 2), Round(e[2] * 2) }
                        : null;
                case Ranks.Kind.Megabytes:
                    return Round(Convert.ToDouble(value), 2);
                case Ranks.Kind.Flag:
                    return value;
                default:
                    return Convert.ToInt64(value);
            }
        }

        static string FormatValue(Ranks.Stat def, object value)
        {
            var d = DisplayValue(def, value);
            if (d == null) return "なし";
            if (d is List<object> l) return string.Join("x", l.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture))) + " m";
            if (d is bool b) return b ? "true" : "false";
            if (def.Kind == Ranks.Kind.Megabytes) return Convert.ToString(d, CultureInfo.InvariantCulture) + " MB";
            return Convert.ToInt64(d).ToString("N0", CultureInfo.InvariantCulture);
        }

        static bool SameValue(Ranks.Stat def, object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            switch (def.Kind)
            {
                case Ranks.Kind.Bounds:
                    var x = a as double[];
                    var y = b as double[];
                    return x != null && y != null && x.Length >= 3 && y.Length >= 3 &&
                           Math.Abs(x[0] - y[0]) <= 0.001 && Math.Abs(x[1] - y[1]) <= 0.001 && Math.Abs(x[2] - y[2]) <= 0.001;
                case Ranks.Kind.Megabytes:
                    return Math.Abs(Convert.ToDouble(a) - Convert.ToDouble(b)) <= 0.01;
                case Ranks.Kind.Flag:
                    return Equals(a, b);
                default:
                    return Convert.ToInt64(a) == Convert.ToInt64(b);
            }
        }

        // =====================================================================================================================
        // Rank related issues
        // =====================================================================================================================

        /// <summary>Japanese text for a <see cref="Ranks.MinRankEffect"/> token (docs "Minimum Displayed Performance Rank" table).</summary>
        static string EffectText(string effect)
        {
            switch (effect)
            {
                case Ranks.EffectFallback: return "アバター全体が Fallback に置き換わります";
                case Ranks.EffectPhysBonesContacts: return "PhysBone・PhysBone Collider・Contact がすべて削除されます";
                case Ranks.EffectConstraints: return "Constraint がすべて削除されます";
                case "animators_removed": return "ルート以外の Animator がすべて削除されます";
                case "lights_removed": return "Light がすべて削除されます";
                case "particle_systems_removed": return "Particle System がすべて削除されます";
                case "trail_renderers_removed": return "Trail Renderer がすべて削除されます";
                case "line_renderers_removed": return "Line Renderer がすべて削除されます";
                case "raycasts_removed": return "VRCRaycast がすべて削除されます";
                case "cloth_removed": return "Cloth がすべて削除されます";
                case "physics_colliders_removed": return "Collider がすべて削除されます";
                case "rigidbodies_removed": return "Rigidbody がすべて削除されます";
                case "audio_sources_removed": return "Audio Source がすべて削除されます";
                default: return "表示は制限されません";
            }
        }

        /// <summary>
        /// What a Poor / Very Poor stat means for viewers (docs "Minimum Displayed Performance Rank"): PC defaults to Very Poor
        /// (nothing is managed unless the viewer chooses Medium or Poor); mobile defaults to Medium, can be relaxed to Poor but
        /// never to Very Poor (only "Show Avatar" overrides it).
        /// </summary>
        static string BlockingText(bool mobile, int rating, string effect)
        {
            var what = EffectText(effect);
            if (mobile && rating == Ranks.VeryPoor && (effect == Ranks.EffectPhysBonesContacts || effect == Ranks.EffectConstraints))
                return "。モバイルの上限 (docs: Mobile Avatar Component Limits) を超えているため、Show Avatar でも PhysBone・Contact・VRChat Constraint が除去されます";
            if (mobile)
                return rating == Ranks.VeryPoor
                    ? $"。モバイルでは既定で{what} (見る側は Very Poor を許可できず、Show Avatar でのみ解除)"
                    : $"。モバイルでは既定 (Minimum Displayed Rank = Medium) で{what} (見る側が Poor まで許可すれば解除)";
            return rating == Ranks.VeryPoor
                ? $"。PC の既定では制限されませんが、Minimum Displayed Rank を Medium / Poor にしている人には{what}"
                : $"。Minimum Displayed Rank を Medium にしている人には{what}";
        }

        static void RaiseRankIssues(Ctx ctx, Est est, SdkResult sdk, StatsOutput o)
        {
            if (sdk != null && !sdk.Ok)
                ctx.Issue(CctSeverity.Info, "SDK_CALCULATOR_UNAVAILABLE",
                    sdk.Error + "。自前の推定 (SDK 3.10.5 の数え方を再実装) で値を出しています。すべての stat が estimate です");
            if (sdk != null && sdk.Ok && sdk.IgnoreRuleReplaced)
                ctx.Issue(CctSeverity.Info, "SDK_IGNORE_RULE_REPLACED",
                    "AvatarPerformance.ShouldIgnoreComponent が他のツールにより設定されています。SDK の値はその除外ルールに従っています");
            if (sdk != null && sdk.Ok && (sdk.ConstraintRefresh.StartsWith("failed", StringComparison.Ordinal) || sdk.ConstraintRefresh == "not_initialized"))
                ctx.Issue(CctSeverity.Info, "CONSTRAINT_GROUPS_NOT_REFRESHED",
                    $"VRC Constraint の実行グループを更新できませんでした ({sdk.ConstraintRefresh})。Constraint Depth は実際より大きく出ることがあります");
            if (o.RankMismatches.Count > 0)
                ctx.Issue(CctSeverity.Info, "RANK_TABLE_MISMATCH",
                    "SDK の判定とこのツールの閾値表 (SDK 3.10.5) が違います。SDK の判定を採用しました (SDK の閾値が更新された可能性): " + string.Join("; ", o.RankMismatches.Take(10)),
                    value: o.RankMismatches.Count);
            if (o.ValueMismatches.Count > 0)
                ctx.Issue(CctSeverity.Info, "BREAKDOWN_MISMATCH",
                    "SDK の合計値と内訳 (breakdown) の合計が違う stat があります。値は SDK を採用し、内訳はおおよその目安として扱ってください: " + string.Join("; ", o.ValueMismatches.Take(10)),
                    value: o.ValueMismatches.Count);
            if (ctx.WantMobile && !ctx.ActiveMobile && Convert.ToDouble(o.Values["texture_memory"] ?? 0.0) > 0)
                ctx.Issue(CctSeverity.Info, "TEXTURE_MEMORY_PLATFORM",
                    $"Texture Memory はビルドターゲット {ctx.ActiveTarget} でインポートされたテクスチャから計算しています。{ctx.PlatformLabel(ctx.MobileKey)} の正確な値はビルドターゲットを切り替えてから再計測してください");

            var fallbackReasons = new List<string>();
            foreach (var p in ctx.Platforms())
            {
                var label = ctx.PlatformLabel(p.Key);
                bool mobile = p.Value;
                var ratings = o.Ratings[p.Key];
                var dynamicsRows = new List<string>();
                var removedRows = new List<string>();
                foreach (var def in Ranks.All)
                {
                    if (!ratings.TryGetValue(def.Key, out var rating)) continue;
                    if (def.Kind == Ranks.Kind.Bounds) continue; // BOUNDS_LARGE explains bounds with the contributing renderers
                    var value = o.Values[def.Key];
                    // docs "Mobile Removed Components": one MOBILE_REMOVED_COMPONENTS issue instead of a rank issue per row.
                    if (mobile && Ranks.IsMobileRemoved(def.Key))
                    {
                        if (value != null && Convert.ToDouble(value) > 0) removedRows.Add($"{def.Label} {FormatValue(def, value)}");
                        continue;
                    }
                    var effect = Ranks.MinRankEffect(def.Key);
                    if (mobile && rating == Ranks.VeryPoor && (effect == Ranks.EffectPhysBonesContacts || effect == Ranks.EffectConstraints))
                        dynamicsRows.Add(def.Label);
                    string severity;
                    if (rating == Ranks.VeryPoor) severity = CctSeverity.Warning;
                    else if (rating == Ranks.Poor) severity = mobile ? CctSeverity.Warning : CctSeverity.Info;
                    else continue;
                    string limitText = "";
                    object limitValue = null;
                    if (Ranks.NextImprovement(def, mobile, rating, out var limit, out var atLimit))
                    {
                        limitValue = def.Kind == Ranks.Kind.Megabytes ? (object)limit : (long)limit;
                        limitText = $" ({FormatValue(def, limitValue)} 以下で {Ranks.DisplayName(atLimit)})";
                    }
                    else if (def.Kind == Ranks.Kind.Flag)
                    {
                        limitValue = false;
                        limitText = " (無効にすると改善)";
                    }
                    ctx.Issue(ctx.ForPlatform(severity, mobile), "RANK_LIMIT",
                        $"{label}: {def.Label} = {FormatValue(def, value)} は {Ranks.DisplayName(rating)}{limitText}{BlockingText(mobile, rating, effect)}{(o.EstimatedKeys.Contains(def.Key) ? " [推定]" : "")}",
                        value: new Dictionary<string, object>
                        {
                            ["stat"] = def.Key, ["platform"] = p.Key, ["value"] = DisplayValue(def, value), ["rank"] = Ranks.Name(rating),
                            ["min_displayed_rank_effect"] = effect,
                        },
                        limit: limitValue);
                }
                // docs footnote: exceeding Very Poor on mobile removes all PhysBones, Contacts and VRChat Constraints, even with "Show Avatar".
                if (dynamicsRows.Count > 0)
                    ctx.Issue(ctx.ForPlatform(CctSeverity.Warning, mobile), "MOBILE_DYNAMICS_REMOVED",
                        $"{label}: {string.Join(", ", dynamicsRows)} が Very Poor のため、表示設定や Show Avatar に関係なく PhysBone・PhysBone Collider・Contact・VRChat Constraint が除去されます (docs: Mobile Avatar Component Limits)。" +
                        "PhysBone 8 個 / 影響 Transform 64 / Collider 16 / 衝突チェック 64 / Contact 16 / Constraint 150 個 / 深さ 50 以下にしてください",
                        value: dynamicsRows.Cast<object>().ToList());
                if (removedRows.Count > 0)
                    ctx.Issue(ctx.ForPlatform(CctSeverity.Warning, mobile), "MOBILE_REMOVED_COMPONENTS",
                        $"{label}: {string.Join(", ", removedRows)} はモバイルのアバターでは使えず常に除去されます (docs: Mobile Removed Components)。" +
                        "SDK の計算ではこれらが 1 つでもあると Very Poor (総合も Very Poor) になり、Light / Cloth / Collider / Rigidbody / AudioSource はモバイルのビルドターゲットではホワイトリスト外のエラーでアップロードできません。モバイル版では削除するか EditorOnly にしてください" +
                        (removedRows.Any(x => x.StartsWith("Lights", StringComparison.Ordinal)) ? " (Lights には Particle System の Lights モジュールに設定された Light も含みます)" : ""),
                        value: removedRows.Cast<object>().ToList());
                if (ctx.Descriptor != null && o.Ranks.TryGetValue(p.Key, out var rankObj) && rankObj is Dictionary<string, object> rank &&
                    rank.TryGetValue("fallback_eligible", out var fe) && fe is bool eligible && !eligible)
                    fallbackReasons.Add($"{label}: 総合 {Ranks.DisplayName(o.Overall[p.Key])}");
            }
            if (fallbackReasons.Count > 0)
            {
                bool humanoid = ctx.RootAnimator != null && SafeIsHuman(ctx.RootAnimator);
                ctx.Issue(CctSeverity.Info, "FALLBACK_INELIGIBLE",
                    $"カスタム Fallback には使えません ({(humanoid ? string.Join(" / ", fallbackReasons) : "Humanoid ではありません")})。Fallback にするには Humanoid かつ総合 Good 以上が必要です (対応する全プラットフォームでのアップロードも必要)");
            }

            // Hard caps outside the rank system (SDK AvatarValidation constants, SDK 3.10.5).
            HardLimit(ctx, o, "physbone_components", 256, "PhysBone コンポーネント");
            HardLimit(ctx, o, "physbone_colliders", 256, "PhysBone Collider");
            HardLimit(ctx, o, "constraints", 2000, "Constraint");
            HardLimit(ctx, o, "raycasts", 80, "VRCRaycast (FinalIK と合わせて 80 まで: docs)");
            if (o.ContactsAll.HasValue && o.ContactsAll.Value > 256)
                ctx.Issue(CctSeverity.Error, "HARD_LIMIT_EXCEEDED", $"Contact (Local Only 含む) が {o.ContactsAll.Value} 個あり、上限 256 を超えています (SDK エラー)", value: o.ContactsAll.Value, limit: 256);
            if (est.HeadChops > 16)
                ctx.Issue(CctSeverity.Error, "HARD_LIMIT_EXCEEDED", $"VRCHeadChop が {est.HeadChops} 個あり、上限 16 を超えています (SDK エラー)", value: est.HeadChops, limit: 16);
            if (est.GlobalCollidersAll > 4)
                ctx.Issue(CctSeverity.Error, "GLOBAL_COLLIDERS_EXCEEDED",
                    $"Global Collision が有効な PhysBone Collider が {est.GlobalCollidersAll} 個あります (上限 4、SDK エラー)。不要なものは Global Collision を False にしてください",
                    value: est.GlobalCollidersAll, limit: 4);
            foreach (var pb in est.PhysBones)
                if (pb.Transforms > PhysBoneChainLimit)
                    ctx.Issue(CctSeverity.Error, "PHYSBONE_CHAIN_TOO_LONG",
                        $"この PhysBone は {pb.Transforms} 個の Transform を動かそうとしています。1 コンポーネント 256 個を超えるとシミュレーションされません (docs は 128 超なら分割を推奨)",
                        ctx.PathOf(pb.Component), pb.Transforms, PhysBoneChainLimit);

            // Bounds.
            var bdef = Ranks.Find("bounds_size");
            if (o.Values["bounds_size"] is double[] ext)
            {
                bool veryPoor = o.Ratings.Values.Any(r => r.TryGetValue("bounds_size", out var v) && v == Ranks.VeryPoor);
                if (veryPoor)
                {
                    var reach = est.Lists.TryGetValue("bounds_reach", out var rows)
                        ? string.Join(", ", rows.OrderByDescending(x => x.Key).Take(3).Select(x => $"{x.Data["path"]} ({x.Data["max_offset_m"]} m)"))
                        : "";
                    ctx.Issue(CctSeverity.Warning, "BOUNDS_LARGE",
                        $"Bounds が {FormatValue(bdef, ext)} で 5x6x5 m を超え Very Poor です。docs では Bounds Size だけで表示がブロックされることはありませんが、SDK の総合ランクには入ります。" +
                        $"非表示のオブジェクトも数えるため、遠くに置いたギミックや大きなメッシュを確認してください。遠くまで伸びている Renderer: {reach}",
                        value: DisplayValue(bdef, ext), limit: new List<object> { 5.0, 6.0, 5.0 });
                }
            }
        }

        static void HardLimit(Ctx ctx, StatsOutput o, string key, int limit, string label)
        {
            if (!o.Values.TryGetValue(key, out var v) || v == null) return;
            var n = Convert.ToInt64(v);
            if (n > limit)
                ctx.Issue(CctSeverity.Error, "HARD_LIMIT_EXCEEDED", $"{label} が {n} 個あり、上限 {limit} を超えています (SDK エラー)", value: n, limit: limit);
        }

        // =====================================================================================================================
        // Report-only tables and notes
        // =====================================================================================================================

        static Dictionary<string, object> RankTablesForReport(Ctx ctx)
        {
            var d = new Dictionary<string, object>
            {
                ["source"] = "VRChat SDK 3.10.5 StatsLevels assets = creator-docs avatars/avatar-performance-ranking-system.md (2026-10-20); value <= limit => rank, above Poor => VeryPoor",
            };
            foreach (var p in new[] { new KeyValuePair<string, bool>("pc", false), new KeyValuePair<string, bool>("mobile", true) })
            {
                if (p.Value ? !ctx.WantMobile : !ctx.WantPc) continue;
                var table = new Dictionary<string, object>();
                foreach (var s in Ranks.All)
                {
                    var row = new Dictionary<string, object> { ["label"] = s.Label };
                    for (int r = Ranks.Excellent; r <= Ranks.Poor; r++)
                    {
                        object limit;
                        if (s.Kind == Ranks.Kind.Flag) limit = (p.Value ? s.MobileFlag : s.PcFlag)[r - 1];
                        else if (s.Kind == Ranks.Kind.Bounds) limit = Ranks.BoundsSizeLimit(s, p.Value, r).Select(x => (object)x).ToList();
                        else limit = (p.Value ? s.Mobile : s.Pc)[r - 1];
                        row[Ranks.Name(r).ToLowerInvariant()] = limit;
                    }
                    table[s.Key] = row;
                }
                d[p.Key] = table;
            }
            return d;
        }

        static Dictionary<string, object> BuildNotes(Ctx ctx, StatsOutput o)
        {
            var tools = new Dictionary<string, object>();
            foreach (var kv in ctx.Scan.ToolFamilies.OrderBy(k => k.Key, StringComparer.Ordinal)) tools[kv.Key] = kv.Value;
            var notes = new Dictionary<string, object>
            {
                ["stage"] = ctx.PlayMode ? "play_mode" : ctx.FromPrefabContents ? "pre_build_prefab_asset" : "pre_build",
                ["caveat"] = "編集中のアバターに対する静的な値です。Modular Avatar / NDMF / AAO / VRCFury はビルド時に構成を変えるため、アップロード後の値とは異なることがあります。非アクティブなオブジェクト・無効なコンポーネントも数えます (docs)",
                ["build_tools"] = tools,
                ["not_checked"] = new List<object>
                {
                    "download_size / uncompressed_size (実ビルドが必要: PC 200/500 MB, モバイル 10/40 MB)",
                    "SDK ビルド・アップロード、VRChat クライアント上の表示 (Read/Write 無効メッシュの Very Poor 表示、FinalIK と Raycast の合算 80 など)",
                    "Animator の中身の詳細 (cct_animator_inspect を使ってください)",
                },
            };
            if (ctx.WantMobile) notes["mobile"] = MobilePlatformNote;
            if (tools.Count > 0 && !ctx.PlayMode)
            {
                ctx.Issue(CctSeverity.Info, "PREBUILD_ESTIMATE",
                    $"MAやAAOなどのビルド時ツールのコンポーネントがあります ({string.Join(", ", tools.Select(k => k.Key + " " + k.Value))})。ビルド時に構成が変わるため、この値はビルド前の推定です (post_build=true で NDMF 処理後の値も確認できます)");
            }
            var applyOnBuild = NdmfApplyOnBuild();
            if (applyOnBuild.HasValue) notes["ndmf_apply_on_build"] = applyOnBuild.Value;
            return notes;
        }

        /// <summary>
        /// Issue codes whose error-severity instances stop the build: the SDK 3.10.5 avatar builder reports them with OnGUIError
        /// (Build / Upload disabled) for the active build target (research/avatar-limits.md §6; the mobile-only rules are errors only
        /// on an Android/iOS target), and POST_BUILD_NDMF_ERROR (an NDMF Error / InternalError fails the build).
        /// </summary>
        static readonly string[] UploadBlockingCodes =
        {
            "HARD_LIMIT_EXCEEDED", "GLOBAL_COLLIDERS_EXCEEDED", "MESH_NOT_READABLE", "BLENDSHAPE_NORMALS_LEGACY_OFF",
            "TEXTURE_MIP_STREAMING_OFF", "TEXTURE_MAX_SIZE_OVER_8192", "ANDROID_INCOMPATIBLE_SHADER", "NOT_WHITELISTED",
            "NOT_WHITELISTED_MOBILE", "DYNAMIC_BONE_PRESENT", "UNITY_CONSTRAINTS", "PIPELINE_MANAGER_COUNT", "PARTICLE_STOP_ACTION_DESTROY",
            "PARTICLE_ROOT_STOP_DISABLE", "TRAIL_AUTODESTRUCT", "AUDIO_DECOMPRESS_ON_LOAD", "ROOT_ANIMATOR_MISSING", "AVATAR_INACTIVE",
            "HUMANOID_BONES_MISSING", "HUMANOID_SPINE_HIERARCHY", "AVATAR_HEIGHT", "VISEME_MESH_MISSING", "GESTURE_MASK_MISSING",
            "EXPRESSIONS_ASSET_MISSING", "PARAM_BUDGET_EXCEEDED", "MENU_PARAM_UNDEFINED", "MENU_PUPPET_BOOL_PARAM", "MENU_ICON_INVALID",
            "POST_BUILD_NDMF_ERROR",
        };

        /// <summary>Reported as warnings because the tool reads a cached value, but the SDK makes them an error.</summary>
        static readonly string[] MayBlockCodes = { "ARMATURE_NOT_DIRECT_CHILD" };

        /// <summary>data.upload: would the SDK builder (active build target) let this avatar be built and uploaded?</summary>
        static Dictionary<string, object> BuildUploadSummary(Ctx ctx)
        {
            var d = new Dictionary<string, object>
            {
                ["build_target"] = ctx.ActiveTarget,
                ["platform"] = ctx.ActiveMobile ? (ctx.ActiveTarget == "iOS" ? "ios" : "android") : "pc",
            };
            if (ctx.Descriptor == null)
            {
                d["applicable"] = false;
                d["note"] = "VRCAvatarDescriptor が無いため、アップロードの可否は判定していません";
                return d;
            }
            var blocking = new List<object>();
            foreach (var code in UploadBlockingCodes)
                if (ctx.ErrorCounts.TryGetValue(code, out var n) && n > 0)
                    blocking.Add(new Dictionary<string, object> { ["code"] = code, ["count"] = n });
            var other = ctx.ErrorCounts.Where(kv => kv.Value > 0 && Array.IndexOf(UploadBlockingCodes, kv.Key) < 0)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (object)new Dictionary<string, object> { ["code"] = kv.Key, ["count"] = kv.Value }).ToList();
            var may = MayBlockCodes.Where(c => ctx.WarningCounts.TryGetValue(c, out var n) && n > 0).Cast<object>().ToList();
            d["blocked"] = blocking.Count > 0;
            d["blocking"] = blocking;
            // e.g. MISSING_SCRIPT, PHYSBONE_CHAIN_TOO_LONG, POST_BUILD_PARAM_BUDGET_EXCEEDED: real problems the SDK does not stop.
            if (other.Count > 0) d["errors_not_blocking"] = other;
            if (may.Count > 0) d["may_block"] = may;
            d["note"] = $"SDK 3.10.5 のビルダーがビルドターゲット {ctx.ActiveTarget} で Build / Upload を止める error だけを blocking に数えています (NDMF の Error を含む)。" +
                        "未検査: ビルド後のサイズ上限 (PC 200/500 MB、モバイル 10/40 MB)、PhysBone の依存の深さ、Particle の衝突レイヤー";
            return d;
        }
    }
}
