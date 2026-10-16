# KLink.Bot —— KARDS 规则内核（模拟器）

> 从反编译产物重建的对局引擎。目标不是「训练优先」，而是先把内核跑对并**量化缺口**。
>
> 相关文档：`klink bot/docs/对局协议参考.md`（协议）、`klink bot/docs/卡组覆盖率.md`（验收标准）

---

## 现在能做什么

```bash
# 列出内置卡组及解析结果
dotnet run -c Release --project tools/BotSim -- decks

# 跑对局（贪心策略 vs 贪心策略）
dotnet run -c Release --project tools/BotSim -- play --games 1000 --seed 7
dotnet run -c Release --project tools/BotSim -- play --games 1 --verbose    # 打印第一局流程

# 统计还缺哪些卡的效果
dotnet run -c Release --project tools/BotSim -- coverage
```

实测（Release，8 对卡组交叉验证）：

```
德芬车   vs 德澳老兵   300 局  1.32s  228 局/秒   左胜 156 / 右胜 144
英苏中立 vs 米色团     300 局  1.47s  205 局/秒   左胜 234 / 右胜  66
日波炸槽 vs 日澳快攻   300 局  1.17s  256 局/秒   左胜 241 / 右胜  59
美澳跳   vs 美英跳     300 局  1.50s  200 局/秒   左胜  97 / 右胜 203
德美     vs 日法       300 局  1.44s  209 局/秒   左胜 148 / 右胜 152

蓝图程序执行: 84/局      Kismet 步数: 636/局      程序异常: 0
未实现的调用: 无（部分卡组还剩 2~7 个原语待补）
```

**约 200 局/秒 ≈ 每小时 72 万局。** 「几十万到几百万场」不是瓶颈。

> ⚠️ 胜率有些偏斜（如 241/59）。这是**预期内**的：`_bal` 平衡变体的数值还没应用、
> 部分效果是近似实现、前线规则也未与客户端对齐。这些要靠真实回放逐帧对拍来收紧，
> 而不是靠调参数。

---

## 核心思路：不手写效果，**解释 Blueprint 字节码**

原计划是给每张卡手写一段 C# 效果脚本 —— 267 张 × 每张几十行，慢且易错。

但完整字节码里天然带着数据流：

```
{"Inst":"Let","Variable":{"Variable Name":"tempCard"},
 "Expression":{"Variable Name":"CallFunc_GetLocationCardBySide_card"}}
```

`CallFunc_<函数名>_<参数名>` 就是那个调用的输出槽。配合 `JumpIfNot` 的控制流，
一张卡的效果就是「一串有输入输出的步骤」，可以直接解释执行。

于是分成两层：

| 层 | 文件 | 作用 |
|---|---|---|
| **原语层** | `Effects/CardApi.cs` + `CardApiDispatch.cs` | 那 170+ 个游戏调用的实现 |
| **编排层** | `Effects/Blueprint/KismetVm.cs` | 解释 IR，把原语按蓝图逻辑串起来 |

**覆盖率随原语层完善自动提升**，不需要为每张卡写代码。

### IR 从哪来

```
pak 里的卡牌蓝图 (.uasset/.uexp)
   ↓ UAssetCLI dump-batch --full        反编译 Kismet → JSON
cards.full.json (84 MB)
   ↓ tools/gen-kismet-ir.py             压成可解释的 IR
card-ir.json (19 MB, 1636 张卡 / 3608 个事件程序)
   ↓ KismetVm                           运行时解释
```

**两条已验证的关键事实**：

1. 事件 stub 里的 `ExecuteUbergraph_X(<entry>)` 实参，**正好等于** ubergraph 中
   该事件链第一条语句的 `StatementIndex` —— 1636 张卡全部命中
2. 局部变量名 `CallFunc_<函数名>_<参数名>` 标识调用的输出槽，数据流由此还原

参数形状也不是猜的：`tools/analyze-call-shapes.py` 从全部 35,360 个调用点
统计出每个调用的实际参数位置（例如 `ChangeDefense(target, instigatorID, 数值, …)`
里数值恒在 index 2）。

---

## 工程结构

