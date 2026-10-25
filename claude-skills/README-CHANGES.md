# claude-skills の変更点と登録手順（2026-10-25）

このフォルダーには、アカウントに登録するスキルの作業コピーが入っている。登録は本人が行う（Claude は自分でアップロードできない）。手順は `skill-registration-workflow`（2026-09-29 実測）に従う。

## 改訂（2026-10-25 夜）：claude-creator-tools を Claude Creator Tools 0.1.1 に合わせた

**登録し直しが要る。** アカウントの `claude-creator-tools` を削除してから、新しい zip をアップロードする（手順は下の「登録の手順」の1〜3と5。対象はこの1本だけ）。

- 版の表記を 0.1.1 にした（`tool_version` を含む）。0.1.0 のままのプロジェクトでは、ワールド監査の iwaSync3 の誤検知と EditorOnly の助言に従わないよう案内する。
- `tool-reference.md` の `cct_world_audit` の issue 表を 0.1.1 のコードに合わせた：`MULTIPLE_SCENES_LOADED`（warning/info・出どころ EST・NDMF のプレビューシーンを数えない）、`UI_NO_EVENT_SYSTEM`（info に。`UI_SHAPE_*` の行から分けた）、`NOT_WHITELISTED`（ビルド時に除去される型は info・`reason`・EditorOnly を勧める条件）、`VIDEO_PLAYER_NO_AUDIO`・`STATION_NO_COLLIDER`・`PICKUP_NOT_SYNCED`・`LIGHTMAP_UV_MISSING`・`ANDROID_LARGE_TEXTURE`・`AVATAR_PACKAGES_IN_WORLD_PROJECT` の条件。新しいレポートのキー（`interactables.dynamics.in_scope_shipped`、`scenes.loaded[].ndmf_preview` など）の表を足した。
- `cct_capture_views` の `front` は対象の +Z で、家具・小物では見た目の正面とは限らないこと（ワールドの椅子の実例）を書いた。
- 「Unity 実機で未検証」の記述を、2026-10-25 の実機確認の結果に直した：0.1.0 はアバタープロジェクトとワールドプロジェクトで初回確認に合格、ユーザーのサーバーでは `cct_*` は単独 tool にならず `execute_custom_tool` で呼ぶ、日本語パスの PNG を Read で開けた、所要時間、0.1.1 のワールド監査を1回実行して誤検知が消えたこと（項目ごとの突き合わせは未完了）。台帳の「未実測」の一覧も残りだけにした。
- frontmatter（name・description）は変えていない。

## 入っているもの

| スキル | 種類 | 中身 |
| --- | --- | --- |
| `vrchat-world-creation` | 新規（v0） | VRChat のワールドを Unity 2022.3.22f1 と Worlds SDK 3.10.5 で作る・直す・アップロード前に点検する。Scene Descriptor・スポーン・レイヤー、ライトベイク、ミラー・動画・音、UdonSharp と同期、Quest/Android/iOS、ClientSim と Build & Test、アップロードが止まる原因。一次資料は VRChat 公式ドキュメント（2026-10-20 時点）と SDK 3.10.5。Unity 実機・VRChat 実機では未検証 |
| `claude-creator-tools` | 新規（v0） | 自作パッケージ Claude Creator Tools 0.1.0 の MCP ツール `cct_*` 8種を、どの工程で、どの引数で呼び、結果・レポート・画像をどう読むか。各 vrchat-* スキルと unity-mcp-operations の工程との組み合わせ。引数・出力・issue コードはパッケージのコードから写した。Unity 実機では未検証 |
| `avatar-world-skill-guide` | 改訂 | 冒頭の注記の後に改訂の注記（2026-10-25）を1段落足し、表に `vrchat-world-creation` と `claude-creator-tools` の2行を足し、「ワールド制作専用のスキルはない」という最終段落を書き換えた。ほかの行は変えていない |

`unity-mcp-operations` は変えていない（写しも作っていない）。足すとよい文は下の「unity-mcp-operations への追記案」にある。本人が今登録されている版に足して、同じ手順で登録し直す。

## 登録の前に確かめること

1. **未登録の作業版**：本人の PC や別のセッションに、`avatar-world-skill-guide` の未登録の作業版（「次の登録でまとめる」とした版など）が無いか。あれば中身を照合して統合してから差し替える。確かめずに差し替えると、その版の追記が消える。
2. **ローカルスキルとの重複**：Claude Code ではアカウントのスキルと `~/.claude/skills/` のローカルスキルが同時に読み込まれ、同じ目的のものが両方にあると発火が不安定になる。
   - `world-udon-gimmick`・`world-lighting-bake`（記録にある名前。中身は未確認）は `vrchat-world-creation` と目的が重なる可能性が高い。登録の前にどちらを使うか決め、使わない方を無効化するか統合する。
   - `claude-creator-tools` と同じ目的のローカルスキルは記録に無い。名前に `cct`・`creator-tools` を含むものが無いかだけ確かめる。
