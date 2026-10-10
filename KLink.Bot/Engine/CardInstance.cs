using KLink.Bot.Cards;

namespace KLink.Bot.Engine;

/// <summary>
/// 对局中的一张卡。字段命名尽量贴近协议与反编译产物，方便和真实回放对齐。
///
/// 协议里的卡对象只有 5 个字段（card_id / is_gold / location / location_number / name），
/// 其余全部是内核自己维护的运行时状态。
/// </summary>
public sealed class CardInstance
{
    /// <summary>协议里的 cardID。左侧 HQ=1、首张牌=2 起；右侧 HQ=41、首张牌=42 起。</summary>
    public required int CardId { get; init; }

    /// <summary>
    /// 卡名。
    ///
    /// ⚠️ 是 <c>set</c> 而不是 <c>init</c> —— 只为了 <see cref="Reidentify"/>
    /// 那条「身份校正」路径（见那边的长注释）。
    /// **不要**在别处直接赋值：身份变更必须整组一起改（名字 + 定义 + 基础数值 + 关键字），
    /// 单独改这一个字段会让卡处于自相矛盾的状态。正常建卡仍走对象初始化器。
    /// </summary>
    public required string Name { get; set; }

    /// <summary>当前控制方。夺取控制权时会改变；卡的原始归属见 <see cref="OriginalOwner"/>。</summary>
    public required Side Owner { get; set; }

    /// <summary>
    /// 创建时的原始归属（蓝图 <c>originalSide</c>）。
    /// 旧的手工测试卡没有填这个字段时，读取方会回退到当前归属。
    /// </summary>
    public Side OriginalOwner { get; init; } = Side.NotAvailable;

    /// <summary>当前是否由原始敌方控制（蓝图 <c>underEnemyControl</c>）。</summary>
    public bool UnderEnemyControl { get; set; }

    /// <summary>卡面定义。同 <see cref="Name"/>：可被 <see cref="Reidentify"/> 就地换掉。</summary>
    public required CardDefinition Definition { get; set; }

    public bool IsGold { get; set; }

    // ---- 位置 ----
    public CardLocation Location { get; set; }
    public int LocationNumber { get; set; }

    /// <summary>
    /// 这张卡最近一次从手牌触发时收到的目标。
    /// 蓝图的 <c>OnPlayedFromHand</c> 延迟调用会从 queued card 的
    /// <c>currentTarget</c> 读取它，而不是重新推导目标。
    /// </summary>
    public CardInstance? CurrentTarget { get; set; }

    // ---- 数值（可被效果修改，所以与 Definition 分开）----
    public int Attack { get; set; }
    public int Defense { get; set; }
    public int MaxDefense { get; set; }
    public int KreditCost { get; set; }
    public int OperationCost { get; set; }

    // ---- 回合内状态 ----
    public int EnteredPlayOnTurn { get; set; } = -1;
    public int OperationsUsedThisTurn { get; set; }
    public bool HasAttackedThisTurn { get; set; }

    /// <summary>
    /// `gotchaActivated` —— **反制卡（Gotcha）的激活序号**，`0` = 未激活。
    ///
    /// ## 权威（蓝图，逐行）
    ///
    /// 写入方**只有** `BP_CardFunctions::PlayCardDirectlyFromHand`
    /// （`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:28092-28316`）：
    /// <code>
    /// :28080  card.location == 4 || card.location == 3     ; 在手牌里
    /// :28088  IsGotcha(card)
    /// :28092  card.gotchaActivated &gt; 0                     ; 已经激活过 ⇒
    /// :28096      card.gotchaActivated = 0                 ;   置 0（正常打出 ⇒ 不再是反制）
    /// :28243  否则：遍历 GetAllCardInBattle()，取**同阵营**且 IsGotcha 的卡里
    /// :28263      gotchaActivated 的**最大值**  → _nextGotchaActivated
    /// :28310  _nextGotchaActivated += 1
    /// :28316  card.gotchaActivated = _nextGotchaActivated
    /// </code>
    /// ⇒ 语义是「**本方第几张被激活的反制卡**」，**从 1 开始递增**（不是 bool）。
    ///
    /// ## 为什么必须是 int 而不是 bool（审计：某个 PR 用了 bool 置 true）
    ///
    /// 判据方读的是 **`&gt; 0`**，而且顺序键就是它的**数值**
    /// （`GetActiveGotchasOrdered`，`BP_CardFunctions.g.cs:18623-18892`）：
    /// <code>
    /// :18733  item.gotchaActivated &gt; 0                    ; 只收激活过的
    /// :18766  activeGotchas.Add(cardID * -1, cardID)      ; interception 键 = -cardID
    /// :18794  activeGotchas.Add((cardID + 1000000) * -1, cardID) ; ultra 键
    /// :18810  Map_Find(activeGotchas, gotchaActivated)    ; backup 用激活号做键
    /// :18854  activeGotchas.Add(backUpNextGotcha, cardID) ; backup 键 = 100,101,…
    /// :18874  activeGotchas.Add(gotchaActivated, cardID)  ; 其余按激活号
    /// :18699  MinOfIntArray(keys) 逐个取最小 ⇒ 输出顺序由这些键升序决定
    /// </code>
    /// 用 bool 的话 `gotchaActivated` 只有 0/1，`GetActiveGotchasOrdered` 的
    /// 「第二张反制卡」就与第一张同号 ⇒ 顺序与客户端不一致。
    ///
    /// ## 复位
    ///
    /// `GotchaTriggered` 的**第一条**语句就是 `TmpGotcha.gotchaActivated = 0`
    /// （`:23735`）—— 触发过的反制卡不再处于激活态。
    /// </summary>
    public int GotchaActivated { get; set; }

    /// <summary>
    /// `cardSeen` —— 「这张手牌已经被**情报（Intel / Cipher）**揭示过了」。
    ///
    /// 写入方（蓝图全量，只有两处）：
    /// <list type="bullet">
    /// <item>`ApplySetCardsSeenByCipher` `BP_CardFunctions.g.cs:4048`
    ///   （`cardSeen = True`，只对 `IsLocatedInHand` 的卡）；</item>
    /// <item>`SetCardSeen` `:34013`（单张版本）。</item>
    /// </list>
    /// 读取方：`SetCardsSeenByCipher` 自己攒「未见面」候选时的 `:34137`
    /// （`if (cardSeen) 跳过`），以及 11 张卡的 IR 体。
    ///
    /// ⚠️ **IR 侧读不到这个成员**：成员表在
    /// `Effects/Blueprint/KismetVm.cs:961-1038`，本轮**不在改动范围内**
    /// ⇒ 那 11 张卡读 `cardSeen` 仍然得到 `null`（判假）。
    /// 这个字段是为**C# 侧语义正确**（`SetCardsSeenByCipher` 的"未见面"过滤）
    /// 与自测可观测而加的，如实标注这处**未接线**。
    /// </summary>
    public bool CardSeen { get; set; }

