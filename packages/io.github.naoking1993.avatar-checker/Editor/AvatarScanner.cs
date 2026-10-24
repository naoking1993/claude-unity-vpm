using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Profiling;
using P = Naoking.AvatarChecker.PerformanceRank;

namespace Naoking.AvatarChecker
{
    /// <summary>
    /// Reads an avatar hierarchy and fills a CheckReport. Never modifies the scene or assets.
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
            r.VrcSdkFound = VrcReflection.SdkFound;

            var gos = Collect(root.transform);
            var stats = new Dictionary<string, double>();
            foreach (var m in P.Metrics(Platform.PC)) stats[m] = 0;

            var bones = new HashSet<Transform>();
            var textures = new HashSet<Texture>();
            var physBones = new List<Component>();
            var animators = new List<Animator>();
            int descriptorsBelowRoot = 0;
            Component descriptor = null;

            foreach (var go in gos)
            {
                var path = PathOf(go.transform, root.transform);
                int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (missing > 0)
                    r.Add(Severity.Error, "missing-script", "Missing スクリプトが " + missing + " 個あります。アップロードできません。削除するか元のアセットを入れ直してください。", path);

                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;  // missing script, reported above
                    if (VrcReflection.Is(c, VrcReflection.AvatarDescriptor))
                    {
                        if (go == root) descriptor = c; else descriptorsBelowRoot++;
                        continue;
                    }
                    var smr = c as SkinnedMeshRenderer;
                    if (smr != null)
                    {
                        stats[P.SkinnedMeshes]++;
                        CountMesh(r, stats, smr.sharedMesh, smr.sharedMaterials, textures, path);
                        int nullBones = 0;
                        foreach (var b in smr.bones) { if (b == null) nullBones++; else bones.Add(b); }
                        if (nullBones > 0)
                            r.Add(Severity.Warning, "null-bone", "Skinned Mesh Renderer のボーン参照が " + nullBones + " 個空になっています。ボーンを消した・衣装の移植に失敗した可能性があります。", path);
                        if (smr.rootBone == null && smr.sharedMesh != null && smr.sharedMesh.bindposes.Length > 0)
                            r.Add(Severity.Info, "no-root-bone", "Root Bone が未設定です。表示範囲 (Bounds) がずれて消えることがあります。", path);
                        continue;
                    }
                    var mr = c as MeshRenderer;
                    if (mr != null)
                    {
                        stats[P.BasicMeshes]++;
                        var mf = go.GetComponent<MeshFilter>();
                        CountMesh(r, stats, mf != null ? mf.sharedMesh : null, mr.sharedMaterials, textures, path);
                        continue;
                    }
                    if (c is Animator) { stats[P.Animators]++; animators.Add((Animator)c); continue; }
                    if (c is Light) { stats[P.Lights]++; continue; }
                    if (c is ParticleSystem) { stats[P.ParticleSystems]++; continue; }
                    if (c is TrailRenderer) { stats[P.TrailRenderers]++; continue; }
                    if (c is LineRenderer) { stats[P.LineRenderers]++; continue; }
                    if (c is Cloth) { stats[P.Cloths]++; continue; }
                    if (c is Collider) { stats[P.PhysicsColliders]++; continue; }
                    if (c is Rigidbody) { stats[P.Rigidbodies]++; continue; }
                    if (c is AudioSource) { stats[P.AudioSources]++; continue; }
                    if (c is IConstraint || c.GetType().Namespace == VrcReflection.ConstraintNamespace) { stats[P.Constraints]++; continue; }
                    if (VrcReflection.Is(c, VrcReflection.PhysBone)) { stats[P.PhysBoneComponents]++; physBones.Add(c); continue; }
                    if (VrcReflection.Is(c, VrcReflection.PhysBoneCollider)) { stats[P.PhysBoneColliders]++; continue; }
                    if (VrcReflection.Is(c, VrcReflection.ContactSender) || VrcReflection.Is(c, VrcReflection.ContactReceiver)) { stats[P.Contacts]++; continue; }
                }
            }

            stats[P.Bones] = bones.Count;
            CountPhysBones(r, stats, physBones, root.transform);
            stats[P.TextureMemoryMB] = TextureMemoryMB(textures);
            foreach (var kv in stats) r.Stats[kv.Key] = kv.Value;

            CheckDescriptor(r, root, descriptor, descriptorsBelowRoot);
            CheckAnimator(r, root, animators);
            return r;
        }

        /// <summary>The selected object itself if it is an avatar, otherwise the nearest parent with a VRC Avatar Descriptor.</summary>
        public static GameObject ResolveAvatarRoot(GameObject selected)
        {
            if (selected == null) return null;
            var t = VrcReflection.Find(VrcReflection.AvatarDescriptor);
            if (t == null) return selected;
            for (var cur = selected.transform; cur != null; cur = cur.parent)
                if (cur.GetComponent(t) != null) return cur.gameObject;
            return selected;
        }

        /// <summary>Every GameObject in loaded scenes that has a VRC Avatar Descriptor (including inactive ones).</summary>
        public static List<GameObject> AvatarsInScene()
        {
            var t = VrcReflection.Find(VrcReflection.AvatarDescriptor);
            var list = new List<GameObject>();
            if (t == null) return list;
            foreach (var o in Resources.FindObjectsOfTypeAll(t))
            {
                var c = o as Component;
                if (c == null || EditorUtility.IsPersistent(c) || c.hideFlags != HideFlags.None) continue;
                if (!c.gameObject.scene.IsValid()) continue;
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

        static void CountMesh(CheckReport r, Dictionary<string, double> stats, Mesh mesh, Material[] mats, HashSet<Texture> textures, string path)
        {
            if (mesh == null)
            {
                r.Add(Severity.Warning, "no-mesh", "メッシュが設定されていません（Missing）。", path);
            }
            else
            {
                stats[P.Triangles] += Triangles(mesh);
                if (mats.Length > mesh.subMeshCount)
                    r.Add(Severity.Info, "extra-material-slot", "マテリアル枠 (" + mats.Length + ") がサブメッシュ数 (" + mesh.subMeshCount + ") より多く、余った枠の分だけ重くなります。", path);
            }
            stats[P.MaterialSlots] += mats.Length;
            int empty = 0;
            foreach (var m in mats)
            {
                if (m == null) { empty++; continue; }
                if (m.shader == null || m.shader.name == "Hidden/InternalErrorShader")
                    r.Add(Severity.Error, "broken-shader", "マテリアル「" + m.name + "」のシェーダーが見つかりません（ピンク表示）。シェーダーを導入してください。", path);
                foreach (var id in m.GetTexturePropertyNameIDs())
                {
                    var tex = m.GetTexture(id);
                    if (tex != null) textures.Add(tex);
                }
            }
            if (empty > 0)
                r.Add(Severity.Warning, "empty-material", "空のマテリアル枠が " + empty + " 個あります（ピンク表示になります）。", path);
        }

        static long Triangles(Mesh mesh)
        {
            long n = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var topo = mesh.GetTopology(i);
                if (topo == MeshTopology.Triangles) n += mesh.GetIndexCount(i) / 3;
                else if (topo == MeshTopology.Quads) n += mesh.GetIndexCount(i) / 4 * 2;
            }
            return n;
        }

        static double TextureMemoryMB(HashSet<Texture> textures)
        {
            long bytes = 0;
            foreach (var t in textures) bytes += Profiler.GetRuntimeMemorySizeLong(t);
            return bytes / (1024.0 * 1024.0);
        }

        // Transform/collision-check counts are an estimate of the SDK's numbers (endpoints are not added).
        static void CountPhysBones(CheckReport r, Dictionary<string, double> stats, List<Component> physBones, Transform avatarRoot)
        {
            foreach (var pb in physBones)
            {
                var path = PathOf(pb.transform, avatarRoot);
                var rootObj = VrcReflection.Get(pb, "rootTransform");
                var root = VrcReflection.IsNull(rootObj) ? pb.transform : (Transform)rootObj;
                if (root != avatarRoot && !root.IsChildOf(avatarRoot))
                    r.Add(Severity.Warning, "physbone-root-outside", "PhysBone の Root Transform がアバターの外を指しています。", path);
                var ignore = new HashSet<Transform>();
                foreach (var o in VrcReflection.Items(VrcReflection.Get(pb, "ignoreTransforms")))
                    if (!VrcReflection.IsNull(o)) ignore.Add((Transform)o);
                int transforms = CountUnder(root, ignore);
                if (transforms <= 1)
                    r.Add(Severity.Info, "physbone-no-chain", "PhysBone の対象が 1 本以下で、揺れません。Root Transform を確認してください。", path);
                int colliders = 0, nullColliders = 0;
                foreach (var o in VrcReflection.Items(VrcReflection.Get(pb, "colliders")))
                {
                    if (VrcReflection.IsNull(o)) nullColliders++; else colliders++;
                }
                if (nullColliders > 0)
                    r.Add(Severity.Warning, "physbone-null-collider", "PhysBone の Colliders に空の参照が " + nullColliders + " 個あります。", path);
                stats[P.PhysBoneTransforms] += transforms;
                stats[P.PhysBoneCollisionChecks] += (transforms - 1 > 0 ? transforms - 1 : 0) * colliders;
            }
        }

        static int CountUnder(Transform t, HashSet<Transform> ignore)
        {
            if (ignore.Contains(t) || t.CompareTag("EditorOnly")) return 0;
            int n = 1;
            for (int i = 0; i < t.childCount; i++) n += CountUnder(t.GetChild(i), ignore);
            return n;
        }

        static void CheckDescriptor(CheckReport r, GameObject root, Component descriptor, int descriptorsBelowRoot)
        {
            if (!r.VrcSdkFound)
            {
                r.Add(Severity.Info, "no-vrc-sdk", "VRChat Avatars SDK が見つからないため、PhysBone・Contact・Expression パラメータは数えていません。");
                return;
            }
            r.HasAvatarDescriptor = descriptor != null;
            if (descriptor == null)
                r.Add(Severity.Warning, "no-avatar-descriptor", "選んだオブジェクトに VRC Avatar Descriptor がありません。アバターのルートを選んでください。");
            if (descriptorsBelowRoot > 0)
                r.Add(Severity.Error, "nested-avatar-descriptor", "アバターの子に VRC Avatar Descriptor が " + descriptorsBelowRoot + " 個あります。衣装プレハブ側の Descriptor は削除してください。");
            if (descriptor == null) return;

            var view = VrcReflection.Get(descriptor, "ViewPosition");
            if (view is Vector3 && ((Vector3)view).y <= 0f)
                r.Add(Severity.Warning, "view-position", "View Position（視点）の高さが 0 以下です。目の位置に合わせてください。");

            var custom = VrcReflection.Get(descriptor, "customExpressions");
            var parameters = VrcReflection.Get(descriptor, "expressionParameters");
            var menu = VrcReflection.Get(descriptor, "expressionsMenu");
            if (custom is bool && (bool)custom)
            {
                if (VrcReflection.IsNull(parameters))
                    r.Add(Severity.Warning, "no-expression-parameters", "Expressions は有効ですが Parameters が未設定です。");
                if (VrcReflection.IsNull(menu))
                    r.Add(Severity.Warning, "no-expression-menu", "Expressions は有効ですが Menu が未設定です。");
            }
            var cost = VrcReflection.CallInt(parameters, "CalcTotalCost");
            if (cost.HasValue)
            {
                r.ExpressionParameterCost = cost.Value;
                if (cost.Value > CheckReport.ExpressionParameterBudget)
                    r.Add(Severity.Error, "parameter-budget", "Expression パラメータが " + cost.Value + " / " + CheckReport.ExpressionParameterBudget + " bit で上限を超えています。アップロードできません。");
                else if (cost.Value > CheckReport.ExpressionParameterBudget * 0.9)
                    r.Add(Severity.Info, "parameter-budget-near", "Expression パラメータが " + cost.Value + " / " + CheckReport.ExpressionParameterBudget + " bit で上限に近づいています。");
            }
        }

        static void CheckAnimator(CheckReport r, GameObject root, List<Animator> animators)
        {
            var a = root.GetComponent<Animator>();
            if (a == null)
            {
                r.Add(Severity.Warning, "no-root-animator", "アバターのルートに Animator がありません。");
                return;
            }
            if (a.avatar == null)
                r.Add(Severity.Warning, "no-unity-avatar", "ルートの Animator に Avatar（ボーン定義）が設定されていません。");
            else if (!a.avatar.isHuman)
                r.Add(Severity.Info, "not-humanoid", "Avatar が Humanoid ではありません（Generic）。フルトラや表情の一部が使えません。");
            int extra = animators.Count(x => x != a);
            if (extra > 0)
                r.Add(Severity.Info, "extra-animators", "ルート以外に Animator が " + extra + " 個あります。衣装プレハブに残った Animator は削除すると軽くなります。");
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
