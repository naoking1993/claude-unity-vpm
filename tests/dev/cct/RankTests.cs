// Unit tests for CctAvatarAudit.Ranks (pure, no Unity). Build + run:
//   CSC=$(ls /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll | head -1); API=/usr/lib/mono/4.7.1-api
//   dotnet $CSC -nologo -langversion:9 -nostdlib -noconfig -r:$API/mscorlib.dll -r:$API/System.dll -r:$API/System.Core.dll \
//     -r:$API/Facades/netstandard.dll -out:RankTests.exe RankTests.cs <package>/Editor/Tools/CctAvatarAudit.Ranks.cs && mono RankTests.exe
using System; using System.Collections.Generic; using System.Linq; using Naoking.ClaudeCreatorTools;
using R = Naoking.ClaudeCreatorTools.CctAvatarAudit.Ranks;
public static class RankTests {
  static int fails = 0, passes = 0;
  static void Eq(object got, object want, string what) {
    if (!Equals(got, want)) { Console.WriteLine($"FAIL {what}: got [{got}] want [{want}]"); fails++; } else passes++;
  }
  static int Pc(string key, object v) => R.Rate(R.Find(key), false, v);
  static int Mob(string key, object v) => R.Rate(R.Find(key), true, v);
  public static int Main() {
    // --- boundary values: value == threshold => that rank, threshold + 1 => next rank
    Eq(Pc("triangles", 32000), R.Excellent, "pc tris 32000 (== Excellent limit)");
    Eq(Pc("triangles", 32001), R.Good, "pc tris 32001");
    Eq(Pc("triangles", 70000), R.Good, "pc tris 70000 (Good=Medium=Poor limit -> Good)");
    Eq(Pc("triangles", 70001), R.VeryPoor, "pc tris 70001 (> Poor)");
    Eq(Mob("triangles", 7500), R.Excellent, "mobile tris 7500");
    Eq(Mob("triangles", 7501), R.Good, "mobile tris 7501");
    Eq(Mob("triangles", 10000), R.Good, "mobile tris 10000");
    Eq(Mob("triangles", 15000), R.Medium, "mobile tris 15000");
    Eq(Mob("triangles", 20000), R.Poor, "mobile tris 20000 (== Poor limit)");
    Eq(Mob("triangles", 20001), R.VeryPoor, "mobile tris 20001 (> Poor)");
    Eq(Pc("skinned_meshes", 16), R.Poor, "pc skinned 16");
    Eq(Pc("skinned_meshes", 17), R.VeryPoor, "pc skinned 17 (> Poor)");
    Eq(Pc("material_slots", 4L), R.Excellent, "pc materials 4 (long)");
    Eq(Pc("material_slots", 5), R.Good, "pc materials 5");
    Eq(Pc("bones", 400), R.Poor, "pc bones 400");
    Eq(Pc("bones", 401), R.VeryPoor, "pc bones 401");
    Eq(Mob("bones", 150), R.Medium, "mobile bones 150 (Medium=Poor -> Medium)");
    Eq(Mob("bones", 151), R.VeryPoor, "mobile bones 151");
    // zero thresholds
    Eq(Pc("lights", 0), R.Excellent, "pc lights 0");
    Eq(Pc("lights", 1), R.Poor, "pc lights 1 (only Poor allows 1)");
    Eq(Pc("lights", 2), R.VeryPoor, "pc lights 2");
    Eq(Mob("lights", 1), R.VeryPoor, "mobile lights 1 (all levels 0)");
    Eq(Mob("audio_sources", 0), R.Excellent, "mobile audio 0");
    Eq(Mob("physbone_components", 0), R.Excellent, "mobile pb 0");
    Eq(Mob("physbone_components", 1), R.Good, "mobile pb 1");
    Eq(Mob("physbone_components", 8), R.Poor, "mobile pb 8");
    Eq(Mob("physbone_components", 9), R.VeryPoor, "mobile pb 9");
    Eq(Pc("animators", 1), R.Excellent, "pc animators 1 (root animator)");
    // float MB
    Eq(Pc("texture_memory", 40.0), R.Excellent, "pc tex 40.0 MB");
    Eq(Pc("texture_memory", 40.01f), R.Good, "pc tex 40.01 MB");
    Eq(Pc("texture_memory", 150.0), R.Poor, "pc tex 150 MB");
    Eq(Pc("texture_memory", 150.5), R.VeryPoor, "pc tex 150.5 MB");
    Eq(Mob("texture_memory", 10.0f), R.Excellent, "mobile tex 10 MB");
    Eq(Mob("texture_memory", 40.5), R.VeryPoor, "mobile tex 40.5 MB");
    // flags
    Eq(Pc("particle_trails_enabled", false), R.Excellent, "pc trails off");
    Eq(Pc("particle_trails_enabled", true), R.Medium, "pc trails on -> Medium");
    Eq(Mob("particle_trails_enabled", true), R.Poor, "mobile trails on -> Poor");
    Eq(Mob("particle_collision_enabled", false), R.Excellent, "mobile collision off");
    // bounds rule (extents = half size), each axis must fit, approximately-equal counts as fitting
    var bounds = R.Find("bounds_size");
    Eq(R.Rate(bounds, false, new[] { 1.25, 1.25, 1.25 }), R.Excellent, "bounds 2.5^3 == Excellent limit");
    Eq(R.Rate(bounds, false, new[] { 1.2501, 1.0, 1.0 }), R.Good, "bounds x over Excellent");
    Eq(R.Rate(bounds, false, new[] { 2.0, 2.0, 2.0 }), R.Good, "bounds 4^3");
    Eq(R.Rate(bounds, false, new[] { 1.0, 2.5, 1.0 }), R.Medium, "bounds y 5 m -> Medium (6 m allowed)");
    Eq(R.Rate(bounds, false, new[] { 2.5, 3.0, 2.5 }), R.Medium, "bounds 5x6x5 == Medium/Poor limit -> Medium");
    Eq(R.Rate(bounds, false, new[] { 2.5, 3.01, 2.5 }), R.VeryPoor, "bounds y over Poor");
    Eq(R.Rate(bounds, true, new[] { 2.6, 0.1, 0.1 }), R.VeryPoor, "mobile bounds x over Poor");
    Eq(R.Rate(bounds, false, new[] { 1.25 + 1e-9, 1.25, 1.25 }), R.Excellent, "double noise below float precision stays Excellent");
    Eq(R.Rate(bounds, false, new[] { 1.25 + 2e-7, 1.25, 1.25 }), R.Good, "next float above the limit is over (SDK float compare)");
    Eq(R.RateExtents(100, 100, 100, new[] { new[] { 0.0, 0, 0 }, new[] { 1.0, 1, 1 }, new[] { 1.0, 1, 1 }, new[] { 1.0, 1, 1 } }), R.Excellent, "level extent.x <= 0 always passes");
    Eq(R.ApproxLessOrEqual(1.0, 1.0), true, "approx equal");
    Eq(R.ApproxLessOrEqual(1.0001, 1.0), false, "approx greater");
    // not rated
    Eq(Pc("triangles", null), R.None, "null value -> None");
    Eq(R.Rate(R.Find("particle_trails_enabled"), false, 1), R.None, "wrong value type for flag -> None");
    Eq(R.Find("nope"), null, "unknown key");
    // overall = worst, None ignored
    Eq(R.Overall(new[] { R.Excellent, R.Good, R.VeryPoor, R.Medium }), R.VeryPoor, "overall worst");
    Eq(R.Overall(new[] { R.None, R.Good, R.None }), R.Good, "overall ignores None");
    Eq(R.Overall(new int[0]), R.None, "overall empty");
    Eq(R.Overall(new[] { R.Excellent, R.Excellent }), R.Excellent, "overall all excellent");
    var ratings = new Dictionary<string, int> { ["triangles"] = R.Poor, ["bones"] = R.Good, ["lights"] = R.Poor };
    Eq(string.Join(",", R.Limiting(ratings, R.Overall(ratings.Values))), "triangles,lights", "limiting stats");
    Eq(R.Limiting(new Dictionary<string, int> { ["a"] = R.Excellent }, R.Excellent).Count, 0, "no limiting when Excellent");
    // improvement hints
    Eq(R.NextImprovement(R.Find("triangles"), false, R.VeryPoor, out var lim, out var at) && lim == 70000 && at == R.Good, true, "pc tris VeryPoor -> <=70000 gives Good");
    Eq(R.NextImprovement(R.Find("skinned_meshes"), false, R.Medium, out lim, out at) && lim == 2 && at == R.Good, true, "pc skinned Medium -> <=2 Good");
    Eq(R.NextImprovement(R.Find("triangles"), false, R.Excellent, out lim, out at), false, "no improvement from Excellent");
    Eq(R.NextImprovement(R.Find("particle_trails_enabled"), false, R.Medium, out lim, out at), false, "no numeric improvement for flags");
    Eq(string.Join("x", R.BoundsSizeLimit(bounds, false, R.Poor)), "5x6x5", "bounds Poor size");
    Eq(R.Limit(R.Find("contacts"), true, R.Poor), 16.0, "mobile contacts Poor limit");
    // names
    Eq(R.Name(R.VeryPoor), "VeryPoor", "name VeryPoor");
    Eq(R.Parse("verypoor"), R.VeryPoor, "parse");
    Eq(R.Name(42), "None", "name out of range");
    // table sanity: 29 rows, unique keys and categories 4..32 minus Download/Uncompressed, thresholds non-decreasing
    Eq(R.All.Length, 29, "row count");
    Eq(R.All.Select(s => s.Key).Distinct().Count(), 29, "unique keys");
    Eq(R.All.Select(s => s.Category).Distinct().Count(), 29, "unique categories");
    Eq(R.All.All(s => s.Category >= 4 && s.Category <= 32), true, "categories in SDK range 4..32");
    foreach (var s in R.All) {
      if (s.Kind == R.Kind.Count || s.Kind == R.Kind.Megabytes)
        foreach (var t in new[] { s.Pc, s.Mobile })
          Eq(t.Length == 4 && t[0] <= t[1] && t[1] <= t[2] && t[2] <= t[3], true, "monotonic " + s.Key);
    }
    // texture memory formula (SDK MeshPerformanceScanner; examples from research/avatar-limits.md §4.6)
    Eq(R.TextureBytes(R.TextureKind.Texture2D, 2048, 2048, 1, 12, 12), 5592405L, "2048 DXT5 full mips");
    Eq(R.TextureBytes(R.TextureKind.Texture2D, 2048, 2048, 1, 12, 10), 2796202L, "2048 DXT1 full mips (banker's rounding of 0.5)");
    Eq(R.TextureBytes(R.TextureKind.Texture2D, 4096, 4096, 1, 13, 4), 89478484L, "4096 RGBA32 full mips");
    Eq(Math.Round(R.BytesToMegabytes(89478484L), 2), 85.33, "4096 RGBA32 MB");
    Eq(R.TextureBytes(R.TextureKind.Texture2D, 1, 1, 1, 1, 74), 16L, "unknown format RGBA64 -> 16 bpp");
    Eq(R.TextureBytes(R.TextureKind.Cubemap, 256, 256, 1, 1, 4), 262144L, "cubemap counts one face");
    Eq(R.TextureBytes(R.TextureKind.Texture2DArray, 256, 256, 4, 9, 4), 1048576L, "array: w*h*depth*bpp, no mips");
    Eq(R.TextureBytes(R.TextureKind.RenderTexture, 1024, 1024, 1, 1, 4), 0L, "render texture skipped");
    Eq(R.TextureBytes(R.TextureKind.Texture2D, 2048, 2048, 1, 0, 12), 0L, "zero mips");
    Eq(R.IsKnownFormat(12), true, "DXT5 known"); Eq(R.IsKnownFormat(74), false, "RGBA64 unknown");
    // parameter cost
    Eq(R.ParameterCost(2), 1, "bool cost"); Eq(R.ParameterCost(0), 8, "int cost"); Eq(R.ParameterCost(1), 8, "float cost");

    // --- table-driven boundary sweep over every numeric row, both platforms:
    //     value == threshold[i] => the best rank whose threshold is >= value; threshold[Poor] + epsilon => VeryPoor
    foreach (var s in R.All) {
      if (s.Kind != R.Kind.Count && s.Kind != R.Kind.Megabytes) continue;
      foreach (var mob in new[] { false, true }) {
        var t = mob ? s.Mobile : s.Pc;
        for (int i = 0; i < 4; i++) {
          int expect = R.Excellent + Array.FindIndex(t, x => x >= t[i]);
          Eq(R.Rate(s, mob, t[i]), expect, $"{(mob ? "mobile" : "pc")} {s.Key} == {t[i]}");
          if (s.Kind == R.Kind.Count) Eq(R.Rate(s, mob, (long)t[i]), expect, $"{(mob ? "mobile" : "pc")} {s.Key} == {t[i]} (long)");
        }
        double over = s.Kind == R.Kind.Megabytes ? t[3] + 0.01 : t[3] + 1;
        Eq(R.Rate(s, mob, over), R.VeryPoor, $"{(mob ? "mobile" : "pc")} {s.Key} {over} > Poor => VeryPoor");
        Eq(R.Rate(s, mob, 0), R.Excellent, $"{(mob ? "mobile" : "pc")} {s.Key} 0 => Excellent");
      }
    }
    // flags: disabled is always Excellent; enabled gets the first level that allows it, VeryPoor never (Poor allows it everywhere)
    foreach (var key in new[] { "particle_trails_enabled", "particle_collision_enabled" }) {
      Eq(Pc(key, true), R.Medium, "pc " + key + " on");
      Eq(Mob(key, true), R.Poor, "mobile " + key + " on");
      Eq(Pc(key, false), R.Excellent, "pc " + key + " off"); Eq(Mob(key, false), R.Excellent, "mobile " + key + " off");
    }
    // overall includes bounds (SDK: AABB counts towards Overall) and a single VeryPoor row decides it
    var mixed = new Dictionary<string, int> { ["triangles"] = R.Good, ["bounds_size"] = R.Rate(bounds, false, new[] { 3.0, 3.0, 3.0 }) };
    Eq(mixed["bounds_size"], R.VeryPoor, "bounds 6x6x6 => VeryPoor");
    Eq(R.Overall(mixed.Values), R.VeryPoor, "overall includes bounds");
    Eq(string.Join(",", R.Limiting(mixed, R.Overall(mixed.Values))), "bounds_size", "bounds is the limiting stat");
    // next improvement across equal thresholds (mobile skinned meshes: Excellent=Good=1, Medium=Poor=2)
    Eq(R.NextImprovement(R.Find("skinned_meshes"), true, R.VeryPoor, out lim, out at) && lim == 2 && at == R.Medium, true, "mobile skinned VeryPoor -> <=2 Medium");
    Eq(R.NextImprovement(R.Find("skinned_meshes"), true, R.Medium, out lim, out at) && lim == 1 && at == R.Excellent, true, "mobile skinned Medium -> <=1 Excellent");

    // --- independent transcription of research/avatar-limits.md §2 (PC) and §3 (Mobile) tables: Excellent, Good, Medium, Poor
    var pcTable = new Dictionary<string, double[]> {
      ["triangles"] = new double[] { 32000, 70000, 70000, 70000 }, ["texture_memory"] = new double[] { 40, 75, 110, 150 },
      ["skinned_meshes"] = new double[] { 1, 2, 8, 16 }, ["basic_meshes"] = new double[] { 4, 8, 16, 24 }, ["material_slots"] = new double[] { 4, 8, 16, 32 },
      ["physbone_components"] = new double[] { 4, 8, 16, 32 }, ["physbone_transforms"] = new double[] { 16, 64, 128, 256 },
      ["physbone_colliders"] = new double[] { 4, 8, 16, 32 }, ["physbone_collision_checks"] = new double[] { 32, 128, 256, 512 },
      ["contacts"] = new double[] { 8, 16, 24, 32 }, ["constraints"] = new double[] { 100, 250, 300, 350 }, ["constraint_depth"] = new double[] { 20, 50, 80, 100 },
      ["animators"] = new double[] { 1, 4, 16, 32 }, ["bones"] = new double[] { 75, 150, 256, 400 }, ["lights"] = new double[] { 0, 0, 0, 1 },
      ["particle_systems"] = new double[] { 0, 4, 8, 16 }, ["total_particles"] = new double[] { 0, 300, 1000, 2500 }, ["mesh_particle_polys"] = new double[] { 0, 1000, 2000, 5000 },
      ["trail_renderers"] = new double[] { 1, 2, 4, 8 }, ["line_renderers"] = new double[] { 1, 2, 4, 8 }, ["raycasts"] = new double[] { 1, 4, 8, 15 },
      ["cloths"] = new double[] { 0, 1, 1, 1 }, ["cloth_vertices"] = new double[] { 0, 50, 100, 200 }, ["physics_colliders"] = new double[] { 0, 1, 8, 8 },
      ["physics_rigidbodies"] = new double[] { 0, 1, 8, 8 }, ["audio_sources"] = new double[] { 1, 4, 8, 8 },
    };
    var mobileTable = new Dictionary<string, double[]> {
      ["triangles"] = new double[] { 7500, 10000, 15000, 20000 }, ["texture_memory"] = new double[] { 10, 18, 25, 40 },
      ["skinned_meshes"] = new double[] { 1, 1, 2, 2 }, ["basic_meshes"] = new double[] { 1, 1, 2, 2 }, ["material_slots"] = new double[] { 1, 1, 2, 4 },
      ["animators"] = new double[] { 1, 1, 1, 2 }, ["bones"] = new double[] { 75, 90, 150, 150 },
      ["physbone_components"] = new double[] { 0, 4, 6, 8 }, ["physbone_transforms"] = new double[] { 0, 16, 32, 64 },
      ["physbone_colliders"] = new double[] { 0, 4, 8, 16 }, ["physbone_collision_checks"] = new double[] { 0, 16, 32, 64 },
      ["contacts"] = new double[] { 2, 4, 8, 16 }, ["constraints"] = new double[] { 30, 60, 120, 150 }, ["constraint_depth"] = new double[] { 5, 15, 35, 50 },
      ["particle_systems"] = new double[] { 0, 0, 0, 2 }, ["total_particles"] = new double[] { 0, 0, 0, 200 }, ["mesh_particle_polys"] = new double[] { 0, 0, 0, 400 },
      ["trail_renderers"] = new double[] { 0, 0, 0, 1 }, ["line_renderers"] = new double[] { 0, 0, 0, 1 }, ["raycasts"] = new double[] { 1, 2, 4, 8 },
      ["lights"] = new double[] { 0, 0, 0, 0 }, ["cloths"] = new double[] { 0, 0, 0, 0 }, ["cloth_vertices"] = new double[] { 0, 0, 0, 0 },
      ["physics_colliders"] = new double[] { 0, 0, 0, 0 }, ["physics_rigidbodies"] = new double[] { 0, 0, 0, 0 }, ["audio_sources"] = new double[] { 0, 0, 0, 0 },
    };
    foreach (var kv in pcTable) Eq(string.Join(",", R.Find(kv.Key).Pc), string.Join(",", kv.Value), "pc table " + kv.Key);
    foreach (var kv in mobileTable) Eq(string.Join(",", R.Find(kv.Key).Mobile), string.Join(",", kv.Value), "mobile table " + kv.Key);
    Eq(pcTable.Count, R.All.Count(s => s.Kind == R.Kind.Count || s.Kind == R.Kind.Megabytes), "pc table covers every numeric row");
    Eq(mobileTable.Count, pcTable.Count, "mobile table covers every numeric row");
    // SDK AvatarPerformanceCategory ints (FieldCheck enum dump)
    var cats = new Dictionary<string, int> { ["triangles"] = 4, ["bounds_size"] = 5, ["skinned_meshes"] = 6, ["basic_meshes"] = 7, ["material_slots"] = 8,
      ["physbone_components"] = 9, ["physbone_transforms"] = 10, ["physbone_colliders"] = 11, ["physbone_collision_checks"] = 12, ["contacts"] = 13,
      ["animators"] = 14, ["bones"] = 15, ["lights"] = 16, ["particle_systems"] = 17, ["total_particles"] = 18, ["mesh_particle_polys"] = 19,
      ["particle_trails_enabled"] = 20, ["particle_collision_enabled"] = 21, ["trail_renderers"] = 22, ["line_renderers"] = 23, ["cloths"] = 24,
      ["cloth_vertices"] = 25, ["physics_colliders"] = 26, ["physics_rigidbodies"] = 27, ["audio_sources"] = 28, ["texture_memory"] = 29,
      ["constraints"] = 30, ["constraint_depth"] = 31, ["raycasts"] = 32 };
    foreach (var kv in cats) Eq(R.Find(kv.Key).Category, kv.Value, "category int " + kv.Key);
    // SDK AvatarPerformanceCategory member names (FieldCheck enum dump, SDK 3.10.5): the SDK is queried by name.
    var catNames = new Dictionary<string, string> { ["triangles"] = "PolyCount", ["bounds_size"] = "AABB", ["skinned_meshes"] = "SkinnedMeshCount",
      ["basic_meshes"] = "MeshCount", ["material_slots"] = "MaterialCount", ["physbone_components"] = "PhysBoneComponentCount",
      ["physbone_transforms"] = "PhysBoneTransformCount", ["physbone_colliders"] = "PhysBoneColliderCount",
      ["physbone_collision_checks"] = "PhysBoneCollisionCheckCount", ["contacts"] = "ContactCount", ["animators"] = "AnimatorCount",
      ["bones"] = "BoneCount", ["lights"] = "LightCount", ["particle_systems"] = "ParticleSystemCount", ["total_particles"] = "ParticleTotalCount",
      ["mesh_particle_polys"] = "ParticleMaxMeshPolyCount", ["particle_trails_enabled"] = "ParticleTrailsEnabled",
      ["particle_collision_enabled"] = "ParticleCollisionEnabled", ["trail_renderers"] = "TrailRendererCount", ["line_renderers"] = "LineRendererCount",
      ["cloths"] = "ClothCount", ["cloth_vertices"] = "ClothMaxVertices", ["physics_colliders"] = "PhysicsColliderCount",
      ["physics_rigidbodies"] = "PhysicsRigidbodyCount", ["audio_sources"] = "AudioSourceCount", ["texture_memory"] = "TextureMegabytes",
      ["constraints"] = "ConstraintsCount", ["constraint_depth"] = "ConstraintDepth", ["raycasts"] = "RaycastCount" };
    foreach (var kv in catNames) Eq(R.Find(kv.Key).SdkCategory, kv.Value, "category name " + kv.Key);
    Eq(catNames.Count, R.All.Length, "category names cover every row");
    Eq(R.SdkCategoryName(1), "Overall", "category name 1 = Overall");
    Eq(R.SdkCategoryName(R.Find("raycasts").Category), "RaycastCount", "raycasts int and name agree");
    Eq(R.SdkCategoryName(33), null, "category 33 (AvatarPerformanceCategoryCount) has no stat name");
    Eq(R.SdkCategoryName(-1), null, "negative category");
    // Ratings are parsed by name from the SDK enum's ToString().
    Eq(R.Parse("VeryPoor"), R.VeryPoor, "parse VeryPoor"); Eq(R.Parse("Excellent"), R.Excellent, "parse Excellent");
    Eq(R.Parse("7"), R.None, "parse undefined enum value -> None"); Eq(R.Parse(null), R.None, "parse null -> None");
    // --- docs "Minimum Displayed Performance Rank" table (independent transcription): bolded rows = whole avatar to Fallback
    var fallbackRows = new[] { "triangles", "texture_memory", "skinned_meshes", "basic_meshes", "material_slots", "bones" };
    foreach (var s in R.All) Eq(R.MinRankEffect(s.Key) != null, true, "min-rank effect defined for " + s.Key);
    foreach (var s in R.All) Eq(R.MinRankEffect(s.Key) == R.EffectFallback, fallbackRows.Contains(s.Key), "fallback row " + s.Key);
    Eq(R.MinRankEffect("bounds_size"), R.EffectNone, "bounds never blocks (docs)");
    foreach (var k in new[] { "physbone_components", "physbone_transforms", "physbone_colliders", "physbone_collision_checks", "contacts" })
      Eq(R.MinRankEffect(k), R.EffectPhysBonesContacts, "physbone/contact effect " + k);
    Eq(R.MinRankEffect("constraints"), R.EffectConstraints, "constraint count effect");
    Eq(R.MinRankEffect("constraint_depth"), R.EffectConstraints, "constraint depth effect");
    foreach (var k in new[] { "particle_systems", "total_particles", "mesh_particle_polys", "particle_trails_enabled", "particle_collision_enabled" })
      Eq(R.MinRankEffect(k), "particle_systems_removed", "particle effect " + k);
    Eq(R.MinRankEffect("animators"), "animators_removed", "animators effect (root animator kept)");
    Eq(R.MinRankEffect("nope"), null, "unknown key has no effect");
    // --- docs "Mobile Removed Components": exactly these six rows, and their SDK mobile thresholds are 0 everywhere
    var removed = new[] { "lights", "cloths", "cloth_vertices", "physics_colliders", "physics_rigidbodies", "audio_sources" };
    foreach (var s in R.All) Eq(R.IsMobileRemoved(s.Key), removed.Contains(s.Key), "mobile removed " + s.Key);
    foreach (var k in removed) { Eq(R.Find(k).Mobile.All(x => x == 0), true, "mobile thresholds 0 for " + k); Eq(Mob(k, 1), R.VeryPoor, "mobile " + k + " 1 => VeryPoor"); }
    Eq(R.DisplayName(R.VeryPoor), "Very Poor", "display name with space"); Eq(R.DisplayName(R.Good), "Good", "display name Good");
    Console.WriteLine(fails == 0 ? $"RANK TESTS OK ({passes})" : $"{fails} FAILED ({passes} passed)");
    return fails;
  }
}
