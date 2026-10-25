using System;
using System.Collections.Generic;

namespace Naoking.ClaudeCreatorTools
{
    public static partial class CctAvatarAudit
    {
        /// <summary>
        /// VRChat avatar performance rank tables and rating rules as plain data (no Unity types, so it is unit-testable
        /// outside Unity: tests/dev/cct/RankTests.cs in the repository).
        ///
        /// Thresholds: VRChat SDK 3.10.5 assets com.vrchat.base/Runtime/VRCSDK/Dependencies/VRChat/Resources/Validation/
        /// Performance/StatsLevels/{Windows,Quest}/{Excellent,Good,Medium,Poor}_*.asset, identical to the docs tables in
        /// creator-docs avatars/avatar-performance-ranking-system.md (snapshot 2026-10-20, "Value Maximums per Rank" PC and
        /// Mobile tables). The mobile ("Quest") set is used for Android and iOS alike (docs: "the limits are shared between
        /// iOS and Android", platforms/iOS/setting-up-unity-for-creating-ios-content.md). Rows that the mobile docs table
        /// omits (lights, cloth, physics, audio) are 0 at every level in the SDK asset.
        ///
        /// Rules (SDK 3.10.5 AvatarPerformanceStats, verified in research/avatar-limits.md §1.3):
        /// - value &lt;= threshold of a rank =&gt; that rank (checked Excellent, Good, Medium, Poor in order); above Poor =&gt; VeryPoor.
        /// - flags (particle trails/collision): enabled while the level does not allow it =&gt; worse.
        /// - bounds: every extent (half size) x/y/z must be &lt;= the level extent (approximately); a level extent.x &lt;= 0 always passes.
        /// - overall = worst rating among all rated stats, bounds included (docs: "If you go above these numbers for any
        ///   category, you'll be bumped into the next rank").
        /// </summary>
        public static class Ranks
        {
            public const int None = 0, Excellent = 1, Good = 2, Medium = 3, Poor = 4, VeryPoor = 5;

            static readonly string[] RatingNames = { "None", "Excellent", "Good", "Medium", "Poor", "VeryPoor" };

            public enum Kind { Count, Megabytes, Flag, Bounds }

            /// <summary>One row of the ranking tables.</summary>
            public sealed class Stat
            {
                /// <summary>snake_case key used in tool output (docs row name).</summary>
                public readonly string Key;
                /// <summary>Row name in the docs table.</summary>
                public readonly string Label;
                /// <summary>VRC.SDKBase.Validation.Performance.AvatarPerformanceCategory int value (FieldCheck enum dump).</summary>
                public readonly int Category;
                /// <summary>AvatarPerformanceStats field holding the value (sub field for physBone).</summary>
                public readonly string SdkField, SdkSubField;
                public readonly Kind Kind;
                public readonly double[] Pc, Mobile;          // Count / Megabytes: Excellent, Good, Medium, Poor
                public readonly bool[] PcFlag, MobileFlag;    // Flag: allowed per level
                public readonly double[][] PcExtents, MobileExtents; // Bounds: half sizes per level

                internal Stat(string key, string label, int category, string field, string sub, Kind kind,
                    double[] pc, double[] mobile, bool[] pcFlag = null, bool[] mobileFlag = null,
                    double[][] pcExtents = null, double[][] mobileExtents = null)
                {
                    Key = key; Label = label; Category = category; SdkField = field; SdkSubField = sub; Kind = kind;
                    Pc = pc; Mobile = mobile; PcFlag = pcFlag; MobileFlag = mobileFlag; PcExtents = pcExtents; MobileExtents = mobileExtents;
                }

                public string Unit => Kind == Kind.Megabytes ? "MB" : Kind == Kind.Bounds ? "m" : Kind == Kind.Flag ? "bool" : "count";

                /// <summary>AvatarPerformanceCategory member name for <see cref="Category"/> (the SDK is queried by name, see <see cref="SdkCategoryName"/>).</summary>
                public string SdkCategory => SdkCategoryName(Category);
            }

