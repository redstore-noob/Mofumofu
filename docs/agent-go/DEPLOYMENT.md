# Go Agent 部署与升级

## 1. 安装

发布产物：

- `mofumofu-agent-linux-amd64`
- `mofumofu-agent-linux-arm64`
- 每个二进制对应的 `.sha256`

二进制为 `CGO_ENABLED=0` 的 Linux 静态构建，不依赖 .NET runtime。

默认安装器仍选择 .NET；Go 分支必须显式开启：

```bash
sudo ./install.sh \
  --mode agent \
  --domain mc.example.com \
  --instance survival \
  --release vX.Y.Z \
  --agent-variant go
```

等价环境变量：

```bash
sudo MOFU_AGENT_VARIANT=go ./install.sh \
  --mode agent --domain mc.example.com --instance survival --release vX.Y.Z
```

安装器保留现有路径与实例语义：

| 项目 | 路径 |
|---|---|
| Server root | `/srv/mofumofu/servers` |
| Instance | `/srv/mofumofu/servers/<instance>` |
| Go binary | `/opt/mofumofu-agent/mofumofu-agent` |
| Config | `/etc/mofumofu/<instance>.json` |
| Unit | `/etc/systemd/system/mofumofu-agent-<instance>.service` |
| Registry/journal/audit | `/var/lib/mofumofu/nekoprobe/<instance>/` |
| Caddy snippet | `/etc/caddy/Caddyfile.d/mofumofu-<instance>.caddy` |

Go unit 显式带 `--no-tui`。Caddy 仍只反代 `127.0.0.1:<agent-port>`，`/internal/*` 仍直接 404。

## 2. 手动运行

```bash
/opt/mofumofu-agent/mofumofu-agent \
  --config /etc/mofumofu/survival.json \
  --urls http://127.0.0.1:28766 \
  --no-tui
```

本地 SSH 终端可显式启用 TUI：

```bash
/opt/mofumofu-agent/mofumofu-agent \
  --config /etc/mofumofu/survival.json \
  --tui
```

默认只在 stdin/stdout 都是 TTY 时启用 TUI；systemd、管道、日志重定向环境不会进入 TUI。TUI 不监听网络，也不建立第二个 HTTP 客户端。

## 3. systemd

安装器会生成 unit。手工模板见：

`deploy/systemd/mofumofu-agent-go.service.example`

多实例必须修改模板中的 Agent/Probe 端口、域名和 callback；模板中的端口只是单实例示例。Agent 退出时发送 SIGTERM/SIGINT，按 C# 现状停止由它拉起的服务器。

## 4. 日志与状态

```bash
journalctl -u mofumofu-agent-survival -f
systemctl status mofumofu-agent-survival
curl -H 'Authorization: Bearer <token>' \
  'http://127.0.0.1:28766/v1/servers/survival/status?apiVersion=1.0'
```

- Agent 结构化日志：stderr/journald。
- Minecraft console：实例 `logs/latest.log`。
- Durable WS journal：`<state>/events.jsonl`。
- Audit：`<state>/audit.jsonl`。
- Token registry：`<state>/registry.json`，由 NekoProbe 拥有。

不要把 Bearer token、RCON password、pairing URI 或 properties 值贴进日志/issue。

## 5. 升级

1. 记录当前 release、binary SHA-256、registry generation。
2. 用新 release 重跑安装器并选择 `--agent-variant go`。
3. 安装器默认不会重启已经 active 的 unit；手工执行：

```bash
sudo systemctl restart mofumofu-agent-survival
```

4. NekoProbe jar 更新需要重启 Minecraft 才生效。
5. 验证 handshake/status/ws，再观察至少一次 control 和 backup。

安装器没有自动回滚。升级前保留原 binary/unit/config，并做冷备份。

## 6. Cutover

1. 在复制的实例上并行验收 Go；不要同时运行 .NET Agent 与 Go Agent 写同一个 `server.json`、`.mofu-pid`、journal 或 audit。
2. 停止 .NET Agent，确认它拉起的服务器已停止。
3. 启动 Go Agent，先直连 loopback 验证，再切 Caddy backend。
4. 用现有 Launcher 验证 handshake/status/console/players/files/properties/backups/ws。
5. 观察 audit、journal、Probe callback 和一次完整备份/恢复周期。

## 7. Rollback

1. Caddy backend 切回 .NET Agent 端口。
2. 停止 Go Agent。
3. 恢复原 .NET unit/config/binary 并启动。
4. 保留 Go 的 journal/audit供排查，不要删除。
5. 文件或属性副作用使用冷安全备份/zip 恢复；confirmation 和 operation 不跨进程恢复。

## 8. 已知部署风险

- Recon 时 GitHub latest `v0.3.1` 尚无 remote Agent/Probe 资产；必须使用包含 Go 资产的新 tag。
- 安装器 Caddy body 上限为 3 MB，低于 Agent 的 4 MiB。
- 当前 Launcher 创建备份发送空 label，而 C# Agent 要求非空；Go 保持 C# 校验。这是已知上下游兼容阻塞，详见 `MIGRATION.md`。
- attach 模式只使用 RCON + `logs/latest.log`，不提供生命周期控制，也不修改 `server.properties`。
