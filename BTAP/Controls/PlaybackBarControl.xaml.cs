using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BTAP.Controls;

public sealed partial class PlaybackBarControl : UserControl
{
    // Segoe MDL2 Assets glyphs
    private const string PlayGlyph  = "";
    private const string PauseGlyph = "";

    public event RoutedEventHandler? PlayClicked;
    public event RoutedEventHandler? StepBackClicked;
    public event RoutedEventHandler? StepFwdClicked;
    public event RoutedEventHandler? LoopClicked;
    public event RoutedEventHandler? MarkerClicked;
    public event RoutedEventHandler? FullscreenClicked;

    public PlaybackBarControl() => InitializeComponent();

    public void SetPlayhead(string label) => TbPlayhead.Text = label;
    public void SetDuration(string label) => TbDuration.Text = label;
    public void SetSpeed(double speed) => TbSpeed.Text = $"{speed}×";

    public void SetIsPlaying(bool playing) =>
        PlayIcon.Glyph = playing ? PauseGlyph : PlayGlyph;

    public void SetIsLooping(bool looping) =>
        BtnLoop.Foreground = looping
            ? (Brush)Application.Current.Resources["AccentInkBrush"]
            : (Brush)Application.Current.Resources["TextMutedBrush"];

    private void OnPlayClick(object sender, RoutedEventArgs e) =>
        PlayClicked?.Invoke(this, e);

    private void OnStepBackClick(object sender, RoutedEventArgs e) =>
        StepBackClicked?.Invoke(this, e);

    private void OnStepFwdClick(object sender, RoutedEventArgs e) =>
        StepFwdClicked?.Invoke(this, e);

    private void OnLoopClick(object sender, RoutedEventArgs e) =>
        LoopClicked?.Invoke(this, e);

    private void OnMarkerClick(object sender, RoutedEventArgs e) =>
        MarkerClicked?.Invoke(this, e);

    private void OnFullscreenClick(object sender, RoutedEventArgs e) =>
        FullscreenClicked?.Invoke(this, e);
}
