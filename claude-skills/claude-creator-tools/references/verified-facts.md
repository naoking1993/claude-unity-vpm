# 検証済み事実の台帳（Claude Creator Tools）

形式：事実 / 出典 / 検証日。
**記憶ベースの値をここに書かない。** パッケージのコード、MCP for Unity のソース、ユーザーのスキルの記録、実測だけを書く。確かめられなかったものは「未確認」、推論は「推定」、ユーザーの PC で確かめていないものは「**未実測**」と書く。誤りが分かったら「【訂正】」と書いて直す。

出典の略記：
- `P/…`：パッケージ `packages/io.github.naoking1993.claude-creator-tools/`（claude-unity-vpm リポジトリ、0.1.0）。
- `MCP調査`：MCP for Unity 9.7.1（Unity 側 `com.coplaydev.unity-mcp` と PyPI の `mcpforunityserver` 9.7.1 の wheel）のソースを読み、FastMCP 3.0.2 / 4.0.10 / 4.1.3 と Python 3.13 / 3.14 で再現試験をした調査ノート（クラウドセッション、2026-10-24）。
- `S/<スキル名>`：アカウントからセッションに同期されたユーザーのスキル（2026-10-24 時点の写し）。
- 「コード読解 2026-10-25」は、このスキルを書くためにパッケージのコードを読んだ日。「クラウド検査 2026-10-25」は、同じ日にクラウドセッション（Linux）でコンパイルと静的検査を通した結果。どちらも Unity 実機・ユーザーの PC での確認ではない。

---

## 1. パッケージと版

| 事実 | 出典 | 検証日 |
| --- | --- | --- |
| パッケージ名 `io.github.naoking1993.claude-creator-tools`、表示名 Claude Creator Tools、版 0.1.0、`unity` 2022.3、依存なし | `P/package.json` | コード読解 2026-10-25 |
| `cct_environment` などが返す `tool_version` は `CctRunner.PackageVersion` = "0.1.0" | `P/Editor/Core/CctRunner.cs` | コード読解 2026-10-25 |
| MCP ツールは8つ：cct_environment、cct_avatar_audit、cct_world_audit、cct_capture_views、cct_snapshot、cct_animator_inspect、cct_dependency_report、cct_world_gimmicks | `P/Editor/Mcp/*Tool.cs`、`compile.sh` の METACHECK 出力「8 tools」 | クラウド検査 2026-10-25 |
| MCP 用のアセンブリ `Naoking.ClaudeCreatorTools.Mcp` は `com.coplaydev.unity-mcp` 9.7.1 以上のときだけ `CCT_UNITY_MCP` が定義されてコンパイルされる。本体 `Naoking.ClaudeCreatorTools.Editor` は MCP・Newtonsoft・VRChat SDK に依存しない | `P/Editor/Mcp/Naoking.ClaudeCreatorTools.Mcp.asmdef`（versionDefines・defineConstraints） | コード読解 2026-10-25 |
| 全ツールの引数は必須が先・任意が後（MCP for Unity 9.7.1 のサーバーは逆順だと登録に失敗する）。必須は `cct_snapshot` の `action` だけ | `compile.sh` の METACHECK、`P/Editor/Mcp/CctSnapshotTool.cs` | クラウド検査 2026-10-25 |
| Roslyn C# 9・Unity 2021.1 の参照 DLL・MCP for Unity のスタブでコンパイルが通る（COMPILE OK） | `compile.sh` | クラウド検査 2026-10-25 |
| 読み取る VRChat SDK のフィールドが SDK 3.10.5 の実 DLL にある：CctAvatarAudit 114、CctAnimatorInspect 83、CctWorldAudit 83、CctWorldGimmicks 4、CctCaptureViews 3（FIELDCHECK OK） | `tests/vrc-fields/*.txt` と `FieldCheck.exe` | クラウド検査 2026-10-25 |
| ワールドギミックのサンプル8本が U# の静的検査を通る（USHARP CHECK OK、0 errors） | `check_usharp.py`、`P/Samples~/WorldGimmicks/Scripts` | クラウド検査 2026-10-25 |
| Unity Editor 上での実行、ユーザーのプロジェクトでの動作 | — | **未実測** |

