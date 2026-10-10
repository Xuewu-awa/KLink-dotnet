using KLink.Bot.Cards;

namespace KLink.Bot.Engine;

/// <summary>
/// 一局对局的完整状态。**所有随机都走 <see cref="Random"/>**，保证同种子同结果。
///
/// 这个模型是照着两条证据建的：
/// 1. 协议里的卡对象字段（card_id / location / location_number / name / is_gold）
/// 2. <c>BP_OnlineMatch_C</c> 的 CDO 属性表（见 docs/对局协议参考.md §7）
/// </summary>
public sealed class GameState
{
    public sealed class GameplayRestriction
    {
        public required Side Side { get; init; }
        public required GameplayRestrictionType Type { get; init; }
        public required int SourceCardId { get; init; }
        public int TurnsRemaining { get; set; }
    }

    public sealed class GameplaySideEffect
    {
        public required Side Side { get; init; }
        public required string Tag { get; init; }
        public required int SourceCardId { get; init; }
        public required int DurationPolicy { get; set; }
        public int TurnsRemaining { get; set; }
        public int EffectValue { get; init; }
    }

    /// <summary>左/右两侧，索引 1/2（与 <see cref="Side"/> 对齐，0 位弃用）。</summary>
    private readonly List<CardInstance>[] _cardsBySide = { new(), new(), new() };

    private readonly Dictionary<int, CardInstance> _byCardId = new();

    public GameState(CardDatabase database, ulong seed)
    {
        Database = database;
        // ★★ 对局路径的随机流 = **客户端 `cardsRandomStream` 的逐位复刻**
        //    （UE `FRandomStream`，用 `match_id` 播种一次）。
        //    以前这里是内核自己的 splitmix64（`DeterministicRandom`），
        //    与客户端毫无关系 ⇒ 随机/复制类效果抽到的卡必然不同
        //    ⇒ 内核持有一张客户端没有的卡（"虚空单位"）。
        //    出处与验证见 `UeRandomStream` 的类注释。
        Random = new UeRandomStream(seed);
        Seed = seed;
    }

    public CardDatabase Database { get; }

    /// <summary>
    /// 本局的确定性随机流 —— **客户端那条 `cardsRandomStream` 的复刻**。
    /// 播种值就是 `match_id`（见 <see cref="UeRandomStream"/>）。
    /// </summary>
    public UeRandomStream Random { get; }
    public ulong Seed { get; }

    // BP_GameState_Battle stores signed updates and returns Abs_Int on query.
    private readonly int[] _kreditSlotsLost = new int[3];
    public int KreditSlotsLost(Side side) => Math.Abs(_kreditSlotsLost[(int)side]);
    public void RecordKreditSlotLoss(Side side) => _kreditSlotsLost[(int)side]--;

    /// <summary>
    /// Active global restrictions from `FGameplayRestrictionEffect`.
    /// A restriction is keyed by affected side, type, and source card ID;
    /// multiple sources of the same type may coexist.
    /// </summary>
    public List<GameplayRestriction> GameplayRestrictions { get; } = new();

    public List<GameplaySideEffect> GameplaySideEffects { get; } = new();

    public bool HasGameplaySideEffect(Side side, string tag)
        => GameplaySideEffects.Any(x => x.Side == side
            && string.Equals(x.Tag, tag, StringComparison.Ordinal));

