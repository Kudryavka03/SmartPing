# SmartPing

运行在 Windows 上的网络质量监测工具，采用 .NET 8、SQLite 和内置网页面板。一个程序既可以作为监测中心，也可以作为远程探测 Agent。无需 Node.js、外部数据库或前端构建工具。

## 功能

- **ICMP Ping**：配置目标、探测间隔和 ICMP 数据载荷大小；固定 2 秒超时。
- **TCP Ping**：测量 TCP 连接建立耗时，设置端口、疑似重传阈值和连接超时。
- **网页管理**：快速添加、编辑、暂停、删除项目，按分组、协议和节点筛选。
- **历史可视化**：延迟曲线、最小/最大延迟区间、丢包率、疑似重传率、抖动和最近探测记录，支持切换时间范围。
- **多节点**：每个项目分配给一个或多个节点；选择多个节点，一次创建双向互测链路。
- **路由追踪**：在网页选择执行节点并发起一次性 traceroute，`ntrace.exe` 支持 ICMP、TCP、可选包大小与默认 GeoIP 信息；基础 ICMP 可回退 Windows `tracert.exe`，逐步显示原始输出并支持取消。
- **持久化**：项目、节点和历史样本保存到 SQLite，默认保留 30 天历史。Agent 短暂断线时使用本地持久化队列。
- **Windows 部署**：源码运行、包含 .NET 运行时的发布包，以及 Windows 服务安装脚本。

初次启动没有预置项目，不会主动探测任何外部地址。

## 本机快速开始

安装 **.NET 8 SDK**，在项目根目录运行：

```powershell
.\scripts\run.ps1
```

打开 <http://127.0.0.1:5080>，点击「添加探测项目」。例如选择 ICMP，填写路由器 IP，载荷 32 bytes、间隔 5 秒，并选中本地节点。选择 TCP 时再填写端口、疑似重传阈值和超时。

本机模式默认只监听回环地址，无需令牌。按 `Ctrl+C` 停止；再次运行会恢复项目和历史。脚本的数据目录默认是项目根目录下的 `data/server`。

```powershell
# 自定义监听端口、节点显示名、数据目录与保留时间
.\scripts\run.ps1 -Port 5088 -NodeName '办公室' `
  -DataDirectory 'D:\SmartPingData' -RetentionDays 60
```

## 多机探测与互测

监测中心保存配置与历史，同时也是一个本地探测节点。远程 Agent 主动连接监测中心，获取分配给自己的项目，再上报结果。Agent 不需要开放管理面板端口。

```text
浏览器 ──HTTP──> 监测中心 A（面板 / SQLite / 本地探测）
                   ↑                   ↑
             注册、取配置、上报    注册、取配置、上报
                   │                   │
                 Agent B             Agent C

每个节点可独立探测多个目标；互测时由 A、B、C 分别探测其他节点的地址。
```

以下使用文档保留地址：监测中心为 `192.0.2.10`，两个 Agent 为 `192.0.2.11` 和 `192.0.2.12`。部署时替换为实际可达的地址；这些示例地址不用于公网探测。

1. 在监测中心生成并保管两个不同的随机令牌，然后启动：

   ```powershell
   $adminToken = [Guid]::NewGuid().ToString('N')
   $agentToken = [Guid]::NewGuid().ToString('N')
   .\scripts\run.ps1 -ListenAddress '0.0.0.0' `
     -NodeName '监测中心 A' -NodeAddress '192.0.2.10' `
     -AdminToken $adminToken -AgentToken $agentToken
   ```

   外部监听必须同时设置 `AdminToken` 与 `AgentToken`，每个至少 16 个字符。浏览器访问 `http://192.0.2.10:5080` 后填写 `$adminToken` 的值；Agent 使用 `$agentToken` 的值。重新启动时继续使用原来的令牌。

