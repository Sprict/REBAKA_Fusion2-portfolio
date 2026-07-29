# 2026-07-09 掴みジョイントの安定化とTreasure運搬まわりの整理

> **公開時注記（2026-07-22）:** 本資料は同日に試したDrive、Limited＋slack、FixedJointの順を残した開発記録です。最終的な一般掴みはFixedJoint＋breakForce調整です。本文前半のTreasure専用Drive・質量分配・運搬ハーネスは、7月20日に未使用経路として削除されました。現在の整理は[`2026-07-20_legacy-treasure-carry-removal.md`](2026-07-20_legacy-treasure-carry-removal.md)を参照してください。

## 問題

Windows PCをホスト、MacBook Proをクライアントにした2台実機テストで以下を確認:

1. プレイヤー同士を掴み合って別方向に移動すると掴みが外れる
2. プレイヤー同士を引っ張り合うと、ホストの装飾パーツの物理が発散し、ホストプレイヤーの本体が地面に埋まって（頭だけ出た状態で）固定される。埋まっている間も本体が微振動し、装飾は発散し続ける
3. 2人が同じ物を掴んで別方向に引っ張ると、クライアント側の掴みだけ外れる
4. 2人が同じ物を掴んで同じ方向に引っ張ると、たまに掴んでいる物の物理が破綻して吹っ飛ぶ
5. クライアントが物を掴んでいると体幹が弱まってへたり込む
6. ホスト・クライアントともにジャンプできなくなることが頻発
7. Treasure_Heavyを2人で運ぼうとしてもびくともしない

このうち5・6は本セッションの対象外（5は原因確定、6は未調査で別バグの疑い）。1・2・3・4・7を「掴みジョイント設計」の観点で調査した。

## 原因

### 体幹弱化（5）: 意図しない実装が混入していた

`RagdollProfile.carryLegDriveMaxForce`（Treasure保持中に脚のJointDrive最大力を制限する仕様）が存在したが、ユーザーはこれを指示した記憶がないと明言。当時の実装計画にもこの仕様の記載はなく、2026-07-03の大規模チェックポイントコミット（`c839815`、ポーズオーサリング等の無関係な変更と一括コミット）に紛れて導入されていた。経緯が追えないため、AIが要件を拡大解釈して自律的に追加した可能性が高いと判断し、撤去した。

### 掴みジョイントの構造（1・2・3・4・7共通の背景）

`RagdollHandContact.DoGrab()` は掴む対象によって全く異なる拘束方式を使っていた:

| 対象 | 変更前 | 特性 |
|---|---|---|
| プレイヤー同士 | `FixedJoint`（完全固定） | 緩みゼロ。`breakForce=2000f`超過で唐突に外れる |
| Treasure | `ConfigurableJoint` + Drive（spring=10000, damper=500, maxForce=500） | バネで追従、上限力で飽和 |

プレイヤー同士は完全固定のため、双方の移動力（ラグドール2体分）が緩衝なくぶつかり合う。破断すれば「パキッと外れる」（1）、破断しなければジョイント網全体に強い内部応力が蓄積し、本体と別処理で動く装飾パーツ側に伝播して発散する（2）という説明が成立する。

### Root剛体のCollisionDetectionMode

`newAPRPlayer.prefab` の APR_Root（Rigidbody）は `m_CollisionDetection: 0`（Discrete）のままだった。Discreteは各物理ステップの始点・終点でのみ衝突判定するため、綱引きのような強い力がかかった高速移動時に床コライダーをすり抜ける（トンネリング）リスクがある。バグ2の「地面に埋まる」はジョイントの硬さだけでなく、この離散衝突判定が高速移動時に床を貫通したことが直接の引き金である可能性が高い。

### Treasure_Heavyの質量と握力の不整合（7、間接的に4）

`TreasureProfile_Heavy.asset` は `baseMass=200`（2人で分担しても100kg）。一方プレイヤー側の `grabDriveMaxForce=500f`（手1本あたりの最大駆動力）と、Treasure保持中のRoot移動力上限 `carryMoveMaxForce=150f` は、この質量に対して構造的に力不足だった。動かないどころか、2人分の独立したバネ拘束（手ごとに1本）が同一剛体に対して非同期タイミングで力をかけ続けることで、位相のズレから発振し「吹っ飛ぶ」（4）という説明とも整合する。

### バグ3（クライアント側だけ掴みが外れる）は対象外

