using Avalonia.Controls;

namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 一个用于控制启动器本体弹出信息的接口
/// </summary>
public interface IPopup
{
    /// <summary>
    /// 调用一次，启动器内来个弹窗。
    /// </summary>
    /// <param name="content">弹窗中显示的内容（比如说输入框）</param>
    /// <param name="title">弹窗的标题</param>
    public void Dialog(Control content, string title);
    /// <summary>
    /// 让启动器的左下角弹出小提示
    /// </summary>
    /// <param name="message">弹出的提示的信息</param>
    public void Toast(string message);
}