using Fusion;
using UnityEngine;
using System.Collections.Generic;

//-------------------------------------------------------------
//--APR Player
//--カメラコントローラー
//
//--Unity Asset Store - バージョン 1.0
//
//--作成者: The Famous Mouse
//
//--Twitter @FamousMouse_Dev
//--Youtube TheFamouseMouse
//-------------------------------------------------------------

namespace MyFolder.Scripts.Camera
{
    /// <summary>
    /// マルチプレイヤー対応のカメラコントローラー
    /// 全プレイヤーが画面に収まるように自動調整します。
    /// MonoBehaviour として動作し、NetworkObject は不要です。
    /// </summary>
    public class MyCameraController : MonoBehaviour
    {
        #region フィールド
        // [Header("追跡対象")] 
        // public Transform APRRoot; // 単一プレイヤー追跡用（現在は複数プレイヤーリストに置き換え済み）

        [Header("フォロー設定")]
        [Tooltip("カメラ移動のなめらかさ（値が大きいほど動きがゆっくりになる）")]
        public float smoothness = 0.25f; // 大きな動きにも対応できるように値を大きめに設定
        
        [Tooltip("プレイヤーからの最小距離")]
        public float minDistance = 5.0f;
        
        [Tooltip("プレイヤーからの最大距離")]
        public float maxDistance = 30.0f; // 複数人対応のため最大距離を拡大
        
        [Tooltip("カメラの位置オフセット（主に高さ調整用）")]
        public Vector3 offset = new Vector3(0, 3f, 0); // グループ中心からの高さオフセット。XとZは必要に応じて調整可能。
        
        [Tooltip("プレイヤーの周囲に追加する余白（画面に収める際の余裕）")]
        public float targetPadding = 1.5f; // プレイヤーの枠に余白を追加

        // [Header("回転設定")] // 回転は自動化されたため不要
        // public bool rotateCamera = true; // マウス回転は現在使用していない
        // public float rotateSpeed = 5.0f; // グループ全体を見るためマウス回転は無効化
        // public float minAngle = -45.0f;  // 上下の回転制限（下限）も不要
        // public float maxAngle = -10.0f;  // 上下の回転制限（上限）も不要


        //プライベート変数
        private UnityEngine.Camera _cam; // メインカメラへの参照
        
        // 以下はマウス回転用の旧コード（現在は不使用）
        // private float currentX = 0.0f; // X軸回転（水平方向）
        // private float currentY = 0.0f; // Y軸回転（垂直方向）
        // private Quaternion rotation; // 計算された回転
        // private Vector3 dir; // 方向ベクトル
        // private Vector3 originalOffset; // 初期オフセット

        /// <summary>
        /// 追跡対象のプレイヤーリスト
        /// </summary>
        private List<Transform> _playerTargets = new List<Transform>();
        
        /// <summary>
        /// SmoothDamp関数用の速度参照変数
        /// </summary>
        private Vector3 _currentVelocity = Vector3.zero; // 位置の滑らかな補間用
        
        #endregion

        #region プロパティ
        // プロパティ宣言
        #endregion

        #region Unityライフサイクルメソッド
        /// <summary>
        /// 初期化処理
        /// </summary>
        void Start()
        {
            // マウスカーソル設定
            // Cursor.lockState = CursorLockMode.Locked; // マウス操作を使わないため不要
            // Cursor.visible = false; // 同上
            Cursor.lockState = CursorLockMode.None; // カーソルをロックしない（UIで使用可能に）
            Cursor.visible = true; // カーソルを表示

            // メインカメラの取得
            _cam = UnityEngine.Camera.main;
            if (_cam == null)
            {
                Debug.LogError("メインカメラが見つかりません！");
                enabled = false; // カメラがない場合はコンポーネントを無効化
                return;
            }
            
            // 以前の実装方法（単一プレイヤー追跡時）
            // originalOffset = cam.transform.position - (APRRoot != null ? APRRoot.position : Vector3.zero);
        }

        // Update()メソッドは不要になったため削除
        // 以前はマウス入力を処理していたが、現在はネットワーク更新に統合

        /// <summary>
        /// 毎フレームカメラを更新（NetworkBehaviourではなくなったためUpdateを使用）
        /// </summary>
        void Update()
        {
            // カメラの存在確認
            if (_cam == null) return;

            // プレイヤーリストの更新
            UpdatePlayerTargets();

            // プレイヤーがいない場合は処理しない
            if (_playerTargets.Count == 0)
            {
                // プレイヤーがいない場合は何もしない
                // 必要に応じてデフォルト位置に戻すなどの処理を追加できます
                return;
            }

            // カメラの移動と画角調整
            MoveAndFramePlayers();
        }

        /// <summary>
        /// アクティブなNetworkRunnerを取得する
        /// </summary>
        private NetworkRunner GetActiveRunner()
        {
            foreach (var runner in NetworkRunner.Instances)
            {
                if (runner != null && runner.IsRunning)
                    return runner;
            }
            return null;
        }

        /// <summary>
        /// アクティブなプレイヤーのリストを更新
        /// </summary>
        void UpdatePlayerTargets()
        {
            // 毎回リストをクリアして最新状態を取得
            _playerTargets.Clear();
            
            var runner = GetActiveRunner();
            if (runner == null || !runner.IsRunning) return;

            // アクティブな全プレイヤーをリストに追加
            foreach (PlayerRef playerRef in runner.ActivePlayers)
            {
                NetworkObject playerNo = runner.GetPlayerObject(playerRef);
                if (playerNo != null)
                {
                    // プレイヤーのNetworkObjectのTransformを追跡対象とする
                    // 必要に応じて特定の子オブジェクト（例：ボディ、ルートなど）を
                    // 追跡することも可能
                    _playerTargets.Add(playerNo.transform);
                }
            }
        }

