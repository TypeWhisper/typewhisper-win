using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TypeWhisper.Windows.Controls.Overlay;

/// <summary>
/// Provides waveform widget behavior.
/// </summary>
public partial class WaveformWidget : UserControl, IDisposable
{
    private const int BarCount = 5;
    private const double MinimumBarHeight = 3;
    private const double MaximumBarHeight = 17;
    private const float NoiseGate = 0.02f;
    private static readonly double[] BarWeights = [0.55, 0.82, 1.0, 0.82, 0.55];

    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly double[] _barTargets = new double[BarCount];
    private readonly double[] _barCurrents = new double[BarCount];
    private bool _isRendering;
    private bool _isLoaded;

    /// <summary>
    /// Initializes a new instance of the WaveformWidget class.
    /// </summary>
    public WaveformWidget()
    {
        InitializeComponent();

        var brush = new SolidColorBrush(Color.FromRgb(0x6F, 0xB6, 0xFF));
        brush.Freeze();

        for (var i = 0; i < BarCount; i++)
        {
            _bars[i] = new Rectangle
            {
                Width = 3,
                Height = MinimumBarHeight,
                RadiusX = 1.5,
                RadiusY = 1.5,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = brush,
                Margin = new Thickness(i > 0 ? 2 : 0, 0, 0, 0)
            };
            _barTargets[i] = MinimumBarHeight;
            _barCurrents[i] = MinimumBarHeight;
            WavePanel.Children.Add(_bars[i]);
        }

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        UpdateWaveTargets();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        StopRendering();
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
        if (e.PropertyName is null or "" or "AudioLevel" or "State")
            Dispatcher.InvokeAsync(UpdateWaveTargets);
    }

    private void UpdateWaveTargets()
    {
        var state = ReadProperty("State")?.ToString();
        var audioLevel = ReadFloatProperty("AudioLevel");

        var isActive = _isLoaded && state == "Recording";
        if (isActive && !_isRendering)
        {
            _isRendering = true;
            CompositionTarget.Rendering += OnRendering;
        }
        else if (!isActive)
        {
            StopRendering();
            for (var i = 0; i < BarCount; i++)
            {
                _barTargets[i] = MinimumBarHeight;
                _barCurrents[i] = MinimumBarHeight;
                _bars[i].Height = MinimumBarHeight;
            }
        }

        if (isActive)
        {
            for (var i = 0; i < BarCount; i++)
                _barTargets[i] = CalculateTargetHeight(audioLevel, i);
        }
    }

    internal static double CalculateTargetHeight(float level, int index)
    {
        if ((uint)index >= BarCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        var normalized = level <= NoiseGate
            ? 0d
            : Math.Clamp((level - NoiseGate) * 4d, 0d, 1d);
        var easedLevel = Math.Sqrt(normalized);
        return MinimumBarHeight
            + easedLevel * (MaximumBarHeight - MinimumBarHeight) * BarWeights[index];
    }

    private object? ReadProperty(string propertyName) =>
        DataContext?.GetType().GetProperty(propertyName)?.GetValue(DataContext);

    private float ReadFloatProperty(string propertyName)
    {
        var value = ReadProperty(propertyName);
        return value switch
        {
            float f => f,
            double d => (float)d,
            _ => 0f
        };
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        for (var i = 0; i < BarCount; i++)
        {
            var smoothing = _barTargets[i] > _barCurrents[i] ? 0.35 : 0.18;
            _barCurrents[i] += (_barTargets[i] - _barCurrents[i]) * smoothing;
            _bars[i].Height = Math.Clamp(_barCurrents[i], MinimumBarHeight, MaximumBarHeight);
        }
    }

    private void StopRendering()
    {
        if (!_isRendering)
            return;

        CompositionTarget.Rendering -= OnRendering;
        _isRendering = false;
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        StopRendering();
        if (DataContext is INotifyPropertyChanged vm)
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        DataContextChanged -= OnDataContextChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
    }
}