            /// <summary>
            /// VRC.SDKBase.Validation.Performance.AvatarPerformanceCategory member names by their SDK 3.10.5 int value (FieldCheck enum
            /// dump). The SDK calculator is queried by NAME, not by number: an SDK without a member (RaycastCount only exists since
            /// 3.10.3; its rating cache is sized by AvatarPerformanceCategoryCount) or with a different numbering must not shift
            /// ratings or throw IndexOutOfRange for every category.
            /// </summary>
            static readonly string[] SdkCategoryNames =
            {
                "None", "Overall", "DownloadSize", "UncompressedSize", "PolyCount", "AABB", "SkinnedMeshCount", "MeshCount", "MaterialCount",
                "PhysBoneComponentCount", "PhysBoneTransformCount", "PhysBoneColliderCount", "PhysBoneCollisionCheckCount", "ContactCount",
                "AnimatorCount", "BoneCount", "LightCount", "ParticleSystemCount", "ParticleTotalCount", "ParticleMaxMeshPolyCount",
                "ParticleTrailsEnabled", "ParticleCollisionEnabled", "TrailRendererCount", "LineRendererCount", "ClothCount", "ClothMaxVertices",
                "PhysicsColliderCount", "PhysicsRigidbodyCount", "AudioSourceCount", "TextureMegabytes", "ConstraintsCount", "ConstraintDepth",
                "RaycastCount",
            };

            /// <summary>SDK 3.10.5 AvatarPerformanceCategory name of an int value; null when out of range.</summary>
            public static string SdkCategoryName(int category) =>
                category >= 0 && category < SdkCategoryNames.Length ? SdkCategoryNames[category] : null;

            /// <summary>AvatarPerformanceCategory.Overall (worst rating of all categories in the SDK).</summary>
            public const string SdkOverallCategory = "Overall";

            static double[] D(params double[] v) => v;
            static bool[] B(params bool[] v) => v;

            // Bounds Size: docs PC/Mobile "2.5m x 2.5m x 2.5m / 4m x 4m x 4m / 5m x 6m x 5m / 5m x 6m x 5m"; SDK assets store the
            // half sizes in aabb.m_Extent (1.25 / 2 / 2.5,3,2.5 / 2.5,3,2.5).
            static readonly double[][] BoundsExtents =
            {
                new[] { 1.25, 1.25, 1.25 }, new[] { 2.0, 2.0, 2.0 }, new[] { 2.5, 3.0, 2.5 }, new[] { 2.5, 3.0, 2.5 },
            };

