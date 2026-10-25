using UnityEditor;
using UnityEngine;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// Converts Unity structs/objects to JSON-friendly values for <see cref="CctJson"/>. Tools should
    /// prefer putting plain values into results; this is a safety net so a stray Vector3 or asset does not
    /// end up as its ToString() (or, through Newtonsoft, as a self-referencing object graph).
    /// </summary>
    [InitializeOnLoad]
    public static class CctUnityValues
    {
        static CctUnityValues()
        {
            CctJson.ValueConverter = Convert;
        }

        public static object Convert(object v)
        {
            switch (v)
            {
                case Vector2 a: return new[] { R(a.x), R(a.y) };
                case Vector3 a: return new[] { R(a.x), R(a.y), R(a.z) };
                case Vector4 a: return new[] { R(a.x), R(a.y), R(a.z), R(a.w) };
                case Quaternion q: return Vec(q.eulerAngles);
                case Color c: return new[] { R(c.r), R(c.g), R(c.b), R(c.a) };
                case Bounds b: return new System.Collections.Generic.Dictionary<string, object> { ["center"] = Vec(b.center), ["size"] = Vec(b.size) };
                case GameObject go: return CctTargets.Describe(go);
                case Component comp: return new System.Collections.Generic.Dictionary<string, object>
                {
                    ["type"] = comp.GetType().FullName,
                    ["path"] = CctTargets.GetPath(comp.transform),
                };
                case Object o: return CctTargets.DescribeAsset(o);
            }
            return v;
        }

        public static double[] Vec(Vector3 a) => new[] { R(a.x), R(a.y), R(a.z) };

        /// <summary>Rounds to 4 decimals: enough for metres/degrees, keeps reports short.</summary>
        public static double R(float f) => System.Math.Round(f, 4);
    }
}