掴み判定・解除はホスト権威で実行される（`RagdollHandContact.FixedUpdateNetwork` の `if (!HasStateAuthority) return;`）。そのため物理力の調整では説明がつかず、ホストの実際の状態とクライアントの表示（プロキシ補間）がズレている可能性がある。ジョイント設計の変更では直らない可能性が高いため、今回は対象外とし、`RagdollNetDiagnostics` の `joint_create`/`joint_destroy` ログをホスト側で確認することで、次回「力の問題か表示の問題か」を1回の実機セッションで切り分けられるようにした。

## 対応

1. **`RagdollHandContact.cs`**: プレイヤー同士の掴みも `FixedJoint` から上限付き `ConfigurableJoint` + Drive に変更。Treasure用ブランチと合流させ、`treasure != null` で使うDriveパラメータを出し分ける形に統一。上限を超えても「壊れる/発散する」のではなく「滑ってズレる」ように緩やかに劣化させる狙い。
2. **`RagdollProfile.cs`**: プレイヤー掴み専用の `playerGrabDriveSpring`(12000f) / `playerGrabDriveDamper`(600f) / `playerGrabDriveMaxForce`(1200f) を新設。Treasure用より硬め・強めの値にして「確実に掴んでいる」感触を保ちつつ、無限大ではない上限を設けた。
3. **`newAPRPlayer.prefab`**: APR_RootのRigidbody `m_CollisionDetection` を `0`(Discrete) から `2`(ContinuousDynamic) に変更。
4. **`TreasureProfile_Heavy.asset`**: `baseMass` を `200` → `80` に変更（2人で分担すると40kg、`minSharedMass=30`はそのまま維持）。
5. **`RagDollPhysics.cs` / `RagDollController.cs` / `RagdollControllerContracts.cs`**: `carryLegDriveMaxForce` 関連のコード・インターフェース契約・Profileフィールドを完全撤去。

## 不採用案

- **プレイヤー掴みのbreakForceだけ調整してFixedJointを維持**: 完全固定という感触自体は保てるが、発散（バグ2）そのものへの対策にはならないため見送った。
- **grabDriveMaxForceを直接引き上げてTreasure_Heavyに対応**: 全Treasure共通のパラメータのため、軽いTreasureでの発振リスク（バグ4）を悪化させる可能性がある。Treasure側の質量を個別調整する方が影響範囲が狭く安全と判断した。
- **Treasureハーネス（`carryHarnessLimitSpring`等）の値調整**: 手のDriveとハーネスのlinearLimitは役割が異なり（常時追従 vs 距離超過時のみ介入）、通常は競合しない設計だと判断した。この部分の微調整は実機での反応を見てから行う方が手戻りが少ないため、今回は見送り保留。

## 自力再実装チェックリスト

- [ ] `ConfigurableJoint` の `xDrive`/`yDrive`/`zDrive` に `JointDrive{positionSpring, positionDamper, maximumForce}` を設定する意味を説明できるか（`FixedJoint` との違いを含む）
- [ ] `SoftJointLimitSpring`（`linearLimitSpring`）が「limit超過時のみ働くバネ」であり、常時作動する `xDrive` 等とは役割が異なることを説明できるか
- [ ] `CollisionDetectionMode.Discrete` が高速移動する剛体でトンネリングを起こしうる理由を説明できるか（[※理論] Unity公式ドキュメントで裏取り推奨）
- [ ] `TreasureGrabRegistry` の質量分配ロジック（`baseMass / grabberCount`、`minSharedMass`下限）を説明できるか
- [ ] なぜ「手のDriveとRootの移動力上限（`carryMoveMaxForce`）」の両方がTreasureを動かせるかどうかに影響するかを説明できるか

## 未検証・次のアクション（初回版時点、以下「実機検証で判明した続き」で更新）

- 全ての変更は静的読解と力学的な見積もりに基づく仮説であり、**2クライアント実機テストでの検証が必須**
- ホスト側で `joint_destroy` ログが出ているかどうかで、バグ3が力の問題か表示の問題かを切り分ける
- `TreasureProfile_Heavy.baseMass=80` は暫定値。実機で動かせるかどうかを見て再調整する
- バグ6（ジャンプ不可）は本セッション未着手。別原因の可能性が高く、後続で `_jumpVelocityApplied` ラッチと接地判定まわりを調査する

---

## 実機検証で判明した続き（同日、ParrelSync + Gamepad 2同時操作検証）

