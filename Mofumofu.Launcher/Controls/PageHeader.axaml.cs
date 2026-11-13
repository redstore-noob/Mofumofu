using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Mofumofu.Launcher.Services;

namespace Mofumofu.Launcher.Controls;

public partial class PageHeader : UserControl
{
    public static readonly StyledProperty<bool> ShowBackProperty =
        AvaloniaProperty.Register<PageHeader, bool>(nameof(ShowBack), true);

    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<PageHeader, object?>(nameof(Actions));

    public event EventHandler? BackRequested;

    public PageHeader()
    {
        InitializeComponent();
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

        if (change.Property == ShowBackProperty)
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
