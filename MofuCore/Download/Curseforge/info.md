# 关于Curseforge功能的说明

> 由于Curseforge的部分政策原因，暂时不予Curseforge功能适配。

## 2026-11-13 补充：整合包导入的边界

在上述前提下，启动器只做「按官方 API 读取玩家自己的整合包清单」这一件事，
不抓取、不内嵌、不镜像、不重新分发任何 Curseforge 内容：

- 需要玩家自己在「设置 → 下载」填写 Curseforge API Key，启动器不内置任何 Key。
- 所有文件都通过官方接口 `download-url` 换取地址后直连官方 CDN 下载。
- 作者关闭第三方分发的文件一律跳过并提示，不做任何绕过。
- 不支持按关键词搜索、浏览、推荐 Curseforge 内容。

实现见 `CurseForgeBundleInstall.cs`。
