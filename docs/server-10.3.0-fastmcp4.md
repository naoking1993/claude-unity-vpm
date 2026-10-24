# サーバー wheel `mcpforunityserver 10.3.0+fastmcp4.2`（FastMCP 4 / MCP SDK 2 移植）

作成日：2026-10-24。Unity 側パッケージ **10.3.100** と組で使う Python サーバーです。上流 `CoplayDev/unity-mcp` のタグ `v10.3.0`（`aa5fc638`）の `Server/` に、FastMCP 4.0.10 / MCP SDK 2.2.0 で動くための変更を加えてビルドしました。

## 結論

- 上流 10.3.0 のサーバーは FastMCP 3 系専用（`fastmcp<4`）で、FastMCP 4 ではそのまま起動できません（`MiddlewareServerSession` の import で落ちます）。この wheel はそこを直し、FastMCP 4.0.10 / MCP SDK 2.2.0 を固定した版です。
- 手元（Linux）で、上流のテスト（移植版で 1,550 件）、ライブ検証（ソース実行 31 項目、インストール済み wheel 29 項目）、実物の Claude Code CLI 2.1.310 を MCP クライアントにした 4 シナリオがすべて通りました（既知の環境依存テスト 1 件を除く）。**Windows 11 ＋ Unity 2022.3.22f1 の実機は未検証**です（手順は下）。
- Unity を 2 台開いたときの `set_active_instance` は、Claude Code が使うプロトコル（MCP 2026-07-28、1 リクエスト＝1 POST、セッションなし）では**選択を覚えられない**ことが分かりました。この wheel は「覚えられない」ことをその場で返し、`unity_instance` を毎回付ける案内をします。Claude Code は実測でその案内どおりに動きました（下の E2E）。
- 配布物：`server/dist/mcpforunityserver-10.3.0+fastmcp4.2-py3-none-any.whl`（SHA256 は `server/SHA256SUMS`）。ソース差分は `server/patches/10.3.0+fastmcp4.2/`、再現ビルドは `server/build_wheel.sh`。

## 変更の中身

| ファイル | 変更 |
| --- | --- |
| `pyproject.toml` / `uv.lock` | `version = "10.3.0+fastmcp4.2"`、`fastmcp==4.0.10`、`mcp==2.2.0`、ビルドバックエンド `setuptools==84.2.1` を固定（再現ビルドのため）、dev extra に `websockets` |
| `src/transport/mcp_client_registry.py`（新規） | MCP クライアント接続の追跡と `tools/list_changed` の配送。FastMCP 3 時代の `MiddlewareServerSession.__aenter__` へのモンキーパッチの代わり。ハンドシェイク型のクライアント（`Mcp-Session-Id` の HTTP、stdio）は SDK 2 の `Connection` を集めて `send_tool_list_changed()`。2026-07-28 のクライアントには `subscriptions/listen` ハンドラー（SDK の `ListenHandler`）を登録し、`ToolsListChanged` をバスに流す |
| `src/transport/plugin_hub.py` | 上記モンキーパッチの削除。`register_tools` 受信時の通知を registry 経由に |
| `src/transport/unity_instance_middleware.py` | 選択の保存先を判定する `selection_scope()`（下表）。`set_active_instance` / `get_active_instance` / `clear_active_instance` がそれに従う。2026-07-28 のリクエストでは、ルーティング用の一時的な値（`unity_instance` など）をセッションストアではなくリクエスト内にだけ置く（使い捨てのセッション id でストアに 24 時間残るのを防ぐ） |
| `src/services/tools/set_active_instance.py` | 保存先がないリクエストには `success:false, hint:"pass_unity_instance", data.reason:"no_persistent_session"` を返す（解決した id と接続中の一覧つき） |
| `src/transport/unity_transport.py` | 2 台以上で選択がないときのエラー文と `hint` を、保存先の有無で切り替える（`select_instance` ／ `pass_unity_instance`） |
| `src/main.py` | registry の組み込みと終了時のクローズ、`serverInfo.version` にこの wheel の版を出す、HTTP の Host/Origin ガード（ループバックに bind したときだけ `auto`、環境変数 `UNITY_MCP_HTTP_HOST_ORIGIN_PROTECTION=auto|true|false` で上書き）、`instructions` に `pass_unity_instance` の説明を追加 |
| `tests/` | 単体テスト `test_fastmcp4_port.py`、ライブ検証 `integration/live_harness.py` と `integration/test_fastmcp4_live_transport.py` |

上記以外のファイル（ツール・リソース・Unity との WebSocket プロトコル・CLI）は上流 10.3.0 のままです。

