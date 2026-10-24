using UnityEditor;
using UnityEngine;

namespace Naoking.AvatarChecker
{
    public class AvatarCheckerWindow : EditorWindow
    {
        GameObject target;
        Platform platform = Platform.PC;
        CheckReport report;
        Vector2 scroll;
        GUIStyle wrap;

        static readonly string[] PlatformLabels = { "PC", "Quest / Android" };

        public static void Open()
        {
            var w = GetWindow<AvatarCheckerWindow>("Avatar Checker");
            w.minSize = new Vector2(380, 320);
            if (w.target == null) w.target = AvatarScanner.ResolveAvatarRoot(Selection.activeGameObject);
        }

        void OnGUI()
        {
            if (wrap == null) wrap = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = false };

            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            target = (GameObject)EditorGUILayout.ObjectField("アバター", target, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck()) report = null;  // the report always describes the object shown above
            platform = (Platform)GUILayout.Toolbar((int)platform, PlatformLabels);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("選択中を使う")) { target = AvatarScanner.ResolveAvatarRoot(Selection.activeGameObject); report = null; }
                using (new EditorGUI.DisabledScope(target == null))
                    if (GUILayout.Button("チェック", GUILayout.Height(24))) report = AvatarScanner.Scan(target);
            }
            EditorGUILayout.HelpBox("このツールはアバターを書き換えません。ランクは目安で、最終的な判定は VRChat SDK のビルド画面を見てください。", MessageType.None);

            if (report == null) return;
            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawSummary();
            DrawStats();
            DrawIssues();
            EditorGUILayout.EndScrollView();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("JSON をコピー")) EditorGUIUtility.systemCopyBuffer = report.ToJson();
                if (GUILayout.Button("JSON を保存"))
                    Debug.Log(AvatarCheckerMenu.LogTag + " " + AvatarCheckerMenu.Write("last-report.json", report.ToJson()));
            }
        }

        void DrawSummary()
        {
            var rank = report.OverallRank(platform);
            EditorGUILayout.Space();
            var old = GUI.color;
            GUI.color = RankColor(rank);
            EditorGUILayout.LabelField(report.AvatarName + "  —  " + PerformanceRank.Label(rank), EditorStyles.boldLabel);
            GUI.color = old;
            EditorGUILayout.LabelField("エラー " + report.Count(Severity.Error) + " / 警告 " + report.Count(Severity.Warning) + " / 情報 " + report.Count(Severity.Info));
            if (TextureTargetMismatch())
                EditorGUILayout.HelpBox("テクスチャメモリは今のビルドターゲット (" + report.BuildTarget + ") の設定で計算しています。" + PlatformLabels[(int)platform] + " の値を見るには、ビルドターゲットを切り替えてからチェックしてください。", MessageType.Info);
            if (report.ExpressionParameterCost >= 0)
                EditorGUILayout.LabelField("Expression パラメータ", report.ExpressionParameterCost + " / " + CheckReport.ExpressionParameterBudget + " bit");
        }

        void DrawStats()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("項目", EditorStyles.boldLabel);
            foreach (var kv in report.Stats)
            {
                if (!PerformanceRank.IsRanked(platform, kv.Key)) continue;
                var rank = PerformanceRank.Rate(platform, kv.Key, kv.Value);
                if (kv.Value == 0 && rank == Rank.Excellent) continue;  // hide unused metrics
                var good = PerformanceRank.Limit(platform, kv.Key, Rank.Good);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(kv.Key, GUILayout.MinWidth(160));
                    EditorGUILayout.LabelField(Format(kv.Value) + (good.HasValue ? "  (Good ≤ " + Format(good.Value) + ")" : ""), GUILayout.MinWidth(120));
                    var old = GUI.color;
                    GUI.color = RankColor(rank);
                    EditorGUILayout.LabelField(PerformanceRank.Label(rank), GUILayout.Width(70));
                    GUI.color = old;
                }
            }
        }

        void DrawIssues()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("問題点", EditorStyles.boldLabel);
            if (report.Issues.Count == 0) EditorGUILayout.LabelField("見つかりませんでした。");
            foreach (var i in report.Issues)
            {
                var type = i.Severity == Severity.Error ? MessageType.Error : i.Severity == Severity.Warning ? MessageType.Warning : MessageType.Info;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox((i.Path.Length > 0 ? i.Path + "\n" : "") + i.Message, type);
                    var obj = i.ObjectId != 0 ? EditorUtility.InstanceIDToObject(i.ObjectId) : null;
                    using (new EditorGUI.DisabledScope(obj == null))
                        if (GUILayout.Button("選択", GUILayout.Width(44), GUILayout.Height(38)))
                        {
                            Selection.activeObject = obj;
                            EditorGUIUtility.PingObject(obj);
                        }
                }
            }
        }

        bool TextureTargetMismatch()
        {
            if (report.Stats.ContainsKey(PerformanceRank.TextureMemoryMB) && report.Stats[PerformanceRank.TextureMemoryMB] == 0) return false;
            bool android = report.BuildTarget == "Android";
            return platform == Platform.Mobile ? !android : android;
        }

        static string Format(double v) { return v == System.Math.Floor(v) ? v.ToString("0") : v.ToString("0.0"); }

        static Color RankColor(Rank r)
        {
            switch (r)
            {
                case Rank.Excellent: return new Color(0.45f, 0.85f, 1f);
                case Rank.Good: return new Color(0.5f, 0.95f, 0.5f);
                case Rank.Medium: return new Color(1f, 0.9f, 0.4f);
                case Rank.Poor: return new Color(1f, 0.6f, 0.3f);
                default: return new Color(1f, 0.4f, 0.4f);
            }
        }
    }
}
