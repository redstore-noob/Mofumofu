# Mofumofu Go ServerAgent

Linux `amd64`/`arm64` 单二进制 ServerAgent，兼容现有 Launcher 的 HTTP/WS JSON 契约。

## Build

```bash
go test ./...
go vet ./...
CGO_ENABLED=0 GOOS=linux GOARCH=amd64 \
  go build -trimpath -ldflags "-s -w -X main.version=dev" ./cmd/mofumofu-agent
CGO_ENABLED=0 GOOS=linux GOARCH=arm64 \
  go build -trimpath -ldflags "-s -w -X main.version=dev" ./cmd/mofumofu-agent
```

## Run

```bash
./mofumofu-agent --config /etc/mofumofu/survival.json --no-tui
```

配置形状与 .NET Agent 相同：

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
  },
  "GoAgent": {
    "Mode": "managed",
    "ProbeEnabled": true,
    "PlayerDiffInterval": 0
  }
}
```

- `Mode=managed`：Agent 拥有 Java 进程生命周期。
- `Mode=attach`：只观察 `logs/latest.log` 与 loopback RCON，不启停服务器、不修改 properties。
- `PlayerDiffInterval>0`：可选开启 RCON 玩家名单 diff；默认关闭以保持 C# 行为。

详见：

- `../docs/agent-go/MIGRATION.md`
- `../docs/agent-go/DEPLOYMENT.md`
- `../docs/agent-go/TUI.md`
