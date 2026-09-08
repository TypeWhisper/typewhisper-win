using Microsoft.UI.Dispatching;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// Dispatcher-owned coordinator. Observation is bounded and runs off the UI thread.
internal static class CorrectionLearning
{
    private static readonly string PreferencePath = WinUIProfile.DataPath("correction-learning.txt");
    private static CancellationTokenSource? _cancellation;
    private static Task _pending = Task.CompletedTask;
    internal static bool Enabled { get; private set; } = ReadEnabled();
    internal static bool Allowed => Enabled && PremiumView.Access.Current.Requirement(PremiumFeature.CorrectionLearning) == PremiumRequirement.Available;
    internal static string Status { get; private set; } = Enabled ? "Ready for the next dictation." : "Automatic learning is off. Existing corrections remain in Dictionary.";
    internal static event Action? Changed;
    internal static event Action? DictionaryChanged;
    internal static event Action<IReadOnlyList<LearnedDictionaryCorrection>>? CorrectionsLearned;
    internal static event Action? ObservationCancelled;
    static CorrectionLearning() { PremiumView.Access.Changed += () => { if (!Allowed) Cancel(); Changed?.Invoke(); }; }
    private static bool ReadEnabled()
    {
        try { return File.Exists(PreferencePath) && new FileInfo(PreferencePath).Length == 7 && File.ReadAllText(PreferencePath) == "enabled"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    internal static void SetEnabled(bool enabled)
    {
        try
        {
            if (enabled) { Directory.CreateDirectory(WinUIProfile.Root); File.WriteAllText(PreferencePath, "enabled"); }
            else File.Delete(PreferencePath);
            Enabled = enabled;
            if (!Allowed) Cancel();
            Status = enabled ? "Ready for the next dictation." : "Automatic learning is off. Existing corrections remain in Dictionary.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = "Could not save the correction-learning setting."; }
        Changed?.Invoke();
    }
    internal static Task Cancel()
    {
        ObservationCancelled?.Invoke();
        _cancellation?.Cancel();
        return _pending;
    }
    internal static void Observe(string text, IntPtr target)
    {
        if (!Allowed || text.Length is 0 or > 2048 || !_pending.IsCompleted) return;
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        Status = "Watching the inserted text for corrections (up to 30 seconds)."; Changed?.Invoke();
        _pending = RunAsync();
        async Task RunAsync()
        {
            try
            {
                var result = await Task.Run(async () =>
                {
                    using var observer = new CorrectionTextObserver();
                    var baseline = observer.Capture(target, TargetAppCorrectionLearningService.GetMaxObservedTextLength(text));
                    if (baseline is null || !baseline.Value.Contains(text, StringComparison.Ordinal))
                        return new TargetAppCorrectionLearningAttemptResult(TargetAppCorrectionLearningOutcomeKind.UnsupportedTextObservation, []);
                    using var commit = new CorrectionCommitObserver(dispatcher, target);
                    var engine = new TargetAppCorrectionLearningService(suggestions =>
                    {
                        var completion = new TaskCompletionSource<IReadOnlyList<LearnedDictionaryCorrection>>(TaskCreationOptions.RunContinuationsAsynchronously);
                        if (!dispatcher.TryEnqueue(() =>
                        {
                            try { completion.SetResult(!cancellation.IsCancellationRequested && Allowed ? Save(suggestions) : []); }
                            catch (Exception e) { completion.SetException(e); }
                        })) return [];
                        return completion.Task.GetAwaiter().GetResult();
                    }, observer, commit);
                    return await engine.TrackInsertionAsync(text, baseline, cancellation.Token);
                });
                Status = result.Outcome switch
                {
                    TargetAppCorrectionLearningOutcomeKind.Learned => $"Learned {result.Count} correction(s). Manage them in Dictionary > Corrections.",
                    TargetAppCorrectionLearningOutcomeKind.UnsupportedTextObservation => "This app does not expose an editable text field. No correction was learned.",
                    TargetAppCorrectionLearningOutcomeKind.DuplicateCorrection => "The correction already exists. Your dictionary was kept unchanged.",
                    TargetAppCorrectionLearningOutcomeKind.Cancelled => "Observation stopped.",
                    TargetAppCorrectionLearningOutcomeKind.Failed => "Could not observe or save corrections. Your dictation was kept.",
                    _ => "No confirmed, unambiguous word correction was learned."
                };
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            { Status = "Correction learning is unavailable for this field. Your dictation was kept."; }
            finally
            {
                if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
                cancellation.Dispose(); Changed?.Invoke();
            }
        }
    }
    private static IReadOnlyList<LearnedDictionaryCorrection> Save(IReadOnlyList<CorrectionSuggestion> suggestions)
    {
        var learned = LearnedCorrectionStore.Save(DictationDictionarySnapshot.StoragePath, suggestions);
        if (learned.Count > 0)
        {
            DictionaryChanged?.Invoke();
            try { CorrectionsLearned?.Invoke(learned); }
            catch (Exception e) when (e is not OutOfMemoryException)
            { System.Diagnostics.Debug.WriteLine("Correction feedback unavailable: " + e.GetType().Name); }
        }
        return learned;
    }
}
