using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Diarization.Nemotron;

public sealed partial class NemotronDiarizationPlugin : IPluginTextSettings
{
    internal const string ProfileSetting = "profile";
    internal const string DeviceSetting = "device";
    internal const string CpuDevice = "cpu";
    private const string DirectMlPrefix = "directml:";

    private string L(string english, string german) => PluginLocalization.Get(_host, english, german);

    /// <summary>The saved streaming profile; <c>fast32</c> until the user chooses another.</summary>
    internal NemotronProfile SelectedProfile => NemotronProfile.Find(_host?.GetSetting<string>(ProfileSetting)) ?? NemotronProfile.Fast32;

    /// <summary>The saved graphics card while it is still present, or null for the CPU.</summary>
    internal DirectMlAdapter? SelectedAdapter =>
        _host?.GetSetting<string>(DeviceSetting) is { } device && device.StartsWith(DirectMlPrefix, StringComparison.Ordinal)
            ? _adapters().FirstOrDefault(adapter => adapter.Name == device[DirectMlPrefix.Length..])
            : null;

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings
    {
        get
        {
            var settings = new List<PluginTextSetting>
            {
                new(ProfileSetting, L("Context", "Kontext"),
                    L("How much audio the model reads per step. Long context takes more memory and suits long recordings with few speaker changes.",
                      "Wie viel Audio das Modell pro Schritt liest. Langer Kontext braucht mehr Speicher und passt zu langen Aufnahmen mit wenigen Sprecherwechseln."),
                    SelectedProfile.Id)
                {
                    SaveChoiceOnChange = true,
                    Choices =
                    [
                        new(NemotronProfile.Fast32.Id, L("Fast · 2.9 s steps", "Schnell · 2,9-s-Schritte")),
                        new(NemotronProfile.Offline.Id, L("Long context · 30 s steps", "Langer Kontext · 30-s-Schritte"))
                    ]
                }
            };
            var adapters = _adapters();
            if (adapters.Count > 0)
            {
                var selected = SelectedAdapter is { } adapter ? DirectMlPrefix + adapter.Name : CpuDevice;
                settings.Add(new(DeviceSetting, L("Processing device", "Verarbeitungsgerät"),
                    L("The CPU works on every PC. A graphics card through DirectML is usually faster. Applies from the next diarization.",
                      "Die CPU funktioniert auf jedem PC. Eine Grafikkarte über DirectML ist meist schneller. Gilt ab der nächsten Diarisierung."),
                    selected)
                {
                    SaveChoiceOnChange = true,
                    Choices = [new(CpuDevice, "CPU"), .. adapters.Select(item => new PluginSettingChoice(DirectMlPrefix + item.Name, item.Name + " (DirectML)"))]
                });
            }

            return settings;
        }
    }

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var host = _host ?? throw new InvalidOperationException("The plugin is not active.");
        if (id == ProfileSetting && NemotronProfile.Find(value) is not null) host.SetSetting(ProfileSetting, value);
        else if (id == DeviceSetting && (value == CpuDevice || (value.StartsWith(DirectMlPrefix, StringComparison.Ordinal)
            && _adapters().Any(adapter => adapter.Name == value[DirectMlPrefix.Length..]))))
            host.SetSetting(DeviceSetting, value);
        else throw new ArgumentException("Invalid setting.", nameof(value));
        return Task.CompletedTask;
    }
}