## 2. MCP の経路（呼び方・登録・時間・定義）

| 事実 | 出典 | 検証日 |
| --- | --- | --- |
| MCP for Unity 9.7.1 のサーバーが作る入力スキーマには `[ToolParameter]` の説明が入らない（名前・型・必須・既定値だけ）。そのため各ツールの Description に全引数を書いてある | MCP調査 §0-2・§3.3、`P/Editor/Mcp/CctMcpBridge.cs`（CommonParamsDoc） | 資料読解 2026-10-24 |
| 9.7.1 のカスタムツール登録を Python 3.14 で動かすと、引数を持つツールは単独 tool として登録できない（`KeyError: '<引数名>'`）。上流 main では修正済み | MCP調査 §8（再現試験） | 資料読解 2026-10-24 |
| ユーザーのサーバーは `9.7.1+fastmcp4.2`（ローカル改修版）、FastMCP 4.0.10、MCP SDK 2.2.0、Python 3.14.5、HTTP `127.0.0.1:8080`、起動引数に `--project-scoped-tools` | `S/unity-mcp-operations/references/verified-facts.md`（2026-09-28〜29 の記録） | 記録の写し（現在は未実測） |
| 改修版が Python 3.14 の登録の修正を含むか、つまり cct が単独 tool として出るか | — | **未実測** |
| `execute_custom_tool(tool_name, parameters)` は `--project-scoped-tools` のときだけ登録され、スキーマ検証も既定値の補完もしない | MCP調査 §2.2、サーバーの `services/tools/execute_custom_tool.py` | 資料読解 2026-10-24、コード読解 2026-10-25 |
| HTTP では既定 30 秒で打ち切る。引数の `timeout_seconds`（または `timeoutSeconds`）があれば、Unity 側の上限は max(30, min(値, 3600))、サーバーの待ちはそれに 5 秒足した値 | サーバーの `transport/plugin_hub.py`（send_command_for_instance） | コード読解 2026-10-25 |
| 打ち切られた呼び出し：まだ始まっていなければ Unity 側で取り消され、始まっていれば最後まで動く（同期ハンドラーは中断できない） | MCP調査 §4 | 資料読解 2026-10-24 |
| HTTP の打ち切りは、サーバーが例外を受けて `success: false` と `hint: "retry"` を返す形になる | サーバーの `transport/unity_transport.py`（send_with_unity_instance） | コード読解 2026-10-25（改修版では**未実測**） |
| Claude Code は HTTP の MCP 呼び出しから5分間何も返らないと中断するので、`timeout_seconds` は 240 以下にする | `P/Editor/Mcp/CctMcpBridge.cs`（CommonParamsDoc）、`P/README.md`「時間の上限」（出典は Claude Code のドキュメント） | 記載の写し 2026-10-25（ユーザーの環境では**未実測**） |
| 既存ツールの Description・引数を変えても、サーバーを再起動するまで古い定義が残る（ログ `already registered with a different schema; keeping existing definition.`） | MCP調査 §7、サーバーの `services/custom_tool_service.py` | コード読解 2026-10-25 |
| ツールの有効・無効は EditorPrefs `MCPForUnity.ToolEnabled.<ツール名>` で、PC の全プロジェクトに共通。無効なツールを呼ぶと `Tool '<name>' is disabled in the Unity Editor.` | MCP調査 §7、Unity 側 `TransportCommandDispatcher.cs` | 資料読解 2026-10-24 |
| カスタムツールの戻り値は JSON の文字列で、画像は返せない。cct は PNG を保存して絶対パスを返す | MCP調査 §5.4、`P/Editor/Tools/CctCaptureViews.cs` | 資料読解 2026-10-24 |
| 失敗時、MCP for Unity の `ErrorResponse` は外側の `code` と `error` に同じ理由の文字列を入れる。サーバー 9.7.1 は応答を `MCPResponse{success, message, error, data, hint}` に詰め替えるので、Claude に届く外側には `code` が無く、`message` は null。cct の失敗コードは `data.code`、理由は `error` と `data.message` | MCP調査 §5.1、サーバーの `services/custom_tool_service.py`（_normalize_response）・`models/models.py`（MCPResponse）・`models/unity_response.py`、`P/Editor/Mcp/CctMcpBridge.cs`、`P/Editor/Core/CctResult.cs` | コード読解 2026-10-25（改修版では**未実測**） |
| Claude Code での tool 名が `mcp__UnityMCP__cct_*` になるか（サーバーの登録名 `UnityMCP` は記録から） | `S/unity-mcp-operations/references/verified-facts.md` | 推定・**未実測** |
| 一覧の変更通知が改修版サーバーから Claude Code に届き、新しいツールが自動で出るか | — | **未実測** |

