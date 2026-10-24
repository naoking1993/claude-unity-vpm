using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using P = Naoking.AvatarChecker.PerformanceRank;

namespace Naoking.AvatarChecker
{
    /// <summary>
    /// Reads an avatar hierarchy and fills a CheckReport. Never modifies the scene or assets.
    /// Counting follows the VRChat SDK's performance scanners (checked against SDK 3.10.5) where it can.
    /// Inactive objects are counted (VRChat counts them too); objects tagged EditorOnly are skipped
    /// because VRChat strips them on upload.
    /// </summary>
    public static class AvatarScanner
    {
        public static CheckReport Scan(GameObject root)
        {
            var r = new CheckReport();
            if (root == null) { r.Add(Severity.Error, "no-target", "チェックするアバターが選ばれていません。"); return r; }
            r.AvatarName = root.name;
            r.HierarchyPath = PathOf(root.transform, null);
            r.BuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
            r.VrcSdkFound = VrcReflection.SdkFound;

            var gos = Collect(root.transform);
            var stats = new Dictionary<string, double>();
            foreach (var m in P.Metrics(Platform.PC)) stats[m] = 0;

            var bones = new HashSet<Transform>();
            var textures = new HashSet<Texture>();
            var nonReadable = new HashSet<Mesh>();
            var physBones = new List<Component>();
            var pbColliders = new List<Component>();
            var animators = new List<Animator>();
            int unityConstraints = 0;
            var vrcConstraints = new List<Component>();
            int descriptorsBelowRoot = 0;
            Component descriptor = null;

            foreach (var go in gos)
            {
                var path = PathOf(go.transform, root.transform);
                int id = go.GetInstanceID();
                int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (missing > 0)
                    r.Add(Severity.Error, "missing-script", "Missing スクリプトが " + missing + " 個あります。アップロードできません。削除するか元のアセットを入れ直してください。", path, id);
                bool hasStation = go.GetComponents<Component>().Any(IsStation);

                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;  // missing script, reported above
                    var rend = c as Renderer;
                    if (rend != null) AddTextures(rend.sharedMaterials, textures);  // the SDK sizes textures of every Renderer
                    if (VrcReflection.Is(c, VrcReflection.AvatarDescriptor))
                    {
                        if (go == root) descriptor = c; else descriptorsBelowRoot++;
                        continue;
                    }
                    var smr = c as SkinnedMeshRenderer;
                    if (smr != null)
                    {
                        stats[P.SkinnedMeshes]++;
                        CountMesh(r, stats, smr.sharedMesh, smr.sharedMaterials, nonReadable, path, id);
                        int nullBones = 0;
                        foreach (var b in smr.bones) { if (b == null) nullBones++; else bones.Add(b); }
                        if (nullBones > 0)
                            r.Add(Severity.Warning, "null-bone", "Skinned Mesh Renderer のボーン参照が " + nullBones + " 個空になっています。ボーンを消した・衣装の移植に失敗した可能性があります。", path, id);
                        if (smr.rootBone == null && smr.sharedMesh != null && smr.sharedMesh.bindposes.Length > 0)
                            r.Add(Severity.Info, "no-root-bone", "Root Bone が未設定です。表示範囲 (Bounds) がずれて消えることがあります。", path, id);
                        continue;
                    }
                    var mr = c as MeshRenderer;
                    if (mr != null)
                    {
                        stats[P.BasicMeshes]++;
                        var mf = go.GetComponent<MeshFilter>();
                        CountMesh(r, stats, mf != null ? mf.sharedMesh : null, mr.sharedMaterials, nonReadable, path, id);
                        continue;
                    }
                    var ps = c as ParticleSystem;
                    if (ps != null) { CountParticles(stats, ps); continue; }
                    var psr = c as ParticleSystemRenderer;
                    if (psr != null)
                    {
                        // the SDK's Read/Write check takes every particle mesh, whatever the render mode
                        var pm = new Mesh[psr.meshCount];
                        int pn = psr.GetMeshes(pm);
                        for (int i = 0; i < pn; i++) CheckReadable(r, pm[i], nonReadable, path, id);
                        continue;
                    }
                    var trail = c as TrailRenderer;
                    if (trail != null) { stats[P.TrailRenderers]++; stats[P.MaterialSlots]++; continue; }
                    var line = c as LineRenderer;
                    if (line != null) { stats[P.LineRenderers]++; stats[P.MaterialSlots]++; continue; }
                    if (c is Animator) { stats[P.Animators]++; animators.Add((Animator)c); continue; }
                    if (c is Animation) { stats[P.Animators]++; continue; }  // the SDK counts legacy Animation too
                    if (c is Light) { stats[P.Lights]++; continue; }
                    var cloth = c as Cloth;
                    if (cloth != null) { stats[P.Cloths]++; stats[P.ClothVertices] += cloth.coefficients.Length; continue; }
                    if (c is Collider) { if (!hasStation) stats[P.PhysicsColliders]++; continue; }
                    if (c is Rigidbody) { if (!hasStation) stats[P.Rigidbodies]++; continue; }
                    if (c is AudioSource) { stats[P.AudioSources]++; continue; }
                    if (c is IConstraint) { stats[P.Constraints]++; unityConstraints++; continue; }
                    if (c.GetType().Namespace == VrcReflection.ConstraintNamespace) { stats[P.Constraints]++; vrcConstraints.Add(c); continue; }
                    if (VrcReflection.Is(c, VrcReflection.PhysBone)) { stats[P.PhysBoneComponents]++; physBones.Add(c); continue; }
                    if (VrcReflection.Is(c, VrcReflection.PhysBoneCollider)) { pbColliders.Add(c); continue; }
                    if (VrcReflection.Is(c, VrcReflection.ContactSender) || VrcReflection.Is(c, VrcReflection.ContactReceiver))
                    {
                        if (!(VrcReflection.Get(c, "localOnly") as bool? ?? false)) stats[P.Contacts]++;  // Local Only contacts are free
                        continue;
                    }
                }
            }

