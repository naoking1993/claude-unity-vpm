# Claude Creator Tools のオフライン検証

`packages/io.github.naoking1993.claude-creator-tools` を Unity なしで検証するための仕組みです。Linux（クラウドセッションや WSL）で動きます。Unity の参照 DLL や VRChat SDK はリポジトリに入れず、`setup.sh` が作業フォルダーにダウンロードします。

```sh
sudo apt-get install -y dotnet-sdk-8.0 mono-complete unzip   # 初回だけ
export CCT_DEV=~/.cache/cct-dev                               # 作業フォルダー（リポジトリの外）
tests/dev/setup.sh                                            # 参照 DLL と SDK を取得（約 190 MB）
tests/dev/check_all.sh                                        # 全チェック。最後に ALL CHECKS OK
```

## 何を確かめているか

| チェック | 中身 | 分かること / 分からないこと |
| --- | --- | --- |
| `cct/compile.sh` | Roslyn（C# 9）で Core と MCP アダプターを別々にコンパイル。Unity 2021.1 の参照 DLL と、MCP for Unity 9.7.1 の本物の `McpForUnityToolAttribute.cs` / `Response.cs` から作ったスタブを使う | 型と API の綴りの誤りは分かる。Unity 2022.3 にしか無い API は誤って失敗する（使わない方針）。実行時の挙動は分からない |
| `MetaCheck`（compile.sh が続けて実行） | MCP for Unity 9.7.1 の登録規則を再現：明示名 `cct_*`、`public static object HandleCommand(JObject)`、入れ子の `Parameters`、snake_case の引数名、必須引数を先に置く、全引数名と `execute_custom_tool` の説明が `Description` にある、共通引数（detail / max_issues / save_report / timeout_seconds）がある | 9.7.1 サーバーは `[ToolParameter]` の説明を LLM に渡さず、Python 3.14 では `execute_custom_tool` 経由になるため（`research` の確認結果） |
| `FieldCheck` + `tests/vrc-fields/*.txt` | ツールが `SerializedObject` で読む VRChat SDK の型とプロパティパスを、本物の SDK 3.10.5 の DLL にリフレクションで照合する | フィールド名・配列・列挙の綴り違いは分かる。SDK がソースで配布する型（`UdonBehaviour` など）は対象外（txt にコメントで記載） |
| `PureTests` / `RankTests` | Unity に依存しない部分（JSON、引数、パフォーマンスランク表）の単体テスト | ランク表の境界値（閾値ちょうど＝そのランク）など |
| `udon/check_usharp.py` | ワールドギミック（UdonSharp）を本物の SDK DLL と SDK 同梱の UdonSharp ランタイムに対してコンパイルし、U# が扱えない構文と、Udon に公開されていない API 呼び出しを検出する | U# コンパイラそのものは動かしていない。UI / TextMeshPro は最小スタブ |
| `tools/gen_meta.py --check` | パッケージ内の全ファイル・フォルダーに `.meta` があり、GUID が重複していない | |

どのチェックも、Unity エディター上・VRChat クライアント上での動作は保証しません。実機での確認手順はパッケージの README の「初回確認」にあります。

## 便利な使い方

- 1 つのツールだけを確かめる: `ONLY=CctSnapshot tests/dev/cct/compile.sh`
- SDK の型のフィールドを調べる: `mono $CCT_DEV/bin/FieldCheck.exe dump VRC.SDK3.Avatars.Components.VRCAvatarDescriptor 1`
- SDK の列挙値: `mono $CCT_DEV/bin/FieldCheck.exe enum VRC.SDK3.Avatars.Components.VRCAvatarDescriptor+AnimLayerType`
- U# スクリプトを 1 本だけ: `python3 tests/dev/udon/check_usharp.py path/to/Script.cs`（`--strict` で警告も失敗扱い）

SDK の版を上げるときは `setup.sh` の `SDK_VER` を変え、`$CCT_DEV/udon-build` を消してから `check_all.sh` を実行します。
