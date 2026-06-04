using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Controls.Overlay;

/// <summary>
/// Provides waveform widget behavior.
/// </summary>
public partial class WaveformWidget : UserControl, IDisposable
{
    private readonly Rectangle[] _bars = new Rectangle[5];
    private readonly double[] _barTargets = new double[5];
    private readonly double[] _barCurrents = new double[5];
    private readonly Random _rng = new();
    private bool _isRendering;

    /// <summary>
    /// Initializes a new instance of the WaveformWidget class.
    /// </summary>
    public WaveformWidget()
    {
        InitializeComponent();

        var barWidth = 5.0;
        for (int i = 0; i < 5; i++)
        {
            _bars[i] = new Rectangle
            {
                Width = barWidth,
                Height = 4,
                RadiusX = 2,
                RadiusY = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x99, 0xFF)),
                Margin = new Thickness(i > 0 ? 2 : 0, 0, 0, 0)
            };
            WavePanel.Children.Add(_bars[i]);
        }

        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => UpdateWaveTargets();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldVm)
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        if (e.NewValue is INotifyPropertyChanged newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateWaveTargets();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DictationViewModel.AudioLevel) or nameof(DictationViewModel.State))
            Dispatcher.InvokeAsync(UpdateWaveTargets);
    }

    private void UpdateWaveTargets()
    {
        if (DataContext is not DictationViewModel vm) return;

        var isActive = vm.State is DictationState.Recording or DictationState.Processing;
        if (isActive && !_isRendering)
        {
            _isRendering = true;
            CompositionTarget.Rendering += OnRendering;
        }
        else if (!isActive && _isRendering)
        {
            _isRendering = false;
            CompositionTarget.Rendering -= OnRendering;
            for (int i = 0; i < 5; i++)
            {
                _barTargets[i] = 4;
                _barCurrents[i] = 4;
                _bars[i].Height = 4;
            }
        }

        if (isActive)
        {
            var level = Math.Min(vm.AudioLevel * 5f, 1f);
            for (int i = 0; i < 5; i++)
                _barTargets[i] = 4 + level * 18 * (0.3 + _rng.NextDouble() * 0.7);
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        for (int i = 0; i < 5; i++)
        {
            _barCurrents[i] += (_barTargets[i] - _barCurrents[i]) * 0.25;
            _bars[i].Height = Math.Max(2, _barCurrents[i]);
        }
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        if (_isRendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRendering = false;
        }
        if (DataContext is INotifyPropertyChanged vm)
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        DataContextChanged -= OnDataContextChanged;
    }
}
