using KLink.Bot.Cards;
using KLink.Bot.Effects;

namespace KLink.Bot.Engine;

/// <summary>
/// 对局引擎主干：回合循环、部署、移动、攻击、胜负判定。
///
/// 设计约束（来自确定性锁步）：
/// - 所有随机走 <see cref="GameState.Random"/>
/// - 不依赖字典遍历顺序、时间、GUID
/// - 同一份输入 + 同一个种子 ⇒ 完全相同的输出
///
/// ⚠️ 尚未确定、需要真实回放确认的点（已在代码里标 TODO）：
/// - 前线/支援线的具体槽位编码（ECardLocationEnum 里只有一个 Board_Frontline）
/// - kredit 上限、疲劳公式、部署线容量
/// - 攻击结算顺序（反击、Guard、Smokescreen 的交互）
/// </summary>
public sealed class MatchEngine
{
    // A selection response occurs after the requesting Blueprint has returned.
    // Keep this order in headless play while resolving the choice synchronously.
    private Queue<Action>? _pendingSelections;
    private readonly Dictionary<int, Action> _waitPlayContinuations = new();

    internal void ResolveHandSelection(CardInstance selecting, Func<bool> resolve, bool updateExtraTriggers,
        bool defer = true, bool consumeAutoPlay = true)
    {
        void Complete()
        {
            if (!resolve()) return; // No reply is different from a reply selecting card ID 0.
            if (_waitPlayContinuations.Remove(selecting.CardId, out var continuation))
                continuation(); // BP_OnlineMatch: AfterWait before HandleExtraPlayTriggers.
            // Reply actions consume after their own callback / AfterWait, but
            // CardToDrawSelected i=1677 precedes HandleExtraPlayTriggers i=1737.
            if (consumeAutoPlay) ExecuteAutoPlayCards();
            if (updateExtraTriggers && selecting.ExtraPlayTriggers > 0)
            {
                selecting.ExtraPlayTriggers--;
                Api.RunCardEffect(selecting, null);
            }
            else if (updateExtraTriggers)
                Api.ResolveTriggerQueue();
            if (consumeAutoPlay) ApplyPendingGotchaPlayReceipts();
        }
        if (defer && _pendingSelections is { } pending) pending.Enqueue(Complete);
        else Complete();
    }

    private int _effectResolutionDepth;
    private bool _processingForcedEndTurn;
    private Side? _pendingForcedEndTurnSide;
    private int _pendingForcedEndTurnInstigator;

    /// <summary>HQ 初始防御。依据：fyserver 注入的 bot 动作里出现 <c>{"side":"right","75":"20"}</c>。</summary>
    public const int InitialHqDefense = 20;

    /// <summary>
    /// **自然增长**的指挥点槽上限 —— 每回合 +1，涨到 12 就停。
    ///
    /// 出处 `KARDS基础规则参考.md:27`：
    /// 「玩家每回合自然增加 **1 个指挥点槽，最高 12 个**。」
    /// </summary>
    public const int NaturalKreditCap = 12;

    /// <summary>
    /// **卡牌效果**能把指挥点槽抬到的硬上限（= 24）。
    ///
    /// 出处 `KARDS基础规则参考.md:29`：
    /// 「通过卡牌效果，**指挥点槽最多可提升至 24 个**，指挥点（费用）本身最高也可达 24 点。」
    ///
    /// ⚠️⚠️ **以前这里只有一个常量 `MaxKreditCap = 12`，而且 `StartTurn` 写成
    /// `Math.Min(MaxKreditCap, MaxKredits + 1)`** ——
    /// 那个写法把**卡牌效果已经抬上去的槽位又钳回 12**。
    ///
    /// 实测后果（雪雾 2026-10-01，对局 781364）：人类用
    /// `card_event_the_war_machine`（Gain 1 extra kredit slot）把槽位抬到 13，
    /// **下一个回合就被我们吃回去**；到第 21 回合真实槽位已 21，我们只算 12
    /// ⇒ 人类打得出、我们打不出 ⇒ **从 t21 起动作接连应用失败（到 t46 累积 29 条）**
    /// ⇒ 状态一路漂开 ⇒ 表现成「AI 移动已经死掉/被钉住的单位」。
    ///
    /// ⇒ **自然增长的上限（12）与卡牌效果的硬上限（24）是两个不同的数**，
    ///    不能用一个常量。
    /// </summary>
    public const int MaxKreditCap = 24;

    /// <summary>
    /// 支援线（半场）容量 —— **5 格，HQ 占其中 1 格 ⇒ 每边最多 4 个单位**。
    ///
    /// 见 <see cref="GameState.HalfBoardCapacity"/>（蓝图 `FetchCardsByLocation`
    /// case 5,6 → `MaxQty = 5`，且计数不排除 HQ）。
    /// 前线的 5 格是**另一个独立上限**，不与这里合计。
    /// </summary>
    public const int SupportLineCapacity = GameState.HalfBoardCapacity;

    /// <summary>半场里能放的单位数 = 总格数 − HQ 占的那 1 格。</summary>
    public const int HalfBoardUnitCapacity = GameState.HalfBoardCapacity - 1;

    /// <summary>先手 4 张、后手 5 张（与服务器 DeckCodeManager 的分牌一致）。</summary>
    private const int FirstPlayerHand = 4;
    private const int SecondPlayerHand = 5;

    private readonly CardDatabase _db;
    private int _actionId;

    public MatchEngine(CardDatabase database, IReadOnlyList<string> leftDeck, IReadOnlyList<string> rightDeck, ulong seed)
    {
        _db = database;
        State = new GameState(database, seed);
        LeftDeckList = leftDeck;
        RightDeckList = rightDeck;

        // ⚠️ 必须在构造函数里就建好 Api：`Start()` 只是「打一副新牌局」的便捷入口，
        //    回放驱动是直接手工摆盘的，不会走 `Start()`。以前 Api 只在 `Start()` 里赋值，
        //    导致回放路径上每个动作都在 `Api.FireTrigger` 上抛 NullReferenceException。
        Api = new CardApi(this);

        // 卡「换区」与「被创建」的钩子。
        //
        // 为什么挂在 GameState 上、而不是在每个调用点手工补一句：
        // 蓝图里这两个事件是从 `CardLocationMoved` / `CreateCardObject` 两个**唯一入口**
        // 派发的（见下面 FireLocationMoved / FireCardCreated 的出处注释），
        // 而内核的 `GameState.Move` / `GameState.Create` 正好是它们对应的唯一入口。
        // 挂在这里等于"每一处换区都发"，不会漏。
        //
        // 初始化牌组时尚未挂接这些钩子，该初始化生命周期仍待单独审计。
        State.CardMoved = FireLocationMoved;
        State.CardCreated = FireCardCreated;
        State.CardCreationModifiers = FireCardCreationModifiers;
    }

    /// <summary>
    /// 创建卡对象时先通知新卡自身，再通知订阅「其它卡被创建」的卡。
    /// `OnOtherCardCreatedAlterCard` 是独立的广播触发点（T35），不能依赖
    /// `OnCreateCard` 的主体派发自动覆盖。
    /// </summary>
    private void FireCardCreated(CardInstance card)
    {
        Api.FireTrigger("OnCreateCard", card, card.Owner);
    }

    private void FireCardCreationModifiers(CardInstance card)
        => Api.ExecuteCardCreationModifiers(card);

    /// <summary>
    /// 「某张卡换区了」—— 出处 `out/bp-cardfn.json` 函数
    /// `ExecuteOnCardLocationMoved(cardID, oldLocation, newLocation, changeOwner, moveReason)`：
    /// <code>
    /// i=38   cardToMove = GetCardFromID(cardID)
    /// i=89   JumpIfNot 469 (cardToMove.isSuppressed)   ; 未压制 ⇒ 跳去自己那一路
    /// i=125  FetchAllCardsWithEventTrigger(47)         ; 47 = OnOtherCardLocationMoved
    /// i=394  CallFunc_EqualEqual_IntInt(item.cardID, cardID)
    /// i=454  JumpIfNot 666                             ; 不等 ⇒ 走"别人"那一路
    /// i=515  cardToMove.OnCardLocationMoved(oldLocation, newLocation, ChangeOwner, MoveReason)
    /// i=771  item.OnOtherCardLocationMoved(cardToMove, oldLocation, newLocation, ChangeOwner, MoveReason)
    /// </code>
    /// 触发号 47 的依据：`ERegisteredCardFunction.h` 逐项数下来第 48 项 = `OnOtherCardLocationMoved`。
    /// 签名 `BaseCardObject.h:721/586`。
    ///
    /// `moveReason` 蓝图侧是 `/Game/Structs/CardMoveReason` 结构体，
    /// 事件桩里被 `GetEnumeratorUserFriendlyName` 转成 **String**（i=469/725）。
    /// 内核通过 <see cref="Effects.Blueprint.LiveRuleTables"/> 解析实时 pak
    /// 导出的枚举名，再把这个字符串传给订阅者；未知值才回退为空串。
    /// </summary>
    private void FireLocationMoved(CardInstance card, CardLocation oldLocation,
                                   CardLocation newLocation, bool changeOwner, CardMoveReason moveReason)
        => ExecuteLocationMovedEvents(card, oldLocation, newLocation, changeOwner, moveReason, true);

    internal void ExecuteLocationMovedEvents(CardInstance card, CardLocation oldLocation,
        CardLocation newLocation, bool changeOwner, CardMoveReason moveReason, bool manageLocation = false)
    {
        // CardLocationMoved i=578 invokes the event helper before removing smoke (i=735).
        ExecuteOnCardLocationMoved(card, oldLocation, newLocation, changeOwner, moveReason);

        // ---- 烟幕：移到**前线**就消失（P1，2026-09-30）----
        //
        // 出处 `out/bp-cardfn.json` → `CardLocationMoved`（61 条语句）：
        // <code>
        // si=638  PushExecutionFlow off=770
        // si=643  EqualEqual_ByteByte(newLocation, 7)          ; 7 = 前线
        // si=674  PopExecutionFlowIfNot(...)                    ; 不是 7 ⇒ 返回
        // si=684  tmpCard.getHasSmokescreen(out doesIt)
        // si=725  PopExecutionFlowIfNot(...)                    ; 没有烟幕 ⇒ 返回
        // si=735  RemoveSmokescreen(cardID, cardID, true, false)
        // </code>
        // 放在这里而不是 `MoveUnit` 里：`CardLocationMoved` 是**所有**移位路径的
        // 唯一入口（移动 / 生成落场 / 退却），蓝图也是在它里面判的。
        if (manageLocation && newLocation == CardLocation.BoardFrontline
            && card.HasSmokescreen)
        {
            if (State.ById(card.CardId) is { } current) Api.RemoveSmokescreen(current, card.CardId, true, false);
        }

        // CardLocationMoved i=775..987: T49 only runs from frontline to a
        // support line, excluding same-location owner changes and other zones.
        if (manageLocation && oldLocation == CardLocation.BoardFrontline
            && newLocation is CardLocation.BoardHqLeft or CardLocation.BoardHqRight)
        {
            ExecuteOnCardMoveFromFrontline(card);
        }

        // ---- 前线归属重算（P0，2026-10-01 实测 replay-214436）----
        //
        // 出处 `out/bp-cardfn.json` → `CardLocationMoved`（61 条语句）：
        // <code>
        // si=1011 NotEqual_ByteByte(moveReason, 13)
        // si=1042 EqualEqual_ByteByte(newLocation, 7)    ; 7 = 前线
        // si=1073 EqualEqual_ByteByte(oldLocation, 7)
        // si=1142 BooleanAND(BooleanOR(上面两个), moveReason != 13)
        // si=1180 PopExecutionFlowIfNot(...)             ; 不满足 ⇒ 整段跳过
        // si=1190 UpdateFrontlineIfNeeded(cardID)
        // </code>
        // ⇒ **任何**进出前线的换区都要重算归属，不只是 `MoveUnit`。
        //
        // ⚠️ 这就是用户实测「AI 空过率极高」的根因：`Destroy` 走
        //    `State.Move(card, CardLocation.Discard)`（本文件 1685 附近）把卡移出
        //    前线，却没有任何人重算 `FrontlineOwner` ⇒ 前线最后一个单位死亡后归属
        //    **永久残留在死者那一方**。而前线是互斥的，于是：
        //      · 推进 → 被 `MoveUnit` 的 `<frontline-blocked-by-opponent>` 拒绝
        //      · 打前线 → 前线已空，没有目标
        //      · 打对面半场 → 半场步兵 `Definition.Range = 1`，够不着
        //    三个方向全堵死 ⇒ 只能一路空过。
        //    实测 `replay-214436` 有两次前线单位死亡（#30 t7、#50 t11，都是
        //    `card_unit_10th_para_battalion` 从 `BoardFrontline` 进 `Discard`）。
        //
        // 为什么挂在**换区钩子**上，而不是在 `Destroy` 里补一句：
        // 蓝图里 `UpdateFrontlineIfNeeded` 的调用点只有 `CardLocationMoved` /
        // `SpawnCardToBoard` / `SpawnMultipleCardsOnBattlefield` /
        // `ApplyDamageToMultipleCards` / `ApplyDestroyMultipleCards`，
        // 而这些路径在内核里**全部**经过 `GameState.Move` ⇒ 挂这里一处顶五处：
        // `Destroy` / `DiscardCard` / 效果退回手牌 / 洗回牌库 / `MakeCardRetreat`
        // 全部覆盖（后三条在 `CardApi.cs` / `CardApiDispatch.cs` 里，只补 `Destroy` 会漏）。
        //
        // 顺序依据：`ApplyRemoveCardFromBoard` 是 si=14 离场事件 → si=22
        // `CardLocationMoved`（⇒ 这里 ⇒ 发归属事件）→ si=26
        // `ExecuteOnAfterLeaveBoardOrOwnerEvents` → si=29 `ExecuteOnCardDestroyedFunction`。
        // 内核 `Destroy` 里 `State.Move` 正好在 `OnDestroyed` 之前，同序。
        //
        if (manageLocation && moveReason != CardMoveReason.Convert
            && (oldLocation == CardLocation.BoardFrontline || newLocation == CardLocation.BoardFrontline))
        {
            RefreshFrontlineOwner(card);
        }
    }

    /// <summary>
    /// Public adapter for Blueprint `ExecuteOnCardLocationMoved` callers that
    /// perform a logical reveal without changing the card's stored location.
    /// </summary>
    public void ExecuteOnCardLocationMoved(CardInstance card, CardLocation oldLocation,
                                           CardLocation newLocation, bool changeOwner = false,
                                           CardMoveReason moveReason = CardMoveReason.Uncategorized)
    {
        Effects.Blueprint.LiveRuleTables.TryGetEnumName("/Game/Structs/CardMoveReason.CardMoveReason",
            (int)moveReason, out string reasonName);
        var args = new object?[] { card, (int)oldLocation, (int)newLocation, changeOwner, reasonName };
        var named = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cardMoved"] = card,
            ["oldLocation"] = (int)oldLocation,
            ["newLocation"] = (int)newLocation,
            ["ChangeOwner"] = changeOwner,
            ["MoveReason"] = reasonName,
        };

        if (!card.IsSuppressed)
        {
            Api.FireTrigger("OnCardLocationMoved", card, card.Owner,
                eventArgs: new object?[] { (int)oldLocation, (int)newLocation, changeOwner, reasonName },
                eventSubject: card, namedArgs: named,
                oldLocation: oldLocation, newLocation: newLocation,
                recipientSnapshot: new[] { card }, includeUnavailableRecipients: true);
        }

