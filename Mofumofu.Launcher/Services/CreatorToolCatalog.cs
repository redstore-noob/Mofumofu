using System.Collections.Generic;
using Mofumofu.Launcher.Models;

namespace Mofumofu.Launcher.Services;

/// <summary>开始菜单 ComboBox 里的工具清单（不含任何示例数据）。</summary>
public static class CreatorToolCatalog
{
    public static readonly IReadOnlyList<CreatorTool> Tools = new[]
    {
        new CreatorTool("resourcepack", "资源包编辑器", "新建/编辑资源包，管理贴图与 pack.mcmeta。", "IconFluentImage", "#3FC6C1"),
        new CreatorTool("command", "命令生成器", "17 种常用命令实时生成：give / summon / effect… 支持选择器、物品组件与 NBT。", "IconFluentCode", "#8B7CF6"),
        new CreatorTool("gradient", "渐变文字", "生成 Minecraft 渐变文字与彩色文本。", "IconFluentFont", "#F2AE49"),
        new CreatorTool("cube3d", "立方体文字 3D", "把文字体素化成立方体预览。", "IconFluentCube", "#4CC38A"),
        new CreatorTool("skin", "皮肤编辑器", "离线皮肤绘制、导入与 3D 预览。", "IconFluentPerson", "#E5698F"),
        new CreatorTool("modpack", "整合包导出", "把当前实例导出为 Modrinth 整合包。", "IconFluentBox", "#5A9CF8"),
        new CreatorTool("solo", "NekoSolo 制作", "打包单文件整合包，分发即开即玩。", "IconFluentDocument", "#3FC6C1"),
    };
}
