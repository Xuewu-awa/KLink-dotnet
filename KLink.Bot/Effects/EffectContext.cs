using KLink.Bot.Engine;

namespace KLink.Bot.Effects;

/// <summary>
/// 触发时机。名字直接取自反编译出的 <c>On*</c> / <c>doOn*</c> 调用名
/// （见 docs/卡牌效果提取报告.md §3.3，共约 160 个）。
///
/// 这是内核事件系统的骨架：每张卡的「触发式效果」都挂在其中一个时机上。
/// </summary>
public enum GameEvent
{
    GameStart,
    StartOfTurn,
    EndOfTurn,
    CardEnterPlay,
    CardPlayedFromHand,
    CardSpawnedInHand,
    CardDrawnFromDeck,
    CardDestroyed,
    BeforeDestroyed,
    BeforeAttack,
    AfterAttack,
    ReceiveDamage,
    DealDamage,
    MoveToFrontline,
    MoveFromFrontline,
    FrontlineOwnershipChange,
    BecomingVeteran,
    Discard,
    Reveal,
    Suppressed,
    Pinned,
    DeploymentEffectTriggered,
    DestructionEffectTriggered,
    PincerEffectApplied,
    IntelTriggered,
    CounterMeasureTriggered,
    NavalEngagementPlayed,
}

/// <summary>
/// 一次效果结算的上下文。对应 Blueprint 里卡函数的那几个入参
/// （<c>instigatorID</c> / <c>targetCardID</c> / <c>side</c> 等）。
/// </summary>
public sealed class EffectContext
{
    public required MatchEngine Engine { get; init; }
    public required GameState State { get; init; }

    /// <summary>效果所属的卡（触发源）。全局事件时为 null。</summary>
    public CardInstance? Self { get; set; }

    /// <summary>玩家选定的目标。</summary>
    public CardInstance? Target { get; set; }

    /// <summary>效果控制方。</summary>
    public required Side Controller { get; init; }

    /// <summary>触发本次结算的卡（例如「你打出某张牌时」的那个某张牌）。</summary>
    public CardInstance? Trigger { get; set; }

    /// <summary>伤害等事件中的显式目标载荷（例如 OnCardDealDamage.toCard）。</summary>
    public CardInstance? EventTarget { get; set; }

    /// <summary>
    /// 触发事件的**额外入参**，按顺序。
    ///
    /// 为什么要单独一个袋子：卡蓝图的触发程序读的是事件入参，在 IR 里表现为
    /// `K2Node_Event_xxx` 这类变量（`K2Node_Event_drawnSide` / `_goingToLocation` …）。
    /// 官方字节码里这些变量由事件 stub 赋值，而 IR 生成器只编 `ExecuteUbergraph_*`，
    /// **赋值那一步被丢掉了** —— 于是 IR 里读它们永远是 null。
    /// 这里由派发方（<see cref="CardApi.FireTrigger"/> 的调用者）显式放进去。
    /// 顺序就是变量名里 `_1` / `_2` 后缀的依据（`K2Node_Event_xxx_1` = 第 2 个入参，从 0 数）。
    /// </summary>
    public IReadOnlyList<object?> EventArgs { get; set; } = Array.Empty<object?>();

    /// <summary>
    /// 按**事件入参变量名**（去掉 `K2Node_Event_` 前缀和 `_1` 后缀）索引的具名载荷。
    ///
    /// 为什么需要它，而不是只靠 <see cref="EventArgs"/> 的位置：
    /// 蓝图事件桩里的变量名**不是**入参下标。实测
    /// `OnCardDrawnFromDeck(bool StartOfTurnDraw, ESideEnum drawnSide)` 的两个槽位是
    /// `K2Node_Event_StartOfTurnDraw` / `K2Node_Event_StartOfTurnDraw_1` 与
    /// `K2Node_Event_drawnSide` / `K2Node_Event_drawnSide_1` ——
    /// `_1` 是「同名槽位第 2 次声明」，**不是「第 2 个入参」**。
    /// 而 `OnOtherCardDrawnFromDeck(int32 drawnCardID, bool StartOfTurnDraw, ESideEnum drawnSide)`
    /// 的 `drawnSide` 明明是第 3 个入参（下标 2），槽位名却仍是 `K2Node_Event_drawnSide_1`。
    /// 用下标推会**系统性读错**，所以派发方按名字直接给值。
    ///
    /// 出处：`klink bot/tools/extract-event-contracts.py` 从
    /// `klink bot/decompiled/cards.full.json` 的 `LetValueOnPersistentFrame` 抽出的
    /// 槽位表（→ `klink bot/docs/event-contracts.json`）。
    /// </summary>
    public IReadOnlyDictionary<string, object?> NamedArgs { get; set; } =
        EmptyNamedArgsPublic;

    /// <summary>空具名载荷表（供派发方复用，避免每次分配）。</summary>
    public static readonly Dictionary<string, object?> EmptyNamedArgsPublic = new();

    /// <summary>
    /// 「这张卡要去哪」（`OnLeaveBoardOrOwner` 的第一个入参，值是 <see cref="CardLocation"/>）。
    /// 单独拎出来是因为 `card_unit_214th_amur` 用它判「离场去向是不是 [半场, 前线]」。
    /// </summary>
    public CardLocation? GoingToLocation { get; set; }

    /// <summary>
    /// `OnCardLocationMoved` / `OnOtherCardLocationMoved` 的旧位置。
    ///
    /// 单拎出来是因为这两个事件的**其它**入参在两个变体里下标不同
    /// （自己那个是 `[oldLocation, newLocation, ChangeOwner, MoveReason]`，
    /// 别人那个前面多一个 `cardMoved`），而 `newLocation` 又和
    /// <see cref="GoingToLocation"/> 的语义不通用（`OnLeaveBoardOrOwner` 只有一个去向）。
    /// </summary>
    public CardLocation? OldLocation { get; set; }

    /// <inheritdoc cref="OldLocation"/>
    public CardLocation? NewLocation { get; set; }

    public object? EventArg(int index)
        => index >= 0 && index < EventArgs.Count ? EventArgs[index] : null;

    /// <summary>该卡反编译出的调用清单，按原顺序。</summary>
    public IReadOnlyList<string> Calls { get; init; } = Array.Empty<string>();

    public Side Opponent => Controller.Opposite();

    public IEnumerable<CardInstance> MyBoard => State.Board(Controller);
    public IEnumerable<CardInstance> EnemyBoard => State.Board(Opponent);
    public IEnumerable<CardInstance> MyHand => State.Hand(Controller);
    public IEnumerable<CardInstance> EnemyHand => State.Hand(Opponent);

    public CardInstance MyHq => State.Hq(Controller);
    public CardInstance EnemyHq => State.Hq(Opponent);

    public void Emit(string subActionName, params ActionValue2[] values)
        => Engine.FireSubAction(subActionName, values);
}
