using System;
using MyFolder.Scripts.Diagnostics;
using UnityEngine;

namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// [Networked]データへの読み取り専用インターフェース。
    /// RagdollControllerが実装し、ClientProxyCorrectionが参照する。
    /// </summary>
    public interface IProxyPoseSource
    {
        Vector3 NetRootPosition { get; }
        Quaternion NetRootRotation { get; }
        Vector3 NetRootLinearVelocity { get; }
        Vector3 NetRootAngularVelocity { get; }
        Vector3 NetHeadPosition { get; }
        Quaternion NetHeadRotation { get; }
        Vector3 NetLeftHandPosition { get; }
        Quaternion NetLeftHandRotation { get; }
        Vector3 NetRightHandPosition { get; }
        Quaternion NetRightHandRotation { get; }
        bool IsNetProxyPoseInitialized { get; }
        bool HasInputAuthority { get; }
        bool IsResimulation { get; }
        int TickRaw { get; }
        string CurrentStateName { get; }
        Vector3 MoveDirectionValue { get; }
        bool IsPlayerGrounded { get; }
    }

    /// <summary>
    /// プロキシ補正の設定をまとめたデータオブジェクト。
    /// RagdollControllerのSerializeFieldから初期化される。
    /// </summary>
    [Serializable]
    public class ProxyCorrectionSettings
    {
        public bool proxyCorrectHeadAndHands;
        public float proxyRootPositionKp = 45f;
        public float proxyRootVelocityKd = 8f;
        public float proxyRootRotationKp = 18f;
        public float proxyRootAngularKd = 4f;
        public bool enableRootPosePrediction = true;
        public float rootPredictionLeadSeconds = 0.06f;
        public float rootPredictionLeadSecondsDuringResim = 0.01f;
        public float maxRootPredictionDistance = 1.5f;
        public float proxyPartLerpStrength = 15f;
        public float proxyHardSnapRootThreshold = 1.0f;
        public float proxyHardSnapPartThreshold = 0.6f;
        public float proxyHardSnapHoldSeconds = 0.25f;
        public float proxyInertiaForceScale = 0.35f;
        public float proxyInertiaMaxAcceleration = 10f;
        public float proxyInertiaSmoothing = 0.25f;
        public float proxySecondaryGravityScale = 0f;
    }

    /// <summary>
    /// クライアントプロキシの初期化と姿勢補正を担当する plain C# クラス。
    /// 現行の RagdollController のクライアント補正挙動を維持したまま抽出する。
    /// </summary>
    public class ClientProxyCorrection
    {
        private readonly IProxyPoseSource _source;
        private readonly ProxyCorrectionSettings _settings;
        private readonly UnityEngine.Object _diagnosticsContext;

        // コールバック: Controller側のメソッドへの委譲
        private readonly Func<Rigidbody[]> _getKinematicTargetRigidbodies;
        private readonly Action _detachRootFromParent;
        private readonly Action<bool> _setProxyVisualsEnabled;
        private readonly Func<int, Rigidbody> _tryGetBodyRigidbody;

        // ボディ参照（キャッシュ）
        private Rigidbody _rootRigidbody;
        private Rigidbody _headRigidbody;
        private Rigidbody _leftHandRigidbody;
        private Rigidbody _rightHandRigidbody;

        // 状態
        private bool _proxyBootstrapApplied;
        private float _rootErrorAboveThresholdSince = -1f;
        private float _partErrorAboveThresholdSince = -1f;
        private int _proxyPoseSnapCount;
        private float _lastCorrectionMag;
        private bool _snapThisTick;
        private float _nextProxyPoseDiagnosticsAt;
        private Vector3 _lastNetRootLinearVelocity;
        private int _lastNetRootVelocityTick;
        private Vector3 _filteredRootAcceleration;
        private bool _hasLastNetRootLinearVelocity;

        // インデックス定数
        private const int IndexRoot = 0;
        private const int IndexHead = 2;
        private const int IndexRightHand = 13;
        private const int IndexLeftHand = 14;

        public ClientProxyCorrection(
            IProxyPoseSource source,
            ProxyCorrectionSettings settings,
            UnityEngine.Object diagnosticsContext,
            Func<Rigidbody[]> getKinematicTargetRigidbodies,
            Action detachRootFromParent,
            Action<bool> setProxyVisualsEnabled,
            Func<int, Rigidbody> tryGetBodyRigidbody)
        {
            _source = source;
            _settings = settings;
            _diagnosticsContext = diagnosticsContext;
            _getKinematicTargetRigidbodies = getKinematicTargetRigidbodies;
            _detachRootFromParent = detachRootFromParent;
            _setProxyVisualsEnabled = setProxyVisualsEnabled;
            _tryGetBodyRigidbody = tryGetBodyRigidbody;
        }

        public void SetInitialState(bool bootstrapApplied)
        {
            _proxyBootstrapApplied = bootstrapApplied;
            _proxyPoseSnapCount = 0;
            _rootErrorAboveThresholdSince = -1f;
            _partErrorAboveThresholdSince = -1f;
            _hasLastNetRootLinearVelocity = false;
            _filteredRootAcceleration = Vector3.zero;
        }

        private void CacheBodyReferences()
        {
            _rootRigidbody = _tryGetBodyRigidbody(IndexRoot);
            _headRigidbody = _tryGetBodyRigidbody(IndexHead);
            _rightHandRigidbody = _tryGetBodyRigidbody(IndexRightHand);
            _leftHandRigidbody = _tryGetBodyRigidbody(IndexLeftHand);
        }

        #region Bootstrap

        public bool EnsureBootstrap()
        {
            if (_proxyBootstrapApplied)
                return true;

            if (!_source.IsNetProxyPoseInitialized)
                return false;

            if (_rootRigidbody == null)
                CacheBodyReferences();

            if (_rootRigidbody == null)
                return false;

            // 全ボディを現在のルート位置からネットワーク位置へ回転移動
            var pivot = _rootRigidbody.position;
            var rotationDelta = _source.NetRootRotation * Quaternion.Inverse(_rootRigidbody.rotation);
            var rigidbodies = _getKinematicTargetRigidbodies();
            if (rigidbodies != null)
            {
                for (var i = 0; i < rigidbodies.Length; i++)
                {
                    var rb = rigidbodies[i];
                    if (rb == null) continue;
                    var relative = rb.position - pivot;
                    rb.position = _source.NetRootPosition + rotationDelta * relative;
                    rb.rotation = rotationDelta * rb.rotation;
                }
            }

            _rootRigidbody.position = _source.NetRootPosition;
            _rootRigidbody.rotation = _source.NetRootRotation;

            if (_settings.proxyCorrectHeadAndHands)
            {
                if (_headRigidbody != null)
                {
                    _headRigidbody.position = _source.NetHeadPosition;
                    _headRigidbody.rotation = _source.NetHeadRotation;
                }

                if (_leftHandRigidbody != null)
                {
                    _leftHandRigidbody.position = _source.NetLeftHandPosition;
                    _leftHandRigidbody.rotation = _source.NetLeftHandRotation;
                }

                if (_rightHandRigidbody != null)
                {
                    _rightHandRigidbody.position = _source.NetRightHandPosition;
                    _rightHandRigidbody.rotation = _source.NetRightHandRotation;
                }
            }

            _detachRootFromParent();
            SetBootstrapRigidbodies(rigidbodies);
            ResetSecondaryMotionState(_source.NetRootLinearVelocity);

            _proxyBootstrapApplied = true;
            _setProxyVisualsEnabled(true);
            Debug.Log($"[PROXY] Bootstrap done. root=kinematic inputAuth={_source.HasInputAuthority}", _diagnosticsContext);

            return true;
        }

        private void SetBootstrapRigidbodies(Rigidbody[] rigidbodies)
        {
            if (rigidbodies == null)
                return;

            foreach (var rb in rigidbodies)
            {
                if (rb == null)
                    continue;

                if (rb == _rootRigidbody)
                {
                    rb.isKinematic = true;
                    rb.useGravity = false;
                }
                else
                {
                    rb.isKinematic = false;
                    rb.useGravity = false;
                    rb.WakeUp();
                }
            }
        }

        #endregion

        #region Pose Correction

        public void ApplyCorrection(float deltaTime)
        {
            if (!_proxyBootstrapApplied || !_source.IsNetProxyPoseInitialized)
                return;

            if (_rootRigidbody == null)
                CacheBodyReferences();

            if (_rootRigidbody == null)
                return;

            var targetRootPosition = _source.NetRootPosition;
            var targetRootRotation = _source.NetRootRotation;
            var targetRootLinearVelocity = _source.NetRootLinearVelocity;

            float hostSpeed = targetRootLinearVelocity.magnitude;
            float maxSpeed = Mathf.Max(hostSpeed * 1.5f, 3f);
            Vector3 newPosition = Vector3.MoveTowards(
                _rootRigidbody.position,
                targetRootPosition,
                maxSpeed * deltaTime);
            _rootRigidbody.MovePosition(newPosition);

            Quaternion newRotation = Quaternion.Slerp(
                _rootRigidbody.rotation,
                targetRootRotation,
                20f * deltaTime);
            _rootRigidbody.MoveRotation(newRotation);

            var rootError = Vector3.Distance(_rootRigidbody.position, targetRootPosition);
            _lastCorrectionMag = rootError;
            var headError = _settings.proxyCorrectHeadAndHands
                ? GetBodyPartError(_headRigidbody, _source.NetHeadPosition) : 0f;
            var leftHandError = _settings.proxyCorrectHeadAndHands
                ? GetBodyPartError(_leftHandRigidbody, _source.NetLeftHandPosition) : 0f;
            var rightHandError = _settings.proxyCorrectHeadAndHands
                ? GetBodyPartError(_rightHandRigidbody, _source.NetRightHandPosition) : 0f;
            var maxPartError = Mathf.Max(headError, leftHandError, rightHandError);

            if (_settings.proxyCorrectHeadAndHands)
            {
                ApplySoftPartCorrection(_headRigidbody, _source.NetHeadPosition, _source.NetHeadRotation, deltaTime);
                ApplySoftPartCorrection(_leftHandRigidbody, _source.NetLeftHandPosition, _source.NetLeftHandRotation, deltaTime);
                ApplySoftPartCorrection(_rightHandRigidbody, _source.NetRightHandPosition, _source.NetRightHandRotation, deltaTime);
            }

            UpdateErrorTimer(ref _rootErrorAboveThresholdSince, rootError > _settings.proxyHardSnapRootThreshold);
            if (_settings.proxyCorrectHeadAndHands)
            {
                UpdateErrorTimer(ref _partErrorAboveThresholdSince, maxPartError > _settings.proxyHardSnapPartThreshold);
            }
            else
            {
                _partErrorAboveThresholdSince = -1f;
            }

            EmitProxyPoseDiagnostics(rootError, headError, leftHandError, rightHandError);

            if (IsErrorHeld(_rootErrorAboveThresholdSince) || IsErrorHeld(_partErrorAboveThresholdSince))
            {
                HardSnapProxyPose(rootError, headError, leftHandError, rightHandError);
            }

            if (_snapThisTick)
            {
                ResetSecondaryMotionState(targetRootLinearVelocity);
            }
            else
            {
                // root補正と分離して、非rootパーツにだけ慣性由来の二次運動を足す。
                ApplySecondaryMotion(targetRootLinearVelocity, deltaTime);
            }

            // CSV プロファイリング（クライアント側）
            if (RagdollCsvProfiler.IsProfilingEnabled)
            {
                RagdollCsvProfiler.Record(new RagdollProfileSample
                {
                    time = Time.realtimeSinceStartup,
                    tick = _source.TickRaw,
                    role = "Client",
                    state = _source.CurrentStateName,
                    isResim = _source.IsResimulation,
                    rootPosX = _rootRigidbody.position.x,
                    rootPosY = _rootRigidbody.position.y,
                    rootPosZ = _rootRigidbody.position.z,
                    rootVelX = _rootRigidbody.linearVelocity.x,
                    rootVelY = _rootRigidbody.linearVelocity.y,
                    rootVelZ = _rootRigidbody.linearVelocity.z,
                    netRootPosX = targetRootPosition.x,
                    netRootPosY = targetRootPosition.y,
                    netRootPosZ = targetRootPosition.z,
                    netRootVelX = targetRootLinearVelocity.x,
                    netRootVelY = targetRootLinearVelocity.y,
                    netRootVelZ = targetRootLinearVelocity.z,
                    rootError = rootError,
                    correctionMag = _lastCorrectionMag,
                    snapCount = _proxyPoseSnapCount,
                    snapThisTick = _snapThisTick,
                    moveInputMag = _source.MoveDirectionValue.magnitude,
                    isGrounded = _source.IsPlayerGrounded
                });
            }
            _snapThisTick = false;
        }

        private void ApplySoftPartCorrection(Rigidbody bodyPart, Vector3 targetPosition,
            Quaternion targetRotation, float deltaTime)
        {
            if (bodyPart == null) return;

            var dt = Mathf.Max(deltaTime, 0.0001f);
            var t = 1f - Mathf.Exp(-_settings.proxyPartLerpStrength * dt);

            bodyPart.position = Vector3.Lerp(bodyPart.position, targetPosition, t);
            bodyPart.rotation = Quaternion.Slerp(bodyPart.rotation, targetRotation, t);
        }

        private void ApplySecondaryMotion(Vector3 targetRootLinearVelocity, float deltaTime)
        {
            if (deltaTime <= 0f)
                return;

            // Resimulation で力を積むと同じ tick の AddForce が再生されてしまうため、状態だけ再seedする。
            if (_source.IsResimulation)
            {
                ResetSecondaryMotionState(targetRootLinearVelocity);
                return;
            }

            Vector3 inertiaAcceleration = CalculateInertiaAcceleration(targetRootLinearVelocity);
            Vector3 gravityAcceleration = _settings.proxySecondaryGravityScale > 0f
                ? Physics.gravity * _settings.proxySecondaryGravityScale
                : Vector3.zero;

            if (inertiaAcceleration.sqrMagnitude <= 0.000001f && gravityAcceleration.sqrMagnitude <= 0.000001f)
                return;

            Rigidbody[] rigidbodies = _getKinematicTargetRigidbodies();
            if (rigidbodies == null)
                return;

            for (int i = 0; i < rigidbodies.Length; i++)
            {
                Rigidbody body = rigidbodies[i];
                if (!ShouldReceiveSecondaryMotion(body))
                    continue;

                if (inertiaAcceleration.sqrMagnitude > 0.000001f)
                {
                    body.AddForce(inertiaAcceleration, ForceMode.Acceleration);
                }

                if (gravityAcceleration.sqrMagnitude > 0.000001f)
                {
                    body.AddForce(gravityAcceleration, ForceMode.Acceleration);
                }
            }
        }

        private Vector3 CalculateInertiaAcceleration(Vector3 targetRootLinearVelocity)
        {
            if (!_hasLastNetRootLinearVelocity)
            {
                ResetSecondaryMotionState(targetRootLinearVelocity);
                return Vector3.zero;
            }

            if (_settings.proxyInertiaForceScale <= 0f || _settings.proxyInertiaMaxAcceleration <= 0f)
            {
                _filteredRootAcceleration = Vector3.zero;
                return Vector3.zero;
            }

            int currentTick = _source.TickRaw;
            int tickDelta = currentTick - _lastNetRootVelocityTick;
            _lastNetRootVelocityTick = currentTick;
            if (tickDelta <= 0)
                return Vector3.zero;

            Vector3 previousRootVelocity = _lastNetRootLinearVelocity;
            _lastNetRootLinearVelocity = targetRootLinearVelocity;
            float actualDeltaTime = tickDelta * Time.fixedDeltaTime;

            Vector3 rawAcceleration = (targetRootLinearVelocity - previousRootVelocity) / actualDeltaTime;
            float smoothing = Mathf.Clamp01(_settings.proxyInertiaSmoothing);
            _filteredRootAcceleration = Vector3.Lerp(_filteredRootAcceleration, rawAcceleration, smoothing);

            Vector3 clampedAcceleration =
                Vector3.ClampMagnitude(_filteredRootAcceleration, _settings.proxyInertiaMaxAcceleration);
            return -clampedAcceleration * _settings.proxyInertiaForceScale;
        }

        private bool ShouldReceiveSecondaryMotion(Rigidbody body)
        {
            if (body == null || body == _rootRigidbody || body.isKinematic)
                return false;

            if (_settings.proxyCorrectHeadAndHands &&
                (body == _headRigidbody || body == _leftHandRigidbody || body == _rightHandRigidbody))
            {
                return false;
            }

            return true;
        }

        private void ResetSecondaryMotionState(Vector3 targetRootLinearVelocity)
        {
            _lastNetRootLinearVelocity = targetRootLinearVelocity;
            _lastNetRootVelocityTick = _source.TickRaw;
            _filteredRootAcceleration = Vector3.zero;
            _hasLastNetRootLinearVelocity = true;
        }

        private void HardSnapProxyPose(float rootError, float headError, float leftHandError, float rightHandError)
        {
            if (_rootRigidbody != null)
            {
                _rootRigidbody.position = _source.NetRootPosition;
                _rootRigidbody.rotation = _source.NetRootRotation;
                _rootRigidbody.linearVelocity = _source.NetRootLinearVelocity;
                _rootRigidbody.angularVelocity = _source.NetRootAngularVelocity;
            }

            if (_settings.proxyCorrectHeadAndHands && _headRigidbody != null)
            {
                _headRigidbody.position = _source.NetHeadPosition;
                _headRigidbody.rotation = _source.NetHeadRotation;
            }

            if (_settings.proxyCorrectHeadAndHands && _leftHandRigidbody != null)
            {
                _leftHandRigidbody.position = _source.NetLeftHandPosition;
                _leftHandRigidbody.rotation = _source.NetLeftHandRotation;
            }

            if (_settings.proxyCorrectHeadAndHands && _rightHandRigidbody != null)
            {
                _rightHandRigidbody.position = _source.NetRightHandPosition;
                _rightHandRigidbody.rotation = _source.NetRightHandRotation;
            }

            _rootErrorAboveThresholdSince = -1f;
            _partErrorAboveThresholdSince = -1f;
            _proxyPoseSnapCount++;
            _snapThisTick = true;

            RagdollNetDiagnostics.Log(
                "proxy_pose_snap",
                $"role=Client snap_count={_proxyPoseSnapCount} root_error={rootError:F3} " +
                $"head_error={headError:F3} left_hand_error={leftHandError:F3} right_hand_error={rightHandError:F3}",
                _diagnosticsContext,
                0f);
        }

        #endregion

        #region Diagnostics

        private void EmitProxyPoseDiagnostics(float rootError, float headError, float leftHandError,
            float rightHandError)
        {
            if (!RagdollNetDiagnostics.IsEnabled) return;

            var now = Time.realtimeSinceStartup;
            if (now < _nextProxyPoseDiagnosticsAt) return;
            _nextProxyPoseDiagnosticsAt = now + 0.2f;

            RagdollNetDiagnostics.Log(
                "proxy_pose_error",
                $"role=Client root_error={rootError:F3} head_error={headError:F3} " +
                $"left_hand_error={leftHandError:F3} right_hand_error={rightHandError:F3} " +
                $"snap_count={_proxyPoseSnapCount}",
                _diagnosticsContext);
        }

        #endregion

        #region Utility

        private static float GetBodyPartError(Rigidbody bodyPart, Vector3 targetPosition)
        {
            if (bodyPart == null) return 0f;
            return Vector3.Distance(bodyPart.position, targetPosition);
        }
        private static void UpdateErrorTimer(ref float timer, bool exceeded)
        {
            if (!exceeded)
            {
                timer = -1f;
                return;
            }

            if (timer < 0f)
            {
                timer = Time.realtimeSinceStartup;
            }
        }

        private bool IsErrorHeld(float timer)
        {
            return timer >= 0f && Time.realtimeSinceStartup - timer >= _settings.proxyHardSnapHoldSeconds;
        }

        #endregion
    }
}
