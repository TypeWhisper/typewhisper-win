using System.Windows;
using System.Windows.Controls;

namespace TypeWhisper.Plugin.FillerWords;

/// <summary>
/// Settings view for the Filler Words plugin. Edits the filler word list.
/// </summary>
public partial class FillerWordsSettingsView : UserControl
{
    private readonly FillerWordsPlugin _plugin;
    private readonly FillerWordsSettingsStore _store;

    /// <summary>
    /// Initializes a new instance of the FillerWordsSettingsView class.
    /// </summary>
    public FillerWordsSettingsView(FillerWordsPlugin plugin, FillerWordsSettingsStore store)
    {
        _plugin = plugin;
        _store = store;
        InitializeComponent();

        TitleText.Text = L("Settings.Title");
        HintText.Text = L("Settings.Hint");
        ResetButton.Content = L("Settings.ResetDefaults");

        WordsBox.Text = store.WordsText;
        UpdateCount();
    }

    private void OnWordsChanged(object sender, TextChangedEventArgs e)
    {
        _store.WordsText = WordsBox.Text;
        UpdateCount();
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _store.ResetToDefaults();
        WordsBox.Text = _store.WordsText;
    }

    private void UpdateCount() => CountText.Text = L("Settings.WordCount", _store.WordCount);

    private string L(string key) => _plugin.Loc?.GetString(key) ?? key;
    private string L(string key, params object[] args) => _plugin.Loc?.GetString(key, args) ?? key;
}
