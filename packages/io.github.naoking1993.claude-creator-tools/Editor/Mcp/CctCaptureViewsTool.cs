#if CCT_UNITY_MCP
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    [McpForUnityTool(CctCaptureViews.ToolName,
        Description = "Claude Creator Tools: render reproducible PNG images of an avatar or object from fixed angles relative to " +
                      "its front (+Z of the target, or of the enclosing avatar root when the target is a part of an avatar), so you " +
                      "can judge appearance and compare before/after changes under identical camera, pose and lighting. Use it " +
                      "before and after material tuning / optimization / outfit or blend-shape edits: capture once, edit, then " +
                      "capture again with compare_with = the first capture_dir (or 'latest'); omitted parameters are inherited from " +
                      "that capture so camera, lighting and background match. To look at the result, Read the contact_sheet PNG " +
                      "(one labelled grid of all views) first; open single view PNGs or compare images only when you need detail. " +
                      "mode 'isolated' (default) renders a script-free clone in a hidden preview scene with neutral studio lighting " +
                      "(reproducible; ignores the open scene's lights, skybox and other objects, and shows the edit-time hierarchy " +
                      "without NDMF / Modular Avatar build or preview effects); 'scene' renders the real open scene with its own " +
                      "lighting. Returns capture_dir, contact_sheet (absolute PNG path), manifest, views[] (file = absolute PNG " +
                      "path, camera pose), params (effective, incl. inherited), target; with compare_with also comparisons[] per " +
                      "view (changed_pct_foreground / mean_abs_diff_foreground exclude the background - prefer them; whole-image " +
                      "changed_pct / mean_abs_diff; max_diff; changed_bbox; compare_image = before | after; diff_image) and " +
                      "comparison_units (8-bit sRGB levels, not Delta E). Side effects: " +
                      "writes only files under Library/ClaudeCreatorTools/Captures/<id>_<label>/ (PNGs, contact_sheet.png, " +
                      "manifest.json with every parameter and camera pose); never modifies scenes or assets (reports SCENE_DIRTIED " +
                      "if a scene unexpectedly became dirty). Parameters: target = GameObject spec: hierarchy path '/Avatar', unique " +
                      "name, '#instanceId', prefab asset path 'Assets/....prefab', or empty / 'selection' (default); views = " +
                      "comma-separated string of view names and/or presets: front, back, left, right, front_left, " +
                      "front_right, back_left, back_right, top, face_front, face_left45, face_right45 (left/right = the target's own " +
                      "sides; face_* always frame the humanoid head), preset 'turnaround' = front,front_right,right,back,left," +
                      "front_left, preset 'face' = face_front,face_left45,face_right45 (default front,back,left,right); resolution = " +
                      "square image size 256-2048 (default 768); mode = 'isolated' (default) or 'scene'; background = 'solid' " +
                      "(default), 'transparent' (PNG with alpha) or 'skybox' (scene mode only); background_color = #RRGGBB sRGB " +
                      "(default #808080); fov = vertical field of view 5-90 degrees (default 25); padding = margin fraction per side " +
                      "0-0.4 (default 0.08); framing = 'auto' (default, whole body), 'upper' (hips to top) or 'face' (head) for " +
                      "non-face views; contact_sheet = also write contact_sheet.png (default true); label = text for the folder " +
                      "name, Japanese kept (default the target's name); compare_with = previous capture folder, its manifest.json, " +
                      "a folder name under Library/ClaudeCreatorTools/Captures, or 'latest' (newest capture of the same target; " +
                      "falls back to the newest capture of any object with COMPARE_TARGET_DIFFERS) - explicitly different " +
                      "resolution/mode/background/background_color/fov/padding/framing/light_follow_camera/keep_scripts give " +
                      "COMPARE_MISMATCH and no comparison (the new capture is still written); reuse_cameras = with compare_with, " +
                      "reuse the previous camera pose when the framing bounds changed (e.g. a mesh was removed) so pixels stay " +
                      "aligned (default true); light_follow_camera = isolated mode: aim key/fill lights relative to each camera " +
                      "instead of fixed to the target's front (default false: back views get only fill + ambient light); " +
                      "keep_scripts = isolated mode: keep MonoBehaviours on the clone (default false; VRChat PhysBones/contacts/" +
                      "constraints are always removed). Timing: one view is usually well under a second, but resolution > 1024 " +
                      "with many views, background='transparent' (two renders per view), compare_with, or the first capture after " +
                      "the Editor starts (shaders are compiled synchronously so no placeholder appears) can exceed 30 s - pass " +
                      "timeout_seconds = 120 for those (it only raises the limit, so it is safe when unsure). If a call times out, " +
                      "Unity may still finish it: look for the newest folder under Library/ClaudeCreatorTools/Captures before " +
                      "retrying." + CctMcpBridge.CommonParamsDoc)]
    public static class CctCaptureViewsTool
    {
        // Parameters that compare_with can inherit have no DefaultValue on purpose: an omitted value must stay absent so
        // the previous capture's value is reused instead of a server-filled default.
        public class Parameters
        {
            [ToolParameter("GameObject to capture: hierarchy path '/Avatar', unique name, '#instanceId', prefab asset path " +
                           "'Assets/....prefab', or empty / 'selection' for the current selection (default)", Required = false, DefaultValue = "")]
            public string target { get; set; }

            [ToolParameter("Comma-separated string of views and/or presets. Views: front, back, left, right, front_left, front_right, back_left, " +
                           "back_right, top, face_front, face_left45, face_right45 (left/right = the target's own left/right side; face_* " +
                           "always frame the humanoid head). Presets: 'turnaround' = front,front_right,right,back,left,front_left; " +
                           "'face' = face_front,face_left45,face_right45. Default 'front,back,left,right' (or the compare_with capture's views)",
                Required = false)]
            public string views { get; set; }

            [ToolParameter("Square image size in pixels, 256-2048 (default 768, or the compare_with capture's value)", Required = false)]
            public int resolution { get; set; }

            [ToolParameter("'isolated' (default): clone into a hidden preview scene with neutral key/fill/ambient lighting, scripts " +
                           "removed, skinned meshes baked in their current pose - reproducible and independent of the open scene. " +
                           "'scene': render the open scene in place with a temporary camera (the user's lighting, skybox and other objects)",
                Required = false)]
            public string mode { get; set; }

            [ToolParameter("'solid' (default, uses background_color), 'transparent' (PNG with alpha recovered by black/white " +
                           "difference matting), or 'skybox' (scene mode only)", Required = false)]
            public string background { get; set; }

            [ToolParameter("Background colour as #RRGGBB in sRGB (default #808080). PNG background pixels equal this value", Required = false)]
            public string background_color { get; set; }

            [ToolParameter("Vertical field of view in degrees, 5-90 (default 25; narrow = little perspective distortion)", Required = false)]
            public float fov { get; set; }

            [ToolParameter("Margin around the framed bounds as a fraction of the image on each side, 0-0.4 (default 0.08)", Required = false)]
            public float padding { get; set; }

            [ToolParameter("Framing for non-face views: 'auto' (default, whole body bounds; renderers far from the humanoid " +
                           "skeleton are ignored), 'upper' (hips to top), 'face' (head)", Required = false)]
            public string framing { get; set; }

            [ToolParameter("Also write contact_sheet.png, one labelled grid of all views to look at in a single read (default true)",
                Required = false, DefaultValue = "true")]
            public bool contact_sheet { get; set; }

            [ToolParameter("Optional text for the output folder name (default: the target's name). Japanese is kept", Required = false, DefaultValue = "")]
            public string label { get; set; }

            [ToolParameter("Previous capture to compare with: its folder path, its manifest.json, a folder name under " +
                           "Library/ClaudeCreatorTools/Captures, or 'latest' (newest capture of the same target). Unspecified " +
                           "parameters are inherited from it; explicitly " +
                           "different resolution/mode/background/fov/padding/framing/lighting params give COMPARE_MISMATCH and no comparison",
                Required = false, DefaultValue = "")]
            public string compare_with { get; set; }

            [ToolParameter("With compare_with: when the framing bounds changed (e.g. a mesh was removed), reuse the previous camera " +
                           "pose (relative to the target) so pixels stay aligned (default true)", Required = false, DefaultValue = "true")]
            public bool reuse_cameras { get; set; }

            [ToolParameter("Isolated mode: aim the key/fill lights relative to each camera instead of fixed to the target's front " +
                           "(default false: back views are lit by the fill light only)", Required = false)]
            public bool light_follow_camera { get; set; }

            [ToolParameter("Isolated mode: keep MonoBehaviours on the clone (default false). When true, [ExecuteAlways] scripts may " +
                           "run on the clone (in Play mode every kept script runs Awake/OnEnable); use only if a script is needed for " +
                           "the look. VRChat PhysBones/contacts/constraints are always removed", Required = false)]
            public bool keep_scripts { get; set; }

            [ToolParameter("'summary' (default) or 'full'. Summary keeps the response short; the full report is always saved to disk", Required = false, DefaultValue = "summary")]
            public string detail { get; set; }

            [ToolParameter("Maximum number of issues in a summary response, errors first (default 30)", Required = false, DefaultValue = "30")]
            public int max_issues { get; set; }

            [ToolParameter("Write the full JSON report under Library/ClaudeCreatorTools/Reports (default true)", Required = false, DefaultValue = "true")]
            public bool save_report { get; set; }

            [ToolParameter("MCP call timeout in seconds (default 30, max 3600); read by the MCP server, not by the tool", Required = false, DefaultValue = "30")]
            public int timeout_seconds { get; set; }
        }

        // "files" is left out of the summary: every path in it is already in contact_sheet, manifest, views[].file and
        // comparisons[].compare_image / diff_image (the saved report keeps the full list). "details" carries failure
        // context such as TARGET_NOT_FOUND candidates.
        static readonly string[] SummaryKeys =
        {
            "capture_dir", "contact_sheet", "manifest", "views", "compare_with", "comparisons", "comparison_units", "target",
            "params", "details",
        };

        public static object HandleCommand(JObject @params) =>
            CctMcpBridge.Handle(@params, CctCaptureViews.ToolName, CctCaptureViews.Run, SummaryKeys);
    }
}
#endif
