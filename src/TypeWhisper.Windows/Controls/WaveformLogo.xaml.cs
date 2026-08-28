using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace TypeWhisper.Windows.Controls;

/// <summary>
/// Displays the TypeWhisper waveform mark and animates it while hovered.
/// </summary>
public partial class WaveformLogo : UserControl
{
    private const int AnimationDurationMilliseconds = 1960;

    /// <summary>
    /// Initializes a new instance of the <see cref="WaveformLogo"/> class.
    /// </summary>
    public WaveformLogo()
    {
        InitializeComponent();
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation || Program.UiAutomation.IsEnabled)
            return;

        // These proportions, phases, and the 3.2 rad/s cadence mirror the macOS mark.
        StartBarAnimation(LogoBar1, 37.1, 0);
        StartBarAnimation(LogoBar2, 62.1, 656);
        StartBarAnimation(LogoBar3, 87.1, 1250);
        StartBarAnimation(LogoBar4, 62.1, 281);
        StartBarAnimation(LogoBar5, 37.1, 969);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e) => StopAnimations();

    private void OnUnloaded(object sender, RoutedEventArgs e) => StopAnimations();

    private static void StartBarAnimation(
        FrameworkElement bar,
        double restingHeight,
        int phaseDelayMilliseconds)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = TimeSpan.FromMilliseconds(phaseDelayMilliseconds),
            Duration = TimeSpan.FromMilliseconds(AnimationDurationMilliseconds),
            RepeatBehavior = RepeatBehavior.Forever
        };

        var easing = new SineEase { EasingMode = EasingMode.EaseInOut };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(restingHeight, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            Math.Min(100, restingHeight * 1.2),
            KeyTime.FromPercent(0.25),
            easing));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            restingHeight * 0.8,
            KeyTime.FromPercent(0.75),
            easing));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            restingHeight,
            KeyTime.FromPercent(1),
            easing));

        bar.BeginAnimation(HeightProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopAnimations()
    {
        StopBarAnimation(LogoBar1, 37.1);
        StopBarAnimation(LogoBar2, 62.1);
        StopBarAnimation(LogoBar3, 87.1);
        StopBarAnimation(LogoBar4, 62.1);
        StopBarAnimation(LogoBar5, 37.1);
    }

    private static void StopBarAnimation(FrameworkElement bar, double restingHeight)
    {
        bar.BeginAnimation(HeightProperty, null);
        bar.Height = restingHeight;
    }
}