            stats[P.Bones] = bones.Count;
            stats[P.NonReadableMeshes] = nonReadable.Count;
            stats[P.ConstraintDepth] = ConstraintDepth(unityConstraints, vrcConstraints);
            CountPhysBones(r, stats, physBones, pbColliders, root.transform);
            stats[P.TextureMemoryMB] = TextureMemoryMB(textures);
            foreach (var kv in stats) r.Stats[kv.Key] = kv.Value;

            CheckDescriptor(r, root, descriptor, descriptorsBelowRoot);
            CheckAnimator(r, root, animators);
            return r;
        }

        /// <summary>
        /// The outermost object (the selection or one of its parents) with a VRC Avatar Descriptor, so a descriptor
        /// left on an outfit inside the avatar does not hide the real avatar. Falls back to the selection.
        /// </summary>
        public static GameObject ResolveAvatarRoot(GameObject selected)
        {
            if (selected == null) return null;
            var t = VrcReflection.Find(VrcReflection.AvatarDescriptor);
            if (t == null) return selected;
            GameObject found = null;
            for (var cur = selected.transform; cur != null; cur = cur.parent)
                if (cur.GetComponent(t) != null) found = cur.gameObject;
            return found ?? selected;
        }

        /// <summary>Every GameObject in the open scenes (not Prefab Mode) with a VRC Avatar Descriptor, including inactive ones.</summary>
        public static List<GameObject> AvatarsInScene()
        {
            var t = VrcReflection.Find(VrcReflection.AvatarDescriptor);
            var list = new List<GameObject>();
            if (t == null) return list;
            foreach (var o in Resources.FindObjectsOfTypeAll(t))
            {
                var c = o as Component;
                if (c == null || EditorUtility.IsPersistent(c) || c.hideFlags != HideFlags.None) continue;
                var scene = c.gameObject.scene;
                if (!scene.IsValid() || EditorSceneManager.IsPreviewScene(scene)) continue;
                list.Add(c.gameObject);
            }
            return list.OrderBy(g => PathOf(g.transform, null)).ToList();
        }