上記の初回対応（プレイヤー同士も Drive 方式に統一）を実機で検証した結果、想定と異なる挙動が2つ判明し、最終的に設計を再度組み直した。試行錯誤の経緯自体に学習価値があるため、以下に順を追って記録する。

### 検証環境: ParrelSync + Gamepad分離の追加

「2人が同時に押し続ける・引っ張り続ける」検証は、フォーカス切り替え型の逐次操作では再現できない（掴みボタンを押し続けないと `RagdollHandContact.FixedUpdateNetwork` が即座に `ReleaseGrab()` する）。そこで ParrelSync のメイン/クローンそれぞれに固定デバイスを割り当てる仕組みを追加した。

- `ParrelSyncInputUtil.cs`（新規）: メイン=Keyboard+Mouse固定、クローン=Gamepad固定。`InputActionAsset.devices` を制限する。
- `ParrelSync.ClonesManager.IsClone()` は Editor 専用アセンブリ（`"includePlatforms": ["Editor"]`）のため、ランタイムアセンブリ（`MyProject.Scripts`）から直接参照するとビルド対象プラットフォームで解決できなくなる。`IsClone()` 自身の実装（プロジェクトルート直下の空ファイル `.clone` の有無を見るだけ）をランタイム側で再現し、アセンブリ参照を避けた。
- 制限適用は `InputCollector.cs` と `OrbitCamera.cs` の両方に必要だった（それぞれ独立に `REBAKA_Fusion2`（Input Actions）インスタンスを生成しているため、片方だけ制限すると「右スティックXだけ反応する」ような部分的な入力漏れが起きた）。

### 検証1: Drive方式（Free + JointDrive）→ 発散はしないが弱すぎて実用にならない

初回対応の Free+Drive 方式（`playerGrabDriveSpring=12000f` 等）を2同時操作で検証:
- プレイヤー同士の引っ張り合い: **発散しない**。ただし拘束力が弱く、引っ張ろうとすると簡単に外れる。
- 一般オブジェクト（`Obs_Sph_s1.0`）: 「弱い磁力に引かれてくっつくような」印象で、かろうじて持てる程度。

原因: `positionSpring` は距離に比例した力（フックの法則）のみを出す。`targetPosition = Vector3.zero`（手のごく近く）を目標にしているため、オブジェクトが手元に近づくほど復元力そのものが小さくなる。「モノを掴んで持つ」動作としては FixedJoint（距離に関係なく完全拘束）の方が正しい感触になる。

このタイミングで「一般オブジェクトまで Drive 方式に変えたのは過剰な一般化だった」と判断した（発散問題はプレイヤー×プレイヤーという特殊ケース限定で、一般オブジェクトは単純な剛体なので FixedJoint でも制御力同士の衝突は起きない）。**一般オブジェクトを FixedJoint に差し戻し、プレイヤー同士の Drive 値だけ強化**（spring 12000→20000, damper 600→2000, maxForce 1200→4000）。

### 検証2: Drive強化版 → 期待通りの結果（一旦の到達点）

強化後の検証:
- プレイヤー同士: 引っ張り合うと自然に離れていける。近くでは腕は振るわず、離れるにつれて腕が振るう（後述）が発散はしない。
- 2人で `Obs_Cube` を協調運搬できた。逆方向に強く引くと自然に接続が切れた。

ここでユーザーから「押し合い（Rigidbody衝突・速度制御のみで、ジョイントなし）は発散しないのに、なぜ掴み合いはジョイントの工夫が必要なのか」という指摘があった。押し合いが安定するのは、拘束の種類ではなく **PhysXの衝突解決による自然な力の相殺**（連結ではなく接触）によるもので、掴み合いは「連結」である以上、ジョイント側の減衰設計が要る、と回答した。

観察された副作用: **距離が伸びるほど腕が振るう**（近距離では振るわない）。`positionSpring` は距離に比例して力が強くなる一方、`positionDamper` は相対速度に比例した減衰にとどまるため、一定速度で離れていく状況では復元力の増加にダンパーが追いつかず、距離依存で振動しやすくなる。致命的ではないが記録しておく。

### 検証3: Limited + slack方式 → 理想からは外れる

ユーザーから「理想を言えばプレイヤー同士もFixedJointの自然な掴みにしたいが、発散は避けたい」という要望があり、Treasureの腕ハーネス（`CreateCarryHarness`）と同じ **Limited + わずかな遊び(slack)** 方式を試した。

