using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    // post_build=true: process a temporary clone of the avatar with NDMF and audit the result (research/ma-ndmf-aao.md §1.10).
    //
    // Recipe (all by reflection, no compile-time NDMF dependency), the same shape as NDMF's own "Test build":
    //   1. clone = Object.Instantiate(avatar) (like the SDK builder), moved into a private preview scene
    //      (EditorSceneManager.NewPreviewScene) so it never appears in or is saved with the user's scenes;
    //   2. nadena.dev.ndmf.ui.ErrorReportWindow.DISABLE_WINDOW = true (no NDMF Console window pops up);
    //   3. using (new OverrideTemporaryDirectoryScope(null)) AvatarProcessor.ProcessAvatar(clone, AmbientPlatform.DefaultPlatform)
    //      -> null asset root = NullAssetSaver: NDMF saves no generated assets to the AssetDatabase (ManualProcessAvatar is NOT
    //      used: it writes Assets/ZZZ_GeneratedAssets and deletes same-named folders);
    //   4. audit the processed clone with the SDK calculator (fallback: estimator);
    //   5. finally: destroy the in-memory objects generated for the clone (non-persistent meshes / materials / textures / animator
    //      objects / ScriptableObjects reachable from the processed clone but not from the same clone before processing, skipping
    //      DontUnloadUnusedAsset caches),
    //      destroy the clone, close the preview scene, restore DISABLE_WINDOW, and delete any asset that appeared under NDMF's
    //      temporary folders during the call (safety net; expected to be none). CleanTemporaryAssets() is never called (it would
    //      delete other avatars' generated assets).
    // Only NDMF plugins run (MA, AAO, ...). VRCFury, IPreprocessCallbackBehaviour and the SDK's own build hooks do not (the SDK
    // path VRCBuildPipelineCallbacks.OnPreprocessAvatar can open modal dialogs and is not used). NDMF logs every pass to the
    // Console. Not available in Play mode.
    public static partial class CctAvatarAudit
    {
        const string TNdmfProcessor = "nadena.dev.ndmf.AvatarProcessor";
        const string TNdmfPlatformProvider = "nadena.dev.ndmf.platform.INDMFPlatformProvider";
        const string TNdmfAmbientPlatform = "nadena.dev.ndmf.platform.AmbientPlatform";
        const string TNdmfOverrideTempDir = "nadena.dev.ndmf.OverrideTemporaryDirectoryScope";
        const string TNdmfErrorWindow = "nadena.dev.ndmf.ui.ErrorReportWindow";
        const string TNdmfConfig = "nadena.dev.ndmf.config.Config";
        const string TNdmfError = "nadena.dev.ndmf.IError";

        /// <summary>Folders NDMF writes generated assets to (ManualProcessAvatar / automatic builds).</summary>
        static readonly string[] NdmfTempRoots = { "Assets/ZZZ_GeneratedAssets", "Packages/nadena.dev.ndmf/__Generated" };

        // nadena.dev.ndmf.ErrorSeverity: Information=0, NonFatal=1, Error=2, InternalError=3 (NDMF 1.15 source).
        static readonly string[] NdmfSeverityNames = { "Information", "NonFatal", "Error", "InternalError" };

        /// <summary>nadena.dev.ndmf.config.Config.ApplyOnBuild, or null when NDMF is not installed.</summary>
        static bool? NdmfApplyOnBuild()
        {
            try
            {
                var t = CctSerialized.FindType(TNdmfConfig);
                var v = t?.GetProperty("ApplyOnBuild", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
                return v is bool b ? b : (bool?)null;
            }
            catch (Exception) { return null; }
        }

        sealed class NdmfApi
        {
            public MethodInfo CanProcess, Process, ProcessLegacy;
            public object Platform;
            public ConstructorInfo OverrideCtor;
            public FieldInfo DisableWindow;
            public Type ErrorType;
            public string Version, Error;
        }

        static NdmfApi ResolveNdmf()
        {
            var api = new NdmfApi();
            const BindingFlags PS = BindingFlags.Public | BindingFlags.Static;
            try
            {
                var tProc = CctSerialized.FindType(TNdmfProcessor);
                if (tProc == null)
                {
                    api.Error = "NDMF (nadena.dev.ndmf) が導入されていません";
                    return api;
                }
                api.Version = PackageVersion("Packages/nadena.dev.ndmf");
                var tPlat = CctSerialized.FindType(TNdmfPlatformProvider);
                api.CanProcess = tProc.GetMethod("CanProcessObject", PS, null, new[] { typeof(GameObject) }, null);
                if (tPlat != null) api.Process = tProc.GetMethod("ProcessAvatar", PS, null, new[] { typeof(GameObject), tPlat }, null);
                api.ProcessLegacy = tProc.GetMethod("ProcessAvatar", PS, null, new[] { typeof(GameObject) }, null);
                api.Platform = CctSerialized.FindType(TNdmfAmbientPlatform)?.GetProperty("DefaultPlatform", PS)?.GetValue(null, null);
                api.OverrideCtor = CctSerialized.FindType(TNdmfOverrideTempDir)?.GetConstructor(new[] { typeof(string) });
                api.DisableWindow = CctSerialized.FindType(TNdmfErrorWindow)?.GetField("DISABLE_WINDOW", PS);
                api.ErrorType = CctSerialized.FindType(TNdmfError);
                if (api.Process != null && api.Platform == null) api.Process = null; // let NDMF pick its current platform
                if (api.OverrideCtor == null)
                    api.Error = "この NDMF には生成アセットを保存しない処理モード (OverrideTemporaryDirectoryScope) がありません。アセットを書き込まずに処理できないため実行しません";
                else if (api.Process == null && api.ProcessLegacy == null)
                    api.Error = "NDMF の AvatarProcessor.ProcessAvatar が見つかりません (API が変わった可能性)";
            }
            catch (Exception e)
            {
                api.Error = "NDMF の API を確認できませんでした: " + Unwrap(e).Message;
            }
            return api;
        }

        static Dictionary<string, object> RunPostBuild(Ctx ctx, StatsOutput pre)
        {
            var d = new Dictionary<string, object> { ["stage"] = "post_build_ndmf" };
            string why = null;
            if (ctx.PlayMode) why = "Play モード中は実行しません";
            else if (ctx.Descriptor == null) why = "VRCAvatarDescriptor が無いため NDMF で処理できません";
            else if (ctx.Root.CompareTag("EditorOnly")) why = "アバタールートが EditorOnly タグのため NDMF は処理しません (アップロードもできません)";
            NdmfApi api = null;
            if (why == null)
            {
                api = ResolveNdmf();
                why = api.Error;
            }
            if (why != null)
            {
                d["status"] = "unavailable";
                d["reason"] = why;
                ctx.Issue(CctSeverity.Info, "POST_BUILD_UNAVAILABLE", "post_build: " + why);
                return d;
            }
            d["ndmf_version"] = api.Version;
            var apply = NdmfApplyOnBuild();
            if (apply.HasValue) d["ndmf_apply_on_build"] = apply.Value;

            var total = Stopwatch.StartNew();
            var cleanup = new Dictionary<string, object>();
            var tempBefore = SnapshotTempAssets();
            Scene preview = default;
            GameObject clone = null;
            object prevDisable = null;
            bool disableSet = false;
            HashSet<int> originalDeps = null;
            var rootsBefore = SceneRootIds();
            try
            {
                preview = EditorSceneManager.NewPreviewScene();
                ctx.TempCloneCreated = true; // Instantiate puts the clone into the active scene until it is moved below
                clone = Object.Instantiate(ctx.Root);
                clone.name = ctx.Root.name + "(Clone)";
                SceneManager.MoveGameObjectToScene(clone, preview);

                // Baseline = what the unprocessed clone references. Instantiate shares every asset with the original, and the clone is
                // a parent-free root, so the walk stays inside the avatar instead of following the original's parent chain into
                // sibling hierarchies (avatars nested under a shared root / world scene).
                // Assigned only when complete: a partial baseline would make Cleanup treat shared objects as generated.
                var baseline = new HashSet<int>();
                foreach (var o in EditorUtility.CollectDependencies(new Object[] { clone }))
                    if (o != null) baseline.Add(o.GetInstanceID());
                originalDeps = baseline;
                if (api.CanProcess != null && !(api.CanProcess.Invoke(null, new object[] { clone }) is bool can && can))
                    throw new InvalidOperationException("NDMF がこのアバターを処理できないと判定しました (CanProcessObject = false)");

                if (api.DisableWindow != null)
                {
                    prevDisable = api.DisableWindow.GetValue(null);
                    api.DisableWindow.SetValue(null, true);
                    disableSet = true;
                }
                object buildContext = null;
                var ndmfClock = Stopwatch.StartNew();
                using ((IDisposable)api.OverrideCtor.Invoke(new object[] { null }))
                {
                    if (api.Process != null) buildContext = api.Process.Invoke(null, new object[] { clone, api.Platform });
                    else api.ProcessLegacy.Invoke(null, new object[] { clone });
                }
                ndmfClock.Stop();
                d["ndmf_ms"] = ndmfClock.ElapsedMilliseconds;
                d["ndmf"] = DescribeNdmfResult(ctx, api, buildContext);
                if (clone == null) throw new InvalidOperationException("NDMF の処理中にクローンが削除されました");

                AuditProcessedClone(ctx, pre, clone, d);
                d["status"] = "ok";
            }
            catch (Exception e)
            {
                var inner = Unwrap(e);
                d["status"] = "failed";
                d["error"] = $"{inner.GetType().Name}: {inner.Message}";
                ctx.Issue(CctSeverity.Warning, "POST_BUILD_FAILED",
                    $"NDMF でクローンを処理できませんでした ({inner.GetType().Name}: {inner.Message})。ビルド前の値だけを報告します。実際のアップロードでも同じエラーになる可能性があります");
            }
            finally
            {
                if (disableSet)
                {
                    try { api.DisableWindow.SetValue(null, prevDisable ?? false); }
                    catch (Exception) { /* static field restore failure is harmless */ }
                }
                Cleanup(ctx, clone, preview, originalDeps, tempBefore, cleanup);
                RemoveStrayRoots(ctx, rootsBefore, cleanup);
                d["cleanup"] = cleanup;
                total.Stop();
                d["elapsed_ms"] = total.ElapsedMilliseconds;
            }

            var notes = new List<object>
            {
                "NDMF のプラグイン (MA / AAO など) だけを実行した結果です。VRCFury や SDK 自身のビルド処理は含みません",
                "生成アセットは保存していません (NDMF の NullAssetSaver)。NDMF の各パスのログが Console に出ます",
            };
            if (ctx.Scan.ToolFamilies.ContainsKey("vrcfury"))
                ctx.Issue(CctSeverity.Info, "POST_BUILD_PARTIAL",
                    "VRCFury のコンポーネントがありますが、post_build は NDMF だけを実行します。VRCFury の変更はこの値に含まれていません");
            if (apply.HasValue && !apply.Value)
                notes.Add("NDMF の Apply on Build が OFF です。実際のアップロードでは NDMF が実行されないため、この post_build の値にはなりません");
            d["notes"] = notes;
            return d;
        }

        static Dictionary<string, object> DescribeNdmfResult(Ctx ctx, NdmfApi api, object buildContext)
        {
            var d = new Dictionary<string, object> { ["errors_available"] = buildContext != null };
            if (buildContext == null) return d;
            try
            {
                var t = buildContext.GetType();
                if (t.GetProperty("Successful")?.GetValue(buildContext, null) is bool ok) d["successful"] = ok;
                var report = t.GetProperty("ErrorReport")?.GetValue(buildContext, null);
                var errors = report?.GetType().GetProperty("Errors")?.GetValue(report, null) as IEnumerable;
                var rows = new List<object>();
                if (errors != null)
                {
                    var pSeverity = api.ErrorType?.GetProperty("Severity");
                    var mMessage = api.ErrorType?.GetMethod("ToMessage", Type.EmptyTypes);
                    foreach (var ec in errors)
                    {
                        if (ec == null) continue;
                        var et = ec.GetType();
                        var err = et.GetField("TheError")?.GetValue(ec);
                        var plugin = et.GetField("Plugin")?.GetValue(ec);
                        var pass = et.GetField("PassName")?.GetValue(ec) as string;
                        int severity = 1;
                        string message = null;
                        try { if (err != null && pSeverity != null) severity = Convert.ToInt32(pSeverity.GetValue(err, null)); }
                        catch (Exception) { /* keep NonFatal */ }
                        try { message = err != null && mMessage != null ? mMessage.Invoke(err, null) as string : err?.ToString(); }
                        catch (Exception) { message = err?.GetType().Name; }
                        string pluginName = null;
                        try { pluginName = plugin?.GetType().GetProperty("DisplayName")?.GetValue(plugin, null) as string; }
                        catch (Exception) { /* optional */ }
                        if (message != null && message.Length > 600) message = message.Substring(0, 600) + "...";
                        rows.Add(new Dictionary<string, object>
                        {
                            ["severity"] = EnumName(NdmfSeverityNames, severity),
                            ["plugin"] = pluginName,
                            ["pass"] = pass,
                            ["message"] = message,
                        });
                        var sev = severity >= 2 ? CctSeverity.Error : severity == 1 ? CctSeverity.Warning : CctSeverity.Info;
                        ctx.Issue(sev, "POST_BUILD_NDMF_ERROR",
                            $"[NDMF{(pluginName != null ? " " + pluginName : "")}{(pass != null ? " / " + pass : "")}] {message}" +
                            (severity >= 2 ? " (Error 以上はアップロードを止めます)" : ""), value: EnumName(NdmfSeverityNames, severity));
                    }
                }
                d["errors"] = rows;
            }
            catch (Exception e)
            {
                d["read_error"] = Unwrap(e).Message;
            }
            return d;
        }

        static void AuditProcessedClone(Ctx ctx, StatsOutput pre, GameObject clone, Dictionary<string, object> d)
        {
            var scratch = CctResult.Ok("post_build");
            var cctx = new Ctx(ctx.Args, scratch, clone, ctx.Platform, ctx.Clock);
            cctx.Scan = ScanHierarchy(clone.transform);
            cctx.Descriptor = FindDescriptorComponent(clone);
            cctx.RootAnimator = clone.GetComponent<Animator>();

            var sdk = RunSdkCalculator(clone, ctx.WantPc, ctx.WantMobile);
            Est est;
            try { est = Estimate(cctx, cctx.Scan); }
            catch (Exception e)
            {
                SectionFailed(ctx, "post_build estimate", e);
                est = new Est();
            }
            var post = BuildStatsOutput(cctx, est, sdk, false);

            d["method"] = post.Method;
            d["ranks"] = post.Ranks;
            var changes = new Dictionary<string, object>();
            int unchanged = 0;
            foreach (var def in Ranks.All)
            {
                pre.Values.TryGetValue(def.Key, out var a);
                post.Values.TryGetValue(def.Key, out var b);
                bool sameRanks = true;
                foreach (var p in ctx.Platforms())
                {
                    pre.Ratings[p.Key].TryGetValue(def.Key, out var ra);
                    post.Ratings[p.Key].TryGetValue(def.Key, out var rb);
                    if (ra != rb) sameRanks = false;
                }
                if (sameRanks && SameValue(def, a, b)) { unchanged++; continue; }
                var row = new Dictionary<string, object> { ["pre"] = DisplayValue(def, a), ["post"] = DisplayValue(def, b) };
                foreach (var p in ctx.Platforms())
                {
                    pre.Ratings[p.Key].TryGetValue(def.Key, out var ra);
                    post.Ratings[p.Key].TryGetValue(def.Key, out var rb);
                    row[p.Key] = ra == rb ? Ranks.Name(rb) : $"{Ranks.Name(ra)} -> {Ranks.Name(rb)}";
                }
                changes[def.Key] = row;
            }
            d["changes"] = changes;
            d["unchanged_stats"] = unchanged;
            d["missing_scripts_after"] = cctx.Scan.MissingScriptTotal;
            if (ctx.Scan.MissingScriptTotal > 0)
                d["missing_scripts_note"] = "NDMF はビルド時に Missing Script を黙って削除します。ビルド後が 0 でも、ビルド前の Missing Script の依存がそろったことにはなりません";
            d["components_after"] = cctx.Scan.Components.Count;
            d["transforms_after"] = cctx.Scan.Transforms.Count;
            if (cctx.Descriptor != null) d["expressions_after"] = Section(ctx, "post_build expressions", () => DescribeExpressionsAfter(ctx, cctx));
            ctx.R.Put("post_build_stats", post.Stats);
            if (ctx.IncludeLists) ctx.R.Put("post_build_breakdown", Section(ctx, "post_build breakdown", () => est.Breakdown(ctx.TopN)));

            var parts = new List<string>();
            foreach (var p in ctx.Platforms())
                if (post.Overall.TryGetValue(p.Key, out var o)) parts.Add($"{ctx.PlatformLabel(p.Key)} {Ranks.DisplayName(o)}");
            ctx.PostBuildSummary = "NDMF 処理後: " + string.Join(" / ", parts);
        }

        /// <summary>Expression Parameters / Menu of the processed clone (MA installs menus and parameters at build time).</summary>
        static Dictionary<string, object> DescribeExpressionsAfter(Ctx ctx, Ctx cctx)
        {
            Object menu, parameters;
            bool custom;
            using (var so = new SerializedObject(cctx.Descriptor))
            {
                custom = CctSerialized.GetBool(so.FindProperty("customExpressions"));
                menu = CctSerialized.GetObject(so.FindProperty("expressionsMenu"));
                parameters = CctSerialized.GetObject(so.FindProperty("expressionParameters"));
            }
            var d = new Dictionary<string, object> { ["custom_expressions"] = custom };
            if (parameters != null)
            {
                var defs = new List<ParamDef>();
                ReadParamDefs(parameters, defs);
                int cost = SyncedCost(defs);
                d["total_bits"] = cost;
                d["limit"] = Ranks.MaxParameterCost;
                d["count"] = defs.Count;
                // The SDK builder checks only the pre-build asset (VRCSdkControlPanelAvatarBuilder, CalcTotalCost) and MA's own
                // 256-bit check is commented out (research/ma-ndmf-aao.md §2.5), so nothing stops this at upload; docs: 256 bits sync.
                if (cost > Ranks.MaxParameterCost)
                    ctx.Issue(CctSeverity.Error, "POST_BUILD_PARAM_BUDGET_EXCEEDED",
                        $"[NDMF 処理後] 同期パラメータが {cost} bit になり、VRChat が同期できる {Ranks.MaxParameterCost} bit (docs) を超えています。MA などがビルド時に追加した分を含みます。" +
                        "SDK のアップロード前検査はビルド前のアセットしか見ないため止まりません (超過時の VRChat 側の挙動は未確認)。同期不要なパラメータの Synced を外してください",
                        value: cost, limit: Ranks.MaxParameterCost);
                if (ctx.ParamDefs != null)
                {
                    var before = new Dictionary<string, ParamDef>(StringComparer.Ordinal);
                    foreach (var p in ctx.ParamDefs) if (!string.IsNullOrEmpty(p.Name) && !before.ContainsKey(p.Name)) before[p.Name] = p;
                    var changed = new List<object>();
                    foreach (var p in defs)
                        if (!string.IsNullOrEmpty(p.Name) && before.TryGetValue(p.Name, out var b) && b.Type != p.Type)
                            changed.Add($"{p.Name}: {EnumName(ParamTypeNames, b.Type)} -> {EnumName(ParamTypeNames, p.Type)}");
                    d["added"] = defs.Count(p => !string.IsNullOrEmpty(p.Name) && !before.ContainsKey(p.Name));
                    if (changed.Count > 0)
                    {
                        d["type_changed"] = changed.Take(30).ToList();
                        d["type_changed_note"] = "ビルド時の最適化 (MA / AAO など) で型が変わったパラメータです (例: Bool が Float にまとめられる)";
                    }
                }
            }
            if (menu != null)
            {
                int menus = 0, controls = 0, over = 0;
                var visited = new HashSet<Object>();
                var queue = new Queue<Object>();
                queue.Enqueue(menu);
                visited.Add(menu);
                while (queue.Count > 0 && menus < MaxMenusWalked)
                {
                    var m = queue.Dequeue();
                    menus++;
                    int n = 0;
                    using (var so = new SerializedObject(m))
                        foreach (var c in CctSerialized.Elements(so.FindProperty("controls")))
                        {
                            n++;
                            if (CctSerialized.GetInt(CctSerialized.Child(c, "type")) != ControlSubMenu) continue;
                            var sub = CctSerialized.GetObject(CctSerialized.Child(c, "subMenu"));
                            if (sub != null && visited.Add(sub)) queue.Enqueue(sub);
                        }
                    controls += n;
                    if (n > MaxMenuControls) over++;
                }
                d["menus"] = menus;
                d["controls_total"] = controls;
                d["menus_over_8_controls"] = over;
            }
            return d;
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // Cleanup
        // ---------------------------------------------------------------------------------------------------------------------

        static void Cleanup(Ctx ctx, GameObject clone, Scene preview, HashSet<int> originalDeps, Dictionary<string, TempRootState> tempBefore,
            Dictionary<string, object> report)
        {
            var problems = new List<string>();
            var generated = new List<Object>();
            int kept = 0;
            if (clone != null && originalDeps != null)
            {
                try
                {
                    foreach (var o in EditorUtility.CollectDependencies(new Object[] { clone }))
                    {
                        if (o == null || o is GameObject || o is Component) continue;
                        if (originalDeps.Contains(o.GetInstanceID()) || EditorUtility.IsPersistent(o)) continue;
                        bool perBuildType = o is Mesh || o is Material || o is Texture || o is Motion || o is RuntimeAnimatorController ||
                                            o is AvatarMask || o is AnimatorState || o is AnimatorStateMachine || o is AnimatorTransitionBase ||
                                            o is ScriptableObject;
                        if (!perBuildType || (o.hideFlags & HideFlags.DontUnloadUnusedAsset) != 0) { kept++; continue; }
                        generated.Add(o);
                    }
                }
                catch (Exception e) { problems.Add("生成物の列挙: " + Unwrap(e).Message); }
            }
            try { if (clone != null) Object.DestroyImmediate(clone); }
            catch (Exception e) { problems.Add("クローンの削除: " + Unwrap(e).Message); }
            report["clone_destroyed"] = clone == null;

            int destroyed = 0;
            foreach (var o in generated)
            {
                try
                {
                    if (o == null) continue; // destroyed together with its owner
                    Object.DestroyImmediate(o);
                    destroyed++;
                }
                catch (Exception) { kept++; }
            }
            report["generated_objects_destroyed"] = destroyed;
            if (kept > 0) report["generated_objects_kept"] = kept;

            try
            {
                if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
                report["preview_scene_closed"] = true;
            }
            catch (Exception e)
            {
                problems.Add("プレビューシーンを閉じる: " + Unwrap(e).Message);
                report["preview_scene_closed"] = false;
            }

            // Safety net: NullAssetSaver writes nothing, but a third-party NDMF plugin might. Delete only what appeared during this call.
            var deleted = new List<object>();
            try
            {
                var after = SnapshotTempAssets();
                foreach (var kv in after)
                {
                    if (!AssetDatabase.IsValidFolder(kv.Key)) continue; // not imported: nothing the AssetDatabase could delete
                    if (!tempBefore.TryGetValue(kv.Key, out var before))
                    {
                        // the whole folder was created by this call (it existed neither on disk nor in the AssetDatabase before)
                        if (AssetDatabase.DeleteAsset(kv.Key)) deleted.Add(kv.Key);
                        else problems.Add("削除できませんでした: " + kv.Key);
                        continue;
                    }
                    if (before.Guids == null || kv.Value.Guids == null)
                    {
                        problems.Add($"{kv.Key} の中身を確認できなかったため、何も削除していません");
                        continue;
                    }
                    foreach (var guid in kv.Value.Guids)
                    {
                        if (before.Guids.Contains(guid)) continue;
                        var path = AssetDatabase.GUIDToAssetPath(guid);
                        // A file that was already on disk (just imported now, e.g. by a Refresh during processing) is not ours.
                        if (string.IsNullOrEmpty(path) || before.Entries.Contains(path) ||
                            deleted.Any(x => path.StartsWith((string)x + "/", StringComparison.Ordinal))) continue;
                        if (AssetDatabase.DeleteAsset(path)) deleted.Add(path);
                        else problems.Add("削除できませんでした: " + path);
                    }
                }
            }
            catch (Exception e) { problems.Add("一時アセットの確認: " + Unwrap(e).Message); }
            report["temporary_assets_deleted"] = deleted;
            if (deleted.Count > 0)
                ctx.Issue(CctSeverity.Warning, "POST_BUILD_CLEANUP_FAILED",
                    $"NDMF の処理中に一時アセットが作られたため削除しました ({deleted.Count} 件: {string.Join(", ", deleted.Take(3))})。保存なしモードでも書き込むプラグインがあります");
            if (problems.Count > 0)
            {
                report["problems"] = problems.Cast<object>().ToList();
                ctx.Issue(CctSeverity.Warning, "POST_BUILD_CLEANUP_FAILED", "post_build の後始末で問題がありました: " + string.Join("; ", problems.Take(3)));
            }
        }

        /// <summary>NDMF's own hidden per-scene helper (ApplyOnPlayGlobalActivator); it may legitimately be created while processing.</summary>
        const string NdmfActivatorName = "nadena.dev.ndmf__Activator";

        static HashSet<int> SceneRootIds()
        {
            var ids = new HashSet<int>();
            foreach (var s in CctTargets.LoadedScenes())
                foreach (var g in s.GetRootGameObjects())
                    ids.Add(g.GetInstanceID());
            return ids;
        }

        /// <summary>
        /// Root GameObjects that appeared in the user's loaded scenes during this call can only come from the processing (a plugin
        /// creating helpers outside the avatar). They would otherwise be saved with the scene, so they are destroyed and reported.
        /// </summary>
        static void RemoveStrayRoots(Ctx ctx, HashSet<int> before, Dictionary<string, object> report)
        {
            var removed = new List<object>();
            try
            {
                foreach (var s in CctTargets.LoadedScenes().ToList())
                    foreach (var g in s.GetRootGameObjects())
                    {
                        if (g == null || before.Contains(g.GetInstanceID()) || g.name == NdmfActivatorName) continue;
                        removed.Add($"{s.name}:/{g.name}");
                        Object.DestroyImmediate(g);
                    }
            }
            catch (Exception e)
            {
                ctx.Issue(CctSeverity.Warning, "POST_BUILD_CLEANUP_FAILED", "シーンに残ったオブジェクトを確認できませんでした: " + Unwrap(e).Message);
            }
            report["stray_scene_objects_removed"] = removed;
            if (removed.Count > 0)
                ctx.Issue(CctSeverity.Info, "POST_BUILD_STRAY_OBJECTS",
                    $"NDMF の処理中にシーンへ作られたオブジェクト {removed.Count} 個を削除しました (保存されないように): {string.Join(", ", removed.Take(5))}");
        }

        /// <summary>
        /// What one NDMF temporary root held. Guids == null: the folder exists but could not be listed, so nothing in it may be
        /// deleted. Entries: asset paths of every file / folder on disk (also ones the AssetDatabase has not imported yet, e.g.
        /// with Auto Refresh off), so an import during the call never makes an older file look new.
        /// </summary>
        sealed class TempRootState
        {
            public HashSet<string> Guids;
            public readonly HashSet<string> Entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Root -> state, for the NDMF temporary roots that exist on disk or in the AssetDatabase (absent = no key).</summary>
        static Dictionary<string, TempRootState> SnapshotTempAssets()
        {
            var d = new Dictionary<string, TempRootState>(StringComparer.Ordinal);
            foreach (var root in NdmfTempRoots)
            {
                var abs = CctPaths.AssetToAbsolute(root).TrimEnd('/');
                bool onDisk = false, valid = false;
                try { onDisk = Directory.Exists(abs); }
                catch (Exception) { onDisk = true; } // unknown: treat as existing, so nothing in it is deleted
                try { valid = AssetDatabase.IsValidFolder(root); }
                catch (Exception) { valid = false; }
                if (!onDisk && !valid) continue;
                var st = new TempRootState();
                d[root] = st;
                try
                {
                    if (Directory.Exists(abs))
                        foreach (var e in Directory.GetFileSystemEntries(abs, "*", SearchOption.AllDirectories))
                            st.Entries.Add(root + CctPaths.Normalize(e).Substring(abs.Length));
                    st.Guids = valid
                        ? new HashSet<string>(AssetDatabase.FindAssets("", new[] { root }), StringComparer.Ordinal)
                        : new HashSet<string>(StringComparer.Ordinal);
                }
                catch (Exception) { st.Guids = null; /* not listable (e.g. immutable package): never delete in it */ }
            }
            return d;
        }
    }
}