2. 将源码和 .NET 8 SDK 放到远程 Windows 主机，分别运行：

   ```powershell
   # 在 B 主机运行；将令牌替换成中心配置的 AgentToken
   .\scripts\agent.ps1 -ServerUrl 'http://192.0.2.10:5080' `
     -Token '<中心的 AgentToken>' -Name 'Agent B' -Address '192.0.2.11'

   # 在 C 主机运行
   .\scripts\agent.ps1 -ServerUrl 'http://192.0.2.10:5080' `
     -Token '<中心的 AgentToken>' -Name 'Agent C' -Address '192.0.2.12'
   ```

   也可以给 Agent 部署下面介绍的独立发布包，无需安装 SDK。

3. 等待约 10 秒，在「探测节点」中查看在线节点。在项目表单中多选执行节点，即可从多台机器探测同一目标。
4. 点击「创建节点互测」，至少选择两个节点。每个节点都必须填写其他节点能够访问的 `NodeAddress`；不能使用 `127.0.0.1` 代替远程节点地址。地址各不相同时，N 个节点建立 N × (N − 1) 条有向链路；相同来源节点与目标地址的组合会去重。

中心所在机器需要允许来自浏览器和 Agent 的 TCP 5080 入站连接；Agent 需要能出站访问中心。ICMP 互测还需要目标允许 ICMP Echo 请求。TCP 互测要求每个目标节点的所选端口确实有服务监听；SmartPing 不会自动创建一个 TCP 测试监听器。

### 节点身份与断线行为

- Agent 默认使用 `data/agent` 保存身份和待上报队列；保留目录即可在重启后保持同一节点身份。也可显式指定 `-NodeId 'branch-b'`。
- 同一台主机上运行多个 Agent 时，分别配置不同的 `-DataDirectory` 和节点身份，避免共享数据库。
- Agent 约每 10 秒同步注册、配置和结果。短时断线可以继续探测；最后一次成功拉取配置后超过 60 秒仍失联，会停止继续探测，重连后恢复。
- 未上报样本写入 `agent-outbox/<节点身份哈希>/agent-outbox.db`，队列最多保留 10,000 条，超过上限时丢弃最旧记录。中心按样本 ID 去重。
- 删除远程节点前先停止对应 Agent，否则仍在运行的 Agent 会重新注册。删除节点会移除它在项目中的分配；没有执行节点的项目会暂停。

## 参数与指标含义

| 参数 | ICMP | TCP |
| --- | --- | --- |
| 主机 | IP 地址或纯主机名 | IP 地址或纯主机名 |
| 端口 | 不适用 | 1–65535，默认 443 |
| 载荷大小 | 0–65500 bytes，默认 32 | 不适用 |
| 探测间隔 | 1–3600 秒，默认 5 | 1–3600 秒，默认 5 |
| 超时 | 固定 2000 ms | 100–60000 ms，默认 2000 |
| 疑似重传阈值 | 不适用 | 大于 0 且小于超时，默认 300 ms |

载荷大小是 ICMP 数据部分，不含 IP/ICMP 报头。主机栏不接受 URL、端口或路径；例如填写 `example.com`，TCP 端口另填 `443`。

- **延迟**：ICMP 是 Echo 往返时间；TCP 是连接建立耗时。连接被拒绝、DNS 失败、不可达和超时均属于失败。
- **丢包率**：失败样本数 ÷ 已完成探测样本总数。对于 TCP，表示连接探测失败率。Agent 没有上报时属于数据缺失，不会凭空补成丢包样本。
- **疑似重传**：只有 TCP **成功连接且耗时大于阈值**时才标记。超时记为失败，不再记一次疑似重传。疑似重传率以全部已完成样本为分母。此指标是用户指定的延迟启发式规则，**并非抓包确认的 TCP 重传次数**。
- **平均/最小/最大延迟**：只使用成功样本。图表按时间桶聚合，保留每个时间桶的平均值、极值与失败比例，避免长时间范围返回全部样本。
- **抖动**：同一执行节点相邻且均成功的探测，其延迟差绝对值的平均数。
- 同一项目分配给多个节点时，可以合并查看，也可以按执行节点单独查看；状态与统计会随所选时间范围变化。

删除项目会同时删除其历史样本。暂停保留配置与已有历史。

## 路由追踪（traceroute）