    /// <summary>蓝图 `isRevealed`：Covert 卡公开后持续保留的状态。</summary>
    public bool IsRevealed { get; set; }

    /// <summary>蓝图 `isSalvaged`：由 Salvage 生成的手牌复制品标记。</summary>
    public bool IsSalvaged { get; set; }

    /// <summary>蓝图 `salvageFaction`：被打捞原卡的阵营名称。</summary>
    public string? SalvageFaction { get; set; }

    /// <summary>蓝图 `salvagedCardID`：被打捞原卡的 cardID。</summary>
    public int SalvagedCardId { get; set; }

    /// <summary>
    /// `cipher` —— 卡的**情报值**（`AddIntelToCard` 写、`SetCardsSeenByCipher` /
    /// `GotchaTriggered` 读）。蓝图出处见 <see cref="Effects.CardApi.AddIntelToCard"/>。
    ///
    /// ⚠️ **CDO 初值缺失**：客户端把它存在卡 CDO 上
    /// （`decompiled/cards.all.json` 里 25 张卡非 0，例 `card_event_espionage_skirm`=9），
    /// 但本内核的卡数据（`docs/cards.live.json` / `docs/card-effects.json`）
    /// **不含该字段**，补表要动 `Cards/CardVarDefaults.cs`（本轮不在改动范围内）
    /// ⇒ 这里初值一律 **0**（偏小）。52 张 gotcha 卡的 CDO 里**本来就没有** `cipher`
    /// （已核实）⇒ 对这一族无影响。
    /// </summary>
    public int Cipher { get; set; }

    /// <summary>
    /// 本回合**已经攻击过几次**（蓝图 `UBaseCardObject::attackCountThisTurn`，
    /// 声明见 `<kards-src>\Source\kards\Public\BaseCardObject.h:256`）。
    ///
    /// ⚠️ 为什么光有 <see cref="HasAttackedThisTurn"/> 不够（2026-10-02 对局 389594 修）：
    /// **奋战（Fury）的单位一回合可以攻击两次**（规则表 `KARDS基础规则参考.md:107`
    /// 「**奋战**：一回合中可以**攻击两次**。」），
    /// 而 `HasAttackedThisTurn` 是布尔，第一次攻击之后就恒 True ⇒ 第二次永远被拒。
    ///
    /// 蓝图的真实形状是**两个字段一起用**：
    /// <list type="bullet">
    /// <item>`DoOnStartOfTurn`（`ref/kards-sim/.../_deps/BP_Logic.g.cs:2647-2655`）
    ///   每回合开始把 `hasAttackedThisTurn=False`、`attackCountThisTurn=0`；
    ///   同一函数 i=3104/3110/3124 再把 `movementLeft=1`、
    ///   `attackLeft = getHasFury() ? 2 : 1`。</item>
    /// <item>`SetAttackerHasAttacked`（`.../BP_CardFunctions.g.cs:33919-33957`）
    ///   攻击时 `attackLeft -= 1`、`hasAttackedThisTurn=True`、`attackCountThisTurn += 1`。</item>
    /// <item>`cardsCheckFunctions::CanAttack`（`.../_deps/cardsCheckFunctions.g.cs:761-805`）
    ///   的门是 **`HasAttackLeft(attackerCard)`**：为真直接放行；
    ///   为假才去读 `attackCountThisTurn` 决定失败原因是 `has_already_attacked`
    ///   还是 `no_attack_left`。⇒ **真正的额度是 `attackLeft`，不是那个布尔。**</item>
    /// </list>
    ///
    /// 实测证据（本局的 ⑤b 就是它）：对局 `389594` `#85/#86 t19 AC` 人类用
    /// `card_unit_queens_own`（卡面关键字 `Blitz / Deployment / Fury`，
    /// 见 `Cards/CardInnateTable.cs:122`）**同一回合对右 HQ 打了两次 7 点**
    /// （动作流 `94` 字段 19→12→5）。内核第二次报
    /// 「攻击者本回合不能行动（…已攻击=True…）」⇒ 动作流少应用一条。
    /// </summary>
    public int AttacksThisTurn { get; set; }

    /// <summary>
    /// Whether this unit has already been attacked during the current turn.
    /// `CalculateDamageDealt` uses this to decide whether Ambush is live.
    /// </summary>
    public bool HasBeenAttackedThisTurn { get; set; }

    /// <summary>
    /// 本回合最多能攻击几次 —— 蓝图 `DoOnStartOfTurn` 给 `attackLeft` 赋的**初值**
    /// （`BP_Logic.g.cs:3110` / `:3124`）。
    ///
    /// 用「按当前关键字**现算**」而不是「回合开始时存一个数」，是照蓝图的两条行为：
    /// <list type="bullet">
    /// <item>`GiveFury`（`BP_CardFunctions.g.cs:22691-22721`）在 `hasFury` 置位后
    ///   `attackLeft += 1` —— 本回合中途获得奋战**立刻**多一次攻击额度。</item>
    /// <item>`RemoveFury`（同文件 `:30759-30769`）用
    ///   `Min(attackLeft, 1)` 把额度收回 —— 中途失去奋战就只剩 1 次。</item>
    /// </list>
    /// 现算正好等价于这两条：`AttacksThisTurn` 是「已用掉的次数」，
    /// 上限跟着关键字走，两个方向的动态增删都对。
    /// </summary>
    public int MaxAttacksThisTurn => Keywords.Contains(Keyword.Fury) ? 2 : 1;

    /// <summary>
    /// 本回合是否**还有攻击额度** —— 蓝图 `UBaseCardObject::HasAttackLeft(bool&)`
    /// （声明 `BaseCardObject.h:969`，函数体在 shipping 二进制里，`BaseCardObject.cpp`
    /// 只是空桩）。语义由上面三条证据钉死为 `attackLeft > 0`，
    /// 也就是本内核的 `AttacksThisTurn &lt; MaxAttacksThisTurn`。
    /// </summary>
    public bool HasAttackLeft => AttacksThisTurn < MaxAttacksThisTurn;

