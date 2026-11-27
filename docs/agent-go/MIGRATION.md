# Mofumofu ServerAgent → Go 迁移文档

> 状态：**Phase 8 本地验收完成；Linux/真实 Launcher/SSH 验收待 CI 或 VPS**
> 行为基准：当前仓库 C# `Mofumofu.ServerAgent` + `MofuCore.Server`，而不是假设中的 Minecraft 通用行为。  
> 目标平台：`linux-amd64`、`linux-arm64`，CGO 关闭，单静态二进制。  
> 本文档中的“计划偏差”只有在实现完成并通过测试后才会标记为已交付。

## 1. Recon 范围与结论

已完整阅读：

- `Mofumofu.ServerAgent/Program.cs`
- `Mofumofu.ServerAgent/AgentOptions.cs`
- `Mofumofu.ServerAgent/ProtocolSupport.cs`
- `Mofumofu.ServerAgent/Endpoints/AgentEndpoints.cs`
- `Mofumofu.ServerAgent/Core/CoreGateway.cs`
- `Mofumofu.ServerAgent/Probe/ProbeGateway.cs`
- `Mofumofu.ServerAgent/Services/{StatusService,PlayerService,BackupService}.cs`
- `Mofumofu.ServerAgent/Files/{SafeFileService,PropertyPatchService}.cs`
- `Mofumofu.ServerAgent/Events/{EventJournal,ServerMonitorService,ProbeEventService}.cs`
- `Mofumofu.ServerAgent/Operations/{OperationService,ConfirmationService}.cs`
- `Mofumofu.ServerAgent/Security/{RequestSecurity,RequestSecurityMiddleware,TokenRegistry}.cs`
- `MofuCore/Server/ServerManage.cs`、`ServerProperties`、Java/RCON/玩家名单相关实现
- `MofuCore/Server/ServerDownload.cs`（仅用于判断迁移边界）
- `Mofumofu.RemoteProtocol/` 全部非构建产物
- `tests/contracts/probe-event-golden.json` 与现有 .NET 协议/Agent/Launcher 测试
- Launcher 消费端 `RemoteApiClient.cs`、`RemoteEventStream.cs`、`RemoteServerBackend.cs`
- NekoProbe callback、token、备份与 HTTP 实现（用于交叉验证，不修改）
- `deploy/install.sh`、`.github/workflows/release.yml`

关键结论：

1. Agent 共有 **27 个** `/v1/servers/{instanceId}` HTTP/WS 路由，另有 **1 个** `/internal/probe/events`。
2. Agent 不创建、不下载、不安装实例；启动要求 `server.json` 已存在。`ServerDownload` 不迁入 Agent。
3. Launcher 所有请求都带 `apiVersion=1.0`；WS 使用 Bearer Authorization 和可恢复 cursor。
4. token registry 由 NekoProbe 拥有。Agent 只读 registry 做认证；derive/revoke/rotate 转发 Probe。
5. 危险操作是 `prepare → confirmation execute → operation`，confirmation 和 operation 都只在内存中。
6. C# Agent 的 handshake capabilities 是静态 `Known` 全量；本任务明确要求动态 capabilities，因此 Go 将采用动态能力并记录偏差。
7. C# properties 允许未知自定义 key，并非严格白名单；本任务以仓库行为为准。
8. 当前 C# Agent/Core 不产生 vanilla 玩家 join/left diff；Go 默认保持兼容，可选开启 diff。
9. Agent/Core 备份与 Probe 备份是两套不同格式；Go Agent 只复刻 Agent/Core 的时间戳 zip 语义。
10. 当前 Launcher 创建备份时发送空 label，而 C# Agent 要求 label 非空。这是现有上下游 bug；Go 先保留 C# 校验并记录，不能在迁移中静默改协议行为。

## 2. 协议不变量

### 2.1 JSON

- 属性名 camelCase；读取大小写不敏感；未知字段忽略。
- 写时省略 `null` 属性，但保留 `""`、`0`、`false`、`[]`、`{}`。
- 字典中的 `null` 值不省略；properties patch 用 `null` 表示删除未知自定义 key。
- `DateTimeOffset` 使用 ISO-8601 round-trip 格式，例如 `2026-11-22T08:00:00+00:00`。
- 顶层 JSON `null`、无效 JSON、类型不匹配均为 `400 invalid_request`。
- WebSocket 的具体帧首字段是 `type`，随后是 `apiVersion`、`cursor`、`sentAt`。

### 2.2 API version

- Agent GET/WS：query `apiVersion` 必填。
- Agent POST/PUT：body 中 `apiVersion` 默认 `1.0`，可省略；显式 null/非法值失败。
- 格式必须恰好为两个无符号十进制整数段，例如 `1.0`、`01.00`、`1.99`。
- Agent 只比较 major=1；minor 不参与兼容判断。
- Probe 对 body 的要求更严格：必须精确等于 `1.0`。

### 2.3 限制

| 项目 | 值 |
|---|---:|
| Agent HTTP body | 4 MiB |
| 通用文本文件 | 2 MiB |
| 命令长度 | 256 UTF-16 code units |
| 玩家名 | 1–16 ASCII `[A-Za-z0-9_]` |
| confirmation 有效期 | 60 秒 |
| token | 32 随机字节 → 64 位小写 hex |
| rate limit | Read 20/s、Write 5/s、Sensitive 1/s，每 token 独立 |
| Caddy 外部 body | 3 MB（安装脚本现状，低于 Agent 4 MiB） |

## 3. 端点兼容矩阵

全局错误适用于所有端点：401 `unauthorized`、403 `forbidden`/`insufficient_scope`、429 `rate_limited`、400 版本错误、413 `payload_too_large`。普通成功响应均为 HTTP 200，除非另行注明。