网页中的路由追踪可以指定本机或在线 Agent 执行。目标可填写 IPv4、IPv6 或 ASCII 域名；国际化域名先转换成 Punycode。域名允许英文字母、数字、连字符和点；IPv6 允许十六进制数字、冒号及 IPv4 映射地址中的点。主机字段拒绝空格、制表符、换行、控制字符、Unicode 字符、带作用域 ID 的 IPv6、URL、端口、路径、引号和命令分隔符等输入，不会自动裁剪或拆分目标。

Windows x64 发布包随附 NextTrace 1.7.4（`ntrace.exe`）及 WinDivert 2.2.2-A 运行文件，位于后端的 `tools/nexttrace/win-x64` 子目录。迁移时复制整个发布目录即可调用，无需在目标机器安装 WinGet 或配置 `PATH`。第三方来源、文件校验与许可证见 [文件清单](src/SmartPing/tools/nexttrace/manifest.json) 和 [第三方声明](src/SmartPing/tools/nexttrace/NOTICE.md)。

程序优先查找随附工具目录，其次查找 `SmartPing.exe` 所在目录和 `PATH` 中的绝对目录里的 `ntrace.exe`；未找到时，基础 ICMP 追踪使用 Windows 系统目录中的 `tracert.exe`。TCP 或指定 `psize` 时必须有可用的 ntrace，缺失时任务会明确报错。程序保留 ntrace 默认的 GeoIP 查询和输出；地理信息取决于执行节点能否访问 ntrace 的数据服务。运行时不自动下载工具，也不通过当前工作目录、`cmd.exe` 或 PowerShell 执行命令。程序将目标和受限制的数值分别传入进程参数列表，网页和 Agent 均不能指定可执行文件、原始命令行或额外参数。

追踪协议默认为 ICMP。选择 TCP 后端口必填，页面预填 `443`，仅允许 1–65535 的十进制整数；ICMP 不接受端口。可选 `psize` 留空时使用 ntrace 默认值，填写时必须是十进制整数，最大为 65535 bytes。它是 ntrace 的**总包大小，包含 IP 和探测协议报头**，与上文持续 ICMP Ping 的数据载荷大小不同。IPv4 的最小值为 ICMP 28 / TCP 40 bytes；IPv6 的最小值为 ICMP 48 / TCP 60 bytes。域名输入先按 IPv4 最小值校验，ntrace 根据实际解析到的地址族继续校验；包大小不适用时保留工具错误，不会静默忽略。端口和 `psize` 同样在网页、中心及 Agent 校验，拒绝空格、符号、表达式、小数、溢出和命令片段。

Windows 上 ntrace TCP 追踪需要管理员权限与 WinDivert 支持；x64 发布包已包含运行文件，无需额外下载或执行 `--init`，但普通账户不会因此自动获得加载驱动的权限。SmartPing 不自动提升权限或修改防火墙；有管理员权限时，NextTrace 调用的 WinDivert 库可能加载或注册自身驱动。权限或系统策略不满足时，页面显示实际错误。默认以 LocalService 运行的 Windows 服务同样受此权限要求限制。

最大跳数为 1–64（默认 30），单次探测等待时间为 100–5000 ms（默认 1000 ms）。每次路由追踪最多运行 120 秒，每个执行节点最多同时执行两个任务。页面显示工具返回的文本，不会将缺失响应补成虚构跳点；输出最多保留 65,536 个字符，并提示截断。取消、超时或服务停止会终止执行进程及其子进程。

远程执行需要将中心和全部参与的 Agent 一起更新。新 Agent 注册路由追踪协议版本 `traceVersion: 2`，并在每次获取任务时携带 `traceVersion=2`；旧 Agent 或未声明版本的 Agent 仍可执行基础 ICMP，中心会拒绝向它们分配 TCP 或带 `psize` 的追踪，并提示升级，避免旧程序忽略新参数。任务获取请求未声明版本或版本小于 2 时，只返回基础 ICMP，即使相同节点此前由新版本 Agent 注册过也不会返回高级任务。Agent 独立于常规 Ping/TCP Ping 约每 2 秒获取任务，并上报完整的最新输出；网络或工具故障不会中断原有探测项目。持续 15 秒无法成功获取任务时，Agent 会取消正在运行的路由追踪；成功获取的任务列表移除某个任务时，也会取消该任务。