    public void ApplyGameplaySideEffect(Side side, string tag, int sourceCardId,
        int durationPolicy, int turnsRemaining, int effectValue = 0)
    {
        var existing = GameplaySideEffects.FirstOrDefault(x => x.Side == side
            && x.SourceCardId == sourceCardId
            && string.Equals(x.Tag, tag, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.DurationPolicy = durationPolicy;
            existing.TurnsRemaining = turnsRemaining;
            return;
        }

        GameplaySideEffects.Add(new GameplaySideEffect
        {
            Side = side,
            Tag = tag,
            SourceCardId = sourceCardId,
            DurationPolicy = durationPolicy,
            TurnsRemaining = turnsRemaining,
            EffectValue = effectValue,
        });
    }

    public void RemoveGameplaySideEffect(Side side, string tag, int sourceCardId)
        => GameplaySideEffects.RemoveAll(x => x.Side == side && x.SourceCardId == sourceCardId
            && string.Equals(x.Tag, tag, StringComparison.Ordinal));

    /// <summary>
    /// 卡牌蓝图的延迟触发队列（<c>AddToTriggerQueue</c>）。
    ///
    /// 这是对局状态而不是派发器状态：卡牌效果可以先把卡加入队列，
    /// 在一次手牌选择链结束后由 <c>ResolveTriggerQueue</c> 按 FIFO 执行。
    /// </summary>
    public List<CardInstance> TriggerQueue { get; } = new();

    public bool HasGameplayRestriction(Side side, GameplayRestrictionType type)
        => GameplayRestrictions.Any(x => x.Side == side && x.Type == type && x.TurnsRemaining != 0);

    public void AddGameplayRestriction(Side side, GameplayRestrictionType type, int sourceCardId, int turns)
    {
        var existing = GameplayRestrictions.FirstOrDefault(x =>
            x.Side == side && x.Type == type && x.SourceCardId == sourceCardId);
        if (existing is not null)
        {
            existing.TurnsRemaining = Math.Max(existing.TurnsRemaining, turns);
            return;
        }

        GameplayRestrictions.Add(new GameplayRestriction
        {
            Side = side,
            Type = type,
            SourceCardId = sourceCardId,
            TurnsRemaining = turns,
        });
    }

    public void RemoveGameplayRestriction(Side side, GameplayRestrictionType type,
        int sourceCardId, bool removeAll)
    {
        GameplayRestrictions.RemoveAll(x => x.Side == side && x.Type == type
            && (removeAll || x.SourceCardId == sourceCardId));
    }

    /// <summary>Called at the start of a global turn, matching DecrementTurnGameplayRestrictions.</summary>
    public void DecrementGameplayRestrictions()
    {
        foreach (var restriction in GameplayRestrictions)
        {
            if (restriction.TurnsRemaining > 0)
            {
                restriction.TurnsRemaining--;
            }
        }

        GameplayRestrictions.RemoveAll(x => x.TurnsRemaining == 0);
    }

    public int Turn { get; set; } = 1;
    public Side ActiveSide { get; set; } = Side.Left;
    public Side StartingSide { get; set; } = Side.Left;

    /// <summary>Blueprint `DestroyedCardIDsByTurnNumber`, indexed by battle turn.</summary>
    public Dictionary<int, List<int>> DestroyedCardIdsByTurn { get; } = new();

    /// <summary>Blueprint `UnitDestroyedThisTurn`.</summary>
    public bool UnitDestroyedThisTurn { get; private set; }

    public IReadOnlyList<int> DestroyedCardsIdsByTurn(int turn)
        => DestroyedCardIdsByTurn.TryGetValue(turn, out var ids)
            ? ids
            : Array.Empty<int>();

    public void RecordDestroyedCard(int cardId, bool isUnit)
    {
        if (!DestroyedCardIdsByTurn.TryGetValue(Turn, out var ids))
        {
            ids = new List<int>();
            DestroyedCardIdsByTurn[Turn] = ids;
        }

        if (!ids.Contains(cardId))
        {
            ids.Add(cardId);
        }

        if (isUnit)
        {
            UnitDestroyedThisTurn = true;
        }
    }

    public void ResetDestroyedThisTurn() => UnitDestroyedThisTurn = false;

    public int Kredits(Side s) => _kredits[(int)s];
    public int MaxKredits(Side s) => _maxKredits[(int)s];
    private readonly int[] _kredits = new int[3];
    private readonly int[] _maxKredits = new int[3];

    // Blueprint BP_GameState_Battle keeps these counters outside the card
    // objects: HQ damage is tracked per side, while operation spend is one
    // match-wide value for the active turn.
    private readonly int[] _hqDamagedThisTurn = new int[3];
    public int OperationKreditsSpentThisTurn { get; private set; }

    public int GetHQDamagedAmountThisTurn(Side side)
        => side is Side.Left or Side.Right ? _hqDamagedThisTurn[(int)side] : 0;

    public void UpdateHQDamagedAmountThisTurn(Side side, int amount)
    {
        if (side is Side.Left or Side.Right)
        {
            _hqDamagedThisTurn[(int)side] += amount;
        }
    }

    public void AddOperationKreditsSpentThisTurn(int amount)
        => OperationKreditsSpentThisTurn += amount;

    public void ResetTurnGameplayCounters()
    {
        Array.Clear(_hqDamagedThisTurn);
        OperationKreditsSpentThisTurn = 0;
    }

    /// <summary>疲劳计数（牌库空后每次抽牌递增）。</summary>
    private readonly int[] _fatigue = new int[3];
    public int Fatigue(Side s) => _fatigue[(int)s];

    public void AddKredits(Side s, int amount) => _kredits[(int)s] = Math.Max(0, _kredits[(int)s] + amount);
    public void SetKredits(Side s, int value) => _kredits[(int)s] = Math.Max(0, value);
    public void AddMaxKredits(Side s, int amount) => _maxKredits[(int)s] = Math.Max(0, _maxKredits[(int)s] + amount);
    public void SetMaxKredits(Side s, int value) => _maxKredits[(int)s] = Math.Max(0, value);
    public void SetFatigue(Side s, int value) => _fatigue[(int)s] = Math.Max(0, value);

    /// <summary>前线归属。NotAvailable = 无人控制。</summary>
    public Side FrontlineOwner { get; set; } = Side.NotAvailable;

    /// <summary>
    /// `stopFurtherActions` —— `BP_GameState_Battle` 的一个 **bool 成员**
    /// （读写各一行：`_deps/BP_GameState_Battle.g.cs:3611` 写、`:2578` 读）。
    ///
    /// ## 谁写谁读（蓝图全量）
    ///
    /// 写：`SetStopFurtherActions(GameStateRef, bool)`。三个写入方：
    /// <list type="bullet">
    /// <item>`AfterWaitCardPlayFromHand` `:624` 开头的 `False`（**每次动作前复位**）；</item>
    /// <item>`GotchaTriggered` `:23859` 的 `True` —— 「反制卡触发 ⇒ 后面的卡动作全部作废」；</item>
    /// <item>`GotchaTriggered` `:23902` 的 `False`（见下方 ⚠️）。</item>
    /// </list>
    /// 读：`GetStopFurtherActions()`，只在卡牌打出链里当**提前退出**的门用
    /// （`BP_CardFunctions.g.cs:684/5887/6316/14937/15673/16567`）。
    ///
    /// ## ⚠️ 内核只建模**状态**，没有建模**消费者**
    ///
    /// 所有读取点都在 `AfterWaitCardPlayFromHand` / `CardPlayedFromHand` /
    /// `PlayCardFromHand` 这一族**库函数**里，而它们**不在 `card-ir.json` 的调用点集合里**
    /// （IR 只含每张卡自己的事件体）。⇒ 本内核设置这个标志后**没有代码读它**，
    /// 效果是惰性的。如实记录：**这是"状态已就位、消费方未接线"**，
    /// 不是"已经实现了 stopFurtherActions 的语义"。
    /// </summary>
    public bool StopFurtherActions { get; set; }

    /// <summary>
    /// 前线**限制者**卡 ID 的集合（对应 `BP_GameState_Battle.FrontlineLimiters`
    /// 与 `ZActionUpdateFrontlineLimiter`）。
    ///
    /// ⚠️ 这**不是**一个容量数字。原先这里是 `int FrontlineLimiter = 1` ——
    /// 类型和默认值都错：蓝图里判的是**集合非空**
    /// （`IsFrontlineLimited` i=0 `Set_IsNotEmpty(self.FrontlineLimiters)` → i=51 出参），
    /// 初值是**空集**；`UpdateFrontlineLimiter(Limiter, Remove)` 只是
    /// `Set_Add` / `Set_Remove` 一个卡 ID（i=79 / i=14）。
    ///
    /// 全卡池**只有 `card_unit_black_prince`** 会改它：
    /// `OnEnterPlay` 加入、`OnAfterLeaveBoard` / `OnSuppressed` 移出。
    /// 卡面："Only 2 units can occupy the frontline."
    /// </summary>
    public HashSet<int> FrontlineLimiters { get; } = new();

    /// <summary>
    /// 前线是否被限制 —— 判据就是「限制者集合非空」，对应 `IsFrontlineLimited`。
    /// </summary>
    public bool IsFrontlineLimited => FrontlineLimiters.Count > 0;

    /// <summary>
    /// 前线容量：默认 **5**，被限制时 **2**。
    ///
    /// 来源：`BP_GameState_Battle::FetchCardsByLocation` case 7 →
    /// `MaxQty = SelectInt(2, 5, IsFrontlineLimited)`（`SelectInt(A,B,bPickA) = bPickA ? A : B`）。
    /// </summary>
    public int FrontlineCapacity => IsFrontlineLimited ? LimitedFrontlineCapacity : DefaultFrontlineCapacity;

    /// <summary>前线默认容量（`FetchCardsByLocation` case 7 的 `IntConst(5)`）。</summary>
    public const int DefaultFrontlineCapacity = 5;

    /// <summary>被限制时的前线容量（`card_unit_black_prince` 在场所致）。</summary>
    public const int LimitedFrontlineCapacity = 2;

    /// <summary>
    /// 半场（支援线）容量：**5**，而且 **HQ 占其中 1 格 ⇒ 每边最多 4 个单位**。
    ///
    /// 来源：`FetchCardsByLocation` case 5,6 → `MaxQty = IntConst(5)`；
    /// 且那条计数循环只按 `location` 过滤（i=801）、**不排除 HQ**，
    /// 而 HQ 的 location 就是 5/6。快照实测 `loc=5 HQ数=1 总卡数=5` 出现 13 次、
    /// `loc=6` 9 次（250 条快照）。
    /// </summary>
    public const int HalfBoardCapacity = 5;

    /// <summary>手牌上限（`FetchCardsByLocation` case 3,4 → `MaxQty = IntConst(9)`）。</summary>
    public const int HandCapacity = 9;

    public bool IsFinished { get; private set; }
    public Side Winner { get; private set; } = Side.NotAvailable;
    public string WinnerReason { get; set; } = "";

    /// <summary>
    /// 顺序号分配器（左侧从 2 起、右侧从 42 起）。
    ///
    /// ⚠️ **只用于「开局建牌库」这条自对弈路径**（`MatchEngine.BuildDeck`）。
    /// 效果生成的卡一律走 <see cref="NextCardId"/> 的**客户端规则**（见那边的长注释）。
    /// </summary>
    private readonly int[] _nextCardId = { 0, 2, 42 };

    /// <summary>
    /// **本回合已生成过几张卡** —— 对应客户端 `BP_GameState_Battle::cardsCreatedCountThisTurn`。
    ///
    /// ⚠️ 它是**全局一个计数器**，不是「每方一个」：客户端的分配器
    /// `GenerateNextCardID(turnNumber, out nextCardID)` **没有 side 参数**
    /// （`ref/kards-sim/KardsSim/Generated/_deps/BP_GameState_Battle.g.cs:1545-1560`）。
    /// </summary>
    private int _cardsCreatedThisTurn;

    /// <summary><see cref="_cardsCreatedThisTurn"/> 是按哪个回合号计的 —— 回合变了就归零。</summary>
    private int _createdCountTurn = int.MinValue;

    /// <summary>
    /// **覆盖「客户端发号用的回合号」**（默认 null = 用 <see cref="Turn"/>）。
    ///
    /// 只有回放路径需要它：`ReplayRunner` 的回合号与真实客户端的回合号会**漂开**
    /// （动作流里每个回合记了两条 `EndOfTurn`，见那边的注释；实测 214436 人类 t7 出牌时
    /// 内核已经是 `Turn=10`），于是按 <see cref="Turn"/> 发出来的号是 10002/10003
    /// 而不是客户端真正用的 7002/7003。回放器每个动作前把**动作流自带的
    /// `turn_number`**（= 客户端的回合号）塞进来，发号就与客户端逐位一致。
    ///
    /// ⚠️ 只影响发号，不影响 <see cref="Turn"/> 本身（kredit/压制/召唤失调那些口径
    /// 一概不动）—— 回合号漂移是**另一个独立问题**，不能靠改发号顺手糊过去。
    /// </summary>
    public int? ClientIdTurnOverride { get; set; }

    /// <summary>本局已结算过的动作（用于回放/对拍）。</summary>
    public List<GameAction> ActionLog { get; } = new();

    /// <summary>
    /// **本回合从手牌打出过的牌**（按打出顺序）。
    ///
    /// 对应客户端 `GetCardsPlayedThisTurn`。存在的理由是一个具体的卡：
    /// `card_unit_85_pioneer_company`（"The first order you play each turn costs 1 less"）
    /// 的私有函数 `anyOrderPlayedThisTurn` 就是遍历这个列表、
    /// 找有没有**己方**的 `IsOrder` 牌。
    ///
    /// ⚠️ 只在 <see cref="MatchEngine.StartTurn"/> 里清空 —— 客户端是「每回合重算」，
    ///    清空时机错了会让「第一张指令」判成「第二张」。
    /// </summary>
    public List<CardInstance> CardsPlayedThisTurn { get; } = new();

    /// <summary>
    /// **按回合分的「从手牌打出过哪些牌」历史**（键 = 回合号 = <see cref="Turn"/>）。
    ///
    /// 对应客户端的 `GameStateRef.cardsPlayedTurnMapped`。
    /// 存在理由是一个具体的族：`didPlayBritishInfantryLastTurn` ——
    /// **5 张卡**（`card_event_forward_observers` / `card_unit_baltimore_mk_iii` /
    /// `card_unit_defiant_mk_i` / `card_unit_the_polar_bears` / `card_unit_valentine_mk_ii`）
    /// 的私有函数都调它，而它调 `GetCardsPlayedFromHandLastTurn()`。
    ///
    /// 蓝图语义（逐字）：
    /// <code>
    /// GetCardsPlayedFromHandLastTurn()            // BP_CardFunctions.g.cs:20315
    ///   = getCardsPlayedFromHandByTurn(GetTurnNumber() - 1)
    /// getCardsPlayedFromHandByTurn(turn)          // _deps/BP_GameState_Battle.g.cs:1718
    ///   = Map_Find(cardsPlayedTurnMapped, turn).CardIDs      // ← 返回的是**卡 ID 列表**
    /// </code>
    /// ⚠️ 返回**卡 ID（int）**而不是卡实例 —— 所以卡自己的程序会拿 `GetCardFromID(元素)` 再解析。
    ///
    /// ⚠️ 只写不读历史的话这个字段没用；快照点在 <see cref="MatchEngine.StartTurn"/>
    ///    清空 <see cref="CardsPlayedThisTurn"/> **之前**（那边 `State.Turn` 已经 +1，
    ///    所以被清的那份属于 `Turn - 1`）。
    /// </summary>
    public Dictionary<int, List<CardInstance>> CardsPlayedFromHandByTurn { get; } = new();

    /// <summary>内核遇到但尚未实现的 API 调用（用于量化缺口，见 Effects/CardApi.cs）。</summary>
    public Dictionary<string, int> UnimplementedCalls { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// **RNG 游标探针** —— 每一次随机消费的流水账（`消费序号 | 调用点 | 参数 | 结果`）。
    ///
    /// 为什么要有它：随机流是一条**游标**，客户端与内核必须逐次对齐 ——
    /// 我们漏掉一个消费点（原语没实现）游标就**落后**，多消费一次就**超前**，
    /// 两种情况都会让后面所有随机取数错位。所以这份流水账是
    /// 「我们的执行路径与客户端是否一致」的直接证据（见 `UeRandomStream.ConsumedCount`）。
    ///
    /// 只在诊断路径上打开（`ReplayRunner.CollectRandomTrace`），避免自对弈热路径上的分配。
    /// </summary>
    public List<string> RandomTrace { get; } = new();

    /// <summary>是否记录 <see cref="RandomTrace"/>。</summary>
    public bool CollectRandomTrace { get; set; }

    // ==================== 「生成卡身份可信度」安全网 ====================
    //
    // 为什么需要它（真人玩家实测，2026-10-02）：
    //   我们发出一条 `PC {"0": X}`，而客户端那边**没有 X 这张卡**（或 X 是别的卡）
    //   ⇒ 记牌器 +1、场上什么都没有 = **虚空部署**。
    //   **虚空单位会立刻不同步**（客户端根本没有这个单位，之后所有攻击/移动全错位）。
    //
    // 判据：卡的 cardID 是**内核分配器**（`NextCardId`）发出来的 ⇒ 客户端未必认得；
    //       只有**动作流用卡组码确认过**它才可信。
    //
    // ⚠️ 只在有动作流可对账的路径上打开（`ReplayRunner` / 接服务器的重建路径）。
    //    自对弈没有动作流，打开会把 bot 自己生成的卡全锁死 —— 所以默认关。

    /// <summary>是否跟踪「生成卡身份可信度」（默认关，见上面那段说明）。</summary>
    public bool TrackGeneratedCardTrust { get; set; }

    /// <summary>由内核分配器（<see cref="NextCardId"/>）发号生成出来的卡。</summary>
    public HashSet<int> GeneratedCardIds { get; } = new();

    /// <summary>已被动作流（卡组码）确认过身份的生成卡。</summary>
    public HashSet<int> VerifiedGeneratedCardIds { get; } = new();

    /// <summary>这张卡的身份可信吗（不是生成卡 ⇒ 来自快照/动作流 ⇒ 可信）。</summary>
    public bool IsIdentityTrusted(CardInstance card)
        => !GeneratedCardIds.Contains(card.CardId) || VerifiedGeneratedCardIds.Contains(card.CardId);

    /// <summary>把这张生成卡标成「动作流已确认」。</summary>
    public void MarkIdentityVerified(int cardId) => VerifiedGeneratedCardIds.Add(cardId);

    /// <summary>记一笔随机消费（<see cref="CollectRandomTrace"/> 关掉时是空操作）。</summary>
    public void TraceRandom(string what)
    {
        if (CollectRandomTrace && RandomTrace.Count < 20000)
        {
            RandomTrace.Add($"#{Random.ConsumedCount} {what}");
        }
    }

    // ==================== 卡牌增删查 ====================

    public IReadOnlyList<CardInstance> AllCards => _byCardId.Values.OrderBy(c => c.CardId).ToList();

    /// <summary>
    /// 无序的卡牌枚举 —— 热路径专用（<see cref="AllCards"/> 每次都会排序+分配）。
    /// ⚠️ 只在「顺序不影响结果」的地方用（例如清理死亡单位）。
    /// </summary>
    public IEnumerable<CardInstance> CardsUnordered()
    {
        for (int s = 1; s <= 2; s++)
        {
            var list = _cardsBySide[s];
            for (int i = 0; i < list.Count; i++)
            {
                yield return list[i];
            }
        }
    }

    /// <summary>场上所有非 HQ 的卡（双方），无排序、无分配。</summary>
    public IEnumerable<CardInstance> BoardUnordered()
    {
        for (int s = 1; s <= 2; s++)
        {
            var list = _cardsBySide[s];
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c.Location.IsBoard() && !c.IsHq)
                {
                    yield return c;
                }
            }
        }
    }