            /// <summary>Every row of the docs PC/Mobile tables (SDK 3.10.5 categories 4..32; DownloadSize/UncompressedSize need a build).</summary>
            public static readonly Stat[] All =
            {
                //       key                          docs row name                       cat  SDK field                     sub                     kind            PC (Exc, Good, Med, Poor)            Mobile
                new Stat("triangles",                 "Triangles",                         4, "polyCount",                null,                  Kind.Count,     D(32000, 70000, 70000, 70000),       D(7500, 10000, 15000, 20000)),
                new Stat("bounds_size",               "Bounds Size",                       5, "aabb",                     null,                  Kind.Bounds,    null, null, pcExtents: BoundsExtents, mobileExtents: BoundsExtents),
                new Stat("texture_memory",            "Texture Memory",                   29, "textureMegabytes",         null,                  Kind.Megabytes, D(40, 75, 110, 150),                 D(10, 18, 25, 40)),
                new Stat("skinned_meshes",            "Skinned Meshes",                    6, "skinnedMeshCount",         null,                  Kind.Count,     D(1, 2, 8, 16),                      D(1, 1, 2, 2)),
                new Stat("basic_meshes",              "Basic Meshes",                      7, "meshCount",                null,                  Kind.Count,     D(4, 8, 16, 24),                     D(1, 1, 2, 2)),
                new Stat("material_slots",            "Material Slots",                    8, "materialCount",            null,                  Kind.Count,     D(4, 8, 16, 32),                     D(1, 1, 2, 4)),
                new Stat("physbone_components",       "PhysBones Components",              9, "physBone",                 "componentCount",      Kind.Count,     D(4, 8, 16, 32),                     D(0, 4, 6, 8)),
                new Stat("physbone_transforms",       "PhysBones Affected Transforms",    10, "physBone",                 "transformCount",      Kind.Count,     D(16, 64, 128, 256),                 D(0, 16, 32, 64)),
                new Stat("physbone_colliders",        "PhysBones Colliders",              11, "physBone",                 "colliderCount",       Kind.Count,     D(4, 8, 16, 32),                     D(0, 4, 8, 16)),
                new Stat("physbone_collision_checks", "PhysBones Collision Check Count",  12, "physBone",                 "collisionCheckCount", Kind.Count,     D(32, 128, 256, 512),                D(0, 16, 32, 64)),
                new Stat("contacts",                  "Contacts",                         13, "contactCount",             null,                  Kind.Count,     D(8, 16, 24, 32),                    D(2, 4, 8, 16)),
                new Stat("constraints",               "Constraint Count",                 30, "constraintsCount",         null,                  Kind.Count,     D(100, 250, 300, 350),               D(30, 60, 120, 150)),
                new Stat("constraint_depth",          "Constraint Depth",                 31, "constraintDepth",          null,                  Kind.Count,     D(20, 50, 80, 100),                  D(5, 15, 35, 50)),
                new Stat("animators",                 "Animators",                        14, "animatorCount",            null,                  Kind.Count,     D(1, 4, 16, 32),                     D(1, 1, 1, 2)),
                new Stat("bones",                     "Bones",                            15, "boneCount",                null,                  Kind.Count,     D(75, 150, 256, 400),                D(75, 90, 150, 150)),
                new Stat("lights",                    "Lights",                           16, "lightCount",               null,                  Kind.Count,     D(0, 0, 0, 1),                       D(0, 0, 0, 0)),
                new Stat("particle_systems",          "Particle Systems",                 17, "particleSystemCount",      null,                  Kind.Count,     D(0, 4, 8, 16),                      D(0, 0, 0, 2)),
                new Stat("total_particles",           "Total Particles Active",           18, "particleTotalCount",       null,                  Kind.Count,     D(0, 300, 1000, 2500),               D(0, 0, 0, 200)),
                new Stat("mesh_particle_polys",       "Mesh Particle Active Polys",       19, "particleMaxMeshPolyCount", null,                  Kind.Count,     D(0, 1000, 2000, 5000),              D(0, 0, 0, 400)),
                new Stat("particle_trails_enabled",   "Particle Trails Enabled",          20, "particleTrailsEnabled",    null,                  Kind.Flag,      null, null, B(false, false, true, true), B(false, false, false, true)),
                new Stat("particle_collision_enabled","Particle Collision Enabled",       21, "particleCollisionEnabled", null,                  Kind.Flag,      null, null, B(false, false, true, true), B(false, false, false, true)),
                new Stat("trail_renderers",           "Trail Renderers",                  22, "trailRendererCount",       null,                  Kind.Count,     D(1, 2, 4, 8),                       D(0, 0, 0, 1)),
                new Stat("line_renderers",            "Line Renderers",                   23, "lineRendererCount",        null,                  Kind.Count,     D(1, 2, 4, 8),                       D(0, 0, 0, 1)),
                new Stat("raycasts",                  "Raycasts",                         32, "raycastCount",             null,                  Kind.Count,     D(1, 4, 8, 15),                      D(1, 2, 4, 8)),
                new Stat("cloths",                    "Cloths",                           24, "clothCount",               null,                  Kind.Count,     D(0, 1, 1, 1),                       D(0, 0, 0, 0)),
                new Stat("cloth_vertices",            "Total Cloth Vertices",             25, "clothMaxVertices",         null,                  Kind.Count,     D(0, 50, 100, 200),                  D(0, 0, 0, 0)),
                new Stat("physics_colliders",         "Physics Colliders",                26, "physicsColliderCount",     null,                  Kind.Count,     D(0, 1, 8, 8),                       D(0, 0, 0, 0)),
                new Stat("physics_rigidbodies",       "Physics Rigidbodies",              27, "physicsRigidbodyCount",    null,                  Kind.Count,     D(0, 1, 8, 8),                       D(0, 0, 0, 0)),
                new Stat("audio_sources",             "Audio Sources",                    28, "audioSourceCount",         null,                  Kind.Count,     D(1, 4, 8, 8),                       D(0, 0, 0, 0)),
            };

            static readonly Dictionary<string, Stat> ByKey = BuildIndex();

            static Dictionary<string, Stat> BuildIndex()
            {
                var d = new Dictionary<string, Stat>(StringComparer.Ordinal);
                foreach (var s in All) d[s.Key] = s;
                return d;
            }

            public static Stat Find(string key) => key != null && ByKey.TryGetValue(key, out var s) ? s : null;

            /// <summary>"Excellent" .. "VeryPoor" ("None" when not rated). Same spelling as the SDK's PerformanceRating names.</summary>
            public static string Name(int rating) => rating >= 0 && rating < RatingNames.Length ? RatingNames[rating] : "None";

            /// <summary>Name as VRChat shows it to people ("Very Poor" with a space); for messages, not for data values.</summary>
            public static string DisplayName(int rating) => rating == VeryPoor ? "Very Poor" : Name(rating);

