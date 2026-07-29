using System;
using System.Collections.Generic;
using MyFolder.Scripts.Player;
using NUnit.Framework;
using UnityEngine;

namespace MyFolder.Tests.EditMode
{
    public sealed class ClientProxyCorrectionSecondaryMotionTests
    {
        private readonly List<GameObject> _gameObjects = new();
        private SimulationMode _originalSimulationMode;

        [SetUp]
        public void SetUp()
        {
            _originalSimulationMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
        }

        [TearDown]
        public void TearDown()
        {
            Physics.simulationMode = _originalSimulationMode;

            for (int i = 0; i < _gameObjects.Count; i++)
            {
                if (_gameObjects[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(_gameObjects[i]);
                }
            }

            _gameObjects.Clear();
        }

        [Test]
        public void ApplyCorrection_AddsSecondaryMotion_WhenNetRootVelocityChanges()
        {
            Harness harness = CreateHarness();
            const float deltaTime = 0.02f;

            Assert.That(harness.Correction.EnsureBootstrap(), Is.True);

            // CalculateInertiaAcceleration は tick が進まないと力を加えない
            // （1f67272 で追加された resim/同一tick二重積分ガード）ため、tick を進める
            harness.Source.NetRootLinearVelocityValue = Vector3.zero;
            harness.Source.TickRawValue = 1;
            harness.Correction.ApplyCorrection(deltaTime);
            Physics.Simulate(deltaTime);

            harness.Source.NetRootLinearVelocityValue = new Vector3(0f, 0f, 5f);
            harness.Source.TickRawValue = 2;
            harness.Correction.ApplyCorrection(deltaTime);
            Physics.Simulate(deltaTime);

            Assert.That(
                harness.DynamicBody.linearVelocity.z,
                Is.LessThan(-0.01f),
                "root が前方へ加速したとき、非 root ボディには逆向きの慣性が必要");
        }

        [Test]
        public void ApplyCorrection_DoesNotAddSecondaryMotion_WhenInertiaScaleIsZero()
        {
            Harness harness = CreateHarness();
            const float deltaTime = 0.02f;

            harness.Settings.proxyInertiaForceScale = 0f;

            Assert.That(harness.Correction.EnsureBootstrap(), Is.True);

            // tick を進めることで tickDelta ガードではなく inertiaScale=0 分岐を検証する
            harness.Source.NetRootLinearVelocityValue = Vector3.zero;
            harness.Source.TickRawValue = 1;
            harness.Correction.ApplyCorrection(deltaTime);
            Physics.Simulate(deltaTime);

            harness.Source.NetRootLinearVelocityValue = new Vector3(0f, 0f, 5f);
            harness.Source.TickRawValue = 2;
            harness.Correction.ApplyCorrection(deltaTime);
            Physics.Simulate(deltaTime);

            Assert.That(harness.DynamicBody.linearVelocity.sqrMagnitude, Is.LessThan(0.000001f));
        }

        private Harness CreateHarness()
        {
            GameObject rootObject = CreateGameObject("Root");
            GameObject dynamicObject = CreateGameObject("DynamicBody");

            Rigidbody root = rootObject.AddComponent<Rigidbody>();
            Rigidbody dynamicBody = dynamicObject.AddComponent<Rigidbody>();

            root.isKinematic = true;
            root.useGravity = false;
            root.position = Vector3.zero;
            root.rotation = Quaternion.identity;

            dynamicBody.isKinematic = false;
            dynamicBody.useGravity = false;
            dynamicBody.linearVelocity = Vector3.zero;
            dynamicBody.angularVelocity = Vector3.zero;
            dynamicBody.position = new Vector3(0f, 1f, 0f);

            RecordingProxyPoseSource source = new RecordingProxyPoseSource
            {
                IsNetProxyPoseInitializedValue = true,
                NetRootPositionValue = Vector3.zero,
                NetRootRotationValue = Quaternion.identity,
                NetRootLinearVelocityValue = Vector3.zero,
                NetRootAngularVelocityValue = Vector3.zero
            };

            ProxyCorrectionSettings settings = new ProxyCorrectionSettings
            {
                proxyCorrectHeadAndHands = false,
                proxyPartLerpStrength = 15f,
                proxyHardSnapRootThreshold = 10f,
                proxyHardSnapPartThreshold = 10f,
                proxyHardSnapHoldSeconds = 1f
            };

            ClientProxyCorrection correction = new ClientProxyCorrection(
                source,
                settings,
                rootObject,
                () => new[] { root, dynamicBody },
                () => { },
                _ => { },
                index => index switch
                {
                    0 => root,
                    1 => dynamicBody,
                    _ => null
                });

            correction.SetInitialState(false);

            return new Harness(source, settings, correction, dynamicBody);
        }

        private GameObject CreateGameObject(string name)
        {
            GameObject gameObject = new GameObject(name);
            _gameObjects.Add(gameObject);
            return gameObject;
        }

        private sealed class Harness
        {
            public Harness(
                RecordingProxyPoseSource source,
                ProxyCorrectionSettings settings,
                ClientProxyCorrection correction,
                Rigidbody dynamicBody)
            {
                Source = source;
                Settings = settings;
                Correction = correction;
                DynamicBody = dynamicBody;
            }

            public RecordingProxyPoseSource Source { get; }
            public ProxyCorrectionSettings Settings { get; }
            public ClientProxyCorrection Correction { get; }
            public Rigidbody DynamicBody { get; }
        }

        private sealed class RecordingProxyPoseSource : IProxyPoseSource
        {
            public Vector3 NetRootPositionValue;
            public Quaternion NetRootRotationValue;
            public Vector3 NetRootLinearVelocityValue;
            public Vector3 NetRootAngularVelocityValue;
            public Vector3 NetHeadPositionValue;
            public Quaternion NetHeadRotationValue = Quaternion.identity;
            public Vector3 NetLeftHandPositionValue;
            public Quaternion NetLeftHandRotationValue = Quaternion.identity;
            public Vector3 NetRightHandPositionValue;
            public Quaternion NetRightHandRotationValue = Quaternion.identity;
            public bool IsNetProxyPoseInitializedValue;
            public int TickRawValue;

            public Vector3 NetRootPosition => NetRootPositionValue;
            public Quaternion NetRootRotation => NetRootRotationValue;
            public Vector3 NetRootLinearVelocity => NetRootLinearVelocityValue;
            public Vector3 NetRootAngularVelocity => NetRootAngularVelocityValue;
            public Vector3 NetHeadPosition => NetHeadPositionValue;
            public Quaternion NetHeadRotation => NetHeadRotationValue;
            public Vector3 NetLeftHandPosition => NetLeftHandPositionValue;
            public Quaternion NetLeftHandRotation => NetLeftHandRotationValue;
            public Vector3 NetRightHandPosition => NetRightHandPositionValue;
            public Quaternion NetRightHandRotation => NetRightHandRotationValue;
            public bool IsNetProxyPoseInitialized => IsNetProxyPoseInitializedValue;
            public bool HasInputAuthority => false;
            public bool IsResimulation => false;
            public int TickRaw => TickRawValue;
            public string CurrentStateName => "Idle";
            public Vector3 MoveDirectionValue => Vector3.zero;
            public bool IsPlayerGrounded => true;
        }
    }
}