- `playerGrabSlack=0.05f`（遊び距離）、`playerGrabLimitSpring=20000f`、`playerGrabLimitDamper=3000f`。
- 遊びの範囲内は無拘束（FixedJoint同等の追従感触）、超えた分だけ `SoftJointLimitSpring`（双方向バネ）が働く設計。
- 検証結果: 発散なし・自然に離れる・`Obs_Cube` 協調運搬もOK、という動作面では良好な結果を得た。

ただしユーザーからは「やっぱり挙動が理想と違う」というフィードバックがあり、**プレイヤーもオブジェクトも分けず全く同じFixedJoint方式にした方が自然。breakForceで壊れる前提にすればいいのでは**という提案があった。

ここで重要な気付きがあった。**最初にバグ2（プレイヤー同士を引っ張り合うとホストが埋まる）を観測したのは、APR_Root の `CollisionDetectionMode` が Discrete のままだった時点**であり、`ContinuousDynamic` に変更した後は一度も FixedJoint 方式を再検証していなかった。つまり「FixedJointが発散の原因」という結論は、交絡変数（Discrete由来の床すり抜け）を切り分けないまま出した仮説に過ぎなかった。

### 検証4: FixedJoint統一（ContinuousDynamic適用後、初めての再検証）→ 一時的な過剰応力を確認

プレイヤー同士・一般オブジェクトを FixedJoint に統一（Treasureは既存のDrive方式を維持）し、2同時操作で再検証:

- 近くでの掴み感触: FixedJoint本来の自然な感触に戻った。
- **プレイヤー同士を逆方向に強く引っ張ると、ホストプレイヤーの体が「普通なら曲がらない方向に曲がって」戻れなくなる状態が再発した。** 装飾パーツは激しく振動し、本体も微振動した。ただし**放置すると自然に安定状態へ復帰した**（恒久的な破綻ではなく一時的な過剰応力）。

これにより、`ContinuousDynamic` 化後も **FixedJointの完全拘束×2体のバランス制御システムという組み合わせ自体が、一時的にジョイントソルバーを破綻寸前まで追い込む**ことが確認された。CollisionDetectionModeは無関係で、初回（検証前）の見立て（FixedJointの完全拘束が主因）が正しかったことになる。

### 最終対応: FixedJoint統一 + breakForceの調整

ユーザーの「現状のbreakForceはそこまで悪くないので少しだけ下げる」という判断に基づき、`effectiveBreakForce` をハードコード値（2000f）から Profile パラメータ化し、下げて再検証した。

- `RagdollProfile.genericGrabBreakForce`（新規、初期値 `1400f`）: プレイヤー同士・一般オブジェクト共通のFixedJoint breakForce/breakTorque。
- 再検証結果: 良好。体が歪む・装飾が激しく振動する前に外れるようになった。

**最終的な掴みジョイント設計**（プレイヤー掴みのDrive方式・Limited+slack方式は撤回・削除済み）:

| 対象 | ジョイント | パラメータ |
|---|---|---|
| プレイヤー同士 | `FixedJoint` | `genericGrabBreakForce=1400f` |
| 一般オブジェクト（Treasure以外のCanBeGrabbed） | `FixedJoint` | `genericGrabBreakForce=1400f` |
| Treasure | `ConfigurableJoint` + Drive | `grabDriveSpring=10000f` / `grabDriveDamper=500f` / `grabDriveMaxForce=500f`（変更なし） |

### Treasure_Heavyの一般オブジェクト化

上記の検証と並行して、ユーザーが `Obs_Cube` のサイズを2倍・mass=80に調整して2人運搬をテストしたところ「一人ではほとんど動かせないが二人なら引きずれる」という理想的な挙動が得られた。これを踏まえ、`TreasureGrabRegistry` の質量分配・`CreateCarryHarness` の腕ハーネスといった専用システムを使わずとも、単純な重いRigidbody + FixedJoint + 複数人の引っ張り力の物理的合成だけで同じ目的を達成できることが分かった。

- `Treasure_Heavy.prefab`: `Treasure` / `TreasureDebugLabel` コンポーネントを削除し、`Obs_Cube.prefab` と同構成（`CanBeGrabbed`タグ、`GameNetworkRigidbody`のみ、mass=80）に変更。
- `MapTreasureSpawner.cs` は `NetworkObject` 型でTreasureプレハブを参照しているだけで `Treasure` コンポーネントに直接依存しないため、スポーン処理への影響はない。
- **Treasureシステム自体（`Treasure.cs` / `TreasureGrabRegistry.cs` / `TreasureProfile.cs` 等）は削除していない**。現時点で使用例は0件になったが、将来的な廃止も視野に入れつつ今回はプレハブ側の変更に留めた。

