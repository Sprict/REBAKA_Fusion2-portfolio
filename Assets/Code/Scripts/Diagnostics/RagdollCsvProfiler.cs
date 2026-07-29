using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace MyFolder.Scripts.Diagnostics
{
    /// <summary>
    /// ラグドール物理同期の時系列データをCSVファイルに記録するプロファイラー。
    /// Inspectorの enableProfiling トグルでON/OFF可能。
    /// 出力先: Application.persistentDataPath/ragdoll_profile_*.csv
    /// Claudeが Read ツールでCSVを読み取り、自律的に分析するために使用する。
    /// </summary>
    public class RagdollCsvProfiler : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private bool enableProfiling = true;
        [SerializeField] private int bufferSize = 100;

        private static RagdollCsvProfiler _instance;
        private StringBuilder _buffer;
        private int _lineCount;
        private string _filePath;
        private bool _headerWritten;

        private const string Header =
            "time,tick,role,state,is_resim," +
            "root_pos_x,root_pos_y,root_pos_z," +
            "root_vel_x,root_vel_y,root_vel_z," +
            "net_root_pos_x,net_root_pos_y,net_root_pos_z," +
            "net_root_vel_x,net_root_vel_y,net_root_vel_z," +
            "root_error,correction_mag," +
            "snap_count,snap_this_tick," +
            "move_input_mag,is_grounded";

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }
            _instance = this;
        }

        private void OnEnable()
        {
            if (!enableProfiling) return;
            InitFile();
        }

        private void OnDestroy()
        {
            Flush();
            if (_instance == this) _instance = null;
        }

        private void OnApplicationQuit()
        {
            Flush();
        }

        private void InitFile()
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _filePath = Path.Combine(Application.persistentDataPath, $"ragdoll_profile_{timestamp}.csv");
            _buffer = new StringBuilder(4096);
            _lineCount = 0;
            _headerWritten = false;

            // ヘッダー書き込み
            _buffer.AppendLine(Header);
            _headerWritten = true;

            Debug.Log($"[RagdollCsvProfiler] Recording to: {_filePath}");
        }

        /// <summary>
        /// プロファイリングが有効かどうかを返す。
        /// Record()呼び出し前にこのフラグをチェックすることで、
        /// 無効時のRagdollProfileSample構造体の構築コストを回避できる。
        /// </summary>
        public static bool IsProfilingEnabled => _instance != null && _instance.enableProfiling;

        /// <summary>
        /// 1ティック分のデータを記録する。static呼び出しで使用。
        /// インスタンスが存在しないか無効の場合は何もしない。
        /// </summary>
        public static void Record(in RagdollProfileSample s)
        {
            if (_instance == null || !_instance.enableProfiling) return;
            _instance.RecordInternal(in s);
        }

        private void RecordInternal(in RagdollProfileSample s)
        {
            if (_buffer == null || !_headerWritten)
            {
                InitFile();
            }

            var ci = CultureInfo.InvariantCulture;
            _buffer.Append(s.time.ToString("F4", ci)).Append(',');
            _buffer.Append(s.tick).Append(',');
            _buffer.Append(s.role).Append(',');
            _buffer.Append(s.state).Append(',');
            _buffer.Append(s.isResim ? 1 : 0).Append(',');
            // root position
            _buffer.Append(s.rootPosX.ToString("F3", ci)).Append(',');
            _buffer.Append(s.rootPosY.ToString("F3", ci)).Append(',');
            _buffer.Append(s.rootPosZ.ToString("F3", ci)).Append(',');
            // root velocity
            _buffer.Append(s.rootVelX.ToString("F3", ci)).Append(',');
            _buffer.Append(s.rootVelY.ToString("F3", ci)).Append(',');
            _buffer.Append(s.rootVelZ.ToString("F3", ci)).Append(',');
            // net target position
            _buffer.Append(s.netRootPosX.ToString("F3", ci)).Append(',');
            _buffer.Append(s.netRootPosY.ToString("F3", ci)).Append(',');
            _buffer.Append(s.netRootPosZ.ToString("F3", ci)).Append(',');
            // net target velocity
            _buffer.Append(s.netRootVelX.ToString("F3", ci)).Append(',');
            _buffer.Append(s.netRootVelY.ToString("F3", ci)).Append(',');
            _buffer.Append(s.netRootVelZ.ToString("F3", ci)).Append(',');
            // errors & correction
            _buffer.Append(s.rootError.ToString("F4", ci)).Append(',');
            _buffer.Append(s.correctionMag.ToString("F4", ci)).Append(',');
            // snap
            _buffer.Append(s.snapCount).Append(',');
            _buffer.Append(s.snapThisTick ? 1 : 0).Append(',');
            // input & grounded
            _buffer.Append(s.moveInputMag.ToString("F3", ci)).Append(',');
            _buffer.AppendLine(s.isGrounded ? "1" : "0");

            _lineCount++;
            if (_lineCount >= bufferSize)
            {
                Flush();
            }
        }

        private void Flush()
        {
            if (_buffer == null || _buffer.Length == 0 || string.IsNullOrEmpty(_filePath))
                return;

            try
            {
                File.AppendAllText(_filePath, _buffer.ToString());
                _buffer.Clear();
                _lineCount = 0;
            }
            catch (Exception e)
            {
                Debug.LogError($"[RagdollCsvProfiler] Write failed: {e.Message}");
            }
        }
    }

    /// <summary>
    /// 1ティック分のプロファイルデータ。値型でGCフリー。
    /// </summary>
    public struct RagdollProfileSample
    {
        public float time;
        public int tick;
        public string role;
        public string state;
        public bool isResim;
        public float rootPosX, rootPosY, rootPosZ;
        public float rootVelX, rootVelY, rootVelZ;
        public float netRootPosX, netRootPosY, netRootPosZ;
        public float netRootVelX, netRootVelY, netRootVelZ;
        public float rootError;
        public float correctionMag;
        public int snapCount;
        public bool snapThisTick;
        public float moveInputMag;
        public bool isGrounded;
    }
}