            // ---------------------------------------------------------------------------------------------------------
            // What VRChat does when a stat is worse than the viewer's Minimum Displayed Performance Rank: creator-docs
            // avatars/avatar-performance-ranking-system.md "Minimum Displayed Performance Rank" table (snapshot 2026-10-20).
            // Bolded rows replace the whole avatar with its Fallback; the others remove only the related components;
            // Bounds Size never blocks ("Bounds Size will not cause the avatar to be blocked").
            // ---------------------------------------------------------------------------------------------------------

            public const string EffectFallback = "avatar_fallback";
            public const string EffectPhysBonesContacts = "physbones_contacts_removed";
            public const string EffectConstraints = "constraints_removed";
            public const string EffectNone = "none";

            static readonly Dictionary<string, string> MinRankEffects = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["triangles"] = EffectFallback, ["bounds_size"] = EffectNone, ["texture_memory"] = EffectFallback,
                ["skinned_meshes"] = EffectFallback, ["basic_meshes"] = EffectFallback, ["material_slots"] = EffectFallback,
                ["physbone_components"] = EffectPhysBonesContacts, ["physbone_transforms"] = EffectPhysBonesContacts,
                ["physbone_colliders"] = EffectPhysBonesContacts, ["physbone_collision_checks"] = EffectPhysBonesContacts,
                ["contacts"] = EffectPhysBonesContacts, ["constraints"] = EffectConstraints, ["constraint_depth"] = EffectConstraints,
                ["animators"] = "animators_removed", ["bones"] = EffectFallback, ["lights"] = "lights_removed",
                ["particle_systems"] = "particle_systems_removed", ["total_particles"] = "particle_systems_removed",
                ["mesh_particle_polys"] = "particle_systems_removed", ["particle_trails_enabled"] = "particle_systems_removed",
                ["particle_collision_enabled"] = "particle_systems_removed", ["trail_renderers"] = "trail_renderers_removed",
                ["line_renderers"] = "line_renderers_removed", ["raycasts"] = "raycasts_removed", ["cloths"] = "cloth_removed",
                ["cloth_vertices"] = "cloth_removed", ["physics_colliders"] = "physics_colliders_removed",
                ["physics_rigidbodies"] = "rigidbodies_removed", ["audio_sources"] = "audio_sources_removed",
            };

            /// <summary>Effect token of a stat (see <see cref="MinRankEffects"/>); null for an unknown key.</summary>
            public static string MinRankEffect(string key) => key != null && MinRankEffects.TryGetValue(key, out var e) ? e : null;

            /// <summary>
            /// docs "Mobile Removed Components": Lights, Cloths, Total Cloth Vertices, Physics Colliders, Physics Rigidbodies and
            /// Audio Sources never exist on mobile avatars (always removed, shown as 0 in game). The SDK's mobile thresholds for
            /// these rows are 0 at every level, so the SDK rates any of them Very Poor.
            /// </summary>
            static readonly HashSet<string> MobileRemovedKeys = new HashSet<string>(StringComparer.Ordinal)
            {
                "lights", "cloths", "cloth_vertices", "physics_colliders", "physics_rigidbodies", "audio_sources",
            };

            public static bool IsMobileRemoved(string key) => key != null && MobileRemovedKeys.Contains(key);

            public static int Parse(string name)
            {
                for (int i = 0; i < RatingNames.Length; i++)
                    if (string.Equals(RatingNames[i], name, StringComparison.OrdinalIgnoreCase)) return i;
                return None;
            }

            /// <summary>Numeric rows. Float arithmetic like the SDK (compare delegate returns a float, rank when diff &lt;= 0).</summary>
            public static int RateValue(double value, double[] limits)
            {
                if (limits == null || limits.Length < 4 || double.IsNaN(value)) return None;
                for (int i = 0; i < 4; i++)
                    if ((float)value - (float)limits[i] <= 0f) return Excellent + i;
                return VeryPoor;
            }

            /// <summary>Flag rows: the SDK compare is 0 when equal, else (value ? 1 : -1); &lt;= 0 means within the level.</summary>
            public static int RateFlag(bool value, bool[] allowed)
            {
                if (allowed == null || allowed.Length < 4) return None;
                for (int i = 0; i < 4; i++)
                    if (!value || allowed[i]) return Excellent + i;
                return VeryPoor;
            }

