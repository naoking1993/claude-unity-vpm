using UnityEditor;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// Avatar audit menu items (cct_avatar_audit). They audit the selected object (moved up to its avatar root), log a one-line
    /// summary and write the full report. No dialogs: MCP's execute_menu_item must never block.
    /// </summary>
    public static class CctAvatarAuditMenu
    {
        const string Prefix = CctMenu.Root + "アバター監査 (cct_avatar_audit)/";

        [MenuItem(Prefix + "選択中のアバターを監査 (PC + モバイル)", priority = 100)]
        static void AuditSelected()
        {
            CctRunner.RunFromMenu(CctAvatarAudit.ToolName, CctAvatarAudit.Run, new CctArgs()
                .Set("target", "selection")
                .Set("platform", "all"));
        }

        [MenuItem(Prefix + "選択中のアバターを監査 + NDMF 処理後の値", priority = 101)]
        static void AuditSelectedPostBuild()
        {
            CctRunner.RunFromMenu(CctAvatarAudit.ToolName, CctAvatarAudit.Run, new CctArgs()
                .Set("target", "selection")
                .Set("platform", "all")
                .Set("post_build", true));
        }

        [MenuItem(Prefix + "選択中のアバターを監査 + NDMF 処理後の値", true)]
        static bool AuditSelectedPostBuildValidate() => !EditorApplication.isPlayingOrWillChangePlaymode;
    }
}
