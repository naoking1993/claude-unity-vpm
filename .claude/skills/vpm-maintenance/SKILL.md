---
name: vpm-maintenance
description: このVPMリポジトリのパッケージ追加・更新・検証の正確な手順。index.jsonの編集、新しいunity-mcpバージョンの追加、zipのSHA256計算、GitHub Pages配信の確認を行うときに必ず使用。
---

# VPM リポジトリ保守手順

このリポジトリは CoplayDev/unity-mcp の非公式 VPM ミラー。
`index.json` と `com.coplaydev.unity-mcp-<version>.zip` を GitHub Pages
（https://naoking1993.github.io/claude-unity-vpm/）で配信し、VCC / ALCOM から参照される。

## 新バージョン追加手順

1. 公式リリースを確認: https://github.com/CoplayDev/unity-mcp/releases
2. UPM パッケージ一式を zip 化する。**zip 直下に `package.json` が必要**
   （既存 9.7.1 と同じく `package.json`, `Editor/` 等がルート直下。
   サブフォルダに包むと VPM が認識しない）。
3. SHA256 を計算: `sha256sum com.coplaydev.unity-mcp-<version>.zip`
4. `index.json` の `packages["com.coplaydev.unity-mcp"].versions` に
   新バージョンのエントリを追加する:
   - 既存エントリをコピーし `version` / `url` / `zipSHA256` / `dependencies` を更新
   - `version` キーと zip 内 `package.json` の `version` は完全一致必須
   - `url` は `https://naoking1993.github.io/claude-unity-vpm/<zipファイル名>`
   - **旧バージョンのエントリは消さず残す**（利用者のロールバック用）
5. JSON 構文検証: `python3 -m json.tool index.json > /dev/null` または `jq . index.json > /dev/null`
6. commit & push。GitHub Pages 反映後に
   `curl -s https://naoking1993.github.io/claude-unity-vpm/index.json` で配信内容を確認。

## 落とし穴（過去に壊れやすかった点）

- `zipSHA256` の不一致 → VCC でインストール失敗。zip を作り直したら必ず再計算。
- zip の中身確認は `unzip -l <file>` を使う。**zip を Read しない**（hooks がブロックする）。
- `index.json` トップレベルの `url` はこのリポジトリ自身の index.json URL。変更しない。
- ライセンス表記（MIT, © CoplayDev）と `licensesUrl` は維持する。非公式ミラーである旨を
  description に残す。
