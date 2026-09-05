using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

// Dedicated hit-testable logo: shared decorative glyphs intentionally ignore input.
public sealed class PrototypeSetupLogo : UserControl
{
    private readonly List<Border> _bars = [];
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private bool _hovering;

    public PrototypeSetupLogo()
    {
        Width = 86; Height = 86;
        var hitArea = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        var waveform = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var height in new[] { 26, 48, 66, 48, 26 })
        {
            var bar = new Border { Width = 9, Height = height, CornerRadius = new CornerRadius(4.5),
                Background = (Brush)Application.Current.Resources["AccentBrush"], VerticalAlignment = VerticalAlignment.Center };
            _bars.Add(bar); waveform.Children.Add(bar);
        }
        hitArea.Children.Add(waveform); Content = hitArea;
        AutomationProperties.SetName(this, "TypeWhisper waveform logo");
        PointerEntered += (_, _) => { _hovering = true; Animate(); };
        PointerExited += (_, _) => { _hovering = false; Animate(); };
        Loaded += (_, _) => _uiSettings.AnimationsEnabledChanged += AnimationsChanged;
        Unloaded += (_, _) =>
        {
            _uiSettings.AnimationsEnabledChanged -= AnimationsChanged;
            _hovering = false;
            foreach (var bar in _bars)
            {
                var visual = ElementCompositionPreview.GetElementVisual(bar);
                visual.StopAnimation("Scale"); visual.Scale = Vector3.One;
            }
        };
    }

    private void AnimationsChanged(Windows.UI.ViewManagement.UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { if (IsLoaded) Animate(); });

    private void Animate()
    {
        for (var i = 0; i < _bars.Count; i++)
        {
            var bar = _bars[i];
            var visual = ElementCompositionPreview.GetElementVisual(bar);
            visual.CenterPoint = new Vector3(4.5f, (float)bar.Height / 2, 0);
            visual.StopAnimation("Scale");
            if (!_uiSettings.AnimationsEnabled) { visual.Scale = Vector3.One; continue; }
            var wave = visual.Compositor.CreateVector3KeyFrameAnimation();
            if (_hovering)
            {
                wave.Duration = TimeSpan.FromMilliseconds(1050 + i * 70);
                wave.IterationBehavior = AnimationIterationBehavior.Forever;
                wave.InsertKeyFrame(0, Vector3.One);
                wave.InsertKeyFrame(0.25f, new Vector3(1, i % 2 == 0 ? 0.48f : 1.15f, 1));
                wave.InsertKeyFrame(0.65f, new Vector3(1, i % 2 == 0 ? 1.2f : 0.52f, 1));
                wave.InsertKeyFrame(1, Vector3.One);
            }
            else
            {
                wave.Duration = TimeSpan.FromMilliseconds(180);
                wave.InsertKeyFrame(1, Vector3.One);
            }
            visual.StartAnimation("Scale", wave);
        }
    }
}
