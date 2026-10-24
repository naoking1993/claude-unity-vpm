// Pure C# (no UnityEngine/UnityEditor) so CI can compile and test it with mono.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Naoking.AvatarChecker
{
    public enum Severity { Info, Warning, Error }

    public class Issue
    {
        public Severity Severity;
        public string Code;     // stable id, e.g. "missing-script", for tools that read the JSON
        public string Message;  // Japanese, for people
        public string Path;     // hierarchy path below the avatar root, or "" for the avatar itself

        public Issue(Severity severity, string code, string message, string path)
        {
            Severity = severity; Code = code; Message = message; Path = path ?? "";
        }
    }

    public class CheckReport
    {
        public string AvatarName = "";
        public bool HasAvatarDescriptor;
        public bool VrcSdkFound;
        public int ExpressionParameterCost = -1;  // -1 = unknown (no SDK or no parameters asset)
        public const int ExpressionParameterBudget = 256;
        public readonly SortedDictionary<string, double> Stats = new SortedDictionary<string, double>(StringComparer.Ordinal);
        public readonly List<Issue> Issues = new List<Issue>();

        public void Add(Severity s, string code, string message, string path = "")
        {
            Issues.Add(new Issue(s, code, message, path));
        }

        public int Count(Severity s)
        {
            int n = 0;
            foreach (var i in Issues) if (i.Severity == s) n++;
            return n;
        }

        public Rank OverallRank(Platform p) { return PerformanceRank.Overall(p, Stats); }

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"tool\": \"io.github.naoking1993.avatar-checker\",\n");
            sb.Append("  \"avatar\": ").Append(Str(AvatarName)).Append(",\n");
            sb.Append("  \"hasAvatarDescriptor\": ").Append(HasAvatarDescriptor ? "true" : "false").Append(",\n");
            sb.Append("  \"vrcSdkFound\": ").Append(VrcSdkFound ? "true" : "false").Append(",\n");
            sb.Append("  \"expressionParameters\": { \"cost\": ")
              .Append(ExpressionParameterCost < 0 ? "null" : ExpressionParameterCost.ToString(CultureInfo.InvariantCulture))
              .Append(", \"budget\": ").Append(ExpressionParameterBudget.ToString(CultureInfo.InvariantCulture)).Append(" },\n");
            sb.Append("  \"rank\": { \"pc\": ").Append(Str(PerformanceRank.Label(OverallRank(Platform.PC))))
              .Append(", \"mobile\": ").Append(Str(PerformanceRank.Label(OverallRank(Platform.Mobile)))).Append(" },\n");
            sb.Append("  \"stats\": {");
            bool first = true;
            foreach (var kv in Stats)
            {
                sb.Append(first ? "\n" : ",\n"); first = false;
                sb.Append("    ").Append(Str(kv.Key)).Append(": { \"value\": ").Append(Num(kv.Value))
                  .Append(", \"pc\": ").Append(RankOrNull(Platform.PC, kv.Key, kv.Value))
                  .Append(", \"mobile\": ").Append(RankOrNull(Platform.Mobile, kv.Key, kv.Value)).Append(" }");
            }
            sb.Append(first ? "},\n" : "\n  },\n");
            sb.Append("  \"issues\": [");
            first = true;
            foreach (var i in Issues)
            {
                sb.Append(first ? "\n" : ",\n"); first = false;
                sb.Append("    { \"severity\": ").Append(Str(i.Severity.ToString().ToLowerInvariant()))
                  .Append(", \"code\": ").Append(Str(i.Code))
                  .Append(", \"path\": ").Append(Str(i.Path))
                  .Append(", \"message\": ").Append(Str(i.Message)).Append(" }");
            }
            sb.Append(first ? "]\n" : "\n  ]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        static string RankOrNull(Platform p, string metric, double v)
        {
            return PerformanceRank.IsRanked(p, metric) ? Str(PerformanceRank.Label(PerformanceRank.Rate(p, metric, v))) : "null";
        }

        static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
        }

        public static string Str(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