    /// <summary>
    /// 「三选一」卡（<c>Choose One</c>）选的是第几个分支（0/1）。
    ///
    /// 数据来源是**真实动作流**，不是猜的：<c>ZActionPlayCardFromHand</c> 的参数表里
    /// 有一个 <c>Int:chooseOneIndex</c> 槽（见 BP_OnlineMatch 的
    /// <c>AddSubActionPlayCardFromHand</c>），而客户端紧凑 <c>PC</c> 的 `3` 号槽
    /// 在 295 条 PC 里只有 1 条非 0 —— 那一条（634651 #122）打的正是
    /// <c>card_event_planned_attack</c>，卡面「Choose One - … OR …」、
    /// 蓝图里用 <c>WhichChooseOne</c> + <c>SwitchEnum(0/1)</c> 分支。
    /// 所以 `PC` 的 `3` 号槽就是 <c>chooseOneIndex</c>。
    ///
    /// 默认 0 与 kardsim 的 <c>EngineHost.ChooseOne = _ =&gt; 0</c> 一致。
    /// 老实现是 <c>Random.Next(2)</c>，会让回放里的分支选择和真实对局不一致。
    /// </summary>
    public int ChooseOne { get; set; }

    /// <summary>
    /// 本回合是否已移动过战线。
    ///
    /// ⚠️ 移动和攻击**是两笔分开的额度**，不是共用一个「每回合一次行动」。
    /// 判据（真实回放 310284）：同一张单位在同一回合里先 `ML` 再 `AC`
    /// —— A15 `ML 48→槽0` 紧接 A16 `AC 48 打 敌方HQ`；
    /// A25 `ML 77→槽0` 紧接 A26 `AC 77 打 36`。
    /// 原先用单一 `OperationsUsedThisTurn` 卡住，会让这些攻击全部被拒。
    /// </summary>
    public bool HasMovedThisTurn { get; set; }

    /// <summary>
    /// **召唤失调**：这张卡是不是"刚部署、本回合还不能动"。
    ///
    /// 判据（逐字来自蓝图，不是推断）：
    /// <code>
    /// HasDeploymentSickness(Card) =
    ///       Card.IsLocatedOnBoard()
    ///    &amp;&amp; (Card.enterPlayOnTurn == GetTurnNumber())
    ///    &amp;&amp; !Card.getHasBlitz()
    /// </code>
    /// 出处：
    /// - `BP_Logic::HasDeploymentSickness` i=0/23/64/93/194/232（具名版本）
    /// - `cardsCheckFunctions::CanAttack` i=1919-2108：失败原因写死
    ///   <c>failReason = "deployment_sickness"</c>（i=2108），
    ///   `BP_Logic::GetCantAttackText` i=1299 把它翻成
    ///   <c>text_Notifications.try_attack_deployment_sickness</c> ——
    ///   这是**客户端自带的正式失败原因**，不是我们发明的词
    /// - `BP_Logic::CanCardDoAnything` i=1352-1568：这段在**攻击检查（i=2608）
    ///   和移动检查（i=2817）之前**，命中就 `canIt = False` 直接 return
    ///   ⇒ **部署当回合攻击和移动都被挡**
    /// </summary>
    public bool HasDeploymentSickness(GameState state)
        => Location.IsBoard()
           && EnteredPlayOnTurn == state.Turn
           && !Keywords.Contains(Keyword.Blitz);

    /// <summary>
    /// 该卡本回合是否还能发起攻击。
    ///
    /// ⚠️ **有召唤失调** —— 这里曾经写着"没有召唤失调"，并用真实回放 310284 的
    /// 三条证据（A24 `PC 77` → A25 `ML` → A26 `AC`；A34 `PC 65` → A35 `AC`；
    /// A70 `PC 79` → A71 `ML`）来论证"打出的当回合就能动"。
    /// **那段论证是错的**：那三张卡 `m20_scout_car` / `7_schutzen` / `sd_kfz_10_38`
    /// 在 CDO 里 `hasBlitz` **全是 True** —— 它们恰恰是"Blitz 例外"的正面证据。
    /// 反向检查：非 Blitz 单位 14 例，出牌 → 首次 ML/AC 的间隔全部 ≥ 2 回合，零反例
    /// （`klink bot/tools/verify-summoning-sickness.py`）。
    /// </summary>
    public bool CanOperateThisTurn(GameState state)
        => AliveOnBoard                  // ★ 死的不能行动（2026-10-02 实测：AI 移动了死单位）
           // ⚠️ 2026-10-02（第二轮）：这里原先有 `&& !IsSuppressed` —— **已删，那是误读**。
           //    中文客户端把 `Pin` 译作「压制」、`Suppress` 译作「抑制」，是两个关键字：
           //     · 压制（Pin）= 不能移动/攻击，于所有者下回合结束移除 ⇒ 门在
           //       `MatchEngine.PinnedBlocksOperation`（保留，权威规则表 159-160 讲的是它）；
           //     · 抑制（Suppress）= 失去所有关键词与效果（见 `CardApi.SuppressUnit`），
           //       **不禁行动**。蓝图证据：`cardsCheckFunctions::CanAttack`
           //       （210 条语句）与 `BP_Logic::CanCardDoAnything` 里 `isSuppressed`
           //       出现 **0 次**；该标志只出现在事件广播/数值改动的分流上
           //       （见 `CardApi.FireTrigger` si=325/710、`ExecuteBeforeReceiveDamage` si=38）。
           //    玩家（雪雾）原话：「抑制使被抑制的单位失去所有特效和关键字」——
           //    里面**没有**「不能行动」这一条。
           // ★★ **攻击额度**，不是「本回合没攻击过」那个布尔 —— 奋战可以攻击两次。
           //   判据与出处见 `HasAttackLeft` / `AttacksThisTurn` 的注释
           //   （蓝图门是 `HasAttackLeft(attackerCard)`，额度 `attackLeft` 回合开始
           //   被设成 `getHasFury() ? 2 : 1`）。实测对局 389594 `#85/#86 t19`。
           && HasAttackLeft
           // ★ 移动过就不能再攻击 —— **除非是坦克**。
           //   出处 `KARDS基础规则参考.md` 兵种表：
           //     「**坦克**：能在同一回合移动并攻击（一次移动 + 一次攻击，顺序任意）」
           //   表里**只有坦克**有这条例外；步兵/炮兵/战斗机/轰炸机都没写
           //   ⇒ 它们都是「移动**或**攻击，二选一」。
           //   （实测 2026-10-02：AI 的步兵部署后立刻移动并攻击，就是缺这条。）
           //   蓝图同结论：`MoveCardToFrontline`（`BP_CardFunctions.g.cs:26978-27043`）
           //   对**不是** `CanMoveAndAttackInTheSameTurn` 的单位把 `attackLeft` 直接置 0
           //   （而那个自定义能力正是 `MakeCountAsTank` 给的，同文件 `:26070`）。
           && (IsTank || !HasMovedThisTurn)
           && !HasDeploymentSickness(state);

