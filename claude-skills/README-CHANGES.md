# claude-skills の変更点と登録手順（2026-10-25）

このフォルダーには、アカウントに登録するスキルの作業コピーが入っている。登録は本人が行う（Claude は自分でアップロードできない）。手順は `skill-registration-workflow`（2026-09-29 実測）に従う。

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
| cct_environment / cct_avatar_audit / cct_world_audit / cct_capture_views / cct_snapshot / cct_animator_inspect / cct_dependency_report / cct_world_gimmicks | read（write は cct_snapshot の restore・delete と cct_world_gimmicks の import だけ、confirm=true のとき） | Claude Creator Tools 0.1.0 のカスタムツール | **未実測**。Python 3.14 のサーバーでは単独 tool として登録されず execute_custom_tool 経由になる可能性（MCP for Unity 9.7.1 のソース調査 2026-10-24）。使い方は claude-creator-tools |
```

**3) SKILL.md のステータス行の末尾**（行の最後の `…霧島での実機確認）。**` の、閉じ括弧「）」の直前に足す）

```markdown
 / 2026-10-25 Claude Creator Tools（cct_*）への案内を原則1に、tool一覧に未実測の行を追記
```

実測したら（初めて `cct_*` を呼んだセッションで）、2) の「未実測」を「実測（日付・プロジェクト名）」と、単独 tool として出たか `execute_custom_tool` 経由だったかに書き換える。

## 今回は変えていないもの

- vrchat-* の各スキルの description に `claude-creator-tools` への振り分けを足すかは、まだ決めていない。今は `claude-creator-tools` の側から各スキルへ振り分けている。足すなら、それぞれ改訂として同じ手順で登録し直す。
