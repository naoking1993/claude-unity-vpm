using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Naoking.ClaudeCreatorTools
{
    public static partial class CctCaptureViews
    {
        // Pixel buffers follow Unity's Texture2D convention: row 0 is the BOTTOM row. Drawing helpers take
        // "yTop" coordinates (row 0 = top) and convert, so labels and layouts read naturally.

        const int SheetGap = 6;
        const int SheetMaxSide = 2048;
        const int CompareSeparator = 4;
        const int ChangedThreshold = 8;
        static readonly Color32 SheetBackground = new Color32(40, 40, 40, 255);
        static readonly Color32 SeparatorColor = new Color32(255, 0, 255, 255);

        // ---------------------------------------------------------------- PNG IO

        static byte[] EncodePng(Color32[] pixels, int width, int height, bool withAlpha)
        {
            var tex = new Texture2D(width, height, withAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, false, false)
            {
                name = "CCT PNG Encode",
                hideFlags = HideFlags.HideAndDontSave,
            };
            try
            {
                tex.SetPixels32(pixels);
                tex.Apply(false, false);
                return tex.EncodeToPNG();
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }

        static Color32[] DecodePng(byte[] bytes, out int width, out int height)
        {
            width = height = 0;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
            {
                name = "CCT PNG Decode",
                hideFlags = HideFlags.HideAndDontSave,
            };
            try
            {
                if (!tex.LoadImage(bytes, false)) return null;
                width = tex.width;
                height = tex.height;
                return tex.GetPixels32();
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }

        // ---------------------------------------------------------------- pixel operations

        /// <summary>Straight-alpha composite over an opaque colour (sRGB space, like an image viewer).</summary>
        static Color32[] Composite(Color32[] pixels, Color32 background)
        {
            var result = new Color32[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                if (c.a == 255)
                {
                    result[i] = c;
                    continue;
                }
                int a = c.a, ia = 255 - a;
                result[i] = new Color32(
                    (byte)((c.r * a + background.r * ia + 127) / 255),
                    (byte)((c.g * a + background.g * ia + 127) / 255),
                    (byte)((c.b * a + background.b * ia + 127) / 255), 255);
            }
            return result;
        }

        static bool HasTransparency(Color32[] pixels)
        {
            foreach (var c in pixels)
                if (c.a < 255) return true;
            return false;
        }

        /// <summary>Area-average downscale (both buffers bottom-up).</summary>
        static Color32[] Downscale(Color32[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color32[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                int y0 = (int)((long)y * sh / dh), y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * sh / dh));
                for (int x = 0; x < dw; x++)
                {
                    int x0 = (int)((long)x * sw / dw), x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * sw / dw));
                    int r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (int yy = y0; yy < y1 && yy < sh; yy++)
                    {
                        int row = yy * sw;
                        for (int xx = x0; xx < x1 && xx < sw; xx++)
                        {
                            var c = src[row + xx];
                            r += c.r;
                            g += c.g;
                            b += c.b;
                            a += c.a;
                            n++;
                        }
                    }
                    if (n == 0) n = 1;
                    dst[y * dw + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                }
            }
            return dst;
        }

        static void Fill(Color32[] buf, int bw, int bh, Color32 c)
        {
            for (int i = 0; i < buf.Length; i++) buf[i] = c;
        }

        static void FillRect(Color32[] buf, int bw, int bh, int x, int yTop, int w, int h, Color32 c, float opacity)
        {
            int ia = Mathf.Clamp(Mathf.RoundToInt(opacity * 255f), 0, 255), ib = 255 - ia;
            for (int j = 0; j < h; j++)
            {
                int row = bh - 1 - (yTop + j);
                if (row < 0 || row >= bh) continue;
                for (int i = 0; i < w; i++)
                {
                    int col = x + i;
                    if (col < 0 || col >= bw) continue;
                    int k = row * bw + col;
                    if (ia == 255) { buf[k] = c; continue; }
                    var d = buf[k];
                    buf[k] = new Color32((byte)((c.r * ia + d.r * ib) / 255), (byte)((c.g * ia + d.g * ib) / 255),
                        (byte)((c.b * ia + d.b * ib) / 255), 255);
                }
            }
        }

        /// <summary>Copies a bottom-up source image so that its top-left lands at (x, yTop) of the destination.</summary>
        static void Paste(Color32[] dst, int dw, int dh, Color32[] src, int sw, int sh, int x, int yTop)
        {
            for (int j = 0; j < sh; j++)
            {
                int srcRow = sh - 1 - j;
                int dstRow = dh - 1 - (yTop + j);
                if (dstRow < 0 || dstRow >= dh) continue;
                int start = Math.Max(0, -x), end = Math.Min(sw, dw - x);
                if (end <= start) continue;
                Array.Copy(src, srcRow * sw + start, dst, dstRow * dw + x + start, end - start);
            }
        }

        // ---------------------------------------------------------------- composed images

        static void ContactLayout(int count, int resolution, out int cols, out int rows, out int cell)
        {
            int n = Math.Max(1, count);
            cols = (int)Math.Ceiling(Math.Sqrt(n));
            rows = (n + cols - 1) / cols;
            int byWidth = (SheetMaxSide - SheetGap * (cols + 1)) / cols;
            int byHeight = (SheetMaxSide - SheetGap * (rows + 1)) / rows;
            cell = Math.Max(64, Math.Min(resolution, Math.Min(byWidth, byHeight)));
        }

        static Color32[] BuildContactSheet(List<Color32[]> cells, List<string> labels, int cell, int cols, int plannedRows,
            out int width, out int height)
        {
            int rows = Math.Max(1, Math.Min(plannedRows, (cells.Count + cols - 1) / cols));
            width = cols * cell + (cols + 1) * SheetGap;
            height = rows * cell + (rows + 1) * SheetGap;
            var buf = new Color32[width * height];
            Fill(buf, width, height, SheetBackground);
            int scale = Mathf.Clamp(cell / 250, 1, 4);
            for (int k = 0; k < cells.Count; k++)
            {
                int c = k % cols, rr = k / cols;
                int x = SheetGap + c * (cell + SheetGap);
                int yTop = SheetGap + rr * (cell + SheetGap);
                Paste(buf, width, height, cells[k], cell, cell, x, yTop);
                DrawLabel(buf, width, height, x + 2 * scale, yTop + 2 * scale, labels[k], scale);
            }
            return buf;
        }

        static Color32[] BuildSideBySide(Color32[] before, Color32[] after, int res, string viewName, out int width)
        {
            width = res * 2 + CompareSeparator;
            var buf = new Color32[width * res];
            Fill(buf, width, res, SeparatorColor);
            Paste(buf, width, res, before, res, res, 0, 0);
            Paste(buf, width, res, after, res, res, res + CompareSeparator, 0);
            int scale = Mathf.Clamp(res / 250, 1, 4);
            DrawLabel(buf, width, res, 2 * scale, 2 * scale, "before " + viewName, scale);
            DrawLabel(buf, width, res, res + CompareSeparator + 2 * scale, 2 * scale, "after " + viewName, scale);
            return buf;
        }

        /// <summary>Dimmed "after" image with pixels changed by more than the threshold painted red (stronger = larger change).</summary>
        static Color32[] BuildDiffImage(Color32[] before, Color32[] after, int res)
        {
            var buf = new Color32[res * res];
            for (int i = 0; i < buf.Length; i++)
            {
                var a = before[i];
                var b = after[i];
                int m = Math.Max(Math.Abs(a.r - b.r), Math.Max(Math.Abs(a.g - b.g), Math.Abs(a.b - b.b)));
                float g = (0.299f * b.r + 0.587f * b.g + 0.114f * b.b) * 0.3f;
                if (m > ChangedThreshold)
                {
                    float k = 0.35f + 0.65f * Mathf.Clamp01(m / 64f);
                    buf[i] = new Color32((byte)(g + (255f - g) * k), (byte)(g * (1f - k)), (byte)(g * (1f - k)), 255);
                }
                else
                {
                    var gb = (byte)g;
                    buf[i] = new Color32(gb, gb, gb, 255);
                }
            }
            int scale = Mathf.Clamp(res / 250, 1, 4);
            DrawLabel(buf, res, res, 2 * scale, 2 * scale, "red: changed > " + ChangedThreshold, scale);
            return buf;
        }

        // ---------------------------------------------------------------- comparison

        static Dictionary<string, object> CompareWithPrevious(string viewName, Color32[] current, int res, Options opt,
            PreviousCapture prev, Dictionary<string, object> prevView, string dir, List<string> files, CctResult r,
            List<string> missing)
        {
            var prevFile = prevView.TryGetValue("file", out var fv) ? fv as string : null;
            if (string.IsNullOrEmpty(prevFile))
            {
                missing.Add(viewName);
                return null;
            }
            string prevPath;
            Color32[] old;
            int w, h;
            try
            {
                prevPath = Path.IsPathRooted(prevFile) ? CctPaths.Normalize(prevFile) : CctPaths.Combine(prev.Dir, prevFile);
                if (!File.Exists(prevPath))
                {
                    missing.Add(viewName);
                    return null;
                }
                old = DecodePng(File.ReadAllBytes(prevPath), out w, out h);
            }
            catch (Exception)
            {
                missing.Add(viewName);
                return null;
            }
            if (old == null || w != res || h != res)
            {
                missing.Add(old == null ? viewName : $"{viewName} ({w}x{h})");
                return null;
            }
            if (HasTransparency(old)) old = Composite(old, opt.BackgroundColor);

            bool maskBackground = opt.Background != "skybox";
            var bg = opt.BackgroundColor;
            long n = (long)res * res, sumAll = 0, sumFg = 0, fgCount = 0, changed = 0;
            int maxDiff = 0, x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            for (int i = 0; i < old.Length; i++)
            {
                var a = old[i];
                var b = current[i];
                int dr = Math.Abs(a.r - b.r), dg = Math.Abs(a.g - b.g), db = Math.Abs(a.b - b.b);
                int s = dr + dg + db, m = Math.Max(dr, Math.Max(dg, db));
                sumAll += s;
                if (m > maxDiff) maxDiff = m;
                if (!maskBackground || !Near(a, bg) || !Near(b, bg))
                {
                    sumFg += s;
                    fgCount++;
                }
                if (m > ChangedThreshold)
                {
                    changed++;
                    int x = i % res, yTop = res - 1 - i / res;
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (yTop < y0) y0 = yTop;
                    if (yTop > y1) y1 = yTop;
                }
            }

            var result = new Dictionary<string, object>
            {
                ["view"] = viewName,
                ["previous_file"] = CctPaths.ToDisplayPath(prevPath),
                ["mean_abs_diff"] = Math.Round(sumAll / (3.0 * n), 3),
                ["mean_abs_diff_foreground"] = maskBackground ? Math.Round(fgCount > 0 ? sumFg / (3.0 * fgCount) : 0.0, 3) : (object)null,
                ["foreground_pct"] = maskBackground ? Math.Round(100.0 * fgCount / n, 3) : (object)null,
                ["changed_pct"] = Math.Round(100.0 * changed / n, 3),
                ["changed_pixels"] = changed,
                ["max_diff"] = maxDiff,
                ["changed_bbox"] = changed > 0 ? new List<object> { x0, y0, x1 + 1, y1 + 1 } : null,
                ["units"] = "8-bit sRGB channel levels 0-255 (not perceptual Delta E); changed = any channel differs by > 8; " +
                            "bbox = [x0, y0, x1, y1) from the top-left; foreground = pixels not equal to the background colour in either image",
            };

            var side = BuildSideBySide(old, current, res, viewName, out int sideWidth);
            var cmpPath = CctPaths.Combine(dir, "compare_" + viewName + ".png");
            if (TryWrite(cmpPath, EncodePng(side, sideWidth, res, false), r))
            {
                files.Add(cmpPath);
                result["compare_image"] = CctPaths.ToDisplayPath(cmpPath);
            }
            var diffPath = CctPaths.Combine(dir, "diff_" + viewName + ".png");
            if (TryWrite(diffPath, EncodePng(BuildDiffImage(old, current, res), res, res, false), r))
            {
                files.Add(diffPath);
                result["diff_image"] = CctPaths.ToDisplayPath(diffPath);
            }
            return result;
        }

        static bool Near(Color32 a, Color32 b) =>
            Math.Abs(a.r - b.r) <= 2 && Math.Abs(a.g - b.g) <= 2 && Math.Abs(a.b - b.b) <= 2;

        /// <summary>A previous capture loaded from its manifest.json (resolved from a folder, the manifest, a file in it, or "latest").</summary>
        sealed class PreviousCapture
        {
            public string Dir;
            public string ManifestPath;
            public string Id;
            public Dictionary<string, object> Params = new Dictionary<string, object>();
            public readonly Dictionary<string, Dictionary<string, object>> Views =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);

            public static PreviousCapture Load(string spec, out string error)
            {
                string manifestPath;
                try
                {
                    manifestPath = ResolveManifestPath(spec, out error);
                }
                catch (Exception e)
                {
                    error = $"compare_with '{spec}' を解釈できません: {e.Message}";
                    return null;
                }
                if (manifestPath == null) return null;

                object parsed;
                try
                {
                    parsed = CctJson.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
                }
                catch (Exception e)
                {
                    error = $"manifest.json を読めません ({CctPaths.ToDisplayPath(manifestPath)}): {e.Message}";
                    return null;
                }
                if (!(parsed is Dictionary<string, object> m) ||
                    !(m.TryGetValue("format", out var fmt) && fmt is string fs && fs.StartsWith("cct_capture_views/", StringComparison.Ordinal)))
                {
                    error = $"cct_capture_views の manifest ではありません: {CctPaths.ToDisplayPath(manifestPath)}";
                    return null;
                }

                var prev = new PreviousCapture
                {
                    ManifestPath = CctPaths.Normalize(manifestPath),
                    Dir = CctPaths.Normalize(Path.GetDirectoryName(manifestPath)),
                };
                prev.Id = m.TryGetValue("id", out var idv) && idv is string ids ? ids : Path.GetFileName(prev.Dir);
                if (m.TryGetValue("params", out var pv) && pv is Dictionary<string, object> pd) prev.Params = pd;
                if (m.TryGetValue("views", out var vv) && vv is List<object> vl)
                {
                    foreach (var item in vl)
                    {
                        if (item is Dictionary<string, object> vd && vd.TryGetValue("name", out var nv) && nv is string name)
                            prev.Views[name] = vd;
                    }
                }
                return prev;
            }

            static string ResolveManifestPath(string spec, out string error)
            {
                error = null;
                var s = (spec ?? "").Trim().Trim('"', '\'');
                if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    try { s = new Uri(s).LocalPath; }
                    catch (UriFormatException) { /* use as given */ }
                }
                if (string.Equals(s, "latest", StringComparison.OrdinalIgnoreCase))
                {
                    var root = CctPaths.CapturesDir;
                    var latest = Directory.Exists(root)
                        ? Directory.GetDirectories(root)
                            .Where(d => File.Exists(Path.Combine(d, ManifestFileName)))
                            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                            .FirstOrDefault()
                        : null;
                    if (latest == null)
                    {
                        error = "compare_with=latest ですが、比較できる過去のキャプチャがありません";
                        return null;
                    }
                    return CctPaths.Combine(latest, ManifestFileName);
                }

                s = CctPaths.Normalize(s);
                var candidates = new List<string>();
                if (Path.IsPathRooted(s)) candidates.Add(s);
                else
                {
                    candidates.Add(CctPaths.Combine(CctPaths.CapturesDir, s));
                    candidates.Add(CctPaths.Combine(CctPaths.ProjectRoot, s));
                }
                foreach (var c in candidates)
                {
                    var p = c.TrimEnd('/');
                    if (Directory.Exists(p))
                    {
                        var mp = CctPaths.Combine(p, ManifestFileName);
                        if (File.Exists(mp)) return mp;
                        error = $"フォルダ '{p}' に {ManifestFileName} がありません";
                        continue;
                    }
                    if (File.Exists(p))
                    {
                        if (string.Equals(Path.GetFileName(p), ManifestFileName, StringComparison.OrdinalIgnoreCase)) return p;
                        var mp = CctPaths.Combine(Path.GetDirectoryName(p), ManifestFileName);
                        if (File.Exists(mp)) return mp;
                    }
                }
                if (error == null)
                    error = $"compare_with '{spec}' のキャプチャが見つかりません (キャプチャフォルダか manifest.json のパス、Captures 下のフォルダ名、または latest を指定)";
                return null;
            }
        }

        // ---------------------------------------------------------------- tiny bitmap font for labels (5x7, lowercase)

        static readonly Dictionary<char, bool[]> Glyphs = BuildGlyphs();

        static Dictionary<char, bool[]> BuildGlyphs()
        {
            var src = new Dictionary<char, string>
            {
                ['a'] = ".....|.....|.###.|....#|.####|#...#|.####",
                ['b'] = "#....|#....|#.##.|##..#|#...#|#...#|####.",
                ['c'] = ".....|.....|.###.|#....|#....|#...#|.###.",
                ['d'] = "....#|....#|.##.#|#..##|#...#|#...#|.####",
                ['e'] = ".....|.....|.###.|#...#|#####|#....|.###.",
                ['f'] = "..##.|.#..#|.#...|###..|.#...|.#...|.#...",
                ['g'] = ".....|.####|#...#|#...#|.####|....#|.###.",
                ['h'] = "#....|#....|#.##.|##..#|#...#|#...#|#...#",
                ['i'] = "..#..|.....|.##..|..#..|..#..|..#..|.###.",
                ['j'] = "...#.|.....|..##.|...#.|...#.|#..#.|.##..",
                ['k'] = "#....|#....|#..#.|#.#..|##...|#.#..|#..#.",
                ['l'] = ".##..|..#..|..#..|..#..|..#..|..#..|.###.",
                ['m'] = ".....|.....|##.#.|#.#.#|#.#.#|#...#|#...#",
                ['n'] = ".....|.....|#.##.|##..#|#...#|#...#|#...#",
                ['o'] = ".....|.....|.###.|#...#|#...#|#...#|.###.",
                ['p'] = ".....|.....|####.|#...#|####.|#....|#....",
                ['q'] = ".....|.....|.##.#|#..##|.####|....#|....#",
                ['r'] = ".....|.....|#.##.|##..#|#....|#....|#....",
                ['s'] = ".....|.....|.###.|#....|.###.|....#|####.",
                ['t'] = ".#...|.#...|###..|.#...|.#...|.#..#|..##.",
                ['u'] = ".....|.....|#...#|#...#|#...#|#..##|.##.#",
                ['v'] = ".....|.....|#...#|#...#|#...#|.#.#.|..#..",
                ['w'] = ".....|.....|#...#|#...#|#.#.#|#.#.#|.#.#.",
                ['x'] = ".....|.....|#...#|.#.#.|..#..|.#.#.|#...#",
                ['y'] = ".....|.....|#...#|#...#|.####|....#|.###.",
                ['z'] = ".....|.....|#####|...#.|..#..|.#...|#####",
                ['0'] = ".###.|#...#|#..##|#.#.#|##..#|#...#|.###.",
                ['1'] = "..#..|.##..|..#..|..#..|..#..|..#..|.###.",
                ['2'] = ".###.|#...#|....#|...#.|..#..|.#...|#####",
                ['3'] = "#####|...#.|..#..|...#.|....#|#...#|.###.",
                ['4'] = "...#.|..##.|.#.#.|#..#.|#####|...#.|...#.",
                ['5'] = "#####|#....|####.|....#|....#|#...#|.###.",
                ['6'] = "..##.|.#...|#....|####.|#...#|#...#|.###.",
                ['7'] = "#####|....#|...#.|..#..|.#...|.#...|.#...",
                ['8'] = ".###.|#...#|#...#|.###.|#...#|#...#|.###.",
                ['9'] = ".###.|#...#|#...#|.####|....#|...#.|.##..",
                ['_'] = ".....|.....|.....|.....|.....|.....|#####",
                ['-'] = ".....|.....|.....|#####|.....|.....|.....",
                ['.'] = ".....|.....|.....|.....|.....|.##..|.##..",
                [':'] = ".....|.##..|.##..|.....|.##..|.##..|.....",
                ['>'] = "#....|.#...|..#..|...#.|..#..|.#...|#....",
                ['%'] = "##...|##..#|...#.|..#..|.#...|#..##|...##",
                [' '] = ".....|.....|.....|.....|.....|.....|.....",
            };
            var glyphs = new Dictionary<char, bool[]>();
            foreach (var kv in src)
            {
                var rows = kv.Value.Split('|');
                var bits = new bool[35];
                for (int y = 0; y < 7 && y < rows.Length; y++)
                for (int x = 0; x < 5 && x < rows[y].Length; x++)
                    bits[y * 5 + x] = rows[y][x] == '#';
                glyphs[kv.Key] = bits;
            }
            return glyphs;
        }

        /// <summary>White text on a translucent dark box; unsupported characters (e.g. Japanese) are skipped.</summary>
        static void DrawLabel(Color32[] buf, int bw, int bh, int x, int yTop, string text, int scale)
        {
            if (string.IsNullOrEmpty(text)) return;
            var chars = text.ToLowerInvariant().Where(ch => Glyphs.ContainsKey(ch)).ToArray();
            if (chars.Length == 0) return;
            int pad = 2 * scale;
            int textW = chars.Length * 6 * scale - scale, textH = 7 * scale;
            FillRect(buf, bw, bh, x, yTop, textW + pad * 2, textH + pad * 2, new Color32(0, 0, 0, 255), 0.6f);
            var white = new Color32(255, 255, 255, 255);
            for (int k = 0; k < chars.Length; k++)
            {
                var bits = Glyphs[chars[k]];
                int gx = x + pad + k * 6 * scale;
                for (int gy = 0; gy < 7; gy++)
                for (int gxi = 0; gxi < 5; gxi++)
                {
                    if (!bits[gy * 5 + gxi]) continue;
                    FillRect(buf, bw, bh, gx + gxi * scale, yTop + pad + gy * scale, scale, scale, white, 1f);
                }
            }
        }
    }
}
