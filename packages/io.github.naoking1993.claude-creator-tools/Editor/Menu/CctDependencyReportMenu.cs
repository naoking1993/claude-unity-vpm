using System.Linq;
using UnityEditor;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// Dependency report menu items (cct_dependency_report). All read-only; results go to the Console and the report file.
    /// A selected folder in the dependencies item is also treated as the export root.
    /// </summary>
    public static class CctDependencyReportMenu
    {
        const string Prefix = CctMenu.Root + "依存関係: ";

        [MenuItem(Prefix + "選択アセット", priority = 500)]
        static void Dependencies()
        {
            CctRunner.RunFromMenu(CctDependencyReport.ToolName, CctDependencyReport.Run, new CctArgs()
                .Set("mode", CctDependencyReport.ModeDependencies)
                .Set("assets", CctDependencyReport.SelectedAssetPaths().Cast<object>().ToList())
                .Set("report_name", "deps"));
        }

        [MenuItem(Prefix + "選択アセット", true)]
        static bool ValidateDependencies() => HasAssetSelection();

        [MenuItem(Prefix + "選択アセットの使用元を検索", priority = 501)]
        static void Reverse()
        {
            CctRunner.RunFromMenu(CctDependencyReport.ToolName, CctDependencyReport.Run, new CctArgs()
                .Set("mode", CctDependencyReport.ModeReverse)
                .Set("assets", CctDependencyReport.SelectedAssetPaths().Cast<object>().ToList())
                .Set("report_name", "users"));
        }

        [MenuItem(Prefix + "選択アセットの使用元を検索", true)]
        static bool ValidateReverse() => HasAssetSelection();

        [MenuItem(Prefix + "選択オブジェクトのシーン外参照", priority = 502)]
        static void SceneReferences()
        {
            CctRunner.RunFromMenu(CctDependencyReport.ToolName, CctDependencyReport.Run, new CctArgs()
                .Set("mode", CctDependencyReport.ModeSceneReferences)
                .Set("target", "selection")
                .Set("report_name", "scene-refs"));
        }

        [MenuItem(Prefix + "選択オブジェクトのシーン外参照", true)]
        static bool ValidateSceneReferences() => Selection.activeGameObject != null;

        static bool HasAssetSelection()
        {
            var guids = Selection.assetGUIDs;
            return guids != null && guids.Length > 0;
        }
    }
}
