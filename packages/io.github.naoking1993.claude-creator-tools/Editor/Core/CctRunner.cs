using System;
using System.Diagnostics;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// Single entry point used by both the MCP bridge and the Tools menu, so a tool behaves the same
    /// whichever way it is called: exceptions become failed results, timing is recorded, and the full
    /// report is written to Library/ClaudeCreatorTools/Reports unless save_report is false.
    /// </summary>
    public static class CctRunner
    {
        public const string PackageVersion = "0.1.2";

        public static CctResult Run(string toolName, Func<CctArgs, CctResult> tool, CctArgs args)
        {
            args = args ?? new CctArgs();
            var sw = Stopwatch.StartNew();
            CctResult result;
            try
            {
                if (EditorApplication.isCompiling)
                    result = CctResult.Fail("Unity がスクリプトをコンパイル中です。完了してから再実行してください", "EDITOR_BUSY");
                else
                    result = tool(args) ?? CctResult.Fail("ツールが結果を返しませんでした", "NO_RESULT");
            }
            catch (Exception e)
            {
                result = CctResult.Fail($"{e.GetType().Name}: {e.Message}", "EXCEPTION",
                    new System.Collections.Generic.Dictionary<string, object> { ["stack"] = Trim(e.StackTrace, 2000) });
            }
            sw.Stop();
            result.Put("tool", toolName);
            result.Put("tool_version", PackageVersion);
            result.Put("elapsed_ms", sw.ElapsedMilliseconds);

            if (args.GetBool("save_report", true))
            {
                try { CctReport.Save(toolName, result, args.GetString("report_name")); }
                catch (Exception e) { result.AddIssue(CctSeverity.Warning, "REPORT_WRITE_FAILED", "レポートを保存できませんでした: " + e.Message); }
            }
            return result;
        }

        /// <summary>For menu items: run, then log a one-line summary plus the report path to the Console.</summary>
        public static CctResult RunFromMenu(string toolName, Func<CctArgs, CctResult> tool, CctArgs args)
        {
            var r = Run(toolName, tool, args);
            var counts = $"error {r.CountIssues(CctSeverity.Error)} / warning {r.CountIssues(CctSeverity.Warning)} / info {r.CountIssues(CctSeverity.Info)}";
            var line = $"[Claude Creator Tools] {toolName}: {(r.Success ? "OK" : "FAILED")} - {r.Message} ({counts})" +
                       (r.ReportPath != null ? $"\nreport: {r.ReportPath}" : "");
            if (r.Success) Debug.Log(line); else Debug.LogWarning(line);
            return r;
        }

        static string Trim(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