## 3. 出力の形と場所

| 事実 | 出典 | 検証日 |
| --- | --- | --- |
| 要約（`detail: "summary"`）は success・message・code（失敗時）・issue_counts・issues（error→warning→info、既定30件）・issues_omitted・ツールの SummaryKeys のキー・report_path | `P/Editor/Core/CctResult.cs`（ToCompact）、`P/Editor/Mcp/*Tool.cs` | コード読解 2026-10-25 |
| どのツールの要約にも `tool`・`tool_version`・`elapsed_ms`・`details`・`candidates` が残る（ツールの SummaryKeys に加えて）。候補は `cct_avatar_audit`・`cct_animator_inspect` が `candidates`、`cct_capture_views` が `details.candidates`、`cct_dependency_report` が `details` | `P/Editor/Mcp/CctMcpBridge.cs`（AlwaysKeptKeys）、各ツールの Fail の呼び出し | コード読解 2026-10-25（同じ日の途中まではこの2キーが一部のツールで要約から落ちていた。コミット 37dfab7 で直った後の状態） |
| `cct_capture_views`・`cct_dependency_report` は、対象の指定に複数が当てはまると `AMBIGUOUS_TARGET` で失敗する（それ以前は `TARGET_NOT_FOUND`） | `P/Editor/Tools/CctCaptureViews.cs`、`CctDependencyReport.Dependencies.cs`・`.SceneReferences.cs` | コード読解 2026-10-25（コミット 52342d7 で確定） |
| レポートは `<プロジェクト>/Library/ClaudeCreatorTools/Reports/<ツール名>-<yyyyMMdd-HHmmss-xxxx>.json`、UTF-8（BOM なし）、整形 JSON（数値・文字列だけの配列は1行）。失敗した呼び出しでも保存する（save_report が true のとき） | `P/Editor/Core/CctPaths.cs`（CctReport.Save）、`P/Editor/Core/CctRunner.cs`、`P/Editor/Core/CctJson.cs` | コード読解 2026-10-25 |
| 同じ名前のレポート・TSV があると末尾に `-2` などを付けて上書きを避ける | `P/Editor/Core/CctPaths.cs`（UniqueFilePath） | コード読解 2026-10-25（同じ日の後半に足された変更。配布版に入っているかは `P/Editor/Core/CctPaths.cs` で確かめる） |
| 1コードあたりの issue の上限と残りの出し方：avatar_audit・animator_inspect は25件＋同じコードの「ほかに N 件」、world_audit は20件＋`ISSUES_TRUNCATED`、dependency_report は100件＋`ISSUES_CAPPED` と `issues_capped` | `P/Editor/Tools/CctAvatarAudit.cs`（FlushSuppressed）、`CctAnimatorInspect.cs`（FlushOverflow）、`CctWorldAudit.cs`（FinishIssues）、`CctDependencyReport.cs`（FlushIssueCaps） | コード読解 2026-10-25 |
| 撮影は `Library/ClaudeCreatorTools/Captures/<id>_<label>/`、スナップショットは `<プロジェクト>/ClaudeCreatorTools/Snapshots/<id>/`（Assets の外、Library の外） | `P/Editor/Core/CctPaths.cs` | コード読解 2026-10-25 |
| 返すパスは絶対パスで区切りは `/`。撮影フォルダーの label は日本語を残し、使えない文字・空白・`%` を `_` にして40字で切る | `P/Editor/Core/CctPaths.cs`（Normalize・SafeName） | コード読解 2026-10-25 |
| Claude Code の Read・Grep が、日本語を含む `/` 区切りの Windows パスのレポート・PNG を開けるか | — | **未実測** |
| 深い日本語のプロジェクトパスで撮影の比較画像を書けるか（Windows の長いパス） | — | **未実測** |

