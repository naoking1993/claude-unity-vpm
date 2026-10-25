using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    // Hierarchy scan, the tool's own performance estimator (SDK 3.10.5 counting rules, research/avatar-limits.md §4/§15.2),
    // the per-component breakdown, and the merge of SDK values + estimator into data.stats / data.ranks.
    public static partial class CctAvatarAudit
    {
        // =====================================================================================================================
        // Hierarchy scan (one pass)
        // =====================================================================================================================

        sealed class HierarchyScan
        {
            public Transform Root;
            public readonly List<Transform> Transforms = new List<Transform>();
            public readonly HashSet<Transform> EditorOnly = new HashSet<Transform>();
            public readonly List<Component> Components = new List<Component>();
            public readonly Dictionary<string, int> TypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> ToolFamilies = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly List<KeyValuePair<GameObject, int>> MissingScriptObjects = new List<KeyValuePair<GameObject, int>>();
            public int MissingScriptTotal;
            public bool RootAboveEditorOnly;

            public bool IsEditorOnly(Component c) => c != null && EditorOnly.Contains(c.transform);
        }

        struct ScanItem
        {
            public Transform T;
            public bool EditorOnly;
        }

        /// <summary>
        /// Walks every transform under <paramref name="root"/> (inactive included, iterative so deep rigs cannot overflow the stack).
        /// EditorOnly follows the SDK rule: the GameObject or ANY ancestor - also above the avatar root - is tagged EditorOnly.
        /// Missing scripts are counted with GameObjectUtility.GetMonoBehavioursWithMissingScriptCount before anything filters nulls.
        /// </summary>
        static HierarchyScan ScanHierarchy(Transform root)
        {
            var scan = new HierarchyScan { Root = root };
            for (var p = root.parent; p != null; p = p.parent)
                if (p.CompareTag("EditorOnly")) { scan.RootAboveEditorOnly = true; break; }
            var stack = new Stack<ScanItem>();
            stack.Push(new ScanItem { T = root, EditorOnly = scan.RootAboveEditorOnly || root.CompareTag("EditorOnly") });
            var buffer = new List<Component>();
            while (stack.Count > 0)
            {
                var item = stack.Pop();
                var t = item.T;
                if (t == null) continue;
                scan.Transforms.Add(t);
                if (item.EditorOnly) scan.EditorOnly.Add(t);
                var go = t.gameObject;
                int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (missing > 0)
                {
                    scan.MissingScriptObjects.Add(new KeyValuePair<GameObject, int>(go, missing));
                    scan.MissingScriptTotal += missing;
                }
                go.GetComponents(buffer);
                foreach (var c in buffer)
                {
                    if (c == null) continue;
                    scan.Components.Add(c);
                    var ti = Info(c.GetType());
                    Inc(scan.TypeCounts, ti.FullName);
                    if (ti.Family != null) Inc(scan.ToolFamilies, ti.Family);
                }
                for (int i = t.childCount - 1; i >= 0; i--)
                {
                    var ch = t.GetChild(i);
                    stack.Push(new ScanItem { T = ch, EditorOnly = item.EditorOnly || ch.CompareTag("EditorOnly") });
                }
            }
            return scan;
        }

        // =====================================================================================================================
        // Per-type classification (cached)
        // =====================================================================================================================

        sealed class TypeInfo
        {
            public string FullName, Name;
            public bool PhysBone, PbCollider, Contact, ContactReceiver, VrcConstraint, Raycast, Station, HeadChop, Pipeline, Descriptor;
            public bool EditorOnlyIface, DynamicBone, SilentlyRemoved, AllowedPc, AllowedMobile;
            public string Family;
            public PropertyInfo GroupIndex;
        }

        static readonly Dictionary<Type, TypeInfo> TypeInfos = new Dictionary<Type, TypeInfo>();

        // Avatar component whitelist, SDK 3.10.5: com.vrchat.base Runtime/VRCSDK/Dependencies/VRChat/Scripts/Validation/
        // AvatarValidation.cs ComponentTypeWhiteListCommon + ComponentTypeWhiteListSdk3 (combined in com.vrchat.avatars
        // Runtime/VRCSDK/SDK3A/AvatarValidation.cs). Entries under #if UNITY_STANDALONE are PC-only. Derived types are allowed
        // (ValidationUtils.AddDerivedClasses), so a type is allowed when it or a base type is listed.
        static readonly HashSet<string> WhitelistPcOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "RootMotion.FinalIK.IKExecutionOrder", "RootMotion.FinalIK.VRIK", "RootMotion.FinalIK.FullBodyBipedIK", "RootMotion.FinalIK.LimbIK",
            "RootMotion.FinalIK.AimIK", "RootMotion.FinalIK.BipedIK", "RootMotion.FinalIK.GrounderIK", "RootMotion.FinalIK.GrounderFBBIK",
            "RootMotion.FinalIK.GrounderVRIK", "RootMotion.FinalIK.GrounderQuadruped", "RootMotion.FinalIK.TwistRelaxer",
            "RootMotion.FinalIK.ShoulderRotator", "RootMotion.FinalIK.FBBIKArmBending", "RootMotion.FinalIK.FBBIKHeadEffector",
            "RootMotion.FinalIK.FABRIK", "RootMotion.FinalIK.FABRIKChain", "RootMotion.FinalIK.FABRIKRoot", "RootMotion.FinalIK.CCDIK",
            "RootMotion.FinalIK.RotationLimit", "RootMotion.FinalIK.RotationLimitHinge", "RootMotion.FinalIK.RotationLimitPolygonal",
            "RootMotion.FinalIK.RotationLimitSpline",
            "UnityEngine.Cloth", "UnityEngine.Light", "UnityEngine.BoxCollider", "UnityEngine.SphereCollider", "UnityEngine.CapsuleCollider",
            "UnityEngine.Rigidbody", "UnityEngine.Joint",
            "UnityEngine.Animations.AimConstraint", "UnityEngine.Animations.LookAtConstraint", "UnityEngine.Animations.ParentConstraint",
            "UnityEngine.Animations.PositionConstraint", "UnityEngine.Animations.RotationConstraint", "UnityEngine.Animations.ScaleConstraint",
            "UnityEngine.Camera", "UnityEngine.AudioSource", "ONSPAudioSource",
            "VRC.SDK3.Avatars.Components.VRCSpatialAudioSource",
        };

        static readonly HashSet<string> WhitelistAll = new HashSet<string>(StringComparer.Ordinal)
        {
            "VRC.Core.PipelineSaver", "VRC.Core.PipelineManager", "UnityEngine.Transform", "UnityEngine.Animator", "UnityEngine.SkinnedMeshRenderer",
            "LimbIK", "LoadingAvatarTextureAnimation", "UnityEngine.MeshFilter", "UnityEngine.MeshRenderer", "UnityEngine.Animation",
            "UnityEngine.ParticleSystem", "UnityEngine.ParticleSystemRenderer", "UnityEngine.TrailRenderer", "UnityEngine.FlareLayer",
            "UnityEngine.GUILayer", "UnityEngine.LineRenderer", "RealisticEyeMovements.EyeAndHeadAnimator", "RealisticEyeMovements.LookTargetController",
            "VRC.SDK3.VRCTestMarker", "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor", "VRC.SDK3.Avatars.Components.VRCStation",
            "VRC.SDK3.Avatars.Components.VRCImpostorSettings", "VRC.SDK3.Avatars.Components.VRCImpostorEnvironment",
            "VRC.SDK3.Avatars.Components.VRCHeadChop", "VRC.SDK3.Avatars.Components.VRCRaycast",
            "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone", "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider",
            "VRC.SDK3.Dynamics.Constraint.Components.VRCAimConstraint", "VRC.SDK3.Dynamics.Constraint.Components.VRCLookAtConstraint",
            "VRC.SDK3.Dynamics.Constraint.Components.VRCParentConstraint", "VRC.SDK3.Dynamics.Constraint.Components.VRCPositionConstraint",
            "VRC.SDK3.Dynamics.Constraint.Components.VRCRotationConstraint", "VRC.SDK3.Dynamics.Constraint.Components.VRCScaleConstraint",
            "VRC.SDK3.Dynamics.Contact.Components.VRCContactSender", "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver",
        };

        /// <summary>Build-time tool families recognised by namespace prefix (reported in notes.build_tools).</summary>
        static readonly KeyValuePair<string, string>[] Families =
        {
            new KeyValuePair<string, string>("nadena.dev.modular_avatar", "modular_avatar"),
            new KeyValuePair<string, string>("nadena.dev.ndmf", "ndmf"),
            new KeyValuePair<string, string>("Anatawa12.AvatarOptimizer", "avatar_optimizer"),
            new KeyValuePair<string, string>("VF.", "vrcfury"),
            new KeyValuePair<string, string>("net.rs64.TexTransTool", "tex_trans_tool"),
            new KeyValuePair<string, string>("jp.lilxyzw.lilycalinventory", "lilycal_inventory"),
            new KeyValuePair<string, string>("KRT.VRCQuestTools", "vrc_quest_tools"),
        };

        static TypeInfo Info(Type t)
        {
            if (TypeInfos.TryGetValue(t, out var ti)) return ti;
            ti = new TypeInfo { FullName = t.FullName ?? t.Name, Name = t.Name };
            var chain = new HashSet<string>(StringComparer.Ordinal);
            for (var c = t; c != null && c != typeof(Object) && c != typeof(object); c = c.BaseType) chain.Add(c.FullName ?? c.Name);
            ti.PhysBone = chain.Contains(TPhysBone);
            ti.PbCollider = chain.Contains(TPhysBoneColliderBase);
            ti.Contact = chain.Contains(TContactBase);
            ti.ContactReceiver = chain.Contains(CctVrcTypes.ContactReceiver);
            ti.Raycast = chain.Contains(TRaycast);
            ti.Station = chain.Contains(TStation);
            ti.HeadChop = chain.Contains(THeadChop);
            ti.Pipeline = chain.Contains(CctVrcTypes.PipelineManager);
            ti.Descriptor = chain.Contains(CctVrcTypes.AvatarDescriptor);
            ti.DynamicBone = ti.FullName == "DynamicBone" || ti.FullName == "DynamicBoneCollider";
            ti.SilentlyRemoved = ti.Name == "PipelineSaver" || ti.Name == "ParentChangeDetector";
            Type[] interfaces;
            try { interfaces = t.GetInterfaces(); }
            catch (Exception) { interfaces = Type.EmptyTypes; }
            foreach (var i in interfaces)
            {
                if (i.FullName == TIEditorOnly) ti.EditorOnlyIface = true;
                if (i.FullName == TIVrcConstraint)
                {
                    ti.VrcConstraint = true;
                    ti.GroupIndex = i.GetProperty("LatestValidExecutionGroupIndex", BindingFlags.Public | BindingFlags.Instance);
                }
            }
            ti.AllowedMobile = chain.Any(WhitelistAll.Contains);
            ti.AllowedPc = ti.AllowedMobile || chain.Any(WhitelistPcOnly.Contains);
            var ns = t.Namespace ?? "";
            foreach (var f in Families)
            {
                var prefix = f.Key.TrimEnd('.');
                if (ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal))
                {
                    ti.Family = f.Value;
                    break;
                }
            }
            TypeInfos[t] = ti;
            return ti;
        }

        // =====================================================================================================================
        // Estimator
        // =====================================================================================================================

        sealed class TexEntry
        {
            public Texture Texture;
            public Ranks.TextureKind Kind;
            public int Width, Height, Depth, Mips, Format;
            public string FormatName;
            public long Bytes;
            public readonly HashSet<Material> UsedBy = new HashSet<Material>();
            public TextureImporter Importer;
            public string AssetPath;
        }

        sealed class PbEntry
        {
            public Component Component;
            public Transform RootTransform;
            public int Transforms, Colliders, CollisionChecks, MaxDepth;
            public bool Truncated;
            public Dictionary<string, object> Item;
        }

        /// <summary>Breakdown list entry with its sort key.</summary>
        struct Row
        {
            public double Key;
            public Dictionary<string, object> Data;
            public Row(double key, Dictionary<string, object> data) { Key = key; Data = data; }
        }

        sealed class Est
        {
            public readonly Dictionary<string, object> Values = new Dictionary<string, object>(StringComparer.Ordinal);
            public Vector3 BoundsCenter, BoundsExtents;
            public int ContactsAll, ContactsEditorOnly, GlobalCollidersAll, HeadChops, PipelineManagers, UnityConstraintsAll;
            public long ParticleMaterials;
            public readonly List<Renderer> Renderers = new List<Renderer>();     // non-EditorOnly (SDK renderer list)
            public readonly List<Renderer> AllRenderers = new List<Renderer>();  // EditorOnly included (shader whitelist check)
            public readonly HashSet<Mesh> UsedMeshes = new HashSet<Mesh>();       // SMR, MeshFilter, ParticleSystemRenderer meshes (non-EditorOnly)
            public readonly Dictionary<Texture, TexEntry> Textures = new Dictionary<Texture, TexEntry>();
            public readonly Dictionary<Material, int> Materials = new Dictionary<Material, int>(); // -> renderer count
            public readonly List<PbEntry> PhysBones = new List<PbEntry>();
            public readonly List<ParticleSystem> Particles = new List<ParticleSystem>();
            public readonly List<AudioSource> AudioSources = new List<AudioSource>();
            public readonly List<TrailRenderer> Trails = new List<TrailRenderer>();
            public readonly List<Component> UnityConstraints = new List<Component>();
            public readonly Dictionary<string, List<Row>> Lists = new Dictionary<string, List<Row>>(StringComparer.Ordinal);

            public void Add(string list, double key, Dictionary<string, object> data)
            {
                if (!Lists.TryGetValue(list, out var l)) Lists[list] = l = new List<Row>();
                l.Add(new Row(key, data));
            }

            public Dictionary<string, object> Breakdown(int topN)
            {
                var d = new Dictionary<string, object>();
                foreach (var kv in Lists.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    var sorted = kv.Value.OrderByDescending(r => r.Key).ToList();
                    var entry = new Dictionary<string, object>
                    {
                        ["total"] = sorted.Count,
                        ["items"] = sorted.Take(topN).Select(r => (object)r.Data).ToList(),
                    };
                    if (sorted.Count > topN) entry["omitted"] = sorted.Count - topN;
                    d[kv.Key] = entry;
                }
                return d;
            }
        }

        static readonly Dictionary<Mesh, long> MeshTriangleCache = new Dictionary<Mesh, long>();

        /// <summary>SDK MeshUtils rule: per sub-mesh GetIndexCount / 2 for Quads, / 3 otherwise (integer division). No Read/Write needed.</summary>
        static long MeshTriangles(Mesh mesh)
        {
            if (mesh == null) return 0;
            if (MeshTriangleCache.TryGetValue(mesh, out var cached)) return cached;
            long tris = 0;
            int sub = mesh.subMeshCount;
            for (int i = 0; i < sub; i++)
            {
                long idx = mesh.GetIndexCount(i);
                tris += mesh.GetTopology(i) == MeshTopology.Quads ? idx / 2 : idx / 3;
            }
            MeshTriangleCache[mesh] = tris;
            return tris;
        }

        static Mesh RendererMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            if (r is MeshRenderer)
            {
                var mf = r.GetComponent<MeshFilter>();
                return mf != null ? mf.sharedMesh : null;
            }
            return null;
        }

        /// <summary>
        /// Computes every ranked stat with the SDK 3.10.5 counting rules plus per-component breakdown rows. Never modifies
        /// anything (PhysBone chains are walked with our own traversal instead of VRCPhysBoneBase.InitTransforms).
        /// </summary>
        static Est Estimate(Ctx ctx, HierarchyScan scan)
        {
            MeshTriangleCache.Clear();
            var e = new Est();
            var root = scan.Root;
            var lodGroups = new List<LODGroup>();
            var physBones = new List<Component>();
            var colliders = new List<Component>();
            var contacts = new List<Component>();
            var vrcConstraints = new List<Component>();
            var unityConstraints = new List<Component>();
            var lights = new List<Light>();
            var animators = new List<Component>();
            var cloths = new List<Cloth>();
            var physColliders = new List<Collider>();
            var rigidbodies = new List<Rigidbody>();
            var lines = new List<LineRenderer>();
            int raycasts = 0;

            foreach (var c in scan.Components)
            {
                var ti = Info(c.GetType());
                bool eo = scan.IsEditorOnly(c);
                if (c is Renderer rend)
                {
                    e.AllRenderers.Add(rend);
                    if (!eo) e.Renderers.Add(rend);
                }
                if (c is LODGroup lg) lodGroups.Add(lg); // SDK: LOD groups are not EditorOnly-filtered
                if (ti.Contact) contacts.Add(c);           // SDK: contacts are not EditorOnly-filtered
                if (ti.PbCollider)
                {
                    if (!eo) colliders.Add(c);
                    using (var so = new SerializedObject(c))
                        if (CctSerialized.GetInt(so.FindProperty("globalCollision")) != 0) e.GlobalCollidersAll++;
                }
                if (c is IConstraint) e.UnityConstraintsAll++;
                if (ti.HeadChop) e.HeadChops++;
                if (ti.Pipeline) e.PipelineManagers++;
                if (eo) continue;
                if (ti.PhysBone) physBones.Add(c);
                else if (ti.VrcConstraint) vrcConstraints.Add(c);
                else if (ti.Raycast) raycasts++;
                switch (c)
                {
                    case IConstraint _: unityConstraints.Add(c); break;
                    case Light l: lights.Add(l); break;
                    case Animator _: animators.Add(c); break;
                    case Animation _: animators.Add(c); break;
                    case Cloth cl: cloths.Add(cl); break;
                    case Collider col: physColliders.Add(col); break;
                    case Rigidbody rb: rigidbodies.Add(rb); break;
                    case AudioSource a: e.AudioSources.Add(a); break;
                    case ParticleSystem ps: e.Particles.Add(ps); break;
                    case TrailRenderer tr: e.Trails.Add(tr); break;
                    case LineRenderer lr: lines.Add(lr); break;
                }
            }
            e.UnityConstraints.AddRange(unityConstraints);

            EstimateMeshes(ctx, e, root, lodGroups);
            EstimateTextures(ctx, e);
            EstimatePhysBones(ctx, e, physBones, colliders);
            EstimateContacts(ctx, scan, e, contacts);
            EstimateConstraints(ctx, e, vrcConstraints, unityConstraints);
            EstimateParticlesAndLights(ctx, e, lights);

            e.Values["trail_renderers"] = (long)e.Trails.Count;
            e.Values["line_renderers"] = (long)lines.Count;
            // Material slots: mesh sub-meshes (EstimateMeshes) + 1 per counted particle system with a renderer (+1 with trails)
            // + 1 per trail renderer + 1 per line renderer (SDK Particle/Trail/LineRendererPerformanceScanner).
            e.Values["material_slots"] = (long)e.Values["material_slots"] + e.ParticleMaterials + e.Trails.Count + lines.Count;
            e.Values["raycasts"] = (long)raycasts;
            e.Values["animators"] = (long)animators.Count;
            foreach (var t in e.Trails) e.Add("trail_line_renderers", 1, Row("trail", t));
            foreach (var l in lines) e.Add("trail_line_renderers", 1, Row("line", l));
            foreach (var a in animators)
            {
                var controller = a is Animator an ? an.runtimeAnimatorController : null;
                var row = Row(a is Animator ? "Animator" : "Animation", a);
                row["is_root"] = a.transform == root;
                if (controller != null) row["controller"] = AssetPathOrName(controller);
                e.Add("animators", a.transform == root ? 0 : 1, row);
            }

            long clothVerts = 0;
            foreach (var cl in cloths)
            {
                var n = cl.coefficients?.Length ?? 0;
                clothVerts += n;
                var row = Row("Cloth", cl);
                row["vertices"] = n;
                e.Add("cloth", n, row);
            }
            e.Values["cloths"] = (long)cloths.Count;
            e.Values["cloth_vertices"] = Math.Min(clothVerts, int.MaxValue);

            // SDK PhysicsPerformanceScanner: colliders/rigidbodies on a GameObject that also has a VRCStation are not counted.
            int colliderCount = 0, rigidbodyCount = 0;
            foreach (var col in physColliders)
            {
                if (HasStation(col)) continue;
                colliderCount++;
                e.Add("physics", 1, Row(col.GetType().Name, col));
            }
            foreach (var rb in rigidbodies)
            {
                if (HasStation(rb)) continue;
                rigidbodyCount++;
                e.Add("physics", 1, Row("Rigidbody", rb));
            }
            e.Values["physics_colliders"] = (long)colliderCount;
            e.Values["physics_rigidbodies"] = (long)rigidbodyCount;

            e.Values["audio_sources"] = (long)e.AudioSources.Count;
            foreach (var a in e.AudioSources)
            {
                var row = Row("AudioSource", a);
                if (a.clip != null)
                {
                    row["clip"] = AssetPathOrName(a.clip);
                    row["load_type"] = a.clip.loadType.ToString();
                    row["load_in_background"] = a.clip.loadInBackground;
                }
                e.Add("audio", 1, row);
            }
            return e;

            Dictionary<string, object> Row(string type, Component comp) => new Dictionary<string, object>
            {
                ["path"] = ctx.PathOf(comp),
                ["type"] = type,
                ["active"] = CctTargets.IsEffectivelyActive(comp.transform),
            };
        }

        static bool HasStation(Component c)
        {
            foreach (var other in c.GetComponents<Component>())
                if (other != null && Info(other.GetType()).Station) return true;
            return false;
        }

        static void EstimateMeshes(Ctx ctx, Est e, Transform root, List<LODGroup> lodGroups)
        {
            // Triangles: per LOD group the largest LOD level counts and its renderers are excluded from the plain sum (SDK
            // MeshPerformanceScanner). Only triangles use the LOD maximum; every other stat counts all LOD levels.
            long tris = 0;
            var inLod = new HashSet<Renderer>();
            foreach (var lg in lodGroups)
            {
                if (lg == null) continue;
                LOD[] lods;
                try { lods = lg.GetLODs(); }
                catch (Exception) { continue; }
                long max = 0;
                foreach (var lod in lods)
                {
                    long sum = 0;
                    if (lod.renderers == null) continue;
                    foreach (var r in lod.renderers)
                    {
                        if (r == null) continue;
                        inLod.Add(r);
                        sum += MeshTriangles(RendererMesh(r));
                    }
                    max = Math.Max(max, sum);
                }
                tris += max;
                e.Add("lod_groups", max, new Dictionary<string, object>
                {
                    ["path"] = ctx.PathOf(lg), ["levels"] = lods.Length, ["counted_triangles"] = max,
                });
            }

            var aabb = new Bounds(root.position, Vector3.zero);
            int skinned = 0, basic = 0;
            long materials = 0;
            var bones = new HashSet<Transform>();
            var reach = new List<KeyValuePair<Renderer, float>>();
            foreach (var r in e.Renderers)
            {
                var smr = r as SkinnedMeshRenderer;
                bool isMesh = r is MeshRenderer;
                if (smr == null && !isMesh) continue;
                var mesh = RendererMesh(r);
                long rt = MeshTriangles(mesh);
                if (!inLod.Contains(r)) tris += rt;
                int slots = mesh != null ? mesh.subMeshCount : 0;
                materials += slots;
                int boneCount = 0;
                if (smr != null)
                {
                    skinned++;
                    var bs = smr.bones;
                    if (bs != null)
                    {
                        boneCount = bs.Length;
                        foreach (var b in bs) if (b != null) bones.Add(b);
                    }
                }
                else basic++;
                if (mesh != null)
                {
                    e.UsedMeshes.Add(mesh);
                    var b = r.bounds;
                    aabb.Encapsulate(b);
                    var far = Vector3.Max(Abs(b.max - root.position), Abs(b.min - root.position));
                    reach.Add(new KeyValuePair<Renderer, float>(r, Mathf.Max(far.x, Mathf.Max(far.y, far.z))));
                }
                var row = new Dictionary<string, object>
                {
                    ["path"] = ctx.PathOf(r),
                    ["renderer"] = smr != null ? "skinned" : "mesh",
                    ["mesh"] = mesh != null ? AssetPathOrName(mesh) : null,
                    ["triangles"] = rt,
                    ["material_slots"] = slots,
                    ["materials"] = r.sharedMaterials.Length,
                    ["active"] = CctTargets.IsEffectivelyActive(r.transform) && r.enabled,
                };
                if (mesh != null)
                {
                    row["vertices"] = mesh.vertexCount;
                    row["blendshapes"] = mesh.blendShapeCount;
                    row["readable"] = mesh.isReadable;
                }
                if (smr != null) row["bones"] = boneCount;
                if (inLod.Contains(r)) row["in_lod_group"] = true;
                e.Add("meshes", rt, row);
            }
            e.Values["triangles"] = Math.Min(tris, int.MaxValue);
            e.Values["skinned_meshes"] = (long)skinned;
            e.Values["basic_meshes"] = (long)basic;
            e.Values["bones"] = (long)bones.Count;

            aabb.center -= root.position;
            e.BoundsCenter = aabb.center;
            e.BoundsExtents = aabb.extents;
            e.Values["bounds_size"] = new double[] { aabb.extents.x, aabb.extents.y, aabb.extents.z };
            foreach (var kv in reach.OrderByDescending(k => k.Value).Take(5))
                e.Add("bounds_reach", kv.Value, new Dictionary<string, object> { ["path"] = ctx.PathOf(kv.Key), ["max_offset_m"] = Round(kv.Value) });

            // Particle / trail / line renderers add their own material slots later.
            e.Values["material_slots"] = materials;

            // Materials and shaders over ALL non-EditorOnly renderers (particles/trails/lines included, as for texture memory).
            var mats = new List<Material>();
            foreach (var r in e.Renderers)
            {
                r.GetSharedMaterials(mats);
                foreach (var m in mats)
                {
                    if (m == null) continue;
                    e.Materials.TryGetValue(m, out var n);
                    e.Materials[m] = n + 1;
                }
            }
            var shaders = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in e.Materials)
            {
                var shaderName = kv.Key.shader != null ? kv.Key.shader.name : "(none)";
                Inc(shaders, shaderName);
                e.Add("materials", kv.Value, new Dictionary<string, object>
                {
                    ["asset"] = AssetPathOrName(kv.Key), ["shader"] = shaderName, ["used_by_renderers"] = kv.Value,
                });
            }
            foreach (var kv in shaders)
                e.Add("shaders", kv.Value, new Dictionary<string, object> { ["name"] = kv.Key, ["materials"] = kv.Value });
        }

        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        static MethodInfo _storageSizeMethod;
        static bool _storageSizeLooked;

        /// <summary>UnityEditor.TextureUtil.GetStorageMemorySizeLong (internal; the size the texture inspector shows). Null when unavailable.</summary>
        static long? StorageBytes(Texture t)
        {
            if (!_storageSizeLooked)
            {
                _storageSizeLooked = true;
                try
                {
                    var type = typeof(Editor).Assembly.GetType("UnityEditor.TextureUtil", false);
                    _storageSizeMethod = type?.GetMethod("GetStorageMemorySizeLong", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null, new[] { typeof(Texture) }, null);
                }
                catch (Exception) { _storageSizeMethod = null; }
            }
            if (_storageSizeMethod == null || t == null) return null;
            try { return Convert.ToInt64(_storageSizeMethod.Invoke(null, new object[] { t })); }
            catch (Exception) { return null; }
        }

        static void EstimateTextures(Ctx ctx, Est e)
        {
            var ids = new List<int>();
            long total = 0;
            foreach (var m in e.Materials.Keys)
            {
                ids.Clear();
                try { m.GetTexturePropertyNameIDs(ids); }
                catch (Exception) { continue; }
                foreach (var id in ids)
                {
                    Texture t;
                    try { t = m.GetTexture(id); }
                    catch (Exception) { continue; }
                    if (t == null) continue;
                    if (!e.Textures.TryGetValue(t, out var te))
                    {
                        te = DescribeTexture(t);
                        e.Textures[t] = te;
                        total += te.Bytes;
                    }
                    te.UsedBy.Add(m);
                }
            }
            e.Values["texture_memory"] = (double)Ranks.BytesToMegabytes(total);
            foreach (var te in e.Textures.Values)
            {
                var row = new Dictionary<string, object>
                {
                    ["asset"] = AssetPathOrName(te.Texture),
                    ["type"] = te.Kind.ToString(),
                    ["width"] = te.Width,
                    ["height"] = te.Height,
                    ["format"] = te.FormatName,
                    ["mips"] = te.Mips,
                    ["memory_mb"] = Round(Ranks.BytesToMegabytes(te.Bytes), 3),
                    ["used_by_materials"] = te.UsedBy.Count,
                };
                if (te.Depth > 1) row["depth"] = te.Depth;
                if (!Ranks.IsKnownFormat(te.Format) && (te.Kind != Ranks.TextureKind.RenderTexture && te.Kind != Ranks.TextureKind.Other))
                    row["note"] = "SDK の表に無い形式のため 16 byte/pixel で計算 (SDK と同じ)";
                if (te.Importer != null)
                {
                    row["max_size"] = te.Importer.maxTextureSize;
                    row["compression"] = te.Importer.textureCompression.ToString();
                    row["crunched"] = te.Importer.crunchedCompression;
                    row["streaming_mips"] = te.Importer.streamingMipmaps;
                }
                e.Add("textures", te.Bytes, row);
            }
        }

        static TexEntry DescribeTexture(Texture t)
        {
            var te = new TexEntry { Texture = t, Width = t.width, Height = t.height, Depth = 1 };
            try { te.Mips = t.mipmapCount; } catch (Exception) { te.Mips = 1; }
            switch (t)
            {
                case Texture2D t2: te.Kind = Ranks.TextureKind.Texture2D; te.Format = (int)t2.format; te.FormatName = t2.format.ToString(); break;
                case Cubemap cm: te.Kind = Ranks.TextureKind.Cubemap; te.Format = (int)cm.format; te.FormatName = cm.format.ToString(); break;
                case Texture2DArray ta: te.Kind = Ranks.TextureKind.Texture2DArray; te.Format = (int)ta.format; te.FormatName = ta.format.ToString(); te.Depth = ta.depth; break;
                case Texture3D t3: te.Kind = Ranks.TextureKind.Texture3D; te.Format = (int)t3.format; te.FormatName = t3.format.ToString(); te.Depth = t3.depth; break;
                case RenderTexture _: te.Kind = Ranks.TextureKind.RenderTexture; te.FormatName = "RenderTexture"; break;
                default: te.Kind = Ranks.TextureKind.Other; te.FormatName = t.GetType().Name; break;
            }
            te.Bytes = Ranks.TextureBytes(te.Kind, te.Width, te.Height, te.Depth, te.Mips, te.Format);
            te.AssetPath = AssetDatabase.GetAssetPath(t);
            if (!string.IsNullOrEmpty(te.AssetPath)) te.Importer = AssetImporter.GetAtPath(te.AssetPath) as TextureImporter;
            return te;
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // PhysBones (SDK PhysBonePerformanceScanner + VRCPhysBoneBase.InitTransforms rules, re-implemented read-only)
        // ---------------------------------------------------------------------------------------------------------------------

        const int MaxPhysBoneWalk = 100000;
        const int PhysBoneChainLimit = 256; // PhysBoneManager.MAX_TRANSFORMS_PER_CHAIN (SDK 3.10.5)

        static readonly string[] AdvancedBoolNames = { "False", "True", "Other" }; // VRCPhysBoneBase.AdvancedBool (FieldCheck)
        static readonly string[] MultiChildNames = { "Ignore", "First", "Average" }; // VRCPhysBoneBase.MultiChildType (FieldCheck)
        static readonly string[] PbShapeNames = { "Sphere", "Capsule", "Plane" };   // VRCPhysBoneColliderBase.ShapeType (FieldCheck)

        static string EnumName(string[] names, int v) => v >= 0 && v < names.Length ? names[v] : v.ToString();

        static Transform PhysBoneRoot(Component pb, SerializedObject so)
        {
            var t = CctSerialized.GetObject(so.FindProperty("rootTransform")) as Transform;
            return t != null ? t : pb.transform;
        }

        static void EstimatePhysBones(Ctx ctx, Est e, List<Component> physBones, List<Component> colliders)
        {
            var referenced = new HashSet<Object>();
            var otherRootsCache = new Dictionary<Transform, List<KeyValuePair<Component, Transform>>>();
            long totalTransforms = 0, totalChecks = 0;
            foreach (var pb in physBones)
            {
                var entry = new PbEntry { Component = pb };
                using (var so = new SerializedObject(pb))
                {
                    var rootT = PhysBoneRoot(pb, so);
                    entry.RootTransform = rootT;
                    var ignores = new HashSet<Transform>();
                    foreach (var o in CctSerialized.GetObjectArray(so.FindProperty("ignoreTransforms")))
                        if (o is Transform it) ignores.Add(it);
                    bool ignoreOther = CctSerialized.GetBool(so.FindProperty("ignoreOtherPhysBones"));
                    var endpointProp = so.FindProperty("endpointPosition");
                    bool hasEndpoint = endpointProp != null && endpointProp.propertyType == SerializedPropertyType.Vector3 && endpointProp.vector3Value != Vector3.zero;
                    int multi = CctSerialized.GetInt(so.FindProperty("multiChildType"));
                    int n = 0;
                    foreach (var o in CctSerialized.GetObjectArray(so.FindProperty("colliders")))
                    {
                        if (o == null) continue;
                        n++;
                        referenced.Add(o);
                    }
                    HashSet<Transform> otherRoots = null;
                    if (ignoreOther)
                    {
                        otherRoots = new HashSet<Transform>();
                        foreach (var kv in OtherPhysBoneRoots(rootT.root, otherRootsCache))
                            if (kv.Key != pb && kv.Value != null) otherRoots.Add(kv.Value);
                    }

                    var stack = new Stack<KeyValuePair<Transform, int>>();
                    stack.Push(new KeyValuePair<Transform, int>(rootT, 0));
                    while (stack.Count > 0)
                    {
                        var cur = stack.Pop();
                        entry.Transforms++;
                        entry.MaxDepth = Math.Max(entry.MaxDepth, cur.Value);
                        if (entry.Transforms >= MaxPhysBoneWalk) { entry.Truncated = true; break; }
                        int childCount = 0;
                        var t = cur.Key;
                        for (int i = 0; i < t.childCount; i++)
                        {
                            var ch = t.GetChild(i);
                            if (ignores.Contains(ch)) continue;
                            if (otherRoots != null && otherRoots.Contains(ch)) continue;
                            childCount++;
                            stack.Push(new KeyValuePair<Transform, int>(ch, cur.Value + 1));
                        }
                        if (n > 0 && (childCount == 1 || (childCount > 1 && multi != 0) || (childCount == 0 && hasEndpoint)))
                            entry.CollisionChecks += n;
                    }
                    entry.Colliders = n;
                    totalTransforms += entry.Transforms;
                    totalChecks += entry.CollisionChecks;
                    entry.Item = new Dictionary<string, object>
                    {
                        ["path"] = ctx.PathOf(pb),
                        ["root"] = ctx.PathOf(rootT),
                        ["affected_transforms"] = entry.Transforms,
                        ["colliders"] = n,
                        ["collision_checks"] = entry.CollisionChecks,
                        ["depth"] = entry.MaxDepth,
                        ["multi_child_type"] = EnumName(MultiChildNames, multi),
                        ["endpoint_position"] = hasEndpoint,
                        ["ignore_other_physbones"] = ignoreOther,
                        ["ignored_transforms"] = ignores.Count,
                        ["is_animated"] = CctSerialized.GetBool(so.FindProperty("isAnimated")),
                        ["allow_grabbing"] = EnumName(AdvancedBoolNames, CctSerialized.GetInt(so.FindProperty("allowGrabbing"))),
                        ["allow_posing"] = EnumName(AdvancedBoolNames, CctSerialized.GetInt(so.FindProperty("allowPosing"))),
                        ["allow_collision"] = EnumName(AdvancedBoolNames, CctSerialized.GetInt(so.FindProperty("allowCollision"))),
                        ["parameter"] = CctSerialized.GetString(so.FindProperty("parameter"), ""),
                        ["active"] = CctTargets.IsEffectivelyActive(pb.transform),
                    };
                    if (rootT != pb.transform && !CctTargets.IsDescendantOf(rootT, ctx.RootT)) entry.Item["note"] = "Root Transform がアバターの外を指しています";
                    if (entry.Truncated) entry.Item["note"] = "Transform が多すぎるため途中で数えるのをやめました";
                }
                e.PhysBones.Add(entry);
                e.Add("physbones", entry.Transforms, entry.Item);
            }

            var global = new HashSet<Object>();
            foreach (var col in colliders)
            {
                using (var so = new SerializedObject(col))
                {
                    int g = CctSerialized.GetInt(so.FindProperty("globalCollision"));
                    if (g != 0) global.Add(col);
                    e.Add("physbone_colliders", referenced.Contains(col) ? 1 : 0, new Dictionary<string, object>
                    {
                        ["path"] = ctx.PathOf(col),
                        ["shape"] = EnumName(PbShapeNames, CctSerialized.GetInt(so.FindProperty("shapeType"))),
                        ["global_collision"] = EnumName(AdvancedBoolNames, g),
                        ["referenced"] = referenced.Contains(col),
                        ["counted"] = referenced.Contains(col) || g != 0,
                    });
                }
            }
            // Referenced colliders outside the avatar (or EditorOnly) still count, as in the SDK.
            foreach (var o in referenced)
            {
                if (o is Component c && !colliders.Contains(c))
                    e.Add("physbone_colliders", 1, new Dictionary<string, object>
                    {
                        ["path"] = ctx.PathOf(c), ["referenced"] = true, ["counted"] = true,
                        ["note"] = CctTargets.IsDescendantOf(c.transform, ctx.RootT) ? "EditorOnly ですが PhysBone から参照されているため数えます" : "アバター外のコライダーですが参照されているため数えます",
                    });
            }
            var counted = new HashSet<Object>(referenced);
            counted.UnionWith(global);
            e.Values["physbone_components"] = (long)physBones.Count;
            e.Values["physbone_transforms"] = Math.Min(totalTransforms, int.MaxValue);
            e.Values["physbone_colliders"] = (long)counted.Count;
            e.Values["physbone_collision_checks"] = Math.Min(totalChecks, int.MaxValue);
        }

        /// <summary>Root transforms of every VRCPhysBoneBase under the scene top transform (SDK: rootTransform.root, inactive included).</summary>
        static List<KeyValuePair<Component, Transform>> OtherPhysBoneRoots(Transform top, Dictionary<Transform, List<KeyValuePair<Component, Transform>>> cache)
        {
            if (top == null) return new List<KeyValuePair<Component, Transform>>();
            if (cache.TryGetValue(top, out var list)) return list;
            list = new List<KeyValuePair<Component, Transform>>();
            foreach (var c in top.GetComponentsInChildren<Component>(true))
            {
                if (c == null || !CctSerialized.IsType(c, TPhysBoneBase)) continue;
                using (var so = new SerializedObject(c))
                    list.Add(new KeyValuePair<Component, Transform>(c, PhysBoneRoot(c, so)));
            }
            cache[top] = list;
            return list;
        }

        static void EstimateContacts(Ctx ctx, HierarchyScan scan, Est e, List<Component> contacts)
        {
            long ranked = 0;
            foreach (var c in contacts)
            {
                using (var so = new SerializedObject(c))
                {
                    bool localOnly = CctSerialized.GetBool(so.FindProperty("localOnly"));
                    bool eo = scan.IsEditorOnly(c);
                    e.ContactsAll++;
                    if (!localOnly) ranked++;
                    if (!localOnly && eo) e.ContactsEditorOnly++;
                    var ti = Info(c.GetType());
                    var row = new Dictionary<string, object>
                    {
                        ["path"] = ctx.PathOf(c),
                        ["type"] = ti.ContactReceiver ? "receiver" : "sender",
                        ["local_only"] = localOnly,
                        ["counted"] = !localOnly,
                        ["active"] = CctTargets.IsEffectivelyActive(c.transform),
                    };
                    if (ti.ContactReceiver) row["parameter"] = CctSerialized.GetString(so.FindProperty("parameter"), "");
                    if (eo) row["editor_only"] = true;
                    e.Add("contacts", localOnly ? 0 : 1, row);
                }
            }
            e.Values["contacts"] = ranked;
        }

        static void EstimateConstraints(Ctx ctx, Est e, List<Component> vrc, List<Component> unity)
        {
            int maxGroup = 0, cached = 0;
            foreach (var c in vrc)
            {
                int group = -1;
                var ti = Info(c.GetType());
                try { if (ti.GroupIndex != null) group = Convert.ToInt32(ti.GroupIndex.GetValue(c, null)); }
                catch (Exception) { group = -1; }
                if (group >= 0)
                {
                    cached++;
                    if (group >= maxGroup) maxGroup = group + 1;
                }
                var row = new Dictionary<string, object>
                {
                    ["path"] = ctx.PathOf(c), ["type"] = ti.Name, ["kind"] = "vrchat", ["execution_group"] = group,
                };
                using (var so = new SerializedObject(c))
                {
                    var target = CctSerialized.GetObject(so.FindProperty("TargetTransform")) as Transform;
                    if (target != null) row["target"] = ctx.PathOf(target);
                    row["is_active"] = CctSerialized.GetBool(so.FindProperty("IsActive"), true);
                }
                e.Add("constraints", group, row);
            }
            foreach (var c in unity)
                e.Add("constraints", 1000, new Dictionary<string, object>
                {
                    ["path"] = ctx.PathOf(c), ["type"] = c.GetType().Name, ["kind"] = "unity",
                    ["note"] = "Unity Constraint は 1 個ごとに Constraint Depth +1 (docs)",
                });
            long count = vrc.Count + unity.Count;
            long depth = 0;
            if (count > 0)
            {
                depth = maxGroup;
                long extra = vrc.Count - cached + unity.Count;
                if (extra > 0) depth += extra;
            }
            e.Values["constraints"] = count;
            e.Values["constraint_depth"] = depth;
        }

        static void EstimateParticlesAndLights(Ctx ctx, Est e, List<Light> lights)
        {
            int systems = 0;
            ulong total = 0;
            long meshPolys = 0;
            bool trails = false, collision = false;
            long particleMaterials = 0;
            long particleLights = 0;
            var meshBuffer = new Mesh[0];
            foreach (var ps in e.Particles)
            {
                var main = ps.main;
                int n = main.maxParticles;
                bool loop = main.ringBufferMode == ParticleSystemRingBufferMode.LoopUntilReplaced;
                var em = ps.emission;
                if (loop)
                {
                    n += Mathf.CeilToInt(CurveMax(em.rateOverTime, em.rateOverTimeMultiplier, false));
                    n += Mathf.CeilToInt(CurveMax(em.rateOverDistance, em.rateOverDistanceMultiplier, true));
                    for (int i = 0; i < em.burstCount; i++)
                    {
                        var burst = em.GetBurst(i);
                        n += Mathf.CeilToInt(burst.maxCount * burst.cycleCount);
                    }
                }
                var lightsModule = ps.lights;
                int k = 0;
                if (lightsModule.light != null)
                {
                    k = Mathf.Min(main.maxParticles, lightsModule.maxLights);
                    if (k > 0) particleLights += k;
                }
                var row = new Dictionary<string, object>
                {
                    ["path"] = ctx.PathOf(ps),
                    ["max_particles"] = main.maxParticles,
                    ["counted_particles"] = n,
                    ["ring_buffer_loop"] = loop,
                    ["stop_action"] = main.stopAction.ToString(),
                    ["active"] = CctTargets.IsEffectivelyActive(ps.transform),
                };
                if (k > 0) row["lights"] = k;
                if (!loop && n <= 0)
                {
                    row["counted"] = false;
                    row["note"] = "maxParticles が 0 以下のため SDK は数えません";
                    e.Add("particles", 0, row);
                    continue;
                }
                systems++;
                total += (ulong)(uint)n;
                var psr = ps.GetComponent<ParticleSystemRenderer>();
                if (psr == null)
                {
                    row["note"] = "ParticleSystemRenderer が無いため Material/Mesh/Trail/Collision には数えません (SDK と同じ)";
                    e.Add("particles", n, row);
                    continue;
                }
                particleMaterials++;
                row["render_mode"] = psr.renderMode.ToString();
                if (psr.renderMode == ParticleSystemRenderMode.Mesh && psr.meshCount > 0)
                {
                    if (meshBuffer.Length < psr.meshCount) meshBuffer = new Mesh[psr.meshCount];
                    int got = psr.GetMeshes(meshBuffer);
                    long maxTri = 0;
                    for (int i = 0; i < got; i++)
                    {
                        if (meshBuffer[i] == null) continue;
                        e.UsedMeshes.Add(meshBuffer[i]);
                        maxTri = Math.Max(maxTri, MeshTriangles(meshBuffer[i]));
                    }
                    long polys = maxTri * n;
                    meshPolys += polys;
                    row["mesh_particle_polys"] = polys;
                }
                if (ps.trails.enabled) { trails = true; particleMaterials++; row["trails"] = true; }
                if (ps.collision.enabled) { collision = true; row["collision"] = true; }
                e.Add("particles", n, row);
            }
            e.Values["particle_systems"] = (long)systems;
            e.Values["total_particles"] = (long)Math.Min(total, (ulong)int.MaxValue);
            e.Values["mesh_particle_polys"] = Math.Min(meshPolys, int.MaxValue);
            e.Values["particle_trails_enabled"] = trails;
            e.Values["particle_collision_enabled"] = collision;
            e.ParticleMaterials = particleMaterials;

            e.Values["lights"] = lights.Count + particleLights;
            foreach (var l in lights)
                e.Add("lights", 1, new Dictionary<string, object>
                {
                    ["path"] = ctx.PathOf(l), ["type"] = l.type.ToString(), ["shadows"] = l.shadows.ToString(),
                    ["active"] = CctTargets.IsEffectivelyActive(l.transform) && l.enabled,
                });
        }

        /// <summary>SDK rule: constant modes (curve == null) use constant / constantMax, curve modes the largest key x multiplier.</summary>
        static float CurveMax(ParticleSystem.MinMaxCurve c, float multiplier, bool useConstantMax)
        {
            var curve = c.curve;
            if (curve == null) return useConstantMax ? c.constantMax : c.constant;
            float max = 0f;
            foreach (var k in curve.keys) if (k.value > max) max = k.value;
            return max * multiplier;
        }
    }
}