| # | 方法 | 路径（相对 `/v1/servers/{instanceId}`） | 请求 → 响应 | Scope / 类别 | 关键行为与错误 |
|---:|---|---|---|---|---|
| 1 | POST | `/handshake` | `HandshakeRequest` → `HandshakeResponse` | `read` / Read | 实例不存在 404；返回实例、部署模式、capabilities、排序后的 scopes、tokenId、`/ws`；不协商 requested capabilities |
| 2 | GET | `/status` | query `apiVersion` → `StatusResponse` | `read` / Read | Core 状态；maxPlayers 来自 `server.json`，MOTD 来自 properties |
| 3 | POST | `/console/commands` | `ConsoleCommandRequest` → `ConsoleCommandResponse` | `command` / Write | 非法命令 400 `invalid_command`；非 running 409 `server_offline`；RCON 优先，失败回退 stdin；不创建 operation |
| 4 | GET | `/players/online` | query `apiVersion` → `OnlinePlayersResponse` | `read` / Read | running/starting/stopping 时优先 Probe；仅 Probe 网络不可用回退 Core；Probe HTTP 错误透传 |
| 5 | GET | `/players/{kind}` | `kind=whitelist/ops/bans` → `PlayerListResponse` | `players` / Read | 非法 kind 404；按 Name ordinal 排序；revision=`pl_`+SHA-256(DTO JSON) |
| 6 | POST | `/players/{kind}/add` | `PlayerAddRequest` → `PlayerMutationResponse` | `players` / Write | 非法名字 400 `invalid_player_name`；opLevel 1–4；reason ≤256；重名 409；revision 冲突 409；运行中走 MC 命令 |
| 7 | POST | `/players/{kind}/remove` | `PlayerRemoveRequest` → `PlayerMutationResponse` | `players` / Write | 不存在 404；运行中走 `whitelist remove/deop/pardon`；返回删除前条目 |
| 8 | GET | `/files` | query `path` → `FileListResponse` | `files` / Read | 空/省略 path 表示根；不存在 404；最多 1000 项；跳过 reparse point、受保护路径、>2 MiB 文件 |
| 9 | GET | `/files/content` | query `path` → `FileContentResponse` | `files` / Read | 空 path 400 `invalid_path`；严格 UTF-8；>2 MiB 413；非法 UTF-8 400 `invalid_request` |
| 10 | POST | `/files/create` | `FileCreateRequest` → `FileMutationResponse` | `files` / Write | 父目录必须存在；已存在 409；文件 `CreateNew`，不覆盖 |
| 11 | POST | `/files/rename` | `FileRenameRequest` → `FileMutationResponse` | `files` / Write | newName 只能单 leaf，同父目录；revision 必须精确匹配；目标存在 409 |
| 12 | PUT | `/files/content` | `FileWriteRequest` → `FileWriteResponse` | `files` / Write | expected=`0`/空可创建；否则 revision 精确匹配；同目录临时文件原子替换 |
| 13 | POST | `/files/delete/prepare` | `FileDeleteRequest` → `ConfirmationPrepareResponse` | `files` / Sensitive | prepare 和 execute 都校验 revision；目录递归删除非事务化 |
| 14 | GET | `/properties` | query `apiVersion` → `ServerPropertiesResponse` | `files` / Read | 固定 `server.properties`；响应移除所有大小写变体的 `rcon.password` |
| 15 | POST | `/properties/prepare` | `ServerPropertiesPatchRequest` → `ConfirmationPrepareResponse` | `files` / Sensitive | prepare 只校验 revision；key/value 校验延迟到 operation；禁止远程改 `rcon.password` |
| 16 | GET | `/backups` | query `apiVersion` → `BackupListResponse` | `backup` / Sensitive | 计算并缓存 SHA-256；并发 prune 删除时跳过该项 |
| 17 | POST | `/backups` | `BackupCreateRequest` → `BackupCreateResponse` | `backup` / Sensitive | label 必填 ≤128；description ≤1024；同步等待 operation；失败 HTTP 500 `operation_failed` |
| 18 | POST | `/backups/restore/prepare` | `BackupRestoreRequest` → `ConfirmationPrepareResponse` | `backup` / Sensitive | backupId 合法且存在；必须已停止，否则 409 `server_running`；execute 再校验 SHA-256 |
| 19 | POST | `/backups/delete/prepare` | `BackupDeleteRequest` → `ConfirmationPrepareResponse` | `backup` / Sensitive | 不要求停止；execute 再校验 SHA-256；删 zip 和 sidecar |
| 20 | POST | `/control/prepare` | `ControlRequest` → `ConfirmationPrepareResponse` | `control` / Sensitive | action=`start/stop/restart/kill`；start 已运行 409，其他未运行 409；execute 再检查状态 |
| 21 | POST | `/confirm/execute` | `ConfirmationExecuteRequest` → `ConfirmationExecuteResponse` | Bearer / Sensitive | 同 instance、同 tokenId、当前仍有原 scope；60 秒；一次性；立即返回 operation 快照 |
| 22 | GET | `/operations/{operationId}` | query `apiVersion` → `OperationResponse` | Bearer / Read | 动态要求 operation 原 scope；不存在 404 `operation_not_found`；terminal 仍返回 200 |
| 23 | GET | `/tokens` | query `apiVersion` → `TokenListResponse` | root / Read | 转发 Probe；审计 `tokens.list` |
| 24 | POST | `/tokens/derive` | `TokenDeriveRequest` → `TokenIssuedResponse` | root / Write | 转发 Probe；明文 token 只出现在响应 |
| 25 | POST | `/tokens/revoke` | `TokenRevokeRequest` → `TokenRevokeResponse` | root / Write | 转发 Probe；不能撤销最后一个 active root |
| 26 | POST | `/tokens/rotate` | `TokenRotateRequest` → `TokenRotateResponse` | root / Write | grace 固定 300 秒；replacement 保留原 expiresAt |
| 27 | GET | `/ws` | query `apiVersion`、可选 `cursor` → WS Text frames | `read` / Read | 非 upgrade 400 `invalid_request`；snapshot/replay/reset；慢消费者断开 |
| 28 | POST | `/internal/probe/events` | 原始 body + 4 个 HMAC headers → `AgentAcknowledgementResponse` | loopback + HMAC | 新事件 202；重复 eventId 200；非 loopback 403；签名/时间/nonce 错误 401 |
| 29 | POST | `/install/prepare`（**Go 扩展**，C# 无此路由） | `ServerInstallRequest` → `ConfirmationPrepareResponse` | `control` / Sensitive | 白名单外 403 `forbidden`；attach 或未配置 `InstallRootDirectory` 403 `capability_not_supported`；目标非空 409；execute 见 3.2 |

