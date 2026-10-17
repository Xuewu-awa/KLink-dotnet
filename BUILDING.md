# KLink-dotnet、KLink-bot 与 fyserver 构建指南

本文按当前仓库布局说明从源码构建启动器、规则内核和服务端的方法。`fyserver`
源码已经放在 KLink-dotnet 的 `vendor/fyserver/`；KLink-bot 的源码和数据仍是独立
checkout，因为 Bot 数据体积和生成工具不属于这个仓库。

## 1. 仓库边界与目录布局

当前工作区中的 `src/` 是 KLink-dotnet 仓库根目录。把它单独 clone 到新目录后，
仓库根目录就是下面树中的 `KLink-dotnet/`：

```text
<workspace>/
├── KLink-dotnet/                  # 本仓库根；当前工作区为 <workspace>/src/
│   ├── KLink.sln                  # WPF 启动器与 KLink.Server
│   ├── KLink.App/
│   ├── KLink.Bot/                 # 集成构建使用的规则内核项目
│   ├── KLink.Server/
│   └── vendor/fyserver/            # 已纳入本仓库的 fyserver 源码
└── klink bot/                     # 独立 KLink-bot checkout（提供 BotData）
    └── klink bot/docs/
```

`vendor/fyserver/` 不是 submodule，也不需要再把上游 fyserver clone 到它上面。
其来源、固定版本和本地工作树文件见 [vendor/fyserver/VENDOR.md](vendor/fyserver/VENDOR.md)。
构建或分发前必须同时阅读该目录的 `README.md` 和 `LICENSE`；KLink-dotnet 的许可证
不会替换 fyserver 自身的许可证和限制。

### 从 GitHub 重建工作区

```powershell
$workspace = 'D:\KLink-workspace'
New-Item -ItemType Directory -Force -Path $workspace | Out-Null

git clone https://github.com/Xuewu-awa/KLink-dotnet.git "$workspace\KLink-dotnet"
git clone https://github.com/Xuewu-awa/KLink-bot.git "$workspace\klink bot"

Set-Location "$workspace\KLink-dotnet"
```

第三个 fyserver checkout 不需要创建：源码已随 KLink-dotnet 位于
`vendor\fyserver`。要复现当前集成结果，还应记录并检出本次使用的 KLink-bot
commit；不要假定两个仓库的 `main` 永远同步。当前集成内核基于
`89515569c036b295e14aeaa4d759d50891192b20`，并补充了 Bot 手牌选择器优先于
回放 HT 读取器的修复。重放结束后继续决策时，Gordon Highlanders 等卡现在可以
正常选择手牌、执行效果并发送 HT 答复。

## 2. 环境要求

- Windows 10/11 x64。
- .NET 10 SDK；用 `dotnet --list-sdks` 确认存在 `10.0.x`。
- Node.js 和 npm；构建 `vendor/fyserver/AdminUi` 时需要。
- 可访问的 NuGet 源，或已存在于用户 NuGet 缓存中的依赖包。

vendor 中不再保留指向本机 `nuget-local` 目录的 `NuGet.config`。干净 checkout 使用
用户或机器级 NuGet 配置和 nuget.org；当前工作区如果需要离线包，使用工作区外层
已有的 NuGet 配置即可。

Python 只在重新生成卡牌数据时需要。常规 C# 编译、BotSim 自测和服务端宿主验证
不需要游戏客户端或 Python。

## 3. BotData 与独立 KLink-bot

从 KLink-dotnet 根目录构建时，`KLink.Bot/KLink.Bot.csproj` 默认读取：

```text
..\klink bot\klink bot\docs
```

从 `vendor/fyserver` 项目构建时，默认路径是：

```text
..\..\..\klink bot\klink bot\docs
```

这些路径都指向目录布局中的同一份 BotData。若数据目录在其它位置，构建时显式
传入 `BotDataRoot`，例如：

```powershell
$botData = 'D:\data\klink-bot'

dotnet build KLink.Bot\KLink.Bot.csproj -c Release `
  -p:BotDataRoot=$botData -p:NuGetAudit=false

dotnet build vendor\fyserver\fyserver.csproj -c Release `
  -p:BotDataRoot=$botData -p:EnableAot=false `
  -p:SkipAdminUiBuild=true -p:NuGetAudit=false
