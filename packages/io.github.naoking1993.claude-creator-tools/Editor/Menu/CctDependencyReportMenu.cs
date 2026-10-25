using System.Linq;
using UnityEditor;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// Dependency report menu items (cct_dependency_report). All read-only; results go to the Console and the report file.
    /// A selected folder in the dependencies item is also treated as the export root. Menu runs pass a long but finite
    /// timeout_seconds (CctDependencyReport.MenuTimeoutSeconds, so the scans stop after MenuTimeBudgetSeconds): no MCP call is
    /// waiting, but the progress bar cannot be cancelled and execute_menu_item may also start these items, so the editor must
    /// never freeze indefinitely on a huge project. (The tool caps its time budget at timeout_seconds - 5, so passing only
    /// time_budget_seconds would leave menu runs at 25 s.)
    /// </summary>
    public static class CctDependencyReportMenu
    {
        const string Prefix = CctMenu.Root + "依存関係 (cct_dependency_report)/";

        static CctArgs MenuArgs(string mode, string reportName) => new CctArgs()
            .Set("mode", mode)
            .Set("timeout_seconds", CctDependencyReport.MenuTimeoutSeconds)
            .Set("report_name", reportName);

        [MenuItem(Prefix + "選択アセットの依存関係", priority = 500)]
        static void Dependencies()
        {
            CctRunner.RunFromMenu(CctDependencyReport.ToolName, CctDependencyReport.Run, MenuArgs(CctDependencyReport.ModeDependencies, "deps")
                .Set("assets", CctDependencyReport.SelectedAssetPaths().Cast<object>().ToList()));
        }

        [MenuItem(Prefix + "選択アセットの依存関係", true)]
        static bool ValidateDependencies() => HasAssetSelection();

        [MenuItem(Prefix + "選択アセットの使用元を検索", priority = 501)]
        static void Reverse()
        {
            CctRunner.RunFromMenu(CctDependencyReport.ToolName, CctDependencyReport.Run, MenuArgs(CctDependencyReport.ModeReverse, "users")
                .Set("assets", CctDependencyReport.SelectedAssetPaths().Cast<object>().ToList()));
        }

        [MenuItem(Prefix + "選択アセットの使用元を検索", true)]
        static bool ValidateReverse() => HasAssetSelection();

        [MenuItem(Prefix + "選択オブジェクトのシーン外参照", priority = 502)]
        static void SceneReferences()
        {
            CctRunner.RunFromMenu(CctDependencyReport.ToolName, CctDependencyReport.Run, MenuArgs(CctDependencyReport.ModeSceneReferences, "scene-refs")
                .Set("target", "selection"));
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
