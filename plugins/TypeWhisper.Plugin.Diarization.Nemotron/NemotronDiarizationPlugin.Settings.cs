using System.Globalization;
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
    internal DirectMlAdapter? SelectedAdapter => SavedAdapter(_host?.GetSetting<string>(DeviceSetting));

    // The value names the adapter's index and description, so two cards with the same description stay apart.
    private static string DeviceValue(DirectMlAdapter adapter) =>
        DirectMlPrefix + adapter.Index.ToString(CultureInfo.InvariantCulture) + ":" + adapter.Name;

    private DirectMlAdapter? SavedAdapter(string? device)
    {
        if (device is null || !device.StartsWith(DirectMlPrefix, StringComparison.Ordinal)) return null;
        var adapters = _adapters();
        var value = device[DirectMlPrefix.Length..];
        var separator = value.IndexOf(':');
        if (separator > 0 && int.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            var name = value[(separator + 1)..];
            // A card whose index moved, for example after another card was removed, is found by its description.
            return adapters.FirstOrDefault(adapter => adapter.Index == index && adapter.Name == name)
                ?? adapters.FirstOrDefault(adapter => adapter.Name == name);
        }

        // An older value names the description only.
        return adapters.FirstOrDefault(adapter => adapter.Name == value);
    }

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
                var selected = SelectedAdapter is { } adapter ? DeviceValue(adapter) : CpuDevice;
                settings.Add(new(DeviceSetting, L("Processing device", "Verarbeitungsgerät"),
                    L("The CPU works on every PC. A graphics card through DirectML is usually faster. Applies from the next diarization.",
                      "Die CPU funktioniert auf jedem PC. Eine Grafikkarte über DirectML ist meist schneller. Gilt ab der nächsten Diarisierung."),
                    selected)
                {
                    SaveChoiceOnChange = true,
                    Choices = [new(CpuDevice, "CPU"), .. adapters.Select(item => new PluginSettingChoice(DeviceValue(item),
                        // Cards with the same description are told apart by their position.
                        adapters.Count(other => other.Name == item.Name) > 1 ? $"{item.Name} #{item.Index + 1} (DirectML)" : item.Name + " (DirectML)"))]
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
        else if (id == DeviceSetting && (value == CpuDevice || _adapters().Any(adapter => DeviceValue(adapter) == value)))
            host.SetSetting(DeviceSetting, value);
        else throw new ArgumentException("Invalid setting.", nameof(value));
        return Task.CompletedTask;
    }
}
