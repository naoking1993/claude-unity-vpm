using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// Reads components of packages we do not compile against (VRChat SDK, Modular Avatar, AAO, UdonSharp)
    /// by full type name and SerializedObject property names. Missing types/properties yield null/defaults
    /// instead of exceptions, so the tools work in avatar projects, world projects and plain projects.
    /// </summary>
    public static class CctSerialized
    {
        /// <summary>True if the component's type or any base type has the given full name.</summary>
        public static bool IsType(Object o, string fullName)
        {
            if (o == null) return false;
            for (var t = o.GetType(); t != null && t != typeof(Object); t = t.BaseType)
                if (t.FullName == fullName) return true;
            return false;
        }

        public static bool IsAnyType(Object o, ICollection<string> fullNames)
        {
            if (o == null) return false;
            for (var t = o.GetType(); t != null && t != typeof(Object); t = t.BaseType)
                if (fullNames.Contains(t.FullName)) return true;
            return false;
        }

        public static bool NamespaceStartsWith(Object o, string prefix) =>
            o != null && (o.GetType().Namespace ?? "").StartsWith(prefix, StringComparison.Ordinal);

        /// <summary>All non-missing components under root (inactive included) whose type (or base) matches.</summary>
        public static List<Component> FindComponents(GameObject root, string fullName)
        {
            var list = new List<Component>();
            if (root == null) return list;
            foreach (var c in root.GetComponentsInChildren<Component>(true))
                if (c != null && IsType(c, fullName)) list.Add(c);
            return list;
        }

        /// <summary>Resolves a type loaded in the editor domain by full name (null when the package is absent).</summary>
        public static Type FindType(string fullName)
        {
            if (TypeCacheByName.TryGetValue(fullName, out var cached)) return cached;
            Type found = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                try { found = asm.GetType(fullName, false); }
                catch (Exception) { /* some assemblies refuse reflection; ignore */ }
                if (found != null) break;
            }
            TypeCacheByName[fullName] = found;
            return found;
        }

        static readonly Dictionary<string, Type> TypeCacheByName = new Dictionary<string, Type>();

        public static SerializedProperty Prop(SerializedObject so, string path) => so?.FindProperty(path);

        public static SerializedProperty Child(SerializedProperty p, string relative) => p?.FindPropertyRelative(relative);

        public static string GetString(SerializedProperty p, string fallback = null) =>
            p != null && p.propertyType == SerializedPropertyType.String ? p.stringValue : fallback;

        public static bool GetBool(SerializedProperty p, bool fallback = false) =>
            p != null && p.propertyType == SerializedPropertyType.Boolean ? p.boolValue : fallback;

        /// <summary>Integer, enum (index -> intValue is the underlying value) or bool as int.</summary>
        public static int GetInt(SerializedProperty p, int fallback = 0)
        {
            if (p == null) return fallback;
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Enum:
                case SerializedPropertyType.Character:
                    return p.intValue;
                case SerializedPropertyType.Boolean:
                    return p.boolValue ? 1 : 0;
                case SerializedPropertyType.Float:
                    return (int)p.floatValue;
            }
            return fallback;
        }

        public static float GetFloat(SerializedProperty p, float fallback = 0f)
        {
            if (p == null) return fallback;
            switch (p.propertyType)
            {
                case SerializedPropertyType.Float: return p.floatValue;
                case SerializedPropertyType.Integer: return p.intValue;
            }
            return fallback;
        }

        public static Object GetObject(SerializedProperty p) =>
            p != null && p.propertyType == SerializedPropertyType.ObjectReference ? p.objectReferenceValue : null;

        /// <summary>
        /// A reference that points at something Unity cannot load (deleted asset / missing file):
        /// objectReferenceValue is null but the serialized instance ID is not.
        /// </summary>
        public static bool IsMissingReference(SerializedProperty p) =>
            p != null && p.propertyType == SerializedPropertyType.ObjectReference &&
            p.objectReferenceValue == null && p.objectReferenceInstanceIDValue != 0;

        public static int ArraySize(SerializedProperty p) => p != null && p.isArray ? p.arraySize : 0;

        public static IEnumerable<SerializedProperty> Elements(SerializedProperty array)
        {
            if (array == null || !array.isArray || array.propertyType == SerializedPropertyType.String) yield break;
            for (int i = 0; i < array.arraySize; i++) yield return array.GetArrayElementAtIndex(i);
        }

        /// <summary>Object references held in an array property (nulls kept so counts match the inspector).</summary>
        public static List<Object> GetObjectArray(SerializedProperty array) =>
            Elements(array).Select(GetObject).ToList();

        /// <summary>Enumerates every visible property of an object (for generic dumps / missing-reference scans).</summary>
        public static IEnumerable<SerializedProperty> AllProperties(SerializedObject so, bool enterChildren = true)
        {
            var it = so.GetIterator();
            bool enter = true;
            while (it.Next(enter))
            {
                enter = enterChildren;
                yield return it;
            }
        }

        /// <summary>Lists ObjectReference properties that are missing (see <see cref="IsMissingReference"/>).</summary>
        public static List<string> FindMissingReferencePaths(Object target, int max = 50)
        {
            var result = new List<string>();
            if (target == null) return result;
            using (var so = new SerializedObject(target))
            {
                foreach (var p in AllProperties(so))
                {
                    if (p.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (p.propertyPath == "m_Script" || p.propertyPath == "m_PrefabInstance" ||
                        p.propertyPath == "m_PrefabAsset" || p.propertyPath == "m_CorrespondingSourceObject") continue;
                    if (IsMissingReference(p))
                    {
                        result.Add(p.propertyPath);
                        if (result.Count >= max) break;
                    }
                }
            }
            return result;
        }
    }
}