### 3.1 Token 转发错误

- Probe 网络不可用、5 秒超时、响应 >4 MiB：Agent 返回 409 `server_offline`。
- Probe 非 200：透传 HTTP status、`code`、`message`、`details`，丢弃 Probe 的 requestId/operationId。
- Probe 响应无法解析：500 `internal_error`。
- registry 持久化失败可透传 503 `registry_unavailable`。

### 3.2 远程安装（Go 扩展）

C# Agent 没有远程创建实例的路由，且其启动要求实例已存在。Go 扩展以确认流程安装服务端核心：

- **scope 决策**：复用 `control`，不新增 scope。新增 scope 需要同步 C# `ProtocolScopes` 与 token 校验，超出本次范围。代价是持有 `control` 的 token 同时可以安装实例，部署时应单独签发。
- **流程**：`POST /install/prepare` 校验白名单、EULA、版本 ID、目标目录后签发 60 秒单次 confirmation（`server.install`）；`POST /confirm/execute` 启动 operation（kind `server.install`，scope `control`）；进度通过 `GET /operations/{id}` 与 WS `operation` 帧推送（`progressPercent` 与 `message` 随下载阶段更新）。
- **审计**：prepare 写 `server.install.prepare`，execute 写 `confirmation.execute`，operation 内部写 `server.install`（pending / succeeded / failed:<code>）。审计参数不含 MOTD 原文，只记长度。
- **限流**：prepare 使用 Sensitive（1/s）。
- **目标**：固定为 `<GoAgent.InstallRootDirectory>/<InstanceId>`。远程请求不能指定路径、Java 可执行文件或任意 URL。目录必须不存在或为空，失败时只清理本次创建的文件。
- **生效方式**：安装完成后 Agent 进程仍绑定原实例。需要把 `ServerRootDirectory` 指向 `InstallRootDirectory` 并重启 Agent 才会接管。
- **HTTP 边界**：仅 https；拒绝 userinfo、localhost、回环/私网/链路本地/保留地址（DNS 解析后检查，拨号时再检查）；重定向逐跳校验，最多 5 跳；core 体积上限 512 MiB；vanilla 校验 manifest 给出的 sha1 与 size。

## 4. DTO 字段基准

所有 DTO 的字段顺序应尽量与 C# 声明顺序一致；动态字段测试会 normalize，但静态 golden 使用精确字节。

| DTO | 字段 |
|---|---|
| `HandshakeResponse` | `apiVersion, instanceId, serverName, deploymentMode, serverImplementationVersion, capabilities, scopes, serverTime, webSocketPath, tokenId?` |
| `StatusResponse` | `apiVersion, instanceId, state, onlinePlayers, maxPlayers, cpuPercent?, memoryUsedBytes?, memoryMaxBytes?, tps?, uptimeSeconds?, minecraftVersion?, motd?, observedAt` |
| `ConsoleEntry` | `sequence, timestamp, level, source, message` |
| `PlayerInfo` | `name, uuid?, joinedAt?, onlineSeconds?, pingMilliseconds?, gameMode?` |
| `PlayerListEntry` | `name, uuid?, opLevel?, bypassesPlayerLimit?, reason?, source?, createdAt?, expiresAt?` |
| `PlayerListResponse` | `apiVersion, kind, revision, entries` |
| `PlayerMutationResponse` | `apiVersion, kind, revision, entry, removed` |
| `FileEntry` | `path, isDirectory, sizeBytes?, modifiedAt?, revision?, writable` |
| `FileContentResponse` | `apiVersion, path, content, encoding, revision, sizeBytes, modifiedAt?` |
| `FileWriteResponse` | `apiVersion, path, revision, sizeBytes, modifiedAt` |
| `FileMutationResponse` | `apiVersion, path, revision, isDirectory, modifiedAt` |
| `ServerPropertiesResponse` | `apiVersion, revision, restartRequired, properties` |
| `BackupMetadata` | `id, label, description?, createdAt, sizeBytes, sha256, minecraftVersion?` |
| `BackupCreateResponse` | `apiVersion, backup, operation` |
| `OperationInfo` | `id, kind, state, createdAt, startedAt?, completedAt?, progressPercent?, message?, metadata?, error?` |
| `ConfirmationPrepareResponse` | `apiVersion, confirmationId, action, summary, createdAt, expiresAt, expiresInSeconds` |
| `ConfirmationExecuteResponse` | `apiVersion, operation` |
| `ServerEvent` | `eventId, type, occurredAt, message, details` |
| `TokenInfo` | `id, name, isRoot, scopes, createdAt, expiresAt?, lastUsedAt?, revokedAt?` |
| `ApiError` | `apiVersion, code, message, details?, requestId?, operationId?` |

