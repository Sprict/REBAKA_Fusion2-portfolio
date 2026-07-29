namespace MyFolder.Editor.Preflight
{
    /// <summary>プリフライトチェックの判定3値。確信が持てない場合は必ず Warning に倒す（誤緑が最悪）。</summary>
    public enum PreflightStatus
    {
        Pass,
        Warning,
        Fail,
    }

    /// <summary>1チェックの判定結果。Message は状況説明、FixHint は修正手順。</summary>
    public readonly struct PreflightResult
    {
        public readonly PreflightStatus Status;
        public readonly string Message;
        public readonly string FixHint;

        public PreflightResult(PreflightStatus status, string message, string fixHint = null)
        {
            Status = status;
            Message = message;
            FixHint = fixHint;
        }

        public static PreflightResult Pass(string message) => new PreflightResult(PreflightStatus.Pass, message);
        public static PreflightResult Warn(string message, string fixHint = null) => new PreflightResult(PreflightStatus.Warning, message, fixHint);
        public static PreflightResult Fail(string message, string fixHint = null) => new PreflightResult(PreflightStatus.Fail, message, fixHint);
    }

    /// <summary>
    /// 統合前チェックの共通インターフェース。
    /// 実装規約: プロジェクト状態の収集は Run() に、判定は static Evaluate(...) 純関数に分離し、
    /// Evaluate のみ EditMode テストで固める。
    /// </summary>
    public interface IPreflightCheck
    {
        string Name { get; }
        PreflightResult Run();
    }
}
