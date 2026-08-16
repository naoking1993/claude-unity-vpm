---
name: token-discipline
description: Claude Codeのトークン消費を抑えつつ精度を保つ運用ルール。ユーザーが「トークン」「節約」「制限」「コンテキスト」「/clear」「/compact」「effort」「モデル切替」に言及したとき、長時間タスクや大量ファイル調査を始めるとき、またはコンテキスト残量が減ってきたときに使用。
---

# トークン節約と精度維持の運用規律

参照記事4本（末尾）から抽出した運用ルール。トークン削減と精度向上は対立しない——
**コンテキストを汚さないことが、そのまま精度を上げる**。

## 1. コンテキスト管理（/clear と /compact の使い分け）

- **タスクが変わったら /clear**。前タスクの残骸は毎ターン再送される固定費であり、
  無関係な文脈は精度も下げる。
- 同一タスクの続きで履歴が長い場合のみ `/compact <残したい観点>` を使う。
  無指示の /compact は要約で重要情報が落ちることがある。
- /clear の前に `/handoff` コマンドで作業状態を `.claude/notes/handoff.md` に保存し、
  次セッション冒頭でそれだけを読んで再開する（履歴全体より圧倒的に安い）。
- `/context` で内訳、`/usage` で消費状況を随時確認する。

## 2. 思考レベル（effort）の調整

- 単純作業（リネーム、typo修正、定型編集）は `/effort low〜medium` に下げる。
- 設計判断・難しいデバッグのみ high 以上を使う。
- デフォルトが高い effort になっていないか、セッション開始時に一度確認する。

## 3. モデルの使い分け

- 情報整理・下調べ = Haiku、実装の本作業 = Sonnet、設計レビュー・最終確認 = Opus。
- サブエージェント（Explore など）に調査を任せると、
  中間の大量出力が親コンテキストに入らず結論だけ受け取れる。

## 4. プランモードで手戻りを防ぐ

- 規模のある実装は、先にプランモードで方針を確認してから書く。
- 手戻り（書き直し）は最大のトークン浪費であり、精度低下の主因でもある。

## 5. 入力の絞り込み

- エラーログ・テスト結果は全文を貼らず、**該当行±前後のみ**渡す。
- 構文エラーやスタイル違反は Linter で先に検出し、要点だけを渡す。
- 検索は Grep/Glob を優先し、Read は offset/limit で必要範囲のみ。
- バイナリ・巨大ファイルの丸読みは本リポジトリの hooks が自動ブロックする。

## 6. プロンプトキャッシュを壊さない

- セッション中に CLAUDE.md・.claude/settings.json を書き換えない
  （キャッシュ無効化で 10〜20 倍のコスト差が出る）。
- CLAUDE.md は常時読み込まれる固定費。短く保ち、詳細はスキル（本ファイルのような
  オンデマンド読み込み）へ逃がす。

## 7. 精度のための規律

- 推測で書かない。変更前に対象ファイルの該当箇所を必ず読む。
- 一括変更は 1 件だけ試して結果を確認してから残りに適用する。
- 失敗した操作を同じ引数で盲目的にリトライしない。原因を 1 つ特定してから再試行。

## 8. MCP運用（ツール定義と呼び出し回数）

- **MCPツール定義は毎セッションの固定費**。MCPサーバーはグローバル設定に置かず、
  必要なプロジェクトのスコープ（プロジェクト側の `.mcp.json`）に限定する。
  Unity MCP は Unity プロジェクトでのみ読み込む（このリポジトリでは不要）。
- `/context` で「MCP tools」の占有トークンを確認し、使っていないサーバーは無効化する。
- **多数の個別ツール呼び出しは、1回のバッチ実行に置き換える**。ツール呼び出しの
  中間結果は毎回コンテキストに積もる。大量の同種操作（多数の GameObject・
  マテリアル操作など）は個別の MCP 呼び出しを繰り返さず、スクリプト
  （Unity なら C# エディタスクリプト 1 本）を実行して結果サマリーだけ受け取る。
  中間結果が載らないぶん文脈汚染が減り、精度も上がる。
- 手順が固まった定型の MCP 作業はスキルに落とし、探索的な呼び出しを減らす。

## 参照記事

- [Claude Codeのトークン消費を半減させる5フェーズ運用術（yamato_snow / Zenn）](https://zenn.dev/yamato_snow/articles/8eff833984b842)
- [Claude Code / GitHub Copilot のトークン消費を手軽に削減する2つのツール（rairaii / Qiita）](https://qiita.com/rairaii/items/0ea0ebf709eb00230b93)
- [Claude Codeがすぐ制限に当たる人へ。トークンを減らす使い方まとめ（hantani / note）](https://note.com/hantani/n/n7eac5c4d3e3c)
- [Claude Codeのトークン消費を減らす5つの方法（yurukusa / Qiita）](https://qiita.com/yurukusa/items/435810e1e8a046c99916)
- [Token-Efficient Enterprise Claude Workflows（CData）](https://www.cdata.com/blog/token-efficient-enterprise-claude-workflows)
- [Claude Code SkillsでMCPのトークン消費を削減する（DevelopersIO）](https://dev.classmethod.jp/articles/claude-code-skills-mcp-token-reduction/)