WS 公共字段：`type, apiVersion, cursor, sentAt`。

- `console`: `entries`
- `event`: `event`
- `snapshot`: `status, players, recentConsole, activeOperations`
- `reset`: `reason, requiresSnapshot`
- `operation`: `operation`

## 5. 行为矩阵

### 5.1 `server.json` 与 Java 启动

精确字段：

```text
id, name, core, coreVersion, mcVersion, port, maxPlayers,
javaPath, memoryMB, memoryMinMB, extraJavaArgs, autoRestart
```

- 读取大小写不敏感；写出使用上述精确名称。
- 文件不存在或损坏时 C# 返回 null；Agent 启动因此拒绝不存在的实例。
- `port` 非法时创建流程默认 25565；`maxPlayers<=0` 默认 20；Agent 本身不重写文件。
- `BuildJavaArguments` 顺序：`-Xmx`、`-Xms`、`extraJavaArgs`、核心入口。
- vanilla：`-jar server.jar nogui`。
- paper：根 `paper.jar` 优先，否则最大 `paper*.jar`，再 `nogui`。
- fabric：`-jar fabric-server.jar nogui`。
- neoforge：优先 `win_args.txt`，否则 `unix_args.txt`，不额外添加 `-jar/nogui`。
- Java 要求：1.21+ → 21；1.20.5+ → 21；1.20 → 17；1.18+ → 17；1.17 → 16；其它 1.x → 8；26.x → 25。
- 候选优先级：显式 javaPath、`MOFUMOFU_JAVA`、Yaml 保存列表、`JAVA_HOME/JDK_HOME`、runtime、PATH、平台注册表/常见目录。
- 有版本要求时：精确版本优先，再 preferred 且满足最低版本，再任意满足最低版本；同档按主版本升序。
- Go 仅 Linux：不迁 Windows 注册表；保留显式/env/JAVA_HOME/runtime/PATH/`/usr/lib/jvm` 扫描。Launcher Yaml 列表若无法无歧义定位，将记录为缩小范围，而不是猜路径。

### 5.2 Supervisor

| 主题 | C# 行为 | Go 计划 |
|---|---|---|
| 状态 | stopped/starting/running/stopping；协议将非 active 映射 offline | 内部增加 crashed 供 TUI；HTTP 默认仍映射 offline 以保持 C# |
| running 判定 | stdout/stderr 任意行包含大小写敏感 `Done (` | 相同 |
| stop | stdin `stop`，15 秒后 kill 进程树，再等 3 秒 | 相同 |
| kill | 不发送 stop，直接 kill 进程树 | 相同 |
| restart | intentional stop 后 start | 相同 |
| 退出 | 任何退出码最终 stopped；退出码只写 Core console | 相同 |
| autoRestart | 非 intentional exit；固定 5 秒；10 分钟内最多 3 次；exit 0 也重启 | 相同，不用指数退避改变现有行为 |
| PID | `.mofu-pid`；启动前按 PID 清 orphan；退出删除 | 相同；记录 PID 复用风险 |
| Agent 退出 | `ShutdownAllAsync(20s)`，超时 force stop | SIGTERM/SIGINT 相同；不会留下 Agent 拉起的服务器 |
| 日志 | Core ring 800 行；cursor 跨服务器重启不归零 | 相同 |
| 合成日志 | Java/orphan/退出/autoRestart 日志只进 Core ring | 默认相同，不写入 Agent WS journal |

### 5.3 Console / RCON

- RCON 配置来自 `server.properties`：`enable-rcon=true`、端口 1–65535、非空密码。
- managed 模式每次启动调用 `EnsureRcon`：保留非空密码，空则生成 24 位字母数字；非法端口回退 25575；强制启用 RCON。
- attach 模式**不调用 EnsureRcon，不修改 properties**。
- 命令优先 RCON，任何 RCON 异常回退 stdin；stdin 去掉尾部 CR/LF 后写一个 LF。
- RCON 只连 `127.0.0.1`；connect 5 秒、读写 8 秒、单包 4096 字节、正文 4080 字节、最多 128 个响应包、累计 1 MiB。
- 在线人数优先 RCON `list`；失败时 ticker 回退 stdin `list`，从 stdout 人数正则更新。
- Agent journal 与 Core ring 独立；Agent journal 只接收真实 stdout/stderr 行，统一 `level=INFO`、`source=minecraft`。

### 5.4 玩家

- 文件：`whitelist.json`、`ops.json`、`banned-players.json`。
- 文件损坏按空列表读取；直接保存不是原子写。
- API entries 按 Name ordinal 排序；revision 哈希 API DTO JSON，不是文件原文。
- offline UUID：MD5(`OfflinePlayer:`+name)，设置 version 3/variant 位，输出小写连字符 GUID。
- 运行中 mutation：`whitelist add/remove`、`op/deop`、`ban/pardon`；opLevel/expiresAt 不拼入运行中命令。
- 停止时直接写文件；starting/stopping 也视为 running，禁止直接写。
- Probe 返回真实 UUID、joinedAt、ping、gameMode；Probe 不可用回退只填 name，其余省略，不伪造。
- 可选玩家 diff：默认关闭；开启后 1–2 秒 RCON `list`，发 `player.joined/left`，joinedAt 由 Agent 记录，UUID 从 `usercache.json` 补。

### 5.5 安全文件路径