    /// <summary>
    /// 该卡本回合是否还能移动战线（同样受召唤失调限制，见
    /// <see cref="HasDeploymentSickness"/> 里 `CanCardDoAnything` 的出处）。
    ///
    /// ★ 与 <see cref="CanOperateThisTurn"/> 对称：**攻击过就不能再移动，除非是坦克**。
    /// </summary>
    public bool CanMoveThisTurn(GameState state)
        => AliveOnBoard
           // ⚠️ 同上：原先这里也有 `!IsSuppressed`，2026-10-02（第二轮）删除。
           //    移动侧的蓝图门是 `BP_Logic::CanCardDoAnything`（i=198 是 `IsPinned`），
           //    里面**没有** `isSuppressed`（全文件 0 次）。
           && !HasMovedThisTurn
           && (IsTank || !HasAttackedThisTurn)
           && !HasDeploymentSickness(state);

    /// <summary>
    /// 被**抑制**（客户端中文译名；英文关键字 = `Suppress`，字段 = 卡对象上的
    /// `isSuppressed`）。
    ///
    /// ⚠️⚠️ **它不是「不能行动」** —— 那是**压制**（`Pin` / `Pinned` / `pinnedTurns`）。
    /// 中文客户端把这两个不同的关键字分别译作「抑制」与「压制」，
    /// 内核一度把两者当成同一件事（`Attack`/`MoveUnit` 里判 `Suppressed` 并写进自测），
    /// 那是**误读**：规则表 159-160 讲的是压制。
    ///
    /// 抑制的语义 = **失去所有关键词与所有增益效果**（+ 老兵变回普通形态），
    /// 实现体见 <see cref="KLink.Bot.Effects.CardApi.SuppressUnit"/>
    /// （蓝图 `BP_CardFunctions::SuppressMultipleUnits`）。玩家（雪雾）权威定义：
    /// 「抑制：使被抑制的单位失去所有特效和关键字；压制不会受到抑制的影响；
    ///   被抑制的单位会失去所有特效；老兵也会变回原来的；所有的增益效果也全部失效
    ///   ——包括友方贴膜、敌方贴膜、友方卡牌给单位添加的额外特效。」
    ///
    /// 本标志在蓝图里**只被写 True、从无一处写 False**（全库唯一写点
    /// `BP_CardFunctions.g.cs:35781`；`SetMember(..., "isSuppressed", …)` 全文搜不到 `False`），
    /// 它的作用是在各处**分流**：被抑制的卡不广播事件、不吃伤害修正、不能变老兵。
    ///
    /// ★★ 2026-10-02（第三轮）：内核原先自己加过一条「到期解除」
    /// （`MatchEngine.ClearExpiredSuppression`）—— **已整条删除**。玩家（雪雾）确认
    /// 「解除时机：**【永不解除】** —— 一直白板到游戏结束」，蓝图也**没有任何**写 `False` 的地方
    /// ⇒ 本标志一旦为真就**保持到游戏结束**（与客户端一致）。见 <see cref="SuppressedOnTurn"/>。
    /// </summary>
    public bool IsSuppressed => Keywords.Contains(Keyword.Suppressed);

    /// <summary>
    /// 被抑制时仍允许接收的触发程序名。
    ///
    /// 蓝图的全局派发门是
    /// <c>isSuppressed &amp;&amp; !suppressionExceptionTriggers.Contains(trigger)</c>；
    /// 当前卡数据没有可加载的白名单来源，因此默认为空，保留一个可扩展的
    /// 运行时集合，待取得逐卡证据后再填充。
    /// </summary>
    public HashSet<string> SuppressionExceptionTriggers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 被**抑制**时的回合号 —— **仅作诊断留痕**（2026-10-02 第三轮改写）。
    ///
    /// ⚠️⚠️ **蓝图里没有这个东西，别把它当成客户端行为。**
    /// `isSuppressed` 在整个反编译产物里**只有一处写点**（`:35781` 写 `True`），
    /// **没有任何一处写 `False`**；`KARDS基础规则参考.md` 也只给**压制**写了
    /// 移除时机（`:160`「压制效果于单位所有者下个回合结束时移除」），
    /// 抑制那一节（`:123-125`）**没有**时机。玩家（雪雾）对此的权威定义是：
    /// 「解除时机：**【永不解除】** —— 一直白板到游戏结束。」
    ///
    /// ⇒ 内核原先据此发明过一条「抑制于所有者下回合结束时解除」
    /// （`MatchEngine.ClearExpiredSuppression`），**2026-10-02（第三轮）已整条删除**：
    /// 抑制永不解除 ⇒ 本字段**不会再被复位**，用途只剩"记录这张卡是什么时候被抑制的"
    /// （回放/诊断）。`-1` = 从未被抑制。
    ///
    /// ⚠️ 不要因为看到这个字段就以为还有一条到期路径：唯一的复位点
    /// `CardApi.RestoreAfterSuppression` 现在**没有产品调用方**（不可达，
    /// 见那边的注释）。自测 `SuppressionNeverExpires` 断言它跨 6 次回合结束**保持不变**。
    /// </summary>
    public int SuppressedOnTurn { get; set; } = -1;

    /// <summary>
    /// 被抑制时**摘掉**的关键词（供解除时还原）—— 对应蓝图的
    /// `customJson.suppressionException`（`BP_CardFunctions.g.cs:35946-35960`：
    /// 抑制前把 `customJson` 备份进 `suppressionException` 字段，解除时再写回去）。
    ///
    /// ⚠️ 为什么必须记：`SuppressUnit` 会摘 `Guard`/`Blitz`/`Fury`/`HeavyArmor`
    /// 等（见 `CardApi.SuppressStrips`）。**只摘不还原** = 单位被抑制一次就永久变白板。
    /// 由 `CardApi.RestoreAfterSuppression` 逐条加回。
    ///
    /// `null` = 从未被抑制过。
    /// </summary>
    public List<string>? SuppressStrippedKeywords { get; set; }

