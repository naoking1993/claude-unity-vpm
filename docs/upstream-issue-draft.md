# 上流（CoplayDev/unity-mcp）向け issue の下書き

**投稿していない。** 投稿するかどうかは保守者が決める。根拠は [`upgrade-10.3-assessment.md`](upgrade-10.3-assessment.md) の (a)。

- 10.3.0 では既定の Start がこのコードを通らないので、実害は無い。そのため bug ではなく、使われていないコードの整理として出す想定。
- 既存の issue を検索した（`chcp` / `CP932` / `pidfile` / `non-ASCII`）が、同じ報告は見当たらなかった（2026-10-23）。
- 9.7.1 での実害と、`chcp 65001` で直ることは、Windows 11（日本語）＋ Unity 2022.3.22f1 の実機で確認済み（このリスティングの 9.7.101）。

---

**Title:** Windows: unused `TerminalLauncher.CreateTerminalProcessStartInfo` writes a BOM-less UTF-8 `.cmd` that cmd.exe decodes with the OEM code page (breaks non-ASCII project paths if reused)

**Version:** v10.3.0 (`aa5fc638`). The same code shipped as the live launch path in v9.7.1.

### Summary

On Windows, `TerminalLauncher.CreateTerminalProcessStartInfo` (`MCPForUnity/Editor/Services/Server/TerminalLauncher.cs`, the `UNITY_EDITOR_WIN` branch) writes `Library/MCPForUnity/TerminalScripts/mcp-terminal.cmd` with `File.WriteAllText(path, string)`. That produces BOM-less UTF-8. `cmd.exe` reads batch files in the console's OEM code page (CP932 on Japanese Windows, CP936/949/1252 etc. elsewhere), so any non-ASCII character in the launch command is garbled.

The launch command contains the project path through `--pidfile`. With a project folder such as `C:\Projects\テスト1`, the server receives a garbled path and creates the pid file in a different, garbled sibling folder (for example `繝・せ繝・1\Library\MCPForUnity\RunState\mcp_http_8080.pid`). Unity then never finds the pid file, and **Stop Server** returns early with "pidfile not available yet".

### Status in v10.3.0

Since the headless launch (`d1092118`, v9.7.3, plus the Windows stdin fix in #1279), `StartLocalHttpServer` uses `CreateHeadlessProcessStartInfo`. That method passes the command through `ProcessStartInfo.Arguments` (a UTF-16 command line), so non-ASCII paths are no longer affected on the default path.

`CreateTerminalProcessStartInfo` is still declared in `ITerminalLauncher`, still implemented, and still wrapped by the private `ServerManagementService.CreateTerminalProcessStartInfo`. That wrapper has no callers. So the bug is latent: anyone who wires the terminal launch back in (for example, a "show server window" option) brings it back.

### Steps to reproduce (v9.7.1, where this path was live)

1. On Windows with a non-UTF-8 system code page (for example Japanese), open a project whose path contains non-ASCII characters (for example `C:\Projects\テスト1`).
2. Use HTTP Local and press **Start Server**.
3. `Library/MCPForUnity/RunState/mcp_http_8080.pid` is not created in the project. A garbled sibling folder appears that contains it.
4. Press **Stop Server**: Unity logs `pidfile not available yet`, and the server keeps running.

### Proposed fix

Either:

1. **Remove the unused path** (preferred). Remove `CreateTerminalProcessStartInfo` from `ITerminalLauncher`, `TerminalLauncher` (Windows/macOS/Linux branches), and the private wrapper in `ServerManagementService`.

or, if the terminal launch is meant to come back:

2. **Make the script decode as UTF-8.** Emit `chcp 65001>nul` as the second line, and keep the file BOM-less. A UTF-8 BOM would corrupt the first line (`@echo off`), so do not switch to `Encoding.UTF8`.

   ```diff
                File.WriteAllText(
                    scriptPath,
                    "@echo off\r\n" +
   +                "chcp 65001>nul\r\n" +
                    "cls\r\n" +
                    command + "\r\n");
   ```

   We have shipped exactly this change on top of v9.7.1 and verified it on Windows 11 (Japanese) with Unity 2022.3.22f1: Start → pid file in the real project folder → Stop (pid file removed, server stopped) → Start. The script also expands `%`, and treats `& ^ | < >` specially outside quotes, so paths containing those characters remain a separate limitation of the batch-file approach.

### Notes

- The headless path still goes through `cmd.exe /c`. `%VAR%` in the command line is expanded there too, but only for variables that exist (unlike in a batch file), and the command line is UTF-16, so the code-page problem above does not apply.