## 4. ツールごとの要点

| 事実 | 出典 | 検証日 |
| --- | --- | --- |
| `cct_avatar_audit` は Avatars SDK があれば SDK 自身の計算（`AvatarPerformance.CalculatePerformanceStats`）をリフレクションで呼ぶ。使えなければ SDK 3.10.5 の規則を再実装した推定（全項目 `estimate: true`） | `P/Editor/Tools/CctAvatarAudit.cs` 冒頭コメント | コード読解 2026-10-25 |
| ランクの表は SDK 3.10.5 の StatsLevels（PC・Quest）と同じ。Android と iOS は同じモバイルの表 | `P/Editor/Tools/CctAvatarAudit.Ranks.cs` | コード読解 2026-10-25 |
| `upload.blocked` は、アクティブなビルドターゲットで SDK 3.10.5 のビルダーが止める error のコード（UploadBlockingCodes）があるか。MISSING_SCRIPT などは止まらない error として別に出す | `P/Editor/Tools/CctAvatarAudit.Output.cs` | コード読解 2026-10-25 |
| `post_build` は一時クローンをプレビューシーンで NDMF の `AvatarProcessor.ProcessAvatar` に通す（生成アセットは保存しない）。VRCFury と SDK のビルド処理は通らない。Play 中は使えない | `P/Editor/Tools/CctAvatarAudit.PostBuild.cs` 冒頭コメント | コード読解 2026-10-25 |
| `cct_world_audit` の `build_blockers` に数えるコードは18個（SdkBuildBlockerCodes）、`upload_blockers` は BUNDLE_TOO_LARGE_MOBILE | `P/Editor/Tools/CctWorldAudit.Gimmicks.cs` | コード読解 2026-10-25 |
| `cct_capture_views` の isolated はスクリプトを外したクローンをプレビューシーンで中立の照明で描き、ライトマップの参照を外す。比較の「変化」はどれかのチャンネルが 8 階調を超えて違う画素 | `P/Editor/Tools/CctCaptureViews.Session.cs`・`.Images.cs` | コード読解 2026-10-25 |
| `cct_capture_views` の isolated で小物だけを対象にすると、クローンは小物の階層だけになり手は写らない | `P/Editor/Tools/CctCaptureViews.Session.cs`（InitIsolated は対象を Instantiate） | コード読解 2026-10-25 |
| `cct_snapshot` の restore は書く前に今の状態を自動で退避する（pre-restore）。追加されたファイルは消さない | `P/Editor/Tools/CctSnapshot.cs` 冒頭コメント | コード読解 2026-10-25 |
| `cct_animator_inspect` の走査は `timeout_seconds × 0.6`（最低 8 秒）で止まる | `P/Editor/Tools/CctAnimatorInspect.cs`（BudgetMs） | コード読解 2026-10-25 |
| `cct_animator_inspect` に2つの Controller を直接比べるモードは無い | `P/Editor/Mcp/CctAnimatorInspectTool.cs`（引数に比較が無い） | コード読解 2026-10-25 |
| `cct_animator_inspect` の検索結果は `find.parameter`（`declared_in`・`expression`・`builtin`・`uses`）と `find.binding`（`matches`）に分かれる。`find_parameter` は大文字小文字を区別し、`find_binding` は区別しない | `P/Editor/Tools/CctAnimatorInspect.Bindings.cs`（RunFind・FindParameter・FindBinding） | コード読解 2026-10-25（【訂正】それまで `find.uses` と書いていた） |
| `cct_animator_inspect` のレポートの `graph` では、`speed` は 1 以外のときだけ、`speed_param`・`motion_time_param`・`mirror_param`・`cycle_offset_param` はパラメーターが有効なときだけキーが入る | `P/Editor/Tools/CctAnimatorInspect.Graph.cs` | コード読解 2026-10-25 |
| `cct_dependency_report` は書き出し済みの `.unitypackage` の中身を読まない | `P/Editor/Tools/CctDependencyReport.cs` 冒頭コメント（3つの mode だけ） | コード読解 2026-10-25 |
| `cct_dependency_report` の走査は `timeout_seconds - 5` 秒（既定 25 秒）で止まり、`conclusive: false` を返す | 同上 | コード読解 2026-10-25 |
| `cct_dependency_report` に存在しない `export_root`・`search_root` を渡すと `INVALID_ARGUMENT` で失敗する（内外の判定を省くのは、`export_root` も `assets` のフォルダーも無いときだけ：`NO_EXPORT_ROOT`） | `P/Editor/Tools/CctDependencyReport.Dependencies.cs`・`.Reverse.cs` | コード読解 2026-10-25（【訂正】それまで「フォルダーが無ければ判定しない」と書いていた） |
| `cct_capture_views` と `cct_dependency_report` の `scene_references` は、`target` を省いて何も選んでいないと `TARGET_NOT_FOUND`（アバターを自動では選ばない。自動で選ぶのは `cct_avatar_audit`・`cct_animator_inspect` だけ） | `P/Editor/Core/CctTargets.cs`（Resolve）、`P/Editor/Tools/CctCaptureViews.cs`・`CctDependencyReport.SceneReferences.cs`、`CctAvatarAudit.cs`（ResolveAvatar）、`CctAnimatorInspect.Targets.cs` | コード読解 2026-10-25 |
| Tools メニューの「Assets/ClaudeWorldGimmicks にインポート」は `confirm: true` で呼ぶ（ドライランなし） | `P/Editor/Menu/CctWorldGimmicksMenu.cs` | コード読解 2026-10-25 |
| メニューの名前は `Tools/Claude Creator Tools/<機能> (cct_…)/<項目>` の形（例「キャプチャ (cct_capture_views)/選択中を4方向」）。例外はワールドギミックで、`ワールドギミック (UdonSharp)/<項目>` | `P/Editor/Menu/*.cs` | コード読解 2026-10-25（同じ日に名前が変わった後の状態） |
| `cct_avatar_audit`・`cct_world_audit` は `platform` が all・pc・android・ios 以外なら `INVALID_ARGUMENT` で失敗する（黙って all にしない） | `P/Editor/Tools/CctAvatarAudit.cs`・`CctWorldAudit.cs`（Run） | コード読解 2026-10-25 |
| `cct_environment` は Editor.log のエラー、MCP サーバーの版、MCP for Unity の DLL を読まない | `P/Editor/Tools/CctEnvironment.cs` | コード読解 2026-10-25 |
| 各ツールの1回の所要時間（ユーザーのアバター・ワールドで 30 秒に収まるか） | — | **未実測** |
| 撮影の色・明るさが、ユーザーの PC（RTX 5080、Windows 11）の Game ビューと一致するか | — | **未実測** |