路由追踪用于临时诊断，其任务和输出仅保留在中心内存中，不写入 SQLite。中心重启后结果不可恢复，Agent 在下一次同步发现旧任务已消失时取消执行；它们与持久化的 Ping/TCP Ping 历史无关。

## 发布为独立 Windows 程序

在安装了 .NET 8 SDK 的开发机器上：

```powershell
.\scripts\publish.ps1
# ARM64 Windows 可使用 -Runtime win-arm64
```

输出位于 `artifacts/publish/win-x64`。**复制整个输出目录**，包括 `SmartPing.exe`、`wwwroot`、配置文件和 `tools/nexttrace`。Windows x64 包含 NextTrace 与 WinDivert，中心和 Agent 迁移时都应保留该子目录；ARM64 发布需自行准备适用架构的追踪工具。发布包包含 .NET 运行时；SQLite 原生库会由单文件宿主解压加载，静态资源仍在 `wwwroot` 中。

```powershell
cd .\artifacts\publish\win-x64
.\SmartPing.exe
```

默认仍为本机面板。部署远程中心或 Agent 时，可以复制 `appsettings.json` 为 `appsettings.Production.json`，修改其中的 `Urls` 和 `SmartPing` 配置，再运行。程序默认环境为 Production。

```json
{
  "Urls": "http://0.0.0.0:5080",
  "SmartPing": {
    "Mode": "server",
    "DataDirectory": "D:\\SmartPingData",
    "RetentionDays": 30,
    "NodeName": "监测中心 A",
    "NodeAddress": "192.0.2.10",
    "AdminToken": "替换成至少16字符的管理员随机令牌",
    "AgentToken": "替换成另一个至少16字符的Agent随机令牌"
  }
}
```

Agent 的配置将 `Mode` 改为 `agent`，设置 `ServerUrl`、`AgentToken`、`NodeName`、`NodeAddress` 与独立的 `DataDirectory`。Agent 使用出站 HTTP，不提供网页面板。

配置优先级是：命令行参数 > 环境变量 > 对应环境的配置文件 > `appsettings.json`。环境变量采用双下划线，例如 `SmartPing__AgentToken`、`SmartPing__AdminToken`、`SmartPing__ServerUrl`。源码运行脚本通过当前子进程环境传递令牌，令牌不会放入 dotnet 命令行参数。不要将真实令牌提交进仓库。

## 安装 Windows 服务

先为每个服务实例复制一份完整发布目录，例如 `C:\Services\SmartPing`。在**管理员 PowerShell** 中运行仓库里的安装脚本：

```powershell
.\scripts\install-service.ps1 -PublishDirectory 'C:\Services\SmartPing' `
  -Name 'SmartPing' -Mode server -ListenAddress '0.0.0.0' `
  -NodeName '监测中心 A' -NodeAddress '192.0.2.10' `
  -AdminToken $adminToken -AgentToken $agentToken `
  -DataDirectory 'C:\ProgramData\SmartPing'

Start-Service -Name 'SmartPing'
Get-Service -Name 'SmartPing'
```

安装脚本会创建自动启动的服务，**安装后保持停止状态**，由上面的 `Start-Service` 启动。默认以 `NT AUTHORITY\LocalService` 运行，写入受 ACL 保护的 `appsettings.Production.json`，仅 Administrators、SYSTEM 和 LocalService 可读取，并授予 LocalService 数据目录写权限。安装时同时注册 Application 事件日志源，服务日志可在 Windows 事件查看器中查看。令牌不进入服务启动命令行。

Agent 服务示例：

```powershell
.\scripts\install-service.ps1 -PublishDirectory 'C:\Services\SmartPingAgent' `
  -Name 'SmartPingAgent' -Mode agent `
  -ServerUrl 'http://192.0.2.10:5080' `
  -NodeName 'Agent B' -NodeAddress '192.0.2.11' `
  -AgentToken '<中心的 AgentToken>' `
  -DataDirectory 'C:\ProgramData\SmartPingAgent'
