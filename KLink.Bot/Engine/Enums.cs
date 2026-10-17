namespace KLink.Bot.Engine;

/// <summary>
/// 阵营。取自游戏原生枚举 <c>/Script/kards.ESideEnum</c>。
/// </summary>
public enum Side
{
    NotAvailable = 0,
    Left = 1,
    Right = 2,
}

/// <summary>
/// `EGameplayRestrictions` from the game's public headers.
/// These values are part of the Blueprint ABI; do not reorder them.
/// </summary>
public enum GameplayRestrictionType
{
    CannotDrawCardAtTurnStart = 0,
    CannotKreditSlotAtTurnStart = 1,
    CannotPlayOrders = 2,
    CannotDeployUnits = 3,
    CannotAttackWithGroundUnits = 4,
    CannotDiscardAnyCardFromHand = 5,
    NotAvailable = 6,
}

/// <summary>
/// 卡牌所在位置。逐值对应游戏原生枚举 <c>/Script/kards.ECardLocationEnum</c>
/// （从 kards-Win64-Shipping.exe 的 .jmap 转储中提取，见 decompiled/kards-enums.json）。
///
/// 注意：整个牌桌（前线 + 支援线）在协议里是同一个位置 <c>Board_Frontline</c>，
/// 具体槽位由 <c>locationNumber</c> 区分 —— 这一点后面要用真实回放确认。
///
/// ⚠️ 补充（2026-09-27，蓝图定案）：<c>5/6</c> 虽然叫 <c>Board_HQ*</c>，
/// **它就是半场（支援线）**：蓝图的 <c>GetSupportLineLocationBySide(side)</c> 是
/// <c>side==1 → 5</c>、<c>side==2 → 6</c>，而 HQ 这份"位置卡"和单位**同处这一格**
/// （HQ 占 1 格容量 ⇒ 半场 5 格里最多放 4 个单位）。
/// 因此**判断"是不是 HQ"要看卡的类型**（<c>location</c>），不能看位置 ——
/// 否则每个部署到半场的单位都会被当成 HQ（被 `Board()` 过滤、被 `LegalTargets` 挡掉）。
/// 单位**默认部署到半场**，上前线（7）是主动动作。
/// </summary>
public enum CardLocation
{
    NotAvailable = 0,
    DeckLeft = 1,
    DeckRight = 2,
    HandLeft = 3,
    HandRight = 4,

    /// <summary>左方**半场**（支援线）。HQ 也在这一格。</summary>
    BoardHqLeft = 5,

    /// <summary>右方**半场**（支援线）。HQ 也在这一格。</summary>
    BoardHqRight = 6,

    /// <summary>前线（双方共享，容量 5；<c>card_unit_black_prince</c> 在场时 2）。</summary>
    BoardFrontline = 7,

    Discard = 8,
    Deck = 9,
}

/// <summary>新卡进入牌库的位置。对应 <c>/Script/kards.EnumSpawnInDeckLocation</c>。</summary>
public enum SpawnInDeckLocation
{
    NotAvailable = 0,
    Bottom = 1,
    Top = 2,
    Shuffle = 3,
}

/// <summary>Current pak's CardMoveReason EnumExport, joined to its DisplayNameMap.</summary>
public enum CardMoveReason
{
    AdvancePlayerMove = 0,
    AdvanceForce = 1,
    BoardToHand = 2,
    ToTopOfDeck = 3,
    PlayCardPlayerMove = 4,
    ControlRelease = 5,
    ControlTake = 6,
    ChangeForce = 7,
    PlayCardForce = 8,
    Retreat = 9,
    Destroyed = 10,
    Removed = 11,
    Uncategorized = 12,
    Convert = 13,
}

public static class SideExtensions
{
    public static Side Opposite(this Side side) => side switch
    {
        Side.Left => Side.Right,
        Side.Right => Side.Left,
        _ => Side.NotAvailable,
    };

    /// <summary>协议里的 side 字符串（小写）。</summary>
    public static string ToWire(this Side side) => side switch
    {
        Side.Left => "left",
        Side.Right => "right",
        _ => "",
    };

    public static Side FromWire(string? value) => value switch
    {
        "left" => Side.Left,
        "right" => Side.Right,
        _ => Side.NotAvailable,
    };

    public static CardLocation HandOf(this Side side) => side == Side.Left ? CardLocation.HandLeft : CardLocation.HandRight;
    public static CardLocation DeckOf(this Side side) => side == Side.Left ? CardLocation.DeckLeft : CardLocation.DeckRight;
    public static CardLocation HqOf(this Side side) => side == Side.Left ? CardLocation.BoardHqLeft : CardLocation.BoardHqRight;

    public static bool IsBoard(this CardLocation loc) =>
        loc is CardLocation.BoardFrontline or CardLocation.BoardHqLeft or CardLocation.BoardHqRight;
}
