using System;
using System.Text.RegularExpressions;
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
    private readonly object _stateLock = new();
    private string _pendingCrashLog = string.Empty;   // 已脱敏
    private LogShareInsights? _insights;
    private volatile bool _analysisDone;
    private volatile bool _ballDismissed;
    private IDisposable? _ballRegistration;
    private CancellationTokenSource? _shareCts;
    private Task? _shareTask;

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

    /// <summary>游戏崩溃钩子：缓存脱敏后的日志并挂悬浮球；任何网络外发都等用户在弹窗里同意。</summary>
    public void GameCrashedHook(string crashedLog)
    {
        if (string.IsNullOrWhiteSpace(crashedLog)) return;

        string redacted = Redact(crashedLog);
        CancellationTokenSource? previous;
        lock (_stateLock)
        {
            previous = _shareCts;
            _shareCts = null;
            _pendingCrashLog = redacted;
            _insights = null;
            _analysisDone = false;
            _ballDismissed = false;
        }
        // 取消上一次仍在进行的外发，避免旧会话结果覆盖新会话（串行/加锁）。
        if (previous is not null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
        }

        RefreshBall();
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
        // 崩溃后即可点开查看同意说明；同意并完成分享前保持可见，之后返回零尺寸占位。
        if (_ballDismissed || _analysisDone)
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
        ToolTip.SetTip(ball, "游戏崩溃了，点我查看崩溃说明");
        ball.Tapped += (_, _) =>
        {
            _ballDismissed = true;
            RefreshBall();
            ShowAnalysisDialog(_insights, allowShare: true);
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

    private void ShowAnalysisDialog(LogShareInsights? insights, bool allowShare)
    {
        var content = new StackPanel { Spacing = 10, Width = 460 };
        content.Children.Add(new TextBlock
        {
            Text = "游戏崩溃了。LogShare 分析结果：",
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        if (insights is null)
        {
            content.Children.Add(new TextBlock
            {
                Text = allowShare
                    ? "点击下方按钮同意后，才会对日志（已脱敏）进行分析并上传。"
                    : "分析服务暂不可用或未能识别已知问题。",
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

        if (allowShare)
        {
            var share = new Button
            {
                Content = "同意并上传分享",
                Classes = { "primary" },
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            // 同步事件处理器：把外发交给可观察、可取消的 Task，避免 async void 异常无法捕获。
            share.Click += (_, _) =>
            {
                share.IsEnabled = false;
                StartShare();
            };
            content.Children.Add(share);
        }
        content.Children.Add(new TextBlock
        {
            Text = "点击弹窗外空白处关闭本窗口。",
            FontSize = 11,
            Foreground = Brushes.Gray,
        });

        _context.Popup.Dialog(content, "崩溃分析 · LogShare");
    }

    private void StartShare()
    {
        string content;
        CancellationTokenSource cts;
        CancellationTokenSource? previous;
        lock (_stateLock)
        {
            content = _pendingCrashLog;
            previous = _shareCts;
            cts = new CancellationTokenSource();
            _shareCts = cts;
        }
        if (previous is not null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
        }

        // 可观察：保存 Task 字段并显式观察异常；通过 CTS 支持取消。
        Task share = ShareAsync(content, cts.Token);
        _shareTask = share;
        _ = share.ContinueWith(
            t => Dispatcher.UIThread.Post(() =>
                _context.Log("分享任务异常：" + (t.Exception?.GetBaseException().Message ?? "未知"))),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private async Task ShareAsync(string content, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(content)) return;

            // 用户已点击「同意」，此处才发生外发；content 在 GameCrashedHook 已脱敏。
            LogShareInsights? insights = await new LogShareAPI()
                .AnalyseAsync(content, cancellationToken).ConfigureAwait(false);
            LogShareUploadResult result = await new LogShareAPI()
                .UploadAsync(content, source: "Mofumofu-Plugin/LogshareService",
                    cancellationToken: cancellationToken).ConfigureAwait(false);

            lock (_stateLock)
            {
                if (cancellationToken.IsCancellationRequested) return;
                _insights = insights;
                _analysisDone = true;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _context.Popup.Toast(result.Success
                    ? (result.HasUrl ? "崩溃日志已上传：" + result.Url : "崩溃日志已上传")
                    : "上传失败：" + (result.Error.Length > 0 ? result.Error : result.Message));
                _context.Log("崩溃日志上传：" + (result.Success ? result.Id : "失败"));
                // 同意后展示分析结果（不再提供上传按钮，避免重复外发）。
                ShowAnalysisDialog(insights, allowShare: false);
            });
        }
        catch (OperationCanceledException)
        {
            // 新的崩溃会话取代了本次分享
        }
        catch (Exception e)
        {
            string message = "崩溃日志分享失败：" + e.Message;
            Dispatcher.UIThread.Post(() => _context.Log(message));
        }
    }

    /// <summary>
    /// 外发前脱敏：尽力过滤令牌、密码、Cookie、Authorization 与命令行里的 accessToken 等。
    /// 完整保证依赖全局输入消毒层（需架构决策），此处为可安全落地的局部收敛。
    /// </summary>
    private static string Redact(string log)
    {
        if (string.IsNullOrEmpty(log)) return string.Empty;

        string redacted = Regex.Replace(log,
            @"(--accessToken|--uuid|--username|auth_access_token|auth_session|accessToken|clientToken)\s*[=:]\s*\S+",
            "${1}=[已脱敏]", RegexOptions.IgnoreCase);
        redacted = Regex.Replace(redacted,
            @"(Authorization|Cookie|Set-Cookie|token|password|secret|api[_-]?key)\s*[:=]\s*\S+",
            "${1}: [已脱敏]", RegexOptions.IgnoreCase);
        redacted = Regex.Replace(redacted,
            @"(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+",
            "${1} [已脱敏]", RegexOptions.IgnoreCase);
        return redacted;
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
