using System.Net.Http;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Windows.Services;

internal sealed record AutomaticTranscriptionFallbackResult(
    TranscriptionResult Result,
    string EngineId,
    string ModelId,
    TranscriptionTask Task);

internal sealed class TranscriptionFallbackException : InvalidOperationException
{
    public TranscriptionFallbackException(Exception primaryFailure, Exception fallbackFailure)
        : base(
            "Primary transcription and configured recovery transcription both failed.",
            new AggregateException(primaryFailure, fallbackFailure))
    {
    }
}

/// <summary>
/// Runs one explicitly configured alternative speech-to-text request after an eligible failure.
/// </summary>
public sealed class AutomaticTranscriptionFallbackService
{
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
    private readonly IDictionaryService _dictionary;
    private readonly Func<bool> _hasEligibleLicense;

    /// <summary>
    /// Creates the automatic transcription fallback service.
    /// </summary>
    public AutomaticTranscriptionFallbackService(
        ModelManagerService modelManager,
        ISettingsService settings,
        LicenseService licenseService,
        IDictionaryService dictionary)
        : this(
            modelManager,
            settings,
            dictionary,
            () => licenseService.HasCommercialLicense || licenseService.IsSupporter)
    {
    }

    internal AutomaticTranscriptionFallbackService(
        ModelManagerService modelManager,
        ISettingsService settings,
        IDictionaryService dictionary,
        Func<bool> hasEligibleLicense)
    {
        _modelManager = modelManager;
        _settings = settings;
        _dictionary = dictionary;
        _hasEligibleLicense = hasEligibleLicense;
    }