```
src/KLink.Bot/
├── Cards/
│   ├── CardDatabase.cs         卡牌静态数据（CDO 权威表 + 变体回退）
│   ├── DeckCodeParser.cs       卡组码解析（与服务器端逐字节一致）
│   └── MetaDecks.cs            22 套内置目标卡组（验收测试集）
├── Engine/
│   ├── Enums.cs                Side / CardLocation（逐值对应游戏原生枚举）
│   ├── CardInstance.cs         对局中的一张卡 + 关键字常量
│   ├── GameState.cs            对局状态 + 快照（用于和客户端对拍）
│   ├── DeterministicRandom.cs  确定性 RNG（锁步必需）
│   └── MatchEngine.cs          回合循环 / 部署 / 移动 / 攻击 / 胜负 / 事件派发
├── Effects/
│   ├── EffectContext.cs        一次效果结算的上下文
│   ├── CardApi.cs              ⭐ 原语层
│   ├── CardApiDispatch.cs      ⭐ 名字派发：字节码调用名 → 原语
│   ├── CardEffectScripts.cs    手写脚本（仅用于需人工澄清的卡）
│   └── Blueprint/
│       ├── KismetIr.cs         IR 模型 + 事件索引
│       └── KismetVm.cs         ⭐ 字节码解释器
└── Bots/GreedyBot.cs           baseline 策略（NN 实现同一 IPlayerPolicy 接口）

tools/BotSim/Program.cs         验证运行器（decks / play / coverage）
```

**触发系统**：`FireTrigger("OnAfterAttack", subject, side, "OnOtherCardAttacks")`
直接用蓝图里的程序名，不做二次映射。事件名有 100+ 个
（`OnStartOfTurn` / `OnEnterPlay` / `OnOtherCardDestroyed` / `OnMoveToFrontline` …），
每张卡实现哪些由 IR 自动决定。

普通 `MoveUnit` 推进会向 `OnMoveToFrontline` / `OnOtherCardMoveToFrontline` 传递
`cardMoved`、`ForceMove=false` 与扣费前的 `moveCost`；BotSim 的 Blue Legion 回归覆盖该载荷。
伤害落地后的 `OnReceiveDamage` / `OnOtherCardReceiveDamage` 同样传递
`fromCard`、`toCard`、`fromAttack` 与实际 `damage`；BotSim 的 Panzer III 回归覆盖该载荷。

---

## 数据从哪来

全部离线提取，**不依赖客户端运行**：

| 数据 | 生成工具 | 说明 |
|---|---|---|
| 卡牌数值 | `tools/gen-card-db.py` | 线上 pak 的卡牌蓝图 **CDO**（2021 张） |
| 效果调用 | `tools/gen-card-effects.py` | 每张卡用了哪些外部调用（覆盖统计用） |
| **字节码 IR** | `tools/gen-kismet-ir.py` | ⭐ 解释器执行的东西 |
| 参数形状 | `tools/analyze-call-shapes.py` | 从调用点统计真实签名 |
| 卡组码表 | `tools/convert-deckcode-table.py` | 线上 pak 的 `deckCodeIDsTable2`（2499 条） |
| 子动作词表 | `tools/gen-protocol-doc.ps1` | 85 个 `ZAction*` + 138 个参数键 |
| 枚举 | `tools/show-enums.py` | `ECardLocationEnum` 等 11 个原生枚举 |

底层解包与反编译由 `klink bot/UAssetCLI` 提供（`pak-extract` / `dump-batch`）。

---

## ⚠️ 当前限制（务必先读）

### 1. `action_type` 与 `action_data` 的线上格式 —— 已用真实对局校正

从 fyserver 控制台日志里抽出了 **8 条真实客户端动作**（`docs/live-actions.json`），
协议层因此有了 ground truth，和纯离线推断有**两处不同**：

**① `action_type` 走紧凑名**（不是蓝图里的全名）

| 全名（离线推断） | 线上实际 |
|---|---|
| `XActionPlayCardFromHand` | **`PC`** |
| `XActionMoveCardToLine` | **`ML`** |
| `XActionAttackCard` | **`AC`** |
| `XActionStartOfTurn` / `XActionEndOfTurn` | 保持全名（不压缩） |

**② `action_data` 是「下标 → 字符串」，且含义随动作类型而变**

```
PC  900009  {0:64, 1:3, 2:0,  3:0,  4:jJ, 84:11}   → 打出 card_unit_heinkel_he_111_waw
AC  900008  {0:7,  1:59, 2:4H, 3:d3,        84:3 }   → 361_light_regiment 攻击 m4a1
ML  900009  {0:74, 1:1,  2:yD,              84:11}   → 移动 card_unit_wolfhounds
```

⚠️ **坑**：`0` 的取值（64/60/32…）**有些恰好是合法的 2 字符卡组码**，
拿去查 `deckCodeIDsTable2` 会得到看似合理但完全无关的卡名。我第一版就踩了这个假阳性。

用 `dotnet run --project tools/BotSim -- replay` 可以复看这份数据的解析结果。

### 2. 规则细节尚未与客户端完全对齐

以下是**猜的**，需要真实回放逐帧确认（代码里都标了 `TODO 待回放确认`）：

- `BP_CardFunctions::CalculateDamageDealt` 已接入重甲、伏击、Shock、lethal 和伤害修正链；
  真实回放仍需继续验证复杂的先后顺序与特殊卡组合。
