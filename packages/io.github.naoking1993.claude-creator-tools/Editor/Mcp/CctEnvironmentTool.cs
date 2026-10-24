#if CCT_UNITY_MCP
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Naoking.ClaudeCreatorTools.Mcp
{
    [McpForUnityTool(CctEnvironment.ToolName,
        Description = "Claude Creator Tools: read-only snapshot of the connected Unity project for VRChat work - Unity version, " +
                      "project path, open scenes and unsaved state, play mode, installed VRChat SDK / Modular Avatar / NDMF / " +
                      "AAO / lilToon / UdonSharp versions, avatar roots (VRCAvatarDescriptor) and world descriptors with " +
                      "blueprint IDs, current selection, and where cct_* tools write reports. Call this first in a session. " +
                      "Parameters: all_packages = list every registered package instead of only VRChat-related ones (default false)." +
                      CctMcpBridge.CommonParamsDoc)]
    public static class CctEnvironmentTool
    {
        public class Parameters
        {
            [ToolParameter("List every registered package instead of only VRChat-related ones (default false)", Required = false, DefaultValue = "false")]
            public bool all_packages { get; set; }

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
            CctMcpBridge.Handle(@params, CctEnvironment.ToolName, CctEnvironment.Run, null);
    }
}
#endif