Start-Service -Name 'SmartPingAgent'
```

每个服务使用独立发布目录与数据目录。安装脚本拒绝覆盖已有服务或 `appsettings.Production.json`。修改受保护配置需要管理员权限，修改后执行 `Restart-Service`。可先使用 `-WhatIf` 查看安装动作。

卸载服务，保留程序、配置及全部数据：

```powershell
.\scripts\uninstall-service.ps1 -Name 'SmartPing'
```

本次开发已完成服务脚本的语法检查，未在开发机器上实际安装、启动或卸载 Windows 服务；服务账户、ACL 和事件日志行为需要在部署机器上进行管理员环境验收。

升级时先停止服务，备份数据和 `appsettings.Production.json`，在暂存目录发布新版本，再更新程序与 `wwwroot`，恢复受保护的实例配置后启动。不要直接将新发布输出覆盖到正在运行的服务目录。

## 数据、安全与使用边界

- 中心数据库为数据目录中的 `smartping.db`，使用 SQLite WAL。备份时先停止服务再复制整个数据目录，或者使用 SQLite 的在线备份工具；只复制运行中的主 `.db` 文件可能漏掉 WAL 中的写入。
- 公开源码仅包含文档保留地址和占位配置。实际运行配置、数据库、日志、截图及发布产物已排除在 Git 之外；迁移这些私有文件时仍应自行保管，避免把真实目标地址、路由结果或令牌提交到仓库。
- 历史按 `RetentionDays` 清理，默认 30 天；配置不会随历史清理而删除。清理在启动时及之后每小时运行，删除历史不保证数据库文件立即缩小。
- `AdminToken` 保护管理 API；`AgentToken` 用于 Agent 接入。设置了管理员令牌时，网页会要求提供令牌，仅保存在当前浏览器标签页的 `sessionStorage`。共享 AgentToken 表示所有持有者都属于同一可信节点集合。
- 回环地址上的无令牌模式适用于本机使用。需要远程接入时限定可信网络，并通过 HTTPS 反向代理或可信 VPN 保护令牌与数据；内置默认地址使用 HTTP，不提供自动证书管理。
- 监测中心与 Agent 应保持系统时间同步，避免远程样本的时间轴错位。
- 当前最多 200 个探测项目、100 个节点；一次互测最多选择 15 个节点，生成后仍不能超过 200 项目。重复互测会跳过已有的相同来源节点、目标主机、协议及 TCP 端口组合。
- 本项目不包含通知告警、按用户划分权限、抓包分析、完整 SmokePing 分位数烟雾图或集群高可用。大量节点与 1 秒探测间隔会增加数据库与网络负载，部署规模需依据实际机器验证。

## HTTP API

管理接口使用 `Authorization: Bearer <AdminToken>`；没有配置 AdminToken 时仅允许回环来源。Agent 接口始终需要 `Authorization: Bearer <AgentToken>`，未配置 AgentToken 时拒绝接入。`GET /api/health` 无需鉴权。错误响应使用 `{ "error": "说明" }`，缺失/错误令牌返回 401，禁用接入或禁止跨站修改返回 403。

| 方法与路径 | 用途 |
| --- | --- |
| `GET /api/health` | 存活状态及运行模式 |
| `GET /api/settings` | 不含令牌值的公共配置 |
| `GET /api/dashboard?hours=1&nodeId=local` | 概览、项目统计与聚合曲线；省略 nodeId 合并所有执行节点 |
| `GET /api/nodes` | 节点、最后在线时间及在线状态 |
| `DELETE /api/nodes/{id}` | 移除远程节点的注册及项目分配；本机节点不可移除 |
| `GET /api/targets` | 项目配置列表 |
| `POST /api/targets` | 创建项目，服务器生成 ID |
| `PUT /api/targets/{id}` | 替换项目配置，`enabled: false` 暂停 |
| `DELETE /api/targets/{id}` | 删除项目及其历史 |
| `GET /api/targets/{id}/history?hours=1&nodeId=local` | 统计、时间桶曲线、最近最多 100 个样本 |
| `POST /api/mesh` | 按所选 nodeIds 创建互测 |
| `GET /api/traces/tools` | 查看中心节点可用的工具和 `supportsTcp` / `supportsPsize` 能力 |
| `POST /api/traces` | 创建一次路由追踪：`host`、`nodeId`、`maxHops`、`timeoutMs`、`protocol`（默认 `icmp`）、`port`、`psize` |
| `GET /api/traces/{id}` | 获取任务状态和最新输出 |
| `DELETE /api/traces/{id}` | 取消路由追踪任务 |
| `POST /api/agent/register` | Agent 注册/心跳：`id`、`name`、`address`、`traceVersion`（可选，省略为 0；新 Agent 为 2） |
| `GET /api/agent/config?nodeId=...` | 取回该 Agent 已启用的项目 |
| `POST /api/agent/report` | 上报 `{ "nodeId": "...", "samples": [...] }`，每批最多 2000 条 |
| `GET /api/agent/traces?nodeId=...&traceVersion=2` | Agent 获取自己的路由追踪任务与取消标记；省略版本或版本小于 2 时仅返回基础 ICMP |
| `POST /api/agent/traces/report` | Agent 上报路由追踪状态、实际工具与输出快照 |

时间范围 `hours` 默认 1，可查询 0.25–168 小时；超出上下限会截取到边界。时间戳使用 UTC ISO 8601，网页按浏览器本地时间显示。

创建 ICMP 项目示例：

```powershell
$headers = @{ Authorization = "Bearer $adminToken" } # 本机无令牌模式可以省略
$body = @{
  name = '示例目标'; host = '192.0.2.1'; protocol = 'icmp'
  packetSize = 32; intervalSeconds = 5; nodeIds = @('local')
  group = '基础网络'; enabled = $true
} | ConvertTo-Json
Invoke-RestMethod -Uri 'http://127.0.0.1:5080/api/targets' `
  -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' `
  -Body ([Text.Encoding]::UTF8.GetBytes($body))