- 根：`<ServerRootDirectory>/<InstanceId>`。
- 拒绝 NUL/control、反斜杠、绝对路径、冒号、`//`、`.`、`..`、空段、尾部空格/点。
- Linux 大小写敏感；逐段 `Lstat` 拒绝 symlink/reparse point。
- 任意目录段拒绝 `auth/audit/backups/logs/.mofumofu-agent/world/world_*`。
- 拒绝保护文件名、二进制扩展名以及配置的 registry/journal/audit 文件。
- 文本 2 MiB、严格 UTF-8；文件 revision=`r_`+SHA256(content + little-endian modified UtcTicks)。
- 目录 revision=`d_`+SHA256(fingerprint)，最多 10000 节点；list 最多 1000 项。
- 写：同目录 `.mofu-*.tmp`、CreateNew、flush/fsync、recheck、rename 覆盖。
- 目录删除逐个进行，非事务化；中途失败可能留下部分删除。

### 5.6 Properties

- `server.properties` 只能由 properties API 访问；通用文件 API 拒绝。
- 读写均拒绝 `rcon.password`（大小写不敏感）；GET 响应删除该键。
- key：trim 后 1–128，无 control/空白/`=`/`\`/`/`。
- value：≤4096 UTF-16 code units，无 control。
- 已知 boolean/integer/enum key 做强校验；未知自定义 key 允许增删改。
- 已知 key 传 null → 400；未知 key 传 null → 删除。
- CRLF/CR 归一化为 LF；保留注释和无 `=` 行；重复 key 取最后值但写在首次位置；新 key ordinal 排序追加。
- `restartRequired` 进程内 bool；patch 成功置 true，start/restart 清 false。

### 5.7 备份

- 目录：`<InstanceDirectory>/backups`。
- 名称：本地时间 `hot-YYYYMMDD-HHmmss.zip` / `cold-YYYYMMdd-HHmmss.zip`；同秒追加 `-1` 等，最多 1000 次。
- zip entry 相对实例根、使用 `/`；排除顶层 `backups/logs`、`.tmp`、`.mofu-download`；只打包文件；累计源大小上限 8 GiB。
- 临时文件 `<name>.tmp`，完成后 rename。
- 热备份：`save-off` → `save-all flush` → 固定等 1500ms → zip → save-on 放 finally；save-off 失败只警告并继续。
- 冷备份不发 save 命令。
- retention：`<ServerRootDirectory>/backup-settings.json`；默认 disabled、interval 6h、keepCount 5、keepDays 0；每次创建后 prune，最新始终保留；0 表示关闭对应限制。
- Agent sidecar：`<zip>.agent.json`，≤4096 字节；label 回退为 zip basename。
- restore：必须停止；先创建冷安全备份；安全备份失败则中止；原地覆盖解压；不删除备份中不存在的文件；**无自动 rollback、无事务 journal、不自动启动服务器**。
- delete：不要求停止；删除 zip 和 sidecar。
- Go 必须能读取 C# Agent 生成的时间戳 zip；不兼容 Probe UUID zip 的恢复语义。

### 5.8 Confirmation / Operation / Audit

- confirmation ID：`cfm_`+32 随机字节 hex；60 秒；绑定 instance/tokenId/scope/action/revision/闭包。
- execute 同 token、instance、scope；锁内一次性消费；并发只有一个成功。
- 过期 410 `confirmation_expired`；重复 410 `confirmation_already_used`；不存在 404。
- tombstone 最多 4096，超限裁剪到 2048；不持久化。
- operation ID：`op_`+16 随机字节 hex；queued→running→succeeded/failed；succeeded progress=100。
- 无幂等键、无去重表、不持久化；confirm 返回快照，backup.create 同步等待。
- 应用停止取消 operation lifetime，等待当前 operation。
- 审计 JSONL：`timestamp, tokenId, action, parameters, result`；pending/succeeded/`failed:<code>`。
- pending 审计失败不执行副作用；成功后审计失败可能出现“副作用已发生但 HTTP 报 AUDIT_UNAVAILABLE”。
- token、RCON 密码、命令正文、properties 值不进日志/审计。

### 5.9 Event Journal / WebSocket

- JSONL 只持久化 `console/event/operation`；snapshot/reset 不持久化。
- cursor 从 1 开始的十进制 Int64 字符串；重启加载要求严格递增，不要求连续。
- 容量默认 4096（16–100000）；console/event/operation 共享容量和 cursor。
- 无 cursor：snapshot(cursor=latest)，recent console 固定 100 条。
- 有效 cursor：replay 所有更大 cursor 的 retained frames，不额外 snapshot。
- 非法、>latest、早于 earliest-1：reset + snapshot。
- `?cursor=` 是无效 cursor，不是“无 cursor”。
- snapshot 的 activeOperations 当前不按 scope 过滤；Go 保持兼容并记录安全风险。
- subscriber channel 满时移除并断开；WS 入站消息不执行命令。

### 5.10 Probe callback

- 路径固定 `/internal/probe/events`，只允许 loopback。
- headers：`X-Mofumofu-Instance-Id`、`X-Mofumofu-Timestamp`、`X-Mofumofu-Nonce`、`X-Mofumofu-Signature`。
- canonical：`METHOD\npath\ninstanceId\ntimestamp\nnonce\nSHA256(rawBody)`；HMAC key 是 registry.linkSecret 的 hex 解码字节。
- timestamp ±5 分钟；nonce 16–128 `[A-Za-z0-9_-]`，缓存 5 分钟。
- signature 接受裸 hex、`sha256=`、`hmac-sha256=`，前缀大小写不敏感。
- 新事件 202；重复 eventId 200；重复 nonce 401。
- 事件 type 不限制为已知枚举；details/key/value 有长度限制。

## 6. 配置映射

C# 配置保持兼容形状：

```json
{
  "ServerAgent": {
    "ServerRootDirectory": "/srv/mofumofu/servers",
    "InstanceId": "survival",
    "ProbeRegistryPath": "/var/lib/mofumofu/nekoprobe/survival/registry.json",
    "ProbeBaseUrl": "http://127.0.0.1:28765",
    "BindUrl": "http://127.0.0.1:28766",
    "EventJournalPath": "/var/lib/mofumofu/nekoprobe/survival/events.jsonl",
    "AuditPath": "/var/lib/mofumofu/nekoprobe/survival/audit.jsonl",
    "EventJournalCapacity": 4096
  }
}
```

| C# `AgentOptions` | Go config | 标准 env | 默认/校验 |
|---|---|---|---|
| `ServerRootDirectory` | `ServerRootDirectory` | `ServerAgent__ServerRootDirectory` | 必填绝对路径 |
| `InstanceId` | `InstanceId` | `ServerAgent__InstanceId` | trim；拒绝空、`.`、`..`、包含 `..`、`/`、`\`、NUL |
| `ProbeRegistryPath` | `ProbeRegistryPath` | `ServerAgent__ProbeRegistryPath` | 必填文件路径 |
| `ProbeBaseUrl` | `ProbeBaseUrl` | `ServerAgent__ProbeBaseUrl` | `http://127.0.0.1:17871`；必须 loopback HTTP(S) |
| `BindUrl` | `BindUrl` | `ServerAgent__BindUrl` | `http://127.0.0.1:17870`；实际只接受 IPv4 `127.0.0.1` |
| `EventJournalPath` | `EventJournalPath` | `ServerAgent__EventJournalPath` | 必填文件路径 |
| `AuditPath` | `AuditPath` | `ServerAgent__AuditPath` | 必填文件路径 |
| `EventJournalCapacity` | `EventJournalCapacity` | `ServerAgent__EventJournalCapacity` | 4096；16–100000 |

