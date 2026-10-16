using KLink.Bot.Engine;

namespace KLink.Bot.Server;

/// <summary>
/// 服务端对局里的一张卡（中立 DTO）。
///
/// 为什么不让 <c>KLink.Bot</c> 直接引用 fyserver 的 <c>MatchCard</c>：
/// 那会让内核反向依赖服务器工程（fyserver 是 <c>Microsoft.NET.Sdk.Web</c>，
/// 还带自己的 Program 入口），依赖方向会变得很脏。
/// 所以这里用一个字段对得上的中立 DTO，由 fyserver 侧一行映射填进来。
///
/// 字段与 fyserver 的 <c>MatchCard(CardId, IsGold, Location, LocationNumber, Name)</c> 一一对应。
/// </summary>
public sealed record ServerCard(
    int CardId,
    bool IsGold,
    string Location,
    int LocationNumber,
    string Name);

/// <summary>
/// 服务端对局里的一条动作（中立 DTO）。
/// 对应 fyserver 的 <c>MatchAction(ActionId, ActionType, PlayerId, ActionData, sub_actions, turn_number, …)</c>。
/// </summary>
public sealed record ServerAction(
    int ActionId,
    string ActionType,
    int PlayerId,
    IReadOnlyDictionary<string, string> ActionData,
    int TurnNumber);

/// <summary>
/// **服务器的对局快照** —— 在任何时刻都能从 `MatchInfo` 完整重建棋盘所需的全部输入。
///
/// ⚠️ 关键认识（这是整个接线能成立的前提）：
/// **服务端不需要「棋盘状态」也能驱动 bot。** 它有的是
/// 「双方牌库 + 手牌 + 完整动作流」，而游戏是**确定性锁步**的
/// （效果由双方各自本地结算，见 `评估与实施路线图.md` §2 修正 2）。
/// 所以把动作流喂进内核重放一遍，就得到与客户端一致的局面。
///
/// 这比「从客户端抓棋盘状态」便宜得多，而且**不需要改客户端**。
/// </summary>
public sealed record ServerMatchSnapshot(
    int MatchId,
    int Turns,
    int LeftPlayerId,
    int RightPlayerId,
    /// <summary>双方全部卡（含 HQ）。顺序不重要 —— 位置由 <see cref="ServerCard.LocationNumber"/> 定。</summary>
    IReadOnlyList<ServerCard> Cards,
    IReadOnlyList<ServerAction> Actions)
{
    /// <summary>下一条该分配的动作号（服务端 `MatchInfo.currentActionId`）。</summary>
    public int NextActionId { get; init; }

    /// <summary>
    /// 客户端最后确认收到的动作号（服务端 `MatchInfo.SendActionId`）。
    /// bot 生成的动作要带这个值，客户端才认。
    /// </summary>
    public int SendActionId { get; init; }

    /// <summary>fyserver ActionCipher 包头使用的整局会话 ID。</summary>
    public int ActionSessionId { get; init; }

    /// <summary>
    /// 卡 **枚举顺序** 必须让 `locationNumber` 说了算，所以这里按
    /// (owner, location, locationNumber) 排一遍再交给内核。
    ///
    /// 为什么重要：`ReplayRunner` 建卡时把 `locationNumber` 原样写进去，
    /// 而 `GameState.Cards()` 按 `locationNumber` 排序 —— 牌库顺序错了，
    /// 每次抽牌都会抽错卡（2026-10-01 踩过，区域准确率只有 60%）。
    /// </summary>
    public IEnumerable<ServerCard> CardsInSpawnOrder()
        => Cards.OrderBy(c => LocationRank(c.Location))
                .ThenBy(c => c.LocationNumber)
                .ThenBy(c => c.CardId);

    private static int LocationRank(string loc) => loc switch
    {
        "board_hqleft" or "board_hqright" => 0,
        "hand_left" or "hand_right" => 1,
        "deck_left" or "deck_right" => 2,
        _ => 3,
    };
}
