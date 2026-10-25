using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Naoking.ClaudeCreatorTools
{
    public static partial class CctCaptureViews
    {
        /// <summary>Axis-aligned box in frame space.</summary>
        struct Box
        {
            public Vector3 Min, Max;
            public bool Valid;

            public void Add(Vector3 p)
            {
                if (!Valid)
                {
                    Min = Max = p;
                    Valid = true;
                    return;
                }
                Min = Vector3.Min(Min, p);
                Max = Vector3.Max(Max, p);
            }

            public void Add(Box b)
            {
                if (!b.Valid) return;
                Add(b.Min);
                Add(b.Max);
            }

            public Vector3 Center => (Min + Max) * 0.5f;
            public Vector3 Size => Max - Min;

            public bool Intersects(Box o) =>
                Valid && o.Valid &&
                Min.x <= o.Max.x && Max.x >= o.Min.x &&
                Min.y <= o.Max.y && Max.y >= o.Min.y &&
                Min.z <= o.Max.z && Max.z >= o.Min.z;

            public static Box FromMinMax(Vector3 min, Vector3 max) => new Box { Min = Vector3.Min(min, max), Max = Vector3.Max(min, max), Valid = true };

            public static Box FromCenterSize(Vector3 center, Vector3 size) => FromMinMax(center - size * 0.5f, center + size * 0.5f);

            public Vector3[] Corners()
            {
                var c = new Vector3[8];
                int k = 0;
                for (int x = 0; x < 2; x++)
                for (int y = 0; y < 2; y++)
                for (int z = 0; z < 2; z++)
                    c[k++] = new Vector3(x == 0 ? Min.x : Max.x, y == 0 ? Min.y : Max.y, z == 0 ? Min.z : Max.z);
                return c;
            }

            public Dictionary<string, object> ToPlain() => Valid
                ? new Dictionary<string, object> { ["center"] = Rounded(Center, 4), ["size"] = Rounded(Size, 4) }
                : null;
        }

        /// <summary>Humanoid landmarks in world space (from Animator.GetBoneTransform or the Avatar's HumanDescription).</summary>
        sealed class HumanoidInfo
        {
            public Vector3? Head, Neck, Hips, Eyes, ViewPosition;
            public readonly List<Vector3> Bones = new List<Vector3>();
            public string Source;
            public string AnimatorPath;
        }

        /// <summary>Camera pose in frame space.</summary>
        sealed class CameraPose
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 LookAt;
            public float Fov, Near, Far, Distance;
        }

        sealed class FramingInfo
        {
            public Box Full, Upper, Face;
            public string FaceSource, UpperSource, HumanoidAnimator, HumanoidSource;
            public int RenderersUsed;
            public readonly List<string> Excluded = new List<string>();

            public Box BoxFor(string framing) => framing == "face" ? Face : framing == "upper" ? Upper : Full;

            public Dictionary<string, object> ToPlain() => new Dictionary<string, object>
            {
                ["space"] = "frame (+Z = target forward, +Y = up, origin = target pivot), metres",
                ["full"] = Full.ToPlain(),
                ["upper"] = Upper.ToPlain(),
                ["face"] = Face.ToPlain(),
                ["face_source"] = FaceSource,
                ["upper_source"] = UpperSource,
                ["humanoid_animator"] = HumanoidAnimator,
                ["humanoid_source"] = HumanoidSource,
                ["renderers_used"] = RenderersUsed,
                ["excluded_renderers"] = Excluded.Take(20).Cast<object>().ToList(),
                ["excluded_omitted"] = Math.Max(0, Excluded.Count - 20),
            };
        }

        // ---------------------------------------------------------------- humanoid

        static HumanoidInfo FindHumanoid(GameObject root, bool persistent)
        {
            if (root == null) return null;
            Animator animator = root.GetComponent<Animator>();
            if (!IsHumanAnimator(animator))
                animator = root.GetComponentsInChildren<Animator>(true).FirstOrDefault(IsHumanAnimator);
            // A part of an avatar (hair, outfit, body mesh) has no Animator of its own: use the enclosing avatar's skeleton.
            // Bone positions are world space, and an isolated clone sits at the source's world pose, so they stay valid.
            for (var p = root.transform.parent; animator == null && p != null; p = p.parent)
            {
                var a = p.GetComponent<Animator>();
                if (IsHumanAnimator(a)) animator = a;
            }
            if (animator == null) return null;

            var info = new HumanoidInfo { AnimatorPath = CctTargets.GetPath(animator.transform) };
            Dictionary<string, Transform> byHumanName = null;
            bool usedDescription = false;

            Transform Bone(HumanBodyBones bone)
            {
                Transform t = null;
                if (!persistent)
                {
                    try { t = animator.GetBoneTransform(bone); }
                    catch (Exception) { t = null; }
                }
                if (t != null) return t;
                if (byHumanName == null) byHumanName = BuildHumanMap(animator);
                var key = HumanTrait.BoneName[(int)bone];
                if (byHumanName.TryGetValue(key, out t) && t != null)
                {
                    usedDescription = true;
                    return t;
                }
                return null;
            }

            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var t = Bone((HumanBodyBones)i);
                if (t == null) continue;
                info.Bones.Add(t.position);
                switch ((HumanBodyBones)i)
                {
                    case HumanBodyBones.Head: info.Head = t.position; break;
                    case HumanBodyBones.Neck: info.Neck = t.position; break;
                    case HumanBodyBones.Hips: info.Hips = t.position; break;
                }
            }
            var le = Bone(HumanBodyBones.LeftEye);
            var re = Bone(HumanBodyBones.RightEye);
            if (le != null && re != null) info.Eyes = (le.position + re.position) * 0.5f;
            else if (le != null) info.Eyes = le.position;
            else if (re != null) info.Eyes = re.position;

            info.ViewPosition = ReadViewPosition(animator.gameObject) ?? ReadViewPosition(root);
            info.Source = usedDescription ? "human_description" : "animator";
            return info.Bones.Count > 0 ? info : null;
        }

        static bool IsHumanAnimator(Animator a) => a != null && a.avatar != null && a.avatar.isHuman;

        /// <summary>HumanDescription human name ("Head", "Left Thumb Proximal", ...) -> transform found by bone name under the animator.</summary>
        static Dictionary<string, Transform> BuildHumanMap(Animator animator)
        {
            var map = new Dictionary<string, Transform>(StringComparer.Ordinal);
            HumanBone[] human;
            try { human = animator.avatar.humanDescription.human; }
            catch (Exception) { return map; }
            if (human == null) return map;
            var byName = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (var t in animator.GetComponentsInChildren<Transform>(true))
                if (!byName.ContainsKey(t.name)) byName[t.name] = t; // breadth is not guaranteed; first match wins
            foreach (var hb in human)
            {
                if (string.IsNullOrEmpty(hb.humanName) || string.IsNullOrEmpty(hb.boneName)) continue;
                if (byName.TryGetValue(hb.boneName, out var t)) map[hb.humanName] = t;
            }
            return map;
        }

        /// <summary>VRCAvatarDescriptor.ViewPosition (local to the descriptor's transform) in world space, if present.</summary>
        static Vector3? ReadViewPosition(GameObject go)
        {
            if (go == null) return null;
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null || !CctSerialized.IsType(c, CctVrcTypes.AvatarDescriptor)) continue;
                using (var so = new SerializedObject(c))
                {
                    var p = so.FindProperty("ViewPosition");
                    if (p == null || p.propertyType != SerializedPropertyType.Vector3) return null;
                    return go.transform.TransformPoint(p.vector3Value);
                }
            }
            return null;
        }

        // ---------------------------------------------------------------- framing boxes

        static FramingInfo ComputeFraming(CaptureSession s, Options opt, CctResult r)
        {
            var frame = s.Frame;
            var hum = s.Humanoid;
            var f = new FramingInfo { HumanoidAnimator = hum?.AnimatorPath, HumanoidSource = hum?.Source };

            var boxes = new List<KeyValuePair<FramingRenderer, Box>>();
            foreach (var fr in s.FramingRenderers)
            {
                var b = new Box();
                foreach (var p in fr.Corners) b.Add(frame.ToLocal(p));
                boxes.Add(new KeyValuePair<FramingRenderer, Box>(fr, b));
            }

            // Renderers nowhere near the skeleton (parked effects, giant planes, world-fixed props) would shrink the
            // avatar to a dot; they are left out of auto framing but still rendered.
            var include = new Box();
            if (hum != null && hum.Bones.Count >= 4)
            {
                var bones = new Box();
                foreach (var b in hum.Bones) bones.Add(frame.ToLocal(b));
                float margin = Mathf.Max(0.25f, 0.35f * Mathf.Max(bones.Size.y, Mathf.Max(bones.Size.x, bones.Size.z)));
                include = Box.FromMinMax(bones.Min - Vector3.one * margin, bones.Max + Vector3.one * margin);
            }
            foreach (var kv in boxes)
            {
                if (include.Valid && !kv.Value.Intersects(include))
                {
                    f.Excluded.Add(kv.Key.Path);
                    continue;
                }
                f.Full.Add(kv.Value);
                f.RenderersUsed++;
            }
            if (!f.Full.Valid)
            {
                f.Excluded.Clear();
                f.RenderersUsed = 0;
                foreach (var kv in boxes)
                {
                    f.Full.Add(kv.Value);
                    f.RenderersUsed++;
                }
            }
            if (f.Excluded.Count > 0)
                r.AddIssue(CctSeverity.Info, "FRAMING_OUTLIERS_EXCLUDED",
                    $"骨格から離れた Renderer {f.Excluded.Count} 個を構図の計算から除外しました (描画はされます): " +
                    string.Join(", ", f.Excluded.Take(5)) + (f.Excluded.Count > 5 ? " ..." : ""), value: f.Excluded.Count);

            var full = f.Full;
            float height = Mathf.Max(full.Size.y, 0.01f);

            // Upper body: from the hips (or 45% height) to the top.
            if (hum?.Hips != null)
            {
                var hips = frame.ToLocal(hum.Hips.Value);
                float bottom = Mathf.Clamp(hips.y, full.Min.y, full.Max.y - 0.1f * height);
                f.Upper = Box.FromMinMax(new Vector3(full.Min.x, bottom, full.Min.z), full.Max);
                f.UpperSource = "hips_bone";
            }
            else
            {
                f.Upper = Box.FromMinMax(new Vector3(full.Min.x, full.Min.y + 0.45f * height, full.Min.z), full.Max);
                f.UpperSource = "bounds_upper_55pct";
            }

            // Face: head bone (+ eyes / VRChat view position) based; fallback top 15% of the bounds.
            bool needHead = opt.Framing == "face" || opt.Views.Any(v => v.Face);
            if (hum?.Head != null)
            {
                var h = frame.ToLocal(hum.Head.Value);
                float below = Mathf.Max(0.05f, h.y - full.Min.y);
                Vector3? eye = hum.Eyes.HasValue ? frame.ToLocal(hum.Eyes.Value)
                    : hum.ViewPosition.HasValue ? frame.ToLocal(hum.ViewPosition.Value) : (Vector3?)null;
                float top, bottom;
                Vector3 center;
                if (eye.HasValue && eye.Value.y > h.y + 0.01f)
                {
                    float d = Mathf.Max(eye.Value.y - h.y, 0.05f * below);
                    top = Mathf.Min(full.Max.y, eye.Value.y + 1.6f * d);
                    bottom = h.y - 0.35f * d;
                    center = new Vector3((h.x + eye.Value.x) * 0.5f, 0f, (h.z + eye.Value.z) * 0.5f);
                    f.FaceSource = hum.Eyes.HasValue ? "head_and_eye_bones" : "head_bone_and_view_position";
                }
                else
                {
                    top = Mathf.Min(full.Max.y, h.y + 0.3f * below);
                    if (top < h.y + 0.08f * below) top = h.y + 0.2f * below;
                    bottom = h.y - 0.4f * (top - h.y);
                    center = new Vector3(h.x, 0f, h.z);
                    f.FaceSource = "head_bone";
                }
                if (top <= bottom) top = bottom + 0.2f * below;
                float side = top - bottom;
                center.y = (top + bottom) * 0.5f;
                f.Face = Box.FromCenterSize(center, new Vector3(side, side, side));
            }
            else
            {
                float side = Mathf.Max(0.15f * height, 0.01f);
                var c = full.Center;
                float horizontal = Mathf.Min(Mathf.Max(full.Size.x, full.Size.z), side * 1.5f);
                f.Face = Box.FromMinMax(new Vector3(c.x - horizontal * 0.5f, full.Max.y - side, c.z - horizontal * 0.5f),
                    new Vector3(c.x + horizontal * 0.5f, full.Max.y, c.z + horizontal * 0.5f));
                f.FaceSource = "bounds_top_15pct";
                if (needHead)
                    r.AddIssue(CctSeverity.Warning, "NO_HUMANOID_HEAD",
                        "Humanoid の Animator (Head ボーン) が対象にも親にも見つからないため、顔の構図は全体の上 15% で代用しました (Humanoid でないモデルでは顔の位置を推定できません)",
                        path: CctTargets.GetPath(s.Source));
            }
            if (hum?.Hips == null && opt.Framing == "upper")
                r.AddIssue(CctSeverity.Warning, "NO_HUMANOID_HEAD",
                    "Humanoid の Hips ボーンが見つからないため、上半身の構図は全体の上 55% で代用しました", path: CctTargets.GetPath(s.Source));
            return f;
        }

        // ---------------------------------------------------------------- camera fitting

        /// <summary>
        /// Perspective fit of <paramref name="box"/> (frame space) for a square image: the camera looks along -dir and is
        /// placed so every box corner projects inside the frame minus <paramref name="padding"/> on each side, then the
        /// look target is shifted so the projected extent is centred. Near/far come from the full bounds.
        /// </summary>
        static CameraPose FitCamera(Box box, Box full, ViewDef view, float fov, float padding, bool sceneMode)
        {
            var dir = Direction(view.Azimuth, view.Elevation);
            var upHint = Mathf.Abs(view.Elevation) > 89f ? Vector3.forward : Vector3.up; // top view: target front at image top
            var rot = Quaternion.LookRotation(-dir, upHint);
            var right = rot * Vector3.right;
            var up = rot * Vector3.up;
            float t = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * Mathf.Max(0.2f, 1f - 2f * padding);
            var corners = box.Corners();
            var center = box.Center;
            float dist = 0f;
            for (int iter = 0; iter < 4; iter++)
            {
                dist = 0f;
                float zmax = float.MinValue;
                foreach (var p in corners)
                {
                    var rel = p - center;
                    float x = Vector3.Dot(rel, right), y = Vector3.Dot(rel, up), z = Vector3.Dot(rel, dir);
                    dist = Mathf.Max(dist, z + Mathf.Max(Mathf.Abs(x), Mathf.Abs(y)) / t);
                    zmax = Mathf.Max(zmax, z);
                }
                dist = Mathf.Max(dist, zmax + 0.05f);

                float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                foreach (var p in corners)
                {
                    var rel = p - center;
                    float depth = Mathf.Max(1e-4f, dist - Vector3.Dot(rel, dir));
                    float px = Vector3.Dot(rel, right) / depth, py = Vector3.Dot(rel, up) / depth;
                    minX = Mathf.Min(minX, px);
                    maxX = Mathf.Max(maxX, px);
                    minY = Mathf.Min(minY, py);
                    maxY = Mathf.Max(maxY, py);
                }
                float cx = (minX + maxX) * 0.5f, cy = (minY + maxY) * 0.5f;
                if (Mathf.Abs(cx) < 1e-4f && Mathf.Abs(cy) < 1e-4f) break;
                center += right * (cx * dist) + up * (cy * dist);
            }

            var position = center + dir * dist;
            float nearest = float.MaxValue, farthest = 0f;
            foreach (var p in full.Corners())
            {
                float depth = Vector3.Dot(p - position, -dir);
                nearest = Mathf.Min(nearest, depth);
                farthest = Mathf.Max(farthest, depth);
            }
            float near = Mathf.Clamp(nearest * 0.5f, 0.01f, 10f);
            float far = Mathf.Max(farthest * 1.5f + 1f, near * 10f);
            if (sceneMode) far = Mathf.Max(far, 200f); // show the surroundings; depth precision stays adequate with near >= 1cm
            return new CameraPose
            {
                Position = position,
                Rotation = rot,
                LookAt = center,
                Fov = fov,
                Near = near,
                Far = far,
                Distance = dist,
            };
        }

        static bool SamePose(CameraPose a, CameraPose b) =>
            (a.Position - b.Position).magnitude < 1e-3f &&
            Quaternion.Angle(a.Rotation, b.Rotation) < 0.05f &&
            Mathf.Abs(a.Fov - b.Fov) < 0.01f;

        /// <summary>Reads the frame-relative camera pose stored by a previous capture's manifest view entry.</summary>
        static bool TryReadPose(Dictionary<string, object> view, out CameraPose pose)
        {
            pose = null;
            if (view == null || !view.TryGetValue("camera", out var co) || !(co is Dictionary<string, object> cam)) return false;
            if (!TryReadFloats(cam, "local_position", 3, out var p) || !TryReadFloats(cam, "local_rotation", 4, out var q)) return false;
            var rot = new Quaternion(q[0], q[1], q[2], q[3]);
            float mag = Mathf.Sqrt(rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w);
            if (mag < 1e-4f) return false;
            rot = new Quaternion(rot.x / mag, rot.y / mag, rot.z / mag, rot.w / mag);
            var position = new Vector3(p[0], p[1], p[2]);
            float fov = ReadFloat(cam, "fov", DefaultFov);
            float distance = ReadFloat(cam, "distance", 1f);
            Vector3 lookAt = TryReadFloats(cam, "local_look_at", 3, out var la)
                ? new Vector3(la[0], la[1], la[2])
                : position + rot * Vector3.forward * distance;
            pose = new CameraPose
            {
                Position = position,
                Rotation = rot,
                LookAt = lookAt,
                Fov = Mathf.Clamp(fov, 1f, 120f),
                Near = Mathf.Max(0.001f, ReadFloat(cam, "near", 0.01f)),
                Far = Mathf.Max(0.1f, ReadFloat(cam, "far", 100f)),
                Distance = distance,
            };
            if (pose.Far <= pose.Near) pose.Far = pose.Near * 1000f;
            return true;
        }

        static bool TryReadFloats(Dictionary<string, object> d, string key, int count, out float[] values)
        {
            values = null;
            if (!d.TryGetValue(key, out var v) || !(v is List<object> list) || list.Count < count) return false;
            values = new float[count];
            for (int i = 0; i < count; i++)
            {
                if (!IsNumber(list[i])) return false;
                values[i] = (float)Convert.ToDouble(list[i], CultureInfo.InvariantCulture);
                if (float.IsNaN(values[i]) || float.IsInfinity(values[i])) return false;
            }
            return true;
        }

        static float ReadFloat(Dictionary<string, object> d, string key, float fallback) =>
            d.TryGetValue(key, out var v) && IsNumber(v) ? (float)Convert.ToDouble(v, CultureInfo.InvariantCulture) : fallback;
    }
}
