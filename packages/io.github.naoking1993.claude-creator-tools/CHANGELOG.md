# 変更履歴

このパッケージの変更点を記録します。版の付け方は [Semantic Versioning](https://semver.org/lang/ja/) に従います。0.x のあいだは、issue コード・引数・出力のキーが変わることがあり、変わったときはここに書きます。

## [0.1.0] - 2026-10-25

最初の版です。**Unity の中では一度も実行していません。** オフラインでのコンパイル（Unity 2021.1 の参照 DLL、MCP for Unity 9.7.1 のソースから作ったスタブ）と、VRChat SDK 3.10.5 の DLL に対するフィールド名の照合、ワールドギミックの U# 静的チェックだけを行っています。導入したら README の「初回確認」を行ってください。

### 追加

- MCP for Unity（9.7.1 以上）用の MCP ツール 8 個。
  - `cct_environment`: 接続中のプロジェクト・シーン・VRChat 関連パッケージ・アバターとワールド記述子の確認（読み取り専用）
  - `cct_avatar_audit`: アバターのアップロード前監査。PC / モバイルの性能ランク（SDK の計算器を使えるときは SDK と同じ数値）、SDK がアップロードを止めるエラー、Missing Script、Write Defaults、Expression。`post_build` で NDMF 処理後の一時クローンも監査（読み取り専用）
  - `cct_world_audit`: ワールドシーンのアップロード前点検。SDK・UdonSharp が止める項目（`verdict`）、ライティング、ミラー、Udon、動画プレイヤー、Android/iOS、Editor.log のコンパイルエラー（読み取り専用）
  - `cct_capture_views`: 決まった角度からの PNG 撮影、一覧画像、同じ条件での前後比較（`Library/ClaudeCreatorTools/Captures` にだけ書く）
  - `cct_snapshot`: ファイル単位のスナップショット（`.meta` 込み）、差分、復元、削除。復元・削除は `confirm: true` のときだけ実行し、復元の前に現状を自動で退避
  - `cct_animator_inspect`: AnimatorController のダンプと VRChat 向けの検査、パラメーター・Binding の使用箇所の検索（読み取り専用）
  - `cct_dependency_report`: 依存関係・使用元の逆引き・シーン外参照のレポート（読み取り専用）
  - `cct_world_gimmicks`: ワールドギミックのサンプルの一覧と取り込み。取り込みは `confirm: true` のときだけ
- `Tools > Claude Creator Tools` のメニュー。MCP ツールと同じ処理を、MCP for Unity が無いプロジェクトでも実行できます。ダイアログは出しません。
- ワールドギミックのサンプル（`Samples~/WorldGimmicks`、UdonSharp）: `CctLocalToggle`、`CctGlobalToggle`、`CctMirrorSwitcher`、`CctTeleporter`、`CctPickupResetter`、`CctAudioVolume`、`CctAreaToggle`、`CctPlayerCounter`。

### 既知の制限

- Unity 2022.3 の実機、ClientSim、VRChat での動作は未確認です。
- 利用者の MCP サーバー（9.7.1 + FastMCP 4 改修版、Python 3.14）で、`cct_*` が単独のツールとして出るか、`execute_custom_tool` 経由になるかは未確認です。
- 詳しくは README の「既知の制限」を参照してください。
