# oscdesk-unity — OscDesk の Unity 側の受け口

OscDesk(Unity を LAN 内のブラウザから操作する OSC コントロールサーフェス)の、Unity 側の汎用の受け口の UPM パッケージ `com.hidano.oscdesk`(`Packages/com.hidano.oscdesk/`)と、それを検証する Unity プロジェクト(リポジトリ直下)。ブリッジ・UI・契約の文書は [Hidano-Dev/oscdesk](https://github.com/Hidano-Dev/oscdesk) にある。全体の構成と手順は `README.md`、版と公開は `docs/UPM_PACKAGE.md`。

## 対象範囲と境界

- **開発対象**: 受け口のパッケージ(受信アドレス → UnityEvent、pong とエコー、定義 JSON からの行の取り込み、OSCQuery の名前空間の公開と mDNS の告知、同期の合図の口など)と、その検証用プロジェクト
- **開発対象外**: Unity アプリ本体と案件固有の制御(フォーク側で実装する)、ブリッジと UI(oscdesk)
- **セキュリティ前提**: 認証を提供しない。信頼できる LAN 内での利用が前提であり、インターネットや信頼できないネットワークへ公開してはならない

## 絶対規律

- **契約の正は oscdesk**。Unity との契約は oscdesk の `docs/UNITY_PROTOCOL.md`、言語間の見本は oscdesk の `protocol/osc-binary-samples.json`。契約を変える変更は oscdesk 側で文書と見本を先に直す。`Tests/Editor/` の見本の写しを手で書き換えない(`scripts/check_package.py --sync` で写す)
- **特定 Unity OSC ライブラリに依存しない**。OSC 1.0 標準の機能だけで契約を成り立たせる(外部パッケージへの依存を足さない)
- 設計判断の記録は oscdesk の `DESIGN.md`(`D-0xx`)と `docs/DECISIONS.md` に置く(このリポジトリに判断の記録の文書を作らない)
- プロジェクトの Markdown 文書は日本語・UTF-8 で書く

## 作業の進め方

- 作業の起点は Linear の Issue(Hidano チーム / oscdesk プロジェクト)。ブランチ名は `<type>/<issue-id>-<topic>`(英語。例: `fix/hid-123-catalog-digest`)、PR のタイトルは英語で Issue ID を含め、本文に `Fixes <ISSUE-ID>` を書く
- C# を変えたら EditMode テストを実行して結果を確かめ(下記)、パッケージの検査 `py -3 scripts/check_package.py` も通す
- 版を上げるときは `docs/UPM_PACKAGE.md`「版の付け方」に従う

## Unity 操作の注意

- Editor の起動・操作・テストは **Unity CLI(`unity` コマンド)** で行う。uloop は使わない(パッケージも入れない)。CLI が Editor を操作する口は `Packages/manifest.json` の `com.unity.pipeline`(Editor 内で `localhost:7800` に API を開く)。手順は `README.md`「Unity CLI での操作」
  - 起動は `unity open <このリポジトリ> --args "-automated"`(`ProjectVersion.txt` の版の Editor を選ぶ)。起動直後は API が 503 を返したり `unity status` に出なかったりするので、`unity status` に `ready` で出るまで待って再試行する
  - Editor へのコマンドは `unity command "<グループ> <名前>"`(例: `unity command "editor status"`・`unity command "console get"`・`unity command "test run" --mode editor`)。名前は 2 語で 1 つの引数にする。一覧は `unity list`
  - 終わったら `unity command "editor quit"` で閉じる。人が開いている Editor は閉じない
  - Editor を開いていないときの EditMode テストは `unity test <このリポジトリ> --mode EditMode --output <絶対パス>.xml`(batchmode で起動する)
- Editor は `ProjectSettings/ProjectVersion.txt` と同じバージョン(6000.0.36f1)だけを使う。別バージョンで開くと `ProjectVersion.txt` が書き換わり全リインポートが走る。食い違いを見つけても Editor の選択や `ProjectVersion.txt` を勝手に「直さない」で報告する
- C# は少なくとも EditMode テストを実行して結果を確認する。batchmode で Test Runner を使うときは `-runTests` と `-quit` を併用しない(テストが走らずに終了する)。`-testResults` / `-logFile` は絶対パスで指定する(手順は `README.md`「EditMode テストの実行」)
- 同じプロジェクトを開いている Editor があると batchmode のテスト(`unity test` を含む)はプロジェクトロックで止まる。`unity status` で確かめ、開いていれば `unity command "test run"` で代わりに走らせるか閉じてから実行する。人が作業中の Editor は勝手に終了させず、閉じてもらうよう依頼する。テストが走らないことを理由にファイルの手編集や検証の省略に逃げない
- 自動化目的で Editor を GUI 付きで起動するときは `-automated` を渡す(ブロッキングダイアログで止まらないようにする。Unity CLI なら `--args "-automated"`)
- シーン(`.unity`)・プレハブ(`.prefab`)・ScriptableObject(`.asset`)の YAML は手で編集しない。Editor 経由(Unity CLI の `unity command` を含む)かコードからの生成で変更し、手編集が避けられないときはその旨を報告する
- 新規ファイルの `.meta` は可能なら Unity に生成させる。手で書くときは GUID をランダムな 32 桁 hex で新規生成する(規則的なパターンや既存 GUID の流用は禁止)。パッケージの中のファイルとフォルダには必ず `.meta` を付けてコミットする(レジストリから入れたパッケージは読み取り専用で、`.meta` の無いファイルは無視される。`scripts/check_package.py` が検査する)
- `Library/` `Temp/` `Logs/` `UserSettings/` などの生成物はコミットしない。`Packages/packages-lock.json` はパッケージ変更時に更新してコミットする