    /// <summary>
    /// 被抑制时**摘掉的攻/防/重甲/行动费 buff**（原文深拷贝，供解除时装回）。
    ///
    /// 对应蓝图那两条"洗掉增益"的调用
    /// （`BP_CardFunctions.g.cs:36076` 与 `:36190/:36194`）：
    /// <code>
    /// i=xxx  ChangeBuffsFromCards(card, 0, giverID, buffType=5, changeType=7 /*customRemove*/, givingCardName)
    ///        ; buffType 5 = 卡牌给的能力（trigger/destruction/passive/lethal/custom 五类）
    /// L_1533 ChangeBuffsFromCards(card, 0, instigatorID, 1 /*Defense*/, 3 /*Suppress*/, "")
    /// L_156C ChangeBuffsFromCards(card, 0, instigatorID, 0 /*Attack*/,  3 /*Suppress*/, "")
    /// </code>
    /// `buffType` 的编号取自 `ChangeDefense`/`ChangeAttack` 自己的调用
    /// （`:7613` 用 1 = 防御、`:6561` 用 0 = 攻击），
    /// 关键字那一族（`GiveGuard`/`RemoveSalvage` …）用的是 6 + `changeType=8 (combatModify)`
    /// + 关键字名（如 `:32105` `…, 6, 8, "salvage"`）。
    ///
    /// 玩家原话：「**所有的增益效果也全部失效** —— 包括友方贴膜、敌方贴膜、
    /// 友方卡牌给单位添加的额外特效」。内核的 `BuffsBySource` 就是"贴膜"的账本，
    /// 所以这一族条目在这里被摘走、并在解除时**按对称逆运算**装回。
    /// </summary>
    public List<KeyValuePair<(int SourceCardId, bool Temporary), CardBuff>>? SuppressStrippedBuffs { get; set; }

    /// <summary>
    /// 抑制把**卡面自带的重甲**清零了（蓝图 `L_070E`
    /// `ChangeHeavyArmor(card, 0, 0, changeType=3 /*Suppress*/, skipAction=true)`）。
    ///
    /// ⚠️ 为什么需要这个位：`Keyword.HeavyArmor` 是**派生**的
    /// （见 <see cref="RecalculateStats"/>：`HeavyArmor &gt; 0` 就挂上），
    /// 所以只把关键字摘掉、下次 `RecalculateStats()` 立刻又挂回来。
    /// 卡面重甲（`Definition.HeavyArmor`）必须能被**暂时遮蔽**，
    /// 解除时（`RestoreAfterSuppression`）复位。
    /// </summary>
    public bool HeavyArmorZeroedBySuppress { get; set; }

    /// <summary>被抑制时清掉的 `CustomAbility`（卡牌给单位添加的额外特效），解除时装回。</summary>
    public string? SuppressStrippedCustomAbility { get; set; }

    /// <summary>
    /// **钉住的剩余「回合结束次数」** —— 对应蓝图的 `card.pinnedTurns`
    /// （`BP_CardFunctions.g.cs:27852` `PinUnit` 写、`:31700` `RemovePin` 清、
    /// `_deps/BP_Logic.g.cs:2218` `DecrementPinnedTurnsEndTurn` 递减）。
    ///
    /// 写入（`CardApi.PinUnit`，照 `PinUnit` i=955）：
    /// <c>PinnedTurns = Max(PinnedTurns, IsSideActive(卡) ? 3 : 2)</c>。
    /// 递减：**每一个回合结束时**减 1，减到 1 的那次结束就解除
    /// （见 `MatchEngine.DecrementPinnedTurnsEndTurn`）。
    ///
    /// ⚠️ 为什么必须记这个（否则钉住就是永久瘫痪）：
    /// 内核原先**完全没有** `pinnedTurns`。`Attack` 早就有一道钉住门，
    /// 所以在补上 `MoveUnit` 的同一道门之前，"永不解除"只表现为**不能攻击**、
    /// 不容易被发现；补上移动门之后就变成**彻底冻住** —— 与压制那条漏
    /// （`SuppressUnit` 只加不删）是同一个坑，见 <see cref="SuppressedOnTurn"/>。
    ///
    /// 实证（回放 `389594`）：`card_event_monty` 在 t7 钉住右方单位
    /// （t7 行动方是 left ⇒ 初值 2 ⇒ 应于 t8 结束解除），内核记成永久 ⇒
    /// t9/t11/t13/t15 的 6 条 `ML` 被新门误拒，连锁把人类 t13 的攻击打成
    /// 「够不着（双方都在支援线）」（`out/_pinned-gate/after-389594.txt`）。
    /// `0` = 未钉住。
    /// </summary>
    public int PinnedTurns { get; set; }

    /// <summary>
    /// **在场上，而且还没死** —— 能不能行动就靠这个。
    ///
    /// ⚠️ 不要用 <see cref="IsAlive"/> 判"能不能行动"：那个属性只判
    /// **位置**（不在弃牌堆 / 未移除），而 `Defense` 掉到 0 的卡
    /// **在 `Destroy` 真正搬走它之前仍然是 `IsAlive = true` 且在场上**。
    /// 2026-10-02 实测：AI 移动了一个已经打死的单位，就是踩了这个。
    ///
    /// 死活的判据与 `MatchEngine.CheckDeaths` **完全一致**
    /// （`!IsHq && IsAlive && Location.IsBoard() && Defense <= 0` → 该销毁），
    /// 这里取它的**补集**，所以两道门永远不会互相矛盾。
    ///
    /// `IsHq` 必须排除：HQ 的防御会掉到 0（= 输），但它不是"死单位"。
    /// 位置卡不受影响 —— 卡库里 location 的 defense 全是正数（6..40）。
    /// </summary>
    public bool AliveOnBoard
        => IsAlive && Location.IsBoard() && (IsHq || Defense > 0);

    /// <summary>
    /// 是不是坦克。
    ///
    /// ⚠️ 判据是卡数据的 `type` 字段（`card_unit_panzer_ii_a` → `"tank"`）。
    /// 卡库里 type 的分布（2021 张）：
    /// `order` 732 / `infantry` 568 / `location` 180 / **`tank` 154** /
    /// `fighter` 144 / `bomber` 98 / `artillery` 60 / 其它 85。
    ///
    /// 为什么只认坦克：见 <see cref="CanOperateThisTurn"/> 的注释 ——
    /// 规则表里只有坦克写了"能在同一回合移动并攻击"。
    /// </summary>
    public bool IsTank
        => string.Equals(Definition.Type, "tank", StringComparison.OrdinalIgnoreCase);

    // ---- 关键字 ----
    public HashSet<string> Keywords { get; } = new(StringComparer.Ordinal);

    /// <summary>自定义能力（对应子动作 ActionCustomAbilityAdd/Remove 与 CustomAbilityAdd 调用）。</summary>
    public string? CustomAbility { get; set; }

