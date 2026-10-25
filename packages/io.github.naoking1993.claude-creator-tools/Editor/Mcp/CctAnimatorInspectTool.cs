#if CCT_UNITY_MCP
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    [McpForUnityTool(CctAnimatorInspect.ToolName,
        Description = "Claude Creator Tools: read-only dump and VRChat lint of AnimatorControllers, for tracing menu -> parameter -> " +
                      "MA Merge Animator -> layer/state/transition/driver -> clip binding -> object. Use it for broken " +
                      "toggles/gestures/emotes, Write Defaults questions and to find what reads or writes a parameter or property. " +
                      "Parameters: controller = AnimatorController / AnimatorOverrideController asset path 'Assets/.../X.controller', " +
                      "GUID or unique asset name (optional; with target it is checked against that avatar); target = avatar or object: " +
                      "'' / 'selection' (default; a controller selected in the Project window is used directly, a GameObject is moved " +
                      "up to its VRCAvatarDescriptor, nothing selected = the only avatar in the loaded scenes), '/Root/Child', " +
                      "'#instanceId', unique name or 'Assets/.../X.prefab'; layer = playable layer to resolve from the avatar: 'fx' " +
                      "(default), 'gesture', 'action', 'base', 'additive', 'sitting', 'tpose', 'ikpose' or 'all'; include_ma = list MA " +
                      "Merge Animators and inspect those merging into the layer (default true when a target is given); layers = JSON " +
                      "array of animator layer names or indices to include (default all); include_transitions = put transitions in the " +
                      "graph (default true; lints always run); check_bindings = check every clip curve path under the avatar root or " +
                      "the MA relative root (default true when a target is given); max_states = states written to the graph before " +
                      "truncating (default 400, max 20000); find_parameter = exact parameter name: list every declaration and use " +
                      "(conditions, blend trees, speed/time, driver read/write, play audio); find_binding = case-insensitive text " +
                      "matched against curve paths and 'Component.property' ('path:property' requires both, e.g. " +
                      "'Body:blendShape.smile' lists every writer of a face shape). Returns source, playable_layers, controllers, " +
                      "parameters, layers (weight, mask, sync, WD), graph_preview, bindings, ma_merge_animators, find (50 rows; up to " +
                      "2000 in the report's find_all) and lint issues (WD_MIXED, PARAM_UNDEFINED, PARAM_TYPE_MISMATCH, " +
                      "TRANSITION_NEVER_FIRES, BINDING_PATH_MISSING, TRACKING_NOT_RESTORED, ...; use layer='all' for the whole " +
                      "avatar); the full graph is in the report at report_path. Static pre-build analysis; nothing is modified. The " +
                      "scan stops at about 60% of timeout_seconds (TRUNCATED): pass timeout_seconds=120 for very large FX controllers, " +
                      "or narrow with layers." +
                      CctMcpBridge.CommonParamsDoc)]
    public static class CctAnimatorInspectTool
    {
        static readonly string[] SummaryKeys =
        {
            "source", "playable_layers", "controllers", "controllers_omitted", "layers", "layers_total", "layers_omitted", "parameters",
            "parameters_total", "parameters_omitted", "graph_preview", "bindings", "ma_merge_animators", "ma_merge_animators_omitted",
            "find", "notes",
        };

        public class Parameters
        {
            [ToolParameter("AnimatorController / AnimatorOverrideController: 'Assets/.../X.controller', GUID or unique asset name (optional)", Required = false, DefaultValue = "")]
            public string controller { get; set; }

            [ToolParameter("Avatar or object: '' / 'selection' (default), '/Root/Child', '#instanceId', unique name, or 'Assets/.../X.prefab'", Required = false, DefaultValue = "")]
            public string target { get; set; }

            [ToolParameter("Playable layer from the avatar: fx (default), gesture, action, base, additive, sitting, tpose, ikpose, all", Required = false, DefaultValue = "fx")]
            public string layer { get; set; }

            [ToolParameter("List MA Merge Animators and inspect those for the layer (default true when a target is given)", Required = false)]
            public bool include_ma { get; set; }

            [ToolParameter("Animator layer names or indices to include (default all)", Required = false)]
            public string[] layers { get; set; }

            [ToolParameter("Include transitions in the graph (default true)", Required = false, DefaultValue = "true")]
            public bool include_transitions { get; set; }

            [ToolParameter("Check clip curve paths under the avatar / MA relative root (default true when a target is given)", Required = false)]
            public bool check_bindings { get; set; }

            [ToolParameter("States written to the graph before truncating (default 400, max 20000)", Required = false, DefaultValue = "400")]
            public int max_states { get; set; }

            [ToolParameter("Parameter name to trace (declarations and every use)", Required = false, DefaultValue = "")]
            public string find_parameter { get; set; }

            [ToolParameter("Text matched against curve paths / Component.property ('path:property' = both)", Required = false, DefaultValue = "")]
            public string find_binding { get; set; }

            [ToolParameter("'summary' (default) or 'full'. Summary keeps the response short; the full report is always saved to disk", Required = false, DefaultValue = "summary")]
            public string detail { get; set; }

            [ToolParameter("Maximum number of issues in a summary response, errors first (default 30)", Required = false, DefaultValue = "30")]
            public int max_issues { get; set; }

            [ToolParameter("Write the full JSON report under Library/ClaudeCreatorTools/Reports (default true)", Required = false, DefaultValue = "true")]
            public bool save_report { get; set; }

            [ToolParameter("MCP call timeout in seconds (default 30, max 3600); read by the MCP server, and by this tool: the scan stops at about 60% of it (TRUNCATED)", Required = false, DefaultValue = "30")]
            public int timeout_seconds { get; set; }
        }

        public static object HandleCommand(JObject @params) =>
            CctMcpBridge.Handle(@params, CctAnimatorInspect.ToolName, CctAnimatorInspect.Run, SummaryKeys);
    }
}
#endif
