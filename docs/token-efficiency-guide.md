# Claude Code トークン効率化キット

このリポジトリの `.claude/` ディレクトリと `CLAUDE.md` は、Claude Code の
**トークン消費を抑えながら精度を上げる**ための仕組みです。
参照記事（末尾）のテクニックを、「人が気をつける」のではなく
**設定として自動で効く形**に落とし込んでいます。

## 仕組みの全体像

| 仕組み | ファイル | 何をするか | 元ネタ |
|---|---|---|---|
| 痩せた CLAUDE.md | `CLAUDE.md` | 毎ターン送られる固定費を最小化。詳細はスキルへ逃がす | yurukusa②, yamato_snow |
| オンデマンド Skills | `.claude/skills/` | 必要なときだけ詳細知識を読み込む。常駐コストゼロで精度を上げる | yamato_snow |
| Read ガード hook | `.claude/hooks/read-guard.sh` | バイナリ・400KB超ファイルの丸読みを自動ブロックし、Grep / offset+limit へ誘導 | yurukusa④, rairaii |
| Bash ガード hook | `.claude/hooks/bash-guard.sh` | `git log -p` など巨大出力コマンドを早期停止し、省トークンな代替を提示 | yurukusa④, rairaii |
| permissions.deny | `.claude/settings.json` | zip / unitypackage / dll / node_modules 等を Read 対象から除外（.claudeignore 相当） | hantani, rairaii |
| /handoff コマンド | `.claude/commands/handoff.md` | /clear 前に作業状態を50行以内のメモへ保存 → 履歴を捨てても文脈を失わない | yamato_snow①, hantani |

### なぜこれで「精度も」上がるのか

- 無関係な過去タスクの履歴・バイナリの文字化け・巨大ログは、トークンを食うだけでなく
  **モデルの注意を散らして精度を下げる**。ブロックと /clear 運用はその汚染を防ぐ。
- `vpm-maintenance` スキルには、このリポジトリ固有の正確な手順
  （zip 直下に package.json 必須、zipSHA256 の再計算、旧バージョン保持など）を
  収録。作業時だけ読み込まれ、推測による事故を防ぐ。
- `/handoff` で決定事項が次セッションへ引き継がれるため、
  /clear しても「なぜそうしたか」が失われない。

## 運用ルール（人がやること）

設定で自動化できない部分は `token-discipline` スキルにまとめてあります。要点:

1. **タスクが変わったら `/clear`**（続きものだけ `/compact <観点>`）
2. **単純作業は `/effort` を下げる**（Opus系はデフォルト effort が高いことがある）
3. **モデル使い分け**: 下調べ=Haiku / 実装=Sonnet / 設計・最終確認=Opus
4. **大きめの実装はプランモードで方針確認してから**
5. **ログは全文貼らず該当行±周辺のみ**。Linter で先に絞る
6. **セッション中に CLAUDE.md / settings.json を書き換えない**
   （プロンプトキャッシュが無効化され 10〜20 倍のコスト差）

## 他プロジェクトへの導入方法

`.claude/` ディレクトリと `CLAUDE.md` をコピーし、CLAUDE.md の中身と
`vpm-maintenance` スキルをそのプロジェクト用に書き換えるだけです。

Unity / VRChat プロジェクトでは `permissions.deny` に以下を足すと効果が大きい:

```json
"Read(**/Library/**)",
"Read(**/Temp/**)",
"Read(**/Logs/**)",
"Read(**/obj/**)",
"Read(**/Build/**)",
"Read(**/*.fbx)",
"Read(**/*.png)",
"Read(**/*.asset)",
"Read(**/*.anim)",
"Read(**/*.controller)"
```

### 注意（Windows）

hooks はシェルスクリプトのため、Git Bash / WSL がある環境で動作します。
ネイティブ cmd 環境で hooks がエラーになる場合は、`settings.json` の
`hooks` セクションを削除してください（`permissions.deny` だけでも主要な
ブロックは効きます）。

## 参照記事

1. [Claude Codeのトークン消費を半減させる5フェーズ運用術（yamato_snow / Zenn）](https://zenn.dev/yamato_snow/articles/8eff833984b842)
   — /clear・/effort・プランモード・入力絞り込み・モデル使い分けの5フェーズ
2. [Claude Code / GitHub Copilot のトークン消費を手軽に削減する2つのツール（rairaii / Qiita）](https://qiita.com/rairaii/items/0ea0ebf709eb00230b93)
   — フックでシェル出力を圧縮する RTK 等のアプローチ
3. [Claude Codeがすぐ制限に当たる人へ。トークンを減らす使い方まとめ（hantani / note）](https://note.com/hantani/n/n7eac5c4d3e3c)
   — トークンが飛ぶ原因（履歴・ソース読み・ログ読み）と /clear・/compact の使い分け
4. [Claude Codeのトークン消費を減らす5つの方法（yurukusa / Qiita）](https://qiita.com/yurukusa/items/435810e1e8a046c99916)
   — effort調整・CLAUDE.md短縮・キャッシュ保護・hooksによる自動制御