### `set_active_instance` の保存先（selection scope）

| クライアントの種類 | 判定 | 保存先 | `set_active_instance` の結果 |
| --- | --- | --- | --- |
| ハンドシェイクあり（`initialize`、HTTP は `Mcp-Session-Id`、stdio は接続ごと） | `Connection.session_id` あり、またはハンドシェイク時代のプロトコル版 | FastMCP のセッション状態（上流と同じ、#1023 の方式） | 成功。`data.selection_scope = "session"` |
| 2026-07-28（HTTP は 1 リクエスト＝1 POST、stdio も 1 リクエストごとに接続を作り直す）で `_meta.client_id` を送る | プロトコル版が 2026-07-28、`client_id` あり | `(user_id, client_id)` ごとの表（24 時間で失効、最大 1,024 件で古いものから削除、プロセス内） | 成功。`data.selection_scope = "client"` |
| 2026-07-28 で `_meta.client_id` なし（**Claude Code 2.1.310 はこれ**） | プロトコル版が 2026-07-28、識別子なし | なし | `success:false`、`hint:"pass_unity_instance"`、`data.reason:"no_persistent_session"`。接続中の一覧と、解決した id を返す |

判定は「通知チャネルの有無」ではなく「プロトコル版」で行います。2026-07-28 の stdio クライアントは通知専用のチャネルを持つものの、接続はリクエストごとに作り直されるためです（レビューで見つかった点。ここを通知チャネルで判定すると、`set_active_instance` が成功と返すのに次の呼び出しで選択が消える）。

- Unity が 1 台だけなら、どの種類でも自動選択されます（上流と同じ）。
- 2 台以上で選択がないとき、ツール呼び出しは `instance_selection_required` を返します。保存先がないクライアントには `hint:"pass_unity_instance"` と「`unity_instance="<Name@hash>"` を毎回付ける」案内、保存先があるクライアントには `hint:"select_instance"`（上流と同じ）を返します。
- `unity_instance` を付けた呼び出しは、どの種類でもそのまま届きます（上流の仕組み）。
- 全クライアント共通の選択へ黙って倒すことはしません（別の Claude Code セッションから同じサーバーを使ったときに、互いの選択を上書きしてしまうため）。
- 以前の `9.7.1+fastmcp4.2`（Codex 版）は、この状況で `Persistent Unity selection requires _meta.client_id on MCP 2026 requests` というエラーだけを返していました。今回は同じ判断のまま、次に何をすればよいかを返すようにしました。

### 通知（`tools/list_changed`）

Unity が `register_tools` を送ると、接続中の MCP クライアントにツール一覧の変更を知らせます。ハンドシェイク型は接続の通知チャネル（HTTP では GET ストリーム）へ、2026-07-28 型は `subscriptions/listen` のストリームへ届きます。Claude Code 2.1.310 は接続直後に `subscriptions/listen` を開いていました（実測）。

### HTTP の Host/Origin ガード

FastMCP 4 の `HostOriginGuardMiddleware` を、ループバック（既定の `127.0.0.1`）に bind したときだけ `auto` で有効にしています。`Host` がループバック以外なら 421、`Origin` がループバック以外なら 403 を返します（DNS リバインディング対策）。同じ PC の Claude Code と Unity Editor には影響しません。

Unity の LAN 公開設定などで `0.0.0.0` / `::` や LAN のアドレスに bind したときは、上流と同じく無効です（このときクライアントは `Host: 0.0.0.0:8080` などで接続してくるため、有効にすると 421 で弾いてしまう）。Unity の WebSocket（`/hub/plugin`）はどちらの場合もガードの対象外です。`UNITY_MCP_HTTP_HOST_ORIGIN_PROTECTION=auto|true|false` で上書きできます。

### 版名を `10.3.100` にしない理由

PyPI に無い番号で「wheel が無ければわざと失敗する」ようにしているのは **Unity 側パッケージ（10.3.100）** です。`GitUrlOverride` が空だと Unity は `mcpforunityserver==10.3.100` を探し、PyPI に無いので起動に失敗します。サーバー wheel は `GitUrlOverride` の `file:///…whl` で直接指定するので、版番号では選ばれません。そのため wheel は、以前の `9.7.1+fastmcp4.2` と同じく「上流の版＋ローカル版ラベル」にしました（`/health` を見れば、上流のどの版を基にした移植かが分かる）。PEP 440 では `==10.3.0` の指定がこの wheel にも当てはまりますが、この wheel は PyPI に置かないので、`==10.3.0` で解決されることはありません。

### 版の履歴