    /// <summary>
    /// 蓝图的 `KreditsTax_AsEnemyTarget` —— **被敌方指定为目标时要多付的费用**。
    ///
    /// 写方：`BP_CardFunctions.AddKreditsTax`（`_deps/BP_CardFunctions.g.cs:403-438`，
    /// 语义是 `Max(0, 当前值 + costToAdd)`）。全卡池 3 张卡调用它：
    /// `card_event_order_of_the_day`(+1) / `card_event_grim_day`(+2 / −2) /
    /// `card_unit_tupolev_sb_2`(+2 / −2)。
    ///
    /// 读方：`cardsCheckFunctions.CanSelectAsTarget`（`g.cs:1142`）——
    ///   `tax = SelectInt(0, KreditsTax_AsEnemyTarget, Targeting.side == Targeted.side)`
    ///   （`SelectX(A, B, cond)` = `cond ? A : B`，所以**同阵营不付税**）；
    ///   余下 kredit 不够付税就回 `cost_extra_to_target`（`g.cs:1184`）。
    ///
    /// ⚠️ 2026-10-02 之前内核**没有这个字段**，`AddKreditsTax` 也不在派发表里
    /// （IR 里 3 个调用点 → 计入缺口）。后果是 `CanSelectAsTarget` 的那条分支
    /// **永远不可能触发**（读一个不存在的字段）—— 门写了也等于没写。
    /// </summary>
    public int KreditsTaxAsEnemyTarget { get; set; }

