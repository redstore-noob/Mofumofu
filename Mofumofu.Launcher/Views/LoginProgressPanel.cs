using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MofuCore.Account;
using Mofumofu.Launcher.Services;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

public sealed class LoginProgressPanel : UserControl
{
    private static readonly (LoginStep Step, string Key, string Fallback)[] Steps =
    {
        (LoginStep.Authorized, "Account.Login.Step.Authorized", "登录页授权"),
        (LoginStep.MicrosoftToken, "Account.Login.Step.MicrosoftToken", "Microsoft 令牌"),
        (LoginStep.XboxLive, "Account.Login.Step.XboxLive", "Xbox Live"),
        (LoginStep.Xsts, "Account.Login.Step.Xsts", "XSTS"),
        (LoginStep.Minecraft, "Account.Login.Step.Minecraft", "Minecraft 令牌"),
        (LoginStep.Done, "Account.Login.Step.Profile", "获取档案"),
    };

    private readonly TextBlock _headline;
    private readonly TextBlock[] _marks = new TextBlock[Steps.Length];
    private readonly TextBlock[] _labels = new TextBlock[Steps.Length];
    private readonly StackPanel _successRow;
    private readonly Border _headFrame;
    private readonly Image _headImage;
    private readonly TextBlock _accountName;
    private readonly Button _cancelButton;
    private readonly Button _retryButton;
    private int _activeIndex = -1;
    private bool _running = true;

    public LoginProgressPanel()
    {
        Width = 380;

        _headline = new TextBlock
        {
            Text = L.T("Account.Login.Waiting", "等待在浏览器中完成授权…"),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        var stepPanel = new StackPanel { Spacing = 7 };
        for (var i = 0; i < Steps.Length; i++)
        {
            _marks[i] = new TextBlock
            {
                Text = "○",
                FontSize = 13,
                Foreground = ThemeTokens.Brush("NekoDimBrush"),
                Width = 18,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _labels[i] = new TextBlock
            {
                Text = L.T(Steps[i].Key, Steps[i].Fallback),
                FontSize = 12,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };

            stepPanel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { _marks[i], _labels[i] },
            });
        }

        _headImage = new Image
        {
            Width = 40,
            Height = 40,
            Stretch = Stretch.Uniform,
            IsVisible = false,
        };
        RenderOptions.SetBitmapInterpolationMode(_headImage, BitmapInterpolationMode.None);

        _headFrame = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(10),
            Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
            ClipToBounds = true,
            IsVisible = false,
            Child = _headImage,
        };

        _accountName = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _successRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            IsVisible = false,
            Children = { _headFrame, _accountName },
        };

        _cancelButton = new Button
        {
            Content = L.T("Account.Login.Cancel", "取消登录"),
            Classes = { "ghost" },
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 0, 8, 0),
        };
        _cancelButton.Click += (_, _) => OnCancelClicked();

        _retryButton = new Button
        {
            Content = L.T("Account.Login.Retry", "重试"),
            Classes = { "primary" },
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
            IsVisible = false,
        };
        _retryButton.Click += (_, _) => RetryRequested?.Invoke();

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _cancelButton, _retryButton },
        };

        Content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                _headline,
                stepPanel,
                new Border
                {
                    Height = 1,
                    Background = ThemeTokens.Brush("NekoBorderBrush"),
                },
                _successRow,
                buttonRow,
            },
        };

        SetActive(0);
    }

    public event Action? RetryRequested;

    public event Action? CancelRequested;

    public event Action? CloseRequested;

    public void Advance(LoginStep step)
    {
        var index = IndexOf(step);
        if (index < 0) return;

        for (var i = 0; i <= index; i++) SetDone(i);

        if (index + 1 < Steps.Length)
        {
            SetActive(index + 1);
        }
        else
        {
            _activeIndex = -1;
            _headline.Text = L.T("Account.Login.Processing", "正在完成登录…");
        }
    }

    public void ShowSuccess(string accountName, Bitmap? head)
    {
        _running = false;
        _activeIndex = -1;

        for (var i = 0; i < Steps.Length; i++) SetDone(i);

        _headline.Text = L.T("Account.Login.Success", "登录成功：{0}", accountName);
        _headline.Foreground = ThemeTokens.Brush("NekoSuccessBrush");

        _successRow.IsVisible = true;
        _accountName.Text = accountName;

        if (head is not null)
        {
            _headImage.Source = head;
            _headImage.IsVisible = true;
            _headFrame.IsVisible = true;
        }

        _cancelButton.Content = L.T("Common.Close", "关闭");
        _retryButton.IsVisible = false;
    }

    public void ShowFailure(string reason)
    {
        _running = false;

        if (_activeIndex >= 0 && _activeIndex < Steps.Length)
        {
            _marks[_activeIndex].Text = "✕";
            _marks[_activeIndex].Foreground = ThemeTokens.Brush("NekoDangerBrush");
            _labels[_activeIndex].Foreground = ThemeTokens.Brush("NekoDangerBrush");
        }

        _headline.Text = L.T("Account.Login.Failed", "登录失败：{0}", reason);
        _headline.Foreground = ThemeTokens.Brush("NekoDangerBrush");

        _cancelButton.Content = L.T("Common.Close", "关闭");
        _retryButton.IsVisible = true;
    }

    public void ShowCancelled()
    {
        _running = false;
        _headline.Text = L.T("Account.Login.Cancelled", "已取消登录");
        _headline.Foreground = ThemeTokens.Brush("NekoMutedBrush");
        _cancelButton.Content = L.T("Common.Close", "关闭");
        _retryButton.IsVisible = false;
    }

    private void OnCancelClicked()
    {
        if (_running)
        {
            CancelRequested?.Invoke();

            return;
        }

        CloseRequested?.Invoke();
    }

    private static int IndexOf(LoginStep step)
    {
        for (var i = 0; i < Steps.Length; i++)
        {
            if (Steps[i].Step == step) return i;
        }

        return -1;
    }

    private void SetDone(int index)
    {
        if (index < 0 || index >= Steps.Length) return;

        _marks[index].Text = "✓";
        _marks[index].Foreground = ThemeTokens.Brush("NekoSuccessBrush");
        _labels[index].Foreground = ThemeTokens.Brush("NekoTextBrush");
    }

    private void SetActive(int index)
    {
        _activeIndex = index;
        if (index < 0 || index >= Steps.Length) return;

        _marks[index].Text = "●";
        _marks[index].Foreground = ThemeTokens.Brush("NekoAccentBrush");
        _labels[index].Foreground = ThemeTokens.Brush("NekoTextBrush");
    }
}