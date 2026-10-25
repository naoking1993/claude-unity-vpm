using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;

namespace Naoking.ClaudeCreatorTools
{
    // The VRChat SDK's own performance calculator, called by reflection (no compile-time SDK dependency). Recipe and every
    // signature: research/avatar-limits.md §15.1 (resolved against the real SDK 3.10.5 DLLs). This is our own code, not a copy.
    //   AvatarPerformanceStats.Initialize()                                   - loads the threshold assets (idempotent)
    //   VRCConstraintManager.Sdk_ManuallyRefreshGroups(VRCConstraintBase[])    - like the SDK builder, so Constraint Depth matches
    //   new AvatarPerformanceStats(bool) + AvatarPerformance.CalculatePerformanceStats(string, GameObject, AvatarPerformanceStats, bool)
    //   AvatarPerformanceStats.CalculateAllPerformanceRatings(bool) + GetPerformanceRatingForCategory(AvatarPerformanceCategory)
    // Categories and ratings are matched by enum NAME (Ranks.SdkCategoryName / Ranks.Parse), never by the 3.10.5 int values, so
    // an older or newer SDK (e.g. no RaycastCount before 3.10.3) degrades per stat instead of failing as a whole.
    // Side effects (verified in the SDK source): VRCPhysBoneBase.InitTransforms(force) rewrites [NonSerialized] chain data and the
    // constraint refresh writes non-serialized group indices; nothing serialized, no scene dirtying. Not run in Play mode.
    public static partial class CctAvatarAudit
    {
        const string TAvatarPerformance = "VRC.SDKBase.Validation.Performance.AvatarPerformance";
        const string TAvatarPerformanceStats = "VRC.SDKBase.Validation.Performance.Stats.AvatarPerformanceStats";
        const string TAvatarPerformanceCategory = "VRC.SDKBase.Validation.Performance.AvatarPerformanceCategory";
        const string TVrcConstraintManager = "VRC.Dynamics.VRCConstraintManager";

        sealed class SdkResult
        {
            public bool Ok;
            public string Error;
            public string Version;
            public long ElapsedMs;
            /// <summary>Ranks key -> long / double (MB) / bool / double[3] extents; missing key = SDK value was null.</summary>
            public readonly Dictionary<string, object> Values = new Dictionary<string, object>(StringComparer.Ordinal);
            public Vector3 BoundsCenter;
            public int? ContactCompleteCount;
            /// <summary>"pc" / "mobile" -> ranks key -> rating; plus overall.</summary>
            public readonly Dictionary<string, Dictionary<string, int>> Ratings = new Dictionary<string, Dictionary<string, int>>();
            public readonly Dictionary<string, int> Overall = new Dictionary<string, int>();
            public string ConstraintRefresh = "not_needed";
            public bool IgnoreRuleReplaced;
            /// <summary>Stat keys whose AvatarPerformanceCategory member this SDK does not define (rated with the tool's table).</summary>
            public readonly List<string> UnratedKeys = new List<string>();
            /// <summary>GetPerformanceRatingForCategory calls that threw (that stat falls back to the tool's table).</summary>
            public int RatingFailures;
            public string RatingError;
        }

        static Exception Unwrap(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e;
        }