Go 扩展配置（不改变现有 JSON 字段）：

| Go 扩展 | env | 默认 | 用途 |
|---|---|---|---|
| `Mode` | `GoAgent__Mode` | `managed` | `managed`/`attach` |
| `ProbeEnabled` | `GoAgent__ProbeEnabled` | `true` | 是否允许在线详情/token 转发；false 时 capabilities 不广告 tokens |
| `PlayerDiffInterval` | `GoAgent__PlayerDiffInterval` | `0` | 可选玩家 diff 秒数；0 关闭以保持 C# 默认 |
| `InstallRootDirectory` | `GoAgent__InstallRootDirectory` | 空（禁用安装） | 远程安装的唯一父目录，实例写入 `<根>/<InstanceId>` |
| `InstallAllowedCores` | `GoAgent__InstallAllowedCores`（逗号分隔） | `[]`（全部拒绝） | 允许的 core：`vanilla`/`paper`/`fabric`/`neoforge` |
| `InstallAllowedMinecraftVersions` | `GoAgent__InstallAllowedMinecraftVersions`（逗号分隔） | `[]`（全部拒绝） | 精确版本，或 `<前缀>.*`（如 `1.21.*` 匹配 `1.21` 与 `1.21.x`，不匹配 `1.210`）；不接受裸 `*` |
| `InstallJavaPath` | `GoAgent__InstallJavaPath` | 空（`JAVA_HOME/bin/java` 或 `java`） | 写入启动脚本并用于 NeoForge 安装器；远程请求不能指定 |

优先级：默认值 < JSON < env < 命令行。支持 `--config`/`--config=`、`--urls`/`--urls=`；新增 `--tui`、`--no-tui`、`--version`。

## 7. 兼容风险与已记录偏差

