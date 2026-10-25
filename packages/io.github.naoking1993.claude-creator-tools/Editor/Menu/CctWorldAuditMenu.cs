using UnityEditor;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// World audit menu items (cct_world_audit). Read-only; the summary goes to the Console and the full report to
    /// Library/ClaudeCreatorTools/Reports. Menu runs pass a generous timeout_seconds because no MCP call is waiting, so the
    /// missing-reference and UdonSharp scans can cover big worlds (they still stop at timeout_seconds - 10 s).
    /// </summary>
    public static class CctWorldAuditMenu
    {
        const string Prefix = CctMenu.Root + "ワールド監査 (cct_world_audit)/";
        const int Priority = 400;
        const int MenuTimeoutSeconds = 300;

        static void RunAudit(string platform) =>
            CctRunner.RunFromMenu(CctWorldAudit.ToolName, CctWorldAudit.Run, new CctArgs()
                .Set("scene", "active")
                .Set("platform", platform)
                .Set("timeout_seconds", MenuTimeoutSeconds)
                .Set("report_name", "world-" + platform));

        [MenuItem(Prefix + "アクティブシーン (全プラットフォーム)", priority = Priority)]
        static void All() => RunAudit("all");

        [MenuItem(Prefix + "アクティブシーン (PC)", priority = Priority + 1)]
        static void Pc() => RunAudit("pc");

        [MenuItem(Prefix + "アクティブシーン (Android)", priority = Priority + 2)]
        static void Android() => RunAudit("android");

        [MenuItem(Prefix + "アクティブシーン (iOS)", priority = Priority + 3)]
        static void Ios() => RunAudit("ios");
    }
}
