// Pure C# (no UnityEngine/UnityEditor) so CI can compile and test it with mono.
using System;
using System.Collections.Generic;

namespace Naoking.AvatarChecker
{
    public enum Rank { Excellent = 0, Good = 1, Medium = 2, Poor = 3, VeryPoor = 4 }

    public enum Platform { PC, Mobile }

    /// <summary>
    /// VRChat avatar performance rank limits (Excellent / Good / Medium / Poor; anything above Poor is Very Poor).
    /// Values follow VRChat's published "Avatar Performance Ranking System" table and the SDK 3.10.5 stat levels.
    /// Bounds size and raycasts are not ranked here. The SDK build panel is authoritative and VRChat may change the table.
    /// </summary>
    public static class PerformanceRank
    {
        public const string Triangles = "triangles";
        public const string TextureMemoryMB = "textureMemoryMB";
        public const string SkinnedMeshes = "skinnedMeshes";
        public const string BasicMeshes = "basicMeshes";
        public const string MaterialSlots = "materialSlots";
        public const string PhysBoneComponents = "physBoneComponents";
        public const string PhysBoneTransforms = "physBoneTransforms";
        public const string PhysBoneColliders = "physBoneColliders";
        public const string PhysBoneCollisionChecks = "physBoneCollisionChecks";
        public const string Contacts = "contacts";
        public const string Constraints = "constraints";
        public const string Animators = "animators";
        public const string Bones = "bones";
        public const string Lights = "lights";
        public const string ParticleSystems = "particleSystems";
        public const string TrailRenderers = "trailRenderers";
        public const string LineRenderers = "lineRenderers";
        public const string Cloths = "cloths";
        public const string PhysicsColliders = "physicsColliders";
        public const string Rigidbodies = "rigidbodies";
        public const string AudioSources = "audioSources";
        public const string ParticleTotalCount = "particleTotalCount";  // sum of maxParticles
        public const string ParticleMeshPolys = "particleMeshPolys";
        public const string ParticleTrails = "particleTrails";          // 0/1 flag
        public const string ParticleCollision = "particleCollision";    // 0/1 flag
        public const string ClothVertices = "clothVertices";
        public const string ConstraintDepth = "constraintDepth";
        public const string NonReadableMeshes = "nonReadableMeshes";    // any Read/Write-disabled mesh = Very Poor

        // metric -> limits for Excellent, Good, Medium, Poor
        static readonly Dictionary<string, double[]> PcLimits = new Dictionary<string, double[]>
        {
            { Triangles, new double[] { 32000, 70000, 70000, 70000 } },
            { TextureMemoryMB, new double[] { 40, 75, 110, 150 } },
            { SkinnedMeshes, new double[] { 1, 2, 8, 16 } },
            { BasicMeshes, new double[] { 4, 8, 16, 24 } },
            { MaterialSlots, new double[] { 4, 8, 16, 32 } },
            { PhysBoneComponents, new double[] { 4, 8, 16, 32 } },
            { PhysBoneTransforms, new double[] { 16, 64, 128, 256 } },
            { PhysBoneColliders, new double[] { 4, 8, 16, 32 } },
            { PhysBoneCollisionChecks, new double[] { 32, 128, 256, 512 } },
            { Contacts, new double[] { 8, 16, 24, 32 } },
            { Constraints, new double[] { 100, 250, 300, 350 } },
            { Animators, new double[] { 1, 4, 16, 32 } },
            { Bones, new double[] { 75, 150, 256, 400 } },
            { Lights, new double[] { 0, 0, 0, 1 } },
            { ParticleSystems, new double[] { 0, 4, 8, 16 } },
            { TrailRenderers, new double[] { 1, 2, 4, 8 } },
            { LineRenderers, new double[] { 1, 2, 4, 8 } },
            { Cloths, new double[] { 0, 1, 1, 1 } },
            { PhysicsColliders, new double[] { 0, 1, 8, 8 } },
            { Rigidbodies, new double[] { 0, 1, 8, 8 } },
            { AudioSources, new double[] { 1, 4, 8, 8 } },
            { ParticleTotalCount, new double[] { 0, 300, 1000, 2500 } },
            { ParticleMeshPolys, new double[] { 0, 1000, 2000, 5000 } },
            { ParticleTrails, new double[] { 0, 0, 1, 1 } },
            { ParticleCollision, new double[] { 0, 0, 1, 1 } },
            { ClothVertices, new double[] { 0, 50, 100, 200 } },
            { ConstraintDepth, new double[] { 20, 50, 80, 100 } },
            { NonReadableMeshes, new double[] { 0, 0, 0, 0 } },
        };

