# 集成 Bot 回归

这个控制台测试项目直接引用本仓库的 `KLink.Bot`，不依赖独立工具 checkout。
卡库、IR 和卡向量使用 KLink-bot `89515569c036b295e14aeaa4d759d50891192b20`
的 `klink bot/docs`：

```powershell
dotnet run --project tests/KLink.Bot.RegressionTests/KLink.Bot.RegressionTests.csproj `
  -c Release -p:BotDataRoot='D:\KLink-bot\klink bot\docs' -p:NuGetAudit=false
```

测试默认加载构建输出中的 `Data/`；也可以在 `--` 后传入已有运行数据目录。
退出码 0 表示全部通过。神经网络检查使用固定的合成模型，不需要部署模型。

覆盖内容：

- 重放结束后，Bot 选择器优先于仍挂在引擎上的 HT 答复读取器。
- Gordon 只选择合法指令；选中卡回到牌库顶，抽到后费用变为 0。
- 无合法候选时记录 HT 目标 0；非法 Bot 选择不回退到旧回放答复。
- 历史回放区分合法目标、明确的目标 0、缺失答复和非法目标。
- 贪心和神经网络服务端路径都生成相邻的 PC/HT，动作编号连续。
- 用新的回放引擎执行生成的 PC/HT，选中卡落点和随机流与决策一致。
- `GetAdjacentCards` 按物理位置扫描全局战场，不按 Owner 截断；按
  `includeCovertCards` 正确排除或保留未揭示隐蔽邻卡。

修复前 9 项中有 5 项失败；修复后 9/9 通过。这个项目补充了独立 Bot 的
规则自测，特别覆盖从历史回放切换到实时 Bot 决策的边界。