    /// <summary>卡牌私有 JSON 暂存（对应游戏里的 JSON_Get*/JSON_Set*/JSON_Clear 一族调用）。</summary>
    public Dictionary<string, string> CustomJson { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Blueprint TSet members that must survive separate event/function VM
    /// frames.  Values remain object-typed because the generated IR omits the
    /// native element type (the gameplay member currently stores card refs).
    /// </summary>
    public Dictionary<string, HashSet<object?>> BlueprintSets { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// buff 记录：(来源卡, 是否临时) → 该来源施加的修正。
    /// 用于 `RemoveTheBuff` / `checkAndUpdateBuffOnCard` / 回合结束清理。
    ///
    /// ⚠️ **键里必须带 `Temporary`**（2026-10-01 自测抓到的 bug）：
    /// 原先键只有来源卡，于是「同一张卡先给 +3 直到回合结束、再给 +2 永久」时
    /// 两次修正挤进**同一个** <see cref="CardBuff"/> 条目，条目带上了 `Temporary=true`；
    /// 回合结束清理把它整个删掉，**那 2 点永久加成跟着一起没了**。
    /// 蓝图里这是两笔独立的 buff（`AddBuffsToRemoveEndOfTurn` 登记的只是临时那一笔），
    /// 所以拆成两条。
    /// </summary>
    public Dictionary<(int SourceCardId, bool Temporary), CardBuff> BuffsBySource { get; } = new();

    /// <summary>
    /// 是否为 HQ 卡（`card_location_*`，类型 <c>location</c>）。
    ///
    /// ⚠️ **判据必须是"卡的类型"，不能是"卡在哪个位置"。**
    /// 这里曾经写的是 `Location is BoardHqLeft or BoardHqRight` ——
    /// 那个写法在"所有单位都塞前线（7）"的旧模型下碰巧能用，
    /// 但真实模型里**半场就是 5/6**（蓝图 `GetSupportLineLocationBySide`：
    /// side1→5、side2→6；HQ 和单位同处一格，HQ 占 1 格容量）。
    /// 按位置判会让**每一个部署到半场的单位都被当成 HQ**：
    /// 被 `Board()` 过滤掉、被 `LegalTargets` 挡掉、被 `CheckDeaths` 跳过。
    /// </summary>
    public bool IsHq => Definition.IsLocationCard;

    /// <summary>这张卡是不是在**自己那侧的半场**（含 HQ 占的那一格所在的区域）。</summary>
    public bool IsInOwnHalf => Location == Owner.HqOf();

    /// <summary>
    /// 重甲点数 —— 基础值（卡面自带）加上所有来源的临时加成。
    ///
    /// 为什么要单独算而不能只看 <see cref="Keywords"/>：`ChangeHeavyArmor(卡, 来源, +1)`
    /// 是可以叠加的数值（客户端另有 `getTotalHeavyArmor`），关键字只能表示「有没有」。
    /// 关键字仍然同步维护（<c>HeavyArmor</c> 出现 ⟺ 点数 &gt; 0），
    /// 因为快照对拍、`AddHeavyArmor` 那条老路径都按关键字判。
    /// </summary>
    public int HeavyArmor
    {
        get
        {
            // ⚠️ 抑制会把**卡面自带**的重甲也清零（蓝图 `L_070E`
            //    `ChangeHeavyArmor(卡, 0, 0, Suppress(3), skipAction=true)`），
            //    所以卡面那一项要能被 <see cref="HeavyArmorZeroedBySuppress"/> 遮蔽。
            int total = HeavyArmorZeroedBySuppress ? 0 : Definition.HeavyArmor;
            foreach (var buff in BuffsBySource.Values)
            {
                total += buff.HeavyArmor;
            }

            return total;
        }
    }

    /// <summary>
    /// 建卡时灌入**卡面自带**的关键字（Blitz / Guard / Ambush / Fury / Smokescreen …），
    /// 然后把派生数值算一遍。
    ///
    /// ⚠️ 这一步以前根本没有 —— `CardInstance.Keywords` 建出来永远是空集，
    /// 于是 **205 张天生 Blitz、128 张 Guard 全部不生效**。
    /// 直接后果就是召唤失调没法正确实现（判据里的 `!getHasBlitz()` 读不到，
    /// 加了规则反而会误伤所有 Blitz 单位），以及 `MatchEngine.LegalTargets`
    /// 的嘲讽判定失效。数据来源见 `CardDefinition.Keywords` 的注释
    /// （pak CDO 抽的 `CardInnateTable`，不是 `cards.live.json`）。
    /// </summary>
    public void InitializeFromDefinition()
    {
        foreach (string keyword in Definition.Keywords)
        {
            Keywords.Add(keyword);
        }

        RecalculateStats();
    }

    /// <summary>
    /// **就地改身份** —— 把这张卡换成另一张卡（卡名 / 定义 / 基础数值 / 卡面关键字）。
    ///
    /// ## 为什么需要它（2026-10-02，「效果随机/复制出来的卡与客户端不一致」这一类）
    ///
    /// 锁步模型下**效果是各客户端本地结算的**，所以随机/复制类的效果
    /// （`card_event_atlantic_convoy` 的「Add one random US unit with cost 3 or less…」、
    /// `card_event_seac` 的「Duplicate it」…）在本内核里抽到的那一张
    /// **与官方客户端抽到的那一张不是同一张** —— 内核的 RNG 由
    /// `ReplayRunner` 的 `seed:(ulong)replay.MatchId` 决定，客户端的是它自己的流。
    ///
    /// 但**客户端会在后续动作里用「卡组码」把它真实选中的那张告诉我们**：
    /// 每个 `PC`/`ML`/`AC` 的 `action_data` 都带被引用卡的卡组码
    /// （见 `WireAction.CardCodes` / `KeyIndex.CodeSlotA`）。
    /// 实测 508065 `#44 ML {"0":"9002","1":"0","2":"DB"}` —— 内核把 9002 建成了
    /// `card_unit_1st_infantry_regiment_us`（油费 3），而码 `DB` = `card_unit_fifth_ohio`
    /// （油费 1）；多算的 2 点油费把 t11 的 kredit 池从 7 挤成 2，
    /// 于是 `#46` 那张 3 费牌「打不出：kredit 不足」。
    ///
    /// 所以这里不是"重新抽一次"，而是**把已经存在的那张卡就地校正成客户端说的那张**。
    ///
    /// ## 为什么基础数值按**增量**迁移
    ///
    /// 校正发生在"卡已生成、但被后续动作引用"的时刻，中间可能已经吃过 buff 或伤害。
    /// 直接把 `Attack` 设成新卡面值会把那些修正抹掉；只搬差值则
    /// **既换成新卡面的底子、又保住已经施加的修正**（buff 存在
    /// <see cref="BuffsBySource"/> 里，是相对**卡面**的偏移量，所以不受影响）。
    ///
    /// ## 为什么 `Keywords` 要撤旧的、灌新的
    ///
    /// <see cref="Keywords"/> 里混着三类：卡面自带（`Definition.Keywords`）、
    /// buff 派生的（`HeavyArmor`，由 <see cref="RecalculateStats"/> 维护）、
    /// 以及效果直接加的（`Suppressed` 等）。只换掉**卡面自带**那一类，其余不动。
    /// </summary>
    public void Reidentify(string name, CardDefinition def)
    {
        CardDefinition oldDef = Definition;

        Name = name;
        Definition = def;

        // 基础数值：搬差值（保留 buff 与已受伤害）
        Attack += def.Attack - oldDef.Attack;
        Defense += def.Defense - oldDef.Defense;
        MaxDefense += def.Defense - oldDef.Defense;

        // 卡面关键字：撤掉旧卡面的，灌入新卡面的
        foreach (string kw in oldDef.Keywords)
        {
            Keywords.Remove(kw);
        }

        foreach (string kw in def.Keywords)
        {
            Keywords.Add(kw);
        }

        // 费用 / 重甲关键字按新卡面重算（纯函数：只看 Definition + BuffsBySource）
        RecalculateStats();
    }

    /// <summary>
    /// 把所有「基础值 + 各来源 buff」的派生数值重算一遍。
    ///
    /// ⚠️ **必须在每次改 buff 之后调用**，而且**必须是纯函数**（只看
    /// <see cref="Definition"/> 与 <see cref="BuffsBySource"/>，不累加当前值）。
    /// 光环类效果（`ApplyTheBuff`/`RemoveTheBuff`）会反复施加/撤销同一来源，
    /// 增量式写法第二次就会翻倍；绝对值重算天然幂等。
    /// 落点：<see cref="KreditCost"/>、<see cref="OperationCost"/>、重甲关键字。
    /// </summary>
    public void RecalculateStats()
    {
        KreditCost = EffectiveKreditCost;
        OperationCost = EffectiveOperationCost;

        if (HeavyArmor > 0)
        {
            Keywords.Add(Keyword.HeavyArmor);
        }
        else
        {
            Keywords.Remove(Keyword.HeavyArmor);
        }
    }

    /// <summary>
    /// 有效费用 = 卡面费用 + 各来源的改费之和，最低为 0。
    ///
    /// 费用下限是通用规则，不区分单位、指令或反制卡；普通减费也可以
    /// 把一张 1 费卡降到 0。卡面明确写「minimum of 1」的效果仍应由该
    /// 效果自己的蓝图逻辑施加额外限制，而不是由全局费用模型假定。
    /// </summary>
    public int EffectiveKreditCost
    {
        get
        {
            int total = Definition.Kredits;
            foreach (var buff in BuffsBySource.Values)
            {
                total += buff.KreditCost;
            }

            return Math.Max(MinKreditCost, total);
        }
    }

    /// <summary>所有卡牌的通用费用下限；卡面明确写 minimum of 1 的效果另行处理。</summary>
    public const int MinKreditCost = 0;

    /// <summary>有效行动费用 = 卡面行动费用 + 各来源的加减（下限 0）。</summary>
    public int EffectiveOperationCost
    {
        get
        {
            // Direct-set buffs replace the card value; relative buffs still
            // compose on top regardless of dictionary insertion order.
            var absolute = BuffsBySource.Values
                .Where(buff => buff.OperationCostSetsAbsoluteValue)
                .LastOrDefault();
            int total = absolute is null
                ? Definition.OperationCost
                : Definition.OperationCost + absolute.OperationCost;
            foreach (var buff in BuffsBySource.Values)
            {
                if (!buff.OperationCostSetsAbsoluteValue)
                {
                    total += buff.OperationCost;
                }
            }

            return Math.Max(0, total);
        }
    }

    public bool IsAlive => Location != CardLocation.Discard && Location != CardLocation.NotAvailable;

    public override string ToString()
        => $"[{CardId}]{Name}@{Location}#{LocationNumber}" + (Definition.IsUnit ? $" {Attack}/{Defense}" : "");

    public CardSnapshot Snapshot() => new(
        CardId, Name, Owner, Location, LocationNumber, IsGold,
        Attack, Defense, MaxDefense, KreditCost, OperationCost,
        EnteredPlayOnTurn, OperationsUsedThisTurn, HasAttackedThisTurn, HasMovedThisTurn,
        AttacksThisTurn,
        Keywords.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
        CustomAbility,
        CustomJson.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value))
        {
            HasBeenAttackedThisTurn = HasBeenAttackedThisTurn,
            PinnedTurns = PinnedTurns,
            IsRevealed = IsRevealed,
            IsSalvaged = IsSalvaged,
            SalvageFaction = SalvageFaction,
            SalvagedCardId = SalvagedCardId,
            BlueprintSets = BlueprintSets
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key,
                    kv => kv.Value.Select(v => v is CardInstance card ? card.CardId : v is int id ? id : 0)
                        .OrderBy(id => id).ToArray(), StringComparer.Ordinal),
        };
}

/// <summary>某个来源施加在卡上的持续修正。</summary>
public sealed class CardBuff
{
    public int SourceCardId { get; init; }
    public int Attack { get; set; }
    public int Defense { get; set; }

    /// <summary>
    /// 费用相对**卡面费用**的偏移量。
    ///
    /// ⚠️ 存的是偏移量而不是「改完之后是多少」：光环会反复 Apply/Remove，
    /// 存绝对值的话第二次 Apply 就没法判断该不该再减一次了。
    /// </summary>
    public int KreditCost { get; set; }

