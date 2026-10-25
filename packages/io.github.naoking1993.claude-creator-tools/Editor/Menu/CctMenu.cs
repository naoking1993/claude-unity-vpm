using UnityEditor;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// Tools menu entries. Menu items never open modal dialogs: MCP's execute_menu_item would block on them.
    /// Each item runs the same code path as the MCP tool and logs the report path to the Console.
    /// Per-tool items live in Menu/&lt;Tool&gt;Menu.cs and use <see cref="Root"/>.
    /// </summary>
    public static class CctMenu
    {
        public const string Root = "Tools/Claude Creator Tools/";

        [MenuItem(Root + "環境情報を出力 (cct_environment)", priority = 0)]
        static void Environment() => CctRunner.RunFromMenu(CctEnvironment.ToolName, CctEnvironment.Run, new CctArgs());

        [MenuItem(Root + "レポートフォルダを開く", priority = 900)]
        static void OpenReports() => EditorUtility.RevealInFinder(CctPaths.EnsureDir(CctPaths.ReportsDir));

        [MenuItem(Root + "キャプチャフォルダを開く", priority = 901)]
        static void OpenCaptures() => EditorUtility.RevealInFinder(CctPaths.EnsureDir(CctPaths.CapturesDir));

        [MenuItem(Root + "スナップショットフォルダを開く", priority = 902)]
        static void OpenSnapshots() => EditorUtility.RevealInFinder(CctPaths.EnsureDir(CctPaths.SnapshotsDir));
    }
}