- `10.3.0+fastmcp4.1`：最初のビルド（2026-10-24、ドラフト PR にだけ置いた。使わない）。
- `10.3.0+fastmcp4.2`：独立レビューの指摘を反映。2026-07-28 の stdio クライアントの判定をプロトコル版で行う、2026-07-28 のリクエストでセッションストアに使い捨てのエントリーを残さない、`client_id` の表をユーザーごとに分けて上限と掃除を付ける、Host/Origin ガードを bind 先で切り替える。同じファイル名で中身の違う wheel を作らないため、版を上げた。

## 検証（2026-10-24、Linux、Python 3.13）

| 項目 | 結果 |
| --- | --- |
| 上流 10.3.0 を FastMCP 3.0.2（上流の `uv.lock`）で実行 | 1,518 passed、18 skipped、1 failed（`website/docs` の有無を見るドキュメントのテスト。環境依存で、この移植と無関係） |
| 上流 10.3.0 を**無改変**で FastMCP 4.0.10 で起動 | 起動時に `ImportError: cannot import name 'MiddlewareServerSession'` で停止（テストは通ってしまうので、テストだけでは検出できない） |
| 移植版を FastMCP 4.0.10 / MCP 2.2.0 で実行 | 1,550 passed、20 skipped、同じ 1 failed（新規の単体テスト 32 件と、ライブ検証・単体テスト再実行の 2 件を含む。`mcp` パッケージをスタブに置き換える上流の conftest の影響を受ける 2 件は、スタブ下では skip し、`test_fastmcp4_live_transport.py` が別プロセスの pytest で本物の SDK を使って実行する。conftest で先に本物の SDK を読み込ませる案は、別のテストが 3 件壊れるため採らなかった） |
| ライブ検証 `live_harness.py`（合成 Unity ピア 2 台、ハンドシェイク型・2026-07-28 型の両クライアント、本物の SDK の接続オブジェクトの判定、`0.0.0.0` bind） | ソース実行 31/31、`uvx --from <wheel>` でインストールした wheel 29/29（SDK の接続オブジェクトの判定 2 項目はソース実行のときだけ） |
| Claude Code CLI 2.1.310 を実クライアントにした E2E（`e2e_claude.py`） | 4/4（`4.1` と `4.2` の両方）。(1) Unity 1 台で `read_console` が届く (2) 2 台で id 指定の呼び出しが ProjB に届く (3) `set_active_instance` を先に呼ばせても、`no_persistent_session` の案内に従って `unity_instance` 付きで ProjB に届く (4) id を教えず `mcpforunity://instances` を読んで ProjA に届く |
| 配線の実測（ログ付きプロキシ） | Claude Code は `MCP-Protocol-Version: 2026-07-28`、`Mcp-Method`、`Mcp-Name`、`_meta` に `clientInfo` / `clientCapabilities` / `protocolVersion` を付けて送る。`Mcp-Session-Id` と `_meta.client_id` は**送らない**。接続直後に `server/discover` → `subscriptions/listen` |
| wheel の再現性 | 同じ入力で 2 回ビルドして SHA256 が一致（`SOURCE_DATE_EPOCH=1792800000`、`setuptools==84.2.1`） |

ライブ検証で確かめたこと：本物の SDK の `Connection`（ハンドシェイク HTTP・ハンドシェイク stdio・2026-07-28 HTTP・2026-07-28 stdio）の判定と通知対象、`/health` の `version`、Host/Origin ガードの 421/403、`0.0.0.0` bind ではガードが無効なこと、ハンドシェイク型クライアントの `initialize`〜ツール呼び出し、`mcpforunity://instances`、1 台のときの自動ルーティング、2 台のときの選択必須エラー、`set_active_instance` の保持（セッション単位、別セッションには影響しない）、`unity_instance` による個別ルーティング、2026-07-28 型での `tools/list`・`tools/call`・`resources/read`、`client_id` あり／なしの分岐、`subscriptions/listen` の ack と `tools/list_changed` の到達、GET ストリームへの `tools/list_changed` の到達、ピア切断時の一覧更新、サーバーログに traceback がないこと。

## 未検証・既知の制限

