using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TypeWhisper.Windows.Controls;

/// <summary>
/// A navigation glyph that bounces when its destination becomes selected.
/// </summary>
public sealed class BouncyNavigationIcon : TextBlock
{
    /// <summary>
    /// Identifies the <see cref="IsSelected"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected),
        typeof(bool),
        typeof(BouncyNavigationIcon),
        new PropertyMetadata(false, OnIsSelectedChanged));

    private bool _hasLoaded;

    /// <summary>
    /// Gets or sets whether this icon's destination is selected.
    /// </summary>
    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BouncyNavigationIcon"/> class.
    /// </summary>
    public BouncyNavigationIcon()
    {
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform(1, 1),
                new TranslateTransform(0, 0)
            }
        };

        Loaded += (_, _) => Dispatcher.BeginInvoke(() => _hasLoaded = true);
        Unloaded += (_, _) =>
        {
            _hasLoaded = false;
            ResetTransform();
        };
    }

    private static void OnIsSelectedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var icon = (BouncyNavigationIcon)sender;
        if (e.NewValue is true && icon._hasLoaded)
            icon.Bounce();
    }

    private void Bounce()
    {
        if (!SystemParameters.ClientAreaAnimation || Program.UiAutomation.IsEnabled)
            return;

        var transforms = (TransformGroup)RenderTransform;
        var scale = (ScaleTransform)transforms.Children[0];
        var translation = (TranslateTransform)transforms.Children[1];
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        var yAnimation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(390)
        };
        yAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        yAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(-3.5, KeyTime.FromPercent(0.28), ease));
        yAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.58), ease));
        yAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(-0.8, KeyTime.FromPercent(0.78), ease));
        yAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), ease));

        var scaleAnimation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(390)
        };
        scaleAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        scaleAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1.12, KeyTime.FromPercent(0.28), ease));
        scaleAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(0.94, KeyTime.FromPercent(0.58), ease));
        scaleAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), ease));

        translation.BeginAnimation(TranslateTransform.YProperty, yAnimation, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private void ResetTransform()
    {
        var transforms = (TransformGroup)RenderTransform;
        var scale = (ScaleTransform)transforms.Children[0];
        var translation = (TranslateTransform)transforms.Children[1];
        translation.BeginAnimation(TranslateTransform.YProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        translation.Y = 0;
        scale.ScaleX = 1;
        scale.ScaleY = 1;
    }
}