        static List<GameObject> Collect(Transform root)
        {
            var list = new List<GameObject>();
            var stack = new Stack<Transform>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                if (t != root && t.CompareTag("EditorOnly")) continue;
                list.Add(t.gameObject);
                for (int i = t.childCount - 1; i >= 0; i--) stack.Push(t.GetChild(i));
            }
            return list;
        }

        static bool IsStation(Component c)
        {
            if (c == null) return false;
            for (var t = c.GetType(); t != null; t = t.BaseType)
                if (t.Name == "VRCStation") return true;
            return false;
        }

        static void CountMesh(CheckReport r, Dictionary<string, double> stats, Mesh mesh, Material[] mats, HashSet<Mesh> nonReadable, string path, int id)
        {
            if (mesh == null)
            {
                r.Add(Severity.Warning, "no-mesh", "メッシュが設定されていません（Missing）。", path, id);
            }
            else
            {
                stats[P.Triangles] += Triangles(mesh);
                stats[P.MaterialSlots] += mesh.subMeshCount;  // the SDK counts submeshes, not the materials array
                CheckReadable(r, mesh, nonReadable, path, id);
                if (mats.Length > mesh.subMeshCount)
                    r.Add(Severity.Info, "extra-material-slot", "マテリアル枠 (" + mats.Length + ") がサブメッシュ数 (" + mesh.subMeshCount + ") より多くなっています。ランクには数えられませんが、余った枠の分だけ描画が増えます。", path, id);
            }
            int empty = 0;
            foreach (var m in mats)
            {
                if (m == null) { empty++; continue; }
                if (m.shader == null || m.shader.name == "Hidden/InternalErrorShader")
                    r.Add(Severity.Error, "broken-shader", "マテリアル「" + m.name + "」のシェーダーが見つかりません（ピンク表示）。シェーダーを導入してください。", path, id);
                else if (ShaderUtil.ShaderHasError(m.shader))
                    r.Add(Severity.Error, "shader-compile-error", "マテリアル「" + m.name + "」のシェーダー「" + m.shader.name + "」にコンパイルエラーがあります（ピンク表示）。シェーダーのバージョンを確認してください。", path, id);
            }
            if (empty > 0)
                r.Add(Severity.Warning, "empty-material", "空のマテリアル枠が " + empty + " 個あります（ピンク表示になります）。", path, id);
        }

        // VRChat docs: a mesh with Read/Write disabled makes the rank Very Poor in game. The SDK build panel does not
        // rank it, but refuses to upload while a model (ModelImporter) has Read/Write off.
        static void CheckReadable(CheckReport r, Mesh mesh, HashSet<Mesh> nonReadable, string path, int id)
        {
            if (mesh == null || nonReadable.Contains(mesh)) return;
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(mesh)) as ModelImporter;
            bool importerOff = importer != null && !importer.isReadable;
            if (mesh.isReadable && !importerOff) return;
            nonReadable.Add(mesh);
            r.Add(Severity.Error, "mesh-read-write-disabled", "メッシュ「" + mesh.name + "」の Read/Write が無効です。VRChat 上のランクが Very Poor になります。" +
                (importerOff ? "SDK もこのままではアップロードできません。モデルの Import Settings で Read/Write にチェックを入れてください。" : "読み書きできるメッシュに作り直してください。"), path, id);
        }

        // Mirrors the SDK's ParticlePerformanceScanner (and the particle part of LightPerformanceScanner).
        static void CountParticles(Dictionary<string, double> stats, ParticleSystem ps)
        {
            var main = ps.main;
            int max = main.maxParticles;
            if (ps.lights.light != null) stats[P.Lights] += Math.Min(max, ps.lights.maxLights);
            if (main.ringBufferMode == ParticleSystemRingBufferMode.LoopUntilReplaced)
            {
                var em = ps.emission;
                max += Mathf.CeilToInt(em.rateOverTime.curve == null ? em.rateOverTime.constant : MaxKey(em.rateOverTime.curve) * em.rateOverTimeMultiplier);
                max += Mathf.CeilToInt(em.rateOverDistance.curve == null ? em.rateOverDistance.constantMax : MaxKey(em.rateOverDistance.curve) * em.rateOverDistanceMultiplier);
                for (int i = 0; i < em.burstCount; i++)
                {
                    var b = em.GetBurst(i);
                    max += Mathf.CeilToInt(b.maxCount * b.cycleCount);
                }
            }
            else if (max <= 0) return;
            stats[P.ParticleSystems]++;
            stats[P.ParticleTotalCount] += max;
            var psr = ps.GetComponent<ParticleSystemRenderer>();
            if (psr != null)
            {
                stats[P.MaterialSlots]++;
                if (psr.renderMode == ParticleSystemRenderMode.Mesh && psr.meshCount > 0)
                {
                    var meshes = new Mesh[psr.meshCount];
                    int n = psr.GetMeshes(meshes);
                    long tris = 0;
                    for (int i = 0; i < n; i++)
                        if (meshes[i] != null) tris = Math.Max(tris, Triangles(meshes[i]));
                    stats[P.ParticleMeshPolys] += (double)max * tris;
                }
                if (ps.trails.enabled) { stats[P.ParticleTrails] = 1; stats[P.MaterialSlots]++; }
            }
            if (ps.collision.enabled) stats[P.ParticleCollision] = 1;
        }

        static float MaxKey(AnimationCurve curve)
        {
            float m = 0f;
            foreach (var k in curve.keys) if (k.value > m) m = k.value;
            return m;
        }

        static void AddTextures(Material[] mats, HashSet<Texture> textures)
        {
            foreach (var m in mats)
            {
                if (m == null) continue;
                foreach (var tid in m.GetTexturePropertyNameIDs())
                {
                    var tex = m.GetTexture(tid);
                    if (tex != null && !(tex is RenderTexture)) textures.Add(tex);
                }
            }
        }

        // Same rule as the SDK: quads count indexCount / 2, every other topology indexCount / 3.
        static long Triangles(Mesh mesh)
        {
            long n = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                long count = mesh.GetIndexCount(i);
                n += mesh.GetTopology(i) == MeshTopology.Quads ? count / 2 : count / 3;
            }
            return n;
        }

        // The SDK's bytes-per-pixel table (MeshPerformanceScanner, SDK 3.10.5); formats not listed count 16.
        static readonly Dictionary<TextureFormat, float> BytesPerPixel = new Dictionary<TextureFormat, float>
        {
            { (TextureFormat)1, 1f }, { (TextureFormat)2, 2f }, { (TextureFormat)3, 4f }, { (TextureFormat)4, 4f }, { (TextureFormat)5, 4f },
            { (TextureFormat)7, 2f }, { (TextureFormat)9, 2f }, { (TextureFormat)10, 0.5f }, { (TextureFormat)12, 1f }, { (TextureFormat)13, 2f },
            { (TextureFormat)14, 4f }, { (TextureFormat)15, 2f }, { (TextureFormat)16, 4f }, { (TextureFormat)17, 8f }, { (TextureFormat)18, 4f },
            { (TextureFormat)19, 8f }, { (TextureFormat)20, 16f }, { (TextureFormat)26, 0.5f }, { (TextureFormat)27, 1f }, { (TextureFormat)24, 1f },
            { (TextureFormat)25, 1f }, { (TextureFormat)28, 0.5f }, { (TextureFormat)29, 1f }, { (TextureFormat)30, 0.25f }, { (TextureFormat)31, 0.25f },
            { (TextureFormat)32, 0.5f }, { (TextureFormat)33, 0.5f }, { (TextureFormat)34, 0.5f }, { (TextureFormat)41, 0.5f }, { (TextureFormat)42, 0.5f },
            { (TextureFormat)43, 1f }, { (TextureFormat)44, 1f }, { (TextureFormat)45, 0.5f }, { (TextureFormat)46, 0.5f }, { (TextureFormat)47, 1f },
            { (TextureFormat)48, 1f }, { (TextureFormat)49, 0.64f }, { (TextureFormat)50, 0.445f }, { (TextureFormat)51, 0.25f }, { (TextureFormat)52, 0.16f },
            { (TextureFormat)53, 0.11125f }, { (TextureFormat)62, 2f }, { (TextureFormat)63, 1f }, { (TextureFormat)64, 0.5f }, { (TextureFormat)65, 1f },
        };

        static float Bpp(TextureFormat f) { float v; return BytesPerPixel.TryGetValue(f, out v) ? v : 16f; }

        // Same formula as the SDK (one face for cubemaps, no mips for arrays and 3D textures). Formats depend on the
        // active build target's import settings.
        static double TextureMemoryMB(HashSet<Texture> textures)
        {
            long bytes = 0;
            foreach (var t in textures)
            {
                var t2 = t as Texture2D;
                if (t2 != null) { bytes += MipChainBytes(Bpp(t2.format), t2.width, t2.height, t2.mipmapCount); continue; }
                var cube = t as Cubemap;
                if (cube != null) { bytes += MipChainBytes(Bpp(cube.format), cube.width, cube.height, cube.mipmapCount); continue; }
                var arr = t as Texture2DArray;
                if (arr != null) { bytes += Mathf.RoundToInt((float)arr.width * arr.height * arr.depth * Bpp(arr.format)); continue; }
                var t3 = t as Texture3D;
                if (t3 != null) { bytes += Mathf.RoundToInt((float)t3.width * t3.height * t3.depth * Bpp(t3.format)); continue; }
            }
            return bytes / (1024.0 * 1024.0);
        }

        static long MipChainBytes(float bpp, int w, int h, int mips)
        {
            long total = 0;
            int pixels = w * h;
            for (int i = 0; i < mips; i++) total += Mathf.RoundToInt((pixels >> 2 * i) * bpp);
            return total;
        }

        // SDK: VRC constraint execution-group depth (+1 for each VRC constraint not grouped yet) + one per Unity constraint.
        static double ConstraintDepth(int unityConstraints, List<Component> vrcConstraints)
        {
            int maxGroup = -1, ungrouped = 0;
            foreach (var c in vrcConstraints)
            {
                var g = VrcReflection.Get(c, "LatestValidExecutionGroupIndex") as int?;
                if (g.HasValue && g.Value >= 0) maxGroup = Math.Max(maxGroup, g.Value); else ungrouped++;
            }
            return maxGroup + 1 + ungrouped + unityConstraints;
        }

        // Mirrors the SDK's PhysBonePerformanceScanner + VRCPhysBoneBase.InitTransforms: transforms are the kept nodes
        // (endpoints are not added); a node costs one check per collider when it has exactly 1 kept child, more than
        // 1 kept child and Multi Child Type is not Ignore, or no kept child and an Endpoint Position.
        static void CountPhysBones(CheckReport r, Dictionary<string, double> stats, List<Component> physBones, List<Component> pbColliders, Transform avatarRoot)
        {
            var roots = new Dictionary<Component, Transform>();
            foreach (var pb in physBones)
            {
                var rootObj = VrcReflection.Get(pb, "rootTransform");
                roots[pb] = VrcReflection.IsNull(rootObj) ? pb.transform : (Transform)rootObj;
            }
            var usedColliders = new HashSet<UnityEngine.Object>();
            foreach (var c in pbColliders)
                if (IsNonZero(VrcReflection.Get(c, "globalCollision"))) usedColliders.Add(c);

            foreach (var pb in physBones)
            {
                var path = PathOf(pb.transform, avatarRoot);
                int id = pb.gameObject.GetInstanceID();
                var root = roots[pb];
                if (root != avatarRoot && !root.IsChildOf(avatarRoot))
                    r.Add(Severity.Warning, "physbone-root-outside", "PhysBone の Root Transform がアバターの外を指しています。", path, id);
                var skip = new HashSet<Transform>();
                foreach (var o in VrcReflection.Items(VrcReflection.Get(pb, "ignoreTransforms")))
                    if (!VrcReflection.IsNull(o)) skip.Add((Transform)o);
                if (VrcReflection.Get(pb, "ignoreOtherPhysBones") as bool? ?? false)
                    foreach (var kv in roots) if (kv.Key != pb && kv.Value != root) skip.Add(kv.Value);
                bool multiChild = IsNonZero(VrcReflection.Get(pb, "multiChildType"));  // 0 = Ignore
                var ep = VrcReflection.Get(pb, "endpointPosition");
                bool endpoint = ep is Vector3 && (Vector3)ep != Vector3.zero;

                int transforms = 0, checkedBones = 0;
                if (!root.CompareTag("EditorOnly")) Walk(root, skip, multiChild, endpoint, ref transforms, ref checkedBones);
                if (transforms == 0 || (transforms == 1 && !endpoint))
                    r.Add(Severity.Info, "physbone-no-chain", "PhysBone の対象が 1 本以下で、揺れません。Root Transform か Endpoint Position を確認してください。", path, id);

                int colliders = 0, nullColliders = 0;
                foreach (var o in VrcReflection.Items(VrcReflection.Get(pb, "colliders")))
                {
                    if (VrcReflection.IsNull(o)) { nullColliders++; continue; }
                    colliders++;
                    usedColliders.Add((UnityEngine.Object)o);
                }
                if (nullColliders > 0)
                    r.Add(Severity.Warning, "physbone-null-collider", "PhysBone の Colliders に空の参照が " + nullColliders + " 個あります。", path, id);
                stats[P.PhysBoneTransforms] += transforms;
                stats[P.PhysBoneCollisionChecks] += checkedBones * colliders;
            }
            // SDK: colliders referenced by a PhysBone, plus colliders with global collision; unused ones cost nothing
            stats[P.PhysBoneColliders] = usedColliders.Count;
        }

        static void Walk(Transform t, HashSet<Transform> skip, bool multiChild, bool endpoint, ref int transforms, ref int checkedBones)
        {
            transforms++;
            int kept = 0;
            for (int i = 0; i < t.childCount; i++)
            {
                var ch = t.GetChild(i);
                if (skip.Contains(ch) || ch.CompareTag("EditorOnly")) continue;
                kept++;
                Walk(ch, skip, multiChild, endpoint, ref transforms, ref checkedBones);
            }
            if (kept == 1 || (kept > 1 && multiChild) || (kept == 0 && endpoint)) checkedBones++;
        }

        static bool IsNonZero(object v)
        {
            if (v == null) return false;
            if (v is bool) return (bool)v;
            try { return Convert.ToInt64(v) != 0; } catch (Exception) { return false; }
        }

        static void CheckDescriptor(CheckReport r, GameObject root, Component descriptor, int descriptorsBelowRoot)
        {
            if (!r.VrcSdkFound)
            {
                r.Add(Severity.Info, "no-vrc-sdk", "VRChat Avatars SDK が見つからないため、PhysBone・Contact・Expression パラメータは数えていません。");
                return;
            }
            int rootId = root.GetInstanceID();
            r.HasAvatarDescriptor = descriptor != null;
            if (descriptor == null)
                r.Add(Severity.Warning, "no-avatar-descriptor", "選んだオブジェクトに VRC Avatar Descriptor がありません。アバターのルートを選んでください。", "", rootId);
            if (descriptorsBelowRoot > 0)
                r.Add(Severity.Error, "nested-avatar-descriptor", "アバターの子に VRC Avatar Descriptor が " + descriptorsBelowRoot + " 個あります。衣装プレハブ側の Descriptor は削除してください。", "", rootId);
            if (descriptor == null) return;

            var view = VrcReflection.Get(descriptor, "ViewPosition");
            if (view is Vector3 && ((Vector3)view).y <= 0f)
                r.Add(Severity.Warning, "view-position", "View Position（視点）の高さが 0 以下です。目の位置に合わせてください。", "", rootId);

            bool custom = VrcReflection.Get(descriptor, "customExpressions") as bool? ?? false;
            var parameters = VrcReflection.Get(descriptor, "expressionParameters");
            var menu = VrcReflection.Get(descriptor, "expressionsMenu");
            bool hasParams = !VrcReflection.IsNull(parameters), hasMenu = !VrcReflection.IsNull(menu);
            // The SDK refuses to upload when only one of the two is set, whether Custom Expressions is on or not.
            if (hasParams != hasMenu)
                r.Add(Severity.Error, hasMenu ? "no-expression-parameters" : "no-expression-menu",
                    "Expressions の " + (hasMenu ? "Parameters" : "Menu") + " が未設定です（Menu と Parameters は両方設定するか、両方空にします）。アップロードできません。", "", rootId);
            var cost = VrcReflection.CallInt(parameters, "CalcTotalCost");
            if (cost.HasValue)
            {
                r.ExpressionParameterCost = cost.Value;
                if (cost.Value > CheckReport.ExpressionParameterBudget)
                    // the SDK checks this whether Custom Expressions is on or not
                    r.Add(Severity.Error, "parameter-budget", "Expression パラメータが " + cost.Value + " / " + CheckReport.ExpressionParameterBudget + " bit で上限を超えています。アップロードできません。", "", rootId);
                else if (custom && cost.Value > CheckReport.ExpressionParameterBudget * 0.9)
                    r.Add(Severity.Info, "parameter-budget-near", "Expression パラメータが " + cost.Value + " / " + CheckReport.ExpressionParameterBudget + " bit で上限に近づいています。", "", rootId);
            }
        }

        static void CheckAnimator(CheckReport r, GameObject root, List<Animator> animators)
        {
            int rootId = root.GetInstanceID();
            var a = root.GetComponent<Animator>();
            if (a == null)
            {
                r.Add(Severity.Warning, "no-root-animator", "アバターのルートに Animator がありません。", "", rootId);
                return;
            }
            if (a.avatar == null)
                r.Add(Severity.Warning, "no-unity-avatar", "ルートの Animator に Avatar（ボーン定義）が設定されていません。", "", rootId);
            else if (!a.avatar.isHuman)
                r.Add(Severity.Info, "not-humanoid", "Avatar が Humanoid ではありません（Generic）。フルトラや表情の一部が使えません。", "", rootId);
            int extra = animators.Count(x => x != a);
            if (extra > 0)
                r.Add(Severity.Info, "extra-animators", "ルート以外に Animator が " + extra + " 個あります。衣装プレハブに残った Animator は削除すると軽くなります。", "", rootId);
        }

        public static string PathOf(Transform t, Transform root)
        {
            if (t == root) return "";
            var names = new List<string>();
            for (var cur = t; cur != null && cur != root; cur = cur.parent) names.Add(cur.name);
            names.Reverse();
            return string.Join("/", names.ToArray());
        }
    }
}
