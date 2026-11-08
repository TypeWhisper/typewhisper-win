using System.Globalization;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Games;

/// <summary>
/// The plugin's strings in the app's language. English is the key, German the fallback for hosts without the
/// package's Localization folder; other languages come from that folder.
/// </summary>
internal sealed class GameText(Func<IPluginHostServices?> host)
{
    public string L(string english, string german) => PluginLocalization.Get(host(), english, german);

    public string L(string english, string german, params object[] args) => string.Format(CultureInfo.CurrentCulture, L(english, german), args);

    public PluginSettingChoice[] OnOff => [new("true", L("On", "An")), new("false", L("Off", "Aus"))];
}
