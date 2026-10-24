using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Naoking.AvatarChecker
{
    /// <summary>
    /// Menu items and a static API. Menu paths are ASCII so MCP clients can call them by name
    /// (e.g. MCP for Unity's execute_menu_item). Results go to the Console and to Library/AvatarChecker/.
    /// </summary>
    public static class AvatarCheckerMenu
    {
        public const string Root = "Tools/Avatar Checker/";
        public const string LogTag = "[AvatarChecker]";
        public static readonly string OutputDir = Path.Combine("Library", "AvatarChecker");

        [MenuItem(Root + "Open Window", false, 0)]
        public static void OpenWindow() { AvatarCheckerWindow.Open(); }

        [MenuItem(Root + "Check Selected (JSON)", false, 20)]
        public static void CheckSelected()
        {
            var root = AvatarScanner.ResolveAvatarRoot(Selection.activeGameObject);
            if (root == null) { Debug.LogWarning(LogTag + " Hierarchy でアバターを選んでから実行してください。"); return; }
            var json = CheckToJson(root);
            var file = Write("last-report.json", json);
            Debug.Log(LogTag + " " + root.name + " → " + file + "\n" + json);
        }

        [MenuItem(Root + "Check Selected (JSON)", true)]
        static bool CheckSelectedValidate() { return Selection.activeGameObject != null; }

        [MenuItem(Root + "Check All Avatars In Scene (JSON)", false, 21)]
        public static void CheckScene()
        {
            var json = CheckSceneToJson();
            var file = Write("scene-report.json", json);
            Debug.Log(LogTag + " scene → " + file + "\n" + json);
        }

        /// <summary>Check one avatar and return the report as JSON. Read-only.</summary>
        public static string CheckToJson(GameObject avatarRoot) { return AvatarScanner.Scan(avatarRoot).ToJson(); }

        /// <summary>Check every avatar (VRC Avatar Descriptor) in the loaded scenes; returns a JSON array.</summary>
        public static string CheckSceneToJson()
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var go in AvatarScanner.AvatarsInScene())
            {
                sb.Append(first ? "\n" : ",\n");
                first = false;
                sb.Append(AvatarScanner.Scan(go).ToJson().TrimEnd('\n'));
            }
            return sb.Append(first ? "]\n" : "\n]\n").ToString();
        }

        public static string Write(string name, string json)
        {
            Directory.CreateDirectory(OutputDir);
            var file = Path.Combine(OutputDir, name);
            File.WriteAllText(file, json, new UTF8Encoding(false));
            return file.Replace('\\', '/');
        }
    }
}
