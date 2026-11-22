using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mofumofu.Console;

public static class Ui
{
    private static readonly ConcurrentQueue<Action> __pending = new();
    private static int __uiThread = -1;
    private static object? __pumpToken;

    private static IApplication App => Application.Instance!;

    /// <summary>必须在主线程且 Application 初始化之后调用；重复调用无副作用。</summary>
    public static void StartPump()
    {
        if (__pumpToken is not null)
        {
            return;
        }

        __uiThread = Environment.CurrentManagedThreadId;
        __pumpToken = Application.AddTimeout(TimeSpan.FromMilliseconds(80), () =>
        {
            Drain();
            return true;
        });
    }

    private static void Drain()
    {
        while (__pending.TryDequeue(out Action? action))
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                System.Console.Error.WriteLine("[TUI] 界面更新失败：" + exception.Message);
            }
        }
    }

    /// <summary>抑制 ExecutionContext 流动，避免捕获主线程同步上下文导致续体被投递回主循环。</summary>
    private static Task StartWorker(Func<Task> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work);
        }
    }

    /// <summary>不捕获同步上下文，回调在调用方线程直接执行。</summary>
    private sealed class ActionProgress(Action<string> handler) : IProgress<string>
    {
        public void Report(string value) => handler(value);
    }

    /// <summary>主线程直接执行；后台线程入队，由消息泵在主循环中执行。</summary>
    public static void Post(Action action)
    {
        if (__uiThread == -1 || Environment.CurrentManagedThreadId == __uiThread)
        {
            action();
            return;
        }

        __pending.Enqueue(action);
    }

    public static void EnableEsc(Dialog dialog)
    {
        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode != KeyCode.Esc)
            {
                return;
            }

            key.Handled = true;
            Application.RequestStop(dialog);
        };
    }

    public static void Info(string title, string message)
    {
        MessageBox.Query(App, title, message, "确定");
    }

    public static void Error(string title, string message)
    {
        MessageBox.ErrorQuery(App, title, message, "确定");
    }

    public static bool Confirm(string title, string message)
    {
        return MessageBox.Query(App, title, message, "确定", "取消") == 0;
    }

    public static string? Prompt(string title, string label, string initial = "", bool secret = false)
    {
        string? result = null;
        Dialog dialog = new()
        {
            Title = title,
            Width = Dim.Percent(65),
            Height = 8,
        };

        Label text = new() { Text = label, X = 1, Y = 1, Width = Dim.Fill(1) };
        TextField field = new() { Text = initial, X = 1, Y = 2, Width = Dim.Fill(1), Secret = secret };
        Button ok = new() { Text = "确定", X = Pos.Percent(30), Y = Pos.AnchorEnd(1) };
        Button cancel = new() { Text = "取消", X = Pos.Percent(55), Y = Pos.AnchorEnd(1) };

        ok.Accepted += (_, _) =>
        {
            result = field.Text?.ToString() ?? string.Empty;
            Application.RequestStop(dialog);
        };
        cancel.Accepted += (_, _) => Application.RequestStop(dialog);
        field.KeyDown += (_, key) =>
        {
            if (key.KeyCode != KeyCode.Enter)
            {
                return;
            }

            result = field.Text?.ToString() ?? string.Empty;
            key.Handled = true;
            Application.RequestStop(dialog);
        };

        dialog.Add(text, field, ok, cancel);
        EnableEsc(dialog);
        field.SetFocus();
        Application.Run(dialog);
        return result;
    }

    public static Dictionary<string, string>? Form(string title, params (string Key, string Label, string Value, bool Secret)[] fields)
    {
        Dictionary<string, string>? result = null;
        int height = Math.Clamp(6 + fields.Length * 2, 8, 30);
        Dialog dialog = new()
        {
            Title = title,
            Width = Dim.Percent(70),
            Height = height,
        };

        List<(string Key, TextField Field)> inputs = [];
        for (int index = 0; index < fields.Length; index++)
        {
            (string key, string label, string value, bool secret) = fields[index];
            dialog.Add(new Label { Text = label, X = 1, Y = 1 + index * 2, Width = Dim.Fill(1) });
            TextField field = new()
            {
                Text = value,
                X = 1,
                Y = 2 + index * 2,
                Width = Dim.Fill(1),
                Secret = secret,
            };
            dialog.Add(field);
            inputs.Add((key, field));
        }

        Button ok = new() { Text = "确定", X = Pos.Percent(30), Y = Pos.AnchorEnd(1) };
        Button cancel = new() { Text = "取消", X = Pos.Percent(55), Y = Pos.AnchorEnd(1) };
        ok.Accepted += (_, _) =>
        {
            result = inputs.ToDictionary(item => item.Key, item => item.Field.Text?.ToString() ?? string.Empty);
            Application.RequestStop(dialog);
        };
        cancel.Accepted += (_, _) => Application.RequestStop(dialog);
        dialog.Add(ok, cancel);
        EnableEsc(dialog);
        if (inputs.Count > 0)
        {
            inputs[0].Field.SetFocus();
        }
        Application.Run(dialog);
        return result;
    }

    public static string? Select(string title, IReadOnlyList<string> options, int selectedIndex = 0)
    {
        if (options.Count == 0)
        {
            Info(title, "没有可选项。");
            return null;
        }

        string? result = null;
        Dialog dialog = new()
        {
            Title = title,
            Width = Dim.Percent(70),
            Height = Dim.Percent(70),
        };
        ObservableCollection<string> source = new(options);
        ListView list = new()
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(9),
        };
        list.SetSource(source);
        list.SelectedItem = Math.Clamp(selectedIndex, 0, options.Count - 1);

        Button ok = new() { Text = "确定", X = Pos.Percent(30), Y = Pos.AnchorEnd(1) };
        Button cancel = new() { Text = "取消", X = Pos.Percent(55), Y = Pos.AnchorEnd(1) };
        ok.Accepted += (_, _) =>
        {
            int index = list.SelectedItem ?? -1;
            result = index >= 0 && index < options.Count ? options[index] : null;
            Application.RequestStop(dialog);
        };
        cancel.Accepted += (_, _) => Application.RequestStop(dialog);
        list.Accepted += (_, _) =>
        {
            int index = list.SelectedItem ?? -1;
            result = index >= 0 && index < options.Count ? options[index] : null;
            Application.RequestStop(dialog);
        };

        dialog.Add(list, ok, cancel);
        EnableEsc(dialog);
        list.SetFocus();
        Application.Run(dialog);
        return result;
    }

    public static async Task<bool> RunTaskAsync(
        string title,
        string description,
        Func<IProgress<string>, CancellationToken, Task> work,
        bool cancellable = true)
    {
        (bool ok, _) = await RunTaskAsync<object?>(title, description, async (progress, token) =>
        {
            await work(progress, token).ConfigureAwait(false);
            return null;
        }, cancellable).ConfigureAwait(false);
        return ok;
    }

    public static Task<(bool Ok, T? Value)> RunTaskAsync<T>(
        string title,
        string description,
        Func<IProgress<string>, CancellationToken, Task<T>> work,
        bool cancellable = true)
    {
        StartPump();

        using CancellationTokenSource cts = new();
        T? value = default;
        bool success = false;
        string? error = null;
        StringBuilder log = new();

        Dialog dialog = new()
        {
            Title = title,
            Width = Dim.Percent(85),
            Height = Dim.Percent(70),
        };
        Label status = new() { Text = description, X = 1, Y = 1, Width = Dim.Fill(1) };
        ProgressBar bar = new() { X = 1, Y = 2, Width = Dim.Fill(1), Height = 1, Fraction = 0 };
        TextView output = new()
        {
            X = 1,
            Y = 3,
            Width = Dim.Fill(1),
            Height = Dim.Fill(3),
            ReadOnly = true,
            WordWrap = true,
        };

        dialog.Add(status, bar, output);
        if (cancellable)
        {
            Button cancel = new() { Text = "取消", X = Pos.Center(), Y = Pos.AnchorEnd(1) };
            cancel.Accepted += (_, _) =>
            {
                if (success)
                {
                    return;
                }

                status.Text = "正在取消…";
                cts.Cancel();
            };
            dialog.Add(cancel);
            dialog.KeyDown += (_, key) =>
            {
                if (key.KeyCode == KeyCode.Esc && !success)
                {
                    key.Handled = true;
                    status.Text = "正在取消…";
                    cts.Cancel();
                }
            };
        }

        void Append(string message)
        {
            log.AppendLine(message);
            if (log.Length > 12000)
            {
                log.Remove(0, log.Length - 10000);
            }

            output.Text = log.ToString();
            status.Text = message.Length > 120 ? message[..120] : message;
        }

        IProgress<string> progress = new ActionProgress(message => Post(() => Append(message)));

        Task? task = null;

        // 等对话框真正开始后再启动任务，避免任务瞬间完成时 RequestStop 早于 Begin 导致卡住。
        Application.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
        {
            task = StartWorker(async () =>
            {
                try
                {
                    value = await work(progress, cts.Token).ConfigureAwait(false);
                    success = true;
                    Post(() =>
                    {
                        bar.Fraction = 1;
                        Append("完成。");
                        Application.RequestStop(dialog);
                    });
                }
                catch (OperationCanceledException)
                {
                    Post(() =>
                    {
                        Append("已取消。");
                        Application.RequestStop(dialog);
                    });
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                    Post(() =>
                    {
                        Append("失败：" + exception.Message);
                        Application.RequestStop(dialog);
                    });
                }
            });
            return false;
        });

        Application.Run(dialog);
        try
        {
            task?.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 任务内的异常已通过 error 记录。
        }

        if (error is not null)
        {
            Error(title, error);
        }

        return Task.FromResult((success, value));
    }

    public static void ShowText(string title, string text)
    {
        Dialog dialog = new()
        {
            Title = title,
            Width = Dim.Percent(90),
            Height = Dim.Percent(85),
        };
        TextView view = new()
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(3),
            ReadOnly = true,
            WordWrap = false,
            Text = text,
        };
        Button close = new() { Text = "关闭", X = Pos.Center(), Y = Pos.AnchorEnd(1) };
        close.Accepted += (_, _) => Application.RequestStop(dialog);
        dialog.Add(view, close);
        EnableEsc(dialog);
        view.SetFocus();
        Application.Run(dialog);
    }

    public static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1073741824 => $"{bytes / 1073741824.0:0.##} GB",
            >= 1048576 => $"{bytes / 1048576.0:0.#} MB",
            >= 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes} B",
        };
    }
}