- `BP_CardFunctions::UpdateGuarded` 的真实语义是相邻 Guard 计算；当前实现仍是近似目标过滤。
- `PinUnit` 的 `pinnedTurns`、到期解除及 `OnOtherUnitPinned/Unpinned` 已建模；
  `cantBePinned` / 位置 / 单位类型三道守卫，以及卡牌直接调用的 `RemovePin` / `ChangedPinnedTurns` 仍待补齐。
- `PayCardCost` 的 `KreditsTax_AsEnemyTarget`、`PayMovementCost` 的行动费用触发尚未完整接入。
- `ChangeFrontlineLimiter` 已接入；`GameplayRestriction` 的来源、时长、查询及抽牌/加槽/出牌/攻击/弃牌限制已接入，仍需用更多真实回放验证时机细节。
- `MakeCardRetreat` 已按蓝图实现：前线优先退到本方半场，半场满或本来在半场时退回拥有者手牌；`cantRetreat` 和非在场目标不变。
- `BP_CardFunctions` 的通用函数是规则首要证据来源；相关蓝图反编译结果见仓库审计文档，
  不应把旧的 C# 近似行为反过来当成规则定义。

**HQ 初始防御 20** 是可确证的（fyserver 注入的 bot 动作里出现 `{"side":"right","75":"20"}`）。

### 3. 平衡变体（`_bal` / `_vet`）暂用基础卡顶替

卡组码表里 **182 个码**指向 `xxx_bal`，另有 `_vet` 变体。蓝图里只有基础卡，
真正的数值微调在 `kards/Content/Structs/BalancedCards` 表里（尚未解出）。
`CardDatabase.Find` 会剥掉后缀回退到基础卡，保证能跑，但**数值可能不准**。

### 4. 近似实现的地方

- `selectTargetFromHand`：真人选择暂时用确定性处理代替
- `selectCardToDraw`（"从候选里挑一张"）：**已按反编译蓝图实现**
  （手牌满判据 + `DrawSpecificCardFromDeckBySide` + `OnHandTargetSelected`）。
  但候选表来自卡内私有函数 `GetChooseSpawnCards`，**它没编进 IR**，所以：
  - 回放路径：由动作流里的 `CS`（= `XActionCardToDrawSelected`）答复指定选中哪张，
    经 `MatchEngine.PickCardToDraw` 钩子传入；答复指向**卡池模板卡**时如实记未应用
  - 自对弈路径：走蓝图自带兜底（`BP_Logic.autoPickCardToDraw` 取牌库第一张）
- `WhichChooseOne`（三选一）：分支来自出牌动作 `PC` 的 `3` 号槽
  （= `ZActionPlayCardFromHand.chooseOneIndex`），写进 `CardInstance.ChooseOne` 后回读；
  默认 0（与 kardsim 的 `ChooseOne` 钩子一致），**不再用随机数**
- `ShowNotification` / `PlaySoundEffect` / `SetVisibility` 等表现层调用：显式 no-op，
  避免污染「未实现」指标
- `Forecast`（预报）机制语义未确认，先当 no-op 并计数
- 触发递归深度上限 8（真实客户端用动作队列串行化，不会无限递归）

### 5. 状态对拍资料仍需补全

仓库已经有 `GameState.SnapshotJson()`、服务端回放审计和部分真实快照能力；但旧的
`docs/live-actions.json` 本身只有**动作**，不是完整的逐动作客户端局面。因此仍需要
更多带动作序号的客户端状态快照，才能把所有差异都定位到首个字段。

**需要客户端侧的状态导出**（游戏自带 `GetMatchCardsAsJsonString`，
见 §下一步）。

---

## 验收标准

**不是「跑得快」，是「和客户端逐步一致」。**

客户端是确定性锁步（效果由双方各自本地计算），所以可以：

1. 拿一局真实对局的动作流
2. 喂进内核重放
3. 用 `GameState.SnapshotJson()` 与客户端状态**逐帧 diff**

只要有一条对不上，就说明规则理解有误。这是本项目唯一可靠的正确性判据。
`GameState.SnapshotJson()` 已按这个用途设计好。

---

## 下一步（按优先级）

1. 以 `BP_CardFunctions` 为依据，补齐 `CalculateDamageDealt`、`UpdateGuarded`、
   `ExecuteOnDealDamageAddDamage` 等高影响通用规则。
2. 完善 `ResetUnitOperations` / Fury、`PinUnit` / `pinnedTurns`、费用税和前线限制。
3. 修复回放器的身份与区域诊断，避免对缺失手牌静默硬塞导致连锁假差异。
4. 获取更多带动作序号的客户端快照，继续定位两个随机效果分岔。
5. 解出 `BalancedCards` 表，修正 `_bal` / `_vet` 变体数值。
6. 内核规则稳定后再生成训练数据、训练 NN，并用 `AotProbe` 验证宿主集成。
