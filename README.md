# KLink — 本地联机工具（Windows 版）

将游戏服务器内嵌到启动器，一个程序即可**本地开服 / 局域网联机 / 连接远程服**。

本仓库为 **.NET (WPF) 重写版**：界面与交互仿照 [KLink Android 版（Java）](https://github.com/Xuewu-awa/KLink) 的前端设计（深色/浅色双主题、侧边导航、控制台），服务器核心逻辑移植自 [Go 服务端](https://github.com/kardswalker/kards-server-go)，并与 Java 版 `kardsserver` 保持协议一致。

---

## 运行模式

| 模式 | 说明 |
|------|------|
| **本地模式** | 本机自建服务器 (`127.0.0.1:5231`)，单人自娱 + 人机对战 |
| **局域网模式** | 服务器绑定 `0.0.0.0`，UDP 广播房间信息（端口 5233） |
| **远程转发** | TCP 透明代理，本地 `127.0.0.1:5231` → 远程服务器，HTTP + WebSocket 全协议转发 |

---

## 项目结构

```
src/
├── KLink.App/                       # WPF 启动器（.NET 10）
│   ├── Views/                       服务器 / 房间发现 / 模组管理 / 设置
│   ├── Services/                    KLinkService（模式编排）/ ModManager /
│   │                                GameProcessService / ProxyServer / LanDiscovery /
│   │                                SettingsService / LogService
│   ├── Infrastructure/              按钮光圈 / 微动画 / 主题切换
│   └── Resources/                   主题色板（Deep Dark / Moon Light）
│
└── KLink.Server/                    # 游戏服务器核心类库
    ├── GameServer.cs                内嵌 Kestrel 宿主（HTTP + WebSocket）
    ├── Http/ApiHandler.cs           30+ 游戏 API 端点（官方协议兼容）
    ├── Ws/GameWebSocketServer.cs    WebSocket 连接管理（JWT 认证 / 消息转发）
    ├── MatchManager.cs              匹配队列 + Bot 对战
    ├── Data/AppDatabase.cs          SQLite 持久化（账号 / 卡组 / 装备）
    ├── Util/                        JWT（HS256）/ ActionCipher（动作加解密）
    ├── DeckCodeManager.cs           卡组代码解析与对局发牌
    └── Assets/kards-server/         游戏静态数据（卡牌库等）
```

---

## 功能

- **服务器生命周期** — 启动 / 停止 / 模式切换 / 实时状态轮询
- **人机对战** — 训练模式自动创建 Bot 对局，Bot 自动跳过回合
- **局域网发现** — UDP 广播（端口 5233）+ 主动扫描 + 一键加入
- **模组管理** — PAK 扫描 / 安装（自动补 `_P` 后缀）/ 卸载 / 启用禁用 / 拖拽安装
- **远程代理** — HTTP + WebSocket 全协议双向 TCP 转发（屏蔽官方域名）
- **游戏进程管理** — 一键启动 / 关闭游戏本体，运行状态检测
- **双主题** — Deep Dark（翡翠深色）/ Moon Light（米白浅色），即时切换并持久化
- **窗口边缘辉光** — 鼠标靠近窗口边缘时感应发光（跟随鼠标）
- **配置持久化** — 启动器 exe 同级 `config.json` + `data/kards_server.db`

---

## 技术架构

```
┌──────────────────────────────────────────────┐
│  WPF UI（Fluent 风格）                        │
│  Views + Services + Infrastructure            │
├──────────────────────────────────────────────┤
│  KLink.App.Services.KLinkService              │
│  模式编排 / 游戏进程 / 模组 / 房间 / 代理      │
├──────────────────────────────────────────────┤
│  KLink.Server.GameServer（内嵌 Kestrel）      │
│  HTTP + WebSocket + MatchMaker + SQLite       │
└──────────────────────────────────────────────┘
```

游戏客户端 → `127.0.0.1:5231` (HTTP) / `127.0.0.1:5232` (WebSocket)

---

## 构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/)。当前源码 checkout 的 solution 是 `KLink.sln`；详细的 KLink-dotnet、KLink-bot 与 fyserver 构建/集成步骤见 [BUILDING.md](BUILDING.md)。

```powershell
dotnet restore KLink.sln
dotnet build KLink.sln -c Release
```

该 solution 构建 WPF 启动器与 `KLink.Server` 项目；Bot 内核和独立 fyserver 需要按构建指南单独构建。完整桌面发行版还需要把 fyserver 自包含产物装配到启动器同级的 `data\fyserver\`。

启动器会自动从自身位置向上查找游戏本体（`kds\kards\Binaries\Win64\`），无需手动配置；找不到时可在「设置」页手动指定。

---

## 相关项目

- **Java 版（Android）** — 界面与交互设计的参考来源，位于 `../KLink/source`
- **Go 服务端** — 服务器核心逻辑移植自 `../KLink/kards-backend-go`（功能更完整的参考实现）

---

## 免责声明

本项目仅供学习交流使用，严禁用于任何商业或非法用途。

- 游戏客户端及卡牌数据等知识产权归原厂商所有，本项目不包含任何游戏原始代码。
- 修改、逆向工程或重新分发游戏客户端可能违反原厂商服务条款，请自行评估风险。
- 项目作者不提供、不分发任何修改后的游戏安装包，一切法律后果由使用者自行承担。
- 严禁将本项目用于线上官服作弊、刷分或任何破坏游戏公平性的行为。
- 本地数据库仅存储于本机，不涉及任何云端同步或数据收集。

使用本项目即表示你已阅读并同意以上条款。