            /// <summary>Bounds row: half sizes (extents) of the avatar AABB against each level's extents.</summary>
            public static int RateExtents(double ex, double ey, double ez, double[][] levels)
            {
                if (levels == null || levels.Length < 4) return None;
                for (int i = 0; i < 4; i++)
                {
                    var l = levels[i];
                    if (ApproxLessOrEqual(l[0], 0) ||
                        (ApproxLessOrEqual(ex, l[0]) && ApproxLessOrEqual(ey, l[1]) && ApproxLessOrEqual(ez, l[2])))
                        return Excellent + i;
                }
                return VeryPoor;
            }

            /// <summary>a &lt;= b, or equal within Unity's Mathf.Approximately tolerance (float arithmetic as in the SDK).</summary>
            public static bool ApproxLessOrEqual(double a, double b)
            {
                float diff = (float)a - (float)b;
                if (diff < 0f) return true;
                float tolerance = Math.Max(1E-06f * Math.Abs(diff), float.Epsilon * 8f);
                return Math.Abs(diff) < tolerance;
            }

            /// <summary>
            /// Rates one stat. <paramref name="value"/>: number (int/long/float/double) for counts and MB, bool for flags,
            /// double[3] extents (half sizes) for bounds; null =&gt; None (not rated, like the SDK's null values).
            /// </summary>
            public static int Rate(Stat stat, bool mobile, object value)
            {
                if (stat == null || value == null) return None;
                switch (stat.Kind)
                {
                    case Kind.Flag:
                        return value is bool b ? RateFlag(b, mobile ? stat.MobileFlag : stat.PcFlag) : None;
                    case Kind.Bounds:
                        return value is double[] e && e.Length >= 3 ? RateExtents(e[0], e[1], e[2], mobile ? stat.MobileExtents : stat.PcExtents) : None;
                    default:
                        var n = ToDouble(value);
                        return n.HasValue ? RateValue(n.Value, mobile ? stat.Mobile : stat.Pc) : None;
                }
            }

            static double? ToDouble(object v)
            {
                switch (v)
                {
                    case int i: return i;
                    case long l: return l;
                    case float f: return f;
                    case double d: return d;
                    case short s: return s;
                    case uint u: return u;
                }
                return null;
            }

            /// <summary>Overall = worst (highest) rating among rated stats; None when nothing is rated.</summary>
            public static int Overall(IEnumerable<int> ratings)
            {
                int worst = None;
                if (ratings == null) return worst;
                foreach (var r in ratings)
                    if (r > worst && r <= VeryPoor) worst = r;
                return worst;
            }

            /// <summary>Keys whose rating equals the overall rating (the stats that set it). Empty when overall is Excellent/None.</summary>
            public static List<string> Limiting(IEnumerable<KeyValuePair<string, int>> ratings, int overall)
            {
                var list = new List<string>();
                if (ratings == null || overall <= Excellent) return list;
                foreach (var kv in ratings)
                    if (kv.Value == overall) list.Add(kv.Key);
                return list;
            }

            /// <summary>Threshold (maximum value) of <paramref name="rank"/> (Excellent..Poor) for a numeric stat; null otherwise.</summary>
            public static double? Limit(Stat stat, bool mobile, int rank)
            {
                if (stat == null || rank < Excellent || rank > Poor) return null;
                var limits = mobile ? stat.Mobile : stat.Pc;
                if (limits == null || stat.Kind == Kind.Flag || stat.Kind == Kind.Bounds) return null;
                return limits[rank - 1];
            }

            /// <summary>Maximum full size (2 x extents) of <paramref name="rank"/> for the bounds row; null otherwise.</summary>
            public static double[] BoundsSizeLimit(Stat stat, bool mobile, int rank)
            {
                if (stat == null || stat.Kind != Kind.Bounds || rank < Excellent || rank > Poor) return null;
                var e = (mobile ? stat.MobileExtents : stat.PcExtents)[rank - 1];
                return new[] { e[0] * 2, e[1] * 2, e[2] * 2 };
            }

            /// <summary>
            /// What the value must get down to for the next better rank: the threshold of rank (current - 1) and the rank a value
            /// exactly at that threshold gets (rows with equal thresholds jump several ranks, e.g. PC Triangles VeryPoor -> Good).
            /// Returns false when already Excellent / not rated / a flag or bounds row.
            /// </summary>
            public static bool NextImprovement(Stat stat, bool mobile, int current, out double limit, out int rankAtLimit)
            {
                limit = 0; rankAtLimit = None;
                if (stat == null || current <= Excellent || current > VeryPoor) return false;
                var l = Limit(stat, mobile, current - 1);
                if (!l.HasValue) return false;
                limit = l.Value;
                rankAtLimit = RateValue(limit, mobile ? stat.Mobile : stat.Pc);
                return true;
            }

