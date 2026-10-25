using System;
using System.Collections.Generic;
using System.Linq;

namespace Naoking.ClaudeCreatorTools
{
    public static partial class CctWorldAudit
    {
        // ------------------------------------------------------------------ gimmicks (estimate by name)

        /// <summary>A community package / gimmick recognised by (lower-case) substrings of type, assembly or package names.</summary>
        sealed class KnownGimmick
        {
            public string Name, Kind;
            public string[] Patterns;
        }

        // Names only (no compile-time dependency, nothing verified against these packages' sources): a pattern that does not
        // match simply means "not detected". The docs name VideoTXL / ProTV / USharpVideo as community video players
        // (docs/worlds/udon/video-players/index.md L5-9) and AudioLink as the community audio-reactive package
        // (releases/release-3.10.5.md L18-21); the others are common in the user's worlds (research/user-skills-context.md 2.3).
        static readonly KnownGimmick[] KnownGimmicks =
        {
            new KnownGimmick { Name = "AudioLink", Kind = "audio_reactive", Patterns = new[] { "audiolink" } },
            new KnownGimmick { Name = "LTCGI", Kind = "lighting", Patterns = new[] { "ltcgi" } },
            new KnownGimmick { Name = "VRC Light Volumes", Kind = "lighting", Patterns = new[] { "vrclightvolumes" } },
            new KnownGimmick { Name = "ProTV", Kind = "video_player", Patterns = new[] { "protv" } },
            new KnownGimmick { Name = "VideoTXL", Kind = "video_player", Patterns = new[] { "videotxl", "texelsaur" } },
            new KnownGimmick { Name = "USharpVideo", Kind = "video_player", Patterns = new[] { "usharpvideo" } },
            new KnownGimmick { Name = "VizVid", Kind = "video_player", Patterns = new[] { "vvmw", "vizvid" } },
            new KnownGimmick { Name = "iwaSync", Kind = "video_player", Patterns = new[] { "iwasync" } },
            new KnownGimmick { Name = "YamaPlayer", Kind = "video_player", Patterns = new[] { "yamaplayer" } },
            new KnownGimmick { Name = "QvPen", Kind = "pen", Patterns = new[] { "qvpen" } },
            new KnownGimmick { Name = "Bakery", Kind = "lightmapper (editor)", Patterns = new[] { "bakery", "ftlightmaps" } },
            new KnownGimmick { Name = "EasyQuestSwitch", Kind = "cross_platform (editor)", Patterns = new[] { "easyquestswitch" } },
        };

        sealed class GimmickHit
        {
            public KnownGimmick Gimmick;
            public int Components;
            public readonly List<string> Examples = new List<string>();
            public readonly HashSet<string> Types = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<string> Packages = new List<string>();
        }

        /// <summary>
        /// Major gimmicks in the audited scene (VRChat video players, U# classes grouped by namespace, and well-known community
        /// packages by name) so Claude knows what the world is built from before touching it. Read-only, one pass over the
        /// component list that the scan already built.
        /// </summary>
        static void AuditGimmicks(Ctx ctx)
        {
            var g = new Dictionary<string, object>();
            ctx.R.Put("gimmicks", g);

            var matchCache = new Dictionary<Type, int[]>();
            var hits = new Dictionary<KnownGimmick, GimmickHit>();
            var usharpByNamespace = new Dictionary<string, (int behaviours, HashSet<string> classes)>(StringComparer.Ordinal);
            foreach (var item in ctx.Components)
            {
                if (!item.Audited || item.C == null) continue;
                var type = item.C.GetType();
                if (!matchCache.TryGetValue(type, out var matched))
                {
                    var key = ((type.FullName ?? type.Name) + " " + (SafeAssemblyName(type) ?? "")).ToLowerInvariant();
                    matched = Enumerable.Range(0, KnownGimmicks.Length)
                        .Where(i => KnownGimmicks[i].Patterns.Any(p => key.IndexOf(p, StringComparison.Ordinal) >= 0)).ToArray();
                    matchCache[type] = matched;
                }
                foreach (var i in matched)
                {
                    var kg = KnownGimmicks[i];
                    if (!hits.TryGetValue(kg, out var hit)) hits[kg] = hit = new GimmickHit { Gimmick = kg };
                    hit.Components++;
                    hit.Types.Add(type.Name);
                    if (hit.Examples.Count < 3 && !hit.Examples.Contains(item.O.Path)) hit.Examples.Add(item.O.Path);
                }
                if (Classify(type).USharp)
                {
                    var ns = string.IsNullOrEmpty(type.Namespace) ? "(global)" : type.Namespace;
                    if (!usharpByNamespace.TryGetValue(ns, out var e)) e = (0, new HashSet<string>(StringComparer.Ordinal));
                    e.classes.Add(type.Name);
                    usharpByNamespace[ns] = (e.behaviours + 1, e.classes);
                }
            }
            foreach (var pkg in ctx.Packages)
            {
                var key = ((pkg.Name ?? "") + " " + (pkg.DisplayName ?? "")).ToLowerInvariant();
                foreach (var kg in KnownGimmicks)
                {
                    if (!kg.Patterns.Any(p => key.IndexOf(p, StringComparison.Ordinal) >= 0)) continue;
                    if (!hits.TryGetValue(kg, out var hit)) hits[kg] = hit = new GimmickHit { Gimmick = kg };
                    hit.Packages.Add(pkg.Name + "@" + pkg.Version);
                }
            }

            int vrcPlayers = ctx.Audited(K.UnityVideo | K.AvProVideo).Count();
            g["vrc_video_players"] = new Dictionary<string, object>
            {
                ["unity"] = ctx.Audited(K.UnityVideo).Count(),
                ["avpro"] = ctx.Audited(K.AvProVideo).Count(),
                ["total"] = vrcPlayers,
            };
            g["detected"] = hits.Values.OrderByDescending(h => h.Components).ThenBy(h => h.Gimmick.Name, StringComparer.Ordinal)
                .Select(h => (object)new Dictionary<string, object>
                {
                    ["name"] = h.Gimmick.Name,
                    ["kind"] = h.Gimmick.Kind,
                    ["in_scene"] = h.Components > 0,
                    ["components"] = h.Components,
                    ["types"] = h.Types.OrderBy(t => t, StringComparer.Ordinal).Take(6).Cast<object>().ToList(),
                    ["examples"] = h.Examples.Cast<object>().ToList(),
                    ["packages"] = h.Packages.Cast<object>().ToList(),
                }).ToList();
            g["usharp_namespaces"] = usharpByNamespace.OrderByDescending(kv => kv.Value.behaviours).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(ctx.TopN).Select(kv => (object)new Dictionary<string, object>
                {
                    ["namespace"] = kv.Key,
                    ["behaviours"] = kv.Value.behaviours,
                    ["classes"] = kv.Value.classes.OrderBy(c => c, StringComparer.Ordinal).Take(8).Cast<object>().ToList(),
                }).ToList();
            if (usharpByNamespace.Count > ctx.TopN) g["usharp_namespaces_omitted"] = usharpByNamespace.Count - ctx.TopN;
            g["note"] = "型名・アセンブリ名・パッケージ名による推定です。一覧に無いギミックは usharp_namespaces (U# クラスの名前空間) を見てください";
        }

