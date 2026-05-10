using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Controls.Overlay;

public partial class EdgeDockIndicatorView : UserControl
{
    private const double MaxPartialPreviewHeight = 86;
    private const double MinPartialPreviewHeight = 26;
    private const double FallbackPartialPreviewTextWidth = 560;
    private const string ShowBuiltInPartialPreviewProperty = "ShowBuiltInPartialPreview";
    private const string PartialTextProperty = "PartialText";
    private const string OverlayPositionProperty = "OverlayPosition";

    private bool _isPartialPreviewScrollPending;
    private INotifyPropertyChanged? _observableDataContext;
    private bool _partialPreviewUpdateQueued;
    private bool _queuedPartialPreviewAnimation;
    private bool _queuedPartialTextAnimation;
    private int _partialPreviewAnimationVersion;

    public EdgeDockIndicatorView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnIsVisibleChanged;
        SizeChanged += OnSizeChanged;
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToDataContext(DataContext);
        QueuePartialPreviewUpdate(animated: false, animateText: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeFromDataContext();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnsubscribeFromDataContext();
        SubscribeToDataContext(e.NewValue);
        QueuePartialPreviewUpdate(animated: IsVisible, animateText: false);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        QueuePartialPreviewUpdate(animated: e.NewValue is true, animateText: false);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ReadDataContextProperty(ShowBuiltInPartialPreviewProperty, fallback: false))
            QueuePartialPreviewUpdate(animated: false, animateText: false);
    }

    private void SubscribeToDataContext(object? dataContext)
    {
        if (dataContext is not INotifyPropertyChanged notify || ReferenceEquals(notify, _observableDataContext))
            return;

        _observableDataContext = notify;
        PropertyChangedEventManager.AddHandler(
            notify,
            OnDataContextPropertyChanged,
            string.Empty);
    }

    private void UnsubscribeFromDataContext()
    {
        if (_observableDataContext is null)
            return;

        PropertyChangedEventManager.RemoveHandler(
            _observableDataContext,
            OnDataContextPropertyChanged,
            string.Empty);
        _observableDataContext = null;
    }

    private void OnDataContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null
            or ""
            or ShowBuiltInPartialPreviewProperty
            or PartialTextProperty
            or OverlayPositionProperty)
        {
            var propertyChanged = e.PropertyName is null or "";
            QueuePartialPreviewUpdate(
                animated: IsVisible,
                animateText: IsVisible && (propertyChanged || e.PropertyName == PartialTextProperty));
        }
    }

    private void QueuePartialPreviewUpdate(bool animated, bool animateText)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => QueuePartialPreviewUpdate(animated, animateText));
            return;
        }

        _queuedPartialPreviewAnimation |= animated;
        _queuedPartialTextAnimation |= animateText;
        if (_partialPreviewUpdateQueued)
            return;

        _partialPreviewUpdateQueued = true;
        Dispatcher.InvokeAsync(
            () =>
            {
                var shouldAnimate = _queuedPartialPreviewAnimation;
                var shouldAnimateText = _queuedPartialTextAnimation;
                _partialPreviewUpdateQueued = false;
                _queuedPartialPreviewAnimation = false;
                _queuedPartialTextAnimation = false;
                UpdatePartialPreview(shouldAnimate, shouldAnimateText);
            },
            DispatcherPriority.Loaded);
    }

    private void UpdatePartialPreview(bool animated, bool animateText)
    {
        var showPreview = ReadDataContextProperty(ShowBuiltInPartialPreviewProperty, fallback: false);
        var position = ReadDataContextProperty(OverlayPositionProperty, fallback: OverlayPosition.Top);
        var hiddenOffset = position == OverlayPosition.Bottom ? -4 : 4;
        var targetHeight = showPreview ? CalculatePartialPreviewHeight() : 0;
        var targetOpacity = showPreview ? 1 : 0;
        var targetMargin = showPreview
            ? position == OverlayPosition.Bottom
                ? new Thickness(0, 0, 0, 5)
                : new Thickness(0, 5, 0, 0)
            : new Thickness(0);
        var targetOffset = showPreview ? 0 : hiddenOffset;

        if (showPreview && PartialPreviewBorder.Height <= 0.5 && PartialPreviewBorder.Opacity <= 0.01)
            PartialPreviewTransform.Y = hiddenOffset;

        if (!animated || !IsVisible)
        {
            ApplyPartialPreviewState(targetHeight, targetOpacity, targetMargin, targetOffset);
            if (showPreview)
                QueuePartialPreviewScrollToEnd();
            return;
        }

        var duration = TimeSpan.FromMilliseconds(showPreview ? 420 : 200);
        var marginDuration = TimeSpan.FromMilliseconds(showPreview ? 320 : 160);
        var version = ++_partialPreviewAnimationVersion;

        BeginFrameworkElementDoubleAnimation(PartialPreviewBorder, FrameworkElement.HeightProperty, targetHeight, duration, version);
        BeginFrameworkElementDoubleAnimation(PartialPreviewBorder, UIElement.OpacityProperty, targetOpacity, marginDuration, version);
        BeginThicknessAnimation(PartialPreviewBorder, FrameworkElement.MarginProperty, targetMargin, marginDuration, version);
        BeginTransformDoubleAnimation(PartialPreviewTransform, TranslateTransform.YProperty, targetOffset, duration, version);

        if (showPreview && animateText)
            AnimatePartialText(position);

        if (showPreview)
            QueuePartialPreviewScrollToEnd();
    }

    private double CalculatePartialPreviewHeight()
    {
        PartialTextBlock.MaxWidth = GetPartialPreviewTextWidth();
        PartialTextBlock.Measure(new Size(PartialTextBlock.MaxWidth, double.PositiveInfinity));
        return Math.Clamp(
            Math.Ceiling(PartialTextBlock.DesiredSize.Height),
            MinPartialPreviewHeight,
            MaxPartialPreviewHeight);
    }

    private double GetPartialPreviewTextWidth()
    {
        var availableWidth = PartialPreviewBorder.ActualWidth;
        return availableWidth > 120 ? Math.Min(availableWidth, FallbackPartialPreviewTextWidth) : FallbackPartialPreviewTextWidth;
    }

    private void ApplyPartialPreviewState(
        double height,
        double opacity,
        Thickness margin,
        double offset)
    {
        _partialPreviewAnimationVersion++;
        PartialPreviewBorder.BeginAnimation(FrameworkElement.HeightProperty, null);
        PartialPreviewBorder.BeginAnimation(UIElement.OpacityProperty, null);
        PartialPreviewBorder.BeginAnimation(FrameworkElement.MarginProperty, null);
        PartialPreviewTransform.BeginAnimation(TranslateTransform.YProperty, null);

        PartialPreviewBorder.Height = height;
        PartialPreviewBorder.Opacity = opacity;
        PartialPreviewBorder.Margin = margin;
        PartialPreviewTransform.Y = offset;
    }

    private void AnimatePartialText(OverlayPosition position)
    {
        var fromOffset = position == OverlayPosition.Bottom ? -3 : 3;
        PartialTextTransform.Y = fromOffset;
        PartialTextBlock.Opacity = 0.72;

        var duration = TimeSpan.FromMilliseconds(220);
        BeginTextTransformDoubleAnimation(PartialTextTransform, TranslateTransform.YProperty, 0, duration);
        BeginTextElementDoubleAnimation(PartialTextBlock, UIElement.OpacityProperty, 1, duration);
    }

    private void BeginFrameworkElementDoubleAnimation(
        FrameworkElement target,
        DependencyProperty property,
        double to,
        TimeSpan duration,
        int version)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = duration,
            EasingFunction = CreateEase(),
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (version != _partialPreviewAnimationVersion)
                return;

            target.BeginAnimation(property, null);
            target.SetValue(property, to);
        };

        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void BeginTransformDoubleAnimation(
        TranslateTransform target,
        DependencyProperty property,
        double to,
        TimeSpan duration,
        int version)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = duration,
            EasingFunction = CreateEase(),
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (version != _partialPreviewAnimationVersion)
                return;

            target.BeginAnimation(property, null);
            target.SetValue(property, to);
        };

        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void BeginTextElementDoubleAnimation(
        FrameworkElement target,
        DependencyProperty property,
        double to,
        TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = duration,
            EasingFunction = CreateEase(),
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
        };

        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void BeginTextTransformDoubleAnimation(
        TranslateTransform target,
        DependencyProperty property,
        double to,
        TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = duration,
            EasingFunction = CreateEase(),
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
        };

        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void BeginThicknessAnimation(
        FrameworkElement target,
        DependencyProperty property,
        Thickness to,
        TimeSpan duration,
        int version)
    {
        var animation = new ThicknessAnimation
        {
            To = to,
            Duration = duration,
            EasingFunction = CreateEase(),
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (version != _partialPreviewAnimationVersion)
                return;

            target.BeginAnimation(property, null);
            target.SetValue(property, to);
        };

        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private T ReadDataContextProperty<T>(string propertyName, T fallback)
    {
        var value = DataContext?.GetType().GetProperty(propertyName)?.GetValue(DataContext);
        return value is T typed ? typed : fallback;
    }

    private static IEasingFunction CreateEase() =>
        new QuinticEase { EasingMode = EasingMode.EaseOut };

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
