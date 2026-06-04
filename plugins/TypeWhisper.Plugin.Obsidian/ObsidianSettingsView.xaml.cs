using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace TypeWhisper.Plugin.Obsidian;

/// <summary>
/// Provides obsidian settings view behavior.
/// </summary>
public partial class ObsidianSettingsView : UserControl
{
    private readonly ObsidianPlugin _plugin;
    private readonly List<ObsidianVaultInfo> _detectedVaults;
    private bool _loading = true;

    /// <summary>
    /// Initializes a new instance of the ObsidianSettingsView class.
    /// </summary>
    public ObsidianSettingsView(ObsidianPlugin plugin)
    {
        _plugin = plugin;
        _detectedVaults = ObsidianPlugin.DetectVaults();

        InitializeComponent();
        LoadSettings();

        _loading = false;
    }

    private void LoadSettings()
    {
        var host = _plugin.Host;
        if (host is null) return;

        var vaultPath = host.GetSetting<string>("vault-path") ?? "";
        var subfolder = host.GetSetting<string>("subfolder") ?? "TypeWhisper";
        var dailyNoteMode = host.GetSetting<bool>("daily-note-mode");
        var filenameTemplate = host.GetSetting<string>("filename-template") ?? "{{date}} {{time}} Transcription";

        // Populate vault combo box
        VaultComboBox.Items.Clear();

        if (_detectedVaults.Count > 0)
        {
            foreach (var vault in _detectedVaults)
                VaultComboBox.Items.Add(vault);

            // Select the matching vault if configured
            var matchIndex = _detectedVaults.FindIndex(v =>
                string.Equals(v.Path, vaultPath, StringComparison.OrdinalIgnoreCase));

            if (matchIndex >= 0)
                VaultComboBox.SelectedIndex = matchIndex;
        }
        else
        {
            VaultComboBox.Items.Add(new ObsidianVaultInfo("(No vaults detected)", ""));
            VaultComboBox.IsEnabled = false;
        }

        VaultPathTextBox.Text = vaultPath;
        SubfolderTextBox.Text = subfolder;
        DailyNoteCheckBox.IsChecked = dailyNoteMode;
        FilenameTemplateTextBox.Text = filenameTemplate;

        UpdateStatus(vaultPath);
    }

    private void OnVaultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (VaultComboBox.SelectedItem is not ObsidianVaultInfo vault) return;
        if (string.IsNullOrEmpty(vault.Path)) return;

        VaultPathTextBox.Text = vault.Path;
        SaveSetting("vault-path", vault.Path);
        UpdateStatus(vault.Path);
    }

    private void OnVaultPathChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var path = VaultPathTextBox.Text.Trim();
        SaveSetting("vault-path", path);
        UpdateStatus(path);
    }

    private void OnBrowseVault(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Obsidian Vault Folder"
        };

        if (!string.IsNullOrEmpty(VaultPathTextBox.Text) && Directory.Exists(VaultPathTextBox.Text))
            dialog.InitialDirectory = VaultPathTextBox.Text;

        if (dialog.ShowDialog() == true)
        {
            VaultPathTextBox.Text = dialog.FolderName;
            SaveSetting("vault-path", dialog.FolderName);
            UpdateStatus(dialog.FolderName);
        }
    }

    private void OnSubfolderChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SaveSetting("subfolder", SubfolderTextBox.Text.Trim());
    }

    private void OnDailyNoteModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SaveSetting("daily-note-mode", DailyNoteCheckBox.IsChecked == true);
    }

    private void OnFilenameTemplateChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SaveSetting("filename-template", FilenameTemplateTextBox.Text.Trim());
    }

    private void SaveSetting<T>(string key, T value)
    {
        _plugin.Host?.SetSetting(key, value);
    }

    private void UpdateStatus(string vaultPath)
    {
        if (string.IsNullOrWhiteSpace(vaultPath))
        {
            StatusText.Text = "No vault path configured.";
        }
        else if (!Directory.Exists(vaultPath))
        {
            StatusText.Text = $"Vault path not found: {vaultPath}";
        }
        else
        {
            var obsidianDir = Path.Combine(vaultPath, ".obsidian");
            StatusText.Text = Directory.Exists(obsidianDir)
                ? $"Vault detected: {Path.GetFileName(vaultPath)}"
                : $"Folder exists but is not an Obsidian vault (no .obsidian directory).";
        }
    }
}
