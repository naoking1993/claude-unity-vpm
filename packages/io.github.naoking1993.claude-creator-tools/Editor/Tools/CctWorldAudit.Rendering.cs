using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    public static partial class CctWorldAudit
    {
        // [PB] VRCSdkControlPanel.MAX_SDK_TEXTURE_SIZE = 8192 (VRCSdkControlPanelBuilder.cs L65); GetOversizeTextureImporters
        // (L1129-1166) compares TextureImporter.maxTextureSize of every texture of every material on scene Renderers.
        const int SdkMaxTextureSize = 8192;
        const string ErrorShaderName = "Hidden/InternalErrorShader";
        // More than one realtime shadow-casting light is reported (docs: "Do NOT overuse real-time lights",
        // docs/worlds/submitting-a-world-to-be-made-public.md L45; the threshold itself is this tool's estimate).
        const int RealtimeShadowLightThreshold = 1;
        // PostProcessLayer.Antialiasing.TemporalAntialiasing (com.unity.postprocessing 3.2.2: None, FXAA, SMAA, TAA).
        const int PpAntialiasingTaa = 3;
        static readonly string[] PpAntialiasingNames = { "None", "FastApproximateAntialiasing", "SubpixelMorphologicalAntialiasing", "TemporalAntialiasing" };
        // "be careful with chromatic aberration, screen-space reflection, and screen-space ambient occlusion"
        // (docs/worlds/submitting-a-world-to-be-made-public.md L41). Type names of the PPv2 effect settings.
        static readonly string[] PpHeavyEffects =
        {
            "UnityEngine.Rendering.PostProcessing.ChromaticAberration",
            "UnityEngine.Rendering.PostProcessing.ScreenSpaceReflections",
            "UnityEngine.Rendering.PostProcessing.AmbientOcclusion",
        };

        // ------------------------------------------------------------------ rendering

        static void AuditRendering(Ctx ctx)
        {
            var rendering = new Dictionary<string, object>();
            ctx.R.Put("rendering", rendering);

            var meshTriangles = new Dictionary<Mesh, long>();
            var meshUses = new Dictionary<Mesh, int>();
            int meshR = 0, skinnedR = 0, particleR = 0, otherR = 0, activeR = 0;
            int staticBase = 0, contributeGi = 0, batching = 0, occluder = 0, occludee = 0;
            var emptySlotPaths = new List<string>();
            var substancePaths = new List<string>();
            var rollingParticles = new List<string>();

            foreach (var item in ctx.Audited(K.Renderer))
            {
                var r = item.C as Renderer;
                if (r == null) continue;
                var info = new RendererInfo { Renderer = r, Item = item, Active = item.O.Active && r.enabled };
                if (info.Active) activeR++;
                switch (r)
                {
                    case SkinnedMeshRenderer _: skinnedR++; break;
                    case MeshRenderer _: meshR++; break;
                    case ParticleSystemRenderer _: particleR++; break;
                    default: otherR++; break;
                }

                info.Mesh = MeshOf(r);
                info.Triangles = TriangleCount(info.Mesh, meshTriangles);
                if (info.Mesh != null)
                {
                    meshUses.TryGetValue(info.Mesh, out var uses);
                    meshUses[info.Mesh] = uses + 1;
                }
                ctx.TotalTriangles += info.Triangles;
                if (info.Active) ctx.ActiveTriangles += info.Triangles;

                try { info.Flags = GameObjectUtility.GetStaticEditorFlags(r.gameObject); }
                catch (Exception) { info.Flags = 0; }
                if (r is MeshRenderer)
                {
                    staticBase++;
                    if ((info.Flags & StaticEditorFlags.ContributeGI) != 0) contributeGi++;
                    if ((info.Flags & StaticEditorFlags.BatchingStatic) != 0) batching++;
                    if ((info.Flags & StaticEditorFlags.OccluderStatic) != 0) occluder++;
                    if ((info.Flags & StaticEditorFlags.OccludeeStatic) != 0) occludee++;
                }

                info.Materials = r.sharedMaterials ?? new Material[0];
                bool emptySlot = false, substance = false;
                int subMeshes = info.Mesh != null && (r is MeshRenderer || r is SkinnedMeshRenderer) ? info.Mesh.subMeshCount : 0;
                for (int i = 0; i < info.Materials.Length; i++)
                {
                    var m = info.Materials[i];
                    if (m == null)
                    {
                        if (i < subMeshes) emptySlot = true;
                        continue;
                    }
                    if (!ctx.Materials.TryGetValue(m, out var mi))
                    {
                        mi = new MaterialInfo { Material = m, Shader = m.shader != null ? m.shader.name : null, AssetPath = AssetPathOf(m) };
                        ctx.Materials[m] = mi;
                    }
                    mi.Renderers++;
                    // [PB] GetSubstanceObjects: material asset path ends with ".sbsar".
                    if (mi.AssetPath != null && mi.AssetPath.EndsWith(".sbsar", StringComparison.OrdinalIgnoreCase)) substance = true;
                }
                if (subMeshes > 0 && info.Materials.Length == 0) emptySlot = true;
                if (emptySlot) emptySlotPaths.Add(item.O.Path);
                if (substance) substancePaths.Add(item.O.Path);

                // [WB] L821-866: ParticleSystemRenderer with renderMode Billboard and allowRoll.
                if (r is ParticleSystemRenderer psr)
                {
                    bool rolling;
                    try { rolling = psr.renderMode == ParticleSystemRenderMode.Billboard && psr.allowRoll; }
                    catch (Exception) { rolling = false; }
                    if (rolling) rollingParticles.Add(item.O.Path);
                }
                ctx.Renderers.Add(info);
            }

            if (emptySlotPaths.Count > 0)
                ctx.Issue(CctSeverity.Info, "MATERIAL_SLOT_EMPTY",
                    $"マテリアル枠が空の Renderer が {emptySlotPaths.Count} 個あります。空の枠のサブメッシュは描画されません (ピンク/透明になる原因)",
                    path: emptySlotPaths[0], value: Grouped(emptySlotPaths.Count, emptySlotPaths));
            if (substancePaths.Count > 0)
                ctx.Issue(CctSeverity.Warning, "SUBSTANCE_MATERIAL",
                    $"Substance (.sbsar) のマテリアルを使う Renderer が {substancePaths.Count} 個あります。VRChat は非対応なので通常のマテリアルにベイクしてください (SDK のメッセージと同じ。3.10.5 の SDK はこの検査を実際には表示しません)",
                    path: substancePaths[0], value: Grouped(substancePaths.Count, substancePaths));
            if (rollingParticles.Count > 0)
                ctx.Issue(CctSeverity.Info, "PARTICLE_BILLBOARD_ROLL",
                    $"Billboard 表示で Allow Roll が ON のパーティクルが {rollingParticles.Count} 個あります。VR で頭を傾けると一緒に回って没入感を損ねるため、SDK は OFF を推奨しています",
                    path: rollingParticles[0], value: Grouped(rollingParticles.Count, rollingParticles));

            // Materials: missing shaders.
            foreach (var mi in ctx.Materials.Values)
            {
                if (mi.Shader != null && mi.Shader != ErrorShaderName) continue;
                ctx.Issue(CctSeverity.Warning, "MISSING_SHADER",
                    $"マテリアル '{mi.Material.name}' のシェーダーが見つかりません (ピンク表示)。シェーダーのパッケージが入っているか、別のシェーダーに変更してください",
                    asset: mi.AssetPath, value: mi.Shader ?? "(null)");
            }

            AuditTextures(ctx);

            var shaders = ctx.Materials.Values
                .GroupBy(m => m.Shader ?? "(missing)")
                .Select(g => new { Name = g.Key, Materials = g.Count(), Renderers = g.Sum(x => x.Renderers) })
                .OrderByDescending(x => x.Renderers).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();

            rendering["renderers"] = new Dictionary<string, object>
            {
                ["total"] = meshR + skinnedR + particleR + otherR,
                ["active"] = activeR,
                ["mesh_renderers"] = meshR,
                ["skinned_mesh_renderers"] = skinnedR,
                ["particle_renderers"] = particleR,
                ["other"] = otherR,
            };
            rendering["triangles"] = new Dictionary<string, object>
            {
                ["total"] = ctx.TotalTriangles,
                ["active"] = ctx.ActiveTriangles,
                ["note"] = "メッシュのインデックス数からの概算 (非アクティブも total に含む。Terrain・パーティクル・UI は除外)",
            };
            rendering["materials"] = ctx.Materials.Count;
            rendering["shader_count"] = shaders.Count;
            rendering["shaders"] = shaders.Take(ctx.TopN).Select(s => (object)new Dictionary<string, object>
            {
                ["name"] = s.Name, ["materials"] = s.Materials, ["renderer_slots"] = s.Renderers,
            }).ToList();
            if (shaders.Count > ctx.TopN) rendering["shaders_omitted"] = shaders.Count - ctx.TopN;
            rendering["static_flags"] = new Dictionary<string, object>
            {
                ["mesh_renderers"] = staticBase,
                ["contribute_gi_pct"] = Percent(contributeGi, staticBase),
                ["batching_static_pct"] = Percent(batching, staticBase),
                ["occluder_static_pct"] = Percent(occluder, staticBase),
                ["occludee_static_pct"] = Percent(occludee, staticBase),
            };

            // Hotspots (report file): biggest renderers, meshes, textures, most used materials.
            var hotspots = new Dictionary<string, object>();
            ctx.R.Put("hotspots", hotspots);
            hotspots["top_renderers_by_triangles"] = ctx.Renderers.Where(i => i.Triangles > 0)
                .OrderByDescending(i => i.Triangles).Take(ctx.TopN)
                .Select(i => (object)new Dictionary<string, object>
                {
                    ["path"] = i.Item.O.Path,
                    ["type"] = i.Renderer.GetType().Name,
                    ["triangles"] = i.Triangles,
                    ["material_slots"] = i.Materials?.Length ?? 0,
                    ["active"] = i.Active,
                    ["static"] = (i.Flags & StaticEditorFlags.BatchingStatic) != 0,
                    ["mesh"] = i.Mesh != null ? (AssetPathOf(i.Mesh) ?? i.Mesh.name) : null,
                }).ToList();
            hotspots["top_meshes"] = meshTriangles.Where(kv => kv.Key != null && kv.Value > 0)
                .OrderByDescending(kv => kv.Value * Math.Max(1, meshUses.TryGetValue(kv.Key, out var u) ? u : 1)).Take(ctx.TopN)
                .Select(kv => (object)new Dictionary<string, object>
                {
                    ["name"] = kv.Key.name,
                    ["asset"] = AssetPathOf(kv.Key),
                    ["triangles"] = kv.Value,
                    ["renderers"] = meshUses.TryGetValue(kv.Key, out var n) ? n : 0,
                    ["readable"] = SafeReadable(kv.Key),
                }).ToList();
            hotspots["top_textures"] = ctx.Textures.Values.OrderByDescending(t => t.Bytes).ThenByDescending(t => (long)t.Width * t.Height).Take(ctx.TopN)
                .Select(t => (object)DescribeTexture(t)).ToList();
            hotspots["top_materials"] = ctx.Materials.Values.OrderByDescending(m => m.Renderers).Take(ctx.TopN)
                .Select(m => (object)new Dictionary<string, object>
                {
                    ["name"] = m.Material.name, ["asset"] = m.AssetPath, ["shader"] = m.Shader, ["renderer_slots"] = m.Renderers,
                }).ToList();
        }

        static Mesh MeshOf(Renderer r)
        {
            switch (r)
            {
                case SkinnedMeshRenderer smr: return smr.sharedMesh;
                case MeshRenderer _: return r.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null;
            }
            return null;
        }

        /// <summary>Triangles from index counts (never reads Mesh.triangles); quads count as two triangles.</summary>
        static long TriangleCount(Mesh m, Dictionary<Mesh, long> cache)
        {
            if (m == null) return 0;
            if (cache.TryGetValue(m, out var n)) return n;
            long t = 0;
            try
            {
                for (int s = 0; s < m.subMeshCount; s++)
                {
                    var topology = m.GetTopology(s);
                    long indices = m.GetIndexCount(s);
                    if (topology == MeshTopology.Triangles) t += indices / 3;
                    else if (topology == MeshTopology.Quads) t += indices / 4 * 2;
                }
            }
            catch (Exception) { t = 0; }
            cache[m] = t;
            return t;
        }

        static bool SafeReadable(Mesh m)
        {
            try { return m != null && m.isReadable; }
            catch (Exception) { return false; }
        }

        static void AuditTextures(Ctx ctx)
        {
            var importers = new Dictionary<string, TextureImporter>(StringComparer.Ordinal);
            foreach (var mi in ctx.Materials.Values)
            {
                int[] ids;
                try { ids = mi.Material.GetTexturePropertyNameIDs(); }
                catch (Exception) { continue; }
                var seenInMaterial = new HashSet<Texture>();
                foreach (var id in ids)
                {
                    Texture tex;
                    try { tex = mi.Material.GetTexture(id); }
                    catch (Exception) { continue; }
                    if (tex == null || !seenInMaterial.Add(tex)) continue;
                    if (!ctx.Textures.TryGetValue(tex, out var ti))
                    {
                        ti = new TextureInfo { Texture = tex, AssetPath = AssetPathOf(tex), Width = tex.width, Height = tex.height };
                        if (ti.AssetPath != null)
                        {
                            if (!importers.TryGetValue(ti.AssetPath, out var imp))
                            {
                                try { imp = AssetImporter.GetAtPath(ti.AssetPath) as TextureImporter; }
                                catch (Exception) { imp = null; }
                                importers[ti.AssetPath] = imp;
                            }
                            ti.Importer = imp;
                        }
                        ti.Bytes = TextureBytes(tex);
                        ctx.Textures[tex] = ti;
                    }
                    ti.Materials++;
                }
            }

            var boxFiltered = new List<string>();
            int over2048 = 0, over4096 = 0;
            long totalBytes = 0;
            foreach (var ti in ctx.Textures.Values)
            {
                if (ti.Bytes > 0) totalBytes += ti.Bytes;
                int size = Math.Max(ti.Width, ti.Height);
                if (size > 2048) over2048++;
                if (size > 4096) over4096++;
                if (ti.Importer == null) continue;
                // [PB] GetOversizeTextureImporters: importer.maxTextureSize > MAX_SDK_TEXTURE_SIZE.
                if (ti.Importer.maxTextureSize > SdkMaxTextureSize)
                    ctx.Issue(CctSeverity.Warning, "LARGE_TEXTURE",
                        $"テクスチャ '{ti.Texture.name}' の Max Size が {ti.Importer.maxTextureSize} です。SDK は {SdkMaxTextureSize} を超えるとメモリ節約のため縮小するよう警告します",
                        asset: ti.AssetPath, value: ti.Importer.maxTextureSize, limit: SdkMaxTextureSize);
                // [PB] GetBoxFilteredTextureImporters: mipmapEnabled && mipmapFilter == BoxFilter (#if UNITY_2021_1_OR_NEWER).
                if (ti.Importer.mipmapEnabled && ti.Importer.mipmapFilter == TextureImporterMipFilter.BoxFilter)
                    boxFiltered.Add(ti.AssetPath);
            }
            if (boxFiltered.Count > 0)
                ctx.Issue(CctSeverity.Info, "TEXTURE_MIP_BOX_FILTER",
                    $"ミップマップのフィルタが Box のテクスチャが {boxFiltered.Count} 枚あります。遠くがぼやけるため、SDK は Kaiser への変更を勧めています",
                    asset: boxFiltered[0], value: Grouped(boxFiltered.Count, boxFiltered));

            if (ctx.R.Data.TryGetValue("rendering", out var ro) && ro is Dictionary<string, object> rendering)
                rendering["textures"] = new Dictionary<string, object>
                {
                    ["count"] = ctx.Textures.Count,
                    ["memory_mb_estimate"] = Round(totalBytes / (1024.0 * 1024.0), 1),
                    ["larger_than_2048"] = over2048,
                    ["larger_than_4096"] = over4096,
                    ["note"] = "マテリアルが参照するテクスチャのみ (ライトマップ・UI・スクリプト参照は除く)。メモリは現在のビルドターゲットでのエディタ上の値",
                };
        }

        static Dictionary<string, object> DescribeTexture(TextureInfo t)
        {
            var d = new Dictionary<string, object>
            {
                ["name"] = t.Texture != null ? t.Texture.name : null,
                ["asset"] = t.AssetPath,
                ["width"] = t.Width,
                ["height"] = t.Height,
                ["memory_mb"] = t.Bytes >= 0 ? Round(t.Bytes / (1024.0 * 1024.0), 2) : (object)null,
                ["materials"] = t.Materials,
            };
            if (t.Texture is Texture2D t2) d["format"] = t2.format.ToString();
            if (t.Importer != null)
            {
                d["max_size"] = t.Importer.maxTextureSize;
                d["crunched"] = t.Importer.crunchedCompression;
                d["mipmaps"] = t.Importer.mipmapEnabled;
            }
            return d;
        }

        static MethodInfo _storageSizeMethod;
        static bool _storageSizeLooked;

        /// <summary>
        /// UnityEditor.TextureUtil.GetStorageMemorySizeLong (internal; the size the texture inspector shows), via reflection.
        /// Falls back to Profiler.GetRuntimeMemorySizeLong, then -1.
        /// </summary>
        static long TextureBytes(Texture t)
        {
            if (t == null) return -1;
            if (!_storageSizeLooked)
            {
                _storageSizeLooked = true;
                try
                {
                    var type = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.TextureUtil", false);
                    _storageSizeMethod = type?.GetMethod("GetStorageMemorySizeLong", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null, new[] { typeof(Texture) }, null);
                    if (_storageSizeMethod != null && _storageSizeMethod.ReturnType != typeof(long)) _storageSizeMethod = null;
                }
                catch (Exception) { _storageSizeMethod = null; }
            }
            if (_storageSizeMethod != null)
            {
                try { return (long)_storageSizeMethod.Invoke(null, new object[] { t }); }
                catch (Exception) { /* fall back */ }
            }
            try { return UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(t); }
            catch (Exception) { return -1; }
        }

        // ------------------------------------------------------------------ lighting

        static void AuditLighting(Ctx ctx)
        {
            var lighting = new Dictionary<string, object>();
            ctx.R.Put("lighting", lighting);
            lighting["scene"] = ctx.Active.IsValid() ? ctx.Active.path : null;

            // Lights.
            var byType = new Dictionary<string, object>();
            var byMode = new Dictionary<string, object>();
            int total = 0, active = 0, realtimeActive = 0, mixedActive = 0, bakedActive = 0;
            var realtimeShadow = new List<string>();
            var notBaked = new List<string>();
            var items = new List<object>();
            foreach (var item in ctx.Audited(K.Light))
            {
                var l = item.C as Light;
                if (l == null) continue;
                total++;
                bool on = item.O.Active && l.enabled;
                var type = l.type;
                bool area = type == LightType.Area || type == LightType.Disc;
                var mode = area ? LightmapBakeType.Baked : SafeBakeType(l);
                Inc(byType, type.ToString());
                Inc(byMode, mode.ToString());
                bool baked = false;
                try { baked = l.bakingOutput.isBaked; }
                catch (Exception) { /* not available */ }
                if (on)
                {
                    active++;
                    if (mode == LightmapBakeType.Realtime) realtimeActive++;
                    else if (mode == LightmapBakeType.Mixed) mixedActive++;
                    else bakedActive++;
                    if (mode != LightmapBakeType.Baked && l.shadows != LightShadows.None) realtimeShadow.Add(item.O.Path);
                    if (mode != LightmapBakeType.Realtime && !area && !baked) notBaked.Add(item.O.Path);
                }
                if (items.Count < ctx.TopN)
                    items.Add(new Dictionary<string, object>
                    {
                        ["path"] = item.O.Path,
                        ["type"] = type.ToString(),
                        ["mode"] = mode.ToString(),
                        ["shadows"] = l.shadows.ToString(),
                        ["active"] = on,
                        ["intensity"] = Round(l.intensity, 3),
                        ["range"] = type == LightType.Directional ? (object)null : Round(l.range, 2),
                        ["baked_output"] = baked,
                    });
            }
            lighting["lights"] = new Dictionary<string, object>
            {
                ["total"] = total,
                ["active"] = active,
                ["by_type"] = byType,
                ["by_mode"] = byMode,
                ["active_realtime"] = realtimeActive,
                ["active_mixed"] = mixedActive,
                ["active_baked"] = bakedActive,
                ["realtime_shadow_casting_active"] = realtimeShadow.Count,
                ["items"] = items,
            };
            if (realtimeShadow.Count > RealtimeShadowLightThreshold)
                ctx.Issue(CctSeverity.Warning, "REALTIME_SHADOW_LIGHTS",
                    $"リアルタイムの影を落とすライト (Realtime/Mixed で Shadow Type が None 以外) が {realtimeShadow.Count} 個有効です。docs はリアルタイムライトの多用を避けるよう強く勧めています。Baked にするか影を OFF にしてください",
                    path: realtimeShadow[0], value: Grouped(realtimeShadow.Count, realtimeShadow), limit: RealtimeShadowLightThreshold);

            // Lightmaps.
            LightmapData[] maps = null;
            try { maps = LightmapSettings.lightmaps; }
            catch (Exception) { /* ignored */ }
            int mapCount = maps?.Length ?? 0;
            int giReceivers = 0, lightmapped = 0;
            var notLightmapped = new List<string>();
            var noUv2 = new List<string>();
            var uv0Fallback = new List<string>();
            var noUv2Builtin = 0;
            var uvChecked = new Dictionary<Mesh, LightmapUv>();
            foreach (var ri in ctx.Renderers)
            {
                if (!(ri.Renderer is MeshRenderer mr) || (ri.Flags & StaticEditorFlags.ContributeGI) == 0) continue;
                ReceiveGI receive;
                try { receive = mr.receiveGI; }
                catch (Exception) { receive = ReceiveGI.Lightmaps; }
                if (receive != ReceiveGI.Lightmaps) continue;
                giReceivers++;
                int index = mr.lightmapIndex;
                if (index >= 0 && index < mapCount) lightmapped++;
                else notLightmapped.Add(ri.Item.O.Path);
                if (ri.Mesh != null)
                {
                    if (!uvChecked.TryGetValue(ri.Mesh, out var uv))
                        uvChecked[ri.Mesh] = uv = LightmapUvState(ri.Mesh);
                    if (uv == LightmapUv.Uv0Usable) uv0Fallback.Add(ri.Item.O.Path);
                    else if (uv == LightmapUv.Missing)
                    {
                        noUv2.Add(ri.Item.O.Path);
                        if (IsBuiltinAsset(ri.Mesh)) noUv2Builtin++;
                    }
                }
            }

            LightingSettings settings = null;
            try { Lightmapping.TryGetLightingSettings(out settings); }
            catch (Exception) { settings = null; }
            bool bakedGi = true;
            var lm = new Dictionary<string, object>
            {
                ["count"] = mapCount,
                ["lighting_data_asset"] = SafeLightingDataAsset(),
                ["contribute_gi_receivers"] = giReceivers,
                ["lightmapped"] = lightmapped,
                ["not_lightmapped"] = notLightmapped.Count,
                ["gi_workflow"] = Lightmapping.giWorkflowMode.ToString(),
                ["without_uv2"] = noUv2.Count,
                // No UV2, but UV0 is a clean 0..1 unwrap Unity falls back to (e.g. the built-in Quad): not reported.
                ["uv0_fallback_ok"] = Grouped(uv0Fallback.Count, uv0Fallback),
            };
            if (settings != null)
            {
                try
                {
                    bakedGi = settings.bakedGI;
                    lm["lighting_settings"] = AssetPathOf(settings);
                    lm["baked_gi"] = settings.bakedGI;
                    lm["realtime_gi"] = settings.realtimeGI;
                    lm["lightmapper"] = settings.lightmapper.ToString();
                    lm["mixed_lighting_mode"] = settings.mixedBakeMode.ToString();
                    lm["lightmap_resolution"] = Round(settings.lightmapResolution, 2);
                    lm["lightmap_max_size"] = settings.lightmapMaxSize;
                }
                catch (Exception) { /* settings asset partially unreadable */ }
            }
            else lm["lighting_settings"] = null;
            lighting["lightmaps"] = lm;

            if (giReceivers > 0 && mapCount == 0)
                ctx.Issue(CctSeverity.Warning, "LIGHTMAP_NOT_BAKED",
                    $"Contribute GI (Static) の MeshRenderer が {giReceivers} 個ありますが、ライトマップがありません。docs はライトのベイクが非常に重要としています (Window > Rendering > Lighting で Generate Lighting)" +
                    (bakedGi ? "" : "。Lighting 設定で Baked Global Illumination が OFF になっています"),
                    value: giReceivers);
            else if (mapCount > 0 && notLightmapped.Count > 0)
                ctx.Issue(CctSeverity.Info, "LIGHTMAP_OUTDATED",
                    $"ライトマップはありますが、Contribute GI の MeshRenderer {notLightmapped.Count} 個にライトマップが割り当てられていません。ベイク後に追加・変更したオブジェクトなら再ベイクしてください",
                    path: notLightmapped[0], value: Grouped(notLightmapped.Count, notLightmapped));
            if (noUv2.Count > 0)
                ctx.Issue(CctSeverity.Info, "LIGHTMAP_UV_MISSING",
                    $"ライトマップ UV (UV2) が無く Generate Lightmap UVs も OFF のメッシュを使う Contribute GI の Renderer が {noUv2.Count} 個あります。Unity は代わりに UV0 (1 番目の UV) を使いますが、UV0 が 0〜1 に収まった重なりの無い展開でなければ影がにじむ・ずれます (Read/Write が OFF のメッシュと大きいメッシュの UV0 は確かめていません)" +
                    (noUv2Builtin > 0 ? $"。うち {noUv2Builtin} 個は Unity 組み込みのメッシュで、Generate Lightmap UVs を設定できません" : ""),
                    path: noUv2[0], value: Grouped(noUv2.Count, noUv2));
            if (notBaked.Count > 0 && (mapCount > 0 || giReceivers > 0))
                ctx.Issue(CctSeverity.Warning, "BAKED_LIGHT_NOT_BAKED",
                    $"Baked / Mixed のライト {notBaked.Count} 個にベイク結果がありません。ベイクし直すまで期待どおりに照らされません (コミュニティ知見: 実行時にリアルタイム扱いになり重くなる)",
                    path: notBaked[0], value: Grouped(notBaked.Count, notBaked));

            // Light probes.
            int probeGroups = ctx.Audited(K.LightProbeGroup).Count();
            int bakedProbes = 0;
            try { bakedProbes = LightmapSettings.lightProbes != null ? LightmapSettings.lightProbes.count : 0; }
            catch (Exception) { /* ignored */ }
            lighting["light_probes"] = new Dictionary<string, object> { ["groups"] = probeGroups, ["baked_probes"] = bakedProbes };
            bool usesBakedLighting = mapCount > 0 || bakedActive + mixedActive > 0;
            if (usesBakedLighting && probeGroups == 0 && bakedProbes == 0)
                ctx.Issue(CctSeverity.Info, "NO_LIGHT_PROBES",
                    "ベイクしたライティングを使っていますが Light Probe がありません。プレイヤーやピックアップなど動くものにベイク光が当たらず暗く見えます (docs: Android ではライトプローブを多用、コミュニティ知見)");

            // Reflection probes.
            var probesByMode = new Dictionary<string, object>();
            var probeNotBaked = new List<string>();
            int probeTotal = 0;
            foreach (var item in ctx.Audited(K.ReflectionProbe))
            {
                var p = item.C as ReflectionProbe;
                if (p == null) continue;
                probeTotal++;
                Inc(probesByMode, p.mode.ToString());
                bool on = item.O.Active && p.enabled;
                if (p.mode == ReflectionProbeMode.Baked && p.bakedTexture == null) probeNotBaked.Add(item.O.Path);
                if (on && p.mode == ReflectionProbeMode.Realtime && p.refreshMode == ReflectionProbeRefreshMode.EveryFrame)
                    ctx.Issue(CctSeverity.Warning, "REFLECTION_PROBE_EVERY_FRAME",
                        "リアルタイムの Reflection Probe が毎フレーム更新 (Every Frame) になっています。周囲を 6 方向描き直すため非常に重いです。Baked にするか On Awake / Via Scripting にしてください",
                        path: item.O.Path);
            }
            lighting["reflection_probes"] = new Dictionary<string, object>
            {
                ["total"] = probeTotal, ["by_mode"] = probesByMode, ["baked_without_texture"] = probeNotBaked.Count,
            };
            if (probeNotBaked.Count > 0)
                ctx.Issue(CctSeverity.Info, "REFLECTION_PROBE_NOT_BAKED",
                    $"Baked の Reflection Probe {probeNotBaked.Count} 個にベイク結果がありません。反射が正しく出ません (コミュニティ知見)",
                    path: probeNotBaked[0], value: Grouped(probeNotBaked.Count, probeNotBaked));

            // Environment (RenderSettings belong to the active scene).
            var skybox = RenderSettings.skybox;
            lighting["environment"] = new Dictionary<string, object>
            {
                ["ambient_mode"] = RenderSettings.ambientMode.ToString(),
                ["ambient_intensity"] = Round(RenderSettings.ambientIntensity, 3),
                ["skybox"] = skybox != null ? new Dictionary<string, object>
                {
                    ["name"] = skybox.name, ["asset"] = AssetPathOf(skybox), ["shader"] = skybox.shader != null ? skybox.shader.name : null,
                } : null,
                ["sun"] = RenderSettings.sun != null ? CctTargets.GetPath(RenderSettings.sun.transform) : null,
                ["fog"] = RenderSettings.fog,
                ["fog_mode"] = RenderSettings.fog ? RenderSettings.fogMode.ToString() : null,
                ["fog_density"] = RenderSettings.fog ? Round(RenderSettings.fogDensity, 4) : (object)null,
            };

            // Occlusion culling.
            int umbra = 0;
            try { umbra = StaticOcclusionCulling.umbraDataSize; }
            catch (Exception) { /* ignored */ }
            int occluders = ctx.Renderers.Count(r => (r.Flags & StaticEditorFlags.OccluderStatic) != 0);
            int areas = ctx.Audited(K.OcclusionArea).Count();
            lighting["occlusion"] = new Dictionary<string, object>
            {
                ["baked"] = umbra > 0,
                ["umbra_data_bytes"] = umbra,
                ["occluder_static_renderers"] = occluders,
                ["occlusion_areas"] = areas,
            };
            if (umbra <= 0)
                ctx.Issue(ctx.CheckMobile ? CctSeverity.Warning : CctSeverity.Info, "OCCLUSION_NOT_BAKED",
                    "オクルージョンカリングがベイクされていません (Window > Rendering > Occlusion Culling > Bake)。見えない物も描画されます。docs は Android では「非常に重要」としています" +
                    (occluders == 0 ? "。Occluder Static の Renderer もありません" : ""),
                    value: new Dictionary<string, object> { ["occluder_static_renderers"] = occluders, ["occlusion_areas"] = areas });
        }

        static LightmapBakeType SafeBakeType(Light l)
        {
            try { return l.lightmapBakeType; }
            catch (Exception) { return LightmapBakeType.Realtime; }
        }

        static string SafeLightingDataAsset()
        {
            try
            {
                var a = Lightmapping.lightingDataAsset;
                return a != null ? AssetPathOf(a) ?? a.name : null;
            }
            catch (Exception) { return null; }
        }

        enum LightmapUv { Present, Missing, Uv0Usable }

        /// <summary>
        /// Lightmap UV of a mesh: UV2, or its ModelImporter generates one (Present); else Unity falls back to UV0, which is fine
        /// when UV0 is a clean 0..1 unwrap (Uv0Usable: e.g. the built-in Quad, one non-overlapping 0..1 island) and otherwise
        /// bleeds (Missing).
        /// </summary>
        static LightmapUv LightmapUvState(Mesh m)
        {
            try
            {
                if (m.HasVertexAttribute(VertexAttribute.TexCoord1)) return LightmapUv.Present;
                var path = AssetPathOf(m);
                if (path != null && AssetImporter.GetAtPath(path) is ModelImporter mi && mi.generateSecondaryUV) return LightmapUv.Present;
            }
            catch (Exception) { return LightmapUv.Present; }
            return Uv0IsLightmapUnwrap(m) ? LightmapUv.Uv0Usable : LightmapUv.Missing;
        }

        static bool IsBuiltinAsset(Object o) => AssetPathOf(o) == "Library/unity default resources";

        const int Uv0CheckGrid = 256;
        const int Uv0CheckMaxTriangles = 20000;
        const long Uv0CheckMaxCellVisits = 4000000;

        /// <summary>
        /// UV0 lies inside 0..1 and its triangles do not overlap: the triangles are rasterised on a 256 x 256 grid whose sample
        /// points sit off the grid lines and off the diagonal, so triangles that only share an edge never claim the same sample
        /// (up to 2 doubly covered samples are tolerated). Not tested (false): meshes without Read/Write (other than Unity's
        /// built-in ones; the read could log an error), over 20,000 triangles or over the visit budget.
        /// </summary>
        static bool Uv0IsLightmapUnwrap(Mesh m)
        {
            try
            {
                if (!m.HasVertexAttribute(VertexAttribute.TexCoord0) || !(m.isReadable || IsBuiltinAsset(m))) return false;
                var uv = new List<Vector2>();
                m.GetUVs(0, uv);
                if (uv.Count == 0) return false;
                const float eps = 1e-4f;
                foreach (var p in uv)
                    if (p.x < -eps || p.y < -eps || p.x > 1 + eps || p.y > 1 + eps) return false;
                long triangles = 0;
                for (int s = 0; s < m.subMeshCount; s++)
                    if (m.GetTopology(s) == MeshTopology.Triangles) triangles += m.GetIndexCount(s) / 3;
                if (triangles == 0 || triangles > Uv0CheckMaxTriangles) return false;

                const int g = Uv0CheckGrid;
                const float ox = 0.5137f, oy = 0.5291f; // sample point inside each cell
                var covered = new bool[g * g];
                var idx = new List<int>();
                long visits = 0;
                int doubled = 0;
                for (int s = 0; s < m.subMeshCount; s++)
                {
                    if (m.GetTopology(s) != MeshTopology.Triangles) continue;
                    m.GetTriangles(idx, s);
                    for (int t = 0; t + 2 < idx.Count; t += 3)
                    {
                        Vector2 a = uv[idx[t]] * g, b = uv[idx[t + 1]] * g, c = uv[idx[t + 2]] * g;
                        float area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
                        if (Mathf.Abs(area) < 1e-9f) continue;
                        int x0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - ox), 0, g - 1);
                        int x1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - ox), 0, g - 1);
                        int y0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - oy), 0, g - 1);
                        int y1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y)) - oy), 0, g - 1);
                        visits += (long)(x1 - x0 + 1) * (y1 - y0 + 1);
                        if (visits > Uv0CheckMaxCellVisits) return false;
                        for (int y = y0; y <= y1; y++)
                            for (int x = x0; x <= x1; x++)
                            {
                                var p = new Vector2(x + ox, y + oy);
                                float w0 = Edge(b, c, p), w1 = Edge(c, a, p), w2 = Edge(a, b, p);
                                bool inside = area > 0 ? w0 >= 0 && w1 >= 0 && w2 >= 0 : w0 <= 0 && w1 <= 0 && w2 <= 0;
                                if (!inside) continue;
                                int i = y * g + x;
                                if (covered[i] && ++doubled > 2) return false;
                                covered[i] = true;
                            }
                    }
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        static float Edge(Vector2 a, Vector2 b, Vector2 p) => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

        static void Inc(Dictionary<string, object> d, string key)
        {
            d.TryGetValue(key, out var v);
            d[key] = (v is int i ? i : 0) + 1;
        }

        // ------------------------------------------------------------------ cameras / post processing

        static void AuditCamerasAndPostProcessing(Ctx ctx)
        {
            if (!(ctx.R.Data.TryGetValue("rendering", out var ro) && ro is Dictionary<string, object> rendering))
                ctx.R.Put("rendering", rendering = new Dictionary<string, object>());

            var cams = new List<object>();
            int total = 0, active = 0, toTexture = 0;
            var rtActive = new List<string>();
            var renderTextures = new Dictionary<RenderTexture, int>();
            foreach (var item in ctx.Audited(K.Camera))
            {
                var cam = item.C as Camera;
                if (cam == null) continue;
                total++;
                bool isReference = ctx.ReferenceCamera != null && cam.gameObject == ctx.ReferenceCamera;
                // Cameras below the Reference Camera are switched off with it (REFERENCE_CAMERA_OBJECT_DISABLED).
                bool underReference = !isReference && ctx.ReferenceCamera != null && !EditorUtility.IsPersistent(ctx.ReferenceCamera) &&
                                      cam.transform.IsChildOf(ctx.ReferenceCamera.transform);
                bool on = item.O.Active && cam.enabled && !underReference;
                var rt = cam.targetTexture;
                if (on) active++;
                if (rt != null)
                {
                    toTexture++;
                    renderTextures.TryGetValue(rt, out var n);
                    renderTextures[rt] = n + 1;
                    if (on && !isReference) rtActive.Add(item.O.Path);
                }
                if (cams.Count < ctx.TopN)
                    cams.Add(new Dictionary<string, object>
                    {
                        ["path"] = item.O.Path,
                        ["active"] = on,
                        ["is_reference_camera"] = isReference,
                        ["under_reference_camera"] = underReference,
                        ["tag"] = SafeTag(cam.gameObject),
                        ["target_texture"] = rt != null ? (AssetPathOf(rt) ?? rt.name) : null,
                        ["depth"] = Round(cam.depth, 2),
                        ["target_display"] = cam.targetDisplay,
                        ["clear_flags"] = cam.clearFlags.ToString(),
                    });
                // The Reference Camera is disabled by VRChat at runtime; any other active camera without a target texture renders
                // the whole world a second time on top of the player's view (community knowledge / estimate).
                if (on && !isReference && rt == null)
                    ctx.Issue(CctSeverity.Warning, "EXTRA_ACTIVE_CAMERA",
                        "Reference Camera 以外に、画面へ描画する有効なカメラがあります。VRChat 内でワールドをもう一度描画して重くなり、表示が重なることがあります。不要なら無効にするか Target Texture を設定してください" +
                        (cam.depth > 0 && cam.targetDisplay == 0 ? " (Depth > 0 の Display 1 カメラはアップロード画面も覆うことがあります: コミュニティ知見)" : "") +
                        (ctx.ReferenceCamera == null ? " (Reference Camera が未設定です)" : ""),
                        path: item.O.Path);
            }
            if (rtActive.Count > 0)
                ctx.Issue(CctSeverity.Info, "CAMERA_RENDER_TEXTURE_ACTIVE",
                    $"RenderTexture に毎フレーム描画している有効なカメラが {rtActive.Count} 個あります。描画コストが増えるため、必要なときだけ有効にしてください (コミュニティ知見)",
                    path: rtActive[0], value: Grouped(rtActive.Count, rtActive));
            rendering["cameras"] = new Dictionary<string, object>
            {
                ["total"] = total,
                ["active"] = active,
                ["rendering_to_texture"] = toTexture,
                ["reference_camera"] = ctx.ReferenceCamera != null && !EditorUtility.IsPersistent(ctx.ReferenceCamera) ? CctTargets.GetPath(ctx.ReferenceCamera) : null,
                ["items"] = cams,
            };
            rendering["render_textures"] = renderTextures.Take(ctx.TopN).Select(kv => (object)new Dictionary<string, object>
            {
                ["name"] = kv.Key.name, ["asset"] = AssetPathOf(kv.Key), ["width"] = kv.Key.width, ["height"] = kv.Key.height, ["cameras"] = kv.Value,
            }).ToList();

            var mainCameraWithVolume = AuditPostProcessing(ctx, rendering);
            AuditReferenceCameraObject(ctx, rendering, mainCameraWithVolume);
        }

        /// <summary>
        /// The Reference Camera's GameObject is switched off when the world starts: ClientSim copies its settings and then calls
        /// refCamera.gameObject.SetActive(false) (Integrations/ClientSim/Runtime/System/ClientSimSceneManager.cs L150-188), and
        /// the SDK's own text says "This Camera is disabled at runtime" ([WB] L211-221). Everything else on that GameObject and
        /// under it (UdonBehaviours, renderers, audio, volumes, child objects) therefore never runs or renders in VRChat.
        /// Camera / PostProcessLayer / FlareLayer / AudioListener / Skybox on the object itself are expected there.
        /// </summary>
        static void AuditReferenceCameraObject(Ctx ctx, Dictionary<string, object> rendering, GameObject mainCameraWithVolume)
        {
            var go = ctx.ReferenceCamera;
            // ClientSim copies/deactivates only when the object has a Camera (else REFERENCE_CAMERA_NO_CAMERA is reported).
            if (go == null || EditorUtility.IsPersistent(go) || !go.TryGetComponent<Camera>(out _)) return;
            var objects = new List<string>();
            var types = new HashSet<string>(StringComparer.Ordinal);
            int components = 0;
            bool descriptorInside = false;
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform) continue;
                var o = ObjOf(ctx, c);
                if (o != null && o.EditorOnly) continue; // stripped from the build anyway
                if (c.gameObject == go && (c is Camera || c is FlareLayer || c is AudioListener || c is Skybox ||
                                           CctSerialized.IsType(c, TPpLayer) || CctSerialized.IsType(c, TPpDebug))) continue;
                var kinds = Classify(c.GetType()).Kinds;
                if (mainCameraWithVolume == go && (kinds & K.PpVolume) != 0) continue; // reported as PP_VOLUME_ON_MAIN_CAMERA
                if ((kinds & K.SceneDescriptor) != 0) descriptorInside = true;
                components++;
                types.Add(c.GetType().Name);
                var path = CctTargets.GetPath(c.transform);
                if (objects.Count == 0 || objects[objects.Count - 1] != path) objects.Add(path);
            }
            if (rendering.TryGetValue("cameras", out var co) && co is Dictionary<string, object> cams)
                cams["reference_camera_extra_components"] = components;
            if (components == 0) return;
            ctx.Issue(CctSeverity.Warning, "REFERENCE_CAMERA_OBJECT_DISABLED",
                $"Reference Camera の GameObject とその子に、カメラ以外のコンポーネントが {components} 個あります ({string.Join(", ", types.Take(8))})。" +
                "このオブジェクトはワールド開始時に無効化されるため (SDK の警告文と ClientSim の実装)、これらは VRChat では動作・表示しません。別の GameObject に移してください" +
                (descriptorInside ? "。VRC Scene Descriptor まで含まれています" : ""),
                path: objects[0], value: new Dictionary<string, object>
                {
                    ["components"] = components, ["types"] = types.Cast<object>().ToList(), ["objects"] = Examples(objects),
                });
        }

        static string SafeTag(GameObject go)
        {
            try { return go.tag; }
            catch (Exception) { return null; }
        }

        /// <summary>Returns Camera.main's GameObject when it holds a PostProcessVolume (PP_VOLUME_ON_MAIN_CAMERA), else null.</summary>
        static GameObject AuditPostProcessing(Ctx ctx, Dictionary<string, object> rendering)
        {
            var volumeType = CctSerialized.FindType(TPpVolume);
            var pp = new Dictionary<string, object> { ["package_present"] = CctSerialized.FindType(TPpLayer) != null };
            rendering["post_processing"] = pp;
            if (volumeType == null) return null;

            // Reference Camera's PostProcessLayer: TAA ([PB] ReferenceCameraHasTAAEnabled) and volume layer.
            Component refLayer = null;
            if (ctx.ReferenceCamera != null)
                refLayer = ComponentsOfKind(ctx.ReferenceCamera, K.PpLayer).FirstOrDefault();
            int volumeLayerMask = -1;
            if (refLayer != null)
            {
                using (var so = TrySerialize(ctx, refLayer, "PostProcessLayer"))
                {
                    if (so != null)
                    {
                        int aa = CctSerialized.GetInt(so.FindProperty("antialiasingMode"));
                        volumeLayerMask = CctSerialized.GetInt(so.FindProperty("volumeLayer"), -1);
                        pp["reference_camera_layer"] = new Dictionary<string, object>
                        {
                            ["antialiasing"] = EnumName(PpAntialiasingNames, aa),
                            ["volume_layer"] = MaskToNames(volumeLayerMask),
                            ["volume_trigger"] = (CctSerialized.GetObject(so.FindProperty("volumeTrigger")) as Transform) != null
                                ? CctTargets.GetPath((Transform)CctSerialized.GetObject(so.FindProperty("volumeTrigger"))) : null,
                        };
                        if (aa == PpAntialiasingTaa)
                            ctx.Issue(CctSeverity.Warning, "REFERENCE_CAMERA_TAA",
                                "Reference Camera の Post-process Layer が Temporal Anti-Aliasing (TAA) です。VRChat は非対応なので別のアンチエイリアス (SMAA/FXAA/なし) にしてください (SDK の警告と同じ)",
                                path: CctTargets.GetPath(ctx.ReferenceCamera));
                    }
                }
            }
            else pp["reference_camera_layer"] = null;

            int layers = ctx.Audited(K.PpLayer).Count();
            var volumes = new List<object>();
            int volumeCount = 0, globalCount = 0;
            var heavy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var profilesSeen = new Dictionary<Object, List<string>>();
            var outsideLayer = new List<string>();
            foreach (var item in ctx.Audited(K.PpVolume))
            {
                volumeCount++;
                using (var so = TrySerialize(ctx, item.C, "PostProcessVolume"))
                {
                    if (so == null) continue;
                    bool global = CctSerialized.GetBool(so.FindProperty("isGlobal"));
                    if (global) globalCount++;
                    var profile = CctSerialized.GetObject(so.FindProperty("sharedProfile"));
                    var effects = profile != null ? ProfileEffects(profile, profilesSeen) : new List<string>();
                    bool on = item.O.Active && Enabled(item.C);
                    bool inLayer = volumeLayerMask == -1 || (volumeLayerMask & (1 << item.C.gameObject.layer)) != 0;
                    // PPv2 picks volumes by PostProcessLayer.volumeLayer (layer mask); others never affect the camera.
                    if (on && refLayer != null && volumeLayerMask != 0 && !inLayer) outsideLayer.Add(item.O.Path);
                    if (on)
                        foreach (var e in effects)
                            if (PpHeavyEffects.Contains(e))
                            {
                                if (!heavy.TryGetValue(e, out var list)) heavy[e] = list = new List<string>();
                                list.Add(item.O.Path);
                            }
                    if (volumes.Count < ctx.TopN)
                        volumes.Add(new Dictionary<string, object>
                        {
                            ["path"] = item.O.Path,
                            ["active"] = on,
                            ["is_global"] = global,
                            ["layer"] = LayerName(item.C.gameObject.layer),
                            ["in_volume_layer"] = inLayer,
                            ["priority"] = Round(CctSerialized.GetFloat(so.FindProperty("priority")), 2),
                            ["weight"] = Round(CctSerialized.GetFloat(so.FindProperty("weight"), 1f), 2),
                            ["profile"] = profile != null ? (AssetPathOf(profile) ?? profile.name) : null,
                            ["effects"] = effects.Select(e => (object)e.Substring(e.LastIndexOf('.') + 1)).ToList(),
                        });
                }
            }
            pp["layers"] = layers;
            pp["volumes"] = volumeCount;
            pp["global_volumes"] = globalCount;
            pp["volume_items"] = volumes;

            foreach (var kv in heavy)
                ctx.Issue(CctSeverity.Info, "PP_HEAVY_EFFECT",
                    $"ポストプロセスの {kv.Key.Substring(kv.Key.LastIndexOf('.') + 1)} が有効です。docs は VR で問題を起こしやすい効果 (色収差・SSR・SSAO) に注意するよう案内しています",
                    path: kv.Value[0], value: Grouped(kv.Value.Count, kv.Value));

            if (volumeCount > 0 && refLayer == null)
                ctx.Issue(CctSeverity.Info, "PP_LAYER_MISSING",
                    ctx.ReferenceCamera == null
                        ? "Post-process Volume がありますが Reference Camera が未設定です。プレイヤーのカメラにポストプロセスが掛かりません (ClientSim も Reference Camera の Post-process Layer だけを複製します)"
                        : "Post-process Volume がありますが Reference Camera に Post-process Layer がありません。プレイヤーのカメラにポストプロセスが掛かりません (ClientSim も Reference Camera の Post-process Layer だけを複製します)",
                    path: ctx.ReferenceCamera != null ? CctTargets.GetPath(ctx.ReferenceCamera) : null);
            else if (refLayer != null && volumeLayerMask == 0)
                ctx.Issue(CctSeverity.Info, "PP_VOLUME_LAYER_EMPTY",
                    "Reference Camera の Post-process Layer の Volume Blending Layer が Nothing です。どの Volume も適用されません (Water など専用レイヤーを推奨: コミュニティ知見)",
                    path: CctTargets.GetPath(ctx.ReferenceCamera));
            else if (outsideLayer.Count > 0)
                ctx.Issue(CctSeverity.Info, "PP_VOLUME_NOT_IN_LAYER",
                    $"有効な Post-process Volume {outsideLayer.Count} 個のレイヤーが、Reference Camera の Post-process Layer の Volume Blending Layer ({string.Join(", ", (MaskToNames(volumeLayerMask) as List<object> ?? new List<object>()).Take(6))}) に含まれていません。これらの Volume は適用されません (Volume の GameObject のレイヤーを合わせてください)",
                    path: outsideLayer[0], value: Grouped(outsideLayer.Count, outsideLayer));

            // [WB] L211-221: Camera.main.GetComponentInChildren(PostProcessVolume). The SDK adds this warning before
            // ResetIssues(), so the panel normally does not show it (research 2.4), but the problem is real.
            try
            {
                var main = Camera.main;
                if (main != null && main.GetComponentInChildren(volumeType) != null)
                {
                    ctx.Issue(CctSeverity.Warning, "PP_VOLUME_ON_MAIN_CAMERA",
                        "Main Camera (MainCamera タグ) の配下に Post-process Volume があります。このカメラは実行時に無効化されるため、Volume は別の GameObject に移してください (SDK のコードにある警告。パネルには通常表示されません)",
                        path: CctTargets.GetPath(main.gameObject));
                    return main.gameObject;
                }
            }
            catch (Exception) { /* Camera.main unavailable */ }
            return null;
        }

        /// <summary>Full type names of the active+enabled effects in a PPv2 profile (settings[], each with active and enabled.value).</summary>
        static List<string> ProfileEffects(Object profile, Dictionary<Object, List<string>> cache)
        {
            if (cache.TryGetValue(profile, out var cached)) return cached;
            var result = new List<string>();
            cache[profile] = result;
            using (var so = TrySerializeAsset(profile))
            {
                if (so == null) return result;
                foreach (var el in CctSerialized.Elements(so.FindProperty("settings")))
                {
                    var setting = CctSerialized.GetObject(el);
                    if (setting == null) continue;
                    using (var sso = TrySerializeAsset(setting))
                    {
                        if (sso == null) continue;
                        bool active = CctSerialized.GetBool(sso.FindProperty("active"), true);
                        bool enabled = CctSerialized.GetBool(sso.FindProperty("enabled.value"));
                        if (active && enabled) result.Add(setting.GetType().FullName);
                    }
                }
            }
            return result;
        }
    }
}