            // ---------------------------------------------------------------------------------------------------------
            // Texture memory (SDK 3.10.5 MeshPerformanceScanner, research/avatar-limits.md §4.6): bytes per pixel by the
            // UnityEngine.TextureFormat int value; formats not in the table count as 16 bpp.
            // ---------------------------------------------------------------------------------------------------------

            /// <summary>Texture kinds the SDK distinguishes.</summary>
            public enum TextureKind { Texture2D, Cubemap, Texture2DArray, Texture3D, RenderTexture, Other }

            public const float UnknownFormatBytesPerPixel = 16f;

            static readonly Dictionary<int, float> BytesPerPixel = new Dictionary<int, float>
            {
                [1] = 1f, [2] = 2f, [3] = 4f, [4] = 4f, [5] = 4f, [7] = 2f, [9] = 2f, [10] = 0.5f, [12] = 1f, [13] = 2f,
                [14] = 4f, [15] = 2f, [16] = 4f, [17] = 8f, [18] = 4f, [19] = 8f, [20] = 16f, [24] = 1f, [25] = 1f, [26] = 0.5f,
                [27] = 1f, [28] = 0.5f, [29] = 1f, [30] = 0.25f, [31] = 0.25f, [32] = 0.5f, [33] = 0.5f, [34] = 0.5f,
                [41] = 0.5f, [42] = 0.5f, [43] = 1f, [44] = 1f, [45] = 0.5f, [46] = 0.5f, [47] = 1f, [48] = 1f, [49] = 0.64f,
                [50] = 0.445f, [51] = 0.25f, [52] = 0.16f, [53] = 0.11125f, [62] = 2f, [63] = 1f, [64] = 0.5f, [65] = 1f,
            };

            public static float GetBytesPerPixel(int textureFormat) =>
                BytesPerPixel.TryGetValue(textureFormat, out var b) ? b : UnknownFormatBytesPerPixel;

            public static bool IsKnownFormat(int textureFormat) => BytesPerPixel.ContainsKey(textureFormat);

            /// <summary>
            /// Bytes the SDK attributes to one texture: 2D/Cubemap sum over mips of round((w*h &gt;&gt; 2i) * bpp) (one cube face
            /// only), arrays/3D round(w*h*depth*bpp) without mips, RenderTexture and other kinds 0. Rounding is Unity's
            /// Mathf.RoundToInt (banker's rounding on a float).
            /// </summary>
            public static long TextureBytes(TextureKind kind, int width, int height, int depth, int mipCount, int textureFormat)
            {
                float bpp = GetBytesPerPixel(textureFormat);
                switch (kind)
                {
                    case TextureKind.Texture2D:
                    case TextureKind.Cubemap:
                    {
                        long total = 0;
                        long pixels = (long)width * height;
                        for (int i = 0; i < Math.Max(0, mipCount); i++)
                        {
                            int shift = 2 * i;
                            long mip = shift >= 63 ? 0 : pixels >> shift;
                            total += RoundToInt((float)mip * bpp);
                        }
                        return total;
                    }
                    case TextureKind.Texture2DArray:
                    case TextureKind.Texture3D:
                        return RoundToInt((float)((long)width * height * Math.Max(0, depth)) * bpp);
                    default:
                        return 0;
                }
            }

            static long RoundToInt(float f) => (long)Math.Round(f);

            /// <summary>SDK conversion: (float)(double)bytes / 1048576f.</summary>
            public static float BytesToMegabytes(long bytes) => (float)(double)bytes / 1048576f;

            // ---------------------------------------------------------------------------------------------------------
            // Expression parameter cost (SDK 3.10.5 VRCExpressionParameters.TypeCost: Int 8, Float 8, Bool 1; only
            // networkSynced entries count, empty names included). ValueType ints from FieldCheck: Int=0, Float=1, Bool=2.
            // ---------------------------------------------------------------------------------------------------------

            /// <summary>Synced-parameter budget (VRCExpressionParameters.MAX_PARAMETER_COST = 256).</summary>
            public const int MaxParameterCost = 256;

            public static int ParameterCost(int valueType) => valueType == 2 ? 1 : 8;
        }
    }
}
