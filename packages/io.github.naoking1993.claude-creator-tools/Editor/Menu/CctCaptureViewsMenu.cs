using UnityEditor;
using UnityEngine;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// Tools menu entries for cct_capture_views. They capture the selected GameObject and log the contact sheet path.
    /// No dialogs (MCP execute_menu_item would block on them).
    /// </summary>
    static class CctCaptureViewsMenu
    {
        const int Priority = 300;

        [MenuItem(CctMenu.Root + "キャプチャ: 選択中を4方向", priority = Priority)]
        static void FourViews() => Capture(new CctArgs()
            .Set("views", CctCaptureViews.DefaultViewList)
            .Set("mode", "isolated"));

        [MenuItem(CctMenu.Root + "キャプチャ: 選択中を顔3方向", priority = Priority + 1)]
        static void FaceViews() => Capture(new CctArgs()
            .Set("views", "face")
            .Set("mode", "isolated"));

        [MenuItem(CctMenu.Root + "キャプチャ: 選択中をシーン照明で4方向", priority = Priority + 2)]
        static void SceneViews() => Capture(new CctArgs()
            .Set("views", CctCaptureViews.DefaultViewList)
            .Set("mode", "scene"));

        [MenuItem(CctMenu.Root + "キャプチャ: 選択中を4方向", true)]
        [MenuItem(CctMenu.Root + "キャプチャ: 選択中を顔3方向", true)]
        [MenuItem(CctMenu.Root + "キャプチャ: 選択中をシーン照明で4方向", true)]
        static bool HasSelection() => Selection.activeGameObject != null && !EditorApplication.isCompiling;

        static void Capture(CctArgs args)
        {
            var r = CctRunner.RunFromMenu(CctCaptureViews.ToolName, CctCaptureViews.Run, args.Set("target", "selection"));
            if (r.Success && r.Data.TryGetValue("capture_dir", out var dir) && dir != null)
                Debug.Log("[Claude Creator Tools] キャプチャ: " + dir);
        }
    }
}
