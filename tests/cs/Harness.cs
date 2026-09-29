using System; using System.IO; using System.Text;
using MCPForUnity.Editor.Services.Server;
public static class Harness {
  // args: <projectDir> <command>  -> prints hex of mcp-terminal.cmd and psi.Arguments
  public static int Main(string[] a) {
    UnityEngine.Application.dataPath = Path.Combine(a[0], "Assets");
    var psi = new TerminalLauncher().CreateTerminalProcessStartInfo(a[1]);
    var p = Path.Combine(a[0], "Library", "MCPForUnity", "TerminalScripts", "mcp-terminal.cmd");
    Console.WriteLine(BitConverter.ToString(File.ReadAllBytes(p)).Replace("-", ""));
    Console.WriteLine(psi.FileName + " " + psi.Arguments);
    return 0;
  }
}
