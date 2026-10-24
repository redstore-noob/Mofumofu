using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mofumofu.ExtensionAPI;
using Mofumofu.ExtensionAPI.Actions;
using Mofumofu.ExtensionAPI.Data;
using Mofumofu.ExtensionAPI.UI;

namespace Mofumofu.Extension.LogshareService;

/// <summary>
/// LogShare 日志分析服务：
/// 游戏崩溃后右下角出现悬浮球，点开是 LogShare 的自动分析结果（问题 + 解决方案），
/// 玩家确认后可一键上传分享链接。上传遵循 LogShare EULA：内容公开、保留 7 天。
/// </summary>
public class LogshareServicePlugin : IMofuPlugin, ILaunchAction
{
    private IPluginContext _context = null!;
    private string? _pendingCrashLog;
    private LogShareInsights? _insights;
    private volatile bool _analysisDone;
    private volatile bool _ballDismissed;
    private IDisposable? _ballRegistration;

    public string Name => "LogShare 崩溃分析";

    public Version Version => new(1, 0, 0);

    public PluginTypeEnum ExtensionType => PluginTypeEnum.Tool;

    public string Description => "游戏崩溃后出现悬浮球，点开即看自动分析结果，并可一键上传分享链接。";

    public Boolean Init(IPluginContext context)
    {
        _context = context;
        context.Log("LogShare 插件初始化完成");
        return true;
    }

    /// <summary>游戏崩溃钩子：立即挂悬浮球，后台自动分析，完成后球变成可点状态。</summary>
    public void GameCrashedHook(string crashedLog)
    {
        if (string.IsNullOrWhiteSpace(crashedLog)) return;

        _pendingCrashLog = crashedLog;
        _insights = null;
        _analysisDone = false;
        _ballDismissed = false;
        RefreshBall();

        _ = Task.Run(async () =>
        {
            _insights = await new LogShareAPI().AnalyseAsync(crashedLog).ConfigureAwait(false);
            _analysisDone = true;
            RefreshBall();
            await Dispatcher.UIThread.InvokeAsync(() =>
                _context.Popup.Toast("崩溃分析完成，点击右下角悬浮球查看"));
        });
    }

    // ------------------------------------------------------------------
    // 悬浮球（Overlay 槽位，全页面可见）
    // ------------------------------------------------------------------

    private void RefreshBall()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _ballRegistration?.Dispose();
            _ballRegistration = _context.Pages.Add("*", PageSlot.Overlay, BuildBall, order: 999);
        });
    }

    private Control BuildBall()
    {
        // 已看过结果 / 分析未完成时返回零尺寸占位（不可见），Overlay 注册保留
        if (_ballDismissed || _analysisDone == false)
        {
            return new Control { Width = 0, Height = 0 };
        }

        var ball = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(24),
            Background = new SolidColorBrush(Color.Parse("#CCD64545")),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 28, 96),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new WarningGlyph(),
        };
        ToolTip.SetTip(ball, "崩溃分析完成，点我查看");
        ball.Tapped += (_, _) =>
        {
            _ballDismissed = true;
            RefreshBall();
            ShowAnalysisDialog(_pendingCrashLog ?? string.Empty, _insights);
        };
        return ball;
    }

    /// <summary>图标：自绘感叹号（IconPark 是宿主内部类型，插件侧用等价矢量替代）。</summary>
    private sealed class WarningGlyph : Control
    {
        public override void Render(DrawingContext context)
        {
            double size = Math.Min(Bounds.Width, Bounds.Height);
            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var white = new Pen(Brushes.White, 2.4, lineCap: PenLineCap.Round);

            context.DrawLine(white,
                new Point(center.X, center.Y - size * 0.18),
                new Point(center.X, center.Y + size * 0.12));
            context.DrawEllipse(Brushes.White, null,
                new Point(center.X, center.Y + size * 0.26), 1.6, 1.6);
        }
    }

    // ------------------------------------------------------------------
    // 分析结果弹窗
    // ------------------------------------------------------------------

    private void ShowAnalysisDialog(string crashedLog, LogShareInsights? insights)
    {
        var content = new StackPanel { Spacing = 10, Width = 460 };
        content.Children.Add(new TextBlock
        {
            Text = "游戏崩溃了。LogShare 自动分析结果：",
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        if (insights is null)
        {
            content.Children.Add(new TextBlock
            {
                Text = "分析服务暂不可用或未能识别已知问题。",
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            AppendMessages(content, "检测到的问题", insights.Problems);
            AppendMessages(content, "附加信息", insights.Information);
        }

        content.Children.Add(new TextBlock
        {
            Text = LogShareAPI.EulaNotice,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
        });

        var share = new Button
        {
            Content = "同意并上传分享",
            Classes = { "primary" },
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        share.Click += async (_, _) =>
        {
            share.IsEnabled = false;
            await UploadCrashLogAsync(crashedLog);
        };
        content.Children.Add(share);
        content.Children.Add(new TextBlock
        {
            Text = "点击弹窗外空白处关闭本窗口。",
            FontSize = 11,
            Foreground = Brushes.Gray,
        });

        _context.Popup.Dialog(content, "崩溃分析 · LogShare");
    }

    private async Task UploadCrashLogAsync(string crashedLog)
    {
        LogShareUploadResult result = await new LogShareAPI().UploadAsync(
            crashedLog, source: "Mofumofu-Plugin/LogshareService").ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (result.Success)
            {
                _context.Popup.Toast(result.HasUrl
                    ? "崩溃日志已上传：" + result.Url
                    : "崩溃日志已上传");
                _context.Log("崩溃日志已上传：" + result.Id);
            }
            else
            {
                _context.Popup.Toast("上传失败：" + (result.Error.Length > 0 ? result.Error : result.Message));
            }
        });
    }

    private void AppendMessages(StackPanel content, string title, IReadOnlyList<LogShareMessage> messages)
    {
        if (messages.Count == 0) return;

        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
        });
        foreach (LogShareMessage message in messages.Take(5))
        {
            string text = message.Counter > 1
                ? $"• {message.Message}（出现 {message.Counter} 次）"
                : $"• {message.Message}";
            content.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
            });
            foreach (string solution in message.Solutions.Take(3))
            {
                content.Children.Add(new TextBlock
                {
                    Text = "  ↳ 建议：" + solution,
                    FontSize = 12,
                    Foreground = Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }
    }
}