```

TCP 项目使用 `protocol: "tcp"`，并设置 `port`、`timeoutMs`、`retransmitThresholdMs`。API 数据结构见 `src/SmartPing/Models/Contracts.cs`。

一次 TCP 路由追踪示例（需要执行节点上的 ntrace 支持与权限）：

```json
{
  "host": "203.0.113.10",
  "nodeId": "local",
  "protocol": "tcp",
  "port": 443,
  "psize": 64,
  "maxHops": 30,
  "timeoutMs": 1000
}
```

`port` 仅 TCP 必填，ICMP 必须省略或为 `null`；`psize` 可省略或为 `null`。这些数值通过 JSON 整数传入，不能传入包含命令、空格或其他字符的字符串。远程任务向 Agent 原样传递已验证的协议、端口及包大小；Agent 执行前再次校验。

## 开发与检查

```powershell
dotnet build .\src\SmartPing\SmartPing.csproj
dotnet run --project .\tests\SmartPing.Tests
# PowerShell 7：隔离的中心 + 双 Agent HTTP 集成验证，无外部探测
.\scripts\test-integration.ps1
# PowerShell 7：真实 ntrace / Windows tracert、Agent 路由追踪和输入安全验证，仅探测回环地址
.\scripts\test-traceroute.ps1
```

项目结构：

```text
src/SmartPing/
  Models/        配置与 API 数据结构
  Services/      ICMP/TCP 探测、Agent 同步、SQLite 存储与保留策略
  tools/nexttrace/  随附追踪工具、来源校验与第三方许可证
  wwwroot/       不依赖外部 CDN 的网页面板
  appsettings.json
scripts/         运行、Agent、发布及服务安装/卸载脚本
tests/           验证脚本或测试项目
```

如果 ICMP 全部失败，先在同一台执行节点上用系统 `ping` 检查路由与目标防火墙；TCP 使用 `Test-NetConnection 主机 -Port 端口` 检查服务是否监听。节点不出现时检查中心地址、AgentToken、中心入站端口和 Agent 日志；TCP 「连接拒绝」说明目标拒绝了连接，并不表示成功探测。
