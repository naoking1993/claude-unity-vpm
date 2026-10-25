using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// cct_environment: what Claude should know before touching a project - Unity version, project path,
    /// open scenes and dirty state, play mode, VRChat SDK / MA / NDMF / AAO / lilToon / UdonSharp versions,
    /// and which avatar/world roots exist. Read-only.
    ///
    /// Issue codes (stable):
    /// | code                | severity | meaning |
    /// |---------------------|----------|---------|
    /// | PLAY_MODE           | warning  | the editor is in Play mode; changes made now are lost when Play stops |
    /// | SCENE_UNSAVED       | info     | a loaded scene has unsaved changes (value = scene path) |
    /// | PACKAGE_LIST_FAILED | warning  | PackageInfo.GetAllRegisteredPackages threw; data.packages is empty |
    /// data.project_kind: "avatar" | "world" | "avatar+world" | "no_vrchat_sdk" (which VRChat SDK component types exist).
    /// </summary>
    public static class CctEnvironment
    {
        public const string ToolName = "cct_environment";

        static readonly string[] InterestingPackages =
        {
            "com.vrchat.base", "com.vrchat.avatars", "com.vrchat.worlds", "com.vrchat.udonsharp",
            "com.vrchat.clientsim", "nadena.dev.modular-avatar", "nadena.dev.ndmf",
            "com.anatawa12.avatar-optimizer", "jp.lilxyzw.liltoon", "com.poiyomi.toon",
            "com.coplaydev.unity-mcp", "io.github.naoking1993.claude-creator-tools",
            "vrchat.blackstartx.gesture-manager", "com.vrcfury.vrcfury", "jp.lilxyzw.lilavatarutils",
        };

        public static CctResult Run(CctArgs args)
        {
            var r = CctResult.Ok("環境情報を取得しました");

            r.Put("unity_version", Application.unityVersion);
            r.Put("project_root", CctPaths.ProjectRoot);
            r.Put("platform", EditorUserBuildSettings.activeBuildTarget.ToString());
            r.Put("color_space", PlayerSettings.colorSpace.ToString());
            r.Put("play_mode", EditorApplication.isPlaying ? (EditorApplication.isPaused ? "paused" : "playing") : "edit");
            r.Put("is_compiling", EditorApplication.isCompiling);
            if (EditorApplication.isPlaying)
                r.AddIssue(CctSeverity.Warning, "PLAY_MODE", "Play モード中です。ここでの変更は Play 終了で失われます");

            var scenes = new List<object>();
            foreach (var s in CctTargets.LoadedScenes())
            {
                scenes.Add(new Dictionary<string, object>
                {
                    ["name"] = s.name,
                    ["path"] = s.path,
                    ["is_dirty"] = s.isDirty,
                    ["root_count"] = s.rootCount,
                    ["active"] = s == UnityEngine.SceneManagement.SceneManager.GetActiveScene(),
                });
                if (s.isDirty)
                    r.AddIssue(CctSeverity.Info, "SCENE_UNSAVED", $"シーン '{s.name}' に未保存の変更があります", value: s.path);
            }
            r.Put("scenes", scenes);

            var packages = new Dictionary<string, object>();
            try
            {
                foreach (var p in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                {
                    if (InterestingPackages.Contains(p.name) || args.GetBool("all_packages", false))
                        packages[p.name] = p.version;
                }
            }
            catch (Exception e)
            {
                r.AddIssue(CctSeverity.Warning, "PACKAGE_LIST_FAILED", "パッケージ一覧を取得できませんでした: " + e.Message);
            }
            r.Put("packages", packages);

            bool avatarsSdk = CctSerialized.FindType(CctVrcTypes.AvatarDescriptor) != null;
            bool worldsSdk = CctSerialized.FindType(CctVrcTypes.SceneDescriptor) != null;
            r.Put("project_kind", avatarsSdk && worldsSdk ? "avatar+world" : avatarsSdk ? "avatar" : worldsSdk ? "world" : "no_vrchat_sdk");

            var avatars = new List<object>();
            var worlds = new List<object>();
            foreach (var go in CctTargets.AllGameObjectsInLoadedScenes())
            {
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    if (CctSerialized.IsType(c, CctVrcTypes.AvatarDescriptor))
                    {
                        var d = CctTargets.Describe(go);
                        d["blueprint_id"] = CctVrcTypes.ReadBlueprintId(go);
                        avatars.Add(d);
                    }
                    else if (CctSerialized.IsType(c, CctVrcTypes.SceneDescriptor))
                    {
                        var d = CctTargets.Describe(go);
                        d["blueprint_id"] = CctVrcTypes.ReadBlueprintId(go);
                        worlds.Add(d);
                    }
                }
            }
            r.Put("avatar_roots", avatars);
            r.Put("world_descriptors", worlds);

            var sel = Selection.activeGameObject;
            r.Put("selection", sel != null ? CctTargets.Describe(sel) : null);

            r.Put("output_dirs", new Dictionary<string, object>
            {
                ["reports"] = CctPaths.ReportsDir,
                ["captures"] = CctPaths.CapturesDir,
                ["snapshots"] = CctPaths.SnapshotsDir,
            });
            r.Message = $"Unity {Application.unityVersion} / {r.Data["project_kind"]} / シーン {scenes.Count} 個 / アバター {avatars.Count} 体 / ワールド記述子 {worlds.Count} 個";
            return r;
        }
    }
}
