using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.Controls;

/// <summary>
/// Renders plugin-provided credential and license requirements without requiring
/// a plugin-specific WPF implementation.
/// </summary>
public sealed class ModelDownloadRequirementsControl : UserControl
{
    private readonly IModelDownloadRequirementsProvider _provider;
    private readonly string? _modelId;
    private readonly StackPanel _requirementsPanel = new();
    private readonly TextBlock _statusText = new()
    {
        Margin = new Thickness(0, 0, 0, 8),
        TextWrapping = TextWrapping.Wrap
    };
    private bool _isWorking;

    /// <summary>Initializes a new host-rendered model requirements control.</summary>
    public ModelDownloadRequirementsControl(
        IModelDownloadRequirementsProvider provider,
        string? modelId = null)
    {
        _provider = provider;
        _modelId = modelId;

        var root = new StackPanel
        {
            Margin = new Thickness(0, 4, 0, 12)
        };
        root.Children.Add(new TextBlock
        {
            Text = Loc.Instance["Models.DownloadRequirements"],
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 4)
        });
        root.Children.Add(new TextBlock
        {
            Text = Loc.Instance["Models.DownloadRequirementsHint"],
            FontSize = 12,
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        root.Children.Add(_statusText);
        root.Children.Add(_requirementsPanel);
        Content = root;

        _provider.ModelDownloadRequirementsChanged += OnRequirementsChanged;
        Unloaded += OnUnloaded;
        Rebuild();
    }

    private IReadOnlyList<PluginModelDownloadRequirement> CurrentRequirements =>
        _provider.ModelDownloadRequirements
            .Where(requirement => _modelId is null
                || string.Equals(requirement.ModelId, _modelId, StringComparison.Ordinal))
            .GroupBy(requirement => new
            {
                requirement.Id,
                requirement.Kind,
                requirement.Revision
            })
            .Select(group => group.First())
            .OrderBy(requirement => requirement.Kind)
            .ThenBy(requirement => requirement.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private void Rebuild()
    {
        _requirementsPanel.Children.Clear();
        foreach (var requirement in CurrentRequirements)
            _requirementsPanel.Children.Add(CreateRequirementCard(requirement));
    }

    private FrameworkElement CreateRequirementCard(PluginModelDownloadRequirement requirement)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = requirement.Title,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = requirement.Description,
            FontSize = 12,
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 8)
        });

        if (requirement.Kind == PluginModelDownloadRequirementKind.Credential)
            AddCredentialEditor(content, requirement);
        else
            AddLicenseEditor(content, requirement);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(31, 38, 48)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 68, 82)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = content
        };
    }

    private void AddCredentialEditor(
        Panel panel,
        PluginModelDownloadRequirement requirement)
    {
        var input = new PasswordBox
        {
            MinWidth = 280,
            Margin = new Thickness(0, 0, 0, 8)
        };
        AutomationProperties.SetAutomationId(
            input,
            $"ModelRequirementCredential.{requirement.ModelId}.{requirement.Id}");
        panel.Children.Add(input);

        var actions = new WrapPanel();
        var save = new Button
        {
            Content = Loc.Instance["Models.SaveCredential"],
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetAutomationId(
            save,
            $"ModelRequirementSave.{requirement.ModelId}.{requirement.Id}");
        save.Click += async (_, _) => await SaveCredentialAsync(requirement, input, save, actions);
        actions.Children.Add(save);

        if (requirement.IsSatisfied)
        {
            var clear = new Button
            {
                Content = Loc.Instance["Models.RemoveCredential"],
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0)
            };
            AutomationProperties.SetAutomationId(
                clear,
                $"ModelRequirementClear.{requirement.ModelId}.{requirement.Id}");
            clear.Click += async (_, _) => await ClearCredentialAsync(requirement, actions);
            actions.Children.Add(clear);
        }

        AddMoreInfoButton(actions, requirement);
        panel.Children.Add(actions);
        panel.Children.Add(CreateRequirementStateText(requirement));
    }

    private void AddLicenseEditor(
        Panel panel,
        PluginModelDownloadRequirement requirement)
    {
        var actions = new WrapPanel();
        var acceptance = new CheckBox
        {
            Content = Loc.Instance["Models.AcceptModelLicense"],
            IsChecked = requirement.IsSatisfied,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        AutomationProperties.SetAutomationId(
            acceptance,
            $"ModelRequirementLicense.{requirement.ModelId}.{requirement.Id}");
        acceptance.Click += async (_, _) =>
            await SetLicenseAcceptanceAsync(requirement, acceptance, actions);
        actions.Children.Add(acceptance);
        AddMoreInfoButton(actions, requirement);
        panel.Children.Add(actions);
        panel.Children.Add(CreateRequirementStateText(requirement));
    }

    private static TextBlock CreateRequirementStateText(PluginModelDownloadRequirement requirement) =>
        new()
        {
            Text = requirement.IsSatisfied
                ? Loc.Instance["Models.RequirementConfigured"]
                : requirement.IsRequired
                    ? Loc.Instance["Models.RequirementRequired"]
                    : Loc.Instance["Models.RequirementOptional"],
            Foreground = requirement.IsSatisfied
                ? Brushes.LightGreen
                : requirement.IsRequired
                    ? Brushes.Orange
                    : Brushes.DarkGray,
            FontSize = 11,
            Margin = new Thickness(0, 7, 0, 0)
        };

    private static void AddMoreInfoButton(
        Panel actions,
        PluginModelDownloadRequirement requirement)
    {
        if (requirement.MoreInfoUri is null)
            return;

        var moreInfo = new Button
        {
            Content = Loc.Instance["Models.RequirementMoreInfo"],
            Padding = new Thickness(12, 5, 12, 5)
        };
        moreInfo.Click += (_, _) =>
            Process.Start(new ProcessStartInfo(requirement.MoreInfoUri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        actions.Children.Add(moreInfo);
    }

    private async Task SaveCredentialAsync(
        PluginModelDownloadRequirement requirement,
        PasswordBox input,
        Button save,
        Panel actions)
    {
        if (_isWorking)
            return;

        _isWorking = true;
        SetEnabled(actions, false);
        try
        {
            var result = await _provider.SaveModelDownloadCredentialAsync(
                requirement.ModelId,
                requirement.Id,
                input.Password,
                CancellationToken.None);
            SetStatus(
                result.Message ?? (result.Succeeded
                    ? Loc.Instance["Models.RequirementSaved"]
                    : Loc.Instance["Models.RequirementInvalid"]),
                result.Succeeded);
            if (result.Succeeded)
                Rebuild();
            else
                input.Focus();
        }
        catch (Exception exception) when (NonFatalExceptionFilter.IsNonFatal(exception))
        {
            SetStatus(
                Loc.Instance.GetString("Models.RequirementUpdateFailedFormat", exception.Message),
                succeeded: false);
            input.Focus();
        }
        finally
        {
            _isWorking = false;
            save.IsEnabled = true;
            SetEnabled(actions, true);
        }
    }

    private async Task ClearCredentialAsync(
        PluginModelDownloadRequirement requirement,
        Panel actions)
    {
        if (_isWorking)
            return;

        _isWorking = true;
        SetEnabled(actions, false);
        try
        {
            await _provider.ClearModelDownloadCredentialAsync(
                requirement.ModelId,
                requirement.Id,
                CancellationToken.None);
            SetStatus(Loc.Instance["Models.RequirementRemoved"], succeeded: true);
            Rebuild();
        }
        catch (Exception exception) when (NonFatalExceptionFilter.IsNonFatal(exception))
        {
            SetStatus(
                Loc.Instance.GetString("Models.RequirementUpdateFailedFormat", exception.Message),
                succeeded: false);
        }
        finally
        {
            _isWorking = false;
            SetEnabled(actions, true);
        }
    }

    private async Task SetLicenseAcceptanceAsync(
        PluginModelDownloadRequirement requirement,
        CheckBox acceptance,
        Panel actions)
    {
        if (_isWorking)
            return;

        _isWorking = true;
        SetEnabled(actions, false);
        try
        {
            await _provider.SetModelDownloadLicenseAcceptanceAsync(
                requirement.ModelId,
                requirement.Id,
                acceptance.IsChecked == true,
                CancellationToken.None);
            SetStatus(
                acceptance.IsChecked == true
                    ? Loc.Instance["Models.RequirementAccepted"]
                    : Loc.Instance["Models.RequirementRevoked"],
                succeeded: true);
            Rebuild();
        }
        catch (Exception exception) when (NonFatalExceptionFilter.IsNonFatal(exception))
        {
            acceptance.IsChecked = requirement.IsSatisfied;
            SetStatus(
                Loc.Instance.GetString("Models.RequirementUpdateFailedFormat", exception.Message),
                succeeded: false);
        }
        finally
        {
            _isWorking = false;
            SetEnabled(actions, true);
        }
    }

    private void SetStatus(string message, bool succeeded)
    {
        _statusText.Text = message;
        _statusText.Foreground = succeeded ? Brushes.LightGreen : Brushes.OrangeRed;
    }

    private static void SetEnabled(Panel panel, bool enabled)
    {
        foreach (UIElement child in panel.Children)
            child.IsEnabled = enabled;
    }

    private void OnRequirementsChanged(object? sender, EventArgs e) =>
        _ = Dispatcher.InvokeAsync(Rebuild);

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _provider.ModelDownloadRequirementsChanged -= OnRequirementsChanged;
        Unloaded -= OnUnloaded;
    }
}

/// <summary>Composes host-rendered model requirements with custom plugin settings.</summary>
public static class PluginSettingsViewComposer
{
    /// <summary>Creates a combined settings view when the plugin exposes requirements.</summary>
    public static UserControl? Create(ITypeWhisperPlugin plugin)
    {
        var customView = plugin.CreateSettingsView();
        if (plugin is not IModelDownloadRequirementsProvider provider
            || provider.ModelDownloadRequirements.Count == 0)
        {
            return customView;
        }

        var requirementsView = new ModelDownloadRequirementsControl(provider);
        if (customView is null)
            return requirementsView;

        var root = new StackPanel();
        root.Children.Add(requirementsView);
        root.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 12) });
        root.Children.Add(customView);
        return new UserControl { Content = root };
    }
}