    /// <summary>行动费用相对**卡面行动费用**的偏移量。</summary>
    public int OperationCost { get; set; }

    /// <summary>该来源是否把行动费用直接设为某个绝对值。</summary>
    public bool OperationCostSetsAbsoluteValue { get; set; }

    /// <summary>重甲点数（可叠加的数值，不是布尔）。</summary>
    public int HeavyArmor { get; set; }

    /// <summary>
    /// 这个来源的改费是「显式设成绝对值」而不是「相对减费」。
    ///
    /// 判据：设值分支使用线上 `changeType=2/3`；手写 `committed_crew` 光环仍用
    /// 兼容标记保存其“设为 0”语义，而普通 `changeType=1` 是永久相对修正。
    /// 它决定 <see cref="CardInstance.EffectiveKreditCost"/> 的下限要不要放开到 0。
    /// </summary>
    public bool KreditCostSetsAbsoluteValue { get; set; }

    public int Duration { get; set; } = -1;   // -1 = 永久

    /// <summary>
    /// 这个来源的修正是「到回合结束为止」（<c>EChangeType::tempBuffGive = 0</c>），
    /// 会在该方回合结束时被 <see cref="CardApi.RemoveTemporaryBuffs"/> 撤掉。
    ///
    /// 出处（两处独立互证）：
    /// 1. `BP_CardFunctions.AddAttackUntilEndOfTurn` si=4 调
    ///    `ChangeAttack(card, instigatorID, attackToAdd, **changeType=0**, silent=False, out)`
    ///    —— 枚举值 0 逐字来自 `EChangeType.h`（见
    ///    <c>CardApiDispatch.ChangeTypeTempBuffGive</c>）
    /// 2. 同函数 si=5 紧接着调
    ///    `GameStateRef.AddBuffsToRemoveEndOfTurn(0 /*buffType*/, instigatorID)`
    ///    —— 把「这个来源的临时 buff」登记进待清理表，回合结束时由
    ///    `BP_CardFunctions.RemoveBuffsEndOfTurn`（71 条语句，遍历 buffType →
    ///    instigatorArray → `GetCardFromID` → `RemoveTheBuff`）清掉
    ///
    /// 旧实现里 `Attack`/`Defense` 的修正**没有这个位**，所以「+N 直到回合结束」
    /// 全部变成了永久 +N（`glossary` 里那一类卡的数值会越滚越高）。
    /// </summary>
    public bool Temporary { get; set; }

    public bool IsEmpty => Attack == 0 && Defense == 0 && KreditCost == 0
                           && OperationCost == 0 && HeavyArmor == 0
                           && !KreditCostSetsAbsoluteValue
                           && !OperationCostSetsAbsoluteValue;

    /// <summary>
    /// 深拷贝 —— `CardApi.SuppressUnit` 要把这条 buff 整个摘走、并在解除时原样装回，
    /// 所以必须留一份**不会被后续改动影响**的副本（抑制期间同一来源可能再施加 buff，
    /// 若存引用就会被那份新数据污染，装回时翻倍）。
    /// </summary>
    public CardBuff Clone() => new()
    {
        SourceCardId = SourceCardId,
        Attack = Attack,
        Defense = Defense,
        KreditCost = KreditCost,
        OperationCost = OperationCost,
        HeavyArmor = HeavyArmor,
        KreditCostSetsAbsoluteValue = KreditCostSetsAbsoluteValue,
        OperationCostSetsAbsoluteValue = OperationCostSetsAbsoluteValue,
        Duration = Duration,
        Temporary = Temporary,
    };
}

/// <summary>某一时刻的卡状态快照 —— 用于和客户端逐步对拍。</summary>
public sealed record CardSnapshot(
    int CardId,
    string Name,
    Side Owner,
    CardLocation Location,
    int LocationNumber,
    bool IsGold,
    int Attack,
    int Defense,
    int MaxDefense,
    int KreditCost,
    int OperationCost,
    int EnteredPlayOnTurn,
    int OperationsUsedThisTurn,
    bool HasAttackedThisTurn,
    bool HasMovedThisTurn,
    /// <summary>本回合已攻击次数（蓝图 `attackCountThisTurn`）—— 奋战的第二次攻击靠它。</summary>
    int AttacksThisTurn,
    string[] Keywords,
    string? CustomAbility,
    Dictionary<string, string> CustomJson)
{
    public bool HasBeenAttackedThisTurn { get; init; }
    public int PinnedTurns { get; init; }
    public bool IsRevealed { get; init; }
    public bool IsSalvaged { get; init; }
    public string? SalvageFaction { get; init; }
    public int SalvagedCardId { get; init; }
    public Dictionary<string, int[]> BlueprintSets { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// 游戏里的关键字。名字取自反编译出的子动作名
/// （ZActionGive* / ZActionRemove* / ZActionAddHeavyArmor / ZActionMakeVeteran …）。
/// </summary>
public static class Keyword
{
    public const string Alpine = "Alpine";
    public const string Ambush = "Ambush";
    public const string Blitz = "Blitz";
    public const string Bond = "Bond";
    public const string Fury = "Fury";
    public const string Guard = "Guard";
    public const string Immune = "Immune";
    public const string Mobilize = "Mobilize";
    public const string Salvage = "Salvage";
    public const string Shock = "Shock";
    public const string Smokescreen = "Smokescreen";
    public const string HeavyArmor = "HeavyArmor";
    public const string Veteran = "Veteran";
    public const string Suppressed = "Suppressed";
    public const string Pinned = "Pinned";

    // ---- P1 新增（2026-09-30）----
    //
    // 下面 5 个是 CDO 里**本来就有**的 `has*` 字段，只是内核的 `Keyword` 集合一直没收录
    // （`gen-card-keywords.py` 的 `BOOL_FLAGS` 里注释写着"硬映射会编译不过，也不该为了
    // 这一步去扩关键字集"）。后果是 IR 里以**成员读**出现的 `hasDeployment` /
    // `hasDestruction` / `hasCovert` / `hasPincer` 全部读成 null → 判假（审计 §4.1 第 1 条）。
    //
    // 卡数（`out/cards-full2.json` 的 CDO）：hasDeployment 249 / hasDestruction 73 /
    // hasCovert 11 / hasPincer 15 / hasScrying 1。出现时**恒为 True**，缺席即默认 false。
    public const string Deployment = "Deployment";
    public const string Destruction = "Destruction";
    public const string Covert = "Covert";
    public const string Pincer = "Pincer";
    public const string Scrying = "Scrying";
}
