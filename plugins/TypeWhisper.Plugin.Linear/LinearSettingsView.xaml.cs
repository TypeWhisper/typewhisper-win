using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TypeWhisper.Plugin.Linear;

public partial class LinearSettingsView : UserControl
{
    private readonly LinearPlugin _plugin;

    public LinearSettingsView(LinearPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        // Pre-fill fields from saved settings
        if (!string.IsNullOrEmpty(_plugin.ApiKey))
        {
            // Show masked placeholder so user knows a key is stored
            ApiKeyBox.Password = _plugin.ApiKey;
            SetStatus("API key loaded.", isSuccess: true);
        }

        if (!string.IsNullOrEmpty(_plugin.DefaultTeamId))
            TeamIdBox.Text = _plugin.DefaultTeamId;

        if (!string.IsNullOrEmpty(_plugin.DefaultProjectId))
            ProjectIdBox.Text = _plugin.DefaultProjectId;
    }

    private async void OnSaveApiKey(object sender, RoutedEventArgs e)
    {
        var apiKey = ApiKeyBox.Password.Trim();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            SetStatus("Please enter an API key.", isSuccess: false);
            return;
        }

        SaveApiKeyButton.IsEnabled = false;
        SetStatus("Saving...", isSuccess: null);

        try
        {
            await _plugin.SaveApiKeyAsync(apiKey);
            SetStatus("API key saved successfully.", isSuccess: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to save API key: {ex.Message}", isSuccess: false);
        }
        finally
        {
            SaveApiKeyButton.IsEnabled = true;
        }
    }

    private async void OnFetchTeams(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_plugin.ApiKey))
        {
            SetStatus("Save an API key first.", isSuccess: false);
            return;
        }

        FetchTeamsButton.IsEnabled = false;
        SetStatus("Fetching teams...", isSuccess: null);

        try
        {
            var teams = await _plugin.FetchTeamsAsync();

            if (teams.Count == 0)
            {
                SetStatus("No teams found. Check your API key.", isSuccess: false);
                TeamsList.ItemsSource = null;
            }
            else
            {
                TeamsList.ItemsSource = teams;
                SetStatus($"Found {teams.Count} team(s). Click a team to use its ID.", isSuccess: true);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to fetch teams: {ex.Message}", isSuccess: false);
            TeamsList.ItemsSource = null;
        }
        finally
        {
            FetchTeamsButton.IsEnabled = true;
        }
    }

    private void OnSelectTeam(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string teamId }) return;

        TeamIdBox.Text = teamId;
        _plugin.SaveDefaultTeamId(teamId);
        SetStatus("Team ID selected and saved.", isSuccess: true);
    }

    private void OnTeamIdChanged(object sender, TextChangedEventArgs e)
    {
        _plugin.SaveDefaultTeamId(TeamIdBox.Text);
    }

    private void OnProjectIdChanged(object sender, TextChangedEventArgs e)
    {
        _plugin.SaveDefaultProjectId(ProjectIdBox.Text);
    }

    private void SetStatus(string message, bool? isSuccess)
    {
        StatusText.Text = message;
        StatusText.Foreground = isSuccess switch
        {
            true => new SolidColorBrush(Color.FromRgb(34, 139, 34)),   // green
            false => new SolidColorBrush(Color.FromRgb(220, 53, 69)),  // red
            _ => new SolidColorBrush(Color.FromRgb(108, 117, 125))     // gray
        };
    }
}