    internal async Task<AutomaticTranscriptionFallbackResult?> TryTranscribeAsync(
        float[] audioSamples,
        string? primaryEngineId,
        Exception primaryFailure,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = _settings.Current;
        if (!settings.DictationRecoveryAutomaticFallbackEnabled
            || !_hasEligibleLicense()
            || !ShouldAttemptFallback(primaryFailure, cancellationToken))
        {
            return null;
        }

        var engineId = settings.DictationRecoveryEngineId;
        var modelId = settings.DictationRecoveryModelId;
        if (string.IsNullOrWhiteSpace(engineId) || string.IsNullOrWhiteSpace(modelId))
            return null;

        var engine = _modelManager.PluginManager.TranscriptionEngines.FirstOrDefault(candidate =>
            string.Equals(candidate.GetTranscriptionSelectionId(), engineId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.ProviderId, engineId, StringComparison.OrdinalIgnoreCase));
        if (!IsEligibleEngine(engine, modelId, primaryEngineId, settings, out var resolvedModelId))
            return null;
        var fallbackEngine = engine!;

        var task = string.Equals(
            settings.DictationRecoveryTask,
            "translate",
            StringComparison.OrdinalIgnoreCase)
            ? TranscriptionTask.Translate
            : TranscriptionTask.Transcribe;
        var languageHints = string.IsNullOrWhiteSpace(settings.DictationRecoveryLanguage)
            || settings.DictationRecoveryLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? Array.Empty<string>()
                : new[] { settings.DictationRecoveryLanguage.Trim() };
        var prompt = TranscriptionDictionaryPrompt.Create(_dictionary, fallbackEngine);

        System.Diagnostics.Debug.WriteLine(
            $"[TranscriptionFallback] engine={fallbackEngine.GetTranscriptionSelectionId()} model={resolvedModelId} event=start");
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await using var scope = await _modelManager.BeginTranscriptionRequestAsync(
                fallbackEngine.GetTranscriptionSelectionId(),
                resolvedModelId,
                awaitDownload: false,
                cancellationToken).ConfigureAwait(false);
            var result = await _modelManager.TranscribeActiveWithLanguageHintsAsync(
                audioSamples,
                languageHints,
                task,
                prompt,
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result.Result.Text))
            {
                throw new PluginRequestException(
                    "The recovery transcription returned an empty response.",
                    PluginRequestFailureKind.EmptyResponse);
            }

            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            System.Diagnostics.Debug.WriteLine(
                $"[TranscriptionFallback] engine={result.EngineId ?? fallbackEngine.ProviderId} model={result.ModelId ?? resolvedModelId} event=success elapsed-ms={elapsed:0}");
            return new AutomaticTranscriptionFallbackResult(
                result.Result,
                result.EngineSelectionId ?? fallbackEngine.GetTranscriptionSelectionId(),
                result.ModelId ?? resolvedModelId,
                task);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception fallbackFailure)
        {
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            System.Diagnostics.Debug.WriteLine(
                $"[TranscriptionFallback] engine={fallbackEngine.GetTranscriptionSelectionId()} model={resolvedModelId} event=failure kind={ClassifyFailure(fallbackFailure)} elapsed-ms={elapsed:0}");
            throw new TranscriptionFallbackException(primaryFailure, fallbackFailure);
        }
    }

    internal static bool IsEligibleEngine(
        ITranscriptionEnginePlugin? engine,
        string modelId,
        string? primaryEngineId,
        AppSettings settings,
        out string resolvedModelId)
    {
        resolvedModelId = string.Empty;
        if (engine is null || !engine.IsConfigured)
            return false;
        if (string.Equals(engine.GetTranscriptionSelectionId(), primaryEngineId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(engine.ProviderId, primaryEngineId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var model = engine.TranscriptionModels.FirstOrDefault(candidate =>
            candidate.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        if (model is null || engine.SupportsModelDownload && !engine.IsModelDownloaded(model.Id))
            return false;
        if (string.Equals(
                settings.DictationRecoveryTask,
                "translate",
                StringComparison.OrdinalIgnoreCase)
            && !engine.SupportsTranslation)
        {
            return false;
        }

        var language = settings.DictationRecoveryLanguage;
        if (!string.IsNullOrWhiteSpace(language)
            && !language.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && engine.SupportedLanguages.Count > 0
            && !engine.SupportedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        resolvedModelId = model.Id;
        return true;
    }

    internal static bool ShouldAttemptFallback(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        return ClassifyFailure(exception) is TranscriptionFallbackFailureKind.Eligible;
    }

    private static TranscriptionFallbackFailureKind ClassifyFailure(Exception exception)
    {
        if (exception is PluginRequestException requestException)
        {
            return requestException.FailureKind is
                PluginRequestFailureKind.Network
                or PluginRequestFailureKind.Timeout
                or PluginRequestFailureKind.RateLimit
                or PluginRequestFailureKind.ServerError
                or PluginRequestFailureKind.Authentication
                or PluginRequestFailureKind.Permission
                or PluginRequestFailureKind.Configuration
                    ? TranscriptionFallbackFailureKind.Eligible
                    : TranscriptionFallbackFailureKind.Ineligible;
        }

        if (exception is ModelManagerRequestException modelFailure)
        {
            if (modelFailure.StatusCode == 413)
                return TranscriptionFallbackFailureKind.Ineligible;
            return modelFailure.StatusCode is 400 or 401 or 403 or 408 or 409 or 429 or >= 500
                ? TranscriptionFallbackFailureKind.Eligible
                : TranscriptionFallbackFailureKind.Ineligible;
        }

        if (exception is HttpRequestException or TimeoutException or TaskCanceledException)
            return TranscriptionFallbackFailureKind.Eligible;

        if (exception is OperationCanceledException)
            return TranscriptionFallbackFailureKind.Ineligible;

        var message = exception.Message;
        if (message.Contains("too large", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unsupported task", StringComparison.OrdinalIgnoreCase)
            || message.Contains("translation is not supported", StringComparison.OrdinalIgnoreCase))
        {
            return TranscriptionFallbackFailureKind.Ineligible;
        }

        return message.Contains("api key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not configured", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("network", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no model loaded", StringComparison.OrdinalIgnoreCase)
            || (message.Contains("model", StringComparison.OrdinalIgnoreCase)
                && message.Contains("load", StringComparison.OrdinalIgnoreCase))
                    ? TranscriptionFallbackFailureKind.Eligible
                    : TranscriptionFallbackFailureKind.Ineligible;
    }

    private enum TranscriptionFallbackFailureKind
    {
        Ineligible,
        Eligible
    }
}
