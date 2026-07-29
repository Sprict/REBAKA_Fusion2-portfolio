using System;
using Fusion;
using UnityEngine;

namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// SnapshotInterpolation モードのクライアント側 Render() 補間本体。
    ///
    /// ホストが発行した全身ポーズ（Root ワールド姿勢 + 14 パーツの Root 相対姿勢）を、
    /// Fusion のスナップショットバッファ（from/to）間で Lerp/Slerp して transform に書き込む。
    /// 物理・PID 補正を一切使わない純粋な視覚補間のため、原理的にガタつかない。
    /// 遅延は補間バッファ分（数十ms）増えるが、本プロジェクトでは許容済み。
    ///
    /// テレポート時（TeleportKey 不一致）は from 側に留めて補間スミアを防ぐ
    /// （Fusion.Addons.Physics.NetworkRigidbody の TeleportKey と同パターン）。
    /// </summary>
    internal sealed class RagdollSnapshotPoseInterpolator
    {
        private readonly IPoseSnapshotAccess _access;
        private bool _visualsShown;

        // 装飾 RB（ローカル物理）のワールド姿勢退避バッファ（毎フレーム alloc しない）
        private Vector3[] _decorationPositions;
        private Quaternion[] _decorationRotations;

        // 装飾 RB の描画補間用バッファ（物理ルート相対座標）。
        // 装飾のローカル物理は Physics.Simulate（tick レート・手動実行）でしか進まず、
        // 手動 Simulate では Rigidbody.interpolation も効かないため、そのまま描くと
        // 毎フレーム補間される本体に対して階段状のブレ（微振動）に見える（2026-06-12 報告）。
        // 物理ステップ直後の姿勢を 2 世代保持し、Render では世代間を経過時間で補間した
        // 「描画専用の姿勢」を書く。物理上の真の姿勢は OnBeforeSimulate で復元するため、
        // 物理シミュレーション自体には影響しない。
        //
        // ワールド座標ではなく「記録時点の本体ルート相対」で保持する理由:
        // 本体はネットワーク補間（from/to + alpha）、装飾はローカル物理（tick + 経過時間）と
        // 進む時間軸が異なるため、ワールドのまま別々に補間すると移動中に並進の位相差が
        // 「本体と装飾の相対位置の振動」として現れる（Walking 時のみ荒ぶる 2026-06-12 報告）。
        // 相対で補間し描画ルート変換で合成すれば、並進は常に本体と同期し位相差が構造的に消える。
        private Vector3[] _decoRelPositionsCurr;
        private Quaternion[] _decoRelRotationsCurr;
        private Vector3[] _decoRelPositionsPrev;
        private Quaternion[] _decoRelRotationsPrev;
        private Vector3 _physRootPositionCurr;
        private Quaternion _physRootRotationCurr;
        private double _lastSimulateTime;
        private float _simulateInterval;
        private int _physPoseGenerations;

        // ルートが 1 tick でこの距離を超えて動いたらテレポート（リスポーン等）とみなし、
        // 装飾を本体に随伴させて一緒に移動する（ジョイント大エラーで吹き飛ぶのを防ぐ）
        private const float RootTeleportDistanceSqr = 4f; // 2m^2

        // 装飾の描画専用ローパスフィルタ（指数移動平均）の状態。
        // 励起均一化（本体 tick ポーズ配置）後も残る高周波の微振動
        // （本体がテレポート移動＝速度ゼロのため、ジョイント励起に原理的残差がある）を
        // 描画レイヤーだけで減衰させる。物理姿勢（_decoRelPositionsCurr 系）には触れないため
        // シミュレーション自体には影響しない。
        // 時定数は RagdollProfile.decorationSmoothingTau（Inspector 調整可）。
        // 大きいほど微振動が消えるが装飾の動きに遅れ（位相遅れ）が乗るトレードオフ。
        private Vector3[] _decoSmoothedRelPositions;
        private Quaternion[] _decoSmoothedRelRotations;
        private bool _decoSmootherPrimed;
        private Vector3 _lastRenderRootPosition;

        public RagdollSnapshotPoseInterpolator(IPoseSnapshotAccess access)
        {
            _access = access ?? throw new ArgumentNullException(nameof(access));
        }

        public void RunRender()
        {
            // スナップショット未充足（接続直後・深刻なパケロス）時は直前の描画ポーズを保持する。
            // 外挿はラグドールでは発散源になるため行わない。
            if (!_access.TryGetPoseSnapshots(out NetworkBehaviourBuffer from, out NetworkBehaviourBuffer to,
                    out float alpha))
            {
                return;
            }

            // ホストがまだ一度もポーズを発行していない区間は描画しない（Tポーズ/原点フラッシュ防止）
            if (!_access.ReadPoseInitialized(to))
            {
                return;
            }

            (int fromKey, int toKey) = _access.ReadPoseTeleportKey(from, to);
            if (fromKey != toKey)
            {
                // テレポートを跨ぐ補間はスミア（滑り移動）になるため from 側のポーズに留める。
                // 次の Render では from/to がテレポート後のスナップショット同士になり通常補間に戻る。
                to = from;
                alpha = 0f;
            }

            Rigidbody rootRigidbody = _access.GetBodyRigidbodyByIndex(0);
            if (rootRigidbody == null)
            {
                return;
            }

            // 装飾 RB（Other/ 配下の Sphere 等）は APR_Root の Transform 階層の子のため、
            // ルートへの transform 書き込みに引きずられてローカル物理が殺される。
            // 通常は物理ステップ直後の姿勢から補間した描画姿勢を書き込み後に上書きする。
            // 物理ポーズ未記録（OnAfterSimulate 未配線 or 初回ステップ前）の間だけ、
            // 従来の退避→復元（正味ゼロ）にフォールバックして分離を防ぐ
            Rigidbody[] decorations = _access.DecorationRigidbodies;
            EnsureDecorationsDynamic(decorations);
            bool hasPhysPoses = HasDecorationPhysPoses(decorations);
            if (!hasPhysPoses)
            {
                StashDecorationPoses(decorations);
            }

            (Vector3 fromRootPos, Vector3 toRootPos) = _access.ReadRootPosition(from, to);
            (Quaternion fromRootRot, Quaternion toRootRot) = _access.ReadRootRotation(from, to);

            Vector3 rootPosition = Vector3.Lerp(fromRootPos, toRootPos, alpha);
            Quaternion rootRotation = Quaternion.Slerp(SafeQuaternion(fromRootRot), SafeQuaternion(toRootRot), alpha);

            // 全 Rigidbody は kinematic のため transform 書き込みで安全に動かせる
            rootRigidbody.transform.SetPositionAndRotation(rootPosition, rootRotation);

            NetworkArrayReadOnly<Vector3> fromPartPositions = _access.ReadPartPositions(from);
            NetworkArrayReadOnly<Vector3> toPartPositions = _access.ReadPartPositions(to);
            NetworkArrayReadOnly<Quaternion> fromPartRotations = _access.ReadPartRotations(from);
            NetworkArrayReadOnly<Quaternion> toPartRotations = _access.ReadPartRotations(to);

            for (int slot = 0; slot < RagdollPoseSync.RelativePartCount; slot++)
            {
                Rigidbody partRigidbody =
                    _access.GetBodyRigidbodyByIndex(slot + RagdollPoseSync.FirstRelativePartIndex);
                if (partRigidbody == null)
                {
                    continue;
                }

                Vector3 relativePosition = Vector3.Lerp(fromPartPositions[slot], toPartPositions[slot], alpha);
                Quaternion relativeRotation = Quaternion.Slerp(
                    SafeQuaternion(fromPartRotations[slot]),
                    SafeQuaternion(toPartRotations[slot]),
                    alpha);

                // Root 相対 → ワールド座標へ合成
                partRigidbody.transform.SetPositionAndRotation(
                    rootPosition + rootRotation * relativePosition,
                    rootRotation * relativeRotation);
            }

            if (hasPhysPoses)
            {
                WriteDecorationRenderPoses(decorations, rootPosition, rootRotation);
            }
            else
            {
                RestoreDecorationPoses(decorations);
            }

            // 初の有効スナップショットを描画できた時点で表示を有効化する
            if (!_visualsShown)
            {
                _access.SetProxyVisualsEnabled(true);
                _visualsShown = true;
            }
        }

        private bool _decorationStateLogged;

        /// <summary>
        /// 装飾 RB を dynamic + 重力あり + 起床状態に保つ（自己修復）。
        /// FUN 側の EnforceSnapshotPhysicsState と二重化しているのは、
        /// 実機でスフィアが kinematic のまま残る事象が確認されたため、
        /// クライアントで確実に毎フレーム実行される Render 経路でも保証する。
        /// kinematic な本体にジョイント接続された RB は親側の移動で自動起床
        /// しないことがあるため、WakeUp も毎フレーム行う。
        /// </summary>
        private void EnsureDecorationsDynamic(Rigidbody[] decorations)
        {
            if (decorations == null || decorations.Length == 0)
            {
                // 装飾が見つからない場合も一度はログを残す（DecorationRigidbodies 構築不全の切り分け用）
                if (!_decorationStateLogged)
                {
                    _decorationStateLogged = true;
                    Debug.Log("[SNAPSHOT_DECO] decorations=0 (no local-physics decorations found)");
                }

                return;
            }

            int flipped = 0;
            foreach (Rigidbody rb in decorations)
            {
                if (rb == null) continue;
                if (rb.isKinematic)
                {
                    rb.isKinematic = false;
                    rb.useGravity = true;
                    flipped++;
                }

                if (rb.IsSleeping())
                {
                    rb.WakeUp();
                }
            }

            if (!_decorationStateLogged)
            {
                _decorationStateLogged = true;
                Debug.Log($"[SNAPSHOT_DECO] decorations={decorations.Length} flippedToDynamic={flipped}");
            }
        }

        /// <summary>
        /// Physics.Simulate 直前（RunnerSimulatePhysics.OnBeforeSimulate）に呼ぶ。
        /// - 本体 15 パーツ: 最新受信 tick の確定ポーズへ配置（励起の均一化）
        /// - 装飾: Render が描画用に書いた transform を最後の物理ポーズへ戻し、
        ///   描画姿勢が次ステップの初期値として物理にコミットされるのを防ぐ
        ///   （transform 書き込みは Simulate 時の SyncTransforms で物理に反映されるため）。
        ///
        /// 注: 本体 15 パーツを MovePosition で速度付き移動させる方式は
        /// 2026-06-12 に実機で破綻した（transform 復元 + MovePosition の併用が
        /// ステップ間で振動し装飾が発散）ため撤回済み。配置は transform 書き込みのみで行う。
        /// </summary>
        public void OnBeforeSimulate()
        {
            // 順序が重要: 本体配置（親 transform 移動）は装飾（子）を引きずるため、
            // 装飾の復元を必ず後に行って上書きする
            WriteBodyTickPosesForPhysics();
            RestoreDecorationPhysicsPoses();
        }

        /// <summary>
        /// 物理ステップ直前に、本体 15 パーツを「最新受信 tick の確定ポーズ」
        /// （[Networked] 生読み）へ配置する。
        ///
        /// Render が書く描画姿勢はフレームレート依存で、tick 間の本体移動量が
        /// 不均一になる（fps が tick レートより低いと「2 tick 静止 → 2 tick 分ジャンプ」）。
        /// それを物理の親として使うとジョイント励起が不均一になり、移動中に装飾が
        /// 相対空間内で振動する（Walking 時の残ジャダー、2026-06-12 報告）。
        /// 生読みの networked ポーズは tick ごとに均一に進む系列のため励起が均一化される。
        ///
        /// 物理本体（tick ポーズ）と描画本体（補間ポーズ）の位置差は、
        /// Render が毎フレーム描画姿勢を上書きし、装飾はルート相対で合成するため
        /// 見た目には現れない。
        /// </summary>
        private void WriteBodyTickPosesForPhysics()
        {
            if (!_access.IsLatestPoseInitialized)
            {
                return;
            }

            Rigidbody rootRigidbody = _access.GetBodyRigidbodyByIndex(0);
            if (rootRigidbody == null)
            {
                return;
            }

            Vector3 rootPosition = _access.LatestRootPosition;
            Quaternion rootRotation = SafeQuaternion(_access.LatestRootRotation);
            rootRigidbody.transform.SetPositionAndRotation(rootPosition, rootRotation);

            for (int slot = 0; slot < RagdollPoseSync.RelativePartCount; slot++)
            {
                Rigidbody partRigidbody =
                    _access.GetBodyRigidbodyByIndex(slot + RagdollPoseSync.FirstRelativePartIndex);
                if (partRigidbody == null)
                {
                    continue;
                }

                Vector3 relativePosition = _access.GetLatestPartRelativePosition(slot);
                Quaternion relativeRotation = SafeQuaternion(_access.GetLatestPartRelativeRotation(slot));
                partRigidbody.transform.SetPositionAndRotation(
                    rootPosition + rootRotation * relativePosition,
                    rootRotation * relativeRotation);
            }
        }

        private void RestoreDecorationPhysicsPoses()
        {
            Rigidbody[] decorations = _access.DecorationRigidbodies;
            if (decorations == null || decorations.Length == 0 || _physPoseGenerations == 0)
            {
                return;
            }

            if (_decoRelPositionsCurr == null || _decoRelPositionsCurr.Length != decorations.Length)
            {
                return;
            }

            Rigidbody rootRigidbody = _access.GetBodyRigidbodyByIndex(0);
            if (rootRigidbody == null)
            {
                return;
            }

            // 通常時: 前ステップ終了時のルート姿勢を基準にワールド復元する。
            // 本体だけが先へ進み、ジョイントが装飾を追従させる = ホストと同じ励起の与え方。
            // テレポート時（リスポーン等でルートが大きく飛んだ）: 現在のルート姿勢を基準にして
            // 装飾を本体に随伴させ、ジョイント大エラーによる吹き飛びを防ぐ。
            Vector3 rootPosition = _physRootPositionCurr;
            Quaternion rootRotation = _physRootRotationCurr;
            Transform rootTransform = rootRigidbody.transform;
            if ((rootTransform.position - _physRootPositionCurr).sqrMagnitude > RootTeleportDistanceSqr)
            {
                rootPosition = rootTransform.position;
                rootRotation = rootTransform.rotation;
            }

            for (int i = 0; i < decorations.Length; i++)
            {
                Rigidbody rb = decorations[i];
                if (rb == null) continue;
                rb.transform.SetPositionAndRotation(
                    rootPosition + rootRotation * _decoRelPositionsCurr[i],
                    rootRotation * _decoRelRotationsCurr[i]);
            }
        }

        /// <summary>
        /// Physics.Simulate 直後（RunnerSimulatePhysics.OnAfterSimulate）に呼ぶ。
        /// 装飾の物理姿勢を「その時点の本体ルート相対」で世代バッファへ記録し、
        /// Render の時間補間の端点にする。
        /// </summary>
        public void OnAfterSimulate()
        {
            Rigidbody[] decorations = _access.DecorationRigidbodies;
            if (decorations == null || decorations.Length == 0)
            {
                return;
            }

            Rigidbody rootRigidbody = _access.GetBodyRigidbodyByIndex(0);
            if (rootRigidbody == null)
            {
                return;
            }

            if (_decoRelPositionsCurr == null || _decoRelPositionsCurr.Length != decorations.Length)
            {
                _decoRelPositionsCurr = new Vector3[decorations.Length];
                _decoRelRotationsCurr = new Quaternion[decorations.Length];
                _decoRelPositionsPrev = new Vector3[decorations.Length];
                _decoRelRotationsPrev = new Quaternion[decorations.Length];
                _physPoseGenerations = 0;
            }

            // curr → prev は参照スワップ（毎 tick の配列コピーを避ける）
            (_decoRelPositionsPrev, _decoRelPositionsCurr) = (_decoRelPositionsCurr, _decoRelPositionsPrev);
            (_decoRelRotationsPrev, _decoRelRotationsCurr) = (_decoRelRotationsCurr, _decoRelRotationsPrev);

            Transform rootTransform = rootRigidbody.transform;
            Vector3 rootPosition = rootTransform.position;
            Quaternion rootRotation = rootTransform.rotation;
            Quaternion inverseRootRotation = Quaternion.Inverse(rootRotation);

            for (int i = 0; i < decorations.Length; i++)
            {
                Rigidbody rb = decorations[i];
                if (rb == null) continue;
                Transform t = rb.transform;
                _decoRelPositionsCurr[i] = inverseRootRotation * (t.position - rootPosition);
                _decoRelRotationsCurr[i] = inverseRootRotation * t.rotation;
            }

            _physRootPositionCurr = rootPosition;
            _physRootRotationCurr = rootRotation;

            double now = Time.timeAsDouble;
            // 同一フレーム内に複数 tick が走ると間隔がほぼ 0 になるため下限でクランプ
            // （その場合 Render の補間 alpha が即 1 になり最新ステップを表示するだけで安全）
            _simulateInterval = Mathf.Clamp((float)(now - _lastSimulateTime), 0.0001f, 0.5f);
            _lastSimulateTime = now;

            if (_physPoseGenerations < 2)
            {
                _physPoseGenerations++;
            }
        }

        private bool HasDecorationPhysPoses(Rigidbody[] decorations)
        {
            return _physPoseGenerations > 0 &&
                   decorations != null &&
                   _decoRelPositionsCurr != null &&
                   _decoRelPositionsCurr.Length == decorations.Length;
        }

        /// <summary>
        /// 装飾 RB の描画姿勢を書き込む。物理ステップ直後のルート相対姿勢 2 世代
        /// （prev/curr）を最後のステップからの経過時間で補間し、本体と同じ
        /// 描画ルート変換（renderRoot）で合成する。
        /// - 相対の補間: tick レートの階段状更新を Render レートの滑らかな動きに変換
        /// - 描画ルートでの合成: 本体と並進・回転を共有するため、本体（ネットワーク補間）と
        ///   装飾（ローカル物理）の時間軸の位相差が相対位置の振動として現れない
        /// 表示は最大 1 tick 分（約 16ms）遅れるが、揺れもの装飾では慣性遅れに
        /// 見えるため許容（本プロジェクトは遅延許容方針）。
        /// </summary>
        private void WriteDecorationRenderPoses(
            Rigidbody[] decorations, Vector3 renderRootPosition, Quaternion renderRootRotation)
        {
            bool hasPrev = _physPoseGenerations >= 2;
            float alpha = hasPrev && _simulateInterval > 0f
                ? Mathf.Clamp01((float)(Time.timeAsDouble - _lastSimulateTime) / _simulateInterval)
                : 1f;

            EnsureDecorationSmoother(decorations.Length, renderRootPosition);

            // フレームレート非依存の EMA 係数: smoothed = Lerp(smoothed, target, k)
            // tau は Play 中の Inspector 調整を即反映するため毎フレーム読む。0 以下なら平滑化なし
            float tau = _access.DecorationSmoothingTau;
            float smoothing = tau > 0.0001f ? 1f - Mathf.Exp(-Time.deltaTime / tau) : 1f;

            for (int i = 0; i < decorations.Length; i++)
            {
                Rigidbody rb = decorations[i];
                if (rb == null) continue;

                Vector3 relativePosition = hasPrev
                    ? Vector3.Lerp(_decoRelPositionsPrev[i], _decoRelPositionsCurr[i], alpha)
                    : _decoRelPositionsCurr[i];
                Quaternion relativeRotation = hasPrev
                    ? Quaternion.Slerp(_decoRelRotationsPrev[i], _decoRelRotationsCurr[i], alpha)
                    : _decoRelRotationsCurr[i];

                if (_decoSmootherPrimed)
                {
                    relativePosition = Vector3.Lerp(_decoSmoothedRelPositions[i], relativePosition, smoothing);
                    relativeRotation = Quaternion.Slerp(_decoSmoothedRelRotations[i], relativeRotation, smoothing);
                }

                _decoSmoothedRelPositions[i] = relativePosition;
                _decoSmoothedRelRotations[i] = relativeRotation;

                rb.transform.SetPositionAndRotation(
                    renderRootPosition + renderRootRotation * relativePosition,
                    renderRootRotation * relativeRotation);
            }

            _decoSmootherPrimed = true;
            _lastRenderRootPosition = renderRootPosition;
        }

        /// <summary>
        /// ローパスフィルタ状態の確保とリセット。
        /// リセット条件: 初回 / 装飾配列長の変化 / 描画ルートのテレポート
        /// （大ジャンプを平滑化すると装飾だけが尾を引いて見えるため、即追従させる）。
        /// </summary>
        private void EnsureDecorationSmoother(int count, Vector3 renderRootPosition)
        {
            if (_decoSmoothedRelPositions == null || _decoSmoothedRelPositions.Length != count)
            {
                _decoSmoothedRelPositions = new Vector3[count];
                _decoSmoothedRelRotations = new Quaternion[count];
                _decoSmootherPrimed = false;
                return;
            }

            if (_decoSmootherPrimed &&
                (renderRootPosition - _lastRenderRootPosition).sqrMagnitude > RootTeleportDistanceSqr)
            {
                _decoSmootherPrimed = false;
            }
        }

        private void StashDecorationPoses(Rigidbody[] decorations)
        {
            if (decorations == null || decorations.Length == 0)
            {
                return;
            }

            if (_decorationPositions == null || _decorationPositions.Length != decorations.Length)
            {
                _decorationPositions = new Vector3[decorations.Length];
                _decorationRotations = new Quaternion[decorations.Length];
            }

            for (int i = 0; i < decorations.Length; i++)
            {
                Rigidbody rb = decorations[i];
                if (rb == null) continue;
                Transform t = rb.transform;
                _decorationPositions[i] = t.position;
                _decorationRotations[i] = t.rotation;
            }
        }

        private void RestoreDecorationPoses(Rigidbody[] decorations)
        {
            if (decorations == null || decorations.Length == 0 || _decorationPositions == null)
            {
                return;
            }

            for (int i = 0; i < decorations.Length; i++)
            {
                Rigidbody rb = decorations[i];
                if (rb == null) continue;
                rb.transform.SetPositionAndRotation(_decorationPositions[i], _decorationRotations[i]);
            }
        }

        /// <summary>
        /// 未初期化スナップショットに含まれる zero quaternion を identity にフォールバックする。
        /// </summary>
        private static Quaternion SafeQuaternion(Quaternion q)
        {
            float sqrMagnitude = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            return sqrMagnitude < 0.01f ? Quaternion.identity : q;
        }
    }
}