3. **Claude Creator Tools 本体**：`claude-creator-tools` は、プロジェクトにパッケージ 0.1.0 が入っていて MCP for Unity 9.7.1 以上があるときに役立つ。パッケージが無くてもスキルは登録してよい（「最初に確かめること」で入っていないと分かる）。

## 登録の手順

1. **zip を作る**（リポジトリのルートで）：

   ```
   python3 tools/build_skill_zips.py
   ```

   `claude-skills/dist/` に `<スキル名>.zip`（中身は `<スキル名>/SKILL.md` と `references/`）と `manifest.json`（各 zip の SHA256）ができる。事前の検査（frontmatter が name と description だけ、name がフォルダー名と同じ、description が1024字以内で `<` `>` を含まない、相対リンクが切れていない）で問題があれば `PROBLEM` と出て終了コード1になる。`dist/` は git に入れない（`.gitignore` 済み）。
2. **渡す**：PC に届くセッションなら `C:\Users\naoki\Desktop\claude-skill-zips-adapted\` に書き出し、チャットにも添付する。PC に届かないクラウドのセッションでは、チャットへの添付だけ。この README-CHANGES.md と `manifest.json` も一緒に渡す。
3. **本人がアップロードする**：claude.ai の「カスタマイズ → スキル → ＋ → スキルをアップロード」。順番は次のとおり。
   1. `vrchat-world-creation`（新規）
   2. `claude-creator-tools`（新規）
   3. `avatar-world-skill-guide`：**先に今の `avatar-world-skill-guide` を削除してから**、新しい zip をアップロードする（同名は差し替え）。新しい表が上の2本を指すので、最後に入れる。
4. **重複の整理**：手順「登録の前に確かめること」2で決めたとおり、ローカルスキルの片方を無効化する。
5. **反映を確かめる**：「アップロードした」と言われたら、同期された manifest（手順書の記載は `/mnt/skills/plugins/manifest.json`。環境によって場所が違い、クラウドのセッションでは同期フォルダーの `manifest.json`）の `updatedAt` と各 SKILL.md の中身（ステータス行）で確かめる。同期は数十秒〜数分遅れるので、直後に古い版が見えても断定しない。版・削除・無効化の結果を表で報告する。
6. **旧版を残す**：差し替える前の `avatar-world-skill-guide` の zip は、ロールバック用に手元に残しておく。

## unity-mcp-operations への追記案（本人が適用する）

今登録されている `unity-mcp-operations` の版に、次の3か所を足す。追記したら `skill-registration-workflow` の手順で zip にして差し替える（このフォルダーには写しを置いていない）。

**1) SKILL.md の原則1の最後の項目の次**（「VRChat作業でSDK/Unityバージョン適合が絡む場合は `vrchat-unity-version-check` を併用する（あちらが正）。」という2行の項目）

```markdown
- Claude Creator Tools（`cct_*`）がプロジェクトに入っている場合、引数・出力の読み方・各工程での使い方は
  **claude-creator-tools** を読む（最初に `cct_environment`）。`cct_*` が tool 一覧に単独で無ければ
  `execute_custom_tool(tool_name="cct_...", parameters={...})` で呼ぶ。破壊的操作の前の退避は `cct_snapshot`（原則2）。
```

**2) references/verified-facts.md の「tool一覧（実測）」の表の最後の行の前**

```markdown
| cct_environment / cct_avatar_audit / cct_world_audit / cct_capture_views / cct_snapshot / cct_animator_inspect / cct_dependency_report / cct_world_gimmicks | read（write は cct_snapshot の restore・delete と cct_world_gimmicks の import だけ、confirm=true のとき） | Claude Creator Tools 0.1.1 のカスタムツール | **実測 2026-10-25（ひかるん・泡ワールド、0.1.0）**：単独 tool としては登録されない（サーバーログ `Failed to register custom tool 'cct_…' globally: '<最初の引数名>'`、Python 3.14 の問題）。execute_custom_tool 経由で呼べた。使い方は claude-creator-tools |
```

**3) SKILL.md のステータス行の末尾**（行の最後の `…霧島での実機確認）。**` の、閉じ括弧「）」の直前に足す）

```markdown
 / 2026-10-25 Claude Creator Tools（cct_*）への案内を原則1に、tool一覧に実測の行を追記
```

2) は 2026-10-25 の実機確認の結果で書き換え済み（それまでは「未実測」の案だった）。

## 今回は変えていないもの

- vrchat-* の各スキルの description に `claude-creator-tools` への振り分けを足すかは、まだ決めていない。今は `claude-creator-tools` の側から各スキルへ振り分けている。足すなら、それぞれ改訂として同じ手順で登録し直す。
