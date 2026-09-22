namespace TypeWhisper.Plugin.Script;

internal enum ScriptExecutionStatus
{
    Success,
    StartFailed,
    Failed,
    TimedOut,
    OutputLimitExceeded
}

internal sealed record ScriptExecutionResult(
    ScriptExecutionStatus Status,
    string Output,
    string Error,
    int? ExitCode,
    TimeSpan Elapsed)
{
    internal bool IsSuccess => Status == ScriptExecutionStatus.Success;
}

internal interface IScriptProcessRunner
{
    Task<ScriptExecutionResult> RunAsync(
        ScriptEntry script,
        string input,
        TypeWhisper.PluginSDK.Models.PostProcessingContext context,
        CancellationToken cancellationToken);
}