```

至少需要 `cards.live.json`、`card-effects.json`、`card-ir.json`、
`card-vectors.json` 和 `deck_code_ids.live.json`。规则内核项目还会复制
`event-contracts.json`。输入 JSON 会被复制到构建输出的 `Data/` 或
`BotData/`；修改数据后应重新构建，避免继续使用旧副本。

如果要构建独立的 KLink-bot，从它自己的仓库根目录运行：

```powershell
dotnet build src/KLink.Bot/KLink.Bot.csproj -c Release -p:NuGetAudit=false
dotnet build tools/BotSim/BotSim.csproj -c Release -p:NuGetAudit=false

dotnet run --project tools/BotSim/BotSim.csproj `
  -c Release -p:NuGetAudit=false -- selftest
```

当前最新远端 Bot checkout 的 selftest 实测为 530/530；这个数字会随数据和测试
增加而变化，运行命令的退出码和最终通过数才是本次 checkout 的验证结果。

本仓库的补充回归直接引用集成后的 `KLink.Bot` 项目，覆盖 Bot 手牌选择与历史
HT 答复的优先级、空目标和非法目标、物理位置邻卡查询、老兵晋升、临时增益撤销、
回合结束清理、抑制与回手重置、射程/放逐阵营实例值、蓝图成员读取、生成顺序，
以及贪心/神经网络服务端动作的重放：

```powershell
dotnet run --project tests/KLink.Bot.RegressionTests/KLink.Bot.RegressionTests.csproj `
  -c Release -p:BotDataRoot=$botData -p:NuGetAudit=false
```

其中 `$botData` 应指向上述固定 Bot 版本的 `klink bot/docs`。测试使用随项目复制的
`Data/`，不需要 `nn-model.bin`；神经网络路径使用确定的合成模型验证控制流程。

老兵晋升要求存在精确的 `_vet` 模板。晋升按蓝图的 `veteranSet` 清除攻、防、行动费和
重甲的永久数值增益，保留临时增益，并重设防御和最大防御。测试生成老兵或晋升事件时
也必须选择有真实 `_vet` 模板的卡；旧 BotSim 中使用普通无升级卡的样本需要相应替换。

完整的集成验证入口位于本仓库 `tests/Run-BotVerification.ps1`：

```powershell
pwsh -File tests/Run-BotVerification.ps1 -BotSourceRoot 'D:/checkouts/KLink-bot'
```

它从固定 commit 读取五个 BotSim 源文件和真实回放 508065 到忽略的 `obj/pinned/`，
保留全部断言，仅应用已记录的五处老兵测试卡替换，然后引用当前集成内核运行。
两个含真实晋升的 live 回放 310284、955337 也从同一 commit 提取，作为带 HQ
校验的回归输入。六个数据文件须匹配
`tests/KLink.Bot.UpstreamTests/upstream.lock.json` 的 SHA-256；数据不在 checkout
默认路径时可传 `-BotDataRoot`。实测本地回归 25/25、固定上游自测 530/530；
源码、数据、补丁锚点或测试失败都会使命令失败。

## 4. 构建 KLink-dotnet 启动器

从 KLink-dotnet 根目录运行：

```powershell
dotnet restore KLink.sln -p:NuGetAudit=false
dotnet build KLink.sln -c Release -p:NuGetAudit=false
```

`KLink.sln` 当前包含 `KLink.App` 和 `KLink.Server`。`KLink.Bot` 与 vendor
fyserver 是独立项目，需要分别构建：

```powershell
dotnet build KLink.Bot\KLink.Bot.csproj -c Release -p:NuGetAudit=false
```

常见输出位置为：

```text
KLink.App\bin\Release\net10.0-windows\KLink.App.exe
KLink.Bot\bin\Release\net10.0\KLink.Bot.dll
```

## 5. 构建 vendor fyserver

先构建规则内核，再构建服务端。Bot 内核仍有运行时反射式数据读取，因此当前
宿主验证和发行版使用 `EnableAot=false`：

```powershell
dotnet build KLink.Bot\KLink.Bot.csproj -c Release -p:NuGetAudit=false

dotnet build vendor\fyserver\fyserver.csproj -c Release `
  -p:EnableAot=false -p:SkipAdminUiBuild=true -p:NuGetAudit=false
```

服务端输出位于：

```text
vendor\fyserver\bin\Release\net10.0\
```

需要完整后台页面时，在服务端项目目录执行：

```powershell
Push-Location vendor\fyserver\AdminUi
npm ci --no-audit --no-fund
npm run build
Pop-Location

dotnet build vendor\fyserver\fyserver.csproj -c Release `
  -p:EnableAot=false -p:NuGetAudit=false