    public CardInstance? ById(int cardId) => _byCardId.GetValueOrDefault(cardId);

    public CardInstance RequireById(int cardId) =>
        _byCardId.TryGetValue(cardId, out var c) ? c : throw new KeyNotFoundException($"cardID {cardId} 不存在");

    /// <summary>
    /// 某个阵营在某位置的卡，按 location_number 排序。
    ///
    /// ⚠️ **立即物化**，不是延迟查询。效果结算过程中会创建/销毁卡，
    /// 如果这里返回 LINQ 延迟序列，调用方一边遍历一边改状态就会抛
    /// "Collection was modified"。物化一份快照既安全，语义上也更接近
    /// 客户端「把动作排进队列再逐个结算」的做法。
    /// </summary>
    public List<CardInstance> Cards(Side side, CardLocation? location = null)
    {
        var list = _cardsBySide[(int)side];
        var result = new List<CardInstance>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (location is null || c.Location == location.Value)
            {
                result.Add(c);
            }
        }

        result.Sort(static (a, b) =>
        {
            int cmp = a.LocationNumber.CompareTo(b.LocationNumber);
            return cmp != 0 ? cmp : a.CardId.CompareTo(b.CardId);
        });
        return result;
    }

    public List<CardInstance> Deck(Side s) => Cards(s, s.DeckOf());
    public List<CardInstance> Hand(Side s) => Cards(s, s.HandOf());
    public List<CardInstance> Board(Side s) => Cards(s).FindAll(c => c.Location.IsBoard() && !c.IsHq);

    /// <summary>
    /// 该方**场上**的卡，按**进入战斗的顺序**（**不是** `LocationNumber` 顺序）。
    ///
    /// ★ 为什么需要它（2026-10-02，从蓝图定案）：
    /// 客户端所有「遍历场上」的函数都走 `GetAllCardInBattle()`
    ///（`ref/kards-sim/…/_deps/BP_GameState_Battle.g.cs:1571`），而它**只有一句**
    /// `Map_Values(self.AllCardsInBattle)` ⇒ 顺序 = **这个 `TMap` 的迭代顺序**。
    /// 而 `AddCardToAllCardsInBattle`（同文件 `:301-319`）是
    /// 「`cardID &gt; 0` 且 `Map_Find` 查不到 ⇒ `Map_Add`」——
    /// **全树 0 处 `Map_Remove`** ⇒ 该映射**只增不删**、永不出现 swap-remove 的洞
    /// ⇒ **`Map_Values` 的顺序就是插入顺序**（= 卡进入战斗的顺序）。
    ///
    /// ⚠️ `Board(s)` 是按 `LocationNumber` **排序**的（见 <see cref="Cards"/>），
    /// 与客户端的插入序**不是一回事**。而「随机挑一张」这类效果的候选集顺序
    /// 直接决定抽到谁（同一次消费、同一个下标，排列不同就取到不同的卡 ——
    /// 见 `CardApi.GetRandomCard` 的注释），所以**随机族必须用本方法**。
    /// </summary>
    public List<CardInstance> BoardInBattleOrder(Side s)
        => _cardsBySide[(int)s].FindAll(c => c.Location.IsBoard() && !c.IsHq);

    /// <summary>
    /// 该方**在战场上**的**全部**卡（**含 HQ**），按**进入战斗的顺序**。
    ///
    /// 客户端 `AllCardsInBattle` 装的是**所有进过战斗的卡**（含双方 HQ），
    /// 而 HQ 是**开局就创建**的 ⇒ 在「只增不删」的插入序里它排在**最前**。
    /// 所以「`unitsOnly=false`」的候选集**不能**把 HQ 追加到末尾
    ///（内核原先正是 `Board(side).Concat([HQ])`，把 HQ 放在最后一位）。
    /// 详见 <see cref="BoardInBattleOrder"/> 里那段蓝图依据。
    /// </summary>
    public List<CardInstance> BattleCardsInOrder(Side s)
        => _cardsBySide[(int)s].FindAll(c => c.Location.IsBoard());
    public List<CardInstance> Discard(Side s) => Cards(s, CardLocation.Discard);

    public CardInstance Hq(Side s) => _cardsBySide[(int)s].First(c => c.IsHq);

    public int HqDefense(Side s) => Hq(s).Defense;

    /// <summary>创建一张卡并加入牌库。用于开局发牌与效果生成的卡（SpawnCard 等）。</summary>
    /// <param name="sequentialId">
    /// true 时用**顺序号**（左 2../右 42..）而不是客户端的 `1000×回合+序号` 规则。
    /// ⚠️ **只有自对弈的开局建牌库（`MatchEngine.BuildDeck`）该传 true** ——
    /// 真实对局的开局牌号来自服务端快照，效果生成的卡必须走客户端规则
    /// （理由见 <see cref="NextCardId"/> 的长注释）。
    /// </param>
    public CardInstance Create(string cardName, Side owner, CardLocation location, int locationNumber,
                               bool isGold = false, bool sequentialId = false,
                               bool isSalvaged = false, string? salvageFaction = null,
                               int salvagedCardId = 0)
    {
        CardDefinition def = Database.Require(cardName);
        var card = new CardInstance
        {
            // ⚠️ 这里原来是 `_nextCardId[(int)owner]++` —— **没有冲突检查**，
            //    而下面 `_byCardId[card.CardId] = card` 是**覆盖**语义（不是 Add）。
            //    于是**对局中途生成**的卡会顶掉同号的既有卡：
            //      左侧号段从 2 起，40 张起手（HQ + 39 张牌库）⇒ 下一个号正好是 **42**，
            //      而 42 是**右侧 HQ** 的号（右侧号段从 42 起）。
            //    实测（`NNPlay --dump-journal`，seed 12345、NN 坐 right）：
            //      `ZActionSpawnCard cardID=42`（royal_research 生成 RADAR 到左手牌）
            //      → `_byCardId[42]` 变成 RADAR，右侧 HQ 从按号索引里消失。
            //    后果有三层：
            //      ① 按号取卡的路径读到**另一张卡**（`ById(42)` = RADAR 而不是右 HQ）；
            //      ② 动作流水账里的攻击目标 `defenderCardID=42` 重放时解析成 RADAR，
            //         RADAR 不在场上 ⇒ `Attack` 拒绝 ⇒ Replayer **静默丢掉这一步**
            //         （`a.Apply(e)` 的返回值被忽略）⇒ 少扣 1 点 kredit ⇒ 指纹不一致 ⇒ NNPlay 中止；
            //      ③ 枚举 `AllCards`（= `_byCardId.Values`）的效果（`GetAllCards`、
            //         `checkAndUpdateBuffOnAllCards`）与 `SnapshotJson` 都会看到错的卡集。
            //
            //    修法：走**全局唯一**的分配器 `NextCardId(owner)`（它会跳过已被任何一方占用的号）。
            //    没有冲突时行为与 `_nextCardId[owner]++` **逐位相同**（开局 40 张牌全走这条路，
            //    号段仍是左侧 2..41 / 右侧 42..81），只在**本来就会撞号**时才改变结果。
            //    ⚠️ 这不改任何规则判据，只保证「一张卡一个号」这个不变量。
            CardId = sequentialId ? NextSequentialCardId(owner) : NextCardId(owner),
            Name = cardName,
            Owner = owner,
            OriginalOwner = owner,
            Definition = def,
            IsGold = isGold,
            IsSalvaged = isSalvaged,
            SalvageFaction = salvageFaction,
            SalvagedCardId = salvagedCardId,
            Location = location,
            LocationNumber = locationNumber,
            Attack = def.Attack,
            Defense = def.Defense,
            MaxDefense = def.Defense,
            KreditCost = def.Kredits,
            OperationCost = def.OperationCost,
        };

        _byCardId[card.CardId] = card;
        _cardsBySide[(int)owner].Add(card);

        // 先灌卡面自带的关键字（Blitz/Guard/…），再算派生数值。
        // 别再手工赋 KreditCost/OperationCost —— 统一走 RecalculateStats。
        card.InitializeFromDefinition();

        // 「卡对象被创建」事件 —— 对应蓝图 `BP_CardFunctions::CreateCardObject` 的
        // i=2610 `CallFunc_SpawnObject_ReturnValue.OnCreateCard()`。
        // 签名 `BaseCardObject.h:703 void OnCreateCard();`（无参、只有"自己"这一路）。
        CardCreated?.Invoke(card);

        // 游标探针：把"效果生成的卡"也记进流水账 —— 才能把
        // 「客户端说 cardID X 是卡 A」与「内核在什么时候、按什么顺序生成了 X」对上。
        TraceRandom($"CREATE #{card.CardId} {cardName} @{location} owner={owner} 回合={Turn}");

        // 安全网：记下"这是内核自己发号生成的卡"（客户端未必认得这个号）。
        if (TrackGeneratedCardTrust)
        {
            GeneratedCardIds.Add(card.CardId);
        }

        return card;
    }

    /// <summary>
    /// 用**指定的 cardID** 创建一张卡。
    ///
    /// 正常对局用 <see cref="Create"/> 顺序分配即可；重放真实对局时必须用这个 ——
    /// 客户端在开局快照里已经把每张卡的 cardID 定死了（左侧 1..40，右侧 41..80），
    /// 后续动作全是按这些 ID 引用的。
    /// </summary>
    public CardInstance CreateWithId(string cardName, Side owner, int cardId, CardLocation location,
                                     int locationNumber, bool isGold = false,
                                     bool isSalvaged = false, string? salvageFaction = null,
                                     int salvagedCardId = 0)
    {
        if (_byCardId.ContainsKey(cardId))
        {
            throw new InvalidOperationException($"cardID {cardId} 已被占用");
        }

        CardDefinition def = Database.Require(cardName);
        var card = new CardInstance
        {
            CardId = cardId,
            Name = cardName,
            Owner = owner,
            OriginalOwner = owner,
            Definition = def,
            IsGold = isGold,
            IsSalvaged = isSalvaged,
            SalvageFaction = salvageFaction,
            SalvagedCardId = salvagedCardId,
            Location = location,
            LocationNumber = locationNumber,
            Attack = def.Attack,
            Defense = def.Defense,
            MaxDefense = def.Defense,
            KreditCost = def.Kredits,
            OperationCost = def.OperationCost,
        };

        _byCardId[cardId] = card;
        _cardsBySide[(int)owner].Add(card);

        // 同上：灌卡面关键字 + 派生数值统一重算
        card.InitializeFromDefinition();

        // 让顺序分配器跳过已用的 ID
        while (_byCardId.ContainsKey(_nextCardId[(int)owner]))
        {
            _nextCardId[(int)owner]++;
        }

        // 同上：`CreateCardObject` 的 `OnCreateCard`（回放路径也走这个入口）。
        CardCreated?.Invoke(card);
        return card;
    }

    /// <summary>
    /// **效果生成卡的发号** —— 与客户端**逐位一致**的那条规则（`GenerateNextCardID`）。
    ///
    /// ## 权威依据（蓝图级，不是推断）
    ///
    /// `BP_CardFunctions::CreateCard` 在建卡时调
    /// `GameStateRef.GenerateNextCardID(turnNumber, out nextCardID)`
    /// （`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:10542-10544`），而它的实现是
    /// （`ref/kards-sim/KardsSim/Generated/_deps/BP_GameState_Battle.g.cs:1545` 与 `:1788`）：
    /// <code>
    /// GenerateNextCardID(turnNumber):
    ///     IncrementCardsCreatedThisTurn()            // cardsCreatedCountThisTurn++ —— **全局一个计数器**
    ///     GetCurrentCardID(turnNumber, out id):
    ///         mult = SelectInt(500, 1000, turnNumber == 0)   // SelectInt(A,B,pickA) = pickA ? A : B
    ///         id   = cardsCreatedCountThisTurn + turnNumber * mult
    /// </code>
    /// 也就是 <c>id = 回合号 × 1000 + 本回合已生成数</c>（回合号 0 时乘 500），
    /// 序号**从 1 起**（先自增再算号），**每回合归零**
    /// （`ResetCardsCreatedThisTurn`，`BP_GameState_Battle.g.cs:3188`）。
    ///
    /// ## ⚠️ 两个以前搞错的点（这就是「虚空部署」的根因）
    ///
    /// 1. **分配器没有 side 参数** ⇒ 规则对**双方一致**。
    ///    旧实现只让 `ClientIdSide`（人类）那一方走客户端号段，我们 bot 自己走顺序号
    ///    （81/82/83…）。但客户端在本地执行**我们打出的效果**时，也会用**它自己的**
    ///    `GenerateNextCardID` 给那张卡编号 ⇒ 我们发 `PC {"0": 81}` 时客户端**认不出 81**
    ///    ⇒ 记牌器 +1、场上什么都没有（真人玩家实测的「虚空部署」，对局 458321 的
    ///    `card_unit_1st_airborne#81/#82`）。
    /// 2. **计数器是全局的、不是每方一个** ⇒ 同一回合内双方生成的卡共用一个序号。
    ///
    /// 旧注释里「右方 KLink 生成的是 81/83/84」那条实测**是循环论证**：
    /// 那些号出自**我们自己的动作流**（旧内核发的），只能证明旧内核用了顺序号，
    /// 不能证明客户端接受了它们。反向证据：4 局回放里**人类动作引用的所有 >80 的 cardID
    /// 全部是 `1000×回合+序号` 形状**（508065 的 7001/7002/9001..9004/15001/19001..19004、
    /// 773639 的 1001/3001/3002/3004/5001/5002/9001/9002/17001/17002/19001/25001..25003/27001/27002、
    /// 542091 的 5002、214436 的 7001..7003），**没有一条落在 81..99 的顺序号段里**。
    ///
    /// ⚠️ 万一撞号（例如回放里快照已经占了某个号）就往后找下一个，绝不返回已占用的号。
    ///
    /// ⚠️ <paramref name="side"/> **故意不参与运算** —— 蓝图的 `GenerateNextCardID(turnNumber, out id)`
    ///    根本没有 side 参数（`BP_GameState_Battle.g.cs:1545`，函数签名里只有 `turnNumber`
    ///    与一个 out 槽），所以两边共用同一个计数器。参数留着只是为了让调用点读起来有归属感，
    ///    并与旧签名兼容；**任何"按 side 分号段"的写法都是错的**（历史上那版
    ///    `ClientIdSide` 只让人类走 `1000×回合`、bot 走顺序号，正是"虚空部署"的根因）。
    /// </summary>
    public int NextCardId(Side side)
    {
        int turn = ClientIdTurnOverride ?? Turn;
        if (_createdCountTurn != turn)
        {
            _createdCountTurn = turn;
            _cardsCreatedThisTurn = 0;   // = 客户端 `ResetCardsCreatedThisTurn`
        }

        // 回合号 0 用 500 倍率（`GetCurrentCardID` 里那条 `SelectInt(500, 1000, turn == 0)`）。
        //
        // ⚠️ 说句实话：**这条倍率分支在数学上是死代码** —— `id = turn*mult + n`，
        //    而 mult 只在 `turn == 0` 时取 500，此时 `turn*mult = 0`，两种取值结果一样。
        //    留着它是为了和蓝图逐句对应（好核对），不是为了行为。
        int mult = turn == 0 ? 500 : 1000;

        // ⚠️ 下面这个"避让已占用号"的循环**蓝图里没有** —— 客户端就是
        //    `cardsCreatedCountThisTurn + turnNumber*mult`，撞了也不管。
        //    留着它是因为回放路径上快照已经把 1..80 占了，而回合 0 发出来的号
        //    正好落在那个区间（`turn == 0 ⇒ id = n`），不避让会直接抛
        //    `CreateWithId` 的"cardID 已被占用"。
        //
        //    但它有一个**必须能看见**的副作用：只要我们内存里多出一张本该没有的卡
        //    （头号来源是 `ReplayRunner` 的兜底占位卡），这里就会**跳号**，
        //    于是我们发出的号与客户端越差越远 —— 这种漂移以前是完全静默的。
        //    所以跳一次就记一笔（审计 ⑥ 会显式报出来）。
        int id;
        int skipped = 0;
        do
        {
            id = turn * mult + ++_cardsCreatedThisTurn;
            if (_byCardId.ContainsKey(id))
            {
                skipped++;
            }
        }
        while (_byCardId.ContainsKey(id));

        if (skipped > 0)
        {
            string key = $"<cardid-collision-skip:回合{turn}第{_cardsCreatedThisTurn}张>";
            UnimplementedCalls[key] = UnimplementedCalls.GetValueOrDefault(key) + 1;
        }

        return id;
    }

    /// <summary>
    /// **开局建牌库**专用的顺序号分配器（左侧 2..、右侧 42..）—— 与服务器给开局牌的编号一致。
    ///
    /// ⚠️ 只给自对弈的 `MatchEngine.BuildDeck` 用。真实对局的牌库编号来自服务端快照
    /// （`CreateWithId`），**不经过这里**。效果生成的卡必须走 <see cref="NextCardId"/>。
    /// </summary>
    public int NextSequentialCardId(Side side)
    {
        int id = _nextCardId[(int)side];
        while (_byCardId.ContainsKey(id))
        {
            id++;
        }

        _nextCardId[(int)side] = id + 1;
        return id;
    }

    /// <summary>把 HQ 防御设成指定值（重放时用快照/动作流里的真实值）。</summary>
    public void SetHqDefense(Side side, int defense)    {
        var hq = Hq(side);
        hq.Defense = defense;
        hq.MaxDefense = Math.Max(hq.MaxDefense, defense);
    }

    /// <summary>把卡移动到新位置，并重排目标位置的 location_number（与客户端语义一致）。</summary>
    public void Move(CardInstance card, CardLocation location, int? locationNumber = null,
                     bool changeOwner = false)
    {
        CardLocation oldLocation = card.Location;
        bool moved = oldLocation != location;

        card.Location = location;
        card.LocationNumber = locationNumber ?? NextLocationNumber(card.Owner, location);
        NormalizeLocationNumbers(card.Owner, location);

        // 换区之后派生数值可能变（最典型的是「手牌里才 -1 费」的光环：
        // card_unit_85_pioneer_company 只给**手牌里**的指令减费，
        // 指令结算完进弃牌堆时那个 -1 就不该再算了）。
        // 重算是绝对值式的、幂等的，所以在热路径上重复调用也安全。
        card.RecalculateStats();

        // 「某张卡换区了」事件 —— 挂在**这个唯一入口**上，见 MatchEngine.FireLocationMoved 的出处。
        // ⚠️ 只有**真的换了区**才发：蓝图 `ExecuteOnCardLocationMoved` 是
        //    `CardLocationMoved(cardID, …, oldLocation, newLocation, …)` 调进来的，
        //    同区重排（改 locationNumber）不走它。
        if (moved)
        {
            CardMoved?.Invoke(card, oldLocation, location, changeOwner);
        }
    }

    /// <summary>
    /// 卡换区回调（由 <see cref="MatchEngine"/> 接到 <c>OnCardLocationMoved</c> /
    /// <c>OnOtherCardLocationMoved</c> 上）。
    /// GameState 不认识 CardApi，所以只留一个钩子。
    /// </summary>
    public Action<CardInstance, CardLocation, CardLocation, bool>? CardMoved { get; set; }

    /// <summary>
    /// 改变当前控制方，同时维护按阵营索引。位置本身由调用方随后通过
    /// <see cref="Move"/> 更新，这样换区触发器仍能看到旧位置。
    /// </summary>
    public bool ChangeOwner(CardInstance card, Side newOwner)
    {
        if (newOwner is Side.NotAvailable || card.Owner == newOwner)
        {
            return false;
        }

        Side oldOwner = card.Owner;
        _cardsBySide[(int)oldOwner].Remove(card);
        card.Owner = newOwner;
        _cardsBySide[(int)newOwner].Add(card);
        NormalizeLocationNumbers(oldOwner, card.Location);
        return true;
    }

    /// <summary>卡对象被创建时的回调（对应蓝图 <c>CreateCardObject</c> 里的 <c>OnCreateCard</c>）。</summary>
    public Action<CardInstance>? CardCreated { get; set; }

    public int NextLocationNumber(Side side, CardLocation location)
    {
        var existing = Cards(side, location).ToList();
        return existing.Count == 0 ? 0 : existing.Max(c => c.LocationNumber) + 1;
    }

    /// <summary>把某个位置内的 location_number 压紧成 0..n-1（顺序不变）。</summary>
    public void NormalizeLocationNumbers(Side side, CardLocation location)
    {
        int i = 0;
        foreach (var c in Cards(side, location).ToList())
        {
            c.LocationNumber = i++;
        }
    }

    /// <summary>把一张卡移出对局（例如被销毁后进入弃牌堆由效果决定，这里只做移除）。</summary>
    public void Remove(CardInstance card)
    {
        _byCardId.Remove(card.CardId);
        _cardsBySide[(int)card.Owner].Remove(card);
        card.Location = CardLocation.NotAvailable;
    }

    public void Finish(Side winner, string reason)
    {
        if (IsFinished)
        {
            return;
        }

        IsFinished = true;
        Winner = winner;
        WinnerReason = reason;
    }

    // ==================== 快照（用于和客户端逐步对拍）====================

    public MatchSnapshot Snapshot() => new(
        Turn,
        ActiveSide,
        Kredits(Side.Left), MaxKredits(Side.Left),
        Kredits(Side.Right), MaxKredits(Side.Right),
        FrontlineOwner,
        IsFrontlineLimited,
        AllCards.Select(c => c.Snapshot()).ToArray())
        {
            LeftKreditSlotsLost = KreditSlotsLost(Side.Left),
            RightKreditSlotsLost = KreditSlotsLost(Side.Right),
            LeftHQDamagedThisTurn = GetHQDamagedAmountThisTurn(Side.Left),
            RightHQDamagedThisTurn = GetHQDamagedAmountThisTurn(Side.Right),
            OperationKreditsSpentThisTurn = OperationKreditsSpentThisTurn,
            FrontlineLimiterIds = FrontlineLimiters.OrderBy(id => id).ToArray(),
            // Preserve list order: future dispatch can depend on insertion order.
            Restrictions = GameplayRestrictions.Select(x => new GameplayRestrictionSnapshot(
                x.Side, x.Type, x.SourceCardId, x.TurnsRemaining)).ToArray(),
            SideEffects = GameplaySideEffects.Select(x => new GameplaySideEffectSnapshot(
                x.Side, x.Tag, x.SourceCardId, x.DurationPolicy, x.TurnsRemaining, x.EffectValue)).ToArray(),
        };

    public string SnapshotJson() => System.Text.Json.JsonSerializer.Serialize(
        Snapshot(), SnapshotJsonOptions);

    public static readonly System.Text.Json.JsonSerializerOptions SnapshotJsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        // ⚠️⚠️ 必须显式配反射式解析器 —— 与 `CardDatabase.Load` 是**同一个坑的第二处**。
        //
        // 宿主可能把反射式序列化关掉（fyserver 的 csproj 里
        // `JsonSerializerIsReflectionEnabledByDefault=false`，为了 AOT），
        // 而那个开关是**进程级**的。
        //
        // 本方法被 `AtomicAction.Fingerprint` 用来做**状态指纹**，
        // 而指纹是 `Replayer`（NN 的试算机制）的核心 ⇒ 它一挂，
        // 整个神经网络决策就抛异常，**每回合静默退回贪心**。
        //
        // 实测症状（`rel/data/fyserver/bot-log/bot-20261001.log`）：
        // 一整局 66 行日志里全是
        //   `⚠ 神经网络决策失败（Reflection-based serialization has been disabled…），本回合改用贪心`
        // 而外层 `ServerBotService` 只把它记成一条 warning —— 对局照常跑完，
        // **看起来像"AI 在打但很笨"，实际是 AI 根本没上场**。
        //
        // 教训：这个开关要查**所有** `JsonSerializer.Serialize/Deserialize` 的 Options，
        // 不能只修报错的那一处。内核里现在共 3 处（本处 + `CardDatabase` 的 2 处）。
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };
}

