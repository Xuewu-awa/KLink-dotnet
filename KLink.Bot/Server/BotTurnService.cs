using KLink.Bot.Bots;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.NN;
using KLink.Bot.Replay;

namespace KLink.Bot.Server;

/// <summary>
/// **让 bot 在真对局里下一整个回合。** 这是接服务器那条链的核心。
///
/// ## 它怎么工作（三步）
///
/// 1. **重建局面**：把服务端的「双方牌库 + 完整动作流」喂进内核重放，
///    得到与客户端一致的局面（依据：游戏是确定性锁步，见
///    <see cref="ServerReplayBridge"/> 的说明）。
/// 2. **决策**：让策略在该局面上连续选动作，直到没得做。
/// 3. **产出**：把选中的动作转成服务端能直接发给客户端的 <see cref="ServerAction"/>。
///
/// ## 为什么**每一回合都从头重建**，而不是维护一个常驻引擎
///
/// 无状态重放更稳：服务端可能重启、动作流可能被外部追加、状态可能因异常丢步。
/// 每次从「牌库 + 完整动作流」重算，任何一次都不会带着上一回合的错误累积。
/// 代价是 O(动作数) 的重放 —— 一局几百条动作，**不构成瓶颈**。
///
/// ## ⚠️ 一个还没验证的前提
///
/// 客户端**会不会接受**服务端注入的「出牌/攻击/移动」动作，**只对回合边界动作验证过**
/// （`评估与实施路线图.md` §1.3 的现有 bot 只塞「开始回合 + 结束回合」）。
/// 客户端有 `isValidatingActionSent` / `syncErrorCheckCards`，
/// **注入格式不对会触发同步错误**。所以第一次真对局要重点看这个。
/// </summary>
public sealed class BotTurnService
{
    /// <summary>单回合动作上限，防死循环。</summary>
    private const int MaxActionsPerTurn = 60;

    private readonly CardDatabase _db;
    private readonly Side _botSide;
    private readonly int _botPlayerId;
    private readonly NnPolicy? _nn;

    public BotTurnService(CardDatabase db, Side botSide, int botPlayerId,
                          NnModel? model = null,
                          StateEncoder.CardVecs? vecs = null,
                          bool allowMoves = true)
    {
        _db = db;
        _botSide = botSide;
        _botPlayerId = botPlayerId;
        if (model is not null && vecs is not null)
        {
            _nn = new NnPolicy(model, vecs, botSide, allowMoves, rollout: false);
        }
    }

    public bool HasNeuralPolicy => _nn is not null;

    /// <summary>
    /// **漂开信号** —— 重建出来的局面是否已经和真人客户端对不上。
    ///
    /// 为什么要有它：内核有保真度缺口（未实现的原语、写错的规则），局面会**逐渐**漂开。
    /// 漂开之后继续下棋 = 在错误局面上做非法操作，客户端表现成
    /// 「卡牌悬空」「AI 移动虚空单位」。修保真度是长期活，
    /// 所以第一步是**漂开时别再把对局搞坏**（服务端据此拒绝下棋，见 `ServerBotService`）。
    ///
    /// ⚠️ **只统计人类（= 对手）的动作**：bot 自己的历史动作是**旧内核**生成的，
    /// 被新内核拒绝属正常，不是保真度信号
    /// （口径与 `out/audit/audit-all-replays.ps1` 的「人类失败」列一致）。
    /// </summary>
    public sealed record DivergenceSignals(
        /// <summary>人类动作里内核**应用不了**的条数（>0 = 已漂开）。</summary>
        int UnappliedHumanActions,
        /// <summary>人类动作里 HQ 校验和对不上的条数（>0 = 已漂开）。</summary>
        int HqMismatches,
        /// <summary>第一条「人类动作没应用」的可读描述（动作号 / 回合 / 类型 / 原因）。</summary>
        string? FirstUnapplied,
        /// <summary>第一条「HQ 对不上」的可读描述（动作号 / 回合 / 类型）。</summary>
        string? FirstHqMismatch,
        /// <summary>那条 HQ 对不上的动作里**客户端写的**值（= 它当时认为的对手 HQ）。</summary>
        int HqClient,
        /// <summary>**我们算出来**的对手 HQ（与 <see cref="HqClient"/> 直接可比）。</summary>
        int HqOur)
    {
        /// <summary>任一信号触发 ⇒ 判定为已漂开。</summary>
        public bool Detected => UnappliedHumanActions > 0 || HqMismatches > 0;

        public static readonly DivergenceSignals None = new(0, 0, null, null, -1, -1);

        /// <summary>给日志与 `/spectate/bot` 用的一句话原因（没漂开时是空串）。</summary>
        public string Reason
        {
            get
            {
                if (!Detected)
                {
                    return "";
                }

                var parts = new List<string>(2);
                if (UnappliedHumanActions > 0)
                {
                    parts.Add($"人类动作有 {UnappliedHumanActions} 条内核应用不了（{FirstUnapplied}）");
                }

                if (HqMismatches > 0)
                {
                    parts.Add($"HQ 校验和有 {HqMismatches} 条对不上" +
                              $"（客户端写 {HqClient}，我们算 {HqOur}；{FirstHqMismatch}）");
                }

                return string.Join("；", parts);
            }
        }
    }

