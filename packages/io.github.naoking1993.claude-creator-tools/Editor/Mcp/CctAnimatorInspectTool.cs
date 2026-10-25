#if CCT_UNITY_MCP
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    [McpForUnityTool(CctAnimatorInspect.ToolName,
        Description = "Claude Creator Tools: read-only dump and VRChat lint of AnimatorControllers, for tracing menu -> parameter -> " +
                      "MA Merge Animator -> layer/state/transition/driver -> clip binding -> object. Resolves the controller from an " +
                      "asset or from an avatar's VRCAvatarDescriptor playable layers (default layers are inspected via the SDK sample " +
                      "controller and flagged) plus Modular Avatar Merge Animator components (listed with layer type, path mode, " +
                      "relative root, WD matching, merge order; the ones merging into the requested layer are inspected too). Returns " +
                      "controllers, parameters (with Expression Parameter / built-in types), a layer table (weight, blending, mask, sync, " +
                      "WD on/off, states, MA WD forecast), graph_preview (whole graph when small, else an outline; the full graph with " +
                      "states, motions incl. blend trees, VRChat behaviours such as Avatar Parameter Driver entries, Tracking / Layer / " +
                      "Playable Layer / Locomotion Control, Temporary Pose Space, Play Audio, and transitions with conditions is in " +
                      "the report file), bindings (missing paths / components / blend shapes under the avatar), and lint issues: " +
                      "WD_MIXED (SDK rule), EMPTY_STATE_WD_OFF, DIRECT_BLENDTREE_WD_OFF, PARAM_UNDEFINED, PARAM_TYPE_MISMATCH (also " +
                      "Expression / built-in type vs animator type, with VRChat's conversion rule), TRANSITION_NEVER_FIRES, " +
                      "ANYSTATE_SELF_LOOP, UNREACHABLE_STATE, " +
                      "LAYER_WEIGHT_ZERO, BINDING_PATH_MISSING, TRACKING_NOT_RESTORED / LOCOMOTION_NOT_RESTORED / " +
                      "ACTION_WEIGHT_NOT_LOWERED (settings VRChat keeps until another state resets them; use layer='all' for " +
                      "the whole avatar), driver / layer-control / MA checks. source.build_tools gives the installed MA / NDMF " +
                      "versions; with MA installed, findings that MA/NDMF fix at build (parameter type harmonization) are info. " +
                      "Static pre-build analysis only " +
                      "(not runtime values; in Play mode an NDMF-processed avatar shows its merged controllers); nothing is " +
                      "modified. The scan stops at about 60% of timeout_seconds and reports TRUNCATED, so pass timeout_seconds " +
                      "(e.g. 120) for avatars with very large FX controllers, or narrow with layers. " +
                      "Parameters: controller = AnimatorController / AnimatorOverrideController asset path 'Assets/.../X.controller', " +
                      "GUID or unique asset name (optional; with target it is checked against that avatar); target = avatar or object: " +
                      "'' / 'selection' (default; a controller selected in the Project window is used directly, a GameObject is moved up " +
                      "to its VRCAvatarDescriptor; nothing selected = the only avatar in the loaded scenes), '/Root/Child', '#instanceId', " +
                      "unique name or 'Assets/.../X.prefab'; objects without descriptor use their Animator controller or MA Merge " +
                      "Animators; layer = playable layer when resolving from the avatar: 'fx' (default), 'gesture', 'action', 'base', " +
                      "'additive', 'sitting', 'tpose', 'ikpose' or 'all'; include_ma = list MA Merge Animators and inspect those for " +
                      "the layer (default true when an avatar/target is given); layers = JSON array of animator layer names or indices " +
                      "to include (default all; WD controller totals still use every layer); include_transitions = put transitions in " +
                      "the graph (default true; lints always run); check_bindings = check every clip curve path (incl. blend tree " +
                      "children) under the avatar root, or under the MA relative root for Relative path mode (default true when a " +
                      "target is given); max_states = states written to the graph before truncating (default 400, max 20000); " +
                      "find_parameter = parameter name: list every declaration, condition, blend tree, speed/time parameter, driver " +
                      "read/write and play-audio use; find_binding = case-insensitive text matched against curve paths and " +
                      "'Component.property' ('path:property' requires both), returns the clips, constant values and states using them " +
                      "(e.g. find_binding='Body:blendShape.smile' lists every writer of a face shape). Search results keep 50 rows " +
                      "in the response; the report's find_all has up to 2000." +
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

            [ToolParameter("MCP call timeout in seconds (default 30, max 3600); read by the MCP server, not by the tool", Required = false, DefaultValue = "30")]
            public int timeout_seconds { get; set; }
        }

        public static object HandleCommand(JObject @params) =>
            CctMcpBridge.Handle(@params, CctAnimatorInspect.ToolName, CctAnimatorInspect.Run, SummaryKeys);
    }
}
#endif
