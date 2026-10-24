using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// Resolves the GameObject a tool should work on. Accepted target specs:
    ///   "" / null / "selection"    -> Selection.activeGameObject
    ///   "#12345" or "12345"         -> instance ID
    ///   "/Root/Child" or "Root/Child" -> hierarchy path in any loaded scene ("Scene.unity:/Root/Child" pins the scene)
    ///   "Name"                      -> unique GameObject name in loaded scenes (ambiguity is an error, never a guess)
    ///   "Assets/.../X.prefab"       -> prefab asset root (read-only use)
    /// </summary>
    public static class CctTargets
    {
        public static IEnumerable<Scene> LoadedScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.IsValid() && s.isLoaded) yield return s;
            }
        }

        public static IEnumerable<GameObject> AllGameObjects(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    yield return t.gameObject;
        }

        public static IEnumerable<GameObject> AllGameObjectsInLoadedScenes() =>
            LoadedScenes().SelectMany(AllGameObjects);

        /// <summary>"/Root/Child/Leaf" (always rooted at the scene root).</summary>
        public static string GetPath(Transform t)
        {
            if (t == null) return null;
            var names = new List<string>();
            for (var cur = t; cur != null; cur = cur.parent) names.Add(cur.name);
            names.Reverse();
            return "/" + string.Join("/", names);
        }

        public static string GetPath(GameObject go) => go == null ? null : GetPath(go.transform);

        /// <summary>Path relative to <paramref name="root"/> as used by AnimationClip bindings ("" for the root itself).</summary>
        public static string GetRelativePath(Transform root, Transform t)
        {
            if (t == null || root == null) return null;
            if (t == root) return "";
            if (!IsDescendantOf(t, root)) return null;
            var names = new List<string>();
            for (var cur = t; cur != null && cur != root; cur = cur.parent) names.Add(cur.name);
            names.Reverse();
            return string.Join("/", names);
        }

        public static bool IsDescendantOf(Transform t, Transform ancestor)
        {
            for (var cur = t; cur != null; cur = cur.parent)
                if (cur == ancestor) return true;
            return false;
        }

        /// <summary>Is the object active in hierarchy (works for prefab assets too, where activeInHierarchy is unreliable).</summary>
        public static bool IsEffectivelyActive(Transform t, Transform stopAt = null)
        {
            for (var cur = t; cur != null; cur = cur.parent)
            {
                if (!cur.gameObject.activeSelf) return false;
                if (cur == stopAt) break;
            }
            return true;
        }

        public sealed class Resolution
        {
            public GameObject GameObject;
            public string Error;
            public List<string> Candidates = new List<string>();
            public bool IsPrefabAsset;
            public bool Ok => GameObject != null;
        }

        public static Resolution Resolve(string spec)
        {
            var r = new Resolution();
            spec = spec?.Trim();
            if (string.IsNullOrEmpty(spec) || string.Equals(spec, "selection", StringComparison.OrdinalIgnoreCase))
            {
                r.GameObject = Selection.activeGameObject;
                if (r.GameObject == null) r.Error = "対象が指定されておらず、Hierarchy で何も選択されていません (target を指定してください)";
                r.IsPrefabAsset = r.GameObject != null && EditorUtility.IsPersistent(r.GameObject);
                return r;
            }

            var idText = spec.StartsWith("#", StringComparison.Ordinal) ? spec.Substring(1) : spec;
            if (int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                var obj = EditorUtility.InstanceIDToObject(id);
                r.GameObject = obj as GameObject ?? (obj as Component)?.gameObject;
                if (r.GameObject == null) r.Error = $"instance ID {id} の GameObject が見つかりません (ドメインリロードで ID は変わります)";
                r.IsPrefabAsset = r.GameObject != null && EditorUtility.IsPersistent(r.GameObject);
                return r;
            }

            if (spec.StartsWith("Assets/", StringComparison.Ordinal) || spec.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var colon = spec.IndexOf(".unity:", StringComparison.OrdinalIgnoreCase);
                if (colon < 0)
                {
                    r.GameObject = AssetDatabase.LoadAssetAtPath<GameObject>(spec);
                    r.IsPrefabAsset = r.GameObject != null;
                    if (r.GameObject == null) r.Error = $"アセット '{spec}' は GameObject (プレハブ) として読み込めません";
                    return r;
                }
                var scenePath = spec.Substring(0, colon + ".unity".Length);
                var inner = spec.Substring(colon + ".unity:".Length);
                var scene = LoadedScenes().FirstOrDefault(s => string.Equals(s.path, scenePath, StringComparison.OrdinalIgnoreCase));
                if (!scene.IsValid())
                {
                    r.Error = $"シーン '{scenePath}' は読み込まれていません";
                    return r;
                }
                return ResolveByPath(new[] { scene }, inner, r);
            }

            if (spec.Contains("/")) return ResolveByPath(LoadedScenes().ToArray(), spec, r);

            var byName = AllGameObjectsInLoadedScenes().Where(g => g.name == spec).ToList();
            if (byName.Count == 1) { r.GameObject = byName[0]; return r; }
            if (byName.Count == 0)
            {
                r.Error = $"名前 '{spec}' の GameObject は読み込み中のシーンにありません";
                return r;
            }
            r.Error = $"名前 '{spec}' の GameObject が {byName.Count} 個あります。パス (/親/子) か instance ID で指定してください";
            r.Candidates = byName.Take(20).Select(g => $"{GetPath(g)} (#{g.GetInstanceID()}, scene {g.scene.name})").ToList();
            return r;
        }

        static Resolution ResolveByPath(IEnumerable<Scene> scenes, string path, Resolution r)
        {
            var parts = path.Trim('/').Split('/');
            var matches = new List<GameObject>();
            foreach (var scene in scenes)
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name != parts[0]) continue;
                    CollectPath(root.transform, parts, 1, matches);
                }
            if (matches.Count == 1) { r.GameObject = matches[0]; return r; }
            if (matches.Count == 0) { r.Error = $"パス '{path}' の GameObject が見つかりません"; return r; }
            r.Error = $"パス '{path}' に一致する GameObject が {matches.Count} 個あります (同名の兄弟)。instance ID で指定してください";
            r.Candidates = matches.Take(20).Select(g => $"{GetPath(g)} (#{g.GetInstanceID()}, scene {g.scene.name})").ToList();
            return r;
        }

        static void CollectPath(Transform t, string[] parts, int index, List<GameObject> matches)
        {
            if (index == parts.Length) { matches.Add(t.gameObject); return; }
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c.name == parts[index]) CollectPath(c, parts, index + 1, matches);
            }
        }

        /// <summary>Short JSON-friendly description of a GameObject for reports.</summary>
        public static Dictionary<string, object> Describe(GameObject go)
        {
            if (go == null) return null;
            var d = new Dictionary<string, object>
            {
                ["name"] = go.name,
                ["path"] = GetPath(go),
                ["instance_id"] = go.GetInstanceID(),
                ["active_in_hierarchy"] = go.activeInHierarchy,
            };
            if (EditorUtility.IsPersistent(go)) d["asset"] = AssetDatabase.GetAssetPath(go);
            else d["scene"] = go.scene.path;
            return d;
        }

        /// <summary>Asset reference for reports: {name, type, asset} (asset omitted for scene objects).</summary>
        public static Dictionary<string, object> DescribeAsset(Object o)
        {
            if (o == null) return null;
            var d = new Dictionary<string, object>
            {
                ["name"] = o.name,
                ["type"] = o.GetType().Name,
            };
            var p = AssetDatabase.GetAssetPath(o);
            if (!string.IsNullOrEmpty(p)) d["asset"] = p;
            return d;
        }
    }
}
