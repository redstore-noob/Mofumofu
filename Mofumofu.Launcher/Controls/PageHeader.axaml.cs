using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Mofumofu.Launcher.Services;

namespace Mofumofu.Launcher.Controls;

public partial class PageHeader : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<PageHeader, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<PageHeader, string>(nameof(Subtitle), string.Empty);

    public static readonly StyledProperty<bool> ShowBackProperty =
        AvaloniaProperty.Register<PageHeader, bool>(nameof(ShowBack), true);

    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<PageHeader, object?>(nameof(Actions));

    public event EventHandler? BackRequested;

    public PageHeader()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public bool ShowBack
    {
        get => GetValue(ShowBackProperty);
        set => SetValue(ShowBackProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TitleProperty)
        {
            TitleText.Text = change.NewValue as string ?? string.Empty;
        }
        else if (change.Property == SubtitleProperty)
        {
            var subtitle = change.NewValue as string ?? string.Empty;
            SubtitleText.Text = subtitle;
            SubtitleText.IsVisible = !string.IsNullOrWhiteSpace(subtitle);
        }
        else if (change.Property == ShowBackProperty)
        {
            BackButton.IsVisible = change.NewValue is true;
        }
        else if (change.Property == ActionsProperty)
        {
            ActionsHost.Content = change.NewValue;
        }
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (BackRequested is not null)
        {
            BackRequested.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Shell.BackToDesktop();
        }
    }
}
