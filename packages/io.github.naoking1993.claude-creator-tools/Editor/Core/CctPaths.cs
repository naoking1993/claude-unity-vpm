using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// Where the tools write files. Reports and captures are disposable, so they live under Library/
    /// (never imported, usually git-ignored). Snapshots must survive a Library rebuild, so they live in
    /// &lt;project&gt;/ClaudeCreatorTools/Snapshots, which is outside Assets/ and therefore not imported either.
    /// </summary>
    public static class CctPaths
    {
        public static string ProjectRoot => Normalize(Directory.GetParent(Application.dataPath).FullName);

        public static string ReportsDir => Combine(ProjectRoot, "Library", "ClaudeCreatorTools", "Reports");
        public static string CapturesDir => Combine(ProjectRoot, "Library", "ClaudeCreatorTools", "Captures");
        public static string SnapshotsDir => Combine(ProjectRoot, "ClaudeCreatorTools", "Snapshots");

        public static string Combine(params string[] parts) => Normalize(Path.Combine(parts));

        /// <summary>Forward slashes everywhere: Claude reads these paths back and they must round-trip on Windows.</summary>
        public static string Normalize(string path) => path?.Replace('\\', '/');

        public static string EnsureDir(string dir)
        {
            Directory.CreateDirectory(dir);
            return Normalize(dir);
        }

        /// <summary>Sortable local timestamp id like 20261024-153012-4821 (suffix avoids same-second collisions).</summary>
        public static string NewId(string prefix = null)
        {
            var now = DateTime.Now;
            var suffix = (Environment.TickCount & 0xffff).ToString("x4");
            var id = now.ToString("yyyyMMdd-HHmmss") + "-" + suffix;
            return string.IsNullOrEmpty(prefix) ? id : prefix + "-" + id;
        }

        /// <summary>
        /// &lt;dir&gt;/&lt;stem&gt;&lt;extension&gt;, or &lt;stem&gt;-2, -3, ... when that file exists. NewId's suffix comes from
        /// Environment.TickCount, which on Windows only advances every ~15.6 ms, so two runs of the same tool in the same tick
        /// (e.g. inside one batch_execute) would otherwise get the same name and the second would overwrite the first report.
        /// </summary>
        public static string UniqueFilePath(string dir, string stem, string extension)
        {
            var path = Combine(dir, stem + extension);
            for (int n = 2; File.Exists(path) && n < 1000; n++)
                path = Combine(dir, stem + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + extension);
            return path;
        }

        /// <summary>Makes a file-name-safe token from arbitrary (possibly Japanese) text.</summary>
        public static string SafeName(string text, int maxLength = 40)
        {
            if (string.IsNullOrEmpty(text)) return "unnamed";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (Array.IndexOf(invalid, ch) >= 0 || ch == '/' || ch == '\\' || ch == ' ' || ch == '%') sb.Append('_');
                else sb.Append(ch);
            }
            var s = sb.ToString().Trim('_', '.');
            if (s.Length == 0) s = "unnamed";
            return s.Length > maxLength ? s.Substring(0, maxLength) : s;
        }

        /// <summary>Absolute path -> project-relative (e.g. "Library/ClaudeCreatorTools/...") when inside the project.</summary>
        public static string ToProjectRelative(string absolutePath)
        {
            var p = Normalize(absolutePath);
            var root = ProjectRoot.TrimEnd('/') + "/";
            return p != null && p.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? p.Substring(root.Length) : p;
        }

        /// <summary>Shown to Claude: the absolute path, because Claude reads the file with its own file tools.</summary>
        public static string ToDisplayPath(string absolutePath) => Normalize(absolutePath);

        /// <summary>Asset path ("Assets/..." or "Packages/...") -> absolute file path.</summary>
        public static string AssetToAbsolute(string assetPath) => Combine(ProjectRoot, assetPath);

        public static bool IsAssetPath(string path) =>
            path != null && (path.StartsWith("Assets/", StringComparison.Ordinal) || path == "Assets" ||
                             path.StartsWith("Packages/", StringComparison.Ordinal));
    }

    public static class CctReport
    {
        /// <summary>Writes the full result as UTF-8 JSON (no BOM) and records the path in the result.</summary>
        public static string Save(string toolName, CctResult result, string subject = null)
        {
            var dir = CctPaths.EnsureDir(CctPaths.ReportsDir);
            var stem = toolName + (string.IsNullOrEmpty(subject) ? "" : "-" + CctPaths.SafeName(subject)) + "-" + CctPaths.NewId();
            var path = CctPaths.UniqueFilePath(dir, stem, ".json");
            result.ReportPath = path;
            File.WriteAllText(path, result.ToJson(true), new UTF8Encoding(false));
            return path;
        }
    }
}
