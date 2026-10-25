using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

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
            var r = CctRunner.RunFromMenu(CctWorldGimmicks.ToolName, CctWorldGimmicks.Run, new CctArgs()
                .Set("action", "list"));
            // The one-line summary only has counts; the menu item promises the list, so print one row per sample.
            if (!r.Success || !r.Data.TryGetValue("samples", out var v) || !(v is List<object> rows) || rows.Count == 0) return;
            var sb = new StringBuilder();
            sb.Append("[Claude Creator Tools] ワールドギミック一覧 (取り込み先 ").Append(Get(r.Data, "dest")).Append(')');
            foreach (var row in rows.OfType<Dictionary<string, object>>())
            {
                var elsewhere = Get(row, "found_elsewhere");
                var state = Get(row, "imported") == "True" ? "取り込み済み"
                    : elsewhere.Length > 0 ? "別の場所にあり: " + elsewhere
                    : "未導入";
                sb.Append('\n').Append(Get(row, "name")).Append("  [").Append(state).Append("]  ")
                  .Append(Get(row, "sync_mode")).Append("  ").Append(Get(row, "summary"));
                if (row.TryGetValue("missing_requirements", out var m) && m is List<object> missing && missing.Count > 0)
                    sb.Append("  (不足: ").Append(string.Join(", ", missing)).Append(')');
            }
            Debug.Log(sb.ToString());
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

        static string Get(IReadOnlyDictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) && v != null ? System.Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : "";
    }
}