### 最終検証結果

- プレイヤー同士の掴み合い: 自然な感触、発散なし、強く引っ張ると体が歪む前に外れる
- `Obs_Cube`（一般オブジェクト）: しっかり掴める、2人協調運搬OK
- `Treasure_Heavy`（一般オブジェクト化後）: 一人ではほとんど動かせないが二人なら引きずれる、狙い通りの挙動

## 更新後の自力再実装チェックリスト

- [ ] `FixedJoint` と `ConfigurableJoint`（Free+Drive／Limited+slack）の3方式の違いと、それぞれの発散・振動特性を説明できるか
- [ ] なぜ「押し合い（Rigidbody接触+速度制御）」は安定するのに「掴み合い（ジョイント連結）」は工夫が要るのかを説明できるか（連結と接触の違い、PhysXソルバーの挙動）
- [ ] `positionSpring`（距離比例力）と `positionDamper`（速度比例減衰）の役割の違いと、「距離が伸びるほど振動しやすくなる」現象がなぜ起きるかを説明できるか
- [ ] `SoftJointLimitSpring` が「limit超過時のみ働く双方向バネ」であり、境界での跳ね返り（アンダーダンプ時）を持つことを説明できるか
- [ ] 交絡変数を切り分けずに立てた仮説（CollisionDetectionModeが真因、という初回の見立て）が、後の再検証でどう覆ったかを説明できるか
- [ ] `ParrelSync.ClonesManager.IsClone()` がEditor専用アセンブリである理由と、ランタイム側でマーカーファイルを直接チェックする回避策を説明できるか
- [ ] `TreasureGrabRegistry` の質量分配システムを使わずとも、単純な重いRigidbody + 複数人の力の合成だけで同じ「協力運搬」ゲームデザインを実現できる理由を説明できるか

## 更新後の未検証・次のアクション

- バグ3（クライアント側だけTreasure掴みが外れる）は依然未検証。`joint_destroy` ログでの切り分けが必要。
- バグ6（ジャンプ不可）は本セッション未着手。
- `genericGrabBreakForce=1400f` は実機で「良い感じ」と確認された値だが、より広範な状況（3人以上の絡み合い等）での再検証は未実施。
- Treasureシステム（`Treasure.cs`等）自体の将来的な削除は今回見送った。使用例が0件のままなら、次回整理のタイミングで削除を検討する。

## バグ3の再調査結果（2026-07-09、後日のバグ6修正セッションにて）

初回はユーザー報告を「クライアント（Join側プレイヤー）の掴みだけがbreakしやすい」という**権限非対称のバグ**として記録し、上記の通り`joint_destroy`ログでの切り分けを次のアクションとして残していた。

しかし後日の会話で報告内容の用語を再確認したところ、ここでの「クライアント」はネットワーク用語（Host/Client同期）ではなく「Join側プレイヤー」を指す口語的表現だったことが判明した。さらに、その後の実機テストでは**Hostプレイヤー側の掴みも同様に外れる**ことが確認された。

これにより、当初疑っていた「掴み判定・解除はホスト権威で実行されるため、物理力の調整では説明がつかない」という仮説（42-44行目参照）は**再考が必要**になった。Host/Joinどちらの掴みも外れるという事実は、非対称な権限・同期の問題ではなく、**プレイヤー同士の掴みジョイント（`FixedJoint`, `genericGrabBreakForce=1400f`）が、綱引きのような強い引っ張り合いでbreakForce閾値付近に達した場合にどちらの側でも起こりうる、設計上想定内の挙動**だったと結論づけた。

**結論**: バグ3は独立したバグではなく、既存の掴みジョイント設計（本devlog内の「対応」セクション参照）の範囲内の現象。`joint_destroy`ログでの追加切り分けは不要と判断し、対応を打ち切った。

**教訓**: ユーザー報告に出てくる「クライアント」等の用語は、ネットワーク文脈の専門用語と口語的な意味（Join側プレイヤーを指す等）の両方があり得るため、原因調査の前に指している対象を確認する価値がある。今回は先に権限非対称という重い仮説を立ててから半日以上経ってようやく用語の食い違いに気づいた。
