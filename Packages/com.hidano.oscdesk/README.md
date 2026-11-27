# OscDesk Receiver(`com.hidano.oscdesk`)

OscDesk(LAN 内のブラウザ・スマートフォンから Unity を操作する OSC コントロールサーフェス)の、Unity 側の汎用の受け口です。受けた OSC のアドレスをインスペクタで登録した UnityEvent に振り分け、`/sys/ping` に `/sys/pong` を、値には同じアドレスへのエコーを返します。受けられるアドレスは OSCQuery の名前空間として HTTP / WebSocket で公開し、mDNS(`_oscjson._tcp`)で告知します。OSC 1.0 のコーデック・UDP・OSCQuery のサーバー・mDNS の応答側を自前で持ち、外部パッケージには依存しません。

> **セキュリティ前提**: この経路に認証・暗号化はありません。信頼できる LAN 内でだけ使い、インターネットや信頼できないネットワークへ公開しないでください。

契約(アドレス・エコー・ping/pong・型)の正典は oscdesk リポジトリの [`docs/UNITY_PROTOCOL.md`](https://github.com/Hidano-Dev/oscdesk/blob/main/docs/UNITY_PROTOCOL.md) です。使い方(対応表の作り方・Record / Strings の行・定義 JSON の取り込み)は同文書の §5「汎用レシーバ」を参照してください。

## 導入

Unity 6000.0 以降。`Packages/manifest.json` に npmjs のスコープ付きレジストリと依存を足します。

```json
{
  "dependencies": {
    "com.hidano.oscdesk": "1.0.0"
  },
  "scopedRegistries": [
    {
      "name": "Hidano",
      "url": "https://registry.npmjs.com",
      "scopes": ["com.hidano"]
    }
  ]
}
```

レジストリを使わずに git の URL で入れる場合は、`dependencies` に次のように書きます(版を固定するなら末尾に `#<タグかコミット>` を付けます)。

```json
"com.hidano.oscdesk": "https://github.com/Hidano-Dev/oscdesk-unity.git?path=Packages/com.hidano.oscdesk"
```

空の GameObject に `OscReceiver` を追加し、`Listen Port` を OscDesk の `unity.sendPort`(既定 7090)に、`Reply Host` / `Reply Port` を OscDesk を動かす PC : `bridge.oscListenPort`(既定 `127.0.0.1` : 7091)に合わせます。これだけで OscDesk の画面に到達性(RTT)が出ます。

## 構成

| フォルダ | アセンブリ(asmdef) | 中身 |
|---|---|---|
| `Runtime/Core/` | `OscDesk.Receiver`(`noEngineReferences: true`) | `UnityEngine` に依存しない純粋 C#。OSC のコーデック(`OscCodec`)、UDP(`OscUdpTransport`)、振り分け(`OscRouter`・`OscDispatcher`・アドレスのパターン `OscAddressPattern`)、接続確認とエコー(`OscLink`)、対応表の検査(`OscBindingAudit`)、定義 JSON の読み取り(`OscDefinitionReader`)、`OscRecord`、トリガ待ちのアプリで再送を適用する窓(`OscResendWindow`)、OSCQuery(名前空間 `OscQueryTree`・HTTP と WebSocket `OscHttp` / `OscWebSocket`・mDNS `OscMdns`・サーバー `OscQueryServer`)、起動ごとの ID `OscBootId`、同期の合図 `OscSyncTracker`、レコードの帳簿(`OscKeyedBook<T>` / `OscUiRowBook<T>`)と読み取りの補助(`OscRecordReader`)、受け手の登録簿(`OscRegistry`・`OscOperation`・`OscAddressNames`)など |
| `Runtime/` | `OscDesk.Receiver.Runtime` | MonoBehaviour。`OscReceiver`(中核を Unity のフレームと UnityEvent につなぐ)、受け手の基底 `OscTarget` と生成担当 `OscSpawner` |
| `Editor/` | `OscDesk.Receiver.Editor`(Editor のみ) | インスペクタ拡張 `OscReceiverEditor` / `OscTargetEditor`、重複した対象名の付け直し `OscTargetNameDeduplicator`、`[OscAddress]` の宣言からの配線 `OscReceiverWiring` |
| `Tests/Editor/` | `OscDesk.Receiver.Tests`(Editor のみ、`UNITY_INCLUDE_TESTS`) | EditMode テスト。OSC のバイト列の見本 `osc-binary-samples.json`(oscdesk リポジトリの `protocol/` の写し)を同梱 |
| `Tests/PlayMode/` | `OscDesk.Receiver.PlayModeTests`(テストのアセンブリ、`UNITY_INCLUDE_TESTS`) | PlayMode テスト(`OscTarget` の有効化・無効化・破棄と `OscSpawner`) |
| `Tests/Runtime/` | `OscDesk.Receiver.Tests.Fixtures`(テストのアセンブリ、`UNITY_INCLUDE_TESTS`) | EditMode テストが GameObject に付ける受け側の MonoBehaviour(Editor のアセンブリの MonoBehaviour は付けられないため) |
| `Samples~/MinimalReceiver/` | なし(取り込むと `Assets/` の既定のアセンブリに入る) | 最小の受け口シーンと、それに合うサーフェス定義 |

- 型はすべて名前空間 `OscDesk.Receiver` にあります(`using OscDesk.Receiver;`)
- 機能を足すときは、Unity に依存しない処理を `Runtime/Core/`(`OscDesk.Receiver`)に、MonoBehaviour を `Runtime/`(`OscDesk.Receiver.Runtime`)に、エディタだけの補助を `Editor/` に置きます。`Core` は `UnityEngine` を参照できないので、Unity の型が要る処理を `Core` に入れないでください
- 自前の asmdef から参照するときは `OscDesk.Receiver.Runtime`(`OscReceiver` を使う場合)と `OscDesk.Receiver`(`OscRecord` などの中核の型を使う場合)を `references` に足します。asmdef の無いスクリプト(`Assembly-CSharp`)からはそのまま参照できます

## OSCQuery で公開する(D-164)

`OscReceiver` は待ち受けを始めると、Registry に載った行と操作(対応表の行と `OscTarget` の操作)を OSCQuery の名前空間として公開します。OscDesk のブリッジは mDNS でこの PC を見つけ、名前空間からパッドに置ける部品の候補を作ります。

- `Serve Osc Query`(既定オン)・`Osc Query Port`(0 = 空きポートを自動で選ぶ)・`Advertise Mdns`(既定オン)・`Osc Query Name`(空なら製品名。後ろに受信ポートを付ける)
- `http://<この PC>:<ポート>/` で名前空間の JSON、`/?HOST_INFO` で HOST_INFO を返します。属性だけのクエリ(`/mix/level?TYPE` など)は、持たない属性・対応していない属性に 204 を返します。`VALUE` は持ちません
- `HOST_INFO` は `WS_PORT` と `EXTENSIONS` を明示し、`OSC_IP` は書きません(ブリッジは HTTP を取ったアドレスへ送る)。起動ごとの ID(32 桁の 16 進)を独自の拡張 `X_OSCDESK_BOOT_ID` に、同じ値を `/sys/pong` の第 2 引数(`,is seq bootId`)に載せます。Play の開始ごとに新しくなります
- 型は Int → `i`、Float → `f`、String → `s`、Bool → `T`、Trigger → TYPE なし、組み合わせの型 → フィールドの型タグを並べたもの。`ACCESS` は値を持つ操作が 3、きっかけ(Trigger と `AsCommand`)が 2 です。選択肢(`WithOptions`)は `RANGE` の `VALS`、範囲(`WithRange`)は `MIN` / `MAX`、`WithLabel` は `DESCRIPTION` になります。型の無い Record・Strings は OSCQuery で表せないので載せません(警告が 1 回出ます)
- パスは ASCII に限ります(空白と `# * , ? [ ] { }` を除く)。実行時に増減する行は、行ごとのアドレスを ASCII の ID で作り(例 `/member/<ID>/active`)、人が読む名前は `OscTarget.DisplayName`(インスペクタの `Display Name`)に入れると、`/member/<ID>` の段の `DESCRIPTION` に載ります。行の増減は WebSocket の `PATH_ADDED` / `PATH_REMOVED`、属性の変化(選択肢の入れ替えなど)は `PATH_CHANGED` で知らせます
- Windows のネットワークが「パブリック」だと、ファイアウォールで mDNS(UDP 5353)と HTTP が塞がれることがあります。mDNS を始められないときは警告を出し、HTTP は動かし続けます
- 認証はありません。信頼できる LAN の中だけで使ってください

## 同期を受けたときの処理(D-165・D-166)

OscDesk が保持値を送り直すとき(自動の再送・手動の同期)、受信側で生成物を作り直すなどの処理が要ることがあります。受け口は作り直しを行わず、同期の始まりと終わりを知らせる口だけを持ちます。

- `OscReceiver.onSyncBegan` / `onSyncEnded`(UnityEvent)と、C# の `SyncBegan` / `SyncEnded`(`OscSyncSession`。`Reason`・`Messages`(始まりから終わりまでに受けた値の数)・`Completed`)
- 合図のアドレスは **仮置き** の `/sys/sync/begin`(引数は省略可。第 1 引数が文字列なら `Reason`)と `/sys/sync/end` です。oscdesk 側の HID-487 で確定します
- 終わりの合図が `SyncTimeoutSeconds`(既定 10 秒。最後の受信から数える)のうちに来なければ、未完了(`Completed` が false)として閉じます
## トリガ待ちのアプリでの再送

値を受けた時点では保持だけして、適用をトリガ(Update ボタンなど)まで待つアプリでは、OscDesk のブリッジが Unity の再起動・再接続で送り直す保持値にトリガが伴いません。`OscResendWindow` は、再送が起きる契機(Play 開始と、受信の途絶からの再開)にだけ窓を開き、窓の中で届いた値をトリガを待たずに適用してよいかを知らせます。`OscReceiver.HasReceived` / `LastReceivedRealtime` を毎フレーム渡す使い方・既定値の根拠・既知の限界は、`docs/UNITY_PROTOCOL.md` §5「トリガ待ちのアプリでの再送の扱い」を参照してください。

## 関数の登録を宣言する(`[OscAddress]`)

行のイベントに関数を手で登録する代わりに、受ける関数にアドレスを宣言できます。

```csharp
using OscDesk.Receiver;
using UnityEngine;

public sealed class AppHost : MonoBehaviour
{
    [OscAddress("/app/volume")]          // Float の行
    public void SetVolume(float value) { }

    [OscAddress("/app/member/values")]   // Record の行
    public void ReceiveMember(OscRecord record) { }

    [OscAddress("/app/device/keys")]     // Strings の行(UI で管理する行の ID の一覧)
    public void ReceiveDeviceKeys(string[] ids) { }

    [OscAddress("/app/segments/request")] // Trigger の行。1 つの関数に複数のアドレスを宣言できる
    [OscAddress("/app/lipdevices/request")]
    public void SendOptionLists() { }
}
```

このスクリプトを `OscReceiver` と同じ GameObject(かその子)に付け、インスペクタで定義ファイルを設定して **定義から配線([OscAddress] の関数を登録)** を押すと、足りない行を足したうえで、宣言したアドレスの行のうち型が引数と合う行のイベントへ永続リスナーとして登録します。何度押しても同じ関数は足さず、手で登録した関数は消しません。結果(変更と警告)はインスペクタと Console に出ます。

- 引数と行の型: 無し = Trigger、`int` = Int、`float` = Float、`string` = String、`bool` = Bool、`OscRecord` = Record、`string[]` = Strings。関数は public のインスタンスメソッドで、戻り値は void
- 型が合わない・行が無い・定義にない宣言と、宣言も関数も無い行は警告になり、登録しません
- エディタのスクリプトから配線するときは `OscReceiverWiring.WireFromDefinition(receiver, targets, parameters, records)` を呼びます。行を自分で整えてから登録だけを行うなら `OscReceiverWiring.WireListeners(receiver, targets, expected)` です。どちらも `OscWiringReport`(`Changes` / `Warnings`)を返します
- 宣言を消しても、前に登録したリスナーは外しません(インスペクタで Missing と出たら手で外します)

## 実行時に受け手を登録する(`OscTarget` / `OscSpawner`)

拡張パッケージは `OscTarget` を継承し、受け付ける操作を宣言します。有効になると `/パッケージ名/対象名/操作名` のアドレスで `OscRegistry.Shared` に登録し、無効になると外します。対象名はインスペクタの `Target Name` で決め、空なら GameObject 名から `(Clone)` を除いたものです。

```csharp
using OscDesk.Receiver;

public sealed class SparkEffect : OscTarget
{
    protected override string DefaultPackageName => "effect";

    protected override void DeclareOperations(OscOperationList operations)
    {
        operations.Trigger("stop", Stop);                                           // /effect/<対象名>/stop
        operations.Float("rate", SetRate).WithRange(0f, 1f).WithWidget("slider");   // 制約と表示ヒント(D-103)
    }

    private void Stop() { }
    private void SetRate(float value) { }
}
```

- 実行中に同じアドレスが重なったら、後から有効になった方は受け付けられず、Console に警告が出ます。シーンに置いたものどうしの重なりは、シーンの保存時と Play の開始時にエディタが後に並ぶ方の `Target Name` へ `_2` などを足して付け直します(Console とインスペクタに出て、Undo で戻せます)
- `OscSpawner` は `/パッケージ名/対象名/spawn ,s "名前"` で `Prefab` を生成し、生成したものの `OscTarget` をその名前で登録します。`destroy ,s "名前"` で消します。使われている名前・使えない名前では生成しません(失敗の理由 `name in use` / `invalid name`)
- `OscReceiver` は Registry に載った行と操作を OSCQuery の名前空間で公開します(上の「OSCQuery で公開する」)。`WithLabel` / `WithRange` / `WithOptions` の宣言が `DESCRIPTION` / `RANGE` になります
- 新しい型(色・ベクトルなど)は `OscValueType` で「基本型・Record・配列の組み合わせ + 制約 + 表示ヒント」として定義し、`operations.Composite` で受けます(D-121)。値は 1 つのアドレスへフィールドを宣言順に並べた OSC の引数で届き(独自の型タグは使いません)、OscDesk は表示ヒントを知っていれば専用の部品(`color` なら色の選択)、知らなければフィールドごとの部品を出します。今受けられるのは基本型だけの Record です(入れ子・配列は HID-397)

```csharp
private static readonly OscValueType Color = OscValueType.Record("color",
        OscValueField.Of("r", OscValueType.Float().WithRange(0f, 1f).WithDefault(1f)),
        OscValueField.Of("g", OscValueType.Float().WithRange(0f, 1f).WithDefault(1f)),
        OscValueField.Of("b", OscValueType.Float().WithRange(0f, 1f).WithDefault(1f)),
        OscValueField.Of("a", OscValueType.Float().WithRange(0f, 1f).WithDefault(1f)))
    .WithHint("color");

protected override void DeclareOperations(OscOperationList operations)
{
    operations.Composite("tint", Color, value => SetTint(value.GetFloat("r"), value.GetFloat("g"), value.GetFloat("b"), value.GetFloat("a")))
        .WithLabel("Tint");                                                       // /effect/<対象名>/tint ,ffff
}
```
- シーンに `OscReceiver` を 1 つ置けば、受けた OSC は対応表の行と同じく `OscTarget` の操作にも届きます(D-108)。`/effect/spark*/stop` のような OSC 1.0 のパターンで、まとめて操作できます(D-106)。詳細は `docs/UNITY_PROTOCOL.md` §5「実行時の受け手の登録」「Registry 経由の振り分け」と DESIGN.md D-107 / D-108

## サンプル

Package Manager の本パッケージの **Samples** から **Minimal Receiver** を Import すると、`Assets/Samples/` に次が入ります。

- `MinimalReceiver.unity` — `OscReceiver`(待受 7090、返信先 `127.0.0.1:7091`)と、受けた値を Console に出す `MinimalReceiverLogger` を置いたシーン。`/sample/value`(Float)と `/sample/hello`(Trigger)の行を登録済み
- `MinimalReceiver.surface.json` — このシーンに合う OscDesk のサーフェス定義。OscDesk の画面で読み込むと、スライダー(`/sample/value`)とボタン(`/sample/hello`)が出る

シーンを再生して OscDesk(ブリッジと UI)を既定の設定で起動すると、到達性が「接続中」になり、スライダーやボタンの操作が Console に出ます。

## テストを利用側で走らせる

利用側のプロジェクトの `Packages/manifest.json` に `testables` を足すと、Test Runner の EditMode に本パッケージのテストが出ます。テストはパッケージに同梱した見本だけを読むので、oscdesk リポジトリが無くても通ります(UDP のテストはループバックの空きポートを使います)。

```json
{
  "testables": ["com.hidano.oscdesk"]
}
```

## 版と契約の対応

版は [Semantic Versioning](https://semver.org/lang/ja/) に従います。Unity との契約(`docs/UNITY_PROTOCOL.md`)の Unity 側の振る舞いを互換でなく変える変更(アドレス・引数の形・エコーの規則の変更、公開 API・シリアライズするフィールドの削除や改名など)は major を上げます。契約の互換を保つ機能追加は minor、不具合の修正は patch です。

| パッケージの版 | 対応する Unity との契約 | ブリッジ |
|---|---|---|
| 1.0.0 | `docs/UNITY_PROTOCOL.md` の D-096 時点(`/sys/ping`・`/sys/pong`、同一アドレスへのエコー、UI で管理する行の `[s:ID, s:Name, ...]`(D-087)、自前の UDP / OSC(D-095)) | oscdesk の `main`(HID-354 以降) |

契約の変更点と移行の手順は `docs/UNITY_PROTOCOL.md` の「互換性ノート」に、版ごとの変更は [CHANGELOG.md](CHANGELOG.md) に記録します。

## ファイルを写して導入していた場合

以前の `OscSurface/Assets/OscReceiver/` を写して使っていたプロジェクトは、写した `.cs` / `.asmdef` を消してから本パッケージを入れます(同じ名前の asmdef `OscDesk.Receiver` が 2 つあるとコンパイルできません)。写した `OscReceiver.cs` の `.meta` の GUID が本パッケージの GUID(`450301b3c49b4642936f40978a3fd642`)と違う場合、シーン・プレハブに付けた `OscReceiver` は Missing Script になります。付け直すか、Unity を閉じた状態でシーン・プレハブの中の旧 GUID を本パッケージの GUID に置き換えてください(どちらを採るかは利用側で判断してください)。

## ライセンス

未設定です(npmjs への公開前に決めます)。
