# Mofumofu Go Agent TUI

TUI 使用 Bubble Tea v1 + Lip Gloss，暗色、低对比边框、单一青色强调。它直接调用与 HTTP 相同的 service 层，不监听网络，也不绕过 audit、confirmation 或 operation。

## 启动

```bash
mofumofu-agent --config /etc/mofumofu/survival.json --tui
```

- `--tui`：显式请求；stdin/stdout 非 TTY 时仍保持 headless。
- `--no-tui`：强制关闭。
- 默认：stdin/stdout 都是 TTY 时启用。
- systemd、管道、日志重定向、CI 环境不会进入 TUI。
- TUI 中 `q` 确认后退出整个 Agent；初始化/运行错误只结束 TUI，Agent HTTP 继续运行。

## 布局

```text
┌ mofumofu agent · survival · ● running · 00:12:34 · CPU 3.2% · MEM 512M ┐
│ Console                                      │ Players  2/20         │
│ 12:00:01 [Server/INFO] Done (0.5s)            │ Alice                  │
│ 12:00:04 [Server/INFO] Alice joined           │ Bob                    │
│ 12:00:08 [Server/WARN] Can't keep up          │                       │
│                                              │ Operations              │
│                                              │ backup.create running   │
│                                              │                       │
│                                              │ Backups                 │
│                                              │ cold-20261126-...       │
│                                              │                       │
│                                              │ Agent                   │
│                                              │ bind 127.0.0.1:28766    │
│                                              │ probe yes · tokens 3    │
└──────────────────────────────────────────────┴────────────────────────┘
 tab pane · / filter · : cmd · s start · x stop · r restart · b backup · ? help · q quit
```

- 120×40 显示侧栏；80×24 自动隐藏侧栏，保留主内容和底部提示。
- resize 会重新布局，不崩溃。
- running 绿、starting 黄、stopped 灰、crashed 红。
- 日志 info 灰、warning 黄、error 红、chat 绿；ANSI 与 `§` 颜色码会被去除。

## 按键

| 按键 | 行为 |
|---|---|
| `q` | 确认退出 Agent |
| `?` | 帮助 modal |
| `tab` / `shift-tab` | 切换 Console/Players/Operations/Backups/Files/Properties |
| `j` / `k` | 向下/向上滚动 |
| `g` / `G` | 顶部/底部 |
| `pgup` / `pgdn` | 翻页 |
| `/` | Console 过滤输入 |
| `:` | 命令面板 |
| `esc` | 关闭 modal/输入 |
| `s` | prepare + execute start |
| `x` | 确认后 prepare + execute stop |
| `r` | 确认后 prepare + execute restart |
| `b` | 创建备份 |
| `p` / `f` / `c` | Players/Files/Properties pane |

危险操作始终先显示 modal，`y` 或 `enter` 才执行；`esc`/`n` 取消。

## 命令面板

```text
:start
:stop
:restart
:kill
:backup [label]
:restore <backup-id>
:delete-backup <backup-id>
:delete-file <path>
:set <property-key> <value>
:op <player>
:deop <player>
:whitelist add|remove <player>
:ban <player> [reason]
:pardon <player>
:help
:quit
```

所有控制、恢复、删除和 properties patch 都通过 prepare → confirmation execute；备份创建通过 `backup.create` operation。文件/目录删除会先读取精确 revision，再准备 confirmation。TUI 的本地审计主体为 `local-tui`，不会被暴露到网络。

Files 面板目前支持目录浏览和删除，不包含文件内容编辑器。

## 测试

TUI 包使用 teatest 验证 80×24、120×40、pane 切换、过滤、危险操作确认和退出。测试固定时钟/数据，避免把动态时间写进 golden。