        static readonly Dictionary<string, double[]> MobileLimits = new Dictionary<string, double[]>
        {
            { Triangles, new double[] { 7500, 10000, 15000, 20000 } },
            { TextureMemoryMB, new double[] { 10, 18, 25, 40 } },
            { SkinnedMeshes, new double[] { 1, 1, 2, 2 } },
            { BasicMeshes, new double[] { 1, 1, 2, 2 } },
            { MaterialSlots, new double[] { 1, 1, 2, 4 } },
            { PhysBoneComponents, new double[] { 0, 4, 6, 8 } },
            { PhysBoneTransforms, new double[] { 0, 16, 32, 64 } },
            { PhysBoneColliders, new double[] { 0, 4, 8, 16 } },
            { PhysBoneCollisionChecks, new double[] { 0, 16, 32, 64 } },
            { Contacts, new double[] { 2, 4, 8, 16 } },
            { Constraints, new double[] { 30, 60, 120, 150 } },
            { Animators, new double[] { 1, 1, 1, 2 } },
            { Bones, new double[] { 75, 90, 150, 150 } },
            { ParticleSystems, new double[] { 0, 0, 0, 2 } },
            { TrailRenderers, new double[] { 0, 0, 0, 1 } },
            { LineRenderers, new double[] { 0, 0, 0, 1 } },
            { ParticleTotalCount, new double[] { 0, 0, 0, 200 } },
            { ParticleMeshPolys, new double[] { 0, 0, 0, 400 } },
            { ParticleTrails, new double[] { 0, 0, 0, 1 } },
            { ParticleCollision, new double[] { 0, 0, 0, 1 } },
            { ConstraintDepth, new double[] { 5, 15, 35, 50 } },
            { NonReadableMeshes, new double[] { 0, 0, 0, 0 } },
        };

        static Dictionary<string, double[]> Table(Platform p) { return p == Platform.PC ? PcLimits : MobileLimits; }

        /// <summary>True if the metric is ranked on this platform (e.g. lights are not ranked on mobile).</summary>
        public static bool IsRanked(Platform p, string metric) { return Table(p).ContainsKey(metric); }

        /// <summary>Limit for the given rank (Excellent..Poor), or null if not ranked.</summary>
        public static double? Limit(Platform p, string metric, Rank r)
        {
            double[] l;
            if (r == Rank.VeryPoor || !Table(p).TryGetValue(metric, out l)) return null;
            return l[(int)r];
        }

        public static Rank Rate(Platform p, string metric, double value)
        {
            double[] l;
            if (!Table(p).TryGetValue(metric, out l)) return Rank.Excellent;
            for (int i = 0; i < l.Length; i++)
                if (value <= l[i]) return (Rank)i;
            return Rank.VeryPoor;
        }

        /// <summary>Overall rank = the worst rank of any metric, like the SDK.</summary>
        public static Rank Overall(Platform p, IDictionary<string, double> stats)
        {
            var worst = Rank.Excellent;
            foreach (var kv in stats)
            {
                var r = Rate(p, kv.Key, kv.Value);
                if (r > worst) worst = r;
            }
            return worst;
        }

        public static string Label(Rank r)
        {
            switch (r)
            {
                case Rank.Excellent: return "Excellent";
                case Rank.Good: return "Good";
                case Rank.Medium: return "Medium";
                case Rank.Poor: return "Poor";
                default: return "Very Poor";
            }
        }

        public static IEnumerable<string> Metrics(Platform p) { return Table(p).Keys; }
    }
}
