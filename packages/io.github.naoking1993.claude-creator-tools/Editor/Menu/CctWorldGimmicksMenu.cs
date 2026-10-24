using UnityEditor;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// World gimmick sample menu items (cct_world_gimmicks). Clicking "import" is the explicit user action, so it passes
    /// confirm=true; the tool itself still refuses without UdonSharp and never overwrites existing files from the menu.
    /// </summary>
    public static class CctWorldGimmicksMenu
    {
        const string Prefix = CctMenu.Root + "ワールドギミック (UdonSharp)/";

        [MenuItem(Prefix + "一覧と導入状況をログ出力", priority = 700)]
        static void List()
        {
            CctRunner.RunFromMenu(CctWorldGimmicks.ToolName, CctWorldGimmicks.Run, new CctArgs()
                .Set("action", "list"));
        }

        [MenuItem(Prefix + "Assets/ClaudeWorldGimmicks にインポート", priority = 701)]
        static void Import()
        {
            CctRunner.RunFromMenu(CctWorldGimmicks.ToolName, CctWorldGimmicks.Run, new CctArgs()
                .Set("action", "import")
                .Set("dest", CctWorldGimmicks.DefaultDestination)
                .Set("confirm", true)
                .Set("overwrite", false)
                .Set("create_program_assets", true));
        }
    }
}
