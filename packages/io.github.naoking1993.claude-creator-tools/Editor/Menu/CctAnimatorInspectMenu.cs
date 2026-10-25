using UnityEditor;
using UnityEngine;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// Animator inspection menu items (cct_animator_inspect). They log a one-line summary and write the full report
    /// (graph, bindings, issues) to Library/ClaudeCreatorTools/Reports. No dialogs: MCP's execute_menu_item must never block.
    /// </summary>
    public static class CctAnimatorInspectMenu
    {
        const string Prefix = CctMenu.Root + "アニメーター解析 (cct_animator_inspect)/";
        const int Priority = 200;

        [MenuItem(Prefix + "選択中のアバターの FX", priority = Priority)]
        static void InspectFx() => RunForSelection("fx");

        [MenuItem(Prefix + "選択中のアバターの Gesture", priority = Priority + 1)]
        static void InspectGesture() => RunForSelection("gesture");

        [MenuItem(Prefix + "選択中のアバターの全レイヤー (+ MA Merge Animator)", priority = Priority + 2)]
        static void InspectAll() => RunForSelection("all");

        [MenuItem(Prefix + "Project で選択中のコントローラ", priority = Priority + 20)]
        static void InspectSelectedController()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            CctRunner.RunFromMenu(CctAnimatorInspect.ToolName, CctAnimatorInspect.Run, new CctArgs()
                .Set("controller", path));
        }

        [MenuItem(Prefix + "Project で選択中のコントローラ", true)]
        static bool InspectSelectedControllerValidate() =>
            Selection.activeObject is RuntimeAnimatorController && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(Selection.activeObject));

        static void RunForSelection(string layer)
        {
            CctRunner.RunFromMenu(CctAnimatorInspect.ToolName, CctAnimatorInspect.Run, new CctArgs()
                .Set("target", "selection")
                .Set("layer", layer));
        }
    }
}
