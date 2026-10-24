using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Naoking.AvatarChecker
{
    /// <summary>
    /// Looks up VRChat SDK types by name so this package compiles and runs without the SDK installed
    /// (and keeps working across SDK versions without assembly references).
    /// </summary>
    internal static class VrcReflection
    {
        public const string AvatarDescriptor = "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor";
        public const string PhysBone = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone";
        public const string PhysBoneCollider = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider";
        public const string ContactSender = "VRC.SDK3.Dynamics.Contact.Components.VRCContactSender";
        public const string ContactReceiver = "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver";
        public const string ConstraintNamespace = "VRC.SDK3.Dynamics.Constraint.Components";

        static readonly Dictionary<string, Type> cache = new Dictionary<string, Type>();

        public static Type Find(string fullName)
        {
            Type t;
            if (cache.TryGetValue(fullName, out t)) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(fullName, false);
                if (t != null) break;
            }
            cache[fullName] = t;
            return t;
        }

        public static bool SdkFound { get { return Find(AvatarDescriptor) != null; } }

        public static bool Is(Component c, string fullName)
        {
            var t = Find(fullName);
            return t != null && c != null && t.IsInstanceOfType(c);
        }

        public static object Get(object obj, string member)
        {
            if (obj == null) return null;
            var type = obj.GetType();
            var f = type.GetField(member);
            if (f != null) return f.GetValue(obj);
            var p = type.GetProperty(member);
            return p != null && p.CanRead ? p.GetValue(obj, null) : null;
        }

        /// <summary>Unity objects compare equal to null after they are destroyed or never assigned.</summary>
        public static bool IsNull(object o)
        {
            var uo = o as UnityEngine.Object;
            return o == null || (o is UnityEngine.Object && uo == null);
        }

        public static IEnumerable<object> Items(object list)
        {
            var e = list as IEnumerable;
            if (e == null) yield break;
            foreach (var x in e) yield return x;
        }

        public static int? CallInt(object obj, string method)
        {
            if (IsNull(obj)) return null;
            var m = obj.GetType().GetMethod(method, Type.EmptyTypes);
            if (m == null) return null;
            var r = m.Invoke(obj, null);
            return r is int ? (int?)(int)r : null;
        }
    }
}
