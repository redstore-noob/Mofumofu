using System;
﻿using System.Diagnostics;
namespace Mofumofu.Launcher;

/// <summary>
/// 打开一个链接
/// 但其实目前只有打开项目链接要用🤔
/// </summary>
public class OpenLinkBrowser
{
    /// <summary>
    /// 打开一个浏览器链接
    /// </summary>
    /// <param name="url">打开的浏览器链接，默认就填https://redstore-noob.github.io/宣传自己了😋</param>
    /// <returns>操作是否成功(成功时返回string="success"，错误时返回一个报错信息)，在Linux环境下极其容易出问题，用的xdg-open</returns>
    public static string OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            /*
             * 只有失败时才尝试一次Linux平台下打开
             */
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    Process.Start("xdg-open", url);
                    return "success";
                }
                
            }
            catch (Exception e)// 放弃打开了，返回一个报错
            {
                return e.Message;
            }
        }
        return "success";
    }
}