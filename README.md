# oscdesk-unity

[OscDesk](https://github.com/Hidano-Dev/oscdesk)(LAN 内のブラウザ・スマートフォンから Unity を操作する OSC コントロールサーフェス)の、Unity 側の汎用の受け口です。UPM パッケージ `com.hidano.oscdesk`(`Packages/com.hidano.oscdesk/`)と、それを検証する Unity プロジェクト(リポジトリ直下)を置きます。

> **セキュリティ前提**: OscDesk と受け口は認証・暗号化を提供しません。信頼できる LAN 内でだけ使い、インターネットや信頼できないネットワークへ公開しないでください。

oscdesk リポジトリの `OscSurface/` から、履歴ごと切り出しました(oscdesk の `DESIGN.md` D-136、HID-460)。

## oscdesk との分担

- **Unity との契約**(`/sys/*`・エコー・ping/pong・型・Catalog)の正典は oscdesk の [`docs/UNITY_PROTOCOL.md`](https://github.com/Hidano-Dev/oscdesk/blob/main/docs/UNITY_PROTOCOL.md) です。契約を変えるときは oscdesk 側の PR で文書を直してから、このリポジトリで受け口を直します
- **言語間の見本**(`protocol/osc-binary-samples.json`・`protocol/catalog-samples.json`)の正も oscdesk です。このリポジトリは `Packages/com.hidano.oscdesk/Tests/Editor/` に写しを持ち、EditMode テストは写しを読みます(利用側のプロジェクトには oscdesk が無いため)
- ブリッジ・UI・mock-unity・手動検証の手順(oscdesk の `docs/VERIFICATION.md`)は oscdesk にあります

## 構成

| パス | 内容 |
|---|---|
| `Packages/com.hidano.oscdesk/` | 受け口の UPM パッケージ(使い方はその `README.md`) |
| `Packages/manifest.json` | 検証用プロジェクトの依存。`testables` でパッケージのテストを Test Runner と CI に出す |
| `Assets/` `ProjectSettings/` | 検証用プロジェクト(Unity 6000.0.36f1) |
| `scripts/check_package.py` | パッケージの検査(見本の写しの一致・`.meta`・版と CHANGELOG) |
| `docs/UPM_PACKAGE.md` | 版の付け方と npmjs への公開の手順 |
| `.github/workflows/unity-ci.yml` | Unity CI(セルフホストランナー) |

## 利用側からの参照

npmjs のスコープ付きレジストリから入れる方法と、git の URL で入れる方法があります(パッケージの `README.md`「導入」)。git の URL は次のとおりです。

```json
"com.hidano.oscdesk": "https://github.com/Hidano-Dev/oscdesk-unity.git?path=Packages/com.hidano.oscdesk"
```

以前の `https://github.com/Hidano-Dev/oscdesk.git?path=OscSurface/Packages/com.hidano.oscdesk` で参照していたプロジェクトは、`Packages/manifest.json` の URL を上のものに書き換え、Unity で開いて `Packages/packages-lock.json` を更新させてからコミットします。`#<ref>` で版を固定していた場合は、このリポジトリのタグかコミットに読み替えます(切り出しでコミットの SHA が変わったため、oscdesk の ref はこのリポジトリに無い)。

## EditMode テストの実行

C# の変更は、少なくとも EditMode テストを実行して結果を確かめます。

### Editor で

1. Unity 6000.0.36f1 でリポジトリ直下を開き、インポートとコンパイルが完了するまで待つ
2. **Window > General > Test Runner** の **EditMode** タブで `OscDesk.Receiver.Tests` が出ることを確かめ、**Run All** を押して全件緑を確かめる。失敗したらテスト名・Console の例外・Editor のバージョンを記録する

### batchmode で

**同じプロジェクトを Editor で開いていると batchmode は起動できない**(`HandleProjectAlreadyOpenInAnotherInstance`)ので、事前に Editor を閉じます。

```powershell
# 1. コンパイルのみ(エラーがあればログに error CS として出る)
& "<Unity 6000.0.36f1>\Editor\Unity.exe" -batchmode -quit -nographics `
  -projectPath "<このリポジトリのクローン>" -logFile "<絶対パス>\compile.log"

# 2. EditMode テスト(結果は NUnit 形式の XML に出る)
& "<Unity 6000.0.36f1>\Editor\Unity.exe" -batchmode -nographics `
  -projectPath "<このリポジトリのクローン>" `
  -runTests -testPlatform EditMode -testResults "<絶対パス>\results.xml" -logFile "<絶対パス>\tests.log"
```

- **`-runTests` に `-quit` を付けない**。付けるとテストの前に終了し、結果 XML が無いまま終了コード 0 が返る
- `-testResults` / `-logFile` は絶対パスで指定する
- 結果は XML の `test-run` 要素(`total` / `passed` / `failed`)で確かめる。テストが失敗すると終了コードは 2
- 初めて開くクローンでは `Library/` の生成に時間がかかる

## パッケージの検査と見本の同期

```powershell
py -3 scripts/check_package.py           # Windows(Linux / macOS は python3)
```

見本の写しを oscdesk の `main` の `protocol/` と比べ、`.meta` の揃いと版と CHANGELOG の一致を確かめます(標準ライブラリだけを使う)。Unity CI の最初の段でも同じものを実行します。

oscdesk 側で見本が変わったら(CI のこの段が「写しと違う」で落ちたら)、写し直してコミットします。

```powershell
py -3 scripts/check_package.py --sync                          # oscdesk の main から写す
py -3 scripts/check_package.py --sync --upstream <oscdesk のクローン>   # 手元のクローン(未マージのブランチなど)から写す
```

見本が変わるのは契約や Catalog の規則が変わったときなので、写しを更新した後は EditMode テストも実行します。

## Unity CI

`.github/workflows/unity-ci.yml` が、同じリポジトリからの PR と `main` への push で、パッケージの検査と EditMode 全件を実行し、結果 XML とログを artifact `unity-editmode-results` に残します。fork からの PR では起動しません。

ランナー側の前提(ラベル `linux-unity` のセルフホストランナー):

- 環境変数 `UNITY_EDITORS_ROOT`(例: `~/Unity/Hub/Editor`)の下に `6000.0.36f1/Editor/Unity` がある
- `xvfb-run` と `python3` が使える(Unity は `-nographics` でもディスプレイを要求するため仮想 X で包む)
- Unity の Personal ライセンスは Hub でログイン済み

## ライセンス

まだ決めていません(`docs/UPM_PACKAGE.md`「初回だけ」)。
