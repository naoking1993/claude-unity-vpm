using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    // Upload-blocking / quality checks that are not performance ranks. Each rule mirrors the SDK 3.10.5 avatar builder
    // (com.vrchat.avatars Editor/VRCSDK/SDK3A/VRCSdkControlPanelAvatarBuilder.cs, summarised in research/avatar-limits.md §6)
    // unless the issue text says it is a docs rule or a heuristic. Everything here is read-only.
    public static partial class CctAvatarAudit
    {
        const int MaxMissingRefVisits = 400000;
        const int MaxYamlBytes = 64 * 1024 * 1024;
        const int MaxYamlFiles = 12;
        const int ExamplesInMessage = 5;

        static void RunChecks(Ctx ctx, Est est, StatsOutput output)
        {
            output.MissingScripts = Section(ctx, "missing_scripts", () => CheckMissingScripts(ctx));
            ctx.R.Put("missing_references", Section(ctx, "missing_references", () => CheckMissingReferences(ctx)));
            Guard(ctx, "whitelist", () => CheckWhitelist(ctx));
            Guard(ctx, "unity_constraints", () => CheckUnityConstraints(ctx));
            Guard(ctx, "particles_audio", () => CheckParticlesTrailsAudio(ctx, est));
            Guard(ctx, "mesh_importers", () => CheckMeshImporters(ctx));
            Guard(ctx, "textures", () => CheckTextures(ctx, est));
            Guard(ctx, "shaders", () => CheckShaders(ctx, est));
            if (ctx.Descriptor != null)
            {
                Guard(ctx, "pipeline_manager", () => CheckPipeline(ctx, est));
                Guard(ctx, "humanoid", () => CheckAnimatorAndHumanoid(ctx));
                Guard(ctx, "descriptor", () => CheckDescriptorSettings(ctx));
                Guard(ctx, "layers", () => CheckLayers(ctx));
            }
            Guard(ctx, "ndmf_config", () => CheckNdmfConfig(ctx));
        }

        static string Examples(IEnumerable<string> items, int total)
        {
            var list = items.Take(ExamplesInMessage).ToList();
            return string.Join(", ", list) + (total > list.Count ? $" ほか {total - list.Count} 件" : "");
        }

        // =====================================================================================================================
        // Missing scripts (never filter null components first) + m_Script GUIDs from the saved YAML
        // =====================================================================================================================

        static Dictionary<string, object> CheckMissingScripts(Ctx ctx)
        {
            var scan = ctx.Scan;
            var d = new Dictionary<string, object>
            {
                ["total"] = scan.MissingScriptTotal,
                ["objects"] = scan.MissingScriptObjects.Count,
                ["method"] = "GameObjectUtility.GetMonoBehavioursWithMissingScriptCount (子オブジェクトも含め、null を除外する前に数えています)",
            };
            if (scan.MissingScriptTotal == 0) return d;

            var guids = MissingScriptGuids(ctx, out var guidNote);
            d["items"] = scan.MissingScriptObjects.Take(50).Select(kv => (object)new Dictionary<string, object>
            {
                ["path"] = ctx.PathOf(kv.Key.transform),
                ["count"] = kv.Value,
            }).ToList();
            if (scan.MissingScriptObjects.Count > 50) d["items_omitted"] = scan.MissingScriptObjects.Count - 50;
            d["guids"] = guids.Select(g => (object)g.ToPlain()).ToList();
            if (guidNote != null) d["guid_note"] = guidNote;

            foreach (var kv in scan.MissingScriptObjects)
            {
                var go = kv.Key;
                var hint = guids.Where(g => g.ObjectNames.Contains(go.name)).Select(g => $"m_Script GUID {g.Guid} ({g.StatusText})").Distinct().Take(3).ToList();
                ctx.Issue(CctSeverity.Error, "MISSING_SCRIPT",
                    $"Missing Script が {kv.Value} 個あります{(hint.Count > 0 ? ": " + string.Join(", ", hint) : "")}。必要なパッケージ/スクリプトを導入するか、不要なら削除してください (NDMF はビルド時に黙って削除するため、ビルド後の 0 個は依存がそろった証明になりません)",
                    ctx.PathOf(go.transform), kv.Value);
            }
            foreach (var g in guids)
                ctx.Issue(CctSeverity.Info, "MISSING_SCRIPT_GUID",
                    $"保存済みファイル {string.Join(", ", g.Files.Take(3))} の MonoBehaviour が GUID {g.Guid} のスクリプトを参照しています: {g.StatusText}" +
                    (g.ObjectNames.Count > 0 ? $" (GameObject: {string.Join(", ", g.ObjectNames.Take(5))})" : ""),
                    value: g.Guid, asset: g.ScriptPath ?? g.Files.FirstOrDefault());
            return d;
        }

        sealed class GuidFinding
        {
            public string Guid, Status, StatusText, ScriptPath;
            public long FileId;
            public int Count;
            public readonly List<string> Files = new List<string>();
            public readonly HashSet<string> ObjectNames = new HashSet<string>(StringComparer.Ordinal);

            public Dictionary<string, object> ToPlain()
            {
                var d = new Dictionary<string, object>
                {
                    ["guid"] = Guid,
                    ["file_id"] = FileId,
                    ["status"] = Status,
                    ["components"] = Count,
                    ["files"] = Files.Cast<object>().ToList(),
                    ["game_objects"] = ObjectNames.Take(20).Cast<object>().ToList(),
                };
                if (ScriptPath != null) d["script_asset"] = ScriptPath;
                return d;
            }
        }

        /// <summary>
        /// Reads m_Script references of MonoBehaviours from the saved YAML of the files that define the GameObjects with missing
        /// scripts (prefab chain of each object + the scene / prefab asset), and keeps those whose script cannot be resolved.
        /// The YAML reflects the last saved state; unsaved changes are not seen.
        /// </summary>
        static List<GuidFinding> MissingScriptGuids(Ctx ctx, out string note)
        {
            note = null;
            var names = new HashSet<string>(ctx.Scan.MissingScriptObjects.Select(kv => kv.Key.name), StringComparer.Ordinal);
            var files = new List<string>();
            void AddFile(string p)
            {
                if (!string.IsNullOrEmpty(p) && (p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) && !files.Contains(p))
                    files.Add(p);
            }
            if (ctx.PrefabAssetPath != null) AddFile(ctx.PrefabAssetPath);
            bool sceneDirty = false;
            foreach (var kv in ctx.Scan.MissingScriptObjects)
            {
                var go = kv.Key;
                try
                {
                    Object cur = go;
                    for (int guard = 0; cur != null && guard < 16; guard++)
                    {
                        var src = PrefabUtility.GetCorrespondingObjectFromSource(cur);
                        if (src == null || src == cur) break;
                        AddFile(AssetDatabase.GetAssetPath(src));
                        cur = src;
                    }
                }
                catch (Exception) { /* not a prefab instance */ }
                if (!ctx.FromPrefabContents && go.scene.IsValid() && !string.IsNullOrEmpty(go.scene.path))
                {
                    AddFile(go.scene.path);
                    sceneDirty |= go.scene.isDirty;
                }
            }

            var found = new Dictionary<string, GuidFinding>(StringComparer.Ordinal);
            var statusCache = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
            var dllScripts = new Dictionary<string, Dictionary<long, MonoScript>>(StringComparer.Ordinal);
            long budget = MaxYamlBytes;
            var skipped = new List<string>();
            foreach (var file in files.Take(MaxYamlFiles))
            {
                var abs = CctPaths.AssetToAbsolute(file);
                try
                {
                    var info = new FileInfo(abs);
                    if (!info.Exists) continue;
                    if (info.Length > budget) { skipped.Add(file); continue; }
                    budget -= info.Length;
                    bool isScene = file.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
                    foreach (var mb in ParseYamlMonoBehaviours(abs))
                    {
                        // Scenes may hold other objects with missing scripts: keep only GameObjects we know by name. Prefab files
                        // come from the prefab chain of the affected objects, and instances may be renamed, so they are not filtered.
                        if (isScene && (mb.GameObjectName == null || !names.Contains(mb.GameObjectName))) continue;
                        var key = mb.Guid + ":" + mb.FileId;
                        if (!statusCache.TryGetValue(key, out var status))
                            statusCache[key] = status = ResolveScript(mb.Guid, mb.FileId, dllScripts);
                        if (status.Key == "ok") continue;
                        if (!found.TryGetValue(key, out var g))
                            found[key] = g = new GuidFinding { Guid = mb.Guid, FileId = mb.FileId, Status = status.Key, StatusText = status.Value };
                        g.Count++;
                        if (!g.Files.Contains(file)) g.Files.Add(file);
                        if (mb.GameObjectName != null) g.ObjectNames.Add(mb.GameObjectName);
                        var scriptPath = AssetDatabase.GUIDToAssetPath(mb.Guid);
                        if (!string.IsNullOrEmpty(scriptPath)) g.ScriptPath = scriptPath;
                    }
                }
                catch (Exception e)
                {
                    skipped.Add(file + " (" + e.GetType().Name + ")");
                }
            }
            var notes = new List<string>();
            if (files.Count == 0) notes.Add("GUID を読める保存済みファイル (.prefab / .unity) がありません (未保存のシーン等)");
            if (files.Count > MaxYamlFiles) notes.Add($"ファイルが多いため先頭 {MaxYamlFiles} 個だけ読みました");
            if (skipped.Count > 0) notes.Add("読めなかった/大きすぎるファイル: " + string.Join(", ", skipped.Take(5)));
            if (sceneDirty) notes.Add("シーンに未保存の変更があり、保存済みファイルの内容と違う可能性があります");
            if (found.Count == 0 && files.Count > 0) notes.Add("保存済みファイルからは該当する m_Script を特定できませんでした (バイナリ形式、ネストしたプレハブ内、または未保存の追加)");
            note = notes.Count > 0 ? string.Join("。", notes) : null;
            return found.Values.OrderByDescending(g => g.Count).ToList();
        }

        struct YamlMb
        {
            public string Guid, GameObjectName;
            public long FileId;
        }

        /// <summary>Minimal Unity YAML reader: GameObject names (class 1) and MonoBehaviour (class 114) m_GameObject / m_Script.</summary>
        static List<YamlMb> ParseYamlMonoBehaviours(string absPath)
        {
            var result = new List<YamlMb>();
            var goNames = new Dictionary<long, string>();
            var pending = new List<KeyValuePair<long, KeyValuePair<string, long>>>(); // goFileId -> (guid, scriptFileId)
            using (var reader = new StreamReader(absPath, Encoding.UTF8, true))
            {
                var first = reader.ReadLine();
                if (first == null || !first.StartsWith("%YAML", StringComparison.Ordinal)) return result; // binary serialization
                int cls = 0;
                long docId = 0, mbGo = 0;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("--- !u!", StringComparison.Ordinal))
                    {
                        cls = 0; docId = 0; mbGo = 0;
                        var amp = line.IndexOf(" &", StringComparison.Ordinal);
                        if (amp > 7 && int.TryParse(line.Substring(7, amp - 7), out var c)) cls = c;
                        if (amp > 0)
                        {
                            var rest = line.Substring(amp + 2);
                            var sp = rest.IndexOf(' ');
                            long.TryParse(sp >= 0 ? rest.Substring(0, sp) : rest, out docId);
                        }
                        continue;
                    }
                    if (cls == 1)
                    {
                        if (line.StartsWith("  m_Name: ", StringComparison.Ordinal)) goNames[docId] = YamlScalar(line.Substring(10));
                    }
                    else if (cls == 114)
                    {
                        if (line.StartsWith("  m_GameObject: ", StringComparison.Ordinal)) mbGo = ParseYamlLong(line, "fileID:");
                        else if (line.StartsWith("  m_Script: ", StringComparison.Ordinal))
                        {
                            var guid = ParseYamlToken(line, "guid:");
                            if (!string.IsNullOrEmpty(guid))
                                pending.Add(new KeyValuePair<long, KeyValuePair<string, long>>(mbGo, new KeyValuePair<string, long>(guid, ParseYamlLong(line, "fileID:"))));
                        }
                    }
                }
            }
            foreach (var p in pending)
            {
                goNames.TryGetValue(p.Key, out var name);
                result.Add(new YamlMb { Guid = p.Value.Key, FileId = p.Value.Value, GameObjectName = name });
            }
            return result;
        }

        static string ParseYamlToken(string line, string key)
        {
            int i = line.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length;
            while (i < line.Length && line[i] == ' ') i++;
            int j = i;
            while (j < line.Length && line[j] != ',' && line[j] != '}' && line[j] != ' ') j++;
            return line.Substring(i, j - i);
        }

        static long ParseYamlLong(string line, string key) => long.TryParse(ParseYamlToken(line, key), out var v) ? v : 0;

        /// <summary>Plain, 'single' or "double" quoted YAML scalar on one line ("\uXXXX" escapes decoded, so Japanese names match).</summary>
        static string YamlScalar(string raw)
        {
            var s = raw.Trim();
            if (s.Length >= 2 && s[0] == '\'' && s[s.Length - 1] == '\'') return s.Substring(1, s.Length - 2).Replace("''", "'");
            if (s.Length < 2 || s[0] != '"' || s[s.Length - 1] != '"') return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 1; i < s.Length - 1; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length - 1) { sb.Append(c); continue; }
                char e = s[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case '0': sb.Append('\0'); break;
                    case 'u' when i + 4 < s.Length - 1 &&
                                  int.TryParse(s.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code):
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: sb.Append(e); break; // \" \\ \/ and anything else literally
                }
            }
            return sb.ToString();
        }

        /// <summary>("ok" | "script_not_found" | "class_not_loadable" | "class_not_in_dll" | "unknown", Japanese explanation).</summary>
        static KeyValuePair<string, string> ResolveScript(string guid, long fileId, Dictionary<string, Dictionary<long, MonoScript>> dllScripts)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path) || AssetDatabase.GetMainAssetTypeAtPath(path) == null)
                return new KeyValuePair<string, string>("script_not_found", "このプロジェクトにスクリプトがありません (導入元のパッケージ/スクリプトが未導入)");
            try
            {
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    if (!dllScripts.TryGetValue(path, out var byId))
                    {
                        dllScripts[path] = byId = new Dictionary<long, MonoScript>();
                        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                            if (o is MonoScript ms && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(ms, out string g, out long lid))
                                byId[lid] = ms;
                    }
                    if (byId.TryGetValue(fileId, out var script0))
                        return script0.GetClass() != null
                            ? new KeyValuePair<string, string>("ok", null)
                            : new KeyValuePair<string, string>("class_not_loadable", $"{path} 内のクラスを読み込めません");
                    return new KeyValuePair<string, string>("class_not_in_dll", $"{path} にこのクラスがありません (DLL の版が違う可能性)");
                }
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script == null) return new KeyValuePair<string, string>("unknown", $"{path} はスクリプトではありません");
                return script.GetClass() != null
                    ? new KeyValuePair<string, string>("ok", null)
                    : new KeyValuePair<string, string>("class_not_loadable", $"{path} はありますがクラスを読み込めません (コンパイルエラー、またはクラス名とファイル名の不一致)");
            }
            catch (Exception e)
            {
                return new KeyValuePair<string, string>("unknown", e.GetType().Name + ": " + e.Message);
            }
        }

        // =====================================================================================================================
        // Missing references (bounded SerializedObject walk)
        // =====================================================================================================================

        static readonly HashSet<string> PrimitiveArrayElements = new HashSet<string>(StringComparer.Ordinal)
        {
            "float", "double", "int", "unsigned int", "bool", "char", "string", "UInt8", "SInt8", "UInt16", "SInt16", "UInt32", "SInt32",
            "UInt64", "SInt64", "Vector2f", "Vector3f", "Vector4f", "Quaternionf", "ColorRGBA", "Matrix4x4f", "Keyframe", "BoneWeight",
            "BoneWeight1", "int2_storage", "int3_storage", "float2", "float3", "float4",
        };

        static readonly HashSet<string> SkipReferencePaths = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_Script", "m_GameObject", "m_PrefabInstance", "m_PrefabAsset", "m_CorrespondingSourceObject", "m_PrefabInternal", "m_Father",
        };

        static Dictionary<string, object> CheckMissingReferences(Ctx ctx)
        {
            var rows = new List<object>();
            int count = 0, visits = 0;
            bool truncated = false;
            foreach (var c in ctx.Scan.Components)
            {
                if (c == null || c is Transform) continue;
                List<string> missing = null;
                try
                {
                    using (var so = new SerializedObject(c))
                    {
                        var it = so.GetIterator();
                        bool enter = true;
                        while (it.Next(enter))
                        {
                            visits++;
                            enter = it.propertyType == SerializedPropertyType.Generic || it.propertyType == SerializedPropertyType.ManagedReference;
                            if (enter && it.isArray && PrimitiveArrayElements.Contains(it.arrayElementType)) enter = false;
                            if (it.propertyType == SerializedPropertyType.ObjectReference && !SkipReferencePaths.Contains(it.propertyPath) &&
                                it.objectReferenceValue == null && it.objectReferenceInstanceIDValue != 0)
                                (missing ??= new List<string>()).Add(it.propertyPath);
                            if (visits >= MaxMissingRefVisits) { truncated = true; break; }
                        }
                    }
                }
                catch (Exception) { /* a component that refuses SerializedObject: skip */ }
                if (missing != null)
                {
                    count += missing.Count;
                    var path = ctx.PathOf(c);
                    var type = c.GetType().Name;
                    if (rows.Count < 50)
                        rows.Add(new Dictionary<string, object> { ["path"] = path, ["component"] = type, ["properties"] = missing.Take(10).Cast<object>().ToList() });
                    ctx.Issue(CctSeverity.Warning, "MISSING_REFERENCE",
                        $"{type} の参照先が見つかりません (Missing): {string.Join(", ", missing.Take(3))}{(missing.Count > 3 ? $" ほか {missing.Count - 3} 件" : "")}。削除されたアセット/オブジェクトを指しています",
                        path, missing.Count);
                }
                if (truncated) break;
            }
            var d = new Dictionary<string, object> { ["count"] = count, ["items"] = rows };
            if (truncated)
            {
                d["truncated"] = true;
                d["note"] = $"プロパティが多いため {MaxMissingRefVisits} 個で走査を打ち切りました";
            }
            return d;
        }

        // =====================================================================================================================
        // Component whitelist (SDK AvatarValidation lists; derived types allowed; IEditorOnly / EditorOnly-tagged excluded)
        // =====================================================================================================================

        static void CheckWhitelist(Ctx ctx)
        {
            var groups = new Dictionary<string, KeyValuePair<string, List<string>>>(StringComparer.Ordinal); // type -> (code, paths)
            var dynamicBones = new List<string>();
            var buildToolUnmarked = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var c in ctx.Scan.Components)
            {
                var ti = Info(c.GetType());
                if (ti.SilentlyRemoved || ti.EditorOnlyIface || ctx.IsEditorOnly(c)) continue;
                if (ti.DynamicBone) { dynamicBones.Add(ctx.PathOf(c)); continue; }
                if (c is IConstraint) continue; // UNITY_CONSTRAINTS
                if (ti.Family != null)
                {
                    // Build-time tool component not marked IEditorOnly: its tool removes it at build time.
                    if (!ti.AllowedPc) Inc(buildToolUnmarked, ti.FullName);
                    continue;
                }
                string code = null;
                if (ctx.ActiveMobile ? !ti.AllowedMobile : !ti.AllowedPc) code = "NOT_WHITELISTED";
                else if (!ctx.ActiveMobile && ctx.WantMobile && !ti.AllowedMobile) code = "NOT_WHITELISTED_MOBILE";
                if (code == null) continue;
                if (!groups.TryGetValue(ti.FullName, out var g)) groups[ti.FullName] = g = new KeyValuePair<string, List<string>>(code, new List<string>());
                g.Value.Add(ctx.PathOf(c));
            }

            foreach (var kv in groups.OrderByDescending(k => k.Value.Value.Count))
            {
                var code = kv.Value.Key;
                var paths = kv.Value.Value;
                bool mobileOnly = code == "NOT_WHITELISTED_MOBILE";
                ctx.NonWhitelisted.Add(new Dictionary<string, object>
                {
                    ["type"] = kv.Key,
                    ["count"] = paths.Count,
                    ["platform"] = mobileOnly ? "mobile" : ctx.ActiveMobile ? "mobile" : "pc",
                    ["examples"] = paths.Take(5).Cast<object>().ToList(),
                });
                if (mobileOnly)
                    ctx.Issue(ctx.ActiveMobile ? CctSeverity.Error : CctSeverity.Warning, code,
                        $"{kv.Key} ({paths.Count} 個) は PC では使えますが Android/iOS では許可されていません。モバイル版をアップロードするとクライアントで削除され、SDK もモバイルのビルドターゲットではエラーにします: {Examples(paths, paths.Count)}",
                        paths[0], paths.Count);
                else
                    ctx.Issue(CctSeverity.Error, code,
                        $"{kv.Key} ({paths.Count} 個) はアバターで許可されていないコンポーネントです (ビルドターゲット {ctx.ActiveTarget})。SDK エラー \"will be removed by the client\" になります: {Examples(paths, paths.Count)}",
                        paths[0], paths.Count);
            }
            if (buildToolUnmarked.Count > 0)
                ctx.NonWhitelisted.Add(new Dictionary<string, object>
                {
                    ["type"] = "(build-time tool components)",
                    ["count"] = buildToolUnmarked.Values.Sum(),
                    ["platform"] = "none",
                    ["note"] = "MA/AAO/VRCFury 等のビルド時ツールのコンポーネントです。ビルド時にそのツールが取り除く前提のため違反として扱っていません",
                    ["types"] = buildToolUnmarked.Keys.Take(10).Cast<object>().ToList(),
                });
            if (dynamicBones.Count > 0)
                ctx.Issue(CctSeverity.Error, "DYNAMIC_BONE_PRESENT",
                    $"DynamicBone / DynamicBoneCollider が {dynamicBones.Count} 個あります。SDK 3.7.0 以降は PhysBone への変換が必須です (SDK の Auto Fix で変換できます): {Examples(dynamicBones, dynamicBones.Count)}",
                    dynamicBones[0], dynamicBones.Count);
        }

        static void CheckUnityConstraints(Ctx ctx)
        {
            // SDK: every IConstraint under the avatar (EditorOnly included), minus constraints claimed by user tooling
            // (MA Convert Constraints claims those with the component on an ancestor inside the same avatar).
            var plain = new List<string>();
            var converted = new List<string>();
            foreach (var c in ctx.Scan.Components)
            {
                if (!(c is IConstraint)) continue;
                bool claimed = false;
                for (var t = c.transform; t != null; t = t.parent)
                {
                    if (t.GetComponents<Component>().Any(x => x != null && CctSerialized.IsType(x, TMaConvertConstraints))) { claimed = true; break; }
                    if (t == ctx.RootT) break;
                }
                (claimed ? converted : plain).Add(ctx.PathOf(c));
            }
            if (plain.Count > 0)
                ctx.Issue(ctx.ActiveMobile ? CctSeverity.Error : CctSeverity.Warning, "UNITY_CONSTRAINTS",
                    $"Unity Constraint が {plain.Count} 個あります。{(ctx.ActiveMobile ? "モバイルでは使えず SDK エラーになります" : "PC では警告ですが、Android/iOS では使えません (SDK エラー)")}。VRChat Constraint に変換してください (SDK の Auto Fix、または MA Convert Constraints): {Examples(plain, plain.Count)}",
                    plain[0], plain.Count);
            if (converted.Count > 0)
                ctx.Issue(CctSeverity.Info, "UNITY_CONSTRAINTS",
                    $"Unity Constraint {converted.Count} 個は MA Convert Constraints がビルド時に VRChat Constraint へ変換します (SDK もこれらを警告から除外します)",
                    converted[0], converted.Count);
        }

        // =====================================================================================================================
        // Particles / trails / audio (SDK ScanAvatarForProblematicParticleSystems: all systems, EditorOnly included)
        // =====================================================================================================================

        static void CheckParticlesTrailsAudio(Ctx ctx, Est est)
        {
            var destroy = new List<string>();
            var lightSystems = new List<string>();
            foreach (var c in ctx.Scan.Components)
            {
                if (c is ParticleSystem ps)
                {
                    var main = ps.main;
                    if (main.stopAction == ParticleSystemStopAction.Destroy) destroy.Add(ctx.PathOf(ps));
                    else if (main.stopAction == ParticleSystemStopAction.Disable && ps.gameObject == ctx.Root)
                        ctx.Issue(CctSeverity.Error, "PARTICLE_ROOT_STOP_DISABLE",
                            "アバタールートの Particle System の Stop Action が Disable です。ルートが無効化されるため SDK エラーになります (None にしてください)", ctx.PathOf(ps));
                    var lights = ps.lights;
                    if (lights.light != null && lights.maxLights > 0) lightSystems.Add(ctx.PathOf(ps));
                }
                else if (c is TrailRenderer tr && tr.autodestruct)
                    ctx.Issue(CctSeverity.Error, "TRAIL_AUTODESTRUCT", "Trail Renderer の Autodestruct が有効です (SDK エラー)。無効にしてください", ctx.PathOf(tr));
            }
            if (destroy.Count > 0)
                ctx.Issue(CctSeverity.Error, "PARTICLE_STOP_ACTION_DESTROY",
                    $"Stop Action が Destroy の Particle System が {destroy.Count} 個あります (SDK エラー)。Disable か None にしてください: {Examples(destroy, destroy.Count)}",
                    destroy[0], destroy.Count);
            if (lightSystems.Count > 0)
                ctx.Issue(CctSeverity.Warning, "PARTICLE_LIGHTS",
                    $"Lights モジュールに Light が設定された Particle System が {lightSystems.Count} 個あります。モジュールが無効でも Lights の数に加算されます (SDK 警告): {Examples(lightSystems, lightSystems.Count)}",
                    lightSystems[0], lightSystems.Count);

            // SDK: AudioSources excluding EditorOnly; clips with Decompress On Load must have Load In Background.
            var clips = new Dictionary<AudioClip, string>();
            foreach (var a in est.AudioSources)
            {
                var clip = a != null ? a.clip : null;
                if (clip != null && clip.loadType == AudioClipLoadType.DecompressOnLoad && !clip.loadInBackground && !clips.ContainsKey(clip))
                    clips[clip] = ctx.PathOf(a);
            }
            foreach (var kv in clips)
                ctx.Issue(CctSeverity.Error, "AUDIO_DECOMPRESS_ON_LOAD",
                    $"AudioClip '{kv.Key.name}' は Load Type が Decompress On Load で Load In Background が無効です (SDK エラー)。インポート設定で Load In Background を有効にしてください",
                    kv.Value, asset: AssetPathOrName(kv.Key));
        }

        // =====================================================================================================================
        // Mesh import settings (SDK: SMR / MeshFilter meshes excluding EditorOnly, model-imported meshes only)
        // =====================================================================================================================

        static PropertyInfo _legacyBlendShapeNormals;
        static bool _legacyLooked;

        static void CheckMeshImporters(Ctx ctx)
        {
            var meshes = new HashSet<Mesh>();
            var buffer = new Mesh[0];
            foreach (var c in ctx.Scan.Components)
            {
                if (ctx.IsEditorOnly(c)) continue;
                switch (c)
                {
                    case SkinnedMeshRenderer smr when smr.sharedMesh != null: meshes.Add(smr.sharedMesh); break;
                    case MeshFilter mf when mf.sharedMesh != null: meshes.Add(mf.sharedMesh); break;
                    case ParticleSystemRenderer psr when psr.meshCount > 0:
                        if (buffer.Length < psr.meshCount) buffer = new Mesh[psr.meshCount];
                        int n = psr.GetMeshes(buffer);
                        for (int i = 0; i < n; i++) if (buffer[i] != null) meshes.Add(buffer[i]);
                        break;
                }
            }
            if (!_legacyLooked)
            {
                _legacyLooked = true;
                try
                {
                    _legacyBlendShapeNormals = typeof(ModelImporter).GetProperty("legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                catch (Exception) { _legacyBlendShapeNormals = null; }
            }

            var notReadable = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var legacyOff = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var mesh in meshes)
            {
                if (!AssetDatabase.Contains(mesh)) continue;
                var path = AssetDatabase.GetAssetPath(mesh);
                if (string.IsNullOrEmpty(path) || !(AssetImporter.GetAtPath(path) is ModelImporter mi)) continue;
                if (!mi.isReadable) Add(notReadable, path, mesh.name);
                if (mi.importBlendShapeNormals == ModelImporterNormals.Calculate && _legacyBlendShapeNormals != null)
                {
                    bool legacy = true;
                    try { legacy = (bool)_legacyBlendShapeNormals.GetValue(mi, null); }
                    catch (Exception) { /* unknown: do not report */ }
                    if (!legacy) Add(legacyOff, path, mesh.name);
                }
            }
            foreach (var kv in notReadable)
                ctx.Issue(CctSeverity.Error, "MESH_NOT_READABLE",
                    $"Read/Write が無効なモデルです (メッシュ: {string.Join(", ", kv.Value.Take(5))})。SDK エラーでアップロードできず、docs ではゲーム内の Triangles が \"Mesh Read/Write Disabled\" (Very Poor) 扱いになります。インポート設定で Read/Write を有効にしてください",
                    asset: kv.Key, value: kv.Value.Count);
            foreach (var kv in legacyOff)
                ctx.Issue(CctSeverity.Error, "BLENDSHAPE_NORMALS_LEGACY_OFF",
                    $"Blend Shape Normals が Calculate なのに Legacy Blend Shape Normals が無効です (メッシュ: {string.Join(", ", kv.Value.Take(5))})。アップロードサイズが大きく増えるため SDK エラーになります",
                    asset: kv.Key, value: kv.Value.Count);

            void Add(Dictionary<string, List<string>> d, string key, string value)
            {
                if (!d.TryGetValue(key, out var l)) d[key] = l = new List<string>();
                if (!l.Contains(value)) l.Add(value);
            }
        }

        // =====================================================================================================================
        // Texture import settings (SDK VerifyAvatarMipMapStreaming / VerifyMaxTextureSize / VerifyTextureMipFiltering)
        // =====================================================================================================================

        /// <summary>VRCSdkControlPanelBuilder.MAX_SDK_TEXTURE_SIZE (SDK 3.10.5).</summary>
        const int MaxSdkTextureSize = 8192;
        const double UncompressedWarnMb = 4.0;

        static void CheckTextures(Ctx ctx, Est est)
        {
            var streaming = new List<string>();
            var oversize = new List<string>();
            var oversizeAsset = new List<string>();
            var box = new List<string>();
            var uncompressedBig = new List<string>();
            var uncompressedSmall = new List<string>();
            string bigAsset = null, smallAsset = null;
            foreach (var te in est.Textures.Values.OrderByDescending(t => t.Bytes))
            {
                var imp = te.Importer;
                if (imp == null) continue;
                var label = te.AssetPath;
                if (imp.mipmapEnabled && !imp.streamingMipmaps) streaming.Add(label);
                if (imp.maxTextureSize > MaxSdkTextureSize) { oversize.Add($"{label} (Max Size {imp.maxTextureSize})"); oversizeAsset.Add(label); }
                if (imp.mipmapEnabled && imp.mipmapFilter == TextureImporterMipFilter.BoxFilter) box.Add(label);
                if (imp.textureCompression == TextureImporterCompression.Uncompressed)
                {
                    var mb = Ranks.BytesToMegabytes(te.Bytes);
                    bool big = mb >= UncompressedWarnMb;
                    (big ? uncompressedBig : uncompressedSmall).Add($"{label} ({Round(mb, 2)} MB)");
                    if (big) bigAsset ??= label; else smallAsset ??= label;
                }
            }
            if (streaming.Count > 0)
                ctx.Issue(CctSeverity.Error, "TEXTURE_MIP_STREAMING_OFF",
                    $"Mip Map が有効で Streaming Mip Maps が無効なテクスチャが {streaming.Count} 枚あります (SDK エラー、SDK の Auto Fix で一括修正できます): {Examples(streaming, streaming.Count)}",
                    asset: streaming[0], value: streaming.Count);
            if (oversize.Count > 0)
                ctx.Issue(CctSeverity.Error, "TEXTURE_MAX_SIZE_OVER_8192",
                    $"Max Size が {MaxSdkTextureSize} を超えるテクスチャが {oversize.Count} 枚あります (SDK エラー): {Examples(oversize, oversize.Count)}",
                    asset: oversizeAsset[0], value: oversize.Count, limit: MaxSdkTextureSize);
            if (uncompressedBig.Count > 0)
                ctx.Issue(CctSeverity.Warning, "TEXTURE_UNCOMPRESSED",
                    $"非圧縮 (Compression = None) で {UncompressedWarnMb} MB 以上のテクスチャが {uncompressedBig.Count} 枚あります。Texture Memory を大きく消費します: {Examples(uncompressedBig, uncompressedBig.Count)}",
                    asset: bigAsset, value: uncompressedBig.Count);
            if (uncompressedSmall.Count > 0)
                ctx.Issue(CctSeverity.Info, "TEXTURE_UNCOMPRESSED",
                    $"非圧縮 (Compression = None) の小さいテクスチャが {uncompressedSmall.Count} 枚あります: {Examples(uncompressedSmall, uncompressedSmall.Count)}",
                    asset: smallAsset, value: uncompressedSmall.Count);
            if (box.Count > 0)
                ctx.Issue(CctSeverity.Info, "TEXTURE_BOX_MIP_FILTER",
                    $"Mip Map Filter が Box のテクスチャが {box.Count} 枚あります。SDK は遠景がぼやけにくい Kaiser を推奨しています: {Examples(box, box.Count)}",
                    asset: box[0], value: box.Count);
        }

        // =====================================================================================================================
        // Mobile shader whitelist (SDK AvatarValidation.ShaderWhiteList; all renderers incl. EditorOnly, as FindIllegalShaders)
        // =====================================================================================================================

        static readonly HashSet<string> MobileShaderWhitelist = new HashSet<string>(StringComparer.Ordinal)
        {
            "VRChat/Mobile/Standard Lite", "VRChat/Mobile/Diffuse", "VRChat/Mobile/Bumped Diffuse", "VRChat/Mobile/Bumped Mapped Specular",
            "VRChat/Mobile/Toon Lit", "VRChat/Mobile/MatCap Lit", "VRChat/Mobile/Particles/Additive", "VRChat/Mobile/Particles/Multiply",
            "VRChat/Mobile/Toon Standard", "VRChat/Mobile/Toon Standard (Outline)",
        };
        const string OutlineFallbackShader = "VRChat/Mobile/Toon Standard (Outline)";

        static void CheckShaders(Ctx ctx, Est est)
        {
            if (!ctx.WantMobile) return;
            var byShader = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var seen = new HashSet<Material>();
            var mats = new List<Material>();
            bool outline = false;
            foreach (var r in est.AllRenderers)
            {
                if (r == null) continue;
                r.GetSharedMaterials(mats);
                foreach (var m in mats)
                {
                    if (m == null || m.shader == null || !seen.Add(m)) continue;
                    var name = m.shader.name;
                    if (name == OutlineFallbackShader) outline = true;
                    if (MobileShaderWhitelist.Contains(name)) continue;
                    if (!byShader.TryGetValue(name, out var l)) byShader[name] = l = new List<string>();
                    l.Add(AssetPathOrName(m));
                }
            }
            foreach (var kv in byShader.OrderByDescending(k => k.Value.Count))
                ctx.Issue(ctx.ActiveMobile ? CctSeverity.Error : CctSeverity.Warning, "ANDROID_INCOMPATIBLE_SHADER",
                    $"シェーダー '{kv.Key}' (マテリアル {kv.Value.Count} 個) は Android/iOS のアバターで使えません。VRChat/Mobile のシェーダーにしてください (モバイルのビルドターゲットでは SDK エラー): {Examples(kv.Value, kv.Value.Count)}",
                    value: kv.Key, asset: kv.Value[0]);
            if (outline)
                ctx.Issue(CctSeverity.Info, "ANDROID_INCOMPATIBLE_SHADER",
                    $"'{OutlineFallbackShader}' は SDK では許可されますが、モバイルのクライアントでは Outline なしの Toon Standard に置き換わります (SDK のコメント)", value: OutlineFallbackShader);
        }

        // =====================================================================================================================
        // Pipeline manager
        // =====================================================================================================================

        static void CheckPipeline(Ctx ctx, Est est)
        {
            if (est.PipelineManagers != 1)
                ctx.Issue(CctSeverity.Error, "PIPELINE_MANAGER_COUNT",
                    est.PipelineManagers == 0
                        ? "PipelineManager がありません (SDK エラー。SDK の Auto Fix でルートに追加できます)"
                        : $"PipelineManager が {est.PipelineManagers} 個あります。ルートの 1 個だけにしてください (SDK エラー、Auto Fix あり)",
                    ctx.PathOf(ctx.RootT), est.PipelineManagers, 1);
            var id = CctVrcTypes.ReadBlueprintId(ctx.Root);
            if (id != null && id.Length == 0)
                ctx.Issue(CctSeverity.Info, "BLUEPRINT_ID_EMPTY", "Blueprint ID が空です。アップロードすると新しいアバターとして登録されます", ctx.PathOf(ctx.RootT));
        }

        // =====================================================================================================================
        // Root Animator / humanoid (SDK builder checks + AnalyzeIK), viewpoint, lip sync
        // =====================================================================================================================

        static void CheckAnimatorAndHumanoid(Ctx ctx)
        {
            var rootPath = ctx.PathOf(ctx.RootT);
            var anim = ctx.RootAnimator;
            if (anim == null)
            {
                ctx.Issue(CctSeverity.Error, "ROOT_ANIMATOR_MISSING", "アバタールートに Animator がありません (SDK エラー)", rootPath);
                return;
            }
            var human = ctx.Human;
            if (human == null || !human.IsHuman)
            {
                ctx.Issue(CctSeverity.Warning, "AVATAR_NOT_HUMANOID",
                    $"Animator の Avatar が Humanoid ではありません ({(anim.avatar == null ? "Avatar 未設定" : "Generic など")})。VRChat 標準のアニメーションやフルトラッキングが使えず、カスタム Fallback にもできません", rootPath);
                return;
            }
            bool active = ctx.FromPrefabContents ? ctx.Root.activeSelf : ctx.Root.activeInHierarchy;
            if (!active)
                ctx.Issue(ctx.PrefabAssetPath != null ? CctSeverity.Warning : CctSeverity.Error, "AVATAR_INACTIVE",
                    "アバターが非アクティブです。SDK はヒエラルキーで無効なアバターをアップロードできません (エラー)", rootPath);

            var missing = SdkRequiredBones.Where(b => human.Get(b) == null).Select(b => b.ToString()).ToList();
            if (missing.Count > 0)
                ctx.Issue(CctSeverity.Error, "HUMANOID_BONES_MISSING",
                    $"Humanoid の必須ボーンが割り当てられていません: {string.Join(", ", missing)} (SDK は頭・手・足・腰・背骨・胸・首・肩・上腕が必要)" +
                    (human.Source != "animator" ? " [Avatar の設定から名前で照合]" : ""), rootPath, missing.Count);

            var optional = OptionalBones.Where(b => human.Get(b) == null).Select(b => b.ToString()).ToList();
            int leftFingers = 0, rightFingers = 0;
            for (var b = HumanBodyBones.LeftThumbProximal; b <= HumanBodyBones.RightLittleDistal; b++)
                if (human.Get(b) != null) { if (IsLeftFinger(b)) leftFingers++; else rightFingers++; }
            if (leftFingers < 15 || rightFingers < 15)
                optional.Add($"指 (左 {leftFingers}/15, 右 {rightFingers}/15)");
            if (optional.Count > 0)
                ctx.Issue(CctSeverity.Info, "HUMANOID_OPTIONAL_BONES_MISSING",
                    $"任意のボーンで未割り当てのものがあります: {string.Join(", ", optional)}。ハンドジェスチャーや持ち物の握り、視線・口の動きに影響することがあります", rootPath);

            // AnalyzeIK hierarchy rules.
            var chest = human.Get(HumanBodyBones.Chest);
            var upper = human.Get(HumanBodyBones.UpperChest);
            var neck = human.Get(HumanBodyBones.Neck);
            var lClav = human.Get(HumanBodyBones.LeftShoulder);
            var rClav = human.Get(HumanBodyBones.RightShoulder);
            if (chest != null && neck != null && lClav != null && rClav != null)
            {
                var expected = upper != null ? upper : chest;
                if (lClav.parent != expected || rClav.parent != expected || neck.parent != expected)
                    ctx.Issue(CctSeverity.Error, "HUMANOID_SPINE_HIERARCHY",
                        $"肩と首の親が {(upper != null ? "UpperChest" : "Chest")} ではありません (SDK エラー: Spine hierarchy incorrect)", ctx.PathOf(expected));
            }
            if (!FirstChildChain(human, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand) ||
                !FirstChildChain(human, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand) ||
                !FirstChildChain(human, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot) ||
                !FirstChildChain(human, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot))
                ctx.Issue(CctSeverity.Warning, "HUMANOID_LIMB_CHILD_ORDER",
                    "LowerArm が UpperArm の最初の子でない (または Hand/Foot が最初の子でない) 腕・脚があります。前腕・すねの回転が崩れることがあります (SDK 警告)", rootPath);
            var hips = human.Get(HumanBodyBones.Hips);
            if (hips != null && new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot }
                    .Any(b => human.Get(b) != null && !CctTargets.IsDescendantOf(human.Get(b), hips)))
                ctx.Issue(CctSeverity.Warning, "HUMANOID_SPLIT_HIERARCHY",
                    "Hips がすべての手足ボーンの祖先になっていません (split hierarchy)。IK が正しく動かないことがあります (SDK 警告)", ctx.PathOf(hips));

            // Armature must be a direct child of the root (SDK reads Animator.avatarRoot after a non-writing rebind; we read the cached
            // value). Animator.avatarRoot is public in Unity 2022.3 but absent from older APIs, hence reflection.
            try
            {
                var avatarRoot = typeof(Animator).GetProperty("avatarRoot", BindingFlags.Public | BindingFlags.Instance)?.GetValue(anim, null) as Transform;
                if (avatarRoot != null && avatarRoot != ctx.RootT && avatarRoot.IsChildOf(ctx.RootT))
                    ctx.Issue(CctSeverity.Warning, "ARMATURE_NOT_DIRECT_CHILD",
                        "Armature がアバタールートの直下にありません。SDK はこれをエラーにします (Auto Fix で親子付けを直せます)。Animator の現在のキャッシュ値で判定しています",
                        ctx.PathOf(avatarRoot));
            }
            catch (Exception) { /* avatarRoot unavailable */ }

            var lFoot = human.Get(HumanBodyBones.LeftFoot);
            var rFoot = human.Get(HumanBodyBones.RightFoot);
            if (lFoot != null && rFoot != null && lFoot.position.y - ctx.RootT.position.y < 0)
                ctx.Issue(CctSeverity.Warning, "FEET_BELOW_ORIGIN", "足がアバターの原点 (床) より下にあります (SDK 警告)", ctx.PathOf(lFoot),
                    Round(lFoot.position.y - ctx.RootT.position.y));

            var lArm = human.Get(HumanBodyBones.LeftUpperArm);
            var rArm = human.Get(HumanBodyBones.RightUpperArm);
            if (lArm != null && rArm != null)
            {
                float h = lArm.position.y - ctx.RootT.position.y;
                string msg = null, sev = CctSeverity.Warning;
                if (h < 0.2f) { msg = "肩の高さが 20cm 未満で低すぎます (SDK エラー)"; sev = CctSeverity.Error; }
                else if (h < 1.0f) msg = "肩の高さが 1m 未満で平均より低いアバターです (SDK 警告)";
                else if (h > 5.0f) msg = "肩の高さが 5m を超えて高すぎます (SDK 警告)";
                else if (h > 2.5f) msg = "肩の高さが 2.5m を超えて平均より高いアバターです (SDK 警告)";
                if (msg != null) ctx.Issue(sev, "AVATAR_HEIGHT", $"{msg}: {Round(h, 2)} m", ctx.PathOf(lArm), Round(h, 3));
            }
        }

        static bool FirstChildChain(HumanInfo human, HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones end)
        {
            var u = human.Get(upper);
            var l = human.Get(lower);
            var e = human.Get(end);
            if (u == null || l == null || e == null) return true; // missing bones are reported separately
            return u.childCount > 0 && u.GetChild(0) == l && l.childCount > 0 && l.GetChild(0) == e;
        }

        static void CheckDescriptorSettings(Ctx ctx)
        {
            Vector3 view = Vector3.zero;
            bool hasView = false;
            int lip;
            Object visemeMesh;
            using (var so = new SerializedObject(ctx.Descriptor))
            {
                var vp = so.FindProperty("ViewPosition");
                if (vp != null && vp.propertyType == SerializedPropertyType.Vector3) { view = vp.vector3Value; hasView = true; }
                lip = CctSerialized.GetInt(so.FindProperty("lipSync"));
                visemeMesh = CctSerialized.GetObject(so.FindProperty("VisemeSkinnedMesh"));
            }
            var descPath = ctx.PathOf(ctx.Descriptor);
            if (lip == LipSyncVisemeBlendShape && visemeMesh == null)
                ctx.Issue(CctSeverity.Error, "VISEME_MESH_MISSING", "Lip Sync が Viseme Blend Shape なのに Face Mesh が未設定です (SDK エラー)", descPath);

            if (!hasView) return;
            // Heuristic (not an SDK rule): the viewpoint should sit near the eyes, i.e. close to the head bone.
            if (view == Vector3.zero)
            {
                ctx.Issue(CctSeverity.Warning, "VIEWPOINT_SUSPICIOUS", "View Position が (0, 0, 0) のままです。目の位置に合わせてください", descPath, Vec(view));
                return;
            }
            var world = ctx.RootT.TransformPoint(view);
            if (world.y < ctx.RootT.position.y)
                ctx.Issue(CctSeverity.Warning, "VIEWPOINT_SUSPICIOUS", "View Position がアバターの原点 (床) より下にあります", descPath, Vec(view));
            var head = ctx.Human?.Get(HumanBodyBones.Head);
            if (head == null) return;
            float scale = Mathf.Max(0.01f, Mathf.Abs(ctx.RootT.lossyScale.y));
            var shoulder = ctx.Human.Get(HumanBodyBones.LeftUpperArm);
            float sizeFactor = shoulder != null ? Mathf.Max(scale, (shoulder.position.y - ctx.RootT.position.y) / 1.4f) : scale;
            float limit = 0.35f * sizeFactor;
            float dist = Vector3.Distance(world, head.position);
            if (dist > limit)
                ctx.Issue(CctSeverity.Warning, "VIEWPOINT_SUSPICIOUS",
                    $"View Position が頭のボーンから {Round(dist, 2)} m 離れています (目安 {Round(limit, 2)} m 以内、ツール独自の目安)。目の位置に合っているか確認してください",
                    descPath, Round(dist, 3), Round(limit, 3));
        }

        // =====================================================================================================================
        // Playable layers and Write Defaults (SDK ScanAvatarForWriteDefaultsMixture / ...OffEmptyClips, per controller)
        // =====================================================================================================================

        sealed class WdScan
        {
            public int On, Off, EmptyOff, States;
            public readonly List<string> OnExamples = new List<string>(), OffExamples = new List<string>(), EmptyExamples = new List<string>();
        }

        static void CheckLayers(Ctx ctx)
        {
            bool customize;
            using (var so = new SerializedObject(ctx.Descriptor))
                customize = CctSerialized.GetBool(so.FindProperty("customizeAnimationLayers"));
            var descPath = ctx.PathOf(ctx.Descriptor);
            if (customize)
            {
                foreach (var l in ctx.Layers)
                {
                    if (l.ControllerMissing)
                        ctx.Issue(CctSeverity.Warning, "LAYER_CONTROLLER_MISSING",
                            $"{l.TypeName} レイヤーの Animator Controller の参照先が削除されています (Missing)", descPath, l.TypeName);
                    else if (!l.IsDefault && l.Controller == null && l.Type != 1)
                        ctx.Issue(CctSeverity.Warning, "LAYER_CONTROLLER_MISSING",
                            $"{l.TypeName} レイヤーがカスタム (Default ではない) なのに Animator Controller が未設定です。このレイヤーは空になります", descPath, l.TypeName);
                }
                // SDK: Gesture slot (baseAnimationLayers[2]) of a humanoid, custom controller, its first layer needs an AvatarMask.
                var gesture = ctx.Layers.FirstOrDefault(l => l.Slot == "base" && l.Index == 2);
                if (gesture != null && gesture.Type == LayerTypeGesture && !gesture.IsDefault && ctx.Human != null && ctx.Human.IsHuman &&
                    gesture.Controller is AnimatorController gc)
                {
                    var layers = gc.layers;
                    if (layers.Length > 0 && layers[0].avatarMask == null)
                        ctx.Issue(CctSeverity.Error, "GESTURE_MASK_MISSING",
                            $"Gesture レイヤーのコントローラー '{gc.name}' の最初のレイヤーに AvatarMask がありません (SDK エラー)", descPath, asset: AssetPathOrName(gc));
                }
            }

            // Write Defaults: every base layer AnimatorController (override controllers are not inspected, as in the SDK).
            var uniform = new List<KeyValuePair<string, bool>>();
            var seen = new HashSet<AnimatorController>();
            foreach (var l in ctx.Layers)
            {
                if (l.Slot != "base" || !(l.Controller is AnimatorController ac) || !seen.Add(ac)) continue;
                var scan = new WdScan();
                foreach (var layer in ac.layers)
                {
                    bool additive = layer.blendingMode == AnimatorLayerBlendingMode.Additive;
                    ScanWd(layer.stateMachine, additive, layer.name, scan, 0);
                }
                var asset = AssetPathOrName(ac);
                if (scan.On > 0 && scan.Off > 0)
                    ctx.Issue(CctSeverity.Warning, "WD_MIXED",
                        $"{l.TypeName} のコントローラー '{ac.name}' で Write Defaults の ON ({scan.On}) と OFF ({scan.Off}) が混在しています (SDK 警告。Additive レイヤーと Direct BlendTree のステートは除外)。" +
                        $"例: ON {string.Join(", ", scan.OnExamples.Take(3))} / OFF {string.Join(", ", scan.OffExamples.Take(3))}",
                        descPath, new Dictionary<string, object> { ["on"] = scan.On, ["off"] = scan.Off }, asset: asset);
                else if (scan.On + scan.Off > 0)
                    uniform.Add(new KeyValuePair<string, bool>($"{l.TypeName} '{ac.name}'", scan.On > 0));
                if (scan.EmptyOff > 0)
                    ctx.Issue(CctSeverity.Warning, "WD_OFF_EMPTY_MOTION",
                        $"{l.TypeName} のコントローラー '{ac.name}' に、Write Defaults OFF で Motion が空 (未設定 / 空のクリップ / BlendTree の空の子) のステートが {scan.EmptyOff} 個あります (SDK 警告)。少なくとも 1 つのプロパティを持つクリップを入れてください。例: {string.Join(", ", scan.EmptyExamples.Take(3))}",
                        descPath, scan.EmptyOff, asset: asset);
            }
            if (uniform.Count > 1 && uniform.Any(u => u.Value) && uniform.Any(u => !u.Value))
                ctx.Issue(CctSeverity.Info, "WD_MIXED_ACROSS_CONTROLLERS",
                    $"コントローラーごとには統一されていますが、ON と OFF のコントローラーがあります (ON: {string.Join(", ", uniform.Where(u => u.Value).Select(u => u.Key))} / OFF: {string.Join(", ", uniform.Where(u => !u.Value).Select(u => u.Key))})。docs はアバター全体での統一を推奨しています (SDK は警告しません)",
                    descPath);
            if (ctx.Scan.TypeCounts.TryGetValue(TMaMergeAnimator, out var merges) && merges > 0 && ctx.Layers.Any(l => l.Controller != null))
                ctx.Issue(CctSeverity.Info, "WD_MERGE_ANIMATOR_NOT_CHECKED",
                    $"MA Merge Animator が {merges} 個あります。そのコントローラーはビルド時に統合されるため、ここの Write Defaults 検査には含まれていません (cct_animator_inspect または post_build=true で確認できます)", descPath);
        }

        static void ScanWd(AnimatorStateMachine sm, bool additive, string prefix, WdScan scan, int depth)
        {
            if (sm == null || depth > 32) return;
            foreach (var cs in sm.states)
            {
                var state = cs.state;
                if (state == null) continue;
                scan.States++;
                var label = prefix + "/" + state.name;
                bool wd = state.writeDefaultValues;
                if (!wd && IsEmptyMotion(state.motion, 0))
                {
                    scan.EmptyOff++;
                    if (scan.EmptyExamples.Count < 5) scan.EmptyExamples.Add(label);
                }
                if (additive) continue;
                if (state.motion is BlendTree bt && bt.blendType == BlendTreeType.Direct) continue;
                if (wd) { scan.On++; if (scan.OnExamples.Count < 5) scan.OnExamples.Add(label); }
                else { scan.Off++; if (scan.OffExamples.Count < 5) scan.OffExamples.Add(label); }
            }
            foreach (var child in sm.stateMachines)
                ScanWd(child.stateMachine, additive, prefix + "/" + (child.stateMachine != null ? child.stateMachine.name : "?"), scan, depth + 1);
        }

        static bool IsEmptyMotion(Motion m, int depth)
        {
            if (m == null) return true;
            if (m is AnimationClip clip) return clip.empty;
            if (m is BlendTree bt && depth < 32)
                foreach (var child in bt.children)
                    if (IsEmptyMotion(child.motion, depth + 1)) return true;
            return false;
        }

        // =====================================================================================================================
        // NDMF configuration
        // =====================================================================================================================

        static void CheckNdmfConfig(Ctx ctx)
        {
            var apply = NdmfApplyOnBuild();
            if (apply.HasValue && !apply.Value &&
                (ctx.Scan.ToolFamilies.ContainsKey("modular_avatar") || ctx.Scan.ToolFamilies.ContainsKey("ndmf") || ctx.Scan.ToolFamilies.ContainsKey("avatar_optimizer")))
                ctx.Issue(CctSeverity.Warning, "NDMF_APPLY_ON_BUILD_OFF",
                    "NDMF の Apply on Build が OFF です。このままアップロードすると MA / AAO の処理が行われず、MA のコンポーネントは SDK に黙って削除されます (Tools > NDM Framework で ON にしてください)");
        }

        // =====================================================================================================================
        // data.components
        // =====================================================================================================================

        static Dictionary<string, object> DescribeComponents(Ctx ctx)
        {
            var scan = ctx.Scan;
            var byType = scan.TypeCounts.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).ToList();
            var d = new Dictionary<string, object>
            {
                ["total"] = scan.Components.Count,
                ["types"] = byType.Count,
                ["by_type"] = byType.Take(80).Select(k => (object)new Dictionary<string, object> { ["type"] = k.Key, ["count"] = k.Value }).ToList(),
                ["non_whitelisted"] = ctx.NonWhitelisted,
                ["editor_only_objects"] = scan.EditorOnly.Count,
            };
            if (byType.Count > 80) d["by_type_omitted"] = byType.Count - 80;
            var ma = new List<object>();
            foreach (var g in scan.Components.Where(c => Info(c.GetType()).Family == "modular_avatar").GroupBy(c => c.GetType().Name).OrderByDescending(g => g.Count()))
                ma.Add(new Dictionary<string, object>
                {
                    ["type"] = g.Key,
                    ["count"] = g.Count(),
                    ["examples"] = g.Take(5).Select(c => (object)ctx.PathOf(c)).ToList(),
                });
            if (ma.Count > 0) d["modular_avatar"] = ma;
            return d;
        }
    }
}