        /// <summary>
        /// プレイヤー全体が見えるようにカメラを移動・調整
        /// </summary>
        void MoveAndFramePlayers()
        {
            // 安全チェック
            if (_playerTargets.Count == 0) return;

            // プレイヤー全体を囲むバウンディングボックスを計算
            Bounds playerBounds = CalculatePlayerBounds();
            Vector3 groupCenter = playerBounds.center; // グループの中心点

            // 全プレイヤーを画面に収めるために必要なカメラ距離を計算
            // FoV（視野角）に基づいて、横幅を基準に距離を算出
            float pseudoWidth = Mathf.Max(playerBounds.size.x, playerBounds.size.z); // X/Z軸の大きい方を使用
            float requiredDistance = (pseudoWidth * 0.5f / Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad)) + targetPadding;
            
            // 以下は縦方向も考慮する場合の計算方法（より複雑だがより正確）
            // 現在はコメントアウトされているが、縦横比を考慮した計算が必要な場合は有効化
            // float screenAspect = (float)Screen.width / Screen.height;
            // float fovRad = cam.fieldOfView * Mathf.Deg2Rad;
            // float distForWidth = playerBounds.size.x / (2.0f * Mathf.Tan(fovRad * 0.5f * screenAspect));
            // float distForHeight = playerBounds.size.y / (2.0f * Mathf.Tan(fovRad * 0.5f));
            // requiredDistance = Mathf.Max(distForWidth, distForHeight) + targetPadding;

            // 計算された距離を最小・最大範囲内に収める
            requiredDistance = Mathf.Clamp(requiredDistance, minDistance, maxDistance);

            // カメラの理想的な位置を計算
            // 1. 現在のカメラのヨー（水平回転）は維持
            // 2. 高さと距離は計算値に基づいて調整
            // 3. offset.yが正の値なら、カメラは上から見下ろす形になる
            Quaternion cameraYawRotation = Quaternion.Euler(0, _cam.transform.eulerAngles.y, 0);
            Vector3 directionFromTarget = cameraYawRotation * (-Vector3.forward); // ターゲットからカメラへの方向

            // 最終的なカメラ位置の計算
            // グループ中心 + 高さオフセット + (方向 * 距離)
            Vector3 desiredPosition = groupCenter + new Vector3(0, offset.y, 0) + directionFromTarget * requiredDistance;

            // カメラを目標位置へ滑らかに移動（SmoothDamp関数を使用）
            _cam.transform.position = Vector3.SmoothDamp(
                _cam.transform.position, 
                desiredPosition, 
                ref _currentVelocity, 
                smoothness
            );

            // カメラの注視点（LookAt）を計算
            // デフォルトではバウンディングボックスの中央高さ付近を見る
            Vector3 lookAtPoint = groupCenter + new Vector3(0, playerBounds.extents.y * 0.5f, 0);
            
            // プレイヤーが1人だけで、高さオフセットがない場合は単純に中心を見る
            if (_playerTargets.Count == 1 && offset.y == 0) lookAtPoint = groupCenter;
            
            // カメラの回転を滑らかに調整（Slerp関数を使用）
            Quaternion targetRotation = Quaternion.LookRotation(lookAtPoint - _cam.transform.position);
            _cam.transform.rotation = Quaternion.Slerp(
                _cam.transform.rotation, 
                targetRotation, 
                smoothness
            );
        }

        /// <summary>
        /// 全プレイヤーを囲むバウンディングボックスを計算
        /// </summary>
        /// <returns>プレイヤー全体を囲むBounds</returns>
        Bounds CalculatePlayerBounds()
        {
            // プレイヤーがいない場合はデフォルト値を返す
            if (_playerTargets.Count == 0)
                return new Bounds(
                    _cam != null ? _cam.transform.position : Vector3.zero, // 中心位置
                    Vector3.one // サイズ（1x1x1の立方体）
                );

            // 最初のプレイヤーを基準にバウンドを初期化
            Bounds bounds = new Bounds(_playerTargets[0].position, Vector3.zero);
            
            // 残りのプレイヤーをバウンドに追加
            for (int i = 1; i < _playerTargets.Count; i++)
            {
                bounds.Encapsulate(_playerTargets[i].position);
            }
            
            // バウンドが極端に小さい場合（例：プレイヤーが1人で動いていない）
            // 最小サイズを確保して過度なズームインを防止
            if (bounds.size.sqrMagnitude < 0.1f) {
                bounds.Expand(1.0f); // 全方向に1ユニット拡張
            }
            return bounds;
        }
        #endregion

        #region 公開メソッド
        /// <summary>
        /// プレイヤーの足が地面に接地しているかを取得
        /// </summary>
        /// <returns>いずれかの足が地面に接地している場合true</returns>
        public bool IsPlayerGrounded()
        {
            // 注：このメソッドはカメラとは無関係で、
            // 実装が完了していません。将来的に別クラスに移動すべきかもしれません。
            return false; // 仮実装、実際の処理が必要
        }
        #endregion

        #region プライベートメソッド
        // 追加のプライベートメソッドがある場合はここに実装
        #endregion
    }
}
