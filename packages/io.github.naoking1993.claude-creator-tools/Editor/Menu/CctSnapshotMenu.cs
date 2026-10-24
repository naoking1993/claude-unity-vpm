using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// Snapshot menu items (cct_snapshot). Only create and list are offered here: restore/delete need an id and an
    /// explicit confirm, which a menu click cannot express without a dialog.
    /// </summary>
    public static class CctSnapshotMenu
    {
        const string Prefix = CctMenu.Root + "スナップショット: ";

        [MenuItem(Prefix + "開いているシーンを保存", priority = 600)]
        static void SnapshotOpenScenes()
        {
            // Scenes plus the editable assets they use: a scene copy alone cannot bring back shared materials / clips.
            CctRunner.RunFromMenu(CctSnapshot.ToolName, CctSnapshot.Run, new CctArgs()
                .Set("action", "create")
                .Set("label", "メニュー: 開いているシーン (+編集可能な依存アセット)")
                .Set("include_open_scenes", true)
                .Set("include_dependencies", true)
                .Set("dependency_filter", "editable"));
        }

        [MenuItem(Prefix + "選択アセットを保存", priority = 601)]
        static void SnapshotSelection()
        {
            var paths = SelectedAssetPaths();
            CctRunner.RunFromMenu(CctSnapshot.ToolName, CctSnapshot.Run, new CctArgs()
                .Set("action", "create")
                .Set("label", "メニュー: 選択アセット")
                .Set("assets", paths.Cast<object>().ToList())
                .Set("include_open_scenes", false));
        }

        [MenuItem(Prefix + "選択アセットを保存", true)]
        static bool ValidateSnapshotSelection()
        {
            var guids = Selection.assetGUIDs;
            return guids != null && guids.Length > 0;
        }

        [MenuItem(Prefix + "一覧をログ出力", priority = 602)]
        static void LogList()
        {
            var r = CctRunner.RunFromMenu(CctSnapshot.ToolName, CctSnapshot.Run, new CctArgs()
                .Set("action", "list")
                .Set("limit", 100)
                .Set("save_report", false));
            if (!r.Success || !r.Data.TryGetValue("snapshots", out var v) || !(v is List<object> rows) || rows.Count == 0) return;
            var sb = new StringBuilder();
            sb.Append("[Claude Creator Tools] スナップショット一覧 (新しい順) ").Append(CctPaths.SnapshotsDir);
            foreach (var row in rows.OfType<Dictionary<string, object>>())
            {
                sb.Append('\n').Append(Get(row, "id")).Append("  ").Append(Get(row, "created_at"))
                  .Append("  ").Append(Get(row, "file_count")).Append(" files / ").Append(Get(row, "total_mb")).Append(" MB  ")
                  .Append(Get(row, "label"));
                if (row.ContainsKey("incomplete")) sb.Append("  (不完全)");
            }
            Debug.Log(sb.ToString());
        }

        static List<string> SelectedAssetPaths()
        {
            var list = new List<string>();
            var guids = Selection.assetGUIDs;
            if (guids == null) return list;
            foreach (var g in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (!string.IsNullOrEmpty(p) && !list.Contains(p)) list.Add(p);
            }
            return list;
        }

        static string Get(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) && v != null ? System.Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : "";
    }
}
