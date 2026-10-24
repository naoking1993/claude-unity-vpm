#if CCT_UNITY_MCP
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    [McpForUnityTool(CctCaptureViews.ToolName,
        Description = "Claude Creator Tools: render reproducible PNG images of an avatar or object from fixed angles relative to " +
                      "its forward (+Z), so you can judge appearance and compare before/after changes under identical camera, " +
                      "pose and lighting. Use it before and after material tuning / optimization / outfit edits: capture once, " +
                      "edit, then capture again with compare_with = the first capture (or 'latest') to get per-view mean " +
                      "absolute RGB difference, changed-pixel percentage and bounding box plus compare_<view>.png (before | after) " +
                      "and diff_<view>.png. Read the returned contact_sheet / PNG paths with your file Read tool to see them. " +
                      "mode 'isolated' (default) renders a script-free clone in a hidden preview scene with neutral studio " +
                      "lighting (reproducible, ignores the open scene); 'scene' renders the real scene with its own lights. " +
                      "Side effects: writes files under Library/ClaudeCreatorTools/Captures/<id>_<label>/ (PNGs, contact_sheet.png, " +
                      "manifest.json with every parameter and camera pose); never modifies scenes or assets (reports SCENE_DIRTIED " +
                      "if a scene unexpectedly became dirty).")]
    public static class CctCaptureViewsTool
    {
        // Parameters that compare_with can inherit have no DefaultValue on purpose: an omitted value must stay absent so
        // the previous capture's value is reused instead of a server-filled default.
        public class Parameters
        {
            [ToolParameter("GameObject to capture: hierarchy path '/Avatar', unique name, '#instanceId', prefab asset path " +
                           "'Assets/....prefab', or empty / 'selection' for the current selection (default)", Required = false, DefaultValue = "")]
            public string target { get; set; }

            [ToolParameter("Comma-separated views and/or presets. Views: front, back, left, right, front_left, front_right, back_left, " +
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
                           "Library/ClaudeCreatorTools/Captures, or 'latest'. Unspecified parameters are inherited from it; explicitly " +
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
                           "run on the clone; use only if a script is needed for the look", Required = false)]
            public bool keep_scripts { get; set; }

            [ToolParameter("'summary' (default) or 'full'. Summary keeps the response short; the full report is always saved to disk", Required = false, DefaultValue = "summary")]
            public string detail { get; set; }

            [ToolParameter("Maximum number of issues in a summary response, errors first (default 30)", Required = false, DefaultValue = "30")]
            public int max_issues { get; set; }

            [ToolParameter("Write the full JSON report under Library/ClaudeCreatorTools/Reports (default true)", Required = false, DefaultValue = "true")]
            public bool save_report { get; set; }
        }

        static readonly string[] SummaryKeys =
        {
            "capture_dir", "contact_sheet", "manifest", "files", "views", "compare_with", "comparisons", "target", "params",
        };

        public static object HandleCommand(JObject @params) =>
            CctMcpBridge.Handle(@params, CctCaptureViews.ToolName, CctCaptureViews.Run, SummaryKeys);
    }
}
#endif
