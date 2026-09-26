using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>Resolves a recording's native task without changing the global preference.</summary>
public static class WorkflowTranscriptionTask
{
    /// <summary>Missing values inherit; only the two native tasks are supported.</summary>
    public static bool IsSupported(string? selectedTask) =>
        string.IsNullOrWhiteSpace(selectedTask) || selectedTask is "transcribe" or "translate";

    /// <summary>Validates the effective task against the active model before decoding.</summary>
    public static TranscriptionTask Resolve(string? selectedTask, TranscriptionTask globalTask, bool supportsTranslation)
    {
        var task = selectedTask switch
        {
            "transcribe" => TranscriptionTask.Transcribe,
            "translate" => TranscriptionTask.Translate,
            _ when string.IsNullOrWhiteSpace(selectedTask) => globalTask,
            _ => throw new InvalidOperationException("This workflow has an unsupported transcription task.")
        };
        if (task == TranscriptionTask.Translate && !supportsTranslation)
            throw new NotSupportedException("This model cannot translate to English. Choose a translation-capable model in Dictation, or change the transcription task to Transcribe.");
        return task;
    }

    /// <summary>Whether a matching App, Website or Global rule could still replace the global task with Transcribe.</summary>
    public static bool AutomaticRuleMayTranscribe(IEnumerable<Workflow> workflows) => workflows.Any(workflow => workflow.IsEnabled
        && workflow.Trigger.Kind is WorkflowTriggerKind.App or WorkflowTriggerKind.Website or WorkflowTriggerKind.Global
        && workflow.Behavior.SelectedTask == "transcribe");
}
