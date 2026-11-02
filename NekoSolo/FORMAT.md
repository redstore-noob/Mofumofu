# NekoSolo 安装包格式

安装器接受完整的载荷 ZIP，或在自身 EXE 后追加相同 ZIP 的单文件安装包。

```text
manifest.json
icon.png
files/Mofumofu.Launcher.exe
files/<启动器程序集及原生依赖>
minecraft/versions/<真实版本目录>/<版本 JSON、JAR、模组、配置、存档>
minecraft/versions/<继承版本>/<版本 JSON、JAR>
minecraft/libraries/...
minecraft/assets/...
jre/bin/java.exe                 # 可选；需包含整个 Java 运行时
jre/...
```

`manifest.json` 使用 UTF-8，字段见 `Services/SoloManifest.cs`。当前导出格式为 `format: 2`。
`versionId` 必须等于实际版本目录名，`hasJava` 只在确实打包完整 Java 时为 `true`。
创作中心在 Windows 上导出完整 ZIP，包含启动器运行文件和选中实例；不会导出启动器账户及全局用户配置。
非自包含启动器的导出包仍要求目标机器安装匹配的 .NET 运行时；发布流水线使用自包含启动器。

`files/` 解压到安装目录；`minecraft/` 解压到安装目录的 `.minecraft/`；`jre/` 解压到
`.minecraft/runtime/jre/`。安装器写入 `solo.json` 和 `.nekosolo-installed`，主启动器启动时读取
`solo.json`，绑定游戏目录和随包 Java，并在没有已选实例时选择 `VersionId`。
安装目录整体移动后，这些路径会重新解析。

兼容将启动器放在 `overrides/` 下的旧完整包。缺少主启动器的包、普通启动器 ZIP 和空包会被拒绝，
不会写入新的安装完成标记。Release 下载只选择名称包含 `NekoSolo` 的 ZIP。

单文件格式为 `安装器 EXE + ZIP + 32 字节尾标`。尾标使用小端序：

| 偏移 | 长度 | 内容 |
| --- | --- | --- |
| 0 | 8 | `NKSOLO1` 后跟字节 `0x01` |
| 8 | 8 | ZIP 在文件中的偏移，Int64 |
| 16 | 8 | ZIP 字节数，Int64 |
| 24 | 4 | ZIP 的 CRC32，UInt32 |
| 28 | 4 | 保留，写 0 |

安装器会校验内嵌载荷的 CRC32。外部 ZIP 会检查目录结构，释放时由 ZIP 解压逻辑读取文件。

回归检查（Windows、.NET 10）：

```powershell
dotnet run --project tests/NekoSolo.Checks/NekoSolo.Checks.csproj -c Release
```

检查覆盖完整包导出安装、继承版本加载、包内清单、Java、文件统计、尾部分隔符、路径越界、
取消、安装后启动、错误 ZIP 拒绝以及内嵌载荷损坏。

> 注：`tests/NekoSolo.Checks` 检查工程尚未入库，发布流水线暂时跳过了上述回归检查，
> 工程补回并提交后需在 `release.yml` 中恢复对应的执行步骤。
