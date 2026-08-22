using System.IO;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Retries failed workflow post-processing in place without producing user output.
/// </summary>
public sealed class HistoryWorkflowRetryService
{
    private readonly IHistoryService _history;
    private readonly IWorkflowService _workflows;
    private readonly ISettingsService _settings;
    private readonly IWorkflowPostProcessingService _postProcessing;
    private readonly DictationRecoveryAudioStore _recoveryStore;

    /// <summary>
    /// Creates a history workflow retry service.
    /// </summary>
    public HistoryWorkflowRetryService(
        IHistoryService history,
        IWorkflowService workflows,
        ISettingsService settings,
        IWorkflowPostProcessingService postProcessing,
        DictationRecoveryAudioStore recoveryStore)
    {
        _history = history;
        _workflows = workflows;
        _settings = settings;
        _postProcessing = postProcessing;
        _recoveryStore = recoveryStore;
    }

    /// <summary>
    /// Returns every current workflow that has a usable post-processing prompt.
    /// </summary>
    public IReadOnlyList<Workflow> GetEligibleWorkflows() =>
        _workflows.Workflows
            .Where(workflow => WorkflowPostProcessingService.HasWorkflowPrompt(
                workflow,
                detectedLanguage: null,
                configuredLanguage: _settings.Current.GetLanguageHints().FirstOrDefault()))
            .OrderBy(workflow => workflow.SortOrder)
            .ThenBy(workflow => workflow.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// Selects the current workflow by stable id or, for legacy records, one unique name match.
    /// </summary>
    public Workflow? ResolveInitialWorkflow(TranscriptionRecord record)
    {
        var eligible = GetEligibleWorkflows();
        if (!string.IsNullOrWhiteSpace(record.WorkflowId))
        {
            return eligible.FirstOrDefault(workflow =>
                string.Equals(workflow.Id, record.WorkflowId, StringComparison.Ordinal));
        }

        if (string.IsNullOrWhiteSpace(record.ProfileName))
            return null;

        var named = eligible.Where(workflow =>
                string.Equals(workflow.Name, record.ProfileName, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return named.Count == 1 ? named[0] : null;
    }

    /// <summary>
    /// Reprocesses one failed record and atomically replaces that same record.
    /// </summary>
    public async Task<TranscriptionRecord> RetryAsync(
        string recordId,
        string workflowId,
        Func<string, Task>? statusCallback,
        CancellationToken cancellationToken)
    {
        await _history.EnsureLoadedAsync().ConfigureAwait(false);
        var record = _history.Records.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, recordId, StringComparison.Ordinal));
        if (record is null)
            throw new InvalidOperationException("The history record no longer exists.");
        if (record.Status != TranscriptionRecordStatus.WorkflowPostProcessingFailed
            || string.IsNullOrWhiteSpace(record.RawText))
        {
            throw new InvalidOperationException("Only failed workflow records with raw text can be retried.");
        }

        var workflow = GetEligibleWorkflows().FirstOrDefault(candidate =>
            string.Equals(candidate.Id, workflowId, StringComparison.Ordinal));
        if (workflow is null)
            throw new InvalidOperationException("Select an available workflow before retrying.");

        try
        {
            var current = _settings.Current;
            var languageHints = current.GetLanguageHints();
            var task = string.Equals(record.TranscriptionTaskUsed, nameof(TranscriptionTask.Translate), StringComparison.OrdinalIgnoreCase)
                       || string.Equals(record.TranscriptionTaskUsed, "translate", StringComparison.OrdinalIgnoreCase)
                ? TranscriptionTask.Translate
                : TranscriptionTask.Transcribe;
            var result = await _postProcessing.ProcessAsync(
                new WorkflowPostProcessingRequest(
                    record.RawText,
                    workflow,
                    record.Language,
                    languageHints.FirstOrDefault(),
                    languageHints,
                    task,
                    record.EngineUsed,
                    record.ModelUsed,
                    record.AppName,
                    record.AppProcessName,
                    record.DurationSeconds),
                statusCallback,
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result.Text))
                throw new InvalidOperationException("The workflow returned an empty response.");

            var succeeded = record with
            {
                FinalText = result.Text,
                ProfileName = workflow.Name,
                WorkflowId = workflow.Id,
                Status = TranscriptionRecordStatus.Succeeded,
                WorkflowFailureMessage = null,
                RecoveryAudioFileName = null
            };
            if (!_history.TryReplaceRecord(succeeded))
                throw new IOException("The updated history record could not be saved.");

            if (!string.IsNullOrWhiteSpace(record.RecoveryAudioFileName))
            {
                var recovery = _recoveryStore.Recordings.FirstOrDefault(candidate =>
                    string.Equals(candidate.FileName, record.RecoveryAudioFileName, StringComparison.OrdinalIgnoreCase));
                if (recovery is not null)
                {
                    try
                    {
                        _ = await _recoveryStore.DeleteAsync(
                            recovery.Id,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Successful text persistence must not be reverted during app shutdown.
                    }
                }
            }

            return succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failed = record with
            {
                FinalText = string.Empty,
                ProfileName = workflow.Name,
                WorkflowId = workflow.Id,
                Status = TranscriptionRecordStatus.WorkflowPostProcessingFailed,
                WorkflowFailureMessage = SanitizeFailure(ex, record.RawText)
            };
            _ = _history.TryReplaceRecord(failed);
            throw;
        }
    }

    internal static string SanitizeFailure(Exception exception, string? sensitiveText = null)
    {
        var message = exception is PluginRequestException requestFailure
            ? requestFailure.FailureKind switch
            {
                PluginRequestFailureKind.Network => "The workflow provider could not be reached.",
                PluginRequestFailureKind.Timeout => "The workflow provider request timed out.",
                PluginRequestFailureKind.RateLimit => "The workflow provider rate limit was reached.",
                PluginRequestFailureKind.ServerError => "The workflow provider returned a server error.",
                PluginRequestFailureKind.EmptyResponse => "The workflow provider returned an empty response.",
                PluginRequestFailureKind.OutputTruncated => "The workflow provider stopped at its output token limit.",
                PluginRequestFailureKind.Authentication => "Workflow provider authentication failed.",
                PluginRequestFailureKind.Permission => "The workflow provider denied this request.",
                PluginRequestFailureKind.Configuration => "The workflow provider is not configured correctly.",
                PluginRequestFailureKind.RequestTooLarge => "The workflow request was too large.",
                PluginRequestFailureKind.InvalidRequest => "The workflow provider rejected the request.",
                PluginRequestFailureKind.Cancellation => "The workflow request was cancelled.",
                _ => "The workflow provider request failed."
            }
            : exception.Message;
        if (!string.IsNullOrEmpty(sensitiveText))
            message = message.Replace(sensitiveText, "[dictated text]", StringComparison.Ordinal);
        message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.IsNullOrWhiteSpace(message))
            return "Workflow post-processing failed.";
        return message.Length <= 300 ? message : string.Concat(message.AsSpan(0, 300), "...");
    }
}