    /// <summary>一个回合的决策结果。</summary>
    public sealed record TurnResult(
        /// <summary>bot 这一回合产出的动作（已带好 ActionId / TurnNumber / PlayerId）。</summary>
        IReadOnlyList<ServerAction> Actions,
        /// <summary>重建出来的局面（诊断用）。</summary>
        GameState? State,
        /// <summary>人可读的决策日志。</summary>
        IReadOnlyList<string> Log,
        /// <summary>重放时有多少动作没被内核应用（>0 说明两边已经对不上了）。</summary>
        int UnappliedActions,
        /// <summary>漂开信号（只含人类动作，见 <see cref="DivergenceSignals"/>）。</summary>
        DivergenceSignals? Divergence = null,
        /// <summary>
        /// **决策前**（刚重建完、bot 还没动手）对手 HQ —— 填 `XActionStartOfTurn` 用。
        ///
        /// 为什么要单独交出来：`State` 是**决策后**的局面，而回合边界动作里那个 HQ
        /// 是**采样当时**的值（实测 214436 人类自己的 StartOfTurn 写 22、同回合
        /// EndOfTurn 写 20 ⇒ 它随回合内发生的事情变）。所以两个时刻的值都要有。
        /// </summary>
        int HqOpponentBefore = -1);

    /// <summary>
    /// 决策**一整个回合**。返回的列表**总是**以一条 `XActionEndOfTurn` 结尾
    /// （即使这一回合什么都没做）—— 客户端靠它把控制权交回来。
    /// </summary>
    public TurnResult DecideTurn(ServerMatchSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var log = new List<string>();
        LearnHqKey(snapshot);

        // ---- 1) 重建局面 ----
        // ⚠️ 走 `RebuildEngine`，**不在这里另写一套** —— 试算副本要用同一个方法
        //    （见 `BuildReplayer`）。两套重建逻辑必然漂移，而漂移的症状极难查。
        _currentSnapshot = snapshot;
        _replayer = null;                     // 快照换了，副本工厂要重建
        var report = RebuildEngine(snapshot);
        var engine = report?.Engine;
        int unapplied = report is null ? 0 : report.TotalActions - report.AppliedCount;

        // ★★ **安全网在这里打开**（见 `MatchEngine.EnforceGeneratedCardTrust`）。
        //
        // 上面那一步是**重放**：`ReplayRunner` 特意把这道门关掉（重放的是客户端自己
        // 发过的动作，动作流本身就是"客户端认得这张卡"的证据，拒绝它只会让重建更差）。
        // 但从这一行往下我们要**生成自己的动作**了 —— 绝不能把「内核自己发号生成、
        // 客户端未必认得」的卡发出去：那正是真人玩家看到的**虚空部署**
        // （记牌器 +1、场上什么都没有；虚空单位会立刻不同步）。
        if (engine is not null)
        {
            engine.EnforceGeneratedCardTrust = true;
        }

        // ★ 漂开信号：**只看人类动作**（bot 自己的历史动作是旧内核产的，被拒属正常）。
        //   这一步要在决策**之前**做完 —— 服务端拿到它才能决定「这回合下不下棋」。
        var divergence = InspectDivergence(report, _botSide.Opposite());
        if (divergence.Detected)
        {
            log.Add($"⚠ 漂开信号：{divergence.Reason}");
        }

        if (engine is null)
        {
            log.Add("⚠ 重放没有产出引擎（动作流为空？）—— 只结束回合");
            return new TurnResult(OnlyEndTurn(snapshot, null, snapshot.Turns), null, log, unapplied, divergence);
        }

        var state = engine.State;

        // 决策**前**的对手 HQ（`state` 还是刚重建完的局面，bot 一条操作都没发）。
        // 它要用来填 `XActionStartOfTurn`（见 TurnResult.HqOpponentBefore 的注释）。
        int hqOpponentBefore = state.HqDefense(_botSide.Opposite());

        log.Add($"局面重建：回合 {state.Turn}，行动方 {state.ActiveSide.ToWire()}，" +
                $"动作 {report!.AppliedCount}/{report.TotalActions} 已应用" +
                (unapplied > 0 ? $"（⚠ {unapplied} 条没应用）" : "") +
                $"，HQ 左{state.HqDefense(Side.Left)}/右{state.HqDefense(Side.Right)}");

        if (state.IsFinished)
        {
            log.Add("对局已结束，不产出操作");
            return new TurnResult(OnlyEndTurn(snapshot, state, state.Turn), state, log, unapplied,
                                  divergence, hqOpponentBefore);
        }

        if (state.ActiveSide != _botSide)
        {
            // 服务端记录的「行动方」与内核重建出来的不一致 —— 说明两边已经漂开。
            // **不要硬下棋**（会在错误局面上做非法操作，触发客户端同步错误），只收尾并报警。
            log.Add($"⚠ 内核认为行动方是 {state.ActiveSide.ToWire()}，不是 bot 的 {_botSide.ToWire()}" +
                    " —— 局面可能已与客户端漂开，本回合不下棋");
            return new TurnResult(OnlyEndTurn(snapshot, state, state.Turn), state, log, unapplied,
                                  divergence, hqOpponentBefore);
        }

        // ---- 2) 决策：连续选动作，直到没得做 ----
        var chosen = new List<AtomicAction>();
        var pickedCards = new List<MatchEngine.PickRecord>();   // 选牌答复（CS）
        var handTargets = new List<MatchEngine.HandTargetRecord>();   // 手牌目标答复（HT）
        int guard = 0;

        while (!state.IsFinished && state.ActiveSide == _botSide && guard++ < MaxActionsPerTurn)
        {
            AtomicAction? next = ChooseOne(engine, chosen, log);
            if (next is null)
            {
                break;
            }

            if (!next.Apply(engine))
            {
                log.Add($"⚠ 选中的动作被内核拒绝：{next.DescribeName(state)} —— 提前收尾");
                break;
            }

            chosen.Add(next);
            state = engine.State;

            // ★ 选牌答复（`CS`）。
            //
            // 出牌**可能触发「开发」**（从 3 张候选里挑 1 张），
            // 而客户端要知道**选了哪张** —— 动作流里用一条 `CS` 表达
            // （`{0:触发选牌的卡, 1:候选下标, 2:选中卡码}`）。
            //
            // ⚠️ 没有这一步的后果（2026-10-02 从真回放查出来）：
            //    实测人类一局发了 3 条 `CS`，而 bot **一条都没有** ——
            //    因为 `AtomicAction` 原来只有出牌/攻击/移动/结束回合，产不出 `CS`。
            //    客户端于是收到一张"没有任何选择的开发牌"，
            //    表现成用户报的「AI 不会选开发」。
            //
            // 位置要紧：必须在**触发它的那条 `PC` 之后**，与人类回放一致
            // （人类 #11 PC → #12 是别的；#9 PC → #10 CS 紧跟）。
            // 位置要紧：必须在**触发它的那条 `PC` 之后**，与人类回放一致。
            if (next is PlayCardAction played
                && engine.LastPick is { } pick
                && pick.SelectingCardId == played.CardId)
            {
                pickedCards.Add(pick);
                engine.ClearLastPick();
            }

            // ★ 「从手牌挑一张」的答复（`HT` = `XActionHandTargetSelected`）。
            //
            // 与 `CS` 并列的**第二种选牌答复**：`CS` 是"从候选/牌库挑"，
            // `HT` 是"从手牌挑"（典型卡 `card_unit_gordon_highlanders`）。
            // 同样必须在**触发它的那条 `PC` 之后**发出。
            if (next is PlayCardAction handPlayed
                && engine.LastHandTarget is { } ht
                && ht.SelectingCardId == handPlayed.CardId)
            {
                handTargets.Add(ht);
                engine.ClearLastHandTarget();
            }
        }

        if (guard >= MaxActionsPerTurn)
        {
            log.Add($"⚠ 达到单回合动作上限 {MaxActionsPerTurn}，强制收尾");
        }

        // ---- 3) 转成服务端动作 ----
        //
        // ⚠️ `turn_number` 用**内核重建出来的** `state.Turn`，不要用 `snapshot.Turns` ——
        //    服务端那个计数与内核的回合号不一定是同一个（实测差很多：
        //    snapshot.Turns=15 而重建出来是回合 2）。客户端按动作里的 turn_number
        //    推进回合，填错会让它**静默跑偏**。
        int actionId = snapshot.NextActionId > 0 ? snapshot.NextActionId : 1;
        int turnNumber = state.Turn;
        var actions = new List<ServerAction>(chosen.Count + 1);

        // 出牌时收集到的「选牌答复」，按卡号排队（同一张卡可能连选两次，实测 206428 有）
        var pickQueue = new Dictionary<int, Queue<MatchEngine.PickRecord>>(chosen.Count);
        foreach (var pk in pickedCards)
        {
            if (!pickQueue.TryGetValue(pk.SelectingCardId, out var q))
            {
                pickQueue[pk.SelectingCardId] = q = new Queue<MatchEngine.PickRecord>();
            }

            q.Enqueue(pk);
        }

        // 「从手牌挑一张」的答复（`HT`），同样按卡号排队
        var handQueue = new Dictionary<int, Queue<MatchEngine.HandTargetRecord>>(chosen.Count);
        foreach (var ht in handTargets)
        {
            if (!handQueue.TryGetValue(ht.SelectingCardId, out var hq))
            {
                handQueue[ht.SelectingCardId] = hq = new Queue<MatchEngine.HandTargetRecord>();
            }

            hq.Enqueue(ht);
        }

        foreach (var a in chosen)
        {
            if (ToServerAction(a, actionId, turnNumber, engine.State) is { } sa)
            {
                actions.Add(sa);
                actionId++;

                // ★ `PC` 之后紧跟它的 `CS`（选牌答复）。人类回放就是这个顺序。
                if (a is PlayCardAction pc
                    && pickQueue.TryGetValue(pc.CardId, out var q)
                    && q.Count > 0)
                {
                    var pk = q.Dequeue();
                    actions.Add(new ServerAction(
                        actionId++,
                        "CS",
                        _botPlayerId,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["0"] = pk.SelectingCardId.ToString(),
                            ["1"] = pk.Index.ToString(),
                            ["2"] = pk.Code,
                        },
                        turnNumber));

                    log.Add($"   ↳ 选牌答复 CS：{pk.ChosenName}（第 {pk.Index} 个候选，码 {pk.Code}）");
                }

                // ★ `PC` 之后紧跟它的 `HT`（「从手牌挑一张」的答复）。
                //   线格式实测 `#156 HT {"0":"10","1":"7"}`：0 = 挑牌的卡，1 = 选中的手牌。
                if (a is PlayCardAction hpc
                    && handQueue.TryGetValue(hpc.CardId, out var hq)
                    && hq.Count > 0)
                {
                    var ht = hq.Dequeue();
                    actions.Add(new ServerAction(
                        actionId++,
                        "HT",
                        _botPlayerId,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["0"] = ht.SelectingCardId.ToString(),
                            ["1"] = ht.ChosenCardId.ToString(),
                        },
                        turnNumber));

                    log.Add($"   ↳ 手牌目标答复 HT：挑牌卡 #{ht.SelectingCardId} → 手牌 #{ht.ChosenCardId}");
                }
            }
        }

        // ★ 这条「回合结束」里那个 HQ 字段是「**行动方的对手** HQ」，不是自己的。
        //
        // ⚠️ 以前写的是 `state.HqDefense(_botSide)`（**自己**的 HQ），是错的。
        //    证据（全部来自真实回放）：
        //      · 人类（左）自己的 `XActionStartOfTurn` 写的是**右方（bot）**的 HQ：
        //        542091 人类 #8 写 22，而当时 bot HQ 正是 22（人类 HQ 是 20）。
        //      · 反证「它是不是『永远填 bot 的 HQ』」：542091 人类 #53（t11 EndOfTurn）
        //        写 24，紧接着 #54 是服务端给 bot 的 `XActionStartOfTurn`；两者之间
        //        **没有任何动作**，而 #54 写 19。若两者都指 bot 的 HQ 就自相矛盾
        //        （24 → 19 无因变化）；只有「各指对手」才自洽（#53=bot HQ，#54=人类 HQ）。
        //      · `out/audit` 的 HQ 对拍：542091 的 9 条对不上里有 **7 条**是这条
        //        （#6/#14/#29/#39/#48/#57/#71），期望值正是**我们自己的 HQ**。
        actions.Add(new ServerAction(
            actionId,
            "XActionEndOfTurn",
            _botPlayerId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["side"] = _botSide.ToWire(),
                ["reason"] = "endTurnButton",
                [_HqKeyOf(state)] = state.HqDefense(_botSide.Opposite()).ToString(),
            },
            turnNumber));

        log.Add($"产出 {chosen.Count} 条操作 + 1 条结束回合");
        return new TurnResult(actions, state, log, unapplied, divergence, hqOpponentBefore);
    }

    /// <summary>
    /// 「HQ 防御」在动作流里的键。
    ///
    /// ⚠️ 它**不是固定值** —— 是**那一方 HQ 卡自己的 cardID**，每局不同
    /// （实测 replay-15→键 15、206428→键 28、634651→键 51、989040→键 40）。
    /// 老 bot 那套硬编码 `{"75":"20"}` 只在某一局碰巧对。
    ///
    /// 来源：每个 `XActionStartOfTurn` / `XActionEndOfTurn` 都带且只带这一个整数键
    /// （实测 266/266）。所以从动作流里现学即可。
    /// </summary>
    private string _hqKey = "40";

    private string _HqKeyOf(GameState state) => _hqKey;

    private void LearnHqKey(ServerMatchSnapshot snapshot)
    {
        foreach (var a in snapshot.Actions)
        {
            if (a.ActionType != "XActionStartOfTurn")
            {
                continue;
            }

            foreach (string k in a.ActionData.Keys)
            {
                if (int.TryParse(k, out _))
                {
                    _hqKey = k;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 「只结束回合」的兜底动作（重放没产出引擎 / 局面已结束 / 行动方不是 bot 时用）。
    ///
    /// ⚠️ HQ 那个字段填的是「**行动方的对手** HQ」，与 <see cref="DecideTurn"/> 里
    ///    最后那条 `XActionEndOfTurn` 同一口径（理由与证据见那处注释）。
    /// </summary>
    private IReadOnlyList<ServerAction> OnlyEndTurn(ServerMatchSnapshot s, GameState? state, int turn)
        => new[]
        {
            new ServerAction(
                s.NextActionId > 0 ? s.NextActionId : 1,
                "XActionEndOfTurn",
                _botPlayerId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["side"] = _botSide.ToWire(),
                    ["reason"] = "endTurnButton",
                    [_hqKey] = (state?.HqDefense(_botSide.Opposite()) ?? 20).ToString(),
                },
                turn),
        };

    /// <summary>
    /// 从重放报告里算出**漂开信号**（只认人类动作）。
    ///
    /// 两个信号（按可靠性排序）：
    ///
    /// **① 人类动作被内核拒绝** —— 最硬的信号，因为人类的动作是 ground truth。
    ///    实测（`out/audit`）：508065 有 13 条，542091 / 214436 都是 0。
    ///    典型形态就是用户报的那个：客户端发 `ML {"0":26,"1":0}`（把 #26 移到槽位 0），
    ///    而内核以为 #26 在弃牌堆 ⇒ `MoveUnit` 被拒（`ReplayRunner` 记成
    ///    「移动被拒（当前 Discard）」）。
    ///
    /// **② HQ 校验和对不上** —— 客户端在**每条动作**的 `action_data` 里都带一个
    ///    「行动方**对手**的 HQ 血量」（键 = 那一方 HQ 卡自己的 cardID，每局不同；
    ///    值 = 当时血量）。HQ 只在受伤/治疗时变，所以它对伤害结算、摧毁判定高度敏感。
    ///    实测 542091：人类 35 条动作里 32 条带这个字段（只有 `CS` 与 `ActionEndMatch` 不带），
    ///    健康局里**全部对得上** ⇒ 这条信号不误报。
    ///    ⚠️ 所以这里**不限于回合边界动作** —— 边界动作只是"一定有"，
    ///    而 PC/AC/ML 也带，比对点更多、能更早发现漂开。
    ///
    /// ⚠️ 方向：动作里的值是**行动方的对手** HQ。所以对人类（左）的动作，
    ///    它是**右方（bot）**的 HQ。`ReplayRunner` 已经算好了：
    ///    `StepResult.ExpectedHq` = 动作里写的值，`ActualHq` = 我们重建出来的值。
    ///
    /// ⚠️ 不用「回合号对不上」当信号：`state.Turn`（内核回合）与 `match.Turns`
    ///    （服务端回合）**本来就不同口径**（实测 `snapshot.Turns=15` 而重建出来是回合 2），
    ///    换算关系没查清，拿来当闸门会误杀。
    /// </summary>
    private static DivergenceSignals InspectDivergence(ReplayRunner.Report? report, Side humanSide)
    {
        if (report is null)
        {
            return DivergenceSignals.None;
        }

        string human = humanSide.ToWire();
        int unapplied = 0, hqBad = 0;
        string? firstUnapplied = null, firstHq = null;
        int hqClient = -1, hqOur = -1;

        foreach (var s in report.Steps)
        {
            if (!string.Equals(s.PlayerSide, human, StringComparison.Ordinal))
            {
                continue;      // bot 自己的历史动作是旧内核产的，被拒属正常 —— 不算信号
            }

            // `Duplicate`（重复记录）不算失败：那条动作的效果**已经**落实过。
            // 口径与 `ReplayRunner.Report.NotUnderstood` / `audit-all-replays.ps1` 一致。
            if (!s.Applied && !s.Duplicate)
            {
                unapplied++;
                firstUnapplied ??= $"#{s.ActionId} t{s.Turn} {s.ActionType} —— {s.Failure}";
            }

            // `HqMatches` 在「动作里没这个字段」或「我们这边没有那一方的 HQ 卡」时为 true，
            // 所以这里不必再判空。
            if (s.ExpectedHq > 0 && !s.HqMatches)
            {
                hqBad++;
                if (firstHq is null)
                {
                    hqClient = s.ExpectedHq;
                    hqOur = s.ActualHq;
                    firstHq = $"#{s.ActionId} t{s.Turn} {s.ActionType}";
                }
            }
        }

        return new DivergenceSignals(unapplied, hqBad, firstUnapplied, firstHq, hqClient, hqOur);
    }

    /// <summary>选一个动作。优先神经网络，失败则退回贪心。</summary>
    private AtomicAction? ChooseOne(MatchEngine engine, List<AtomicAction> chosen, List<string> log)
    {
        if (_nn is not null)
        {
            try
            {
                var diag = _diagnostics ??= new List<string>();
                diag.Clear();
                _nn.Diagnostics = diag;

                var d = _nn.Choose(engine, chosen, BuildReplayer(), chosen.Count,
                                   verifyEveryReplay: false);
                var st = engine.State;

                // ⚠️ 记录**候选全表**，不只记选中的那个。
                //    「AI 出了不该出的牌」这类问题，只有看到候选与分值才能判断
                //    是「枚举出了非法候选」还是「模型给错的分」。
                log.Add($"--- NN 决策 #{chosen.Count}（回合 {st.Turn}，kredit {st.Kredits(_botSide)}/{st.MaxKredits(_botSide)}，" +
                        $"手牌 {st.Hand(_botSide).Count}，枚举 {d.Enumerated}，合法 {d.LegalCount}，拒绝 {d.Rejected}）");
                foreach (var c in d.Candidates.OrderByDescending(c => c.WinProb).Take(8))
                {
                    bool isChosen = ReferenceEquals(c, d.Chosen);
                    log.Add($"      {(isChosen ? "✔" : " ")} {c.WinProb,7:P1}  {DescribeForLog(c.Action, st)}");
                }

                // 被拒的原因 —— 「手牌 9 张却一个动作都做不了」时必须看这个
                if (diag.Count > 0)
                {
                    log.Add($"      被拒原因（前 {diag.Count} 条）：");
                    foreach (string why in diag)
                    {
                        log.Add($"        ✗ {why}");
                    }
                }

                log.Add($"CHOSEN {DescribeForLog(d.Chosen.Action, st)}");
                return d.Chosen.Action;
            }
            catch (Exception ex)
            {
                log.Add($"⚠ 神经网络决策失败（{ex.GetType().Name}: {ex.Message}），本回合改用贪心");
            }
        }

        return ChooseGreedy(engine);
    }

    /// <summary>
    /// 把候选动作描述成**带卡名的可读串**（诊断用）。
    ///
    /// 为什么要卡名：出问题时最常问的是「它到底对哪张牌做了什么」，
    /// 光有 cardID 得回去查表。
    /// </summary>
    private static string DescribeForLog(AtomicAction a, GameState st)
    {
        string Name(int id) => st.ById(id) is { } c
            ? $"{c.Name}#{c.CardId}@{c.Location}#{c.LocationNumber}({c.Attack}/{c.Defense})"
            : $"#{id}";

        return a switch
        {
            PlayCardAction p => $"出牌 {Name(p.CardId)}" +
                                (p.TargetId != 0 ? $" → {Name(p.TargetId)}" : ""),
            AttackAction at => $"攻击 {Name(at.AttackerId)} → {Name(at.DefenderId)}",
            MoveAction m => $"移动 {Name(m.UnitId)} → 槽位 {m.Slot}",
            _ => a.DescribeName(st),
        };
    }

    /// <summary>
    /// **重建到「当前时刻」的唯一入口。**
    ///
    /// 主流程与试算副本都必须用它 —— 这是"不写第二套重建逻辑"的落点。
    /// 每次调用都返回一个**全新的、确定性的**引擎（同种子、同批卡、同串动作），
    /// 所以调用方拿到的是独立副本，可以随便改。
    /// </summary>
    private ReplayRunner.Report? RebuildEngine(ServerMatchSnapshot snapshot)
    {
        var replay = ServerReplayBridge.ToReplayData(snapshot, clientSide: _botSide.Opposite());
        var runner = new ReplayRunner(_db)
        {
            InitialLocations = BuildInitialLocations(snapshot),
            InitialLocationNumbers = BuildInitialLocationNumbers(snapshot),
        };

        var report = runner.Run(replay, verbose: false);

        // ★ 重放**做完之后**才装 bot 的选牌钩子（时机见 InstallBotPickHook 的注释）。
        if (report.Engine is { } eng)
        {
            InstallBotPickHook(eng);
        }

        return report;
    }

    /// <summary>
    /// **重放结束之后**给引擎装上「选牌钩子」—— bot 自己回合里的开发/占卜族靠它。
    ///
    /// ⚠️⚠️ 时机很关键：**必须等重放做完**。
    /// `ReplayRunner` 重建时会装 `PickCardToDraw`（答复来自**人类动作流**里的 `CS`），
    /// 那个钩子**不能**被覆盖 —— 否则人类的选牌会被 bot 的选择劫持，局面重建就错了。
    /// 所以这里装的是**另一个、优先级更高**的钩子（`ChooseSpawnCard`），
    /// 而且只在重放完成后挂上。
    ///
    /// 不装它的后果（2026-10-02 实测）：bot **自己回合**的动作流里没有 `CS`
    /// ⇒ `PickCardToDraw` 返回 null ⇒ `CardApiDispatch` 里 `return 0`
    /// ⇒ **开发牌什么都不做**。
    /// 真对局日志里 bot 打了 `card_event_baker_street_irregulars`
    /// （卡面 Develop a British special force unit），客户端**收不到任何 `CS`**，
    /// 选中的牌也没进手牌 —— 不是"选得不好"，是这张牌完全没生效。
    ///
    /// ## 现在怎么选
    ///
    /// 挑**费用最高**的那张。比"候选表第一张"稍好（开发池多是同族卡，
    /// 贵的一般更强），而且**确定**（不引入随机）。
    /// ⚠️ 仍然是**启发式**，不是学出来的 —— 下一步应让 NN 对候选逐个估值。
    /// </summary>
    private static void InstallBotPickHook(MatchEngine engine)
    {
        engine.ChooseSpawnCard = (selecting, candidates) =>
        {
            if (candidates.Count == 0)
            {
                return null;
            }

            var best = candidates[0];
            foreach (var c in candidates)
            {
                if (c.EffectiveKreditCost > best.EffectiveKreditCost)
                {
                    best = c;
                }
            }

            return best;
        };

        // ★ 同理：「从手牌挑一张」（`gordon_highlanders` 那类卡）。
        //
        // ⚠️ 不装它的后果与 `CS` 那处一样：bot 自己回合的动作流里没有 `HT`
        //    ⇒ `ReplayRunner` 装的 `PickHandTarget` 取不到答复 ⇒
        //    `selectTargetFromHand` 拿不到卡 ⇒ **整条效果不生效**
        //    （手牌既没被设成 0 费、也没回牌库）。
        engine.ChooseHandTarget = (selecting, candidates) =>
        {
            if (candidates.Count == 0)
            {
                return null;
            }

            // 挑**费用最高**的指令 —— "设成 0 费并放到牌库顶" 对贵牌收益最大，
            // 而且确定（不引入随机）。⚠️ 仍是启发式。
            var best = candidates[0];
            foreach (var c in candidates)
            {
                if (c.EffectiveKreditCost > best.EffectiveKreditCost)
                {
                    best = c;
                }
            }

            return best;
        };
    }

    private Replayer? _replayer;

    /// <summary>候选被拒原因的复用缓冲（每回合清空）。</summary>
    private List<string>? _diagnostics;

    /// <summary>本回合重建用的快照（副本重建要用它，见 <see cref="BuildReplayer"/>）。</summary>
    private ServerMatchSnapshot? _currentSnapshot;

    /// <summary>
    /// 建「试算副本」工厂。
    ///
    /// ⚠️⚠️ 这里**必须和主重建走同一条路径**，否则副本会是**另一局游戏**。
    ///
    /// `Replayer.Build` 原本是 `new MatchEngine(...) → Start() → 重放 journal`，
    /// 而 `Start()` 开的是**全新一局**、journal 里只有本回合几步 ——
    /// 服务器是**中途接管**的对局，前面几百条动作不在 journal 里。
    ///
    /// 实测症状：`tryEngine` 里 `#42 = card_location_london`，而真局面里
    /// `#42 = card_unit_panzer_ii_a` ⇒ 枚举 8~16 个候选**只有 1 个合法**
    /// （就是"结束回合"），AI 一张牌都打不出去、手牌从 6 涨到 9。
    /// 对照 `NNPlay`（journal 完整）：枚举 12 → 合法 12、**拒绝 0**。
    ///
    /// 所以 `Prelude` 直接复用 <see cref="RebuildEngine"/> —— **同一个方法**。
    /// </summary>
    private Replayer BuildReplayer()
        => _replayer ??= new Replayer(_db, Array.Empty<string>(), Array.Empty<string>(), 1)
        {
            Prelude = () => RebuildEngine(_currentSnapshot!)?.Engine
                            ?? new MatchEngine(_db, Array.Empty<string>(), Array.Empty<string>(), 1),
        };

    /// <summary>
    /// 贪心兜底：一次挑一个动作（打牌 → 攻击 → 移动）。
    ///
    /// 为什么不直接调 <c>GreedyBot.PlayTurn</c>：它内部会自己调 `EndTurn`，
    /// 而服务端需要「动作列表」和「结束回合」分开产出。
    /// </summary>
    private AtomicAction? ChooseGreedy(MatchEngine engine)
    {
        var state = engine.State;

        foreach (var card in state.Hand(_botSide).ToList())
        {
            if (!engine.CanPlay(card, out _))
            {
                continue;
            }

            bool needsTarget = card.Definition.ExternalCalls.Contains("GetTargetedCard", StringComparer.Ordinal);
            if (needsTarget)
            {
                // ★★ 目标必须过**客户端的两道门**（2026-10-02，见 `CardApi.CanTarget`）。
                // 旧实现取「敌方场上攻击力最高的单位」—— 对「只能指空军 / 老兵 / 友方」
                // 的牌就是硬塞非法目标，客户端静默不执行而我们这边落了地（虚空牌）。
                var legal = engine.LegalPlayTargets(card);
                if (legal.Count == 0)
                {
                    continue;   // 没有合法目标 ⇒ 这张牌打不了，看下一张
                }

                var t = legal.Where(x => x.Owner == _botSide.Opposite())
                             .OrderByDescending(u => u.Attack).FirstOrDefault()
                        ?? legal.OrderByDescending(u => u.Attack).First();

                return new PlayCardAction(card.CardId, t.CardId);
            }

            return new PlayCardAction(card.CardId, 0);
        }

        foreach (var unit in state.Board(_botSide).ToList())
        {
            if (!unit.CanOperateThisTurn(state))
            {
                continue;
            }

            var targets = engine.LegalTargets(unit).ToList();
            if (targets.Count == 0)
            {
                continue;
            }

            var kill = targets.Where(t => !t.IsHq && t.Defense <= unit.Attack)
                              .OrderByDescending(t => t.Attack)
                              .FirstOrDefault();
            return new AttackAction(unit.CardId, (kill ?? targets.FirstOrDefault(t => t.IsHq) ?? targets[0]).CardId);
        }

        foreach (var unit in state.Board(_botSide).ToList())
        {
            if (!unit.CanMoveThisTurn(state))
            {
                continue;
            }

            if (state.FrontlineOwner != Side.NotAvailable && state.FrontlineOwner != _botSide)
            {
                break;
            }

            if (unit.OperationCost > state.Kredits(_botSide))
            {
                continue;
            }

            for (int slot = 0; slot < state.FrontlineCapacity; slot++)
            {
                bool taken = state.Cards(Side.Left, CardLocation.BoardFrontline)
                    .Concat(state.Cards(Side.Right, CardLocation.BoardFrontline))
                    .Any(c => !c.IsHq && !ReferenceEquals(c, unit) && c.LocationNumber == slot);
                if (!taken)
                {
                    return new MoveAction(unit.CardId, slot);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 内核动作 → 服务端动作。**槽位语义是对着真实回放认出来的**，不是猜的
    /// （`out/audit/identify-action-slots.py`）：
    ///
    /// <code>
    /// PC  keys=[0,1,2,3,4,40]   {"0":"73","1":"0","2":"0","3":"0","4":"DT","40":"20"}
    /// ML  keys=[0,1,2,40]       {"0":"77","1":"0","2":"DR","40":"21"}
    /// </code>
    ///
    /// | 槽 | 含义 | 判据 |
    /// |---|---|---|
    /// | `0` | **打出的卡 / 行动的单位的 cardID** | 与该局 `starting_hand_*` 的 card_id 对得上 |
    /// | `1` | **目标 cardID**（无目标为 `0`） | 非零率随卡组而变；部署牌恒 0 |
    /// | `2` | 落点/位置相关 | ⚠️ **语义未定**，目前恒填 `0` |
    /// | `3` | 多选分支（`chooseOneIndex`） | 实测绝大多数为 0 |
    /// | `4` | **这张牌的卡组码**（2 字符） | 查 `deck_code_ids` 能反查到 `0` 槽那张牌 |
    /// | `40` | **对手 HQ 防御**（每局动态） | 全部 27/27 落在 HQ 值域，且随对局变化 |
    ///
    /// ⚠️ **那个 HQ 字段的「键」也必须填准**，不能写死 `40`。
    ///    `40` 是从**一局**回放（989040）里认出来的下标，而那个下标是**每局重新登记**的
    ///    （见 <see cref="_hqKey"/>）。实测三局：
    ///      · 542091：人类的 PC/AC/ML 全用 `91`，而我们的 PC/AC/ML 写 `40`（19 条）
    ///      · 214436：人类用 `36`，我们写 `40`（10 条）
    ///      · 508065：人类用 `65`，我们写 `40`（20 条）
    ///    也就是说**三局全部对不上**。客户端按自己的表解 `action_data`，
    ///    把值写到一个它没登记的（或**别的**）下标上，轻则被忽略、重则改错状态。
    ///    所以这里和回合边界动作一样，用学到的 `_hqKey`。
    ///
    /// ⚠️ **槽 `40` 的值也必须填准** —— 它是客户端判胜负/同步用的。填死值（老 bot 那套
    /// `{"75":"20"}`）在真实对局里就是错的：那个键是**那一方 HQ 卡自己的 cardID**，
    /// 每局不同，值也是实时血量而非恒 20。
    /// </summary>
    private ServerAction? ToServerAction(AtomicAction action, int actionId, int turn, GameState state)
    {
        int opponentHq = state.HqDefense(_botSide.Opposite());
        string hqKey = _HqKeyOf(state);
        Dictionary<string, string> data;
        string type;

        switch (action)
        {
            case PlayCardAction p:
                {
                    var card = state.ById(p.CardId);
                    type = "PC";
                    data = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["0"] = p.CardId.ToString(),
                        ["1"] = p.TargetId.ToString(),
                        // ★★ **目标必须同时写进 `2` 号槽**（2026-10-02）。
                        //
                        // 这个槽位表原先认反了。原注释写的是
                        //   「`1` = 目标 cardID（无目标为 0）；`2` = 落点/位置相关（语义未定）」
                        // 而**真实动作流里的分布正好相反**：
                        //
                        // 全部 5 局回放（`out/_server-replays/replay-*.actions.json`）
                        // 里**人类**发出的、带目标的 `PC` 共 **16 条**，逐条核对：
                        //   #  局号   槽1   槽2(目标)  卡组码 → 卡名（needsTarget）
                        //   29 214436  1      56      pO → card_event_desert_dust     ✔
                        //   43 214436  0      57      0m → card_event_monty           ✔
                        //   30 389594  3      54      0m → card_event_monty           ✔
                        //   42 389594  2      60      eF → card_event_fog_of_war      ✔
                        //   44 389594  0      66      oh → card_event_duress          ✔
                        //   73 389594  0      67      sQ → card_unit_queens_own       ✔
                        //   54 508065  3      9003    02 → card_event_aa_barrage      ✔
                        //   88 508065  1      47      0m → card_event_monty           ✔
                        //   51 542091  0      64      sQ → card_unit_queens_own       ✔
                        //   22 773639  1      64      Cv → card_event_blast          ✔
                        //   29 773639  1      60      eF → card_event_fog_of_war      ✔
                        //   51 773639  2      58      oh → card_event_duress          ✔
                        //   63 773639  2      66      0m → card_event_monty           ✔
                        //   82 773639  2      37      sD → card_event_chain_home      ✔
                        //  111 773639  3      26      Cv → card_event_blast          ✔
                        //  132 773639  3      13      tV → card_event_hms_formidable  ✔
                        // **16/16 都是「`2` 号槽 = 目标 cardID」，而且 16/16 那张牌的
                        // `external_calls` 里都含 `GetTargetedCard`**（= 确实是需要目标的牌）。
                        // 反方向也干净：人类 137 条 `PC` 里，**没有一条**是「槽1 是 cardID
                        // 而槽2 是 0」——槽1 的取值恒在 **0..4**（38×0 / 35×1 / 19×3 /
                        // 18×2 / 12×4），那是"落点/位置"量级，不可能是卡 ID
                        //（对照：目标卡 ID 是 56 / 57 / 64 / 9003 这种量级）。
                        //
                        // 也就是说**旧注释把两个槽的语义写反了**，于是 `ToServerAction`
                        // 把目标写进 `1`、把 `0` 写进 `2` ⇒ 客户端从 `2` 读到的恒为 0
                        // ⇒ **bot 打出的每一张需要目标的牌，客户端都收到"没有目标"**
                        // ⇒ 卡照打（记牌器 +1）但目标相关效果不发生 = 玩家报告的「虚空」。
                        // 实测：全部 58 条 bot `PC` 的槽2 都是 `0`，槽1 才是目标
                        //（例 508065 #17 槽1=1/槽2=0 打 pR=breakout；#133 槽1=26/槽2=0
                        //  打 0z=the_commonwealth）。
                        //
                        // ⚠️ 这里**只补 `2`、不动 `1`**：槽1 的真实语义仍未定
                        //    （人类恒在 0..4，但 bot 的落点也一直能出来 —— 终局快照里
                        //     bot 的单位分布在不同槽位 `#1/#2/#3`，说明客户端**不看**这个槽
                        //     决定落点）。把一个"已知写错但客户端容忍"的值改成另一个猜的值，
                        //     风险大于收益；补上目标槽是**纯增益、零回归面**。
                        ["2"] = p.TargetId.ToString(),
                        ["3"] = "0",          // chooseOneIndex
                        ["4"] = DeckCodeOf(card?.Name),
                        [hqKey] = opponentHq.ToString(),
                    };
                    break;
                }

            case AttackAction a:
                {
                    var attacker = state.ById(a.AttackerId);
                    type = "AC";
                    data = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["0"] = a.AttackerId.ToString(),
                        ["1"] = a.DefenderId.ToString(),
                        ["2"] = "0",
                        ["4"] = DeckCodeOf(attacker?.Name),
                        [hqKey] = opponentHq.ToString(),
                    };
                    break;
                }

            case MoveAction m:
                {
                    var unit = state.ById(m.UnitId);
                    type = "ML";
                    data = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["0"] = m.UnitId.ToString(),
                        ["1"] = m.Slot.ToString(),
                        ["2"] = DeckCodeOf(unit?.Name),
                        [hqKey] = opponentHq.ToString(),
                    };
                    break;
                }

            default:
                return null;
        }

        return new ServerAction(actionId, type, _botPlayerId, data, turn);
    }

    /// <summary>
    /// 卡名 → 2 字符卡组码。
    ///
    /// 由调用方在启动时喂入 `<c>deck_code_ids.json</c>`（码 → 卡名），
    /// 这里做反向映射。
    ///
    /// ⚠️ **同一个卡名可能对应多个码**（基础卡与 `_bal`/`_vet` 变体共用卡名）——
    /// 实测 57 条里有 10 条按卡名反查会落空或歧义。这里挑**最短的那个码**，
    /// 因为实测里基础卡的码通常是 2 字符的紧凑形式。
    /// 反查不到就返回 `"0"`：客户端对未知码的容忍度**未验证**，
    /// 但留空比猜一个错的码更安全。
    /// </summary>
    private string DeckCodeOf(string? cardName)
    {
        if (string.IsNullOrEmpty(cardName))
        {
            return "0";
        }

        return _nameToCode.TryGetValue(cardName, out string? code) ? code : "0";
    }

    private readonly Dictionary<string, string> _nameToCode = new(StringComparer.Ordinal);

    /// <summary>反向索引里有多少条（诊断用）。</summary>
    public int DeckCodeCount => _nameToCode.Count;

    /// <summary>喂入「码 → 卡名」表，建立反向索引。启动时调一次即可。</summary>
    public void LoadDeckCodeTable(IEnumerable<KeyValuePair<string, string>> codeToName)
    {
        foreach (var (code, name) in codeToName)
        {
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (!_nameToCode.TryGetValue(name, out string? existing) || code.Length < existing.Length)
            {
                _nameToCode[name] = code;
            }
        }
    }

    private static Dictionary<int, CardLocation> BuildInitialLocations(ServerMatchSnapshot s)
    {
        var map = new Dictionary<int, CardLocation>();
        foreach (var c in s.Cards)
        {
            if (ServerReplayBridge.TryLocation(c.Location, out var loc, out _)
                && loc is CardLocation.HandLeft or CardLocation.HandRight
                       or CardLocation.DeckLeft or CardLocation.DeckRight)
            {
                map[c.CardId] = loc;
            }
        }

        return map;
    }

    private static Dictionary<int, int> BuildInitialLocationNumbers(ServerMatchSnapshot s)
    {
        var map = new Dictionary<int, int>();
        foreach (var c in s.Cards)
        {
            if (ServerReplayBridge.TryLocation(c.Location, out var loc, out _)
                && loc is CardLocation.HandLeft or CardLocation.HandRight
                       or CardLocation.DeckLeft or CardLocation.DeckRight)
            {
                map[c.CardId] = c.LocationNumber;
            }
        }

        return map;
    }
}