```

项目构建前也会自动运行 AdminUi 构建；`SkipAdminUiBuild=true` 只适合已经有
`wwwroot/admin-ui` 构建结果的快速 C# 验证。

### 当前工作区的 BotHostTest

外层工作区的 `tools/BotHostTest` 是集成验证工具，不属于 KLink-dotnet 仓库。
它的项目引用应指向 `src/vendor/fyserver/fyserver.csproj`，而不是旧的
`tem/fyserver`。从工作区根目录运行：

```powershell
dotnet run --project tools\BotHostTest\BotHostTest.csproj `
  -c Release -p:EnableAot=false -p:SkipAdminUiBuild=true `
  -p:NuGetAudit=false -- src\vendor\fyserver\bin\Release\net10.0\BotData
```

该测试会加载构建输出中的真实卡牌数据、IR、向量和神经网络模型，并验证服务端
Bot 宿主的动作编号、回放、会话 ID、换牌和卡组码表。模型文件不是源码输入；没有
`nn-model.bin` 时内核会退回贪心策略，测试所需模型可由当前工作区的部署脚本
或显式 `BotData` 目录提供。

## 6. 发布启动器与服务端

先发布非 AOT、自包含的 fyserver。发布目录使用新的目录，避免覆盖运行中的账号、
回放或 FASTER 数据：

```powershell
dotnet publish vendor\fyserver\fyserver.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:EnableAot=false -p:SkipAdminUiBuild=true `
  -o vendor\fyserver\publish\win-x64 -p:NuGetAudit=false
```

自包含服务端不要求目标机另装 .NET 10。随后把同一份服务端产物装入启动器发布
目录：

```powershell
$out = 'D:\KLink-release-clean'

dotnet publish KLink.App\KLink.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:FyServerSource="$PWD\vendor\fyserver\publish\win-x64" `
  -o $out -p:NuGetAudit=false
```

`KLink.App.csproj` 的默认来源顺序是 `vendor\fyserver\publish\win-x64`，然后是
`vendor\fyserver\bin\<Configuration>\net10.0`。构建后它会把服务端二进制和
`library\`、`config\`、`wwwroot\`、`BotData\` 装配到启动器同级的
`data\fyserver\`。

也可以从 KLink-dotnet 根目录用仓库内的发布脚本完成上述四步。默认输出到仓库
同级的 `rel\`；输出目录会被删除后重建：

```powershell
pwsh -File .\build-release.ps1 -OutDir 'D:\KLink-release-clean'

# BotData 不在相邻 klink bot checkout 时：
pwsh -File .\build-release.ps1 `
  -BotDataRoot 'D:\data\klink-bot' `
  -OutDir 'D:\KLink-release-clean'
```

该脚本不处理现有部署目录，也不停止运行中的服务。它只适合新的发行输出目录。

源目录不提交运行时 `setting.json`。启动器启动服务端前会根据本地、局域网或远程
模式写入部署目录的配置；直接手工运行服务端时可以复制
`vendor\fyserver\setting.example.json` 为 `setting.json` 后再编辑。

## 7. 更新现有部署目录

当前工作区外层的 `tools/build-deploy-server.ps1` 会构建 KLink.Bot 和 vendor
fyserver、运行 BotHostTest、备份旧二进制并校验 SHA-256。它只属于这个集成工作区，
不是 KLink-dotnet 仓库的一部分：

```powershell
pwsh -File tools\build-deploy-server.ps1
pwsh -File tools\build-deploy-server.ps1 -DeployTo 'D:\KLink-test\data\fyserver'
```

部署前应停止正在运行的 fyserver/KLink.App。脚本只替换服务端二进制和 BotData，
保留目标目录中的 `setting.json`、`data\`、`faster-log\` 和回放。发布打包脚本
可以删除输出目录，因此只能把它指向新的发行目录，不要指向现有运行目录。

## 8. 交付前核对

1. 分别记录 KLink-dotnet、KLink-bot 和 vendor 来源的 commit；vendor 来源记录在
   `vendor/fyserver/VENDOR.md`。
2. 检查 `git status --short`，不要把 `bin/`、`obj/`、`publish/`、npm 依赖、账号库、
   回放、FASTER 数据或部署目录加入提交。
3. 运行 BotSim `selftest`、KLink.sln 构建、vendor fyserver 构建和 BotHostTest。
4. 检查发布目录存在 `data/fyserver/BotData/`、`library/`、`config/` 和
   `wwwroot/`，并记录部署前后的 DLL SHA-256。
5. 运行或分发前阅读 fyserver 的 `LICENSE`。该许可证包含非商业用途和网络服务等
   附加限制；KLink-dotnet 的 GPL-3.0 不会覆盖这些限制。
