using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pathleap.UI.Theme;

namespace Pathleap.UI.Controls;

/// <summary>设计稿样式的拨动开关（36×20），开启色使用当前强调色。</summary>
public sealed partial class LpSwitch : UserControl
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
        nameof(IsOn),
        typeof(bool),
        typeof(LpSwitch),
        new PropertyMetadata(false, OnIsOnChanged));

    public LpSwitch()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ThemeService.Instance.Changed += OnThemeChanged;
            Apply();
        };
        Unloaded += (_, _) => ThemeService.Instance.Changed -= OnThemeChanged;
    }

    public event EventHandler? Toggled;

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var lpSwitch = (LpSwitch)d;
        lpSwitch.Apply();
        if (FrameworkElementAutomationPeer.FromElement(lpSwitch) is LpSwitchAutomationPeer lpPeer)
        {
            lpPeer.RaisePropertyChangedEvent(
                TogglePatternIdentifiers.ToggleStateProperty,
                lpSwitch.IsOn ? ToggleState.Off : ToggleState.On,
                lpSwitch.IsOn ? ToggleState.On : ToggleState.Off);
        }
    }

    /// <summary>开关对读屏/自动化暴露 Toggle 模式，状态变化与真实 IsOn/Toggled 一致。</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new LpSwitchAutomationPeer(this);

    internal void ToggleFromAutomation()
    {
        if (!IsEnabled)
        {
            return;
        }

        IsOn = !IsOn;
        Toggled?.Invoke(this, EventArgs.Empty);
    }

    private sealed class LpSwitchAutomationPeer : FrameworkElementAutomationPeer, IToggleProvider
    {
        private readonly LpSwitch _lpOwner;

        public LpSwitchAutomationPeer(LpSwitch owner) : base(owner) => _lpOwner = owner;

        public ToggleState ToggleState => _lpOwner.IsOn ? ToggleState.On : ToggleState.Off;

        public void Toggle() => _lpOwner.ToggleFromAutomation();

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.CheckBox;

        protected override string GetClassNameCore() => nameof(LpSwitch);

        protected override string GetNameCore()
        {
            var lpName = base.GetNameCore();
            return string.IsNullOrWhiteSpace(lpName) ? "开关" : lpName;
        }

        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Toggle ? this : base.GetPatternCore(patternInterface);
    }

    private void OnThemeChanged()
    {
        Apply();
    }

    private void Apply()
    {
        Track.Background = IsOn ? ThemeService.Instance.AccentBrush : ThemeService.Instance.TrackOffBrush;
        Track.BorderBrush = IsOn ? ThemeService.Instance.AccentBrush : ThemeService.Instance.TrackOffBorderBrush;
        Thumb.HorizontalAlignment = IsOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Thumb.Margin = IsOn ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0);
        Thumb.Fill = new SolidColorBrush(ColorsWhite);
        Opacity = IsEnabled ? 1.0 : 0.45;
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e) => ToggleFromAutomation();

    private static Windows.UI.Color ColorsWhite => Windows.UI.Color.FromArgb(255, 255, 255, 255);
}