## 5. 専門スキルとの組み合わせの根拠

組み合わせは、同期されたユーザーのスキルの記述と照合した（2026-10-25）。

| 組み合わせ | スキル側の根拠 | 検証日 |
| --- | --- | --- |
| optimization：作業前の性能値と画像、比較条件をそろえる、共有アセットもコピー | `S/vrchat-avatar-optimization/SKILL.md`「対象と比較条件を決める」「完了の判断」 | 照合 2026-10-25 |
| material-tuning：同条件の撮影、承認状態を基準に残す、`scripts/compare_regions.py`、Blueprint ID を保つ | `S/vrchat-avatar-material-tuning/SKILL.md`、`references/validation-and-export.md`「比較撮影」「数値比較」 | 照合 2026-10-25 |
| expression-debugging：制御の鎖、統合後の Controller を見る、Observed と Verified fix を分ける | `S/vrchat-expression-debugging/SKILL.md` 1〜2節・Scope and evidence | 照合 2026-10-25 |
| gimmick-packaging：成功判定①〜④、名前で探す参照は依存一覧に出ない | `S/vrchat-gimmick-packaging/SKILL.md`「成功判定を段階ごとに行う」「素材と依存の境界を決める」 | 照合 2026-10-25 |
| hand-prop-setup：Missing Script を子まで数え GUID を照合、指の割り当て、発光 OFF・握り姿勢・近くから・側面から | `S/vrchat-hand-prop-setup/SKILL.md`「欠落を見逃さない」「見た目と永続化を検証する」 | 照合 2026-10-25 |
| hand-prop-setup では `face` プリセットは手元の確認に合わない（頭部の構図）。両手と側面は turnaround・upper、近接は scene | 上の記述と `P/Editor/Tools/CctCaptureViews.cs`（face_* は常に頭部の構図） | 照合 2026-10-25 |
| bpm-controls：MA Merge Animator などが参照する Controller から State の speed・speedParameter・timeParameter と Motion を読み、元製品・汎用版・個体専用コピーを比べる | `S/vrchat-bpm-controls/references/implementation.md` L21・L60・L70、`S/vrchat-bpm-controls/SKILL.md` L26 | 照合 2026-10-25 |
| unity-mcp-operations：原則2の破壊的操作の前の確認、MCP の削除は Undo に載らない | `S/unity-mcp-operations/SKILL.md` 原則2、`references/verified-facts.md`「実測挙動メモ」 | 照合 2026-10-25 |
| vrchat-world-creation：`cct_world_audit`・`cct_world_gimmicks`・`cct_capture_views` の scene モード・`cct_snapshot` | `claude-skills/vrchat-world-creation/SKILL.md`「作業の流れ」（同じリポジトリ、2026-10-25 作成） | 照合 2026-10-25 |