        static SdkResult RunSdkCalculator(GameObject root, bool pc, bool mobile)
        {
            var res = new SdkResult();
            var sw = Stopwatch.StartNew();
            try
            {
                const BindingFlags PS = BindingFlags.Public | BindingFlags.Static;
                const BindingFlags PI = BindingFlags.Public | BindingFlags.Instance;
                var tAP = CctSerialized.FindType(TAvatarPerformance);
                var tStats = CctSerialized.FindType(TAvatarPerformanceStats);
                var tCat = CctSerialized.FindType(TAvatarPerformanceCategory);
                if (tAP == null || tStats == null || tCat == null || !tCat.IsEnum)
                {
                    res.Error = "VRChat SDK の性能計算クラスが見つかりません (VRChat SDK Base が未導入か、型名が変わりました)";
                    return res;
                }
                var mInit = tStats.GetMethod("Initialize", PS, null, Type.EmptyTypes, null);
                var ctor = tStats.GetConstructor(new[] { typeof(bool) });
                var mCalc = tAP.GetMethod("CalculatePerformanceStats", PS, null, new[] { typeof(string), typeof(GameObject), tStats, typeof(bool) }, null);
                var mRating = tStats.GetMethod("GetPerformanceRatingForCategory", PI, null, new[] { tCat }, null);
                var mAll = tStats.GetMethod("CalculateAllPerformanceRatings", PI, null, new[] { typeof(bool) }, null);
                var missing = new List<string>();
                if (mInit == null) missing.Add("AvatarPerformanceStats.Initialize()");
                if (ctor == null) missing.Add("AvatarPerformanceStats(bool)");
                if (mCalc == null) missing.Add("AvatarPerformance.CalculatePerformanceStats(string, GameObject, AvatarPerformanceStats, bool)");
                if (mRating == null) missing.Add("GetPerformanceRatingForCategory(AvatarPerformanceCategory)");
                if (mAll == null) missing.Add("CalculateAllPerformanceRatings(bool)");
                if (missing.Count > 0)
                {
                    res.Error = "VRChat SDK の API が想定と違います: " + string.Join(", ", missing);
                    return res;
                }
                res.Version = PackageVersion("Packages/com.vrchat.base");
                try
                {
                    var ignore = tAP.GetProperty("ShouldIgnoreComponent", PS);
                    res.IgnoreRuleReplaced = ignore != null && ignore.GetValue(null, null) != null;
                }
                catch (Exception) { /* informational only */ }

                mInit.Invoke(null, null);
                res.ConstraintRefresh = RefreshConstraintGroups(root);

                var stats = ctor.Invoke(new object[] { false });
                mCalc.Invoke(null, new object[] { root.name, root, stats, false });

                // Per field: a field whose type changed in another SDK version is skipped (that stat is estimated), not fatal.
                foreach (var def in Ranks.All)
                {
                    try { ReadSdkValue(tStats, stats, def, res); }
                    catch (Exception) { res.Values.Remove(def.Key); }
                }
                try
                {
                    var complete = tStats.GetField("contactCompleteCount", PI)?.GetValue(stats);
                    if (complete != null) res.ContactCompleteCount = Convert.ToInt32(complete);
                }
                catch (Exception) { res.ContactCompleteCount = null; /* the estimator's count is used */ }

                // Categories are looked up by NAME (Ranks.SdkCategoryName). Older SDKs lack members (RaycastCount before 3.10.3, the
                // constraint rows before 3.7.0) and size the rating cache by their own category count, so passing the 3.10.5 int
                // would throw IndexOutOfRange and discard the whole SDK result. Missing members are rated with the tool's table.
                var categories = new List<KeyValuePair<string, object>>();
                foreach (var def in Ranks.All)
                {
                    var name = def.SdkCategory;
                    if (name != null && Enum.IsDefined(tCat, name)) categories.Add(new KeyValuePair<string, object>(def.Key, Enum.Parse(tCat, name)));
                    else res.UnratedKeys.Add(def.Key);
                }
                var overallCategory = Enum.IsDefined(tCat, Ranks.SdkOverallCategory) ? Enum.Parse(tCat, Ranks.SdkOverallCategory) : null;

                // Same counts, both threshold sets: CalculateAllPerformanceRatings clears the rating cache and re-rates every category.
                foreach (var platform in new[] { "pc", "mobile" })
                {
                    bool isMobile = platform == "mobile";
                    if (isMobile ? !mobile : !pc) continue;
                    mAll.Invoke(stats, new object[] { isMobile });
                    var ratings = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var kv in categories)
                    {
                        var rating = SdkRating(mRating, stats, kv.Value, res);
                        if (rating != Ranks.None) ratings[kv.Key] = rating;
                    }
                    res.Ratings[platform] = ratings;
                    if (overallCategory != null) res.Overall[platform] = SdkRating(mRating, stats, overallCategory, res);
                }
                res.Ok = true;
            }
            catch (Exception e)
            {
                var inner = Unwrap(e);
                res.Ok = false;
                res.Error = $"VRChat SDK の性能計算が失敗しました: {inner.GetType().Name}: {inner.Message}";
            }
            finally
            {
                sw.Stop();
                res.ElapsedMs = sw.ElapsedMilliseconds;
            }
            return res;
        }

