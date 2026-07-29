using System;

namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// RagdollController が使う入力・状態・物理サブシステムを生成して結び付ける初期化用クラスです。
    /// </summary>
    internal sealed class RagdollRuntime
    {
        private readonly IRagdollRuntimeHost _host;

        /// <summary>
        /// 生成したサブシステムの受け渡し先となるホストを受け取ります。
        /// </summary>
        /// <param name="host">初期化結果を保持し、各サブシステムへ橋渡しするホストです。</param>
        public RagdollRuntime(IRagdollRuntimeHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>
        /// 入力・状態・物理の各サブシステムを生成し、初期状態を Idle にそろえたうえで剛体初期化まで行います。
        /// </summary>
        public void InitializeCore()
        {
            var input = new RagdollInput(_host.PhysicsContext);
            var state = new RagdollState(_host.StateContext);
            var physics = new RagdollPhysics(
                _host.PhysicsContext,
                _host.BodyParts,
                _host.BodyRigidbodies,
                _host.BodyJoints);

            _host.SetSubsystems(input, state, physics);
            _host.CurrentState = PlayerState.Idle;
            _host.SetupHandJoints();
            _host.InitializeRigidbodies();
        }
    }
}
