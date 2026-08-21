using System.Runtime.CompilerServices;

// Editor ツールの判定ロジックを internal のまま EditMode テストから検証するため。
// Assets/Code/Scripts/AssemblyInfo.cs と同じ方針（公開 API を増やさずにテストする）。
[assembly: InternalsVisibleTo("MyProject.Code.Editor.Tests")]
