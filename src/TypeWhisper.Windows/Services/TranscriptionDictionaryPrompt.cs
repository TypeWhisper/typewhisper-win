using TypeWhisper.Core.Interfaces;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Windows.Services;

internal static class TranscriptionDictionaryPrompt
{
    public static string? Create(
        IDictionaryService? dictionary,
        ITranscriptionEnginePlugin? plugin)
    {
        if (dictionary is null || plugin?.SupportsDictionaryTerms != true)
            return null;

        return PluginDictionaryTerms.CreatePrompt(
            dictionary.GetEnabledTerms(),
            plugin.DictionaryTermsBudget);
    }
}
