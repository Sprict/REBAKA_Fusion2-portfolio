文書は[Docs/devlogs/2026-07-09_grab-joint-stability-and-carry-drive-cleanup.md](devlogs/2026-07-09_grab-joint-stability-and-carry-drive-cleanup.md)に対する私の振り返りメモです。
## 症状
1. プレイヤー同士で掴みあって引っ張り合うと掴みが外れる。
2. 引っ張り合っている時にプレイヤーの体がConfigurableJointのLimitを超えて変形しホストプレイヤーの体が地面に埋まって固定される。埋まっている間も体が微振動し装飾は発散し続けるバグ
があり、AIにこれらのバグ修正を依頼しました。
AIは内部で以下のような仮説と対応をしました。
### 仮説
プレイヤー同士はFixedJointで連結しているため、緩衝なくぶつかり合い、破断すれば「パキッと外れる」（1）、破断しなければジョイント全体に強い内部応力が蓄積し、本体と別処理で動く装飾パーツ側に伝播して発散する（2）。

プレイヤーは各物理ステップの始点・終点でのみ衝突判定するため、この離散衝突判定が高速移動時に床を貫通したことが直接の引き金である可能性が高い。

### 対応
1. プレイヤー同士の掴みを `FixedJoint` から上限付き `ConfigurableJoint` + Drive に変更。上限を超えても「壊れる/発散する」のではなく「滑ってズレる」ように緩やかに劣化させる狙い。
2. プレイヤー掴み専用の `playerGrabDriveSpring`(12000f) / `playerGrabDriveDamper`(600f) / `playerGrabDriveMaxForce`(1200f) を新設。硬め・強めの値にして「確実に掴んでいる」感触を保ちつつ、無限大ではない上限を設けた。
3. プレイヤーRootのRigidbody `m_CollisionDetection` を `0`(Discrete) から `2`(ContinuousDynamic) に変更。


## 仮説と試行
AIが修正 → 私が実機検証＆AIに修正依頼 → AIが修正 と繰り返して実機での掴み挙動の体感を確認しながら徐々に理想の挙動に近付けていきました。
### 検証１：Drive方式（Free + JointDrive）→ 発散はしないが拘束力が弱すぎて掴めない
プレイヤー同士の引っ張り合いで物理は発散はしなくなりましたが、拘束力が弱すぎて引っ張る前に拘束が解けてしまうことをAIに伝えて修正指示を出しました。

### 検証２：Drive強化版 → つかみというより引力で引っ張る挙動
プレイヤー同士が引っ張り合うと、お互いの腕の間に引力があるような挙動をし、ほとんど抵抗なくどこまでも離れられました。また、約8m以上離れたあたりから腕が振動し始めて離れるほど振動が大きくなりましたが発散はしませんでした。
AIの修正が私の意図と違う方向に進んでいると思い、実機検証での挙動を詳細に伝えつつFixedJointを使って自然な掴みにしたいこと・物理の発散は避けたいことを伝えると **Limited + わずかな遊び(slack)** 方式の案が出てきたので試しに実装してもらいました。

### 検証３：Limited + slack方式 → プレイヤーを紐で引っ張っているような挙動
物理は発散せず、プレイヤーやオブジェクトを引っ張れるようになりましたが、プレイヤー間にslackによる拳1個分くらいの隙間ができることと、引っ張る箇所が掴んだ箇所に固定されないことが気になりました。
そこで、ある程度の発散リスクは許容してFixedJoint方式にし、発散問題については物理が発散する前にbreakForceされるように調整することにし、AIに修正依頼しました。

### 検証４：FixedJoint統一 → 期待していた掴み挙動、発散前にbreakForce
掴んだポイントでFixedJointされて自然な挙動になりましたが、物理が発散してホストプレイヤーが地面に埋まる状態が再発したのでbreakForceを2000f→1400fに下げると発散する前に掴みが外れるようになりました。

## 今変更依頼されたらどこを見るか
- 「掴みが壊れやすすぎる」と言われたら、どのファイルのどの値から見るか？
→ RagdollControllerのProfileのgenericGrabBreakForceとgenericGrabBreakTorqueの値を見ます。 <br>


- 「掴める対象を増やしたい」と言われたら、経路のどの地点を触るか？
→ RagdollHandContact.OnCollisionEnter()の`if (collision.gameObject.CompareTag("CanBeGrabbed") || collision.gameObject.CompareTag("Player")` の部分に条件を追記します。<br>


- 読み始めの1ファイルを1つだけ挙げるなら何か？ それはなぜか？
→ ホストでもクライアントでもボタン入力を初めに検知する場所であるInputCollector.OnInput()から見ます。

## まだ一部説明できない点
- FixedJointとConfigurableJoint+Driveの違い
- FixedJoint同士の引っ張り合いで「物理が発散する」とき、具体的に何が起きているのか。
- 検証1でDrive方式が「弱すぎた」のはなぜか。
- 検証2で「離れるほど腕が振動した」のはなぜか。
- プレイヤー同士の押し合いは安定するのにつかみ合いに工夫がいる理由
- Input AuthorityとState Authorityの違い。掴み処理では、それぞれ誰が何を担当しているか。
- なぜ `OnCollisionEnter()` の中で直接入力を取得せず、`_grabButtonHeld` にキャッシュしたboolを使う設計なのか。
