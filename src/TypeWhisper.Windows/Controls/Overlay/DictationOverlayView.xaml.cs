using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace TypeWhisper.Windows.Controls.Overlay;

public partial class DictationOverlayView : UserControl
{
    private bool _isPartialPreviewScrollPending;

    public DictationOverlayView()
    {
        InitializeComponent();
    }

    private void OnPartialTextTargetUpdated(object sender, DataTransferEventArgs e)
    {
        if (sender is TextBlock { Text.Length: 0 })
        {
            _isPartialPreviewScrollPending = false;
            PartialPreviewScrollViewer.ScrollToHome();
            return;
        }

        QueuePartialPreviewScrollToEnd();
    }

    private void QueuePartialPreviewScrollToEnd()
    {
        if (_isPartialPreviewScrollPending)
            return;

        _isPartialPreviewScrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _isPartialPreviewScrollPending = false;
            PartialPreviewScrollViewer.UpdateLayout();
            PartialPreviewScrollViewer.ScrollToEnd();
        }));
    }
}