| 主题 | 处理 |
|---|---|
| handshake capabilities | C# 静态返回 10 项；Go 按任务要求动态返回实际能力，保留字符串顺序规则，不把 requested capabilities 当协商结果 |
| properties 白名单 | prompt 与仓库冲突；按 C# 允许未知自定义 key，只强校验已知 key |
| crashed 状态 | C# HTTP 不返回 crashed；Go 内部/TUI 可显示 crashed，HTTP 默认 offline，保持 Launcher/C# 行为 |
| 玩家 join/left | C# 无 diff；Go 默认关闭，可选开启，开启后才产生事件 |
| autoRestart 退避 | C# 固定 5 秒、10 分钟 3 次；Go 保持相同，不引入指数退避改变重启时序 |
| backup label | Launcher 当前发空 label，C# Agent 拒绝；Go 保留拒绝并记录为上游兼容阻塞，TUI 会发送明确 label |
| restore rollback | C# Core 无自动 rollback；Go 保持安全备份+覆盖解压，不伪造事务回滚 |
| 路径 TOCTOU/hardlink | C# 有检查与使用间竞态且不识别 hardlink；Go 保持 symlink/traversal 防护并记录剩余风险 |
| WS scope | C# snapshot 暴露所有 active operation；Go 保持兼容并记录，Operation GET 仍校验 scope |
| registry 差异 | Agent reader 与 Probe loader 规则不同；Go 认证按 Agent reader，token 写入仍归 Probe |
| API minor | Agent 接受 major 1，Probe 精确 1.0；Go 保持两层行为 |
| Java 扫描 | Linux 不迁 Windows 注册表；Yaml/runtime 无法定位时优先环境/PATH/系统 JVM，并记录差异 |
| PID orphan | C# 仅凭 PID kill；Go 记录相同 PID 复用风险，不假装具备进程身份验证 |
| Linux CPU | Go 读取 `/proc/<pid>/stat`，按 Linux 常规 100 ticks/秒换算；无法读取时省略 CPU，不伪造 |
| Probe timeout | Go 的 5 秒 HTTP timeout 覆盖响应体读取；.NET `ResponseHeadersRead` 只约束到响应头，Go 对慢响应体更严格 |
| 备份 symlink | C# Core 未显式跳过 symlink；Go 备份跳过 symlink，恢复拒绝 symlink 目标，避免越界读取/写入 |
| 文件权限 | Go 临时写文件使用 0600；最终文本文件权限可能比 C# 默认 umask 更严格 |
| Operation Stop | Go 在 Stop 后拒绝新 operation；C# 会接受但立即使用已取消 lifetime。仅影响 Agent 退出窗口 |
| Confirmation tombstone | Go 在超过 4096 时立即裁剪到 2048；C# 在下一次 prune 时裁剪。极端重放窗口有差异 |
| JSON 对象顺序 | Go map 序列化使用确定性排序，C# Dictionary 保留插入顺序；字段名/值/null 不变，契约比较应 normalize 对象顺序 |
| Console journal 队列 | Go 使用有界无关的无等待入队循环，避免磁盘卡顿阻塞 Java stdout；事件/operation 仍等待持久化。极端磁盘故障会积压内存并记录错误 |
| attach | 新增 Go 模式；protocol `deploymentMode` 仍用现有 `agent`，不引入新枚举；动态移除 control，properties 只读配置不改写 |
| 远程安装（扩展） | C# 无此能力，Go 新增 `/install/prepare` 并复用 `control` scope（见 3.2）。handshake capabilities 不新增 `install` 项，客户端通过 prepare 的 `capability_not_supported` 判断是否可用 |
| 安装版本元数据 | Go 不缓存版本列表（C# 缓存 6 小时/30 分钟）；每次安装都直接请求上游 |
| Paper 下载选择 | C# 取 JSON 中第一个带 URL 的 download；Go 优先 `server:default`，否则按键名排序取第一个，保证确定性 |
| Paper/Fabric/NeoForge 校验 | 上游未提供 sha1 时只做 https、公网地址与 512 MiB 上限校验；C# 行为相同，Go 未额外校验 Paper 的 sha256 checksum |
| 下载 HEAD 探测 | C# 先发 HEAD 取 Content-Length；Go 直接 GET 并使用响应头与流式计数，减少一次上游请求 |
| server.json | 安装后写入的 `coreVersion` 为解析后的实际版本（C# 写入用户请求值）；`javaPath` 仅记录配置值。`memoryMB` 等字段保持 0，与 C# 创建流程一致 |
| maxPlayers 与 port | C# 不限制 maxPlayers；Go 远程请求限制为 1–1000，port 必须在 1–65535 |
| 错误文本 | 安装错误使用英文，与 Go Agent 其他错误一致；错误与日志不包含下载 URL 或查询串 |
| NeoForge 运行 | 平台无关使用 `exec.CommandContext`，10 分钟超时；超时只终止直接子进程，不处理其孙进程 |
| TPS | 当前协议没有 TPS capability token；Go 不发明握手能力。Paper/Spigot 可尝试 RCON `tps`，失败/其他平台 `tps=null` |
| TUI 权限 | TUI 仅本地，使用 `local-tui` 审计主体；仍走同一 service、confirmation、operation、audit，不开放网络监听 |
| 文本编码/转义 | Go 必须补回 STJ 日期 offset、null 省略、WS type 首字段、字典 null 值等语义，不能直接用零值 struct 猜 |
| 当前 Release | Recon 时 GitHub latest `v0.3.1` 尚无 remote Agent/Probe 资产；新 Go 资产随后续 tag 发布，安装前必须校验资产存在 |
| 本地运行环境 | 当前 Windows 无 WSL；可做 Go 单测与 Linux 交叉构建，Linux fake-Java/systemd 实测需 CI/Linux 主机 |
| .NET 测试 | `dotnet test` ServerAgent 在当前 Windows 命名管道权限失败；直接运行测试 exe 为 19 通过、1 跳过 |

## 8. 分阶段计划

| Phase | 内容 | 当前状态 |
|---|---|---|
| 0 | Recon、兼容矩阵、配置/风险/计划 | **完成** |
| 1 | Go module、config、HTTP skeleton、auth、handshake/status、slog、优雅退出 | **完成** |
| 2 | Supervisor、console、RCON、commands、状态事件 | **完成**：fake Java、RCON mock、stop/kill/restart/autoRestart、audit command、state monitor 已接入 |
| 3 | Players、files、properties、revision/UUID | **完成**：HTTP/audit/revision/UUID/安全文件/properties 已接入 |
| 4 | Backups、operations、confirmations、restore | **完成**：控制、文件删除、属性 patch、备份 create/restore/delete 已接入 |
| 5 | WebSocket、journal、probe callback、NekoProbe fallback | **完成**：coder/websocket、JSONL replay/reset、HMAC/nonce、Probe 在线详情与 token 转发已接入 |
| 6 | Bubble Tea/Lip Gloss TUI + teatest | **完成**：80×24/120×40、危险确认、过滤、命令面板、终端恢复 |
| 7 | attach、平台识别、capabilities、systemd/install/release、文档 | **完成**：attach/TPS/install/release/systemd/部署文档已接入 |
| 8 | contract/integration、性能/安全收尾、cutover | **本地完成**：cross-build、契约/集成、smoke、文档；Linux race/真实 Launcher/SSH 待外部环境 |

每个实现 Phase 结束必须执行：

```powershell
cd Mofumofu.ServerAgent
gofmt -l .
go test ./...
go vet ./...
```

Linux 构建：

```bash
CGO_ENABLED=0 GOOS=linux GOARCH=amd64 \
  go build -trimpath -ldflags "-s -w -X main.version=dev" ./cmd/mofumofu-agent
CGO_ENABLED=0 GOOS=linux GOARCH=arm64 \
  go build -trimpath -ldflags "-s -w -X main.version=dev" ./cmd/mofumofu-agent
```