/// <summary>整局状态快照。</summary>
public sealed record MatchSnapshot(
    int Turn,
    Side ActiveSide,
    int LeftKredits,
    int LeftMaxKredits,
    int RightKredits,
    int RightMaxKredits,
    Side FrontlineOwner,
    bool IsFrontlineLimited,
    CardSnapshot[] Cards)
{
    public int LeftKreditSlotsLost { get; init; }
    public int RightKreditSlotsLost { get; init; }
    public int LeftHQDamagedThisTurn { get; init; }
    public int RightHQDamagedThisTurn { get; init; }
    public int OperationKreditsSpentThisTurn { get; init; }
    public int[] FrontlineLimiterIds { get; init; } = Array.Empty<int>();
    public GameplayRestrictionSnapshot[] Restrictions { get; init; } = Array.Empty<GameplayRestrictionSnapshot>();
    public GameplaySideEffectSnapshot[] SideEffects { get; init; } = Array.Empty<GameplaySideEffectSnapshot>();
}

public sealed record GameplayRestrictionSnapshot(Side Side, GameplayRestrictionType Type,
    int SourceCardId, int TurnsRemaining);

public sealed record GameplaySideEffectSnapshot(Side Side, string Tag, int SourceCardId,
    int DurationPolicy, int TurnsRemaining, int EffectValue);

/// <summary>一条已结算的动作 —— 与协议里的 action 信封对应。</summary>
public sealed record GameAction(
    int ActionId,
    string ActionType,
    Side Player,
    int TurnNumber,
    IReadOnlyList<SubAction> SubActions,
    IReadOnlyDictionary<string, object?> ActionData);

/// <summary>子动作 —— 对应协议里的 <c>SubAction { Name, Values: ActionValue2[] }</c>。</summary>
public sealed record SubAction(string Name, IReadOnlyList<ActionValue2> Values);

/// <summary>对应游戏原生结构体 <c>/Script/kards.ActionValue2</c>。</summary>
public sealed record ActionValue2(string Name, int Value, string Text = "")
{
    public static ActionValue2 Int(string name, int value) => new(name, value);
    public static ActionValue2 Str(string name, string value) => new(name, 0, value);
    public static ActionValue2 Bool(string name, bool value) => new(name, value ? 1 : 0);
}
