using UnityEngine;
using UnityEngine.InputSystem;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MyFolder.Scripts.Settings
{
    /// <summary>
    /// 右スティックの「倒し量＝レート」を Look デルタ等価に変換する。
    /// overrideProcessors は authored processors を置き換えるため、感度 scale と必ずチェーンする。
    /// </summary>
    /// <remarks>
    /// 基準 480/秒 = 旧仕様「感度 8.0 × 60fps」。OrbitCamera の orbitSensitivityX(0.2) と合わせると
    /// 480 × 0.2 = 96 度/秒（旧 orbitStickDegreesPerSecond）。
    /// </remarks>
#if UNITY_EDITOR
    [InitializeOnLoad]
#endif
    public sealed class StickToLookDeltaProcessor : InputProcessor<Vector2>
    {
        public const float DefaultUnitsPerSecond = 480f;

        [Tooltip("フル倒し時の Look デルタ等価(/秒)。感度設定 1.0 のときの基準。")]
        public float unitsPerSecond = DefaultUnitsPerSecond;

#if UNITY_EDITOR
        static StickToLookDeltaProcessor()
        {
            Initialize();
        }
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            InputSystem.RegisterProcessor<StickToLookDeltaProcessor>();
        }

        public override Vector2 Process(Vector2 value, InputControl control)
        {
            return Convert(value, unitsPerSecond, Time.deltaTime);
        }

        /// <summary>純関数。EditMode でフレームレート非依存を検証するため公開。</summary>
        internal static Vector2 Convert(Vector2 stick, float unitsPerSecond, float deltaTime)
        {
            return stick * unitsPerSecond * deltaTime;
        }
    }
}
