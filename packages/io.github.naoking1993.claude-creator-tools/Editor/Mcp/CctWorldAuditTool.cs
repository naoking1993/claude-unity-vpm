#if CCT_UNITY_MCP
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    [McpForUnityTool(CctWorldAudit.ToolName,
        Description = "Claude Creator Tools: read-only pre-upload audit of a VRChat WORLD scene (Worlds SDK 3.10.5, Unity 2022.3.22f1). " +
                      "Use it before Build & Test / upload, after big scene changes, and when a world fails to build or behaves differently " +
                      "in VRChat. It re-implements the SDK's own world build checks with the same conditions (exactly one VRC Scene " +
                      "Descriptor and one PipelineManager across ALL loaded scenes, valid spawns, spawn >= 1000 m from origin or below " +
                      "Respawn Height, VRChat layer names 3-21 and collision matrix 0-21, VRC_EventHandler/SDK2, VRCObjectSync with a " +
                      "Manual UdonBehaviour or an object pool, Android texture format ASTC, gravity, fog stripping, Auto Generate lighting, " +
                      "audio sources without VRC Spatial Audio Source, PhysBone/Contact 1024 limits, textures with Max Size > 8192, Box mip " +
                      "filter, TAA on the Reference Camera, mobile shader allowlist, last build > 100 MB on Android/iOS) plus UdonSharp's " +
                      "project-wide compile validation (program asset without source, script outside a U# assembly = 'does not belong to a " +
                      "U# assembly', duplicate program assets), Editor.log U#/CS errors, and the creator docs' recommendations: mirrors " +
                      "enabled at start / reflect layers, lighting bake state (lightmaps, baked lights, light/reflection probes, occlusion " +
                      "culling), realtime shadow lights, extra active cameras, post processing, Udon program/sync summary, pickups (Rigidbody/" +
                      "Collider/ObjectSync/layer), stations, pools, portals, pedestals, video players (output, audio, URL allowlist, HTTPS on " +
                      "Android, more than 2 players), VRC Ui Shape canvases, reserved layers in use, missing scripts, missing references, and " +
                      "components outside the SDK3 world allowlist (NOT_WHITELISTED: the SDK does not check this at build, the client strips " +
                      "them), and Android/iOS limits (250k triangles, 1k textures, realtime lights, post processing disabled, transparency, " +
                      "GPU instancing). Everything is a static editor-side estimate: it does not build, upload, run ClientSim or modify the " +
                      "scene. Returns issues (stable UPPER_SNAKE codes; message says when the SDK reports the same thing) and data: scenes, " +
                      "platforms, counts, project, descriptor (spawns, respawn height, reference camera, pipeline manager / blueprint id), " +
                      "layers, lighting, rendering (triangles, materials, shaders, textures, static flags, cameras, post_processing), " +
                      "mirrors, udon (programs, sync methods, udonsharp), android, not_checked, notes; the report file additionally has " +
                      "interactables, colliders, ui, components, hotspots (top renderers/meshes/textures/materials) and editor_log. " +
                      "Parameters: scene = 'active' (default; the scene the SDK uploads) | 'all' (every loaded scene) | path or name of a " +
                      "loaded scene (SDK-blocking descriptor/pipeline/ObjectSync/PhysBone checks always cover every loaded scene); " +
                      "platform = 'all' (default) | 'pc' | 'android' | 'ios' (android and ios share the mobile limits); top_n = list " +
                      "length per section (default 15, 1-500); check_references = scan components for references to deleted " +
                      "assets/objects (default true); max_reference_scan = maximum components to scan for missing references (default " +
                      "5000; the scan also stops when the audit has run timeout_seconds - 10 s); check_editor_log = read the tail of " +
                      "Editor.log for 'error CS' and UdonSharp errors (default true). For big worlds pass timeout_seconds (e.g. 120)." +
                      CctMcpBridge.CommonParamsDoc)]
    public static class CctWorldAuditTool
    {
        static readonly string[] SummaryKeys =
        {
            "scenes", "platforms", "counts", "project", "descriptor", "layers", "lighting", "rendering", "mirrors", "udon",
            "android", "not_checked", "notes",
        };

        public class Parameters
        {
            [ToolParameter("'active' (default), 'all', or the path/name of a loaded scene to audit; SDK-blocking descriptor/pipeline checks always use every loaded scene", Required = false, DefaultValue = "active")]
            public string scene { get; set; }

            [ToolParameter("'all' (default), 'pc', 'android' or 'ios' (android and ios share the mobile limits)", Required = false, DefaultValue = "all")]
            public string platform { get; set; }

            [ToolParameter("Entries per list in each section (default 15, 1-500)", Required = false, DefaultValue = "15")]
            public int top_n { get; set; }

            [ToolParameter("Scan components for references to deleted assets/objects (default true)", Required = false, DefaultValue = "true")]
            public bool check_references { get; set; }

            [ToolParameter("Maximum components scanned for missing references (default 5000); the scan also stops at timeout_seconds - 10 s", Required = false, DefaultValue = "5000")]
            public int max_reference_scan { get; set; }

            [ToolParameter("Read the tail of Editor.log for 'error CS' and UdonSharp errors (default true)", Required = false, DefaultValue = "true")]
            public bool check_editor_log { get; set; }

            [ToolParameter("'summary' (default) or 'full'. Summary keeps the response short; the full report is always saved to disk", Required = false, DefaultValue = "summary")]
            public string detail { get; set; }

            [ToolParameter("Maximum number of issues in a summary response, errors first (default 30)", Required = false, DefaultValue = "30")]
            public int max_issues { get; set; }

            [ToolParameter("Write the full JSON report under Library/ClaudeCreatorTools/Reports (default true)", Required = false, DefaultValue = "true")]
            public bool save_report { get; set; }

            [ToolParameter("MCP call timeout in seconds (default 30, max 3600); read by the MCP server, and by this tool to bound its scans", Required = false, DefaultValue = "30")]
            public int timeout_seconds { get; set; }
        }

        public static object HandleCommand(JObject @params) =>
            CctMcpBridge.Handle(@params, CctWorldAudit.ToolName, CctWorldAudit.Run, SummaryKeys);
    }
}
#endif