## 6. ユーザーの PC で確かめること（未実測の一覧）

初めて cct を使うセッションで、次を確かめて上の表の「未実測」を書き換える。手順と合格の見え方はパッケージの README の「初回確認」にもある。

1. ツール一覧に `cct_*` が単独 tool として出るか。出なければ `execute_custom_tool` で `cct_environment` が呼べるか。
2. サーバーログに `Failed to register custom tool 'cct_…'` が出ているか。
3. Window > MCP for Unity > Tools の Custom Tools 欄に8つが On で並ぶか。
4. `cct_environment` の `project_root` に日本語が正しく出るか、`report_path` を Read で開けるか。
5. 小さいアバターで `cct_avatar_audit`（`method.stats` が sdk か）と `cct_capture_views`（`contact_sheet` を開けるか、写っているか）の所要時間。
6. `cct_snapshot` の create → diff を無害なアセット1つで往復し、Assets に一時フォルダーが残らないか。
7. パッケージを更新したとき、サーバーの再起動なしで説明が変わらないことと、再起動で変わること。

## 7. 還流先

- Unity MCP の接続・tool 一覧・既知エラーの事実の正は `unity-mcp-operations` の verified-facts。cct が単独 tool として出たかどうかは、あちらの「tool一覧（実測）」にも書く。
- MCP サーバー（Python・FastMCP）の改修の事実は `unity-mcp-maintenance`。
- VRChat SDK の恒久的な事実（ランクの表、上限）は `vrchat-unity-version-check` が正。
- パッケージのコードと配布（VPM リスティング）は claude-unity-vpm リポジトリが正。このスキルの記述とコードが食い違ったら、コードに合わせてこのスキルを直す。
