using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TypeWhisper.Plugin.GemmaLocal;

public partial class GemmaLocalSettingsView : UserControl
{
    private readonly GemmaLocalPlugin _plugin;
    private readonly List<ModelViewModel> _viewModels = [];
    private CancellationTokenSource? _downloadCts;

    public GemmaLocalSettingsView(GemmaLocalPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        ModelLabel.Text = "🤖 " + L("Settings.SelectModel");
        BuildModelList();
    }

    private void BuildModelList()
    {
        _viewModels.Clear();
        foreach (var model in _plugin.ModelDefinitions)
        {
            var vm = new ModelViewModel
            {
                Id = model.Id,
                DisplayName = model.DisplayName,
                SizeText = model.SizeDescription,
                IsRecommended = model.IsRecommended,
            };

            UpdateModelStatus(vm);
            _viewModels.Add(vm);
        }

        ModelList.ItemsSource = _viewModels;
    }

    private void UpdateModelStatus(ModelViewModel vm)
    {
        var isDownloaded = _plugin.IsModelDownloaded(vm.Id);
        var isLoaded = _plugin.LoadedModelId == vm.Id;

        if (isLoaded)
        {
            vm.StatusText = "✅ " + L("Settings.Loaded");
            vm.StatusBrush = Brushes.LightGreen;
            vm.ActionText = L("Settings.Unload");
            vm.ActionEnabled = true;
            vm.ProgressVisibility = Visibility.Collapsed;
        }
        else if (isDownloaded)
        {
            vm.StatusText = "📦 " + L("Settings.Downloaded");
            vm.StatusBrush = Brushes.Gray;
            vm.ActionText = L("Settings.Load");
            vm.ActionEnabled = true;
            vm.ProgressVisibility = Visibility.Collapsed;
        }
        else
        {
            vm.StatusText = L("Settings.NotDownloaded");
            vm.StatusBrush = Brushes.DarkGray;
            vm.ActionText = L("Settings.Download");
            vm.ActionEnabled = true;
            vm.ProgressVisibility = Visibility.Collapsed;
        }
    }

    private async void OnModelAction(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string modelId)
            return;

        var vm = _viewModels.FirstOrDefault(m => m.Id == modelId);
        if (vm is null) return;

        var isLoaded = _plugin.LoadedModelId == modelId;
        var isDownloaded = _plugin.IsModelDownloaded(modelId);

        if (isLoaded)
        {
            // Unload
            _plugin.UnloadModel();
            RefreshAll();
        }
        else if (isDownloaded)
        {
            // Load
            vm.ActionEnabled = false;
            vm.StatusText = "⏳ " + L("Settings.Loading");
            vm.StatusBrush = Brushes.Orange;

            try
            {
                await _plugin.LoadModelAsync(modelId, CancellationToken.None);
                ShowStatus("✅ " + L("Settings.ModelReady"), Brushes.LightGreen);
            }
            catch (Exception ex)
            {
                ShowStatus("❌ " + L("Settings.Error", ex.Message), Brushes.Red);
            }

            RefreshAll();
        }
        else
        {
            // Download
            _downloadCts?.Cancel();
            _downloadCts = new CancellationTokenSource();
            var ct = _downloadCts.Token;

            vm.ActionEnabled = false;
            vm.ActionText = L("Settings.Cancel");
            vm.ProgressVisibility = Visibility.Visible;
            vm.Progress = 0;
            vm.StatusText = "⬇️ " + L("Settings.Downloading");
            vm.StatusBrush = Brushes.CornflowerBlue;

            // Re-enable as cancel button
            vm.ActionEnabled = true;

            var progress = new Progress<double>(p => Dispatcher.Invoke(() =>
            {
                vm.Progress = p * 100;
                vm.StatusText = $"⬇️ {p:P0}";
            }));

            try
            {
                await _plugin.DownloadModelAsync(modelId, progress, ct);
                ShowStatus("✅ " + L("Settings.DownloadComplete"), Brushes.LightGreen);
            }
            catch (OperationCanceledException)
            {
                ShowStatus("⏹ " + L("Settings.DownloadCancelled"), Brushes.Orange);
            }
            catch (Exception ex)
            {
                ShowStatus("❌ " + L("Settings.Error", ex.Message), Brushes.Red);
            }

            RefreshAll();
        }
    }

    private void RefreshAll()
    {
        foreach (var vm in _viewModels)
            UpdateModelStatus(vm);
    }

    private void ShowStatus(string text, Brush color)
    {
        StatusPanel.Visibility = Visibility.Visible;
        StatusText.Text = text;
        StatusText.Foreground = color;
    }

    private string L(string key) => _plugin.Loc?.GetString(key) ?? key;
    private string L(string key, params object[] args) => _plugin.Loc?.GetString(key, args) ?? key;
}

internal sealed class ModelViewModel : INotifyPropertyChanged
{
    private string _statusText = "";
    private Brush _statusBrush = Brushes.Gray;
    private string _actionText = "";
    private bool _actionEnabled = true;
    private double _progress;
    private Visibility _progressVisibility = Visibility.Collapsed;

    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string SizeText { get; init; }
    public required bool IsRecommended { get; init; }

    public Visibility RecommendedVisibility => IsRecommended ? Visibility.Visible : Visibility.Collapsed;

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
    public string ActionText { get => _actionText; set => Set(ref _actionText, value); }
    public bool ActionEnabled { get => _actionEnabled; set => Set(ref _actionEnabled, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public Visibility ProgressVisibility { get => _progressVisibility; set => Set(ref _progressVisibility, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
