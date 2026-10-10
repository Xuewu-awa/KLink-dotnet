using KLink.Bot.Engine;
using KLink.Bot.Replay;

namespace KLink.Bot.Server;

/// <summary>
/// **服务端对局 → 内核重放**的桥。这是「让 bot 在真对局里下棋」的第一步。
///
/// ## 为什么这条路可行（前提，别绕过它）
///
/// 服务端**没有棋盘状态**（`MatchState` 只有手牌/牌库/弃牌/动作列表），
/// 也**不做任何合法性校验**（原样搬运 `action_data`）。但游戏是**确定性锁步**的：
/// 效果由双方各自本地结算，动作流就是全部信息。
///
/// 于是：**把服务端的动作流喂进内核重放一遍，就得到与客户端一致的局面。**
///
/// 这比「从客户端抓棋盘状态」便宜得多，而且**不用改客户端、不用 UE4SS**。
///
/// ## 已知的两个坑（都踩过，别重犯）
///
/// 1. **牌库顺序**：`GameState.Cards()` 按 `locationNumber` 排序，而 `DrawCard` 取第一张。
///    所以 `locationNumber` 必须如实播种 —— 否则「每回合抽到哪张牌」是错的
///    （2026-10-01 实测：区域逐卡准确率只有 60%）。
///    服务端的 `GetCardsFromDeck` 恰好**就是**按客户端顺序编号的
///    （`LeftHand` 0..3、`LeftDeck` 4..38），所以直接照抄即可。
/// 2. **`Location` 必须落成「分侧」枚举**：`CardLocation.Deck(=9)` 不是
///    `State.Deck(side)` 查的值。用错会让牌库看起来是空的 ⇒ 每回合凭空吃疲劳伤害。
///
/// ## 本类**不做**的事
///
/// 不生成动作、不调策略 —— 它只负责「把服务器的快照变成一份可重放的 `ReplayData`」。
/// 决策在 `BotTurnService` 里。
/// </summary>
public static class ServerReplayBridge
{
    /// <summary>
    /// 把服务端快照转成内核的 <see cref="ReplayData"/>。
    /// </summary>
    /// <param name="snapshot">服务端当前对局（双方牌库/手牌 + 完整动作流）。</param>
    /// <param name="localSubactions">
    /// 是否「效果本地结算」。服务端 `MatchStartingInfo.LocalSubactions` 实测为 true，
    /// 保留成参数是为了将来能复现旧口径。
    /// </param>
    /// <param name="clientSide">
    /// **官方客户端是哪一方** —— 现在**只是诊断信息**（见 `ReplayData.ClientSide`）。
    /// 实时对局里 bot 自己知道坐哪边（`BotTurnService.BotSide`），所以由调用方显式给
    /// **对手那一方**；不给时按玩家 id 推断（见 <see cref="ReplayData.InferClientSide"/>）。
    ///
    /// ⚠️ **2026-10-02 起它不再影响发号**：生成卡的编号规则（`回合号 × 1000 + 第几张`）
    /// 对**双方一致**（客户端分配器 `GenerateNextCardID` 没有 side 参数，见
    /// `GameState.NextCardId`）。以前只让这一方走客户端规则是错的 ——
    /// 我们 bot 生成卡发顺序号，客户端认不出 ⇒ **虚空部署**。
    /// </param>
    public static ReplayData ToReplayData(ServerMatchSnapshot snapshot, bool localSubactions = true,
                                          Side clientSide = Side.NotAvailable)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var cards = new List<ReplayData.SnapshotCard>(snapshot.Cards.Count);
        foreach (var c in snapshot.CardsInSpawnOrder())
        {
            if (c.CardId <= 0)
            {
                continue;   // 生成卡/伪卡的兜底：cardID 0 在内核里是"模板卡"的意思，不能进对局
            }

            if (!TryLocation(c.Location, out CardLocation loc, out Side owner))
            {
                continue;   // 认不出的位置（例如 sideeffect holder）直接跳过
            }

            cards.Add(new ReplayData.SnapshotCard(c.CardId, c.Name, loc, c.LocationNumber, owner, c.IsGold,
                                       Faction: null));
        }

        var actions = new List<WireAction>(snapshot.Actions.Count);
        foreach (var a in snapshot.Actions)
        {
            if (string.IsNullOrEmpty(a.ActionType) && a.ActionData.Count == 0)
            {
                continue;   // 空动作（服务端有时会记 `lvl-loaded` 之类）
            }

            actions.Add(new WireAction
            {
                ActionType = a.ActionType,
                PlayerId = a.PlayerId,
                ActionId = a.ActionId,
                LocalSubactions = localSubactions,
                TurnNumber = a.TurnNumber,
                SubActionCount = 0,      // 锁步下服务端侧恒为 0
                ActionData = a.ActionData,
            });
        }

        return new ReplayData
        {
            MatchId = snapshot.MatchId,
            Turns = snapshot.Turns,
            LeftPlayerId = snapshot.LeftPlayerId,
            RightPlayerId = snapshot.RightPlayerId,
            WinnerSide = "",             // 进行中的对局没有胜方
            ClientSide = clientSide != Side.NotAvailable
                ? clientSide
                : ReplayData.InferClientSide(snapshot.LeftPlayerId, snapshot.RightPlayerId, actions),
            Cards = cards,
            Actions = actions,
        };
    }

    /// <summary>
    /// 服务端的 `location` 字符串 → (内核位置, 归属方)。
    ///
    /// 字符串取值取自 fyserver 的 `GetCardsFromDeck` / `MakeMatchStartingInfo`：
    /// `board_hqleft` / `board_hqright` / `hand_left` / `hand_right` / `deck_left` /
    /// `deck_right`；`discard_left` / `discard_right` 是 KLink 在换牌快照中补出的
    /// 明确归属别名，真实线上 `discard` 位置仍然不接受，因为它没有侧信息。
    ///
    /// ⚠️ 返回**分侧**枚举（`DeckLeft`/`DeckRight`…），不是 `CardLocation.Deck`/`Hand`。
    /// </summary>
    public static bool TryLocation(string wire, out CardLocation location, out Side owner)
    {
        switch (wire)
        {
            case "board_hqleft": location = CardLocation.BoardHqLeft; owner = Side.Left; return true;
            case "board_hqright": location = CardLocation.BoardHqRight; owner = Side.Right; return true;
            case "hand_left": location = CardLocation.HandLeft; owner = Side.Left; return true;
            case "hand_right": location = CardLocation.HandRight; owner = Side.Right; return true;
            case "deck_left": location = CardLocation.DeckLeft; owner = Side.Left; return true;
            case "deck_right": location = CardLocation.DeckRight; owner = Side.Right; return true;
            case "discard_left": location = CardLocation.Discard; owner = Side.Left; return true;
            case "discard_right": location = CardLocation.Discard; owner = Side.Right; return true;
            case "discard": location = CardLocation.Discard; owner = Side.NotAvailable; return false;
            default: location = CardLocation.NotAvailable; owner = Side.NotAvailable; return false;
        }
    }
}