.NET 回归：

```bash
dotnet test tests/Mofumofu.RemoteProtocol.Tests/Mofumofu.RemoteProtocol.Tests.csproj -c Release
dotnet test tests/Mofumofu.Launcher.Tests/Mofumofu.Launcher.Tests.csproj -c Release
```

C# `Mofumofu.ServerAgent` 已移除；其行为基准保留在 git 历史中（迁移前的最后一次提交）。

## 9. Cutover / Rollback 草案

### Cutover

1. 在独立复制的实例上并行验收 Go，不直接让两个 Agent 同时写同一个 `server.json`、`.mofu-pid`、journal 或 audit。
2. 保留原 .NET binary/unit/config；记录当前 Caddy backend port、release tag、registry generation。
3. 做冷备份并记录 SHA-256；停止 .NET Agent，确认其拉起的服务器已按 C# 语义停止。
4. 启动 Go Agent，先验证 loopback `/status`，再切 Caddy backend。
5. 用现有 Launcher 跑 handshake/status/console/players/files/properties/backups/ws。
6. 观察 audit、journal、autoRestart、Probe callback 至少一个完整备份/恢复周期。
7. 再切换 systemd 默认单元；保留 .NET 单元但禁用。

### Rollback

1. 切回 Caddy 到 .NET 端口。
2. 停止 Go Agent；确认服务器停止或按运维决策单独处理。
3. 恢复原 systemd unit/config/binary，`daemon-reload` 后启动。
4. 不删除 Go journal/audit；它们可能包含 cutover 期间事件。
5. 若发生文件/属性/备份副作用，使用 restore 前冷安全备份或备份 zip 恢复；confirmation/operation 不跨进程恢复。

## 10. 验证记录与 Definition of Done

- `go version`：Go 1.27.4（module 计划最低 1.24）。
- Bubble Tea 最新可用 v1.3.10；Lip Gloss v1.1.0；coder/websocket v1.8.15。按约束使用 v1。
- RemoteProtocol：47 项通过。
- Launcher：5 项通过。
- ServerAgent：直接运行测试 exe，19 通过、1 因 Windows 无符号链接权限跳过。
- Go Phase 1：`go test ./...`、`go vet ./...` 通过。
- Linux 交叉构建：`CGO_ENABLED=0`、`GOARCH=amd64/arm64` 均通过。
- Phase 2 fake Java 集成测试覆盖启动/Done、console、stdin 命令、stop 超时 kill、restart、autoRestart 上限；RCON mock 验证优先级与失败回退。
- Phase 3–4 测试覆盖玩家 UUID/revision/mutation、文件/properties、confirmation 重放、备份 create/restore、control operation。
- Phase 5 测试覆盖 WS snapshot/replay/reset/operation cursor、Probe HMAC/重复 event/nonce 重放/loopback。
- attach 测试覆盖 log tail、只读 properties、生命周期降级；Paper/Spigot TPS 仅在 RCON 可解析时返回。
- TUI teatest 覆盖 80×24/120×40、pane/过滤/resize、危险确认、退出、ANSI/§ 清理和目录删除 revision。
- Windows 真实进程 smoke：loopback `/status` 就绪约 790ms（不含 JVM），结构化日志未输出 token/密码。
- 当前 Windows 的 `CGO_ENABLED=0` 环境不能运行 `go test -race`；race 留给 Linux CI。
- 本机 Gradle Wrapper 下载被 Java PKIX 证书链校验阻断；NekoProbe 测试需在 CI/证书完整的 Linux 环境运行。
- 全部 27 个业务路由和 internal callback 均已实现，无 `capability_not_supported` 占位端点；不支持的能力只按实际后端动态省略。
- Recon 与 Phase 1 未修改任何现有 C#/Java/协议业务文件。
- Go 代码规模：105 个 `.go` 文件、约 22,338 行，其中测试约 8,665 行；生产代码约 13,673 行。
- 静态二进制大小：linux-amd64 约 9.98 MB，linux-arm64 约 9.24 MB（`-s -w`）。

### 验收清单

- [x] Linux amd64/arm64、CGO 关闭、单二进制构建。
- [x] 全部 HTTP/WS/internal 路由、golden JSON、错误码、scope、rate limit、audit、confirmation、operation。
- [x] fake Java、RCON mock、备份/恢复、玩家 revision/UUID、WS 帧与 Probe HMAC 集成测试。
- [x] TUI 80×24/120×40 teatest、危险操作确认、headless 保护。
- [x] token/密码日志脱敏、RCON loopback、路径/symlink/请求体限制测试。
- [x] `MIGRATION.md`、`TUI.md`、`DEPLOYMENT.md`、systemd/install/release Go 分支。
- [x] cutover 与 rollback 步骤。
- [ ] 在真实 Linux VPS 上用现有 Launcher 完成端到端验收（本机无 WSL/Docker）。
- [ ] 在 Linux CI 运行 `go test -race ./...`（本机无 CGO 编译器）。
- [ ] 在真实 SSH 80×24 终端做 TUI 人工验收（已有 teatest）。
- [ ] 在证书完整的 Linux 环境运行 NekoProbe Gradle 测试（本机 PKIX 阻断）。

### 已知阻塞

- 现有 Launcher 的 `CreateBackupAsync` 发送空 label，C# Agent 与 Go Agent 都按现有契约拒绝。迁移没有静默修改这一行为；需要在单独的上游变更中修复 Launcher 或协议默认值。
- 当前最新发布 `v0.3.1` 不含 remote Agent/Probe 资产；必须使用包含新 Go 资产的 tag。