        /// <summary>
        /// One category rating by enum NAME ("VeryPoor" -> Ranks.VeryPoor), so a renumbered PerformanceRating cannot shift ranks.
        /// A category that throws is skipped (counted in RatingFailures) instead of discarding the whole SDK result.
        /// </summary>
        static int SdkRating(MethodInfo mRating, object stats, object category, SdkResult res)
        {
            try
            {
                var v = mRating.Invoke(stats, new[] { category });
                return v == null ? Ranks.None : Ranks.Parse(v.ToString());
            }
            catch (Exception e)
            {
                res.RatingFailures++;
                res.RatingError ??= category + ": " + Unwrap(e).GetType().Name + ": " + Unwrap(e).Message;
                return Ranks.None;
            }
        }

        static void ReadSdkValue(Type tStats, object stats, Ranks.Stat def, SdkResult res)
        {
            var f = tStats.GetField(def.SdkField, BindingFlags.Public | BindingFlags.Instance);
            if (f == null) return;
            var v = f.GetValue(stats); // Nullable<T> boxes to null or to T
            if (v == null) return;
            switch (def.Kind)
            {
                case Ranks.Kind.Bounds:
                    if (v is Bounds b)
                    {
                        res.Values[def.Key] = new double[] { b.extents.x, b.extents.y, b.extents.z };
                        res.BoundsCenter = b.center;
                    }
                    return;
                case Ranks.Kind.Flag:
                    if (v is bool flag) res.Values[def.Key] = flag;
                    return;
                case Ranks.Kind.Megabytes:
                    res.Values[def.Key] = Convert.ToDouble(v);
                    return;
                default:
                    if (def.SdkSubField != null)
                    {
                        var sub = v.GetType().GetField(def.SdkSubField, BindingFlags.Public | BindingFlags.Instance);
                        if (sub == null) return;
                        v = sub.GetValue(v);
                        if (v == null) return;
                    }
                    res.Values[def.Key] = Convert.ToInt64(v);
                    return;
            }
        }

        /// <summary>
        /// Same as the SDK builder before scanning: VRCConstraintManager.Sdk_ManuallyRefreshGroups(avatar constraints). The manager is an
        /// internal class (InternalsVisibleTo the SDK only), so reflection. Skipped when not initialised (the SDK would throw).
        /// </summary>
        static string RefreshConstraintGroups(GameObject root)
        {
            var tBase = CctSerialized.FindType(TVrcConstraintBase);
            if (tBase == null) return "not_needed";
            Component[] found;
            try { found = root.GetComponentsInChildren(tBase, true); }
            catch (Exception) { return "failed: GetComponentsInChildren"; }
            if (found == null || found.Length == 0) return "not_needed";
            var tManager = CctSerialized.FindType(TVrcConstraintManager);
            if (tManager == null) return "failed: VRCConstraintManager not found";
            const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            try
            {
                var initialized = tManager.GetProperty("IsInitialized", S);
                if (initialized == null || !(initialized.GetValue(null, null) is bool ok) || !ok) return "not_initialized";
                var m = tManager.GetMethod("Sdk_ManuallyRefreshGroups", S, null, new[] { tBase.MakeArrayType() }, null);
                if (m == null) return "failed: Sdk_ManuallyRefreshGroups not found";
                var typed = Array.CreateInstance(tBase, found.Length);
                Array.Copy(found, typed, found.Length);
                m.Invoke(null, new object[] { typed });
                return "refreshed";
            }
            catch (Exception e)
            {
                var inner = Unwrap(e);
                return $"failed: {inner.GetType().Name}: {inner.Message}";
            }
        }

        static string PackageVersion(string packagePath)
        {
            try { return UnityEditor.PackageManager.PackageInfo.FindForAssetPath(packagePath)?.version; }
            catch (Exception) { return null; }
        }
    }
}
