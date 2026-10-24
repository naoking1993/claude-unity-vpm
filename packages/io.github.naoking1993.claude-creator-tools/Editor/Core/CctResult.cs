using System;
using System.Collections.Generic;
using System.Linq;

namespace Naoking.ClaudeCreatorTools
{
    public static class CctSeverity
    {
        public const string Error = "error";     // breaks upload/behaviour, or certainly wrong
        public const string Warning = "warning"; // likely problem or performance cost worth fixing
        public const string Info = "info";       // context for the reader; no action required

        public static int Rank(string severity)
        {
            switch (severity)
            {
                case Error: return 0;
                case Warning: return 1;
                default: return 2;
            }
        }
    }

    /// <summary>
    /// Result of one tool run. <see cref="Data"/> holds the full structured payload; <see cref="Issues"/>
    /// holds findings with stable codes so skills can refer to them. Values must be JSON-friendly
    /// (see <see cref="CctJson"/>), because both the MCP bridge (Newtonsoft) and the report writer
    /// (CctJson) serialize them.
    /// </summary>
    public sealed class CctResult
    {
        public bool Success { get; private set; }
        public string Message { get; set; }
        public string Code { get; private set; }
        public Dictionary<string, object> Data { get; } = new Dictionary<string, object>();
        public List<Dictionary<string, object>> Issues { get; } = new List<Dictionary<string, object>>();

        /// <summary>Absolute path of the saved JSON report, if one was written.</summary>
        public string ReportPath { get; set; }

        CctResult() { }

        public static CctResult Ok(string message) => new CctResult { Success = true, Message = message };

        public static CctResult Fail(string message, string code = "ERROR", object details = null)
        {
            var r = new CctResult { Success = false, Message = message, Code = code };
            if (details != null) r.Data["details"] = details;
            return r;
        }

        public CctResult Put(string key, object value)
        {
            Data[key] = value;
            return this;
        }

        public CctResult AddIssue(string severity, string code, string message,
            string path = null, object value = null, object limit = null, string asset = null)
        {
            var issue = new Dictionary<string, object>
            {
                ["severity"] = severity,
                ["code"] = code,
                ["message"] = message,
            };
            if (path != null) issue["path"] = path;
            if (asset != null) issue["asset"] = asset;
            if (value != null) issue["value"] = value;
            if (limit != null) issue["limit"] = limit;
            Issues.Add(issue);
            return this;
        }

        public int CountIssues(string severity) =>
            Issues.Count(i => (i.TryGetValue("severity", out var s) ? s as string : null) == severity);

        IEnumerable<Dictionary<string, object>> SortedIssues() =>
            Issues.OrderBy(i => CctSeverity.Rank(i.TryGetValue("severity", out var s) ? s as string : null));

        /// <summary>Full payload: what the JSON report file contains.</summary>
        public Dictionary<string, object> ToPlain()
        {
            var d = new Dictionary<string, object>
            {
                ["success"] = Success,
                ["message"] = Message,
            };
            if (!Success && Code != null) d["code"] = Code;
            d["issue_counts"] = IssueCounts();
            d["issues"] = SortedIssues().ToList();
            foreach (var kv in Data) d[kv.Key] = kv.Value;
            if (ReportPath != null) d["report_path"] = CctPaths.ToDisplayPath(ReportPath);
            return d;
        }

        /// <summary>
        /// Compact payload for an MCP response: issues sorted errors-first and truncated, and only the
        /// data keys listed in <paramref name="keepKeys"/> (null = keep all). The full report stays on disk.
        /// </summary>
        public Dictionary<string, object> ToCompact(int maxIssues, ICollection<string> keepKeys)
        {
            var d = new Dictionary<string, object>
            {
                ["success"] = Success,
                ["message"] = Message,
            };
            if (!Success && Code != null) d["code"] = Code;
            d["issue_counts"] = IssueCounts();
            var sorted = SortedIssues().ToList();
            d["issues"] = sorted.Take(Math.Max(0, maxIssues)).ToList();
            if (sorted.Count > maxIssues)
                d["issues_omitted"] = sorted.Count - maxIssues;
            foreach (var kv in Data)
                if (keepKeys == null || keepKeys.Contains(kv.Key)) d[kv.Key] = kv.Value;
            if (ReportPath != null) d["report_path"] = CctPaths.ToDisplayPath(ReportPath);
            return d;
        }

        Dictionary<string, object> IssueCounts() => new Dictionary<string, object>
        {
            [CctSeverity.Error] = CountIssues(CctSeverity.Error),
            [CctSeverity.Warning] = CountIssues(CctSeverity.Warning),
            [CctSeverity.Info] = CountIssues(CctSeverity.Info),
        };

        public string ToJson(bool pretty = true) => CctJson.Serialize(ToPlain(), pretty);
    }
}
