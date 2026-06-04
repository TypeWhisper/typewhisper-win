using System.Windows.Controls;
using System.Windows.Threading;

namespace TypeWhisper.Windows.Controls.Overlay;

/// <summary>
/// Provides clock widget behavior.
/// </summary>
public partial class ClockWidget : UserControl, IDisposable
{
    private readonly DispatcherTimer _timer;

    /// <summary>
    /// Initializes a new instance of the ClockWidget class.
    /// </summary>
    public ClockWidget()
    {
        InitializeComponent();
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("HH:mm");
        _timer.Start();
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose() => _timer.Stop();
}