        // Native i=125 queries T47 after the own hook; its direct loop has
        // no physical-zone filter, repeated suppression check or stop flag.
        Api.ExecuteMovementObservers("OnOtherCardLocationMoved", card, args, named,
            oldLocation: oldLocation, newLocation: newLocation);
    }

    /// <summary>Native T49: own hook, query observers, stop check before each saved item.</summary>
    public void ExecuteOnCardMoveFromFrontline(CardInstance card)
    {
        if (!card.IsSuppressed)
            Api.FireTrigger("OnMoveFromFrontline", card, card.Owner,
                eventArgs: Array.Empty<object?>(), recipientSnapshot: new[] { card },
                includeUnavailableRecipients: true);
        Api.ExecuteMovementObservers("OnOtherCardMoveFromFrontline", card, new object?[] { card },
            new Dictionary<string, object?> { ["cardMoved"] = card }, stopFurtherActions: true);
    }

    /// <summary>ExecuteOnEnterPlayEvents: unsuppressed self first, then T43 observers.</summary>
    public void ExecuteOnEnterPlayEvents(CardInstance card, int method)
    {
        if (!card.IsSuppressed)
            Api.FireTrigger("OnEnterPlay", card, card.Owner,
                eventArgs: new object?[] { method },
                namedArgs: new Dictionary<string, object?> { ["Method"] = method },
                recipientSnapshot: new[] { card }, includeUnavailableRecipients: true);
        Api.ExecuteOtherCardEnterPlayEvents(card, method);
    }

    public GameState State { get; }

    public IReadOnlyList<string> LeftDeckList { get; }
    public IReadOnlyList<string> RightDeckList { get; }

    public CardApi Api { get; private set; } = null!;

    /// <summary>
    /// 因为**射程不够跨前线**而被拒的攻击次数（诊断用，只增不改行为）。
    ///
    /// 判据见 <see cref="CanReachAcrossFrontline"/>。加这个计数器是因为
    /// 「攻击被拒」在回放里只有一句笼统的失败原因，分不清是射程拒的还是
    /// 召唤失调拒的 —— 而这次改动会让一大批历史回放里的攻击被拒。
    /// </summary>
    public int OutOfRangeAttacksRejected { get; private set; }

    /// <summary>
    /// 「从牌库里挑一张」的外部答复源 —— 对应 <c>CardApi</c> 的 <c>selectCardToDraw</c>
    /// （Develop / 选牌）里的**玩家选择**那一步。
    ///
    /// 为什么需要这个钩子（依据是反编译，不是推测）：
    /// <list type="bullet">
    /// <item>卡自己的 <c>selectCardToDraw(cardSelectingCardToDraw, selectFromTopOfDeck, isEffect,
    ///   out drawnCardID)</c>（签名见 <c>ref/kards-sim/.../_index.g.cs</c>）在候选多于 1 张时
    ///   **不自己抽牌**，而是 <c>NotifySelectCardToDrawPending</c> 把候选交给玩家
    ///   （BP_CardFunctions 里那条 <c>L_0D61</c> 分支），抽牌发生在**答复到达之后**
    ///   （同函数的单候选分支 <c>L_0B57</c> 是现成的对照：直接
    ///   <c>DrawSpecificCardFromDeckBySide</c> + <c>OnHandTargetSelected</c>）。</item>
    /// <item>真实客户端把答复作为紧凑动作 <c>CS</c>（全名
    ///   <c>XActionCardToDrawSelected</c>，见 BP_OnlineMatch 的分派开关链）发上来，
    ///   参数是 <c>[cardBeingPlayed, 候选下标, 选中卡的卡组码]</c>。</item>
    /// </list>
    ///
    /// 参数：正在结算的那张卡（挑牌的主体）、<c>selectFromTopOfDeck</c>（决定走哪一族）、
    /// <c>isEffect</c>（蓝图原样传入）。
    ///
    /// ⚠️ 返回值有**两种含义**，取决于 <c>selectFromTopOfDeck</c>：
    /// <list type="bullet">
    /// <item><c>true</c>（牌库/占卜族）：必须返回**牌库里那张卡的实例**（仍在牌库中）。</item>
    /// <item><c>false</c>（Develop 族）：返回一张**卡池模板**实例即可 ——
    ///   只有 <c>Name</c> 会被用到（内核按这个名字 `Create` 一张新卡，
    ///   依据是 `OpponentActionsCardToDrawSelected` 里的 `CreateCard(...)`）。
    ///   传牌库实例也不会出错，但语义上不是同一回事。</item>
    /// </list>
    ///
    /// 返回 null = 没有答复，由 <c>CardApi</c> 走兜底：牌库族取
    /// <c>GetDeckBySide(side).DeckCardIDs[0]</c>（蓝图 `BP_Logic.autoPickCardToDraw`），
    /// Develop 族取候选表第一张（**蓝图没有这条兜底**，是本内核的近似）。
    /// </summary>
    public Func<CardInstance?, bool, bool, CardInstance?>? PickCardToDraw { get; set; }

    /// <summary>
    /// **收到候选表的选牌钩子** —— 比 <see cref="PickCardToDraw"/> 高一档优先级。
    ///
    /// ## 为什么必须有它（2026-10-02 从真对局 + 自测查出来的 bug）
    ///
    /// `ReplayRunner` 重建局面时会装一个 <see cref="PickCardToDraw"/>，
    /// 它**只从"人类动作流里的 `CS` 队列"取答复**。
    /// 但 bot **自己回合**的动作流里根本没有 `CS`（还没发过）⇒ 队列空 ⇒ 返回 null
    /// ⇒ `CardApiDispatch` 里 `if (picked is null) return 0`
    /// ⇒ **整张开发牌什么都不做**：不加牌、不留痕、不发 `CS`。
    ///
    /// 实测（`rel/data/fyserver/bot-log/bot-20261001.log`）：bot 打了
    /// `card_event_baker_street_irregulars`（卡面 Develop a British special force unit），
    /// **没有任何 `CS`** —— 不是"选得不好"，是这张牌**完全没生效**。
    ///
    /// 自测也证实：四张开发牌都"走到了选牌"（被问 1 次），但**留痕全是"无"**。
    ///
    /// ## 与 `PickCardToDraw` 的分工
    ///
    /// <list type="bullet">
    /// <item><b>重放人类动作时</b>：用 `PickCardToDraw`（答复来自动作流）——
    ///   本钩子此时**必须为 null**，否则会把人类的选牌劫持掉、局面重建就错了。</item>
    /// <item><b>bot 自己决策时</b>：`BotTurnService` 在**重放结束之后**装本钩子，
    ///   它拿到**候选表**（所以能真正择优，而不是只能答"要/不要"）。</item>
    /// </list>
    ///
    /// 参数：`(挑牌的卡, 候选表)` → 选中项；返回 null 表示候选为空。
    /// </summary>
    public Func<CardInstance, IReadOnlyList<CardInstance>, CardInstance?>? ChooseSpawnCard { get; set; }

    /// <summary>
    /// **回放路径**的「从手牌挑一张」答复源 —— 答复来自动作流里的 `HT`
    /// （`XActionHandTargetSelected`）。
    /// </summary>
    public Func<CardInstance, IReadOnlyList<CardInstance>, CardInstance?>? PickHandTarget { get; set; }

    /// <summary>Replay reply: null means no answer; 0 is an explicit empty-target answer.</summary>
    public Func<CardInstance, IReadOnlyList<CardInstance>, int?>? PickHandTargetId { get; set; }

    /// <summary>
    /// **bot 决策路径**的「从手牌挑一张」—— 优先级高于
    /// <see cref="PickHandTargetId"/> 和 <see cref="PickHandTarget"/>。
    ///
    /// 用于 `card_unit_gordon_highlanders` 这类卡：
    /// 「Deployment: Choose an order in hand. Set its cost to 0 and put it on top of your deck.」
    ///
    /// ⚠️ 这条链路以前**整个是空的**：`selectTargetFromHand` 的实现是
    /// `(c, r, a) => c.Target`（什么都不做），而 `HT` 在 `ReplayRunner` 里也没处理
    /// ⇒ 人类选的手牌既没被设成 0 费、也没回牌库 ⇒ 状态从那里开始漂开。
    /// </summary>
    public Func<CardInstance, IReadOnlyList<CardInstance>, CardInstance?>? ChooseHandTarget { get; set; }

    /// <summary>最近一次「手牌目标」留痕 —— 供 `BotTurnService` 发 `HT`。</summary>
    public sealed record HandTargetRecord(int SelectingCardId, int ChosenCardId);

    public HandTargetRecord? LastHandTarget { get; private set; }

    public void RecordHandTargetPick(int selectingCardId, int chosenCardId)
        => LastHandTarget = new HandTargetRecord(selectingCardId, chosenCardId);

    public void ClearLastHandTarget() => LastHandTarget = null;

    /// <summary>
    /// **最近一次"选牌"的留痕** —— 用来把 AI 的选择发回客户端。
    ///
    /// ## 为什么必须有它（2026-10-02 从真回放查出来的 bug）
    ///
    /// 游戏里「开发」这类效果会让玩家**从 3 张候选里挑 1 张**。
    /// 客户端用一条 `CS` 动作（`XActionCardToDrawSelected`）把选择告诉对方：
    /// <code>
    ///   CS { "0": 触发选牌的卡 cardID, "1": 候选下标, "2": 选中卡的 deck code }
    /// </code>
    /// 实测（`out/_server-replays/replay-630801`）：人类发了 **3 条 CS**，
    /// 而 bot **一条都没有** —— 因为 `AtomicAction` 只有出牌/攻击/移动/结束回合
    /// 四种，**根本产不出 CS**。
    ///
    /// 症状就是用户报的「AI 不会选开发」：内核里 `CardApiDispatch` 确实"选"了
    /// （退化成候选表第一张），但那个选择**从不发给客户端**，
    /// 所以客户端看到的是一张**没有任何选择**的开发牌。
    ///
    /// ⚠️ 这是"内核与客户端之间缺了一条协议"，不是 AI 强弱问题。
    /// </summary>
    public sealed record PickRecord(int SelectingCardId, int Index, string Code, string ChosenName);

    /// <summary>最近一次选牌的留痕；没有则为 null。每次选牌覆盖。</summary>
    public PickRecord? LastPick { get; private set; }

    /// <summary>由 `CardApiDispatch` 在选牌落实时调用。</summary>
    public void RecordPick(int selectingCardId, int index, string code, string chosenName)
        => LastPick = new PickRecord(selectingCardId, index, code, chosenName);

    /// <summary>本次决策开始前清掉留痕（`BotTurnService` 每回合调一次）。</summary>
    public void ClearLastPick() => LastPick = null;

    /// <summary>统计信息。</summary>
    public int TurnCount => State.Turn;
    public List<string> Log { get; } = new();

    private void Say(string msg) => Log.Add($"[T{State.Turn}/{State.ActiveSide.ToWire()}] {msg}");

    // ==================== 开局 ====================

    public void Start()
    {
        // HQ：左右各一，防御 20
        var leftHq = State.Create("card_location_london", Side.Left, CardLocation.BoardHqLeft, 0);
        leftHq.Defense = InitialHqDefense;
        leftHq.MaxDefense = InitialHqDefense;

        var rightHq = State.Create("card_location_london", Side.Right, CardLocation.BoardHqRight, 0);
        rightHq.Defense = InitialHqDefense;
        rightHq.MaxDefense = InitialHqDefense;

        BuildDeck(Side.Left, LeftDeckList);
        BuildDeck(Side.Right, RightDeckList);

        // 洗牌（确定性）
        ShuffleDeck(Side.Left);
        ShuffleDeck(Side.Right);

        // 起手
        DealOpeningHand(Side.Left, FirstPlayerHand);
        DealOpeningHand(Side.Right, SecondPlayerHand);

        // kredit 初始 **0**，让下面的 `StartTurn(Left)` 自己加出先手 T1 的 1 点。
        //
        // ⚠️ 这里曾经写的是 1，注释还写着"先手方 kredit 上限 1，后手 1"，
        //    但 `StartTurn` 里是 `MaxKredits + 1` —— 于是先手 T1 的上限变成 **2**。
        //    后果不是"多一点费用"这么轻：先手 T1 能连出两张 1 费牌，
        //    200 局自对弈的**先手胜率因此到 98%**（真实 KARDS 约 50~55%）。
        //
        // 真实数据（回放 kredit 追踪实测）：
        //     right T2 max=1   left T3 max=2   left T5 max=3
        // 也就是**每人第 1 回合都是 1**，之后每个自己的回合 +1。正确序列是：
        //     init 0 → StartTurn(Left) → left 1（T1）
        //            → StartTurn(Right) → right 1（T2）
        //            → StartTurn(Left) → left 2（T3）
        // 所以初始值必须是 0。**别再把它们改成 1** —— 那等于先手白送一个 kredit 槽位。
        State.SetMaxKredits(Side.Left, 0);
        State.SetMaxKredits(Side.Right, 0);
        State.SetKredits(Side.Left, 0);
        State.SetKredits(Side.Right, 0);

        State.StartingSide = Side.Left;
        State.ActiveSide = Side.Left;
        State.SetupSidesInBattle(State.StartingSide, State.StartingSide.Opposite());
        State.Turn = 1;

        Api.FireTrigger("OnStartOfGame", null, Side.Left);
        Api.FireTrigger("OnStartOfGame", null, Side.Right);

        // 先手的第 1 回合不摸牌 —— 判据在 `StartTurn` 里（`State.Turn != 1`），
        // 这里不用传参。见 `StartTurn` 的 `draw` 参数说明（BP_Logic.CanSideDrawCards）。
        StartTurn(Side.Left);
    }

    private void BuildDeck(Side side, IReadOnlyList<string> cards)
    {
        int n = 0;
        foreach (string name in cards)
        {
            // ⚠️ 开局建牌库必须用**顺序号**（左 2../右 42..），不能用效果发号规则：
            //    真实对局的开局牌号由服务端快照定死（`CreateWithId`），这里只是
            //    自对弈/NNPlay 造一副牌。见 `GameState.NextCardId` 的长注释。
            State.Create(name, side, side.DeckOf(), n++, sequentialId: true);
        }
    }

    private void ShuffleDeck(Side side)
    {
        var deck = State.Deck(side).ToList();
        State.Random.Shuffle(deck);
        for (int i = 0; i < deck.Count; i++)
        {
            deck[i].LocationNumber = i;
        }
    }

    private void DealOpeningHand(Side side, int count)
    {
        var deck = State.Deck(side).ToList();
        for (int i = 0; i < count && i < deck.Count; i++)
        {
            State.Move(deck[i], side.HandOf());
        }
    }

    // ==================== 回合 ====================

    /// <summary>开始一个回合。</summary>
    /// <param name="draw">
    /// 回合开始是否摸牌。**null = 按客户端规则自动判定**（推荐），
    /// 只有调用方有明确理由时才显式覆盖。
    ///
    /// 规则来自**反编译的 `BP_Logic.CanSideDrawCards`**（不是口述、不是猜）：
    /// <code>
    /// canDraw = false  当 (GetTurnNumber() == 1 &amp;&amp; !isTutorialGame)
    /// </code>
    /// 也就是**全局回合号 == 1 的那一次回合开始不摸牌**（只有先手的第 1 回合），
    /// 之后每回合都摸。
    ///
    /// 实测三方一致：反编译蓝图判据 + 服务端发牌数据（left 4/35、right 5/34）
    /// + 真机快照（act=1 手牌与发牌逐数相同，act=3 后手才 +1）。
    /// </param>
    public void StartTurn(Side side, bool? draw = null)
    {
        BeginEffectResolution();
        try
        {
            StartTurnCore(side, draw);
            // OpponentActionsStartTurn: StartTurnBySide then ExecuteAutoPlayCards.
            ExecuteAutoPlayCards();
            ApplyPendingGotchaPlayReceipts();
        }
        finally
        {
            EndEffectResolution();
        }
    }

    private void StartTurnCore(Side side, bool? draw = null)
    {
        bool doDraw = draw ?? (State.Turn != 1);

        State.ActiveSide = side;
        State.ResetTurnGameplayCounters();
        State.SetHasPlayedWeatherCardThisTurn(false);
        State.ResetDestroyedThisTurn();

        State.ActiveBondFactions.Clear();
        foreach (var unit in State.Board(side))
            if (unit.Definition.IsUnit && !CardApi.IsUnrevealedCovertCard(unit))
                State.ActiveBondFactions.Add(unit.Definition.Faction ?? "");

        // ---- kredit 槽位 +1，并回满 ----
        //
        // 规则（`KARDS基础规则参考.md:27/29`）是**两个不同的上限**：
        //   · **自然增长**：每回合 +1，涨到 **12** 就停；
        //   · **卡牌效果**（`GainKreditSlot` / `the_war_machine` 的 "Gain 1 extra
        //     kredit slot"）：最多把槽位抬到 **24**。
        //
        // ⚠️ 这两个数**不能合成一个常量**：以前写成
        // `Math.Min(MaxKreditCap, MaxKredits + 1)`（`MaxKreditCap = 12`）会把
        // **卡牌效果已经抬上去的槽位又钳回 12**。实测（对局 781364）：人类用
        // `the_war_machine` 抬到 13，下一回合被我们吃回 12；到 t21 真实槽位已 21、
        // 我们只算 12 ⇒ 人类打得出的牌我们打不出 ⇒ 从 t21 起动作接连失败。
        //
        // ★★ 线上 pak 的 `BP_Logic::StartTurnBySide` 直接读取当前
        // `getKreditSlotBySide`，满足条件时加 1，再用同一个新值同时写回
        // kredit 与 kredit slot；这里不能维护一个隐藏的生命周期槽位基线。
        //
        // ---- 证据 1（权威）：真人玩家给出的规则描述 ----
        // 逐字引用（问的就是"槽位到底怎么涨"）：
        // <code>
        //   第1个回合我是先手，那我开始了，我就加一指挥点槽 并且回满指挥点
        //   （我现在指挥点槽是一，那么指挥点也变成一）
        //   接着我过了，轮到对面 对面也变成1
        //   接下来轮到我，我指挥点槽变成二，并且回满指挥点 然后就轮到对面，对面也变成2
        //   假如我现在用了一张战争机器，增加一个指挥点槽 那么我就有三个了
        //   下个回合开始时，因为自然增长，我变成了四个，指挥点依旧回满
        // </code>
        // ⇒ 「我第 1 个自己回合 = 1」「对面第 1 个自己回合**也** = 1」
        //   「我第 2 个自己回合 = 2」「对面第 2 个自己回合**也** = 2」
        //   「战争机器 +1 → 3」「**我的**下个回合自然增长 → 4」。
        //   即正常情况下每个自己的回合开始时当前槽位 +1；卡牌效果抬高的
        //   当前槽位也会参与下一次增长（3 → 4），但降槽后的 3 也只能恢复到 4。
        //
        // ---- 证据 2（重算的花费表，**实际支付**口径）----
        // 工具：`tools/ServerBridgeTest --kredit-table`（成本取内核
        // `CardInstance.KreditCost`，已叠加所有 `ChangeKreditCost`；卡身份取
        // **动作流自带的卡组码**，见 `WireAction.CardCodes`）。
        // 4 局 / 60 个人类回合，只做「本回合支付之和 ≤ 槽位」这个必要条件检验：
        // <code>
        //   原始结果            214436 self 0 / global 0
        //                       508065 self 0 / global 0
        //                       542091 self 0 / global 0
        //                       773639 self 2 / global 1
        // </code>
        // 773639 那 3 条都落在 t9 / t18，逐条查过：
        // <list type="bullet">
        // <item>**t9（11 点）**：其中 2 张 `card_event_iron_from_the_north`（卡面
        //   「If you control the frontline, gain **3 additional Kredits**」，
        //   IR `GiveKreditsBySide(side, 3, …)`，i=78）各倒回 3 点 ⇒ **净需求 5**
        //   ≤ self 5 ✓ / ≤ global 9 ✓。</item>
        // <item>**t18（12 点）**：self 11 / global 14。动作流在 t17 与 t18 **连着**
        //   发了两条 `XActionStartOfTurn`（中间没有 `EndOfTurn`，见 `ReplayRunner`
        //   里 `turnStarted` 的去重注释）。客户端若照数，它的"自己第几个回合"就是
        //   **10** ⇒ 10 + `war_bonds` 的 2 个额外槽 = **12 = 花费**，正好花光。
        //   即这一条差的是**回放驱动的去重口径**，不是槽位规则。</item>
        // </list>
        // ⇒ 扣掉上面两类之后 **self 0 条 / global 0 条**。
        // ⚠️ **但这张表不能当"global 模型正确"的证据**：global 槽位恒 ≥ self 槽位
        //    （`min(12, 全局回合号) ≥ min(12, 自己第几个回合)`），
        //    所以「花费 ≤ 槽位」这个检验**只能证伪 self、永远证伪不了 global** ——
        //    它是**单侧弱约束**。两个模型都"不违反"不等于两个模型都对。
        //    真正定案的是证据 1（真人玩家的描述）；这张表只当**健全性检查**。
        //
        // ---- 教训：为什么上一轮会误判（**"花费反推"这条路本身不该走**）----
        // 上一轮据以改模型的「self 模型 5 条违反」，是拿 **`cards.live.json` 的卡面费用**
        // 当花费算出来的 —— 而**卡面费用 ≠ 实际支付**，偏差**两个方向都有**：
        // <list type="bullet">
        // <item>**高估**：`card_event_pams` 开发出来的牌，客户端当 **0 费**。
        //   出处 `card_event_pams` 的 IR **i=348**
        //   `ChangeKreditCost(卡, 自己, 0, changeType=2)`（`EChangeType::SetValue`），
        //   卡面原文「Add it to your deck with a cost of **0**.」。
        //   实测那一版表里的「542091 t7 花费 8」（`convoy_175` 费 3 + `war_bonds` 费 5）
        //   真实支付是 **0 + 5 = 5**。同理 `508065 t9` 的 9 实际是 6。</item>
        // <item>**低估**：回合内的加费/加槽卡（`iron_from_the_north` 控制前线时 **+3 kredit**、
        //   `war_bonds` **+2 槽位**、`the_war_machine` **+1 槽位**）会让花费**超过**回合
        //   开始时的槽位，被误判成"模型被证伪"。</item>
        // </list>
        // ⇒ **槽位规则由真人玩家的描述定案；"花费反推"只配当**单侧**的健全性检查，
        //    不配当改规则的依据。**
        //
        // ---- 附带**查清**（但**未修**）的那个真 bug（`508065` 的 ⑤b）----
        // 那个「`#46 t11 PC` kredit 不足（kredits=2，费用=3）」**与槽位模型无关**，
        // 根因是**卡身份**：`card_event_atlantic_convoy` 的卡面是
        // 「Add one random US unit with cost 3 or less to the support line and another to hand.」，
        // 内核随机到的 `card_unit_1st_infantry_regiment_us`（**油费 3**）而客户端随机到的是
        // `card_unit_fifth_ohio`（**油费 1**，动作流 `#44 ML` 的卡组码 `DB` 是铁证）。
        // 人类 t11 的真实支出是 `ML fifth_ohio 1 + land_girls 2 + p40_warhawk 3 + ML 2nd_west_africa 1 = 7`，
        // 而"自己第 6 回合 + 战争机器 1 = 7" —— **正好花光，一分不差**。
        // 内核多算的那 2 点油费（3 vs 1）把它挤成了 2，于是最后那张 3 费牌打不出来。
        int previousSlots = State.MaxKredits(side);
        int slots = previousSlots;
        if (!State.HasGameplayRestriction(side,
                GameplayRestrictionType.CannotKreditSlotAtTurnStart)
            && slots < NaturalKreditCap)
        {
            slots++;
        }

        State.SetMaxKredits(side, Math.Min(MaxKreditCap, slots));
        // BP_Logic::StartTurnBySide calls SetKreditsAndKreditSlots with the
        // same new slot value for both kredit and slot.  A temporary kredit
        // bonus therefore does not survive the next start-of-turn refill.
        State.SetKredits(side, State.MaxKredits(side));
        State.DecrementGameplayRestrictions();

        // 「本回合打出过哪些牌」按回合清空（客户端 GetCardsPlayedThisTurn 的语义）。
        // ⚠️ 必须在这里清、而不是在 EndTurn 里清：回放路径上 XActionStartOfTurn 与
        //    EndTurn 的配对并不严格（见 ReplayRunner 的 turnStarted 处理）。
        //
        // ★★ **清之前先快照进「按回合的历史」**（2026-10-02 补）——
        //    `GetCardsPlayedFromHandLastTurn()` 要读它（见 `GameState.CardsPlayedFromHandByTurn`
        //    的长注释）。这里是唯一的快照点：`EndTurn` 在 `:652` 先把 `State.Turn` +1
        //    再调本方法，所以此刻 `CardsPlayedThisTurn` 里的正是 **`Turn - 1`** 那一回合的。
        //    漏了这一步的后果：`didPlayBritishInfantryLastTurn`（5 张卡的私有函数）
        //    读到空列表 ⇒ 恒假 ⇒ 那些卡的「上回合打过英国步兵」分支永不执行。
        State.CardsPlayedFromHandByTurn[State.Turn - 1] = State.CardsPlayedThisTurn.ToList();
        State.CardsPlayedThisTurn.Clear();

        // 重置本单位行动状态
        //
        // ⚠️ `AttacksThisTurn` 必须一起清（2026-10-02 补）—— 它是**奋战额度**的已用计数，
        //    只清 `HasAttackedThisTurn` 那个布尔的话，奋战的第二次攻击在**下个回合**
        //    也会被 `AttacksThisTurn < MaxAttacksThisTurn` 挡住（计数只增不减）。
        //    出处 `DoOnStartOfTurn`（`ref/kards-sim/.../_deps/BP_Logic.g.cs:2647-2655`）
        //    同一处清 `hasAttackedThisTurn` / `attackCountThisTurn`。
        foreach (var unit in State.Board(side).ToList())
        {
            unit.HasAttackedThisTurn = false;
            unit.HasBeenAttackedThisTurn = false;
            unit.HasMovedThisTurn = false;
            unit.AttacksThisTurn = 0;
            unit.AttackQuotaOffset = 0;
            unit.OperationsUsedThisTurn = 0;
        }

        RecordAction("XActionStartOfTurn", side, new Dictionary<string, object?>
        {
            ["side"] = side.ToWire(),
        });

        // 「回合开始之前」—— 出处 `out/bp-cardfn.json` 函数
        // `ExecuteBeforeStartOfTurnEvents`（i=307 广播 `OnBeforeStartOfTurn`），
        // 唯一调用点是 `BP_Logic::StartTurnBySide` i=1047（`out/bp-logic.json`）。
        // 签名 `BaseCardObject.h:733 void OnBeforeStartOfTurn();`（无参，无"别人"变体）。
        Api.FireTrigger("OnBeforeStartOfTurn", null, side);

        // 抽牌（全局回合 1 跳过，见 `draw` 参数说明）
        if (doDraw && !State.HasGameplayRestriction(side,
            GameplayRestrictionType.CannotDrawCardAtTurnStart))
        {
            // `StartOfTurnDraw = true` —— 出处 `BP_Logic::StartTurnBySide` i=1175 调
            // `DrawTopCardFromDeck(sideStartTurn, 0, False, False, **True**, 0.4, False, out)`，
            // 参数顺序取自 `ref/kards-sim/KardsSim/Generated/_index.g.cs:1952`
            //   ["DrawTopCardFromDeck"] = { deckSide, instigatorID, opponentDraw,
            //                              cardSeen, startOfTurnDraw, delay, scryingDraw, drawnCard }
            // 对照：`ExecuteScryingEffectBySide` i=2005 传的是 (…, False, 0.4, True, …)
            // —— 即"占卜抽"而不是"回合开始抽"，两者互斥。
            DrawCard(side, startOfTurnDraw: true);
        }

        // BP_Logic::StartTurnBySide calls GiveMobilizeBonus after the draw and
        // before ExecuteStartOfTurnEvents.  The bonus is permanent and sourced
        // by the unit itself, so repeated active turns accumulate in BuffsBySource.
        foreach (var unit in State.Board(side).ToList())
        {
            if (unit.Keywords.Contains(Keyword.Mobilize))
            {
                Api.ChangeAttack(unit, 1, unit);
                Api.ChangeDefense(unit, 1, unit);
            }
        }

        Api.FireTrigger("OnStartOfTurn", null, side, "OnOtherStartOfTurn");
    }

    public void EndTurn(Side side)
    {
        BeginEffectResolution();
        try
        {
            EndTurnCore(side);
        }
        finally
        {
            EndEffectResolution();
        }
    }

    private void EndTurnCore(Side side)
    {
        // Blueprint ExecuteEndOfTurnEvents owns the complete queue, including
        // endofturn1/endofturn2 ordering and recursive subscribers.
        Api.ExecuteEndOfTurnEvents();

        // ★★ 抑制（Suppress）**永不解除** ⇒ 这里**没有**任何清理（2026-10-02 第三轮删除）。
        //
        // 这一行原先调 `ClearExpiredSuppression(side)` —— 那条「抑制于单位所有者
        // 下回合结束时移除」是**内核自己发明的**，两方独立证据都指向"永不解除"：
        //   · 玩家（雪雾）：「抑制：使被抑制的单位失去所有特效和关键字（压制不受影响、
        //     老兵变回原形、所有增益失效）。解除时机：**【永不解除】** —— 一直白板到游戏结束。」
        //   · 蓝图：`isSuppressed` 全库**只有一处写点且写的是 `True`**
        //     （`BP_CardFunctions.g.cs:35781`），`SetMember(…, "isSuppressed", …False)`
        //     **一处都没有**；规则表 `KARDS基础规则参考.md` 只给**压制**写了移除时机（`:160`），
        //     抑制那一节（`:123-125`）**没有时机**。
        // ⇒ 删掉它，抑制就一直挂到游戏结束。`CardApi.RestoreAfterSuppression` 因此
        //    **没有产品调用方**（该还原路径当前不可达）—— 能力保留，注释见该方法本体。
        // ⚠️ 别把抑制和**压制**（`Pinned`，中文「压制」）搞混：压制**有**蓝图依据
        //    （规则表 `:160` + `_deps/BP_Logic.g.cs:2218`），到期递减就在下面
        //    `DecrementPinnedTurnsEndTurn()` 里，**必须保留**。

        // ★ 钉住的到期递减（2026-10-02 补）。
        // ⚠️ 「与上面那条同族」现在不成立了：上面那条**抑制**的到期清理已删
        //    （抑制永不解除），`EndTurn` 里只剩这一条到期机制。
        //
        // ⚠️ 为什么必须和 `MoveUnit` 的钉住门一起补：内核原先**完全没有**
        //    `pinnedTurns`（全文件搜不到）⇒ 钉住一旦加上就**永不解除**。
        //    在只有 `Attack` 有钉住门时，这表现为"被钉住永远不能攻击"；
        //    给 `MoveUnit` 加上同一道门之后，就变成"被钉住永远不能动"。
        //    实证：回放 389594 的 6 条 bot `ML` 被新门误拒，连锁把人类 t13 的
        //    攻击打成「够不着」—— 见 `DecrementPinnedTurnsEndTurn` 的注释。
        DecrementPinnedTurnsEndTurn();

        // OpponentActionsEndTurn i=673 precedes SwitchPlayingSide/IncrementTurnNumber.
        ExecuteAutoPlayCards();
        ApplyPendingGotchaPlayReceipts();

        RecordAction("XActionEndOfTurn", side, new Dictionary<string, object?>
        {
            ["side"] = side.ToWire(),
            ["reason"] = "endTurnButton",
        });

        if (State.IsFinished)
        {
            return;
        }

        State.Turn++;
        StartTurn(side.Opposite());
    }

    /// <summary>
    /// Marks the current effect chain for a deferred ForceEndTurn request.
    /// The blueprint notifier is deliberately asynchronous from the headless
    /// engine's point of view: the current VM/trigger program must return first.
    /// </summary>
    internal void RequestForceEndTurn(Side side, int instigatorID)
    {
        if (_pendingForcedEndTurnSide is null)
        {
            _pendingForcedEndTurnSide = side;
            _pendingForcedEndTurnInstigator = instigatorID;
        }
    }

    internal void BeginEffectResolution()
    {
        if (_effectResolutionDepth == 0 && _pendingSelections is null)
            _pendingSelections = new Queue<Action>();
        _effectResolutionDepth++;
    }

    internal void EndEffectResolution()
    {
        if (_effectResolutionDepth <= 0)
        {
            return;
        }

        _effectResolutionDepth--;
        if (_effectResolutionDepth == 0)
        {
            // A response follows the entire requesting action/Blueprint frame.
            // Keep a depth while draining: repeated and nested effects append
            // further responses instead of recursively resolving them inline.
            _effectResolutionDepth++;
            try
            {
                while (_pendingSelections is { } selections && selections.TryDequeue(out var selection))
                    selection();
            }
            finally
            {
                _pendingSelections = null;
                _effectResolutionDepth--;
            }
            ConsumePendingForcedEndTurn();
        }
    }

    private void ConsumePendingForcedEndTurn()
    {
        if (_processingForcedEndTurn
            || _pendingForcedEndTurnSide is not { } side)
        {
            return;
        }

        int instigatorID = _pendingForcedEndTurnInstigator;
        _pendingForcedEndTurnSide = null;
        _pendingForcedEndTurnInstigator = 0;

        FireSubAction("ZActionForceEndTurn", new[]
        {
            ActionValue2.Int("instigatorID", instigatorID),
        });

        _processingForcedEndTurn = true;
        try
        {
            EndTurn(side);
        }
        finally
        {
            _processingForcedEndTurn = false;
            ConsumePendingForcedEndTurn();
        }
    }

    // ==================== 抽牌 ====================

    /// <summary>
    /// 抽一张牌。牌库空则吃疲劳伤害；**手牌已满则这张牌直接进弃牌堆（不进手牌）**。
    ///
    /// 手牌上限的判据与动作全部照抄反编译蓝图：
    /// <code>
    /// BP_CardFunctions.DrawTopCardFromDeck
    ///   i=712  FetchCardsByLocation(手牌) → nextHandLocation(=QtyInLocation), isHandFull
    ///   i=739  （此时牌刚从牌库移除、还没进手牌 ⇒ 这个值就是「抽之前」的手牌数）
    ///   i=758  JumpIfNot(isHandFull) → 1042        ← 没满：走 1042 那条（进手牌）
    ///   i=772      drawnCardRef.location = 8       ← ★ 满了：直接置弃牌堆
    ///   i=883      NotifyDrawCardFromDeck(…, PreQtyInHand=nextHandLocation, HandFull=isHandFull, …)
    ///   i=1042 drawnCardRef.locationNumber = nextHandLocation   ← 没满才写位置
    ///   i=1091 drawnCardRef.location = handLocation
    /// BP_CardFunctions.DiscardOnDrawingWithFullHands(cardID, insitigatorID, oldLocation)
    ///   i=0    SetCardLocationAndLocNumber(cardID, 8 /*Discard*/, 0)
    ///   i=53   JumpIfNot(IsActionProcess()) → 132
    ///   i=67   CardFunctionsNotifier.NotifyDiscardCard(cardID, insitigatorID, oldLocation, false, false)
    /// </code>
    /// 上限 9 的来源是 `BP_GameState_Battle.FetchCardsByLocation` case 3,4 → `MaxQty = IntConst(9)`
    /// （见 <see cref="GameState.HandCapacity"/>）。
    ///
    /// ⚠️ 2026-09-26 修：**这条检查以前完全不存在**，`DrawCard` 无条件把牌塞进手牌，
    /// 于是手牌能涨到 12 张（NN 对局日志实测），而真实规则是满了直接烧牌。
    /// </summary>
    /// <summary>ApplyFatigueDamage: direct HQ defense subtraction, then T26, with a saved death flag.</summary>
    internal bool ApplyFatigueDamage(Side side, bool fromBond)
    {
        var hq = State.Hq(side);
        int amount = State.Fatigue(side) + 1;
        int oldDefense = hq.Defense;
        hq.Defense -= amount;
        bool destroyed = hq.Defense <= 0;
        State.SetFatigue(side, amount);
        FireSubAction("ZActionFatigueDamage", new[]
        {
            ActionValue2.Int("cardID", hq.CardId), ActionValue2.Int("damage", amount),
            ActionValue2.Int("oldDefense", oldDefense), ActionValue2.Bool("destroyed", destroyed),
            ActionValue2.Bool("fromBond", fromBond),
        });
        Api.FireTrigger("OnFatigueDamage", null, side,
            eventArgs: new object?[] { side, amount, fromBond },
            namedArgs: new Dictionary<string, object?>
            { ["sideToApply"] = side, ["fatigueDamage"] = amount, ["fromBond"] = fromBond });
        return destroyed;
    }

    public CardInstance? DrawCard(Side side, bool startOfTurnDraw = false, int instigatorId = 0, bool cardSeen = false, bool opponentDraw = false)
        => DrawCardCore(side, startOfTurnDraw, instigatorId, cardSeen, opponentDraw, null);

    internal CardInstance? DrawSpecificCardFromDeck(Side side, int cardId, int instigatorId, bool cardSeen)
        => DrawCardCore(side, false, instigatorId, cardSeen, false, cardId);

    private CardInstance? DrawCardCore(Side side, bool startOfTurnDraw, int instigatorId, bool cardSeen, bool opponentDraw, int? selectedCardId)
    {
        var deck = State.Deck(side).ToList();
        // Specific draw's remove/add-to-top/draw sequence has no callbacks in
        // between. Select its ID directly while preserving object numbers and
        // the order of every other deck member. Invalid selection does nothing.
        var selected = selectedCardId.HasValue ? deck.Find(x => x.CardId == selectedCardId.Value) : null;
        if (selectedCardId.HasValue && selected is null) return null;
        if (deck.Count == 0)
        {
            if (ApplyFatigueDamage(side, fromBond: false))
                State.Finish(side.Opposite(), "Victory_DestroyHQ");
            return null;
        }

        var card = selected ?? deck[0];
        // DrawTopCardFromDeck i=62 selects handSide independently of deckSide.
        Side handSide = opponentDraw ? side.Opposite() : side;

        // DrawTopCardFromDeck i=558 removes membership before i=621 T3;
        // the object still has its deck location throughout that notification.
        State.WithDeckCardRemoved(side, card.CardId, () => Api.ExecuteOnAfterDeckChanged(side));

        // 「抽**之前**」的手牌数 —— 蓝图 `DrawTopCardFromDeck` i=712 就是在这个时刻
        // （牌已从牌库移除、还没进手牌）取 `FetchCardsByLocation(手牌).QtyInLocation`，
        // 而且同一个值在「没满」分支里被当成 `locationNumber` 用（= 追加下标 = 当前张数），
        // 反证它确实是「抽之前」。
        // ⚠️ 以前这里写的是 `State.Move(...)` **之后**再读 `Hand(side).Count()`，是「抽之后」，
        //    比蓝图多 1（`ZActionDrawCardFromDeck` 的 PreQtyInHand 一直偏大 1）。
        // FetchCardsByLocation excludes current autoplay tags and uses physical
        // location rather than current owner; query only after the T3 callbacks.
        int preQtyInHand = Api.CountCardsForHandCapacity(handSide.HandOf());

        List<ActionValue2> DrawValues(bool full)
        {
            var values = new List<ActionValue2>
            {
                ActionValue2.Int("instigatorID", instigatorId),
                ActionValue2.Str("side", handSide.ToWire()),
                ActionValue2.Int("cardID", card.CardId),
                ActionValue2.Str("cardType", card.Name),
                ActionValue2.Int("PreQtyInHand", preQtyInHand),
                ActionValue2.Bool("handIsFull", full),
            };
            if (opponentDraw) values.Add(ActionValue2.Bool("oppSide", true));
            return values;
        }

        // ★ 手牌上限：满了就烧牌，不进手牌。
        if (preQtyInHand >= GameState.HandCapacity)
        {
            Say($"{handSide.ToWire()} 手牌已满（{preQtyInHand}/{GameState.HandCapacity}），{card} 被弃掉");
            // i=772 directly assigns location: no locationNumber write,
            // discard rearrangement or CardLocationMoved notification.
            card.Location = CardLocation.Discard;
            card.RecalculateStats();
            FireSubAction("ZActionDrawCardFromDeck", DrawValues(true));
            // DrawTopCardFromDeck i=1194/1204 runs even on the full-hand path.
            if (cardSeen) Api.SetCardSeen(card.CardId, 0);
            return null;
        }

        // i=1042..1140 changes current side on successful receipt only;
        // OriginalOwner is preserved, and the full-hand branch never changes it.
        State.ChangeOwner(card, handSide, normalize: false);
        // DrawTopCardFromDeck i=712/1042 assigns the count, without rearranging existing hand cards.
        // i=1042..1189 directly writes fields: no CardLocationMoved helper.
        State.Move(card, handSide.HandOf(), locationNumber: preQtyInHand,
            notifyMoved: false, normalizeLocation: false);
        FireSubAction("ZActionDrawCardFromDeck", DrawValues(false));

        // 「别的卡被抽到手」触发点。**必须接**：`card_unit_85_pioneer_company` /
        // `card_unit_big_red_one` / `card_event_committed_crew` 三张光环都订阅它，
        // 靠它把 buff 补给**新入手**的牌（这就是实测「没打出指令前手牌里所有指令都显示 -1」
        // 那个现象的来源：光环进场时只补了当时手牌里的，后续抽上来的得靠这个事件）。
        //
        // ⚠️ **两个事件都要发**，这是本轮的 bug 修复点之一。
        //
        // 出处：`out/bp-cardfn.json` 函数 `ExecuteOnDrawnFromDeck(DrawnCardID, StartOfTurnDraw, drawnSide)`：
        //   i=132  `drawnCard.OnCardDrawnFromDeck()`      ← **自己**（此前从未派发）
        //   i=191  FetchAllCardsWithEventTrigger(42)      ; 42 = OnOtherCardDrawnFromDeck
        //   i=708  `item.OnOtherCardDrawnFromDeck(drawnCardID, StartOfTurnDraw, drawnSide)`
        //
        // 旧实现只发了 `OnOtherCardDrawnFromDeck`，而 `CardApi.FireTrigger` 的
        // `broadcast` 判定（`programName.StartsWith("OnOther")`）会**把主体自己排除**，
        // 兜底那一段又被 `subjectBroadcast` 挡住 ⇒ 22 张订阅 `OnCardDrawnFromDeck`
        // 的卡永远收不到（`card_event_guarilla_warfare_school` 这类"抽到牌时"效果全死）。
        FireEnteredHandFromDeckEvents(card, handSide, startOfTurnDraw);
        // DrawTopCardFromDeck i=1329..1474 checks the live tag after own/T42
        // events, then AddAutoPlayCards preserves first-insertion order by ID.
        // Consumption belongs to the outer action boundary, not this draw.
        if (CardApi.HasInstanceGameplayTag(card, "autoplay")) State.AddAutoPlayCards(card.CardId);
        // i=819 pushes the reveal continuation at i=1194, but i=1013 first
        // jumps to the own/T42 events at i=1274. Reveal runs after those events.
        // False is a no-op, not a request to clear existing revealed state.
        if (cardSeen) Api.SetCardSeen(card.CardId, 0);
        return card;
    }

    /// <summary>
    /// ExecuteOnDrawnFromDeck：有效对象先执行自身事件，再查询 T42。
    /// 此阶段不额外限制对象位置；是否进入该阶段由各抽牌入口决定。
    /// </summary>
    public void FireEnteredHandFromDeckEvents(CardInstance card, Side side, bool startOfTurnDraw)
    {
        var drawnNamed = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["drawnCardID"] = card.CardId,
            ["StartOfTurnDraw"] = startOfTurnDraw,
            ["drawnSide"] = (int)side,
        };

        Api.FireTrigger("OnCardDrawnFromDeck", card, side,
            eventArgs: new object?[] { startOfTurnDraw, (int)side },
            // ExecuteOnDrawnFromDeck i=89/118 validates the object only;
            // i=132 directly invokes it regardless of location/suppression.
            eventSubject: card, namedArgs: drawnNamed, includeUnavailableRecipients: true);

        Api.ExecuteOtherCardDrawnEvents(card, side, startOfTurnDraw);
    }

    private readonly Queue<(int CardId, bool PlayedDirectly)> _pendingGotchaPlayReceipts = new();

    /// <summary>Apply the rule-bearing Gotcha part of local ReceiveActionPlayCardFromHand.</summary>
    public void ApplyPendingGotchaPlayReceipts()
    {
        while (_pendingGotchaPlayReceipts.TryDequeue(out var receipt))
        {
            var card = State.ById(receipt.CardId);
            if (card is null || !Api.IsGotcha(card)) continue;
            // i=1294/1308: local direct-play notifications must not toggle twice.
            if (receipt.PlayedDirectly) continue;
            if (card.GotchaActivated > 0) card.GotchaActivated = 0;
            else
            {
                // i=3602..3762 reads current cost, not the cost at notification time.
                Api.JsonSetInt(card, "kreditsPayed", card.KreditCost);
                int next = 0;
                foreach (var other in State.AllCards)
                    if (Api.IsGotcha(other) && other.Owner == card.Owner && other.GotchaActivated > next)
                        next = other.GotchaActivated;
                // The receive function has no hand-location filter.
                card.GotchaActivated = next + 1;
            }
        }
    }

    private void NotifyPlayFromHand(CardInstance card, int instigatorId,
        int locationNumber, int targetId, bool playedDirectly, CardLocation? destination = null)
    {
        Effects.Blueprint.LiveRuleTables.TryGetEnumName("/Script/kards.ECardLocationEnum",
            (int)(destination ?? card.Location), out string locationName);
        var values = new List<ActionValue2>
        {
            ActionValue2.Int("cardID", card.CardId),
            ActionValue2.Int("instigatorID", instigatorId),
            ActionValue2.Str("location", locationName),
            ActionValue2.Int("locationNumber", locationNumber),
            ActionValue2.Int("enterPlayOnTurn", State.Turn),
            ActionValue2.Int("targetCardID", targetId),
            ActionValue2.Int("nonVisualLocationNumber", locationNumber),
        };
        // Direct function passes chooseOneIndex=0; normal Gotcha has no choices.
        if (playedDirectly)
        {
            values.Add(ActionValue2.Int("chooseOneIndex", 0));
            values.Add(ActionValue2.Bool("playedDirectly", true));
        }
        FireSubAction("ZActionPlayCardFromHand", values);
        if (Api.IsGotcha(card)) _pendingGotchaPlayReceipts.Enqueue((card.CardId, playedDirectly));
    }

    private bool _processingAutoPlayCards;

    /// <summary>Process autoplay rules; action boundaries apply local Gotcha receipts afterwards.</summary>
    public void ExecuteAutoPlayCards()
    {
        if (_processingAutoPlayCards) return;
        _processingAutoPlayCards = true;
        try
        {
            for (int index = 0; ; index++)
            {
                // Native i=157/345/542/797 re-reads the queue throughout the loop.
                var ids = State.GetAutoPlayCards();
                if (index >= ids.Length) break;
                var card = State.ById(ids[index]);
                if (card is null || card.Location is not (CardLocation.HandLeft or CardLocation.HandRight)) continue;
                ApplyNativeHandPlayCost(card);
                // PlayCardFromHand i=921/962/976 returns for Gotcha after cost
                // and play notification. It does not toggle activation or move it.
                // Gotcha records a notification; receipt application is a later batch stage.
                if (Api.IsGotcha(card))
                {
                    NotifyPlayFromHand(card, card.CardId, 0, 0, playedDirectly: false);
                    continue;
                }
                PlayCardFromHandCore(card, target: null, skipLeaveTrigger: false,
                    chargeKredits: false,
                    recordAction: false, toFrontline: false, locationNumber: 0,
                    instigatorID: card.CardId);
            }
            State.ClearAutoPlayCards();
        }
        finally { _processingAutoPlayCards = false; }
    }

    private void ApplyNativeHandPlayCost(CardInstance card, CardInstance? target = null)
    {
        // PlayCardFromHand i=61..304: the activation field alone selects refund,
        // and neither normal cost nor the autoplay tag is checked on that path.
        if (card.GotchaActivated > 0)
        {
            ChangeHandPlayKredits(card, Api.JsonGetInt(card, "kreditsPayed"));
            return;
        }
        if (CardApi.HasInstanceGameplayTag(card, "autoplay")) return;
        int cost = GetPlayCardCost(card, target);
        // PayCardCost does not partially deduct on failure. Its caller ignores
        // success, so the subsequent play flow still runs.
        if (cost <= State.Kredits(card.Owner)) ChangeHandPlayKredits(card, -cost);
    }

    private void ChangeHandPlayKredits(CardInstance card, int delta)
    {
        int old = State.Kredits(card.Owner);
        int slots = State.MaxKredits(card.Owner);
        // ChangeKreditsBySide clamps current kredits to the maximum possible,
        // not to the side's current slots (native getter reads game-state +0x338).
        int current = Math.Clamp(old + delta, 0, MaxKreditCap);
        State.SetKredits(card.Owner, current);
        FireSubAction("ZActionChangeKredits", new[]
        {
            ActionValue2.Int("triggerCardID", card.CardId),
            ActionValue2.Str("side", card.Owner.ToWire()),
            ActionValue2.Int("newMaxKredits", slots),
            ActionValue2.Int("oldMaxKredits", slots),
            ActionValue2.Int("newKredits", current),
            ActionValue2.Int("oldKredits", old),
        });
    }

    // ==================== 部署 ====================

    /// <summary>
    /// **安全网**：是否禁止打出「内核自己生成、但身份未经动作流确认」的卡（默认开）。
    ///
    /// 为什么需要它（真人玩家实测，2026-10-02）：我们发出一条 `PC`，而客户端那边
    /// **没有这张卡**（或那是个别的卡）⇒ 记牌器 +1、场上什么都没有 = 「虚空部署」。
    /// **虚空单位会立刻不同步**（客户端根本没有这个单位，后面所有攻击/移动全错位），
    /// 虚空指令稍好但也该避免。
    ///
    /// 判据见 <see cref="GameState.IsIdentityTrusted"/>：卡的 cardID 是内核分配器
    /// （`NextCardId`）发的 ⇒ 客户端未必认得；只有动作流用卡组码确认过它才可信。
    ///
    /// ⚠️ 回放路径要把它**关掉**（`ReplayRunner` 里设 false）：那里我们是在重放
    /// **客户端自己发过**的动作，动作流本身就是"客户端认得这张卡"的证据；
    /// 拒绝它只会让重建更差。
    /// </summary>
    public bool EnforceGeneratedCardTrust { get; set; } = true;

    /// <summary>
    /// 安全网是否也拦**指令**（默认拦）。单位是硬要求（虚空单位立刻不同步），
    /// 指令按用户口径是第二优先级 —— 置 false 可以只拦单位、放开指令。
    /// </summary>
    public bool BlockUntrustedOrders { get; set; } = true;

    /// <summary>能否打出这张牌（费用 + 目标 + 身份可信）。</summary>
    public bool CanPlay(CardInstance card, out string reason, CardInstance? target = null)
    {
        reason = "";

        // ★★ 安全网（见 `EnforceGeneratedCardTrust` 的注释）。
        //    必须在最前面 —— 身份不可信的卡连"能不能打"都不该问。
        if (EnforceGeneratedCardTrust
            && State.TrackGeneratedCardTrust
            && !State.IsIdentityTrusted(card)
            && (card.Definition.IsUnit || BlockUntrustedOrders))
        {
            // 记进 `UnimplementedCalls`，让审计的 ⑥ 段能看见"我们因为不确定放弃了多少次"
            // —— 静默跳过会让这个缺口永远查不出来。
            string key = $"<unverified-generated-card-not-played:{card.Name}>";
            State.UnimplementedCalls[key] = State.UnimplementedCalls.GetValueOrDefault(key) + 1;
            reason = "身份未核实（内核自己生成的卡，客户端可能没有这张）";
            return false;
        }

        if (card.Owner != State.ActiveSide)
        {
            reason = "不是当前行动方";
            return false;
        }

        if (card.Location != State.ActiveSide.HandOf())
        {
            reason = "不在手牌";
            return false;
        }

        // ⚠️ **死牌不能打**（2026-10-02 补）。
        //
        // 以前这道门不存在，于是「手牌里有一张已经死掉的牌」时内核会放行 ——
        // 接服务器之后 AI 真的打出了死牌（用户实测）。
        //
        // `IsAlive` 的语义是「还能参与结算」；`Location` 已经是弃牌堆/已移出的卡
        // 不该还能从手牌打出。这里和 `Attack` 的 `defender.IsAlive` 用同一个判据。
        if (!card.IsAlive)
        {
            reason = "这张牌已经不在场上（已死亡/已结算）";
            return false;
        }

        if (CardApi.IsWeatherCard(card) && !State.CanPlayWeatherCard())
        {
            reason = "本回合已经打过天气牌";
            return false;
        }

        if (GetPlayCardCost(card, target) > State.Kredits(card.Owner))
        {
            reason = "kredit 不足";
            return false;
        }

        // BP_Logic.CanPlayCardFromHand i=56..234 handles weather; i=468..717
        // checks affordability before invoking the card-local blocker at i=731.
        if (Api.IsBlockedFromPlay(card, out reason)) return false;

        if (card.Definition.IsOrder && State.HasGameplayRestriction(card.Owner,
            GameplayRestrictionType.CannotPlayOrders))
        {
            reason = "当前回合禁止打出指令";
            return false;
        }

        if (card.Definition.IsUnit && State.HasGameplayRestriction(card.Owner,
            GameplayRestrictionType.CannotDeployUnits))
        {
            reason = "当前回合禁止部署单位";
            return false;
        }

        if (card.Definition.IsUnit && HalfBoardFull(card.Owner))
        {
            reason = "半场已满";
            return false;
        }

        // BP_Logic.CanPlayCardFromHand i=1400: an order without a target
        // requirement skips enumeration; otherwise i=1482..2618 must find a
        // candidate passing its own and common target checks.
        if (card.Definition.IsOrder && card.SelectTargetOnPlayedFromHand
            && !Api.HasPlayableOrderTarget(card))
        {
            reason = "no_legal_play_target";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 蓝图 <c>PayCardCost</c> 的普通出牌费用：指定敌方卡牌时，目标的
    /// <c>KreditsTax_AsEnemyTarget</c> 叠加到当前卡费。同阵营目标不加税，
    /// 免费直出路径不调用这里的扣费分支。
    /// </summary>
    private static int GetPlayCardCost(CardInstance card, CardInstance? target)
    {
        if (target is null || target.Owner == card.Owner || target.KreditsTaxAsEnemyTarget <= 0)
        {
            return card.TotalKreditCost;
        }

        return card.TotalKreditCost + target.KreditsTaxAsEnemyTarget;
    }

    /// <summary>
    /// 半场是不是满了（单位出不来）。
    ///
    /// ⚠️ **半场和前线是两个独立上限**，不能合计 —— 这里原先写的是
    /// `Board(side).Count() >= SupportLineCapacity + State.FrontlineLimiter`
    /// （= 4 + 1 = 5），把「半场 4 + 前线 1」当成一个总数。
    /// 蓝图 `FetchCardsByLocation` 里 case 5,6 和 case 7 各给各的 `MaxQty`
    /// （半场 5、前线 5），判满也是分别判的。
    ///
    /// 出厂口 `BP_Logic::CanPlayCardFromHand` 印证：i=890 只对单位查，
    /// i=959 `IsLocationFull(SupplyLineLocationFromSide(side))` ——
    /// **只查半场（5/6），不查前线**。
    ///
    /// 计数要**含 HQ**（蓝图的循环只按 location 过滤、不排除 HQ）。
    /// </summary>
    private bool HalfBoardFull(Side side)
        => State.Cards(side, side.HqOf()).Count >= GameState.HalfBoardCapacity;

    /// <summary>从手牌打出一张牌。</summary>
    /// <param name="skipLeaveTrigger">
    /// 跳过「离场」触发点。给「同一张卡先离场再进场」的移位用（见 <see cref="MoveUnit"/>）：
    /// 不跳过的话 <c>MoveUnit</c> 里那次 `OnLeaveBoardOrOwner` 和这里的
    /// `OnEnterPlay` 会各触发一次，光环类卡会被撤销再重挂。
    /// </param>
    public bool PlayCard(CardInstance card, CardInstance? target = null, bool skipLeaveTrigger = false)
    {
        BeginEffectResolution();
        try
        {
            bool completed = PlayCardActionCore(card, target, skipLeaveTrigger);
            if (completed)
            {
                ExecuteAutoPlayCards();
                ApplyPendingGotchaPlayReceipts();
            }
            return completed;
        }
        finally { EndEffectResolution(); }
    }

    private bool PlayCardActionCore(CardInstance card, CardInstance? target, bool skipLeaveTrigger)
    {
        if (!CanPlay(card, out string reason, target))
        {
            return false;
        }

        if (Api.IsGotcha(card))
        {
            RecordAction("XActionPlayCardFromHand", card.Owner, new Dictionary<string, object?>
            {
                ["cardID"] = card.CardId,
                ["location"] = "Hand",
                ["side"] = card.Owner.ToWire(),
            });
            ApplyNativeHandPlayCost(card, target);
            NotifyPlayFromHand(card, card.CardId, card.LocationNumber, target?.CardId ?? 0,
                playedDirectly: false);
            return true;
        }

        return PlayCardFromHandCore(card, target, skipLeaveTrigger,
            chargeKredits: true, recordAction: true,
            toFrontline: false, locationNumber: -1, instigatorID: card.CardId);
    }

    /// <summary>
    /// 蓝图 <c>PlayCardDirectlyFromHand</c> 的免费/特殊出牌路径。
    ///
    /// Gotcha 在手牌中只切换激活号，然后提前返回，不经过普通打牌链。
    /// 非 Gotcha 只经过位置移动及Method=2入场事件，不执行普通手牌打牌链。
    /// </summary>
    public bool PlayCardDirectlyFromHand(CardInstance card, bool toFrontline,
                                         int instigatorID, int locationNumber)
    {
        if (card.Location != card.Owner.HandOf() || !card.IsAlive)
        {
            return false;
        }

        if (Api.IsGotcha(card))
        {
            // PlayCardDirectlyFromHand i=271..441/1652..2632 toggles the
            // activation, preserves the hand location/number, then i=853/908
            // returns before CardLocationMoved and enter-play effects.
            Api.AssignGotchaActivatedOnPlayFromHand(card);
            NotifyPlayFromHand(card, instigatorID, card.LocationNumber,
                card.BlueprintVariables.GetValueOrDefault("targetOverride") is CardInstance overrideTarget ? overrideTarget.CardId : 0,
                playedDirectly: true);
            return true;
        }

        CardLocation destination = toFrontline ? CardLocation.BoardFrontline : card.Owner.HqOf();
        int count = State.CardsUnordered().Count(x => x.Location == destination);
        int capacity = toFrontline ? State.FrontlineCapacity : GameState.HalfBoardCapacity;
        if (count >= capacity || toFrontline && Api.DoesSideControlTheFrontline(card.Owner.Opposite()))
        {
            // i=1260/1391 -> i=1302: source=0, skipSubAction=false, ForceDiscard=true.
            Api.ForceDiscardHandCardForBlockedDirectPlay(card);
            return false;
        }

        BeginEffectResolution();
        try
        {
            var oldLocation = card.Location;
            int slot = locationNumber >= 0 ? locationNumber : count;
            int targetId = card.BlueprintVariables.GetValueOrDefault("targetOverride") is CardInstance directTarget
                ? directTarget.CardId : 0;
            // i=757 notifies before i=2809 moves. This path never calls CardPlayedFromHand.
            NotifyPlayFromHand(card, instigatorID, slot, targetId, playedDirectly: true, destination);
            Api.SetHandEdgesWhenPlayed(card);
            // InjectCardIntoLocation: create a gap, then RefreshLocationStatus
            // compacts both physical locations before movement callbacks.
            foreach (var other in State.CardsUnordered().Where(x => x.Location == destination))
                if (other.LocationNumber >= slot) other.LocationNumber++;
            State.Move(card, destination, slot, moveReason: CardMoveReason.PlayCardForce,
                notifyMoved: false, normalizeLocation: false);
            Effects.Blueprint.LiveRuleTables.TryGetEnumName("/Script/kards.ECardLocationEnum", (int)oldLocation, out string oldName);
            Effects.Blueprint.LiveRuleTables.TryGetEnumName("/Script/kards.ECardLocationEnum", (int)destination, out string newName);
            Effects.Blueprint.LiveRuleTables.TryGetEnumName("/Game/Structs/CardMoveReason.CardMoveReason",
                (int)CardMoveReason.PlayCardForce, out string reasonName);
            FireSubAction("ZActionMoveCardToNewLocation", new[] {
                ActionValue2.Int("cardID", card.CardId), ActionValue2.Int("instigatorID", instigatorID),
                ActionValue2.Str("oldLocation", oldName), ActionValue2.Str("newLocation", newName),
                ActionValue2.Int("newLocationNumber", slot), ActionValue2.Int("nonVisualLocationNumber", slot),
                ActionValue2.Str("moveReason", reasonName),
                ActionValue2.Int("attackLeft", card.RemainingAttacks),
                ActionValue2.Int("movementLeft", card.HasMovedThisTurn ? 0 : 1),
            });
            Api.RefreshLocationStatus(new[] { oldLocation, destination });
            State.CardMoved?.Invoke(card, oldLocation, destination, false, CardMoveReason.PlayCardForce);
            // Follow the execution-flow stack: clear/persist -> fields -> entry -> Alpine.
            if (Api.JsonClear(card, "countdown_timer")) Api.PersistCustomFields(card);
            card.EnteredPlayOnTurn = State.Turn;
            card.KreditCost = card.IsSalvaged ? Math.Min(3, card.Definition.Kredits) : card.Definition.Kredits;
            card.CardSeen = false;
            ExecuteOnEnterPlayEvents(card, 2);
            if (card.Keywords.Contains(Keyword.Alpine)) Api.GiveAlpineBonus(card);
            return true;
        }
        finally { EndEffectResolution(); }
    }

    private bool PlayCardFromHandCore(CardInstance card, CardInstance? target,
                                      bool skipLeaveTrigger, bool chargeKredits,
                                      bool recordAction, bool toFrontline,
                                      int locationNumber, int instigatorID)
    {
        BeginEffectResolution();
        try
        {
            return PlayCardFromHandCoreImpl(card, target, skipLeaveTrigger, chargeKredits,
                recordAction, toFrontline, locationNumber, instigatorID);
        }
        finally
        {
            EndEffectResolution();
        }
    }

    private bool PlayCardFromHandCoreImpl(CardInstance card, CardInstance? target,
                                          bool skipLeaveTrigger, bool chargeKredits,
                                          bool recordAction, bool toFrontline,
                                          int locationNumber, int instigatorID)
    {
        if (CardApi.IsWeatherCard(card))
        {
            State.SetHasPlayedWeatherCardThisTurn(true);
        }

        if (chargeKredits)
        {
            State.AddKredits(card.Owner, -GetPlayCardCost(card, target));
        }

        // Gotcha uses separate processing/receipt branches before this common path.

        if (recordAction)
        {
            RecordAction("XActionPlayCardFromHand", card.Owner, new Dictionary<string, object?>
            {
                ["cardID"] = card.CardId,
                ["location"] = "Hand",
                ["side"] = card.Owner.ToWire(),
            });

            FireSubAction(card.Definition.IsUnit || card.Definition.IsLocationCard
                ? "ZActionPlayCardFromHand"
                : "ZActionPlayOrderCardFromHand", new[]
            {
                ActionValue2.Int("instigatorID", instigatorID),
                ActionValue2.Int("targetCardID", target?.CardId ?? 0),
                ActionValue2.Int("playedDirectly", 1),
            });
        }

        if (!skipLeaveTrigger && card.Location.IsBoard())
        {
            // 已经在场上的卡被打出（`MoveUnit` 的「先回手再进场」路径）：
            // 先广播离场触发点，让挂在它身上的光环把 buff 撤掉。
            FireLeaveTrigger(card, CardLocation.BoardFrontline);
        }

        // PlayCardFromHand i=1988 -> CardPlayedFromHand i=2114.
        if (card.Definition.IsUnit || card.Definition.IsLocationCard)
        {
            PlaceOnBoard(card, toFrontline, locationNumber,
                chargeKredits ? CardMoveReason.PlayCardPlayerMove : CardMoveReason.PlayCardForce);
        }
        else
        {
            // 指令牌：先进弃牌堆再结算（游戏里指令是"用完即弃"，
            // 结算期间它已经不在手牌里了）
            State.Move(card, CardLocation.Discard, moveReason:
                chargeKredits ? CardMoveReason.PlayCardPlayerMove : CardMoveReason.PlayCardForce);
        }

        // 「本回合进场的回合数」——**指令也要记**。
        // 卡蓝图里的实例变量 `enterPlayOnTurn` 就是它，实测
        // `card_event_committed_crew` 的每个分支都先判 `enterPlayOnTurn > 0`
        // （"这张牌是不是本回合打出的"）—— 只有单位记的话，指令类光环
        // （committed_crew 自己就是 10 费指令）会全部静默失效。
        // `PlaceOnBoard` 里也会设一次，值相同，重复设没有副作用。
        card.EnteredPlayOnTurn = State.Turn;

        if (card.Name is "card_event_sunny1_blue_sky" or "card_event_rain1_mist" or "card_event_storm1_gale")
            Api.RunCardEffect(card, target);
        State.StopFurtherActions = false;
        var cardsDone = new HashSet<int>();
        var afterPlayGotchas = new List<CardInstance>();
        foreach (int id in Api.GetActiveGotchasOrdered())
        {
            if (State.StopFurtherActions) return true;
            if (State.ById(id) is not { } gotcha) continue;
            if (CardApi.CustomNameHasAttribute(gotcha, "customName1", "gotchaAfterPlayFromHand"))
                afterPlayGotchas.Add(gotcha);
            else
            {
                Api.FireTrigger("OnOtherCardPlayedFromHand", gotcha, gotcha.Owner,
                    selfProgramName: "OnOtherCardPlayedFromHand", eventSubject: card,
                    namedArgs: new Dictionary<string, object?> { ["cardPlayed"] = card });
                cardsDone.Add(id);
            }
        }
        if (State.StopFurtherActions) return true;

        // ---- 未揭示隐蔽牌的专用触发点 ----
        //
        // 蓝图 `CardPlayedFromHand` 的顺序是：先由出牌卡自己收到
        // `OnCovertCardPlayedFromHand`，再取触发点 60 的接收者并派发
        // `OnOtherCovertCardPlayedFromHand`，随后才进入普通的
        // `OnBeforeOtherCardPlayedFromHand`。主体事件可能改变场面，故广播
        // 接收者快照必须在主体事件完成后取得。
        if (CardApi.IsUnrevealedCovertCard(card))
        {
            Api.FireTrigger("OnCovertCardPlayedFromHand", card, card.Owner);
            var covertTriggerSnapshot = Api.CaptureTriggerSnapshot();
            Api.FireTrigger("OnOtherCovertCardPlayedFromHand", card, card.Owner,
                eventArgs: new object?[] { card }, eventSubject: card,
                recipientSnapshot: covertTriggerSnapshot);
        }

        // ---- 「别的卡**即将**从手牌被打出」----
        //
        // 出处 `out/bp-cardfn.json` 函数 `CardPlayedFromHand`：
        //   i=1322..1386  遍历 FetchAllCardsWithEventTrigger(19) 并把主体排除
        //                 （i=1386 `NotEqual_ObjectObject(item, cardPlayed)`）
        //   i=1493        `item.OnBeforeOtherCardPlayedFromHand(cardPlayed)`
        // 触发号 19 的依据：`ERegisteredCardFunction.h` 逐项数下来第 20 项
        //   = `OnBeforeOtherCardPlayedFromHand`。
        // 签名 `BaseCardObject.h:739`。**没有"自己"那一路** —— 39 张订阅者全是别人。
        // PlayCardFromHand moves the card before CardPlayedFromHand callbacks.
        // ⚠️ 必须显式 `broadcastName: true`：这个名字以 `OnBefore` 开头、不是 `OnOther`，
        //    靠命名约定判广播会把它当成"只发给主体" ⇒ 39 张订阅者全部收不到。
        Api.FireTrigger("OnBeforeOtherCardPlayedFromHand", card, card.Owner,
            eventArgs: new object?[] { card },
            eventSubject: card,
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardPlayed"] = card,
            },
            broadcastName: true);

        // CardPlayedFromHand i=1539..2066: Bond uses the start-turn snapshot,
        // then ordinary Cipher reveals cards before entry/deployment.
        if (CardApi.HasBond(card) && !State.ActiveBondFactions.Contains(card.Definition.Faction ?? "")
            && ApplyFatigueDamage(card.Owner, fromBond: true))
        {
            State.Finish(card.Owner.Opposite(), "Victory_DestroyHQ");
            return true;
        }
        if (card.Cipher > 0 && !Api.IsGotcha(card))
            Api.SetCardsSeenByCipher(card.Cipher, card.CardId, card.Owner);

        // ---- ② 本回合打出过的牌 ----
        // 放在 `OnEnterPlay` **之前**：光环的 `anyOrderPlayedThisTurn` 是
        // 「本回合第一张指令打完了没有」的判据，而 `OnEnterPlay` 是**刚打出的这张**
        // 自己的进场时机 —— 它自己当然算"已经打出"。
        State.CardsPlayedThisTurn.Add(card);

        // T51 then T43 are captured before deployment; each unique recipient
        // receives both callbacks after deployment (i=2080/2132 -> i=6750/6795).
        var triggerCards = Api.CaptureTriggerSnapshot().ToArray();
        var playedCardTriggerSnapshot = triggerCards
            .Where(x => x.Definition.IsRegisteredFor("OnOtherCardPlayedFromHand"))
            .Concat(triggerCards.Where(x => x.Definition.IsRegisteredFor("OnOtherCardEnterPlay")))
            .ToArray();
        Api.FireTrigger("OnEnterPlay", card, card.Owner,
            eventArgs: new object?[] { 1 },
            namedArgs: new Dictionary<string, object?> { ["Method"] = 1 });

        void ContinuePlay(bool waited)
        {
            // AfterWait uses the current active gotchas and the pre-deployment
            // T51/T43 snapshot. Immediate play uses the captured after-play list.
            if (waited) State.StopFurtherActions = false;
            IEnumerable<CardInstance> gotchas = waited
                ? Api.GetActiveGotchasOrdered().Select(State.ById).OfType<CardInstance>()
                : afterPlayGotchas;
            if (waited)
            {
                gotchas = gotchas.ToArray();
                foreach (var gotcha in gotchas)
                    if (!CardApi.CustomNameHasAttribute(gotcha, "customName1", "gotchaAfterPlayFromHand"))
                        cardsDone.Add(gotcha.CardId);
            }
            foreach (var observer in gotchas.Concat(playedCardTriggerSnapshot))
            {
                if (State.StopFurtherActions) break;
                if (!cardsDone.Add(observer.CardId)) continue;
                Api.FireTrigger("OnOtherCardPlayedFromHand", observer, observer.Owner,
                    selfProgramName: "OnOtherCardPlayedFromHand", eventSubject: card,
                    eventArgs: new object?[] { card },
                    namedArgs: new Dictionary<string, object?> { ["cardPlayed"] = card });
                Api.FireTrigger("OnOtherCardEnterPlay", observer, observer.Owner,
                    selfProgramName: "OnOtherCardEnterPlay", eventSubject: card,
                    eventArgs: new object?[] { card, 1 },
                    namedArgs: new Dictionary<string, object?> { ["cardPlayed"] = card, ["Method"] = 1 });
            }
            Api.GiveAlpineBonus(card);
        }

        // CardPlayedFromHand runs the Pincer relation after OnEnterPlay and
        // before the card's own OnPlayedFromHand (si=4561..4728).  Deployment
        // cancellation takes this branch too; it only bypasses the own effect.
        if (card.Keywords.Contains(Keyword.Pincer) && target is not null)
            Api.ApplyPincerEffects(card, target);

        // CardPlayedFromHand sets ExecuteWaitPlayFromHand before invoking the
        // card's own effect. A one-card selection resolves inside that effect,
        // so registering this continuation afterwards leaves a stale wait.
        card.CurrentTarget = target;
        bool waitsForTarget = card.Definition.WaitForHandTarget;
        if (waitsForTarget)
            _waitPlayContinuations[card.CardId] = () => ContinuePlay(waited: true);
        cardsDone.Add(card.CardId);

        bool effectExecuted = RunDeploymentEffect(card, target);

        if (!waitsForTarget || !effectExecuted)
        {
            _waitPlayContinuations.Remove(card.CardId);
            ContinuePlay(waited: false);
        }
        // The inline singleton/empty-selection paths already consumed the
        // continuation. Multiple-choice paths retain it for the queued reply.

        CheckDeaths();
        return true;
    }

    /// <summary>
    /// CardPlayedFromHand: deployment cancellation skips the own effect only;
    /// ChooseOne/weather/Pincer and played/entry observers continue (i=4297..5703).
    /// Non-targeted deployment uses T23 to determine extra own-effect executions.
    /// </summary>
    private bool RunDeploymentEffect(CardInstance card, CardInstance? target, int? instigatorId = null,
        bool nonTargeting = false)
    {
        // si=3640：没有 hasDeployment 的卡（全部指令 + 21 张没这个字段的单位）走 si=5840，
        // 那条路上 `_triggerMultiple` 恒 0 ⇒ 只跑一次，且**没有取消钩子**。
        if (!card.Keywords.Contains(Keyword.Deployment))
        {
            Api.RunCardEffect(card, target);
            return true;
        }

        // si=3676..4278：事件 14 的取消钩子。
        if (Api.FireDeploymentCancelHook(card, instigatorId))
        {
            return false;   // Own deployment is cancelled; PlayCard continues the common chain.
        }

        // si=5702/5750：只在「非指向性」部署时取翻倍数（targetCardID == 0）。
        int triggerMultiple = nonTargeting || target is null
            ? Api.SumDeploymentTriggerMultiple(card, instigatorId)
            : 0;

        if (card.Definition.WaitForHandTarget && triggerMultiple > 0)
        {
            card.ExtraPlayTriggers = triggerMultiple;
            Api.RunCardEffect(card, target);
            return true;
        }

        // si=6114（第 1 次）+ si=6230..6434（再 triggerMultiple 次）。
        for (int i = 0; i <= triggerMultiple; i++)
        {
            Api.RunCardEffect(card, target);
        }
        return true;
    }

    /// <summary>供 <c>CardApi.TriggerDeployment</c> 调用的主动部署效果入口。</summary>
    public void RunDeploymentEffectForTrigger(CardInstance card, int instigatorId)
    {
        // TriggerDeployment i=59/524/956/1119 is a distinct path: it always
        // repeats directly, with no wait flag or extra-play JSON counter.
        if (!card.Keywords.Contains(Keyword.Deployment) || Api.FireDeploymentCancelHook(card, instigatorId)) return;
        BeginEffectResolution();
        try
        {
            int additional = Api.SumDeploymentTriggerMultiple(card, instigatorId);
            for (int i = 0; i <= additional; i++) Api.RunCardEffect(card, card.CurrentTarget);
        }
        finally { EndEffectResolution(); }
    }

    /// <summary>
    /// 把刚打出的单位放到**自己半场（支援线）**。
    ///
    /// ⚠️ **不是"前线有空位就上前线"**（那是以往的写法，也是错的）。
    /// 真实数据：250 条真实快照里**前线为空的占 83%**（207/250）——
    /// 单位绝大多数待在底线，上前线是**主动动作**（走 `MoveUnit`，
    /// 且要过「对面占着就推不进去」那条互斥检查，见 <see cref="MoveUnit"/>）。
    ///
    /// 出厂口 `BP_Logic::CanPlayCardFromHand` i=959 判的也是
    /// `IsLocationFull(SupplyLineLocationFromSide(side))` —— **只查半场**，
    /// 说明部署的落点就是半场（5/6），和前线的空位无关。
    ///
    /// `locationNumber` 按蓝图 `GetNextCardLocationNumber` 的规则「追加到队尾」
    /// （`= QtyInLocation`），而且计数**含 HQ** —— 所以第一个单位的槽位是 1，不是 0。
    /// </summary>
    private void PlaceOnBoard(CardInstance card, bool toFrontline = false, int locationNumber = -1,
                              CardMoveReason moveReason = CardMoveReason.PlayCardPlayerMove)
    {
        card.EnteredPlayOnTurn = State.Turn;

        CardLocation destination = toFrontline ? CardLocation.BoardFrontline : card.Owner.HqOf();
        int slot = locationNumber >= 0
            ? locationNumber
            : State.Cards(card.Owner, destination).Count;
        State.Move(card, destination, slot, moveReason: moveReason);
        Say($"{card} 部署到{(toFrontline ? "前线" : "半场")} {destination} 槽位 {slot}");
    }

    // ==================== 移动 ====================

    // 推进前线的规则（= `MoveUnit`，对应 `XActionMoveCardToLine` /
    // `MoveUnitFromSupportToFrontLine`；⚠️ 这段文字原先挂在 `ClearExpiredSuppression`
    // 头顶上，那个方法删掉之后它会变成"没有归属的 XML 文档注释"，所以改成普通注释，
    // 内容一字未动）：
    //
    //   `BP_CardFunctions::MoveUnitFromSupportToFrontLine`（27 条语句）：
    //   i=281  (card.GetOppositeSide() == GameStateRef.GetFrontlineOwnerSide())
    //   i=333      拒绝                       ← ★ 对面占着前线 ⇒ 推不进去
    //   i=468  FetchCardsByLocation(7, …) → isLocationFull
    //   i=543      拒绝                       ← 我方占着但已满(5) ⇒ 也推不进去
    //
    //   **没有"把对面挤回半场"这条规则** —— 唯一的赶人手段是
    //   `card_unit_black_prince` 的打出效果 `MakeCardRetreat(GetAllCardsInFrontline)`。
    //
    //   拥有权由 `BP_CardFunctions::UpdateFrontlineIfNeeded` 维护：
    //   前线空 → owner = NotAvailable；非空 → owner = 第一张牌的 side。

    // ★★ 2026-10-02（第三轮）：这里原先有一个 `ClearExpiredSuppression(Side side)`
    //（遍历 `State.CardsUnordered()`，把 `SuppressedOnTurn < State.Turn` 且主人正在结束
    //  回合的卡交给 `Api.RestoreAfterSuppression` 还原）—— **整个方法已删**。
    //
    // 删除依据（两方独立证据，都指向「永不解除」）：
    //   · 玩家（雪雾）权威定义：「**抑制**：使被抑制的单位失去所有特效和关键字
    //     （**压制不受影响**、老兵变回原形、所有增益失效）。解除时机：**【永不解除】**
    //     —— 一直白板到游戏结束。」
    //   · 蓝图：`isSuppressed` 全库**只有一处写点，而且写的是 `True`**
    //     （`ref/kards-sim/KardsSim/Generated/_deps/../BP_CardFunctions.g.cs:35781`）；
    //     `SetMember(…, "isSuppressed", …)` **没有任何一处写 `False`**。
    //     规则表 `klink bot/docs/KARDS基础规则参考.md` 只给**压制**写了移除时机
    //     （`:160`「压制效果于单位所有者下个回合结束时移除」），抑制那一节（`:123-125`）
    //     **没有时机** ⇒ 这条"到期解除"是**内核自己发明的**，删掉它 = 抑制永不解除，
    //     与客户端（锁步模型下的唯一真相）一致。
    //
    // ⚠️ 连带后果：`CardApi.RestoreAfterSuppression` 因此**没有产品调用方**（该还原路径
    //    当前不可达）—— 本体**保留**（摘除/还原是成对能力，也是未来真需要时唯一入口），
    //    自测里改为**手工直调**验证它仍然完整可用。
    // ⚠️ 与**压制**（`Pinned`，中文「压制」）无关：压制**有**蓝图依据（规则表 `:160` +
    //    `_deps/BP_Logic.g.cs:2218` `DecrementPinnedTurnsEndTurn`），到期递减照旧保留。
    //    两个关键字**别一起改**。

    /// <summary>
    /// 钉住的到期递减 —— 对应 `BP_Logic::DecrementPinnedTurnsEndTurn`
    /// （`_deps/BP_Logic.g.cs:2218`）。
    ///
    /// 蓝图逻辑（逐条，`L_XXXX` 是字节偏移、即注释里说的 `i=`）：
    /// <code>
    /// L_003E i=62   遍历 GetAllCardInBattle()
    /// L_0171 i=369  if (card.pinnedTurns &lt; 1) → 跳过         ; 没钉住就不管
    /// L_0202 i=514  if (card.pinnedTurns &gt; 1) ChangedPinnedTurns(…, -1) → 回循环
    ///                                                       ; 递减，但**不**解除
    /// L_02CD i=717  RemovePin(card)                         ; 减到 1 ⇒ 解除
    /// </code>
    /// 这个函数没有side参数，遍历全部对局卡；>1分支另经在场单位门。它在
    ///    **每一个回合结束时**各跑一次（不是只在自己回合）。配上
    ///    `CardApi.PinUnit` 的初值（`IsSideActive(卡) ? 3 : 2`）正好等于
    ///    权威规则表的「钉住/压制于单位所有者**下个回合结束时**移除」。
    ///
    /// ⚠️ 抑制（`Suppress`，中文「抑制」）那边**已经没有任何到期清理**了：
    ///    2026-10-02（第三轮）删掉了 `ClearExpiredSuppression`（玩家确认抑制【永不解除】，
    ///    蓝图 `isSuppressed` 全库无写 `False` 处）⇒ 本函数是 `EndTurn` 里唯一的到期机制。
    ///    钉住这边蓝图是**数回合结束次数**（2 或 3），不做 side 过滤 —— 各自忠实于各自蓝图的形状。
    /// </summary>
    private void DecrementPinnedTurnsEndTurn()
    {
        // BP_Logic refreshes GetAllCardInBattle at the loop condition and
        // again before indexing. Callbacks can append/remove/move cards.
        for (int index = 0; index < State.AllCardsInBattleOrder.Count; index++)
        {
            var card = State.AllCardsInBattleOrder[index];
            if (card.PinnedTurns > 1) Api.ChangePinnedTurns(card, -1, 0);
            else if (card.PinnedTurns == 1) Api.RemovePin(card);
        }
    }

    /// <summary>
    /// **被钉住 ⇒ 不能移动、也不能攻击**。判据**只有这一份**，
    /// <see cref="MoveUnit"/> 与 <see cref="Attack"/> 共用（不再各写一套）。
    ///
    /// ## 移动侧的出处：`BP_Logic::CanCardDoAnything`（**不是** `MoveCardToFrontline`）
    ///
    /// ⚠️ 先排掉一个坑：`BP_CardFunctions::MoveUnitFromSupportToFrontLine`
    /// （`BP_CardFunctions.g.cs:27437`，即我们 `MoveUnit` 的对应物）**通篇没有钉住门**
    /// —— 它只判 `cantMove` 自定义能力（`:27455`）、在不在场上（`:27447`）、
    /// 前线归属（`:27471`/`:27481`）、前线满没满（`:27487`）。
    /// 钉住门写在**更外层**的「这张卡还能不能操作」判据里：
    ///
    /// `_deps/BP_Logic.g.cs:731` `CanCardDoAnything`，函数体**第一条**判据：
    /// <code>
    /// L_00C6 i=198  HasCustomAbility(_card, "canOperateWhilePinned") -> doesIt
    /// L_0106 i=262  IsPinned(_card)                                  -> isPinned
    /// L_012F i=303  Not_PreBool(doesIt)                              -> !doesIt
    /// L_0175 i=373  and = isPinned &amp;&amp; !doesIt
    /// L_019B i=411  or  = IsLocation(_card) || and
    /// L_01C1 i=449  if (or) { canIt = False; return; }   ← 直接返回，后面全不跑
    /// </code>
    /// 它在**攻击检查（`i=2608` 调 `cardsCheckFunctions::CanAttack`）与移动检查
    /// （`i=2817`：`location != 7` &amp;&amp; `HasMovementLeft` &amp;&amp; `!CantMove`）之前**，
    /// 而且**完全不看位置** ⇒ 钉住挡的是"这个单位还能不能操作"，
    /// **不是只挡"推进前线"**（支援线内换位同样挡；何况 `i=2817` 那条
    /// `location != 7` 本来就禁止已经在前线的单位再移动）。
    ///
    /// ## 攻击侧的出处（同一判据的另一份实现）
    ///
    /// `_deps/cardsCheckFunctions.g.cs:368` `CanAttack`：
    /// `HasCustomAbility(attackerCard,"canOperateWhilePinned")` + `IsPinned(attackerCard)`
    /// ⇒ `canAttack = false; failReason = "unit_is_pinned"`（`:401`/`:403`）。
    /// **两侧的例外同名同形** ⇒ 这里一起按 `canOperateWhilePinned` 放行。
    ///
    /// ## 为什么现在才补（日志实证，2026-10-02）
    ///
    /// 对局 `637706`（`rel/data/fyserver/bot-log/bot-20261002.log`）：
    /// <code>
    /// t12  ✗ 攻击 #67 card_unit_85_pioneer_company → … 被拒：攻击者被钉住  （:140-144）
    /// t12  CHOSEN 移动 #67 @BoardFrontline#1 → 槽位 0                    （:159）← 居然动了
    /// t14  ✗ 攻击 #58 card_unit_111th_indian_brigade → … 被拒：攻击者被钉住 （:175-179）
    /// t14  CHOSEN 移动 #58 @BoardFrontline#0 → 槽位 0                    （:194）← 又动了
    /// </code>
    /// 即 `Attack` 有这道门、`MoveUnit` 没有 —— 正是玩家报的
    /// 「被压制（= `Pinned`；中文客户端把 Pin 译作"压制"）还能上前线」。
    /// </summary>
    private bool PinnedBlocksOperation(CardInstance card)
        => card.IsPinned
           && !Api.HasCustomAbility(card, "canOperateWhilePinned");

    /// <summary>推进（不要原因）。见带 <c>out reason</c> 的重载。</summary>
    public bool MoveUnit(CardInstance unit, int targetSlot)
        => MoveUnit(unit, targetSlot, out _);

    /// <summary>
    /// 推进。**带拒绝原因**（诊断用）—— 原因由**同一套判据**产出，不是另写一份。
    ///
    /// ⚠️ 为什么要加 `out reason`（2026-10-02）：照 <see cref="Attack"/> 的做法。
    /// 审计 ⑤b 对 `ML` 只报「移动被拒（当前 BoardHqLeft）」—— **位置是对的、
    /// 被拒的原因是别的**，而 `MoveUnit` 有 7 道门，光看"被拒"根本分不清是哪一道。
    /// 实测对局 `773639` 的 `#45 t9 ML`（人类推前线）就卡在这里。
    /// </summary>
    public bool MoveUnit(CardInstance unit, int targetSlot, out string reason)
    {
        BeginEffectResolution();
        try
        {
            bool completed = MoveUnitActionCore(unit, targetSlot, out reason);
            if (completed)
            {
                ExecuteAutoPlayCards();
                ApplyPendingGotchaPlayReceipts();
            }
            return completed;
        }
        finally { EndEffectResolution(); }
    }

    private bool MoveUnitActionCore(CardInstance unit, int targetSlot, out string reason)
    {
        reason = "";

        if (unit.Owner != State.ActiveSide || !unit.Location.IsBoard() || unit.IsHq)
        {
            reason = $"单位不是行动方的场上单位（位置={unit.Location} 归属={unit.Owner} " +
                     $"行动方={State.ActiveSide} 是HQ={unit.IsHq}）";
            return false;
        }

        // ★ 死的不能移动（2026-10-02 用户实测：AI 移动了死亡单位）。
        //
        // 死单位 `Defense = 0` 但 `Location` **仍是** `BoardFrontline` / `BoardSupport`，
        // 而 `Location.IsBoard()` 只判**区域**、不判死活 ⇒ 光靠上面那句拦不住。
        // `CanMoveThisTurn` 现在也判了 `AliveOnBoard`，这里再显式写一道：
        // 「死亡 ⇒ 不能行动」是独立成立的规则，不该只寄在别的谓词里
        // （以前就是因为两处都没写，才让 AI 移了死单位）。
        if (!unit.AliveOnBoard)
        {
            reason = $"单位已死（在场上但防御 ≤ 0：{unit.Defense}）";
            return false;
        }

        if (Api.HasCustomAbility(unit, "cantMove"))
        {
            reason = "单位带有 cantMove 能力";
            return false;
        }

        // ★★ 2026-10-02（第二轮）：这里原先判 `Keyword.Suppressed` 并拒绝移动 ——
        //    **已删，那是误读**。`Suppress`（中文「抑制」）与 `Pin`（中文「压制」）
        //    是两个关键字：只有**压制**禁止行动（下面那道 `PinnedBlocksOperation`），
        //    抑制的语义是"失去所有关键词与增益"（见 `CardApi.SuppressUnit`）。
        //    蓝图证据：移动侧的判据是 `BP_Logic::CanCardDoAnything`，
        //    全文**没有** `isSuppressed`（BP_Logic.g.cs 里 0 次）；
        //    `isSuppressed` 只出现在"移动之后要不要广播事件"那种分流上
        //    （`ExecuteOnCardLocationMoved` `:15475`、`ExecuteOnMoveToFrontlineCardEffects`
        //     `:16595` —— 那是**事件广播**的门，不是行动合法性的门）。
        //
        // ★★ **被「压制」（`Pin`）的单位不能移动**（2026-10-02 玩家实测 + 日志实证）——
        //    这道门**保留**：玩家原话「压制不会受到抑制的影响」，
        //    规则表 `KARDS基础规则参考.md:159-160` 讲的也是它
        //    （「被压制的单位不能移动或攻击 / 于单位所有者下个回合结束时移除」）。
        //    判据与出处见 `PinnedBlocksOperation`（与 `Attack` **共用同一份**，
        //    例外 `canOperateWhilePinned` 也在那里判，不在这里另写一遍）。
        if (PinnedBlocksOperation(unit))
        {
            reason = "单位被钉住（CanCardDoAnything i=198：钉住的单位不能操作；" +
                     "带 canOperateWhilePinned 的除外）";
            return false;
        }

        if (!unit.CanMoveThisTurn(State))
        {
            reason = $"单位本回合不能移动（召唤失调 / 已移动过 / 已攻击过且非坦克）" +
                     $"（在场={unit.Location} 已移动={unit.HasMovedThisTurn} " +
                     $"已攻击={unit.HasAttackedThisTurn} 进场回合={unit.EnteredPlayOnTurn} " +
                     $"当前回合={State.Turn} 是坦克={unit.IsTank}）";
            return false;
        }

        if (unit.TotalOperationCost > State.Kredits(unit.Owner))
        {
            reason = $"油费不足（油费={unit.TotalOperationCost} kredit={State.Kredits(unit.Owner)}）";
            return false;
        }

        // 前线互斥：对面占着就推不进去；我方占着且满了也推不进去。
        // （`MoveUnitFromSupportToFrontLine` i=281/333 与 i=468/543）
        //
        // 两条拒绝各记一笔，好在对拍里看出"这条规则是不是在误伤真实动作" ——
        // 静默拒绝是最难查的一类 bug。
        if (State.FrontlineOwner != Side.NotAvailable && State.FrontlineOwner != unit.Owner)
        {
            reason = $"前线被对面占着（前线归属={State.FrontlineOwner}，本单位={unit.Owner}）";
            State.UnimplementedCalls["<frontline-blocked-by-opponent>"] =
                State.UnimplementedCalls.GetValueOrDefault("<frontline-blocked-by-opponent>") + 1;
            return false;
        }

        if (State.FrontlineOwner == unit.Owner
            && State.Cards(unit.Owner, CardLocation.BoardFrontline).Count >= State.FrontlineCapacity)
        {
            reason = $"我方前线已满（{State.Cards(unit.Owner, CardLocation.BoardFrontline).Count}" +
                     $"/{State.FrontlineCapacity}）";
            State.UnimplementedCalls["<frontline-blocked-full>"] =
                State.UnimplementedCalls.GetValueOrDefault("<frontline-blocked-full>") + 1;
            return false;
        }

        // 事件签名携带的是这次推进实际支付的费用。先保存再扣费，避免后续
        // 任何费用重算让订阅卡读到错误值。
        int moveCost = unit.TotalOperationCost;
        State.AddKredits(unit.Owner, -moveCost);
        State.AddOperationKreditsSpentThisTurn(moveCost);

        RecordAction("XActionMoveCardToLine", unit.Owner, new Dictionary<string, object?>
        {
            ["cardID"] = unit.CardId,
        });

        FireSubAction("ZActionMoveCardToNewLocation", new[]
        {
            ActionValue2.Int("instigatorID", unit.CardId),
            ActionValue2.Int("locationNumber", targetSlot),
        });

        // CardLocationMoved refreshes physical old/new locations before its
        // movement callbacks and location tail. Retain the existing slot writer
        // and same-location callback policy until their separate audit.
        var oldLocation = unit.Location;
        State.Move(unit, CardLocation.BoardFrontline, targetSlot,
            moveReason: CardMoveReason.AdvancePlayerMove, notifyMoved: false);
        Api.RefreshLocationStatus(new[] { oldLocation, CardLocation.BoardFrontline });
        if (oldLocation != CardLocation.BoardFrontline)
            State.CardMoved?.Invoke(unit, oldLocation, CardLocation.BoardFrontline, false, CardMoveReason.AdvancePlayerMove);
        if (!unit.CanMoveAndAttackInSameTurn) unit.ClearAttackQuotaForMovement();
        unit.HasMovedThisTurn = true;
        Api.ExecuteOnMoveToFrontlineCardEffects(unit, false, moveCost);

        FireOperationKreditsSpent(unit, moveCost);

        return true;
    }

    /// <summary>
    /// 蓝图 <c>MoveUnitFromSupportToFrontLine</c> 的效果位移路径。
    ///
    /// 这是卡牌效果调用的免费移动，不经过普通行动方、召唤失调、压制或油费门；
    /// 蓝图检查卡在场、<c>cantMove</c>、前线归属和前线容量。
    /// 移动后消费额度，再执行反制/自身/T50及零费行动事件。
    /// 此方法返回内部执行结果；Blueprint的qqq输出由派发器独立处理。
    /// </summary>
    public bool MoveUnitFromSupportToFrontLine(CardInstance unit, int instigatorID,
                                               out string reason)
    {
        reason = "";
        if (!unit.Location.IsBoard() || unit.IsHq)
        {
            reason = "目标不是场上单位";
            return false;
        }

        if (Api.HasCustomAbility(unit, "cantMove"))
        {
            reason = "单位带有 cantMove 能力";
            return false;
        }

        Side owner = State.FrontlineOwner;
        if (owner != Side.NotAvailable && unit.Owner.Opposite() == owner)
        {
            reason = $"前线被对面占着（前线归属={owner}，本单位={unit.Owner}）";
            return false;
        }

        int quantity = State.AllCardsInBattleOrder.Count(x => x.Location == CardLocation.BoardFrontline);
        if (owner == unit.Owner && quantity >= State.FrontlineCapacity)
        {
            reason = $"我方前线已满（{quantity}/{State.FrontlineCapacity}）";
            return false;
        }

        var oldLocation = unit.Location;
        // GetNextCardLocationNumber uses physical quantity, then injection
        // compacts the other cards if the mover already occupies this zone.
        var others = State.AllCardsInBattleOrder.Where(x => x != unit && x.Location == CardLocation.BoardFrontline)
            .OrderBy(x => x.LocationNumber).ToArray();
        if (oldLocation == CardLocation.BoardFrontline)
            for (int i = 0; i < others.Length; i++) others[i].LocationNumber = i;
        foreach (var other in others)
            if (other.LocationNumber >= quantity) other.LocationNumber++;
        State.Move(unit, CardLocation.BoardFrontline, quantity, notifyMoved: false, normalizeLocation: false);
        Effects.Blueprint.LiveRuleTables.TryGetEnumName("/Script/kards.ECardLocationEnum", (int)oldLocation, out string oldName);
        Effects.Blueprint.LiveRuleTables.TryGetEnumName("/Script/kards.ECardLocationEnum", (int)CardLocation.BoardFrontline, out string newName);
        Effects.Blueprint.LiveRuleTables.TryGetEnumName("/Game/Structs/CardMoveReason.CardMoveReason",
            (int)CardMoveReason.AdvanceForce, out string reasonName);
        FireSubAction("ZActionMoveCardToNewLocation", new[] {
            ActionValue2.Int("cardID", unit.CardId), ActionValue2.Int("instigatorID", instigatorID),
            ActionValue2.Str("oldLocation", oldName), ActionValue2.Str("newLocation", newName),
            ActionValue2.Int("newLocationNumber", quantity), ActionValue2.Int("nonVisualLocationNumber", quantity),
            ActionValue2.Str("moveReason", reasonName),
            ActionValue2.Int("attackLeft", unit.RemainingAttacks),
            ActionValue2.Int("movementLeft", unit.HasMovedThisTurn ? 0 : 1),
        });
        Api.RefreshLocationStatus(new[] { oldLocation, CardLocation.BoardFrontline });
        State.CardMoved?.Invoke(unit, oldLocation, CardLocation.BoardFrontline, false, CardMoveReason.AdvanceForce);
        // Native CanMoveAndAttackInTheSameTurn: IsArmorUnit (tank or
        // tankdestroyer or its customName1 marker), otherwise the explicit marker.
        if (!unit.CanMoveAndAttackInSameTurn) unit.ClearAttackQuotaForMovement();
        unit.HasMovedThisTurn = true;
        Api.ExecuteOnMoveToFrontlineCardEffects(unit, true, 0);
        FireOperationKreditsSpent(unit, 0);
        return true;
    }

    /// <summary>
    /// 维护前线归属（对应 `BP_CardFunctions::UpdateFrontlineIfNeeded`）：
    /// 前线空 → <see cref="Side.NotAvailable"/>；非空 → **第一张牌的 side**。
    ///
    /// ⚠️ 蓝图用的是 `GetAllCardInBattle()` 的**数组顺序**第一张，
    /// 不是 `locationNumber` 最小的那张，也不先按阵营分组。
    /// 重入中间状态仍必须按物理数组读取，不能依赖前线互斥假设。
    /// </summary>
    private void UpdateFrontlineOwner()
    {
        // FetchCardsByLocation returns the first physical battle-array match,
        // without owner grouping, slot sorting or an HQ exclusion.
        State.FrontlineOwner = State.AllCardsInBattleOrder
            .FirstOrDefault(c => c.Location == CardLocation.BoardFrontline)?.Owner ?? Side.NotAvailable;
    }

    /// <summary>
    /// 重算前线归属，**且只在归属真的变了时**发 `OnFrontlineOwnershipChange`。
    ///
    /// 出处 `out/bp-cardfn.json` → `UpdateFrontlineIfNeeded`（40 条语句）：
    /// <code>
    /// si=5    oldFrontlineOwnerSide = GameStateRef.GetFrontlineOwnerSide()
    /// si=77   FetchCardsByLocation(7, …)                     ; 7 = 前线
    /// si=138  EqualEqual_IntInt(QtyInLocation, 0)
    /// si=172      JumpIfNot 262                              ; 不空 ⇒ 去 262 比"第一张的 side"
    /// si=186          EnumCompareSide(old, 0=NotAvailable)
    /// si=247              （相等 ⇒ 本来就没归属，无事可做）
    /// si=262  EnumCompareSide(old, FirstCard.side)
    /// si=352      相等 ⇒ 直接返回（**归属没变就不发事件**）
    /// si=...  GameStateRef.UpdateFrontlineOwnerSide(新归属)
    /// si=601  FetchAllCardsWithEventTrigger(27)；si=870 每项重读当前归属后派发
    /// </code>
    /// ⇒ 「算 → 变了才发」是蓝图原样，不是本内核的取舍。
    ///
    /// <paramref name="subject"/> 是**引发这次重算的那张卡**（对应蓝图的
    /// `instigator`）：换区钩子里是刚换区的卡，`MoveUnit` 里是被推进去的单位。
    ///
    /// ⚠️ `internal` 而不是 `private`：`CardApi.SpawnOnBattlefield` 也要调它 ——
    /// 蓝图 `SpawnCardToBoard` 是在生成之后**显式**调 `UpdateFrontlineIfNeeded` 的
    /// （那张卡是 `Create` 到前线再 `Move` 到**同一区**的，换区钩子根本不发，
    /// 见 `CardApi.SpawnOnBattlefield` 里的注释与出处）。
    /// </summary>
    internal void RefreshFrontlineOwner(CardInstance? subject)
    {
        // 必须在 `UpdateFrontlineOwner()` **之前**读：这正是蓝图 si=5 取
        // `oldFrontlineOwnerSide` 的时机，也是"变了才发"的比较基准。
        Side oldOwner = State.FrontlineOwner;
        UpdateFrontlineOwner();
        if (oldOwner == State.FrontlineOwner)
        {
            return;
        }

        Api.ExecuteFrontlineOwnershipChange(subject, oldOwner);
    }

    // ==================== 离场触发点 ====================

    /// <summary>
    /// 广播「这张卡要离开棋盘（或离开原主）」。
    ///
    /// 卡面里写「部署后持续生效、离场还原」的效果（`OnLeaveBoardOrOwner`，
    /// 全卡池 67 张）全靠这个触发点撤销自己的 buff —— 光环（①）只是它的一个子类。
    /// **必须在真正移动之前调用**：效果里要读卡的旧位置。
    ///
    /// 这里只发 before；真正移动后调用 FireAfterLeaveTrigger。
    /// 未抑制主体先执行自身事件，再查询并广播给其他订阅者。
    /// </summary>
    /// <param name="goingToLocation">卡要去哪（卡牌蓝图的第 1 个入参就是它）。</param>
    public void FireLeaveTrigger(CardInstance card, CardLocation goingToLocation, int method = 0,
                                 CardLocation? oldLocation = null)
    {
        if (card.IsHq || !(oldLocation ?? card.Location).IsBoard())
        {
            return;   // HQ 不会「离场」
        }

        FireSubActionWithCard("ZActionLeaveBoardOrOwner", card, (int)goingToLocation);
        Api.RemovePincerEffects(card);
        var named = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cardLeaving"] = card, ["goingToLocation"] = (int)goingToLocation,
            ["NewLocation"] = (int)goingToLocation, ["method"] = method,
        };
        if (!card.IsSuppressed)
            Api.FireTrigger("OnLeaveBoardOrOwner", card, card.Owner,
                eventArgs: new object?[] { (int)goingToLocation, method },
                goingToLocation: goingToLocation, namedArgs: named,
                includeUnavailableRecipients: oldLocation.HasValue);

        // i=845/899: self precedes the T46 query; eligibility is fixed by that query.
        var observers = Api.CaptureRegisteredTriggerSnapshot("OnOtherCardLeaveBoardOrOwner")
            .Where(observer => observer.CardId != card.CardId).ToArray();
        foreach (var observer in observers)
            Api.FireTrigger("OnOtherCardLeaveBoardOrOwner", observer, observer.Owner,
                selfProgramName: "OnOtherCardLeaveBoardOrOwner", eventSubject: card,
                eventArgs: new object?[] { card, (int)goingToLocation, method },
                goingToLocation: goingToLocation, namedArgs: named,
                recipientSnapshot: new[] { observer }, includeUnavailableRecipients: true);
    }

    /// <summary>BP_CardFunctions after helper: observers see OldLocation, self sees destination.</summary>
    public void FireAfterLeaveTrigger(CardInstance card, CardLocation goingToLocation, CardLocation oldLocation)
    {
        if (card.IsHq || !oldLocation.IsBoard()) return;
        Api.RemovePincerEffects(card);
        var afterNamed = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["goingToLocation"] = (int)goingToLocation,
            ["OldLocation"] = (int)oldLocation,
            ["cardLeaving"] = card,
        };
        if (!card.IsSuppressed)
            Api.FireTrigger("OnAfterLeaveBoard", card, card.Owner,
                eventArgs: new object?[] { (int)goingToLocation },
                goingToLocation: goingToLocation, namedArgs: afterNamed,
                recipientSnapshot: new[] { card }, includeUnavailableRecipients: true);
        // i=836/881: the T8 query follows the self callback and preserves its array.
        var observers = Api.CaptureRegisteredTriggerSnapshot("OnAfterOtherCardLeaveBoardOrOwner")
            .Where(observer => observer.CardId != card.CardId).ToArray();
        foreach (var observer in observers)
            Api.FireTrigger("OnAfterOtherCardLeaveBoardOrOwner", observer, observer.Owner,
                selfProgramName: "OnAfterOtherCardLeaveBoardOrOwner", eventSubject: card,
                eventArgs: new object?[] { card, (int)oldLocation }, namedArgs: afterNamed,
                recipientSnapshot: new[] { observer }, includeUnavailableRecipients: true);
    }
    /// <summary>给「离场」这类带参数的事件记一条子动作（便于回放比对）。</summary>
    private void FireSubActionWithCard(string name, CardInstance card, int goingToLocation)
        => FireSubAction(name, new[]
        {
            ActionValue2.Int("instigatorID", card.CardId),
            ActionValue2.Int("goingToLocation", goingToLocation),
        });

    /// <summary>
    /// Current ExecuteOnOperationKreditsSpent: i=38 skips only the suppressed
    /// subject's callback; i=594 calls it before jumping back to the T68 query.
    /// The observer loop separately rejects suppressed recipients at i=381.
    /// </summary>
    private void FireOperationKreditsSpent(CardInstance operated, int cost)
        => Api.ExecuteOnOperationKreditsSpent(operated, cost);

    // ==================== 攻击 ====================

    /// <summary>单位攻击目标（单位或 HQ）。</summary>
    /// <summary>攻击（不要原因）。见带 <c>out reason</c> 的重载。</summary>
    public bool Attack(CardInstance attacker, CardInstance defender)
        => Attack(attacker, defender, out _);

    /// <summary>
    /// 攻击。**带拒绝原因**（诊断用）—— 原因由**同一套判据**产出，不是另写一份。
    ///
    /// ⚠️ 为什么要加 `out reason`（2026-10-02）：
    /// 回放审计与 NN 候选日志以前只报"攻击被拒"，**说不出是哪道门**。
    /// 实测对局 508065 在 t19 有 4 起攻击被拒，光看"被拒"分不清是
    /// 射程 / 守护 / 烟幕 / 压制 / 油费 —— 排查成本极高。
    /// 这里把每条 `return false` 都补上原因，**判据只有这一份**（避免两份漂移）。
    /// </summary>
    public bool Attack(CardInstance attacker, CardInstance defender, out string reason)
    {
        BeginEffectResolution();
        try
        {
            bool completed = AttackActionCore(attacker, defender, out reason);
            if (completed)
            {
                ExecuteAutoPlayCards();
                ApplyPendingGotchaPlayReceipts();
            }
            return completed;
        }
        finally { EndEffectResolution(); }
    }

    private bool AttackActionCore(CardInstance attacker, CardInstance defender, out string reason)
    {
        reason = "";

        if (attacker.Owner != State.ActiveSide || !attacker.Location.IsBoard() || attacker.IsHq)
        {
            reason = $"攻击者不在场上或不是行动方（位置={attacker.Location} 归属={attacker.Owner} 行动方={State.ActiveSide}）";
            return false;
        }

        // ★ 死的不能攻击（与 `MoveUnit` 同理，2026-10-02 补）。
        //   死单位 `Location` 仍是场上区域，`Location.IsBoard()` 拦不住。
        if (!attacker.AliveOnBoard)
        {
            reason = "攻击者已死（在场上但防御 ≤ 0）";
            return false;
        }

        // ★★ 2026-10-02（第二轮）：这里原先有一道「被抑制不能攻击」的门 —— **已删**。
        //
        // ## 为什么删（上一轮加错了）
        //
        // 中文客户端把两个不同的关键字分别译作：
        //   · `Pin`     → **压制**（不能移动或攻击；于所有者下回合结束时移除）
        //   · `Suppress`→ **抑制**（失去所有关键词与增益）
        // 上一轮把"不能行动"挂到了 `Suppress` 上，理由是权威规则表 159-160 ——
        // 但那两行讲的是**压制**（原文：「**压制**：被压制的单位不能移动或攻击」）。
        // 那是一次**关键字误读**，不是"反编译错、规则表对"。
        //
        // ## 蓝图证据（这次是**正面证据**，不是"没搜到"）
        //
        // · 攻击合法性：`cardsCheckFunctions::CanAttack`（210 条语句）里
        //   `isSuppressed` 出现 **0 次**（该文件全文只有两处 `isSuppressed`：
        //   `:1150` 在 `CanSelectAsTarget`（`isSuppressed || canIt`，被抑制的卡
        //   **反而更好指定**）、`:2487` 在 `getActiveEffects`（特效图标））。
        // · 行动资格：`BP_Logic::CanCardDoAnything` 全文 `isSuppressed` **0 次**
        //   （该文件里 `Suppress` 这个子串一次都不出现）。
        // · 卡牌自己的过滤器：全库 60 处 `isSuppressed` 里，**没有一处**是"拒绝行动"。
        //   它们全部落在两类地方：**事件广播的分流**（`FireTrigger` si=325/710、
        //   `ExecuteOnCardLocationMoved` `:15475`、`ExecuteOnSurvivedCombatEvents`
        //   `:17020` …）和**数值改动的分流**（`ChangeDefense` `:7854`、
        //   `ChangeAttack`（同族）、`ExecuteBeforeReceiveDamage` `:13886` …）。
        //   即：被抑制的卡**照样能行动**，只是它自己不广播、不吃修正。
        // · 玩家（雪雾）的权威定义里也**没有**"不能行动"这一条：
        //   「抑制：使被抑制的单位失去所有特效和关键字 …… 压制不会受到抑制的影响」。
        //
        // ## 调用方过滤？—— 查过，不存在
        //
        // 上一轮那句"也许客户端在生成可行动单位列表时滤掉了"经不起查：
        // 客户端生成可行动候选用的就是 `CanCardDoAnything` / `CanAttack`
        // （`_index.g.cs:1843` 注册的就是它们），两者都不看 `isSuppressed`。
        //
        // `Pinned` 才是真的禁止攻击（下面那道 `PinnedBlocksOperation`，保留）。
        if (!attacker.CanOperateThisTurn(State))
        {
            reason = $"攻击者本回合不能行动（召唤失调 / 攻击额度用尽 / 已移动过且非坦克）" +
                     $"（在场={attacker.Location} 已攻击={attacker.HasAttackedThisTurn} " +
                     $"攻击次数={attacker.AttacksThisTurn}/{attacker.MaxAttacksThisTurn}" +
                     $" 已移动={attacker.HasMovedThisTurn} 是坦克={attacker.IsTank}）";
            return false;
        }

        if (!attacker.IsHq
            && attacker.Definition.Type is "infantry" or "tank" or "artillery"
            && State.HasGameplayRestriction(attacker.Owner,
                GameplayRestrictionType.CannotAttackWithGroundUnits))
        {
            reason = "当前回合禁止地面单位攻击";
            return false;
        }

        // 防御者合法性：**必须还在场上**。
        //
        // 为什么单独加这一条：`Attack` 原先对防御者**一个字都不校验**，
        // 完全依赖调用方（GreedyBot 用 `LegalTargets`）给对目标。
        // 拿一个已经进弃牌堆的卡来打，会走完整套结算：扣行动费、加攻防 buff、
        // 触发 OnAfterAttack —— 等于凭空多打一次。
        // 调用方一旦持有过期引用（决策与结算之间夹了别的效果），就会静默发生。
        // 宁可在这里拒掉并记一笔，也不要"看着能跑"。
        if (!defender.IsAlive || !defender.Location.IsBoard())
        {
            reason = $"目标不在场上（位置={defender.Location} 活着={defender.IsAlive}）";
            State.UnimplementedCalls["<attack-on-non-board-target>"] =
                State.UnimplementedCalls.GetValueOrDefault("<attack-on-non-board-target>") + 1;
            return false;
        }

        if (attacker.TotalOperationCost > State.Kredits(attacker.Owner))
        {
            reason = $"油费不足（油费={attacker.TotalOperationCost} kredit={State.Kredits(attacker.Owner)}）";
            return false;
        }

        // ★★ **被「压制」（`Pin`）的单位不能攻击** —— 这道门保留。
        //
        // ⚠️ 2026-10-02 之前的注释里写着"这里原先不再因压制禁止攻击"，
        //    那是把 `Pin`（压制）与 `Suppress`（抑制）混作一谈时的产物：
        //    「不能行动」确实是真的，只是它属于**压制**而不是抑制
        //    （抑制那道门在本函数上方，第二轮已删，理由见那里）。
        //
        // `PinnedBlocksOperation` 是移动侧与攻击侧**共用**的判据，不在这里另写一份：
        //    · 蓝图这条门是**带例外**的（`cardsCheckFunctions.g.cs:368`
        //      `HasCustomAbility(attackerCard,"canOperateWhilePinned")`），
        //      就地写 `Keywords.Contains(Pinned)` 会把例外漏掉。
        if (PinnedBlocksOperation(attacker))
        {
            reason = "攻击者被钉住";
            return false;
        }

        // 卡面攻击限制（`cantAttack:*` / `cantBeAttackedBy:*`）——来自
        // `cardsCheckFunctions::CanAttack` 的 HasCantAttackType / HasCantBeAttackedBy
        // 判据。效果卡通过 CustomAbilityAdd/CustomName1Add 写入这些状态；
        // 该门必须在扣行动费、记攻击次数之前执行。
        if (!CanAttackByCardRestrictions(attacker, defender, out reason))
        {
            return false;
        }

        // 掩护（`isBeingGuarded`）—— 出处 `cardsCheckFunctions::CanAttack`：
        // <code>
        // si=2492 IsBomber(attackerCard)   si=2533 IsArtillery(attackerCard)
        // si=2574 or = IsBomber || IsArtillery
        // si=2612 JumpIfNot(or) -> si=2627      ; 为假（不是轰炸/火炮）⇒ 落下去查掩护
        // si=2626     PopExecutionFlow           ; 是轰炸/火炮 ⇒ 跳过整个掩护判定
        // si=2627 PopExecutionFlowIfNot(defenderCard.isBeingGuarded)   ; 没被掩护 ⇒ 跳过
        // si=2659 IsLocation(defenderCard)
        // si=2700 JumpIfNot(IsLocation) -> si=2788
        // si=2714     canAttack=False; failReason="hq_is_being_garded"; return
        // si=2788     canAttack=False; failReason="is_being_guarded";   return
        // </code>
        if (!IsBomber(attacker) && !Api.IsArtillery(attacker) && IsBeingGuarded(defender))
        {
            reason = "目标受守护保护（攻击者既不是轰炸机也不是炮兵）";
            State.UnimplementedCalls["<attack-on-guarded-target>"] =
                State.UnimplementedCalls.GetValueOrDefault("<attack-on-guarded-target>") + 1;
            return false;
        }

        // 跨前线射程判据 —— 逐字来自蓝图，见 CanReachAcrossFrontline 的出处注释。
        // 这是本次改动**唯一**新增的判据，位置放在其它合法性检查之后、
        // 任何状态变更（扣 kredit / 记 hasAttackedThisTurn / 发触发）之前。
        if (!CanReachAcrossFrontline(attacker, defender))
        {
            reason = $"够不着（跨战线射程不足：攻方={attacker.Location} " +
                     $"目标={defender.Location} 射程={attacker.Range}；" +
                     "任一方在前线则不需要射程，双方都在支援线才要射程 ≥ 2）";
            OutOfRangeAttacksRejected++;
            return false;
        }

        // 烟幕（`CanAttack` si=3596-3793，出处见 `LegalTargets` 里那段注释）。
        // 蓝图里这一段的落点比掩护更靠后（si=3596 vs si=2627），所以放在射程之后、
        // 与蓝图同序 —— 拒绝本身与顺序无关，但保持同序便于以后逐条对账。
        if (defender.HasSmokescreen)
        {
            reason = "目标有烟幕（无法被攻击）";
            State.UnimplementedCalls[defender.IsHq
                ? "<attack-on-location-with-smokescreen>"
                : "<attack-on-smokescreen-target>"] =
                State.UnimplementedCalls.GetValueOrDefault(defender.IsHq
                    ? "<attack-on-location-with-smokescreen>"
                    : "<attack-on-smokescreen-target>") + 1;
            return false;
        }

        // ★★ 目标合法性门（规则库 `CanSelectAsTarget`）—— 攻击路径原先**完全没接**这道门。
        //
        // ## 蓝图依据
        //
        // `cardsCheckFunctions.CanAttack` 里就有这一句
        //（`ref/kards-sim/KardsSim/Generated/_deps/cardsCheckFunctions.g.cs:902`）：
        // <code>
        // CanSelectAsTarget(self, defenderCard, attackerCard, False, __WorldContext,
        //                   out can, out Reason, …)
        // if (!can) { canAttack = False; failReason = Reason; }      // :904/:930
        // </code>
        // 实参顺序（**第 0 个是接收者**，不在 4 个形参里，见 `KismetVm` 的约定）：
        // `Targeted = defenderCard`、`Targeting = attackerCard`、`byPlayFromHand = False`。
        // 内核 `CardApi.CanSelectAsTarget` 的形参顺序是 `(targeting, targeted, …)`
        //（与蓝图**相反**，内部自洽；判据见那个方法的注释与 `CanTarget` 的调用点），
        // 所以这里写 `(attacker, defender, false)` —— 攻方是 `targeting`，防御方是 `targeted`。
        //
        // ## ⚠️⚠️ 位置：必须在下面 `State.AddKredits` **之前**，不能挪
        //
        // 这道门**自己会算一次行动费**（`CanSelectAsTarget` 的 ⑤）：
        // <code>
        // cost = byPlayFromHand ? KreditCost : OperationCost
        // remaining = State.Kredits(targeting.Owner) - cost      // ← 它自己减
        // if (remaining < 0) ⇒ 拒（not_enough_kredits_to_target）
        // </code>
        // 放在 `AddKredits(-OperationCost)` **之后**，油费会被减两次 ⇒ 费用紧时
        // **误拒合法攻击**（而 4 张受影响卡都不在回放语料里，这种误拒只会表现成
        // "别的地方莫名失败"，极难从数字上看出来）。
        // 放在之前则与上面 `:1571` 的「油费够不够」预检**同源**：那一条保证
        // `OperationCost ≤ Kredits`，于是门里的 ⑤ 必然通过，两者不会互相打架。
        // 拒绝原因逐字用门给的 `Reason`（蓝图自己也是把 `Reason` 直接当 `failReason`）。
        // ⚠️ 判据与 `LegalTargets` **共用 `AttackTargetGate`**，不在这里另写一份 ——
        //    枚举与结算漂移会让 AI 反复尝试一个永远失败的动作。
        var targetGate = AttackTargetGate(attacker, defender);
        if (!targetGate.Can)
        {
            reason = $"目标合法性门拒绝：{targetGate.Describe()}";
            return false;
        }

        // AttackCard captures cost before reveal/T30/T31 using the ORIGINAL target.
        // Positive tax: i=492 falls through to i=506/547/615. Otherwise jump to
        // i=4706/4747; PopExecutionFlow i=4774 returns to i=643, not a late reread.
        int attackCost = attacker.TotalOperationCost + Math.Max(0, defender.KreditsTaxAsEnemyTarget);
        int attackerCardIdAtStart = attacker.CardId;
        RecordAction("XActionAttackCard", attacker.Owner, new Dictionary<string, object?>
        {
            ["attackerCardID"] = attacker.CardId,
            ["defenderCardID"] = defender.CardId,
        });
        if (CardApi.IsUnrevealedCovertCard(attacker)) Api.RevealCard(attacker, attacker.CardId);
        if (CardApi.IsUnrevealedCovertCard(defender)) Api.RevealCard(defender, attacker.CardId);
        if (!attacker.Location.IsBoard() || !defender.Location.IsBoard()) return false;

        // T30 i=1058..1650: the first changed target ends the loop.
        // Save query-time membership, including the attacker. Invoke saved objects
        // without rechecking registration, suppression or location after callbacks.
        foreach (var observer in Api.CaptureRegisteredTriggerSnapshot("OnOtherCardAttackSwitchTarget"))
        {
            var hit = Api.BroadcastLocalWithOutParams("OnOtherCardAttackSwitchTarget", attacker,
                new[] { "newDefender" }, new Dictionary<string, object?>
                {
                    ["cardAttacking"] = attacker, ["oldDefender"] = defender,
                }, only: new[] { observer }, includeSubject: true, recipientsAlreadyFiltered: true).FirstOrDefault();
            if (hit.Outs?.GetValueOrDefault("newDefender") is CardInstance replacement
                && !ReferenceEquals(replacement, defender))
            {
                defender = replacement;
                break;
            }
        }

        // T31 resets the shared flag before querying. Every saved recipient runs;
        // a true output writes the flag immediately, while later callbacks may
        // change it (including through a reentrant attack). Read it only at the end.
        State.StopAttack = false;
        bool attackedAndStopped = false;
        var attackObservers = Api.CaptureRegisteredTriggerSnapshot("OnOtherCardAttacks");
        foreach (var observer in attackObservers)
        {
            if (observer.CardId == attackerCardIdAtStart) continue;
            var hit = Api.BroadcastLocalWithOutParams("OnOtherCardAttacks", attacker,
                new[] { "stopAttack", "AttackedAndStopped" }, new Dictionary<string, object?>
                {
                    ["cardAttacking"] = attacker, ["defenderCard"] = defender,
                }, only: new[] { observer }, includeSubject: true, recipientsAlreadyFiltered: true).FirstOrDefault();
            if (hit.Outs?.GetValueOrDefault("stopAttack") is true) State.StopAttack = true;
            if (hit.Outs?.GetValueOrDefault("AttackedAndStopped") is true) attackedAndStopped = true;
        }
        if (State.StopAttack) return true;

        State.AddKredits(attacker.Owner, -attackCost);
        State.AddOperationKreditsSpentThisTurn(attackCost);
        if (attacker.Location.IsBoard())
        {
            Api.ExecuteBeforeAttackEvents(attacker, defender);
            if (State.ById(attackerCardIdAtStart) is { } current)
                Api.RemoveSmokescreen(current, attackerCardIdAtStart, true, false);
        }
        else
        {
            FireOperationKreditsSpent(attacker, attackCost);
            return true;
        }

        if (attackedAndStopped)
        {
            SetAttackerHasAttacked(attacker);
            Api.FireTrigger("OnAttackStopped", attacker, attacker.Owner);
            FireOperationKreditsSpent(attacker, attackCost);
            CheckDeaths();
            return true;
        }

        bool shockAttack = !defender.IsHq && attacker.Keywords.Contains(Keyword.Shock);
        Say($"⚔ {attacker} → {defender} 攻击，行动费 {attackCost}");
        var damage = Api.DealCombatDamagePair(attacker, defender, (toDefender, toAttacker) =>
            FireSubAction("ZActionAttackCard", new[]
            {
                ActionValue2.Int("attackerCardID", attacker.CardId),
                ActionValue2.Int("defenderCardID", defender.CardId),
                ActionValue2.Int("damageDefender", toDefender),
                ActionValue2.Int("damageAttacker", toAttacker),
                ActionValue2.Int("attackerAttackLeft", attacker.Attack),
                ActionValue2.Int("defenderDefense", defender.Defense),
            }));

        // Own after-attack event precedes the T4 query, including lethal HQ attacks.
        Api.ExecuteAfterAttackEvents(attacker, defender, damage.ToDefender, shockAttack, attackCost);
        FireOperationKreditsSpent(attacker, attackCost);
        CheckDeaths();
        return true;
    }

    // ExecuteAttackCard i=279 / ExecuteStoppedAttack i=0 consume the attack only
    // after calculation/before receive hooks. CanMoveThisTurn also consults this flag.
    internal void SetAttackerHasAttacked(CardInstance attacker)
    {
        // SetAttackerHasAttacked i=379 decrements movementLeft first when
        // the native armor/custom predicate is false (including stopped attacks).
        if (!attacker.CanMoveAndAttackInSameTurn) attacker.HasMovedThisTurn = true;
        attacker.HasEverAttacked = true; // SetAttackerHasAttacked i=168.
        attacker.HasAttackedThisTurn = true;
        attacker.AttacksThisTurn++;
    }

    /// <summary>列出当前可以攻击的目标。</summary>
    public IEnumerable<CardInstance> LegalTargets(CardInstance attacker)
    {
        var enemy = attacker.Owner.Opposite();
        var enemyUnits = State.Board(enemy).Where(u => u.IsAlive).ToList();

        // `CanAttack` 不把敌方前线当成嘲讽或硬性阻挡：它只按位置和攻击者
        // `range` 拒绝距离不足的目标（CanReachAcrossFrontline）。因此候选集
        // 必须先包含敌方全体单位与 HQ，再由统一的射程/掩护/目标门过滤。
        // 旧实现有敌方前线时只列前线，导致 range>=2 的炮兵/飞机无法攻击后方，
        // 也把“候选枚举”和蓝图的 CanAttack 语义分开了。
        List<CardInstance> targets = new(enemyUnits) { State.Hq(enemy) };

        // ---- 掩护（Guard）----
        //
        // ⚠️ 旧实现把 Guard 做成了**嘲讽**（"有 Guard 就必须先打它、HQ 直接不可选"），
        //    而蓝图 `BP_CardFunctions::UpdateGuarded`（62 条语句，`out/bp-cardfn.json`）的语义是
        //    「**邻卡有 Guard ⇒ 这张卡被掩护**」：
        // <code>
        // si=5/36/67/174   location ∈ {5,6,7} 之外直接返回（只对棋盘上的卡算）
        // si=188           FetchCardsByLocation(location) —— 遍历**同一条线**上的卡
        // si=523/634       getHasGuard(_currentCard) && !IsUnrevealedCovertCard(_currentCard)
        // si=672           JumpIfNot(那个条件) -> si=836
        // si=686/718       有 Guard 的卡：isBeingGuarded = False     ← ★ 掩护卡自己不免疫
        // si=836/847       _removeGuarded = True; GetAdjacentCards(_currentCard, true, out 邻卡)
        // si=1276/1336     邻卡 hasGuard && !IsUnrevealedCovertCard ⇒ 成立
        // si=1346/1405     _currentCard.isBeingGuarded = True        ← ★ 邻卡有 Guard ⇒ 被掩护
        // si=1517/1587     _removeGuarded && isBeingGuarded ⇒ 置回 False（清掉过期标记）
        // </code>
        //    再配合 `CanAttack` si=2627 的拒绝（上面 Attack 里的注释），三条结论：
        //      · 孤立单位**可以**被打（没有 Guard 邻卡）
        //      · 掩护卡**自己可以**被打（它的 isBeingGuarded 恒 False）
        //      · HQ 只在**被邻卡掩护**时不可打（HQ 也在 5/6 这条线上，占第 0 格）
        //    轰炸机/炮兵跳过整条判定（si=2492-2626）。旧实现这三条全反 —— 128 张天生
        //    Guard + `GiveGuard` 的子集，目标集两个方向都错。
        if (!IsBomber(attacker) && !Api.IsArtillery(attacker))
        {
            targets = targets.Where(t => !IsBeingGuarded(t)).ToList();
        }

        // ---- 烟幕（Smokescreen）：带烟幕的单位**不能被攻击** ----
        //
        // 出处 `out/bp-cardscheck.json` → `cardsCheckFunctions::CanAttack`：
        // <code>
        // si=3596  defenderCard.getHasSmokescreen(out doesIt)
        // si=3637  PopExecutionFlowIfNot(doesIt)                ; 没有烟幕 ⇒ 跳过
        // si=3647  defenderCard.IsLocation(out isIt)
        // si=3688  JumpIfNot(isIt) -> si=3782
        // si=3702/3713  canAttack=False; failReason="location_has_smokescreen"
        // si=3782/3793  canAttack=False; failReason="defender_has_smokescreen"
        // </code>
        // 卡面互证：`card_unit_85_pioneer_company` / `card_unit_coastwatchers` /
        // `card_unit_royal_ulster_rifles` 等 54 张的「Smokescreen」文本。
        targets = targets.Where(t => !t.HasSmokescreen).ToList();

        // 末尾这条 `CanReachAcrossFrontline` 是射程判据（见 CanReachAcrossFrontline 的出处注释）。
        // ⚠️ 它现在对**所有**分支一致生效（包括原来那条 Guard 分支）——
        //    旧实现里 Guard 分支也套了它，但那是因为分支结构不同；
        //    统一过滤后语义不变：够不着的目标就是不能打（蓝图 si=90-99）。
        //
        // ★★ 再叠上**目标合法性门**（规则库 `CanSelectAsTarget`，`byPlayFromHand: false`）。
        // 为什么原先没有这一道：`LegalTargets` 只做了「射程 + 烟幕 + 掩护」三条**手写**判据，
        // 而蓝图 `CanAttack` 在末尾（`_deps/cardsCheckFunctions.g.cs:902`）是**直接调规则库**的
        // —— 于是「被敌方指定的额外税」`KreditsTax_AsEnemyTarget`、
        // 「触发点 2 的否决位」`CanOtherCardBeTargetted`（唯一实现者
        // `card_unit_no_3_commando`：「Units with 4 or more attack cannot attack.」）
        // 这两条在攻击路径上**从来没有生效过**。
        //
        // ⚠️ 与 `Attack` 里那道门**必须同源**：这里过滤掉的目标，`Attack` 也必须拒；
        //    反过来，`Attack` 拒的，这里也不能列出来 —— 否则「候选里有、结算说非法」
        //    会表现成 AI 反复尝试一个永远失败的动作。两处都走 `AttackTargetGate`。
        return targets.Where(t => CanReachAcrossFrontline(attacker, t)
                               && CanAttackByCardRestrictions(attacker, t, out _)
                               && AttackTargetGate(attacker, t).Can);
    }

    /// <summary>
    /// `CanAttack` 的卡面攻击限制门。
    ///
    /// `cantAttack` 是攻击者自己的无条件限制；`cantAttack:&lt;type&gt;` 是
    /// 按目标类型限制（例如 `cantAttack:location` = 不能攻击 HQ），而
    /// `cantBeAttackedBy:&lt;type&gt;` 是目标卡自己的防护标记。原生谓词同时
    /// 查询名称与能力来源；Blueprint先检查具体类型（如location、infantry），
    /// 再独立检查air/ground类别。
    /// </summary>
    private bool CanAttackByCardRestrictions(CardInstance attacker, CardInstance defender,
                                             out string reason)
    {
        reason = "";
        if (Api.HasCantAttack(attacker))
        {
            reason = "unit_cant_attack";
            return false;
        }

        // CanAttack checks the concrete enum label first. Only this check has
        // an ignoreCantAttack_<type> exception; air/ground are separate gates.
        string defenderType = defender.Definition.Type ?? "NotAvailable";
        if (Api.HasCantAttackType(attacker, defenderType)
                && !Api.HasCustomAbility(attacker, "ignoreCantAttack_" + defenderType)
            || Api.IsAirUnit(defender) && Api.HasCantAttackType(attacker, "air")
            || IsGroundAttackRestrictionUnit(defender) && Api.HasCantAttackType(attacker, "ground"))
        {
            reason = "cant_be_attacked_by_unit";
            return false;
        }

        string attackerType = attacker.Definition.Type ?? "NotAvailable";
        if (Api.HasCantBeAttackedBy(defender, attackerType)
            || Api.IsAirUnit(attacker) && Api.HasCantBeAttackedBy(defender, "air")
            || IsGroundAttackRestrictionUnit(attacker) && Api.HasCantBeAttackedBy(defender, "ground"))
        {
            reason = "unit_cant_be_attack_by_unit";
            return false;
        }
        return true;
    }

    private static bool IsGroundAttackRestrictionUnit(CardInstance card)
        => card.Definition.Type is "infantry" or "tank" or "artillery";

    /// <summary>
    /// 攻击路径的**目标合法性门**（规则库 `cardsCheckFunctions.CanSelectAsTarget`，
    /// `byPlayFromHand: false`）—— `LegalTargets`（候选枚举）与 `Attack`（结算）**共用这一份**。
    ///
    /// 为什么要抽成一个方法：这两处以前各写各的手写判据（射程 / 烟幕 / 掩护），
    /// 而蓝图只有**一处**（`CanAttack` 末尾调规则库）。抽出来之后「枚举」与「结算」
    /// 不可能再漂移 —— 这是本文件里唯一一处「同一条判据被两条路径共用」的写法。
    ///
    /// 实参顺序：内核 `CanSelectAsTarget` 的形参是 `(targeting, targeted, byPlayFromHand)`
    /// （与蓝图签名的 `(Targeted, Targeting, …)` **相反**，见那个方法的注释），
    /// 所以攻方传第 1 个、防御方传第 2 个。
    /// </summary>
    private CardApi.TargetCheck AttackTargetGate(CardInstance attacker, CardInstance defender)
        => Api.CanSelectAsTarget(attacker, defender, byPlayFromHand: false);

    /// <summary>轰炸机（`IsBomber`，`CanAttack` si=2492 用它跳过掩护判定）。</summary>
    public static bool IsBomber(CardInstance card) => card.Definition.Type == "bomber";

    /// <summary>
    /// ★★ 从手牌打出这张牌时，**合法目标的完整集合** —— 客户端的两道门都过一遍
    /// （<see cref="Effects.CardApi.CanTarget"/>）。
    ///
    /// ## 为什么候选要**含友方**
    ///
    /// 旧实现（`NnPolicy.TargetCandidates` / `GreedyBot.ChooseTarget` /
    /// `BotTurnService.ChooseGreedy`）一律只枚举「**敌方**场上单位 + 敌方 HQ」。
    /// 但卡面写着「Give a friendly unit +1+1」（`card_event_air_corps_ferrying`）、
    /// 「Retreat a friendly unit in the frontline」（`card_event_tactical_withdrawal`）
    /// 的牌，**合法目标只有友方** —— 只枚举敌方 ⇒ 这些牌在 bot 手里
    /// **一个合法目标都没有**，而旧代码又不会因此跳过（它拿"攻击力最高的敌方单位"
    /// 硬顶）⇒ 客户端收到一个非法目标、静默不执行 ⇒ 记牌器 +1、场上无变化。
    /// 这正是玩家报告的「虚空牌」第三个来源。
    ///
    /// 所以候选是**双方棋盘 + 双方 HQ**（超集），再交给客户端的门去筛。
    /// 顺序固定（敌方在前，保持与旧行为一致的确定性），不依赖任何哈希遍历序。
    /// </summary>
    public List<CardInstance> LegalPlayTargets(CardInstance card)
    {
        var list = new List<CardInstance>();

        foreach (var side in new[] { card.Owner.Opposite(), card.Owner })
        {
            foreach (var c in State.Board(side))
            {
                if (c.IsAlive && Api.CanTarget(card, c).Can)
                {
                    list.Add(c);
                }
            }

            // HQ 不在 `Board()` 里（那是"半场/前线的单位"），要单独加。
            // 蓝图 `IsLocatedOnBoard` 是含 HQ 的，见 `CardApi.CanSelectAsTarget` 的 ②。
            var hq = State.Hq(side);
            if (hq.IsAlive && Api.CanTarget(card, hq).Can)
            {
                list.Add(hq);
            }
        }

        return list;
    }

    /// <summary>
    /// Legacy dynamic approximation used by attack target filtering.
    /// Native UpdateGuarded writes CardInstance.IsBeingGuarded explicitly and
    /// distinguishes effective own Guard, raw neighbor Guard and hidden Covert.
    /// This helper still reads raw Guard and owner adjacency. Migrate consumers
    /// only after RevealCard/MakeVeteran/RefreshLocationStatus cache timing is audited.
    /// </summary>
    public bool IsBeingGuarded(CardInstance card)
    {
        if (!card.Location.IsBoard())
        {
            return false;   // si=174：location ∉ {5,6,7} 直接返回
        }

        if (card.Keywords.Contains(Keyword.Guard))
        {
            return false;   // si=686/718：掩护卡自己不被掩护
        }

        // 同一条线上的邻卡（locationNumber ± 1）—— GetAdjacentCards 的语义。
        // 只看**同阵营**的卡：掩护是给自己人挡的。
        foreach (var other in State.Cards(card.Owner, card.Location))
        {
            if (ReferenceEquals(other, card))
            {
                continue;
            }

            if (Math.Abs(other.LocationNumber - card.LocationNumber) == 1
                && other.Keywords.Contains(Keyword.Guard))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// **跨前线射程判据**：攻击者能不能够到目标。
    ///
    /// <para>
    /// 逐字来自 <c>cardsCheckFunctions::CanAttack</c>
    /// （资产 <c>klink bot/live/Library/kards/Content/Library/cardsCheckFunctions.uasset</c>）。
    /// 反编译产物：<c>out/xr-cardscheck.bpasm</c>（bpasm）/ <c>out/bp-cardscheck.json</c>（UAssetCLI）。
    /// </para>
    ///
    /// <para>蓝图语句（UAssetCLI 语句号；bpasm 行号 467-544）：</para>
    /// <code>
    /// i=90  NotEqual_ByteByte(defenderCard.location, 7)          ; 7 = Board_Frontline
    /// i=91  NotEqual_ByteByte(attackerCard.location, 7)
    /// i=92  BooleanAND(i=91, i=90)
    /// i=93  Less_IntInt(attackerCard.range, 2)
    /// i=94  BooleanAND(i=92, i=93)
    /// i=95  PopExecutionFlowIfNot(i=94)      ; 条件为假 ⇒ 跳过下面三条
    /// i=96  canAttack = false
    /// i=97  failReason = "not_enough_range"
    /// i=99  Jump 5098                        ; return
    /// </code>
    ///
    /// <para>
    /// <c>PopExecutionFlowIfNot(cond)</c> 的语义是「<c>cond</c> 为**假**时跳走、
    /// 为真时落下去执行块体」—— 这一点不是猜的：同一个函数里的
    /// <c>deployment_sickness</c> 块（i=81-89）用的是同一条指令，而已有自测
    /// <c>DeploymentSickness</c> 证明那条块的判据方向是对的
    /// （<c>enterPlayOnTurn == currentTurn &amp;&amp; !getHasBlitz()</c> ⇒ 不能攻击）。
    /// </para>
    ///
    /// <para>
    /// 所以规则是：**当攻击者和目标都不在前线时，攻击者需要 <c>range ≥ 2</c>**。
    /// 等价地 <c>range ≥ 距离</c>：支援线→前线距离 1、支援线→支援线距离 2。
    /// 任一方在前线则距离 ≤ 1，任何单位都够得着。
    /// </para>
    ///
    /// <para>
    /// 「只有炮兵/战斗机/轰炸机能跨前线」是这条射程规则的**数据后果**，
    /// 不是另有一条按类型的判据 —— <c>CanAttack</c> 里**没有** <c>IsFighter</c>，
    /// <c>IsArtillery</c>/<c>IsBomber</c> 只出现在 <c>isBeingGuarded</c> 那条分支上
    /// （i=100-104），与射程无关。卡池实测（<c>klink bot/docs/cards.live.json</c>）：
    /// infantry 567 张 range=1（1 张 range=2）、tank 154 张 range=1、
    /// artillery 60 张 range=2、fighter 144 张 range=2、bomber 97 张 range=2（1 张 range=4）。
    /// </para>
    /// </summary>
    public static bool CanReachAcrossFrontline(CardInstance attacker, CardInstance defender)
    {
        // 任一方在前线 ⇒ 距离 ≤ 1 ⇒ 任何射程都够得着（蓝图里就是「条件为假」）
        if (attacker.Location == CardLocation.BoardFrontline
            || defender.Location == CardLocation.BoardFrontline)
        {
            return true;
        }

        // 双方都不在前线（支援线 ↔ 支援线 / HQ）⇒ 要跨过前线，需要 range ≥ 2
        return attacker.Range >= 2;
    }

    // ==================== 伤害与死亡 ====================

    /// <summary>造成伤害（由 <see cref="CardApi"/> 统一入口以保证事件顺序）。</summary>
    /// <param name="isCombatDamage">
    /// 是不是**战斗伤害**（攻击结算）。这是重甲减伤的唯一判据 —— 见下面那段注释。
    /// 只有 <see cref="MatchEngine.Attack"/> 传 true（主伤害 + 反击两次都传）。
    /// </param>
    /// <param name="ignoreHeavyArmor">
    /// 蓝图 `CalculateDamageDealt` 的同名入参（`SelectInt(0, 重甲, ignoreHeavyArmor)`）。
    /// 全库 4 个调用点**没有一个传 true**，保留它是为了不把蓝图的形状压扁。
    /// </param>
    internal int ApplyDamage(CardInstance target, int amount, CardInstance? source,
                             bool isCombatDamage = false, bool ignoreHeavyArmor = false,
                             bool damageAlreadyCalculated = false)
    {
        if (amount <= 0 || (!damageAlreadyCalculated && !target.IsAlive))
        {
            return 0;
        }

        if (!damageAlreadyCalculated)
        {
            amount = CalculateAppliedDamage(target, amount, source, isCombatDamage, ignoreHeavyArmor);
        }

        if (amount <= 0)
        {
            return 0;
        }

        target.Defense -= amount;
        if (target.IsHq)
        {
            State.UpdateHQDamagedAmountThisTurn(target.Owner, amount);
            Say($"HQ {target.Owner.ToWire()} 受到 {amount} 伤害，剩余 {target.Defense}");
            if (target.Defense <= 0 && !damageAlreadyCalculated)
            {
                State.Finish(target.Owner.Opposite(), "Victory_DestroyHQ");
            }
        }

        return amount;
    }

    internal int CalculateAppliedDamage(CardInstance target, int amount, CardInstance? source,
                                        bool isCombatDamage = false, bool ignoreHeavyArmor = false)
    {
        if (amount <= 0 || !target.IsAlive || target.Keywords.Contains(Keyword.Immune))
        {
            return 0;
        }

        // Combat-only mitigation belongs to CalculateDamageDealt, not to the
        // raw defense subtraction in ApplyDamageToCard.
        int reduction = (isCombatDamage && !ignoreHeavyArmor) ? target.HeavyArmor : 0;
        if (reduction > 0)
        {
            amount = Math.Max(amount - reduction, 0);
            if (amount == 0)
            {
                return 0;
            }
        }

        return amount;
    }

    /// <summary>直接对 HQ 造成伤害（疲劳等无来源伤害）。</summary>
    public void DamageHq(Side side, int amount) => ApplyDamage(State.Hq(side), amount, null);

    /// <summary>清理防御 ≤ 0 的单位。</summary>
    public void CheckDeaths()
    {
        if (State.IsFinished)
        {
            return;
        }

        // 热路径：用无序枚举收集再销毁，避免每次 AllCards 的排序 + 分配
        List<CardInstance>? dying = null;
        foreach (var card in State.CardsUnordered())
        {
            // ⚠️ 必须限定在**场上**（IsBoard），这是 2026-09-26 对拍抓出来的一个严重 bug：
            //    指令牌（Type=order）的 defense 天然是 0，而这里原来遍历的是**所有卡**，
            //    于是**第一次** CheckDeaths（= 本局第一张牌被打出时）就把双方牌库 + 手牌里
            //    全部指令牌一次性"消灭"进弃牌堆。
            //    实测 match40 act=4：牌库 68→27、弃牌 0→50，其中 49 张正好是该局
            //    defense<=0 的指令牌总数，第 50 张是刚打出的那张。
            //    "阵亡"只适用于在场单位 —— 手牌和牌库里的牌不会因为防御力是 0 而死。
            if (!card.IsHq && card.IsAlive && card.Location.IsBoard() && card.Defense <= 0)
            {
                (dying ??= new List<CardInstance>()).Add(card);
            }
        }

        if (dying is null)
        {
            return;
        }

        foreach (var card in dying)
        {
            Destroy(card);
        }
    }

    /// <summary>ApplyRemoveCardFromBoard + ExecuteOnCardDestroyedFunction.</summary>
    public void Destroy(CardInstance card, CardInstance? destroyer = null, bool destroyedInCombat = false)
    {
        if (!card.IsAlive || card.IsHq) return;
        var batch = new[] { card.CardId };
        BeforeDestroyed(card, destroyer, destroyedInCombat);
        var oldLocation = card.Location;
        FireLeaveTrigger(card, CardLocation.Discard, method: 1);
        Say($"{card} 被摧毁");
        FireSubAction("ZActionDestroyUnit", new[]
        {
            ActionValue2.Int("destroyerCardID", destroyer?.CardId ?? 0),
            ActionValue2.Int("destroyedLocation", (int)oldLocation),
        });
        // ApplyRemoveCardFromBoard -> CardLocationMoved: Discard retains its old number,
        // then RefreshLocationStatus runs before ExecuteOnCardLocationMoved.
        State.Move(card, CardLocation.Discard, locationNumber: card.LocationNumber,
            moveReason: CardMoveReason.Destroyed, notifyMoved: false, normalizeLocation: false);
        Api.RefreshLocationStatus(new[] { oldLocation, CardLocation.Discard });
        State.CardMoved?.Invoke(card, oldLocation, CardLocation.Discard, false, CardMoveReason.Destroyed);
        FireAfterLeaveTrigger(card, CardLocation.Discard, oldLocation);
        ExecuteDestroyed(card, destroyer, oldLocation, batch, destroyedInCombat);
    }

    /// <summary>ExecuteAttackCard i=3967..4442: simultaneous destruction with distinct killers.</summary>
    internal void DestroyCombatPair(CardInstance defender, CardInstance attacker)
    {
        var cards = new[] { defender, attacker };
        var killers = new[] { attacker, defender };
        var oldLocations = cards.Select(c => c.Location).ToArray();
        for (int i = 0; i < cards.Length; i++) BeforeDestroyed(cards[i], killers[i], true);
        foreach (var card in cards) FireLeaveTrigger(card, CardLocation.Discard, method: 1);
        for (int i = 0; i < cards.Length; i++)
        {
            // ExecuteAttackCard i=4151/4208 calls CardLocationMoved separately:
            // each removal refreshes its location before its own movement callbacks.
            State.Move(cards[i], CardLocation.Discard, locationNumber: cards[i].LocationNumber,
                moveReason: CardMoveReason.Destroyed, notifyMoved: false, normalizeLocation: false);
            Api.RefreshLocationStatus(new[] { oldLocations[i], CardLocation.Discard });
            State.CardMoved?.Invoke(cards[i], oldLocations[i], CardLocation.Discard, false, CardMoveReason.Destroyed);
        }
        for (int i = 0; i < cards.Length; i++) FireAfterLeaveTrigger(cards[i], CardLocation.Discard, oldLocations[i]);
        for (int i = 0; i < cards.Length; i++)
            ExecuteDestroyed(cards[i], killers[i], oldLocations[i], new[] { cards[i].CardId, killers[i].CardId }, true);
    }

    /// <summary>
    /// ApplyDestroyMultipleCards: all before hooks, all leaves, all moves, all
    /// after-leave hooks, then destruction effects. Every callback sees the same batch.
    /// </summary>
    public void DestroyMultiple(IReadOnlyList<CardInstance> cards, CardInstance? destroyer)
    {
        var targets = cards.Where(x => !x.IsHq).ToArray();
        var ids = targets.Select(x => x.CardId).ToArray();
        var locations = new Dictionary<int, CardLocation>();
        var affected = new HashSet<CardLocation>();
        foreach (var card in targets)
        {
            BeforeDestroyed(card, destroyer, false);
            locations[card.CardId] = card.Location;
            affected.Add(card.Location);
        }
        foreach (var card in targets)
        {
            if (card.Location == CardLocation.Discard) locations.Remove(card.CardId);
            else FireLeaveTrigger(card, CardLocation.Discard, method: 1);
        }
        foreach (var card in targets)
        {
            // The batch Blueprint uses SetCardLocationAndLocNumber, not CardLocationMoved.
            // That setter skips the locationNumber write for Discard.
            State.Move(card, CardLocation.Discard, locationNumber: card.LocationNumber,
                notifyMoved: false, normalizeLocation: false);
        }
        // i=1509 refreshes affected locations before any move callback.
        Api.RefreshLocationStatus(affected);
        for (int i = 0; i < targets.Length; i++)
            ExecuteLocationMovedEvents(targets[i], locations.GetValueOrDefault(targets[i].CardId), CardLocation.Discard, false, CardMoveReason.Destroyed);
        for (int i = 0; i < targets.Length; i++)
            FireAfterLeaveTrigger(targets[i], CardLocation.Discard, locations.GetValueOrDefault(targets[i].CardId));
        for (int i = 0; i < targets.Length; i++)
            ExecuteDestroyed(targets[i], destroyer, locations.GetValueOrDefault(targets[i].CardId), ids, false);
        if (affected.Contains(CardLocation.BoardFrontline)) RefreshFrontlineOwner(null);
    }

    internal void BeforeDestroyed(CardInstance card, CardInstance? killer, bool combat, bool triggered = false)
    {
        // ExecuteOnBeforeOtherCardDestroyed i=140/520/574: suppressed skips only self.
        if (!card.IsSuppressed)
            Api.FireTrigger("OnBeforeDestroyed", card, card.Owner,
                eventArgs: new object?[] { killer, triggered }, eventSubject: card,
                namedArgs: new Dictionary<string, object?>
                {
                    ["killer"] = killer, ["attacker"] = killer, ["TriggerNotDestroyed"] = triggered,
                });
        // i=176 queries after the self callback. The returned T15 array has
        // no location filter and is not re-filtered during observer callbacks.
        var observers = Api.CaptureRegisteredTriggerSnapshot("OnBeforeOtherCardDestroyed")
            .Where(observer => observer.CardId != card.CardId).ToArray();
        foreach (var observer in observers)
            Api.FireTrigger("OnBeforeOtherCardDestroyed", observer, observer.Owner,
                selfProgramName: "OnBeforeOtherCardDestroyed",
                eventArgs: new object?[] { card, killer, triggered, combat }, eventSubject: card,
                namedArgs: new Dictionary<string, object?>
                {
                    ["cardDestroyed"] = card, ["killer"] = killer, ["attacker"] = killer,
                    ["TriggerNotDestroyed"] = triggered, ["DestroyedInCombat"] = combat,
                }, recipientSnapshot: new[] { observer }, includeUnavailableRecipients: true);
    }

    internal void ExecuteDestroyed(CardInstance card, CardInstance? killer, CardLocation location,
                                  IReadOnlyList<int> batch, bool combat)
    {
        // ReceiveActionDestroy*: history is updated after movement and before
        // destruction callbacks; explicit off-board destruction also counts.
        State.RecordDestroyedCard(card.CardId, card.Owner, Api.IsUnit(card));
        // Capture T24 before OnDestroyed can create/remove subscribers (i=225).
        var destructionRecipients = Api.CaptureRegisteredTriggerSnapshot("OnDestructionEffectTriggered");
        void SelfDestroyed(bool repeated) => Api.FireTrigger("OnDestroyed", card, card.Owner,
            eventArgs: new object?[] { killer, repeated }, eventSubject: card,
            namedArgs: new Dictionary<string, object?>
            {
                ["killer"] = killer, ["TriggerNotDestroyed"] = repeated,
            });
        int NotifyDestruction(bool skipSuppress = false) => Api.FireDestructionEffectTriggered(
            card, killer, batch, destructionRecipients, skipSuppress);

        // i=292..657: the self-source ability matters; abilities from an observer
        // trigger that observer's destruction branch below.
        if (!card.IsSuppressed
            && !CardApi.CustomNameHasAttribute(card, "customName1", "StopDestructionEffect")
            && (card.Keywords.Contains(Keyword.Destruction)
                || Api.HasCustomAbilityFromCard(card, "destruction", card.CardId)))
        {
            SelfDestroyed(false);
            int extra = NotifyDestruction();
            for (int i = 0; i < extra; i++)
            {
                SelfDestroyed(true);
                NotifyDestruction();
            }
        }

        // T39 is fetched after the self effect (i=1016), excluding the destroyed object.
        var observers = Api.CaptureRegisteredTriggerSnapshot("OnOtherCardDestroyed")
            .Where(x => x.CardId != card.CardId)
            .ToArray();
        foreach (var observer in observers)
        {
            void OtherDestroyed(bool repeated) => Api.FireTrigger(
                "OnOtherCardDestroyed", observer, observer.Owner,
                selfProgramName: "OnOtherCardDestroyed", eventSubject: card,
                eventArgs: new object?[] { card, killer, repeated, (int)location,
                                          batch.Contains(observer.CardId), combat },
                namedArgs: new Dictionary<string, object?>
                {
                    ["cardDestroyed"] = card, ["killer"] = killer,
                    ["TriggerNotDestroyed"] = repeated, ["destroyedLocation"] = (int)location,
                    ["selfIsAlsoGettingDestroyed"] = batch.Contains(observer.CardId),
                    ["destroyedInCombat"] = combat,
                }, recipientSnapshot: new[] { observer }, includeUnavailableRecipients: true);
            OtherDestroyed(false);
            // i=1543..1938: external destruction ability belongs to its source observer.
            if (Api.HasCustomAbilityFromCard(card, "destruction", observer.CardId))
            {
                int extra = NotifyDestruction(skipSuppress: true);
                for (int i = 0; i < extra; i++)
                {
                    OtherDestroyed(true);
                    NotifyDestruction();
                }
            }
        }
        // i=2147..2572: salvage resolves after destruction broadcasts.
        if (killer is not null && killer.Keywords.Contains(Keyword.Salvage)
            && killer.Owner == State.ActiveSide && card.Owner == killer.Owner.Opposite()
            && !Api.HasCustomAbility(card, "cantBeSalvaged"))
            Api.SalvageMultipleUnits(new[] { card.CardId }, killer.CardId);

        // i=2636: this self event is outside the destruction/suppression gate.
        Api.FireTrigger("OnAfterDestroyed", card, card.Owner,
            eventArgs: new object?[] { killer, false }, eventSubject: card,
            namedArgs: new Dictionary<string, object?>
            {
                ["killer"] = killer, ["TriggerNotDestroyed"] = false,
            });
    }

    // ==================== 动作记录 ====================

    internal void RecordAction(string actionType, Side player, Dictionary<string, object?> data, IReadOnlyList<SubAction>? subs = null)
    {
        _actionId++;
        State.ActionLog.Add(new GameAction(
            _actionId, actionType, player, State.Turn,
            subs ?? Array.Empty<SubAction>(),
            data));
    }

    /// <summary>产生并记录一个子动作（对应游戏里的 CreateAction_AddSubAction / AddSubAction*）。</summary>
    internal void FireSubAction(string name, IEnumerable<ActionValue2> values)
        => State.ActionLog.Add(new GameAction(
            ++_actionId, "SubAction", State.ActiveSide, State.Turn,
            new[] { new SubAction(name, values.ToList()) },
            new Dictionary<string, object?>()));

    /// <summary>该动作名是否是「效果」而不是纯查询 —— 用于统计未实现的效果调用。</summary>
    public IReadOnlyDictionary<string, int> UnimplementedCalls => State.UnimplementedCalls;

    /// <summary>
    /// `ChangeUnitOwnership` 的无头实现。夺取控制权时把单位放到新控制方半场，
    /// 释放时按调用方保存的原始位置恢复；两条路径都维护阵营索引、换区事件和协议子动作。
    /// </summary>
    public bool ChangeUnitOwnership(CardInstance card, int instigatorId, Side fromSide,
                                    Side toSide, CardLocation originalLocation,
                                    bool releaseControl)
    {
        if (!card.Definition.IsUnit || !card.Location.IsBoard()
            || card.Owner != fromSide || toSide is Side.NotAvailable
            || fromSide == toSide)
        {
            return false;
        }

        CardLocation oldLocation = card.Location;
        Side oldOwner = card.Owner;
        CardLocation destination = releaseControl ? originalLocation : toSide.HqOf();
        // ChangeUnitOwnership i=2683..2854: a lone frontline unit stays there;
        // with other units present it follows the support-line branch i=753.
        if (!releaseControl && oldLocation == CardLocation.BoardFrontline
            && State.Cards(Side.Left, oldLocation).Count + State.Cards(Side.Right, oldLocation).Count == 1)
            destination = CardLocation.BoardFrontline;
        if (destination is not (CardLocation.BoardHqLeft or CardLocation.BoardHqRight
            or CardLocation.BoardFrontline))
        {
            destination = toSide.HqOf();
        }

        int frontlineCount = State.Cards(Side.Left, CardLocation.BoardFrontline).Count
            + State.Cards(Side.Right, CardLocation.BoardFrontline).Count;
        // Release queries occupancy before changing ownership or location (i=130).
        // Its count includes this card when it already occupies the destination.
        bool destinationFull = destination switch
        {
            CardLocation.BoardFrontline => frontlineCount
                - (!releaseControl && oldLocation == CardLocation.BoardFrontline ? 1 : 0) >= State.FrontlineCapacity,
            CardLocation.BoardHqLeft or CardLocation.BoardHqRight
                => State.Cards(toSide, destination).Count >= GameState.HalfBoardCapacity,
            _ => false,
        };
        // The take branch exits at i=927. Release instead restores the original
        // board location, runs enter events, then applies retreat at i=1948.
        if (destinationFull && !releaseControl) return false;
        bool frontlineBlocked = releaseControl && destination == CardLocation.BoardFrontline
            && State.FrontlineOwner == fromSide
            && (frontlineCount > 1 || oldLocation != CardLocation.BoardFrontline);
        bool shouldRetreat = releaseControl && (destinationFull || frontlineBlocked);

        FireLeaveTrigger(card, destination, method: 5);
        if (!State.ChangeOwner(card, toSide))
        {
            return false;
        }

        card.UnderEnemyControl = !releaseControl;
        State.Move(card, destination, locationNumber: null, changeOwner: true,
            moveReason: releaseControl ? CardMoveReason.ControlRelease : CardMoveReason.ControlTake);
        // ChangeUnitOwnership resets movement/attack allowance only when taking
        // control (i=1090/1190/2859), not in the release branch i=1527..1948.
        if (!releaseControl)
        {
            card.HasMovedThisTurn = false;
            card.AttacksThisTurn = 0;
            card.AttackQuotaOffset = 0;
            card.HasAttackedThisTurn = false;
        }

        // CardLocationMoved is also explicitly called for same-location owner
        // changes (i=1363/1780); GameState.Move only notifies on location changes.
        if (oldLocation == destination)
            ExecuteLocationMovedEvents(card, oldLocation, destination, true,
                releaseControl ? CardMoveReason.ControlRelease : CardMoveReason.ControlTake, true);

        FireAfterLeaveTrigger(card, destination, oldLocation);

        FireSubAction("ZActionChangeUnitOwnership", new[]
        {
            ActionValue2.Bool("releaseControl", releaseControl),
            ActionValue2.Int("newLocation", (int)destination),
            ActionValue2.Str("newSide", toSide.ToWire()),
            ActionValue2.Int("oldLocation", (int)oldLocation),
            ActionValue2.Str("oldSide", oldOwner.ToWire()),
            ActionValue2.Int("instigatorID", instigatorId),
        });

        if (destination.IsBoard())
        {
            ExecuteOnEnterPlayEvents(card, 0);
        }

        if (shouldRetreat) Api.ApplyMakeCardRetreat(card, 0);

        return true;
    }
}