- Windows 11 ＋ Unity 2022.3.22f1 の実機（Unity 10.3.100 の窓なし起動 → この wheel → Claude Code）。
- stdio トランスポート（Unity 側は HTTP 固定なので使わない）。
- リモートホスト（API キー認証）モード。上流の実装のまま。
- `client` スコープの選択はプロセス内の表に置くので、サーバーを再起動すると消えます。Claude Code は `client_id` を送らないため、この経路は現状使われません。
- ハンドシェイク型 HTTP のセッションは、クライアントが DELETE せずに消えると残り続けます（上流と同じ。`session_idle_timeout` は未設定のまま）。Claude Code はハンドシェイク型を使わないので影響しません。
- 上流 10.3.0 の既知の問題はそのままです（例：対話中の Unity を 1 つ閉じると共有サーバーが止まる #1299）。
- テレメトリは上流と同じく既定で有効です（送信先は上流の Coplay）。止めるには、Windows のユーザー環境変数に `UNITY_MCP_DISABLE_TELEMETRY=1` を設定してから Unity を起動します（`--help` に出る `UNITY_MCP_TELEMETRY_ENABLED` はサーバーが読んでいない、上流の表記の誤り）。この PR の検証ツールは、起動するサーバーのテレメトリを必ず止めます。
- `fastmcp` / `mcp` を固定しているため、上流がこれらを上げたときは、この wheel の版も上げ直す必要があります。

## 再現ビルド

```sh
server/build_wheel.sh                      # 上流をクローンして server/patches を当て、SHA256SUMS と照合
server/build_wheel.sh /path/to/unity-mcp   # 既存のクローンを使う
```

Linux/WSL 用です。`git`・GNU `patch`・`uv` が必要で、ビルド依存（setuptools）の取得に PyPI へのアクセスが要ります。

## 導入手順（Windows、未検証）

本番のプロジェクトでは行わず、日本語名のプロジェクトの複製で確認してから本番に移します。

1. **wheel を置く**：`mcpforunityserver-10.3.0+fastmcp4.2-py3-none-any.whl` を、空白や `% & ^ | < >` を含まないフォルダーに置く（例：`C:\Tools\unity-mcp\`）。SHA256 が `server/SHA256SUMS` と一致することを PowerShell の `Get-FileHash` で確かめる。
2. **Unity 側を 10.3.100 にする**（VCC。`docs/upgrade-10.3-assessment.md` の実機確認手順も参照）。
3. **GitUrlOverride を差し替える**：MCP for Unity ウィンドウの Advanced、または EditorPrefs ウィンドウで `MCPForUnity.GitUrlOverride` を `mcpforunityserver@file:///C:/Tools/unity-mcp/mcpforunityserver-10.3.0+fastmcp4.2-py3-none-any.whl` にする（スラッシュは `/`、パーセントエンコードしない）。この値は PC 全体で共有されるので、他のプロジェクトも同じ wheel を使うようになる。元の値（`9.7.1+fastmcp4.2` の wheel）は控えておく。
4. **初回だけネットワークありで起動する**：Unity の Start Server は、`uvx --offline` で起動できるかを 3 秒だけ試し、できなければ通常起動します。この wheel は依存の版が以前と一部違うので、初回は PyPI から取得が走ります（`Library/MCPForUnity/Logs/server-launch-8080.log` に進行が出る）。
5. **確認**
   - `http://127.0.0.1:8080/health` の `version` が `10.3.0+fastmcp4.2`。
   - 日本語名のプロジェクトフォルダーに `Library/MCPForUnity/RunState/mcp_http_8080.pid` ができ、隣に文字化けフォルダーができていない。
   - **Stop Server** で pid が消え、`/health` が落ちる。もう一度 Start で復帰する。
   - `claude mcp list` で Unity が HTTP（`127.0.0.1:8080`）のまま。Claude Code から `read_console` などが通る。
6. **Unity 2 台**：別のプロジェクトも開く。Claude Code に「2 つ目のプロジェクトのコンソールを読んで」と頼み、`mcpforunity://instances` を読む／`set_active_instance` の案内に従って `unity_instance` を付ける動きになること。選択の案内が出ないのに別の Unity に届く、または届かない場合は、サーバーログ（`server-launch-8080.log`）と Claude Code の `--debug` ログを控える。
7. **戻す**：`GitUrlOverride` を控えた値に戻し、Unity 側を VCC で 9.7.101 に戻す。

## 開発者向け：ライブ検証の実行

```sh
# 依存：fastmcp==4.0.10 mcp==2.2.0 httpx websockets（wheel の依存に加えて websockets）
python server/tools/live_harness.py <Server/src のパス または mcp-for-unity 実行ファイル>
python server/tools/e2e_claude.py <同上> <出力フォルダー>      # claude CLI が使える環境で
```

どちらも起動するサーバーのテレメトリを止め、サーバーのログは実行ごとの一時ファイルに書き、失敗したときに末尾を表示します。`e2e_claude.py` は `logproxy.py` を挟んで Claude Code が送る HTTP をそのまま記録し、`report.json` にプロトコル版・ヘッダー・`_meta` のキー・ツール呼び出しの到達先をまとめます。
