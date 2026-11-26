# Mofumofu ServerAgent → Go 迁移文档

> 状态：**Phase 1 / Skeleton 完成；Phase 2–4 基础包进行中**
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

### 3.1 Token 转发错误

- Probe 网络不可用、5 秒超时、响应 >4 MiB：Agent 返回 409 `server_offline`。
- Probe 非 200：透传 HTTP status、`code`、`message`、`details`，丢弃 Probe 的 requestId/operationId。
- Probe 响应无法解析：500 `internal_error`。
- registry 持久化失败可透传 503 `registry_unavailable`。

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
| attach | 新增 Go 模式；protocol `deploymentMode` 仍用现有 `agent`，不引入新枚举；动态移除 control，properties 只读配置不改写 |
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
| 2 | Supervisor、console、RCON、commands、状态事件 | 进行中：Java 解析与 RCON 已完成，Supervisor/HTTP 待接入 |
| 3 | Players、files、properties、revision/UUID | 进行中：安全文件与 properties 已完成，Players/HTTP 待接入 |
| 4 | Backups、operations、confirmations、restore | 进行中：备份 zip/retention/restore 基础已完成，Operations/Confirmation/HTTP 待接入 |
| 5 | WebSocket、journal、probe callback、NekoProbe fallback | 计划 |
| 6 | Bubble Tea/Lip Gloss TUI + teatest | 计划 |
| 7 | attach、平台识别、capabilities、systemd/install/release、文档 | 计划 |
| 8 | contract/integration、性能/安全收尾、cutover | 计划 |

每个实现 Phase 结束必须执行：

```powershell
cd agent-go
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

当前 Windows 的 ServerAgent 测试替代命令：

```powershell
.\tests\Mofumofu.ServerAgent.Tests\bin\Release\net10.0\Mofumofu.ServerAgent.Tests.exe
```

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

## 10. Phase 0 验证记录

- `go version`：Go 1.27.4（module 计划最低 1.24）。
- Bubble Tea 最新可用 v1.3.10；Lip Gloss v1.1.0；coder/websocket v1.8.15。按约束使用 v1。
- RemoteProtocol：47 项通过。
- Launcher：5 项通过。
- ServerAgent：直接运行测试 exe，19 通过、1 因 Windows 无符号链接权限跳过。
- Go Phase 1：`go test ./...`、`go vet ./...` 通过。
- Linux 交叉构建：`CGO_ENABLED=0`、`GOARCH=amd64/arm64` 均通过。
- Phase 1 已注册全部 27 个业务路由和 internal callback；handshake/status 为真实实现，其余明确返回 `capability_not_supported`，未留 TODO。
- Recon 与 Phase 1 未修改任何现有 C#/Java/协议业务文件。