        // ------------------------------------------------------------------ verdict

        // Error codes the SDK / UdonSharp actually stop on (research/world-requirements.md 2.6 A-D: Builder gates, U# build
        // callbacks, Build() re-validation, exporter dialogs, PostBuildValidator) and the one Upload() refuses (2.6 E6).
        static readonly string[] SdkBuildBlockerCodes =
        {
            "NO_SCENE_DESCRIPTOR", "MULTIPLE_SCENE_DESCRIPTORS", "DESCRIPTOR_NOT_IN_UPLOAD_SCENE", "PIPELINE_MANAGER_MISSING",
            "MULTIPLE_PIPELINE_MANAGERS", "PREFAB_STAGE_SDK_COMPONENTS", "NO_SPAWNS", "LAYERS_NOT_SETUP", "COLLISION_MATRIX_NOT_SETUP",
            "SDK2_EVENT_HANDLER", "SDK2_COMPONENT", "OBJECT_SYNC_MANUAL_UDON", "OBJECT_SYNC_WITH_POOL", "ANDROID_TEXTURE_FORMAT_NOT_ASTC",
            "SCRIPT_COMPILATION_FAILED", "USHARP_PROGRAM_NO_SOURCE", "USHARP_SCRIPT_NOT_IN_USHARP_ASSEMBLY", "USHARP_DUPLICATE_PROGRAM_ASSET",
        };
        static readonly string[] SdkUploadBlockerCodes = { "BUNDLE_TOO_LARGE_MOBILE" };

        /// <summary>
        /// Answers "will the SDK build/upload this?" from the issues: blockers are error-severity issues whose rule mirrors an SDK
        /// or UdonSharp stop condition; every other error is listed separately (broken in VRChat but not stopping the build).
        /// </summary>
        static void PutVerdict(Ctx ctx)
        {
            var errorCodes = ctx.R.Issues
                .Where(i => (i.TryGetValue("severity", out var s) ? s as string : null) == CctSeverity.Error)
                .Select(i => i.TryGetValue("code", out var c) ? c as string : null)
                .Where(c => c != null).Distinct(StringComparer.Ordinal).ToList();
            List<object> Entries(IEnumerable<string> codes) =>
                codes.OrderBy(c => c, StringComparer.Ordinal)
                    .Select(c => (object)new Dictionary<string, object> { ["code"] = c, ["count"] = Math.Max(1, ctx.IssueCount(c)) }).ToList();
            var build = errorCodes.Where(c => SdkBuildBlockerCodes.Contains(c)).ToList();
            var upload = errorCodes.Where(c => SdkUploadBlockerCodes.Contains(c)).ToList();
            var other = errorCodes.Where(c => !SdkBuildBlockerCodes.Contains(c) && !SdkUploadBlockerCodes.Contains(c)).ToList();
            ctx.R.Put("verdict", new Dictionary<string, object>
            {
                ["sdk_build_blocked"] = build.Count > 0,
                ["build_blockers"] = Entries(build),
                ["upload_blockers"] = Entries(upload),
                ["other_errors"] = Entries(other),
                ["note"] = "build_blockers / upload_blockers は SDK と UdonSharp が実際にビルド・アップロードを止める条件 (このツールで判定できるもの) だけです。" +
                           "other_errors はビルドは通っても VRChat で壊れている可能性が高いものです。アカウント権限・Network ID・実ビルドのサイズなどは not_checked を参照",
            });
            ctx.VerdictText = build.Count > 0
                ? $"SDK のビルドが止まる問題 {build.Count} 種類 ({string.Join(", ", build.Take(4))}{(build.Count > 4 ? " ほか" : "")})"
                : upload.Count > 0
                    ? $"ビルドは通る見込みですがアップロードが拒否される問題があります ({string.Join(", ", upload)})"
                    : "SDK のビルド検証で止まる問題は見つかりませんでした";
        }
    }
}
