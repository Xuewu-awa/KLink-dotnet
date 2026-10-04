using KLink.Bot.Engine;

namespace KLink.Bot.Effects;

/// <summary>
/// 卡牌级效果 API —— 就是那 170 个「外部调用」的内核实现。
///
/// 名字与游戏反编译出的调用名**逐字一致**，这样：
/// 1. <c>docs/card-effects.json</c> 里的调用清单可以直接对着查
/// 2. 未实现的调用能被精确统计（<see cref="GameState.UnimplementedCalls"/>）
/// 3. 将来做「Kismet 字节码 → 效果脚本」的反编译器时，输出可以直接对接这里
///
/// ⚠️ 已知限制：反编译产物给的是**调用集合**，不是数据流图。
/// 也就是说我们知道一张卡调用了 <c>GetTargetedCard</c> 和 <c>ChangeDefense</c>，
/// 但不知道 ChangeDefense 用的是不是 GetTargetedCard 的返回值、数值是几。
/// 所以本类分成两层：
/// - <b>原语层</b>（下面这些方法）：语义明确、可复用，是真正要实现的
/// - <b>编排层</b>（<see cref="CardEffectScripts"/>）：每张卡具体怎么组合
/// 编排层目前只手工写了一批，其余记入未实现统计。
/// </summary>
public sealed partial class CardApi
{
    private readonly HashSet<int> _playedCardBroadcastDone = new();
    private int _playedCardBroadcastDepth;

    public IDisposable BeginPlayedCardBroadcast(int cardId)
    {
        if (_playedCardBroadcastDepth++ == 0) _playedCardBroadcastDone.Clear();
        return new BroadcastScope(this);
    }

    public void EndPlayedCardBroadcast() => _playedCardBroadcastDepth = Math.Max(0, _playedCardBroadcastDepth - 1);

    private sealed class BroadcastScope : IDisposable
    {
        private readonly CardApi _api;
        public BroadcastScope(CardApi api) => _api = api;
        public void Dispose() => _api.EndPlayedCardBroadcast();
    }
    private readonly MatchEngine _engine;

    public CardApi(MatchEngine engine)
    {
        _engine = engine;
        _dispatch = BuildDispatch();
        Vm = new Blueprint.KismetVm(this);
    }

    /// <summary>Kismet 解释器 —— 执行卡牌蓝图的效果程序。</summary>
    public Blueprint.KismetVm Vm { get; }

    private GameState State => _engine.State;

    // ==================================================================
    //  编排层：执行一张卡的效果
    // ==================================================================

    /// <summary>
    /// 执行一张卡从手牌打出时的效果。
    ///
    /// 顺序：
    /// 1. 手写脚本（<see cref="CardEffectScripts"/>）—— 用于需要人工语气澄清的卡
    /// 2. **Kismet 解释器** —— 直接跑反编译出来的效果程序（覆盖全部 1636 张有蓝图的卡）
    /// 3. 都没有 → 计入未实现统计
    /// </summary>
    public void RunCardEffect(CardInstance card, CardInstance? target)
    {
        var ctx = new EffectContext
        {
            Engine = _engine,
            State = State,
            Self = card,
            Target = target,
            Controller = card.Owner,
            Calls = card.Definition.PlayFromHandCalls().Distinct(StringComparer.Ordinal).ToList(),
        };

        // 1) 手写脚本优先
        if (CardEffectScripts.TryGet(card.Name, out var script)
            || CardEffectScripts.TryGet(card.Definition.Name, out script))
        {
            script(ctx);
            return;
        }

        // 2) Kismet 解释器
        var library = Blueprint.KismetLibrary.Default;
        var program = library?.FindProgram(card.Definition.Name, "OnPlayedFromHand");
        if (program is not null)
        {
            Vm.Run(program, ctx);
            return;
        }

        // 3) 没有 OnPlayedFromHand —— 先判断这是不是**正常**的。
        //    竞技卡组里大量单位只有触发式效果（例如「本回合受到攻击时 +1」），
        //    它们本来就没有战吼。这种情况不是缺口，不该计入。
        if (library?.ProgramNames(card.Definition.Name).Any() == true)
        {
            return;   // 触发式卡：效果挂在它自己的事件程序上，等事件派发
        }

        // 4) 连蓝图逻辑都没有 → 才算真的缺口
        NotifyUnimplemented($"<card:{card.Definition.Name}>");
    }

    /// <summary>
    /// 派发一个触发事件。
    ///
    /// 事件名**就是**蓝图里的程序名（<c>OnStartOfTurn</c> / <c>OnAfterAttack</c> /
    /// <c>OnOtherCardDestroyed</c> …），不做二次映射 —— IR 里有什么名字就用什么名字。
    ///
    /// <paramref name="fireOthers"/> 为 true 时，还会给**其它**卡派发对应的
    /// <c>OnOtherXxx</c> 事件（例如「当其它卡被打出时」）。
    /// </summary>
    /// <param name="selfProgramName">
    /// 给事件主体（<paramref name="subject"/>）单独用的程序名。
    ///
    /// ⚠️ 需要它的理由：同一个时机常常有**两个**不同的「别人」程序名，
    /// 例如离场时 <c>OnOtherCardLeaveBoardOrOwner</c>（`card_unit_214th_amur`）
    /// 和 <c>OnAfterOtherCardLeaveBoardOrOwner</c>（`card_unit_big_red_one`）
    /// 是并列的，第二遍调用时要用 <c>selfProgramName</c> 把主体那一路置空，
    /// 否则主体会被触发两次、光环被撤销两次。
    /// </param>
    /// <param name="eventSubject">
    /// **事件参数**里那张卡 —— 也就是 <c>K2Node_Event_cardPlayed</c> /
    /// <c>K2Node_Event_cardLeaving</c> 应当读到的卡。
    ///
    /// ⚠️ 它和 <paramref name="subject"/> **不是一回事**，这个区分是必须的：
    /// <paramref name="subject"/> 是「事件的主角」（决定哪张卡的程序被跑、
    /// 以及"别人"分支要排除谁），而 <paramref name="eventSubject"/> 是事件**携带的数据**。
    /// 典型场景：A 被打出 → 给场上的光环派发 `OnOtherCardPlayedFromHand`，
    /// 这时主角是 A，但**被派发到的是光环**。光环的程序里
    /// `tempCard = K2Node_Event_cardPlayed` 想拿的是 A；
    /// 如果只传 subject、让 VM 拿 Self 兜底，光环会读到自己 ——
    /// 实测症状：`IsOrder(光环)` 恒假，85 先驱连永远不还原费用。
    /// 传 null 时退回 <paramref name="subject"/>（大部分事件两者本来就相同）。
    /// </param>
    /// <param name="namedArgs">
    /// 具名参数（按蓝图出参槽名）。`KismetVm.GetMember` 在具名载荷里找不到时，
    /// 回退到位置参数 <paramref name="eventArgs"/>。
    /// </param>
    /// <param name="broadcastName">
    /// **这个程序名本身是"广播给别人"的，即使它不以 `OnOther` 开头。**
    ///
    /// ⚠️ 为什么需要它：广播判据原先是纯命名约定（`StartsWith("OnOther")`），
    /// 但蓝图里有一批广播名不满足这个约定，最典型的是
    /// `OnBeforeOtherCardPlayedFromHand`（39 张订阅者）——
    /// 它名字里带 `Other` 但前缀是 `OnBefore`，于是被当成"只发给主体"，
    /// **39 张卡的整条分支静默死掉**。同一个坑还有
    /// `OnAfterOtherCardSuppressed`（见 `SuppressUnit`，那里用 otherProgramName 绕开）。
    ///
    /// 出处（`out/bp-cardfn.json`，函数 `CardPlayedFromHand`）：
    /// <code>
    /// si=1322  FetchAllCardsWithEventTrigger(19)      ; 19 = OnBeforeOtherCardPlayedFromHand
    /// si=1386      NotEqual_ObjectObject(item, cardPlayed)   ; ★ 广播**排除主体**
    /// si=1493      item.OnBeforeOtherCardPlayedFromHand(cardPlayed)
    /// </code>
    /// </param>
    public void FireTrigger(string programName, CardInstance? subject, Side controller,
                            string? otherProgramName = null, string? selfProgramName = null,
                            IReadOnlyList<object?>? eventArgs = null,
                            CardLocation? goingToLocation = null,
                            CardInstance? eventSubject = null,
                            IReadOnlyDictionary<string, object?>? namedArgs = null,
                            CardLocation? oldLocation = null,
                            CardLocation? newLocation = null,
                            bool broadcastName = false,
                            IReadOnlyDictionary<string, object?>? localsSeed = null)
    {
        CardInstance? eventCard = eventSubject ?? subject;
        if (_playedCardBroadcastDepth > 0 && programName == "OnOtherCardPlayedFromHand"
            && eventCard is not null && !_playedCardBroadcastDone.Add(eventCard.CardId))
        {
            return;
        }
        var library = Blueprint.KismetLibrary.Default;
        if (library is null)
        {
            return;
        }

        // 性能要点：**遍历"还活着的卡"**，而不是遍历「实现了该事件的卡名列表」。
        // 后者可能有几百个名字，每次派发都要对每个名字扫一遍找实例；
        // 前者每张卡一次字典查询。实测差几十倍。
        //
        // ⚠️ 必须先快照：触发程序内部会创建/销毁卡（例如 meteor 打完自己移除），
        //    直接迭代活列表会抛 "Collection was modified"。
        //
        // ⚠️ 而且 FireTrigger 会**重入**（触发效果里又派发事件），
        //    所以缓冲不能共用一份 —— 内层 Clear() 会把外层正在遍历的列表清空。
        //    这里按嵌套深度取不同的缓冲。
        //
        // ⚠️⚠️ **快照必须包含弃牌堆**，不能只有棋盘。
        //    判据（全卡池统计）：订阅 `OnOtherCardPlayedFromHand` 的 131 张里有
        //    **23 张是指令**、订阅 `OnEndOfTurn` 的 188 张里有 **69 张指令**、
        //    `OnOtherCardReset` 39 张里 12 张指令。指令打出后**立刻进弃牌堆**，
        //    却仍然订阅这些"打出之后才发生"的事件 —— 说明客户端是把触发派发给
        //    仍在局内的指令卡的。最典型的例子就是 `card_event_committed_crew`：
        //    卡面写"Until end of turn, Spitfires … get +3+3 when deployed"，
        //    它自己是指令、在弃牌堆里，却要靠 `OnOtherCardPlayedFromHand`
        //    给之后部署的 Spitfire 加成。只扫棋盘的话这条永远不生效。
        //
        //    为什么不怕"死掉的单位也被派发"：卡蓝图里每个分支**开头都自带**
        //    `IsLocatedOnBoard` / `IsValid` 守卫（实测 85_pioneer 的每条分支、
        //    committed_crew 的每条分支都是这么写的），不在场的卡会被自己挡掉。
        //
        // ⚠️⚠️ **快照还必须包含「手牌」**（2026-10-04 修，见 `FillTriggerSnapshot`）。
        //    原先只有「棋盘 + 弃牌堆」⇒ **手牌里的卡从收不到任何触发** ⇒
        //    所有「在手牌里响应事件」的分支是死代码。
        var snapshot = SnapshotBuffer(_triggerDepth);
        FillTriggerSnapshot(snapshot);

        bool subjectSeen = false;
        foreach (var card in snapshot)
        {
            // ⚠️ 这里是 **`!= NotAvailable`**，不是 `IsAlive`。
            //
            // `IsAlive` 把「在弃牌堆」也算作"不在场"（它的语义是"还能被打/被选中"），
            // 而触发派发要的是"这张卡**还在局内**"：指令打出后就躺在弃牌堆里，
            // 但它的"本回合内持续生效"效果还得继续响应事件
            // （`card_event_committed_crew` 就是这类，见上面快照那段注释）。
            // 只有 `NotAvailable`（被移出对局）才真正不该再收到任何触发。
            // 不在场的卡会不会误触发，由它自己程序里的 `IsLocatedOnBoard` 守卫负责。
            if (card.Location == CardLocation.NotAvailable)
            {
                continue;
            }

            string name = card.Name;
            bool isSubject = ReferenceEquals(card, subject);
            if (isSubject)
            {
                subjectSeen = true;
            }

            // 主体那一路要跑的程序名（调用方可用 selfProgramName 覆盖）
            string selfProgram = selfProgramName ?? programName;

            // 程序名是「广播给别的卡」还是「只有主体自己」？
            //
            // 判据是**命名约定**，而且是实测出来的：
            //   · `OnOtherXxx` 是广播 —— `OnOtherCardPlayedFromHand` 有 131 个订阅者，
            //     而它的事件主体是"刚被打出的那张牌"，那张牌自己显然不是订阅者。
            //   · 其余 `OnXxx` 是「**自己**发生了 X」—— 只给主体。
            //
            // ⚠️ 旧实现把 `programName` 无条件发给**场上每一张卡**，这个错误非常隐蔽：
            //    `card_unit_3_panzergrenadier` 的 `OnAfterAttack` 程序体是
            //    **无条件** `ChangeAttack(+1)+ChangeDefense(+1)`（"我攻击了就 +1+1"），
            //    于是别人攻击时它也跟着涨 —— 实测一个美国 M2A4 操作也能让它 +1+1，
            //    表现成"累计涨超"（T5 6/6、T7 11/12，比实际德国单位操作次数多）。
            //    它的 `OnOtherCardAttacks` 才是那条带阵营判定的分支。
            bool broadcast = selfProgramName is null
                             && (broadcastName
                                 || programName.StartsWith("OnOther", StringComparison.Ordinal));

            if (broadcast)
            {
                // 广播：除主体之外的所有卡
                if (!isSubject && library.FindProgram(name, programName) is not null)
                {
                    TriggerTrace?.Add($"{programName} → {name}#{card.CardId}" +
                                      $"（eventCard={eventCard?.Name ?? "null"}#{eventCard?.CardId}）");
                    RunTriggerProgram(library, card, name, programName, eventCard, eventArgs, goingToLocation,
                                      namedArgs, oldLocation, newLocation, localsSeed);
                }
            }
            else if (subject is null || isSubject)
            {
                // 自己那一路：主体在场就只发主体；主体为 null（全局事件）时发给所有卡
                if (library.FindProgram(name, selfProgram) is not null)
                {
                    TriggerTrace?.Add($"{selfProgram} → {name}#{card.CardId}" +
                                      $"（eventCard={eventCard?.Name ?? "null"}#{eventCard?.CardId}）");
                    RunTriggerProgram(library, card, name, selfProgram, eventCard, eventArgs, goingToLocation,
                                      namedArgs, oldLocation, newLocation, localsSeed);
                }
            }

            // 显式的「别的卡」那一路：除主体之外的所有卡
            if (otherProgramName is not null
                && !isSubject
                && library.FindProgram(name, otherProgramName) is not null)
            {
                TriggerTrace?.Add($"{otherProgramName} → {name}#{card.CardId}" +
                                  $"（eventCard={eventCard?.Name ?? "null"}#{eventCard?.CardId}）");
                RunTriggerProgram(library, card, name, otherProgramName, eventCard, eventArgs, goingToLocation,
                                  namedArgs, oldLocation, newLocation, localsSeed);
            }
        }

        // 事件主体可能不在场上（例如刚被打出、或已被移走），单独补一次。
        // ⚠️ 只补「自己那一路」：`OnOtherXxx` 是广播给**别人**的，
        //    把主体也算进去会让"刚抽到手的牌"响应自己的 `OnOtherCardDrawnFromDeck`。
        //
        // ⚠️⚠️ **补之前必须确认主体真的不在快照里**（2026-10-04 加）。
        //    快照现在**含手牌**（见 `FillTriggerSnapshot`），而手牌里的卡满足
        //    `IsAlive && !Location.IsBoard()` ⇒ 旧的兜底条件对它恒成立 ⇒
        //    **同一个程序会跑两遍**（一遍走循环里 `isSubject` 那一路，一遍走这里）。
        //    典型受害卡：`card_unit_5th_regiment` 的 `-2 费`（跑两遍 = -4 费）。
        string subjectProgram = selfProgramName ?? programName;
        bool subjectBroadcast = selfProgramName is null
                                && (broadcastName
                                    || programName.StartsWith("OnOther", StringComparison.Ordinal));
        if (!subjectBroadcast && !subjectSeen
            && subject is not null && subject.IsAlive && !subject.IsHq && !subject.Location.IsBoard()
            && library.FindProgram(subject.Name, subjectProgram) is not null)
        {
            // ⚠️ 这一支**也必须记 TriggerTrace**。原先漏了它，于是"主体不在棋盘上"
            //    （刚抽到手 / 刚生成到手 / 刚进弃牌堆…）的派发在诊断记录里**完全看不见** ——
            //    自测据此判"没派发"，会把好代码判死。属于诊断口径的 bug，不是规则行为。
            TriggerTrace?.Add($"{subjectProgram} → {subject.Name}#{subject.CardId}" +
                              $"（eventCard={eventCard?.Name ?? "null"}#{eventCard?.CardId}，主体不在棋盘，兜底那一路）");
            RunTriggerProgram(library, subject, subject.Name, subjectProgram, eventCard, eventArgs, goingToLocation,
                              namedArgs, oldLocation, newLocation, localsSeed);
        }
    }

    /// <summary>触发派发用的快照缓冲，按嵌套深度索引（FireTrigger 会重入）。</summary>
    private readonly List<List<CardInstance>> _triggerBuffers = new();

    /// <summary>
    /// 广播一个**带出参**的事件，并把每个订阅者写回的出参收集起来。
    ///
    /// 为什么 <see cref="FireTrigger"/> 不够：蓝图里有一族事件是靠出参回传结果的
    /// —— 调用方循环 `FetchAllCardsWithEventTrigger(N)`，对每张订阅卡
    /// `item.OnXxx(..., out value)`，然后**累加/判断**这个 value。
    /// <see cref="FireTrigger"/> 丢掉返回值，这一族就整条断掉。
    ///
    /// 出处（`out/bp-cardfn.json`）：
    /// <code>
    /// ExecuteOnDeploymentTriggered（24 条语句）
    ///   si=61   FetchAllCardsWithEventTrigger(23)
    ///   si=334  item
    ///   si=397  _triggerMultipleDeployment += item.OnDeploymentEffectTriggered_TriggerMultiple
    ///   si=471  out:triggerMultiple = _triggerMultipleDeployment
    ///
    /// CardPlayedFromHand si=3676..4278（事件 14 的取消钩子）
    ///   si=3676 FetchAllCardsWithEventTrigger(14)
    ///   si=4081 PopExecutionFlowIfNot(cancelDeploymentEffect)   ; 假 ⇒ 继续循环
    ///   si=4197 breakFlag = true                                ; 真 ⇒ 跳出
    ///   ★ 这一段**没有** `NotEqual(item, cardPlayed)` 排除主体
    ///     （对比事件 19 在 si=1386 就有）—— 所以订阅者集合**含主体自己**。
    /// </code>
    /// </summary>
    /// <param name="outParamName">
    /// 出参在函数体里的变量名（IR 把 `out:X` 编成普通的 `set dst="X"`）。
    /// 例：`cancelDeploymentEffect` / `TriggerMultiple`。
    /// </param>
    /// <param name="seed">
    /// 事件函数的**入参**。独立事件函数体里读的是裸变量名
    /// （`cardDeploying` / `cardTriggered`），`Frame` 不认识它们，必须显式喂。
    /// </param>
    /// <returns>每个成功执行的订阅者写回的出参值，按派发顺序。</returns>
    public List<object?> BroadcastWithOutParam(
        string programName, CardInstance? subject, Side controller, string outParamName,
        IReadOnlyDictionary<string, object?>? seed = null,
        IReadOnlyList<object?>? eventArgs = null,
        CardInstance? eventSubject = null,
        IReadOnlyDictionary<string, object?>? namedArgs = null,
        Func<CardInstance, IReadOnlyDictionary<string, object?>?>? seedFactory = null)
    {
        var bags = BroadcastWithOutParams(programName, subject, controller, new[] { outParamName },
                                          seed, eventArgs, eventSubject, namedArgs, seedFactory);
        var results = new List<object?>(bags.Count);
        foreach (var (_, outs) in bags)
        {
            results.Add(outs.GetValueOrDefault(outParamName));
        }

        return results;
    }

    /// <summary>一次「带出参的派发」的结果：派发到哪张卡 + 它写回的出参。</summary>
    public readonly record struct OutParamHit(CardInstance Card, IReadOnlyDictionary<string, object?> Outs);

    /// <summary>
    /// <see cref="BroadcastWithOutParam"/> 的多出参版本。
    ///
    /// 需要它是因为 `OnOtherCardDealDamageAddDamage` 有**两个**出参
    /// （`damageToAdd` + `reRunAtEnd`，见 <see cref="ExecuteOnDealDamageAddDamage"/>），
    /// 而单出参版只能取回一个。返回的字典对每个请求过的名字都给键
    /// （函数体没写到的值为 null），免得调用方分不清「没这个出参」和「出参是空」。
    ///
    /// 额外回传**是哪张卡**：蓝图的 `reRunAtEnd` 语义是「把这张**卡**记下来，
    /// 最后整轮再派发一次」（`si=623 Array_Add(addDamageToReRun, item)`），
    /// 光有出参值重建不出这一批卡。
    /// </summary>
    public List<OutParamHit> BroadcastWithOutParams(
        string programName, CardInstance? subject, Side controller, string[] outParamNames,
        IReadOnlyDictionary<string, object?>? seed = null,
        IReadOnlyList<object?>? eventArgs = null,
        CardInstance? eventSubject = null,
        IReadOnlyDictionary<string, object?>? namedArgs = null,
        Func<CardInstance, IReadOnlyDictionary<string, object?>?>? seedFactory = null,
        IReadOnlyList<CardInstance>? only = null,
        CardInstance? exclude = null)
    {
        var results = new List<OutParamHit>();
        var library = Blueprint.KismetLibrary.Default;
        if (library is null)
        {
            return results;
        }

        // 快照规则与 FireTrigger 一致（含手牌）：见 `FillTriggerSnapshot`。
        // ⚠️ 必须先快照：被派发的程序内部会创建/销毁卡（`receiver` 可能被打死）。
        List<CardInstance> snapshot;
        if (only is not null)
        {
            snapshot = new List<CardInstance>(only);
        }
        else
        {
            snapshot = SnapshotBuffer(_triggerDepth);
            FillTriggerSnapshot(snapshot);
        }

        CardInstance? eventCard = eventSubject ?? subject;
        foreach (var card in snapshot)
        {
            if (card.Location == CardLocation.NotAvailable)
            {
                continue;
            }

            // ★ `exclude`：蓝图里"广播**排除某一方**"是逐触发点写的，不是命名约定。
            //   最典型的是 T31 `OnOtherCardAttacks` 的 `:4356
            //   NotEqual_IntInt(attackerCardID, item.cardID)` —— **排除攻击者本人**
            //   （而**防御方照收**：`card_unit_beaufighter_tf_mk_x` / `card_unit_33rd_livorno`
            //   / `card_unit_bm_13n_us6` 就是靠"自己是被打的那个"来触发的）。
            //   注意：`FireTrigger` 的广播分支是按"排除主体"实现的，与这里**不是**一回事。
            if (exclude is not null && ReferenceEquals(card, exclude))
            {
                continue;
            }

            var program = library.FindProgram(card.Name, programName);
            if (program is null)
            {
                continue;
            }

            // ⚠️ `outParamNames` 为空时**不写 `(out )`** —— 否则 trace 变成
            //    `事件名(out ) → 卡#id`，而 `SelfTest.Reached` 判的是
            //    `StartsWith("事件名 → 卡#id")` ⇒ 无出参的广播会被误判成"没派发"。
            string outSuffix = outParamNames.Length > 0
                ? $"(out {string.Join("/", outParamNames)})"
                : "";
            TriggerTrace?.Add($"{programName}{outSuffix} → {card.Name}#{card.CardId}" +
                              $"（eventCard={eventCard?.Name ?? "null"}#{eventCard?.CardId}）");

            var ctx = new EffectContext
            {
                Engine = _engine,
                State = State,
                Self = card,
                Target = eventCard,
                Trigger = eventCard,
                Controller = card.Owner,
                EventArgs = eventArgs ?? Array.Empty<object?>(),
                NamedArgs = namedArgs ?? EffectContext.EmptyNamedArgsPublic,
            };

            if (_triggerDepth >= MaxTriggerDepth)
            {
                Vm.UnsupportedOps["<trigger-depth-limit>"] =
                    Vm.UnsupportedOps.GetValueOrDefault("<trigger-depth-limit>") + 1;
                continue;
            }

            _triggerDepth++;
            try
            {
                var perCardSeed = seedFactory?.Invoke(card) ?? seed;
                results.Add(new OutParamHit(card,
                    Vm.RunLocalProgramMulti(program, ctx, perCardSeed, outParamNames)));
            }
            finally
            {
                _triggerDepth--;
            }
        }

        return results;
    }

    /// <summary>
    /// 部署链的第一环：**「别的卡即将部署」钩子，任一订阅者可以取消整条部署效果**。
    ///
    /// 出处 `out/bp-cardfn.json` 的 `CardPlayedFromHand`（si=3640 起、`hasDeployment` 门内）：
    /// <code>
    /// si=3676  FetchAllCardsWithEventTrigger(14)          ; 14 = OnBeforeOtherCardDeploymentTrigger
    /// si=4081  PopExecutionFlowIfNot(cancelDeploymentEffect)   ; 假 ⇒ 继续循环
    /// si=4197  Temp_bool_True_if_break_was_hit_Variable = true ; 真 ⇒ 跳出
    /// si=4283  JumpIfNot 5702 if !cancelDeploymentEffect
    /// si=4297  （取消分支）OnPlayedFromHandExecuted = false
    /// si=4308  NotifySideEffectTrigger(side, 'sideeffect.blockdeployment')
    /// si=4881  Jump 6853                                    ; ★ 取消分支到此返回，
    ///                                                        **不会**跑到 si=6114 的 OnPlayedFromHand
    /// </code>
    /// 控制流可达性用 `out/audit/p1-cfg.py CardPlayedFromHand 4297` 复核过：
    /// 从 si=4297 出发可达 44 条语句，全部终止于 si=6853 `Return`，**不经过 5702/6114**。
    ///
    /// 语义与卡面文本互证（`out/cards-full2.json`）：
    ///   · `card_unit_petlyakov_pe_2ft`「Deployment effects do not trigger.」
    ///     —— 函数体 `cardDeploying.hasDeployment &amp;&amp; self.IsLocatedOnBoard()` ⇒ true
    ///   · `card_event_evasive_action`「… Cancel the effect.」⇒ true
    ///   · `card_event_close_call`「Counter an order or deployment effect…」⇒ true
    ///   · `card_unit_buffs`「Gets +1+1 when it is targeted by an order or deployment effect.」
    ///     —— 两条分支都写 false（它只加 buff，不取消）
    /// </summary>
    /// <returns>被取消了就返回 true。</returns>
    public bool FireDeploymentCancelHook(CardInstance card)
    {
        var seed = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // 函数体里的裸变量名（见 IR 的 locals 表）：
            //   card_unit_petlyakov_pe_2ft / card_event_evasive_action / card_unit_buffs
            //   都读 `cardDeploying`；`card_event_close_call` 还读 `cardDeploying.currentTarget`。
            ["cardDeploying"] = card,
            ["instigatorID"] = card.CardId,
        };

        var named = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cardDeploying"] = card,
        };

        var outs = BroadcastWithOutParam(
            "OnBeforeOtherCardDeploymentTrigger", card, card.Owner, "cancelDeploymentEffect",
            seed: seed, eventArgs: new object?[] { card }, eventSubject: card, namedArgs: named);

        foreach (var v in outs)
        {
            if (Blueprint.KismetVm.Truthy(v))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// T30 `OnOtherCardAttackSwitchTarget`（枚举 30）—— 「攻击中途换目标」，只有 **2 张**订阅卡
    /// （`card_event_cold_trap` / `card_event_decisive_defense`，**都是 gotcha**）。
    ///
    /// 蓝图 `AttackCard`（`:4214-4308`，本次逐行复核）：
    /// <code>
    /// :4214  FetchAllCardsWithEventTrigger(30)
    /// :4216  Temp_bool_True_if_break_was_hit_Variable = false
    /// :4221  while (!break_flag &amp;&amp; i &lt; len)
    /// :4257      item.OnOtherCardAttackSwitchTarget(_attackerCard, _defenderCard, out newDefender)
    /// :4259      NotEqual_ObjectObject(newDefender, _defenderCard)   ; ★ 变了才继续
    /// :4261      if (!变了) → 下一个订阅者
    /// :4282          _defenderCard = newDefender
    /// :4284          defenderCardID = newDefender.cardID
    /// :4288          _defenderLocation = _defenderCard.location
    /// :4308          break_flag = true                                ; ★ 第一个改者胜出、跳出轮
    /// </code>
    /// ⇒ **第一个把 `newDefender` 改成别的卡的订阅者胜出**；"不改"的表达是**写回 `oldDefender`**
    ///（`cold_trap` i=46/50、`decisive_defense` i=691 都是显式写回）——
    /// 所以判据必须是 `newDefender != oldDefender`，不是"出参为空"。
    ///
    /// ⚠️ **与 T31 的两处差别（都按蓝图）**：
    /// ① 这一轮**不排除攻击者**（`:4214` 的 Fetch 之后直接就是调用，没有 T31 那种
    ///    `attackerCardID != item.cardID` 比对）⇒ 本方法**不传** `exclude`；
    /// ② 换完目标**不重做任何合法性判据**（掩护 / 烟幕 / 射程 / `CanSelectAsTarget` 都不再查），
    ///    伤害是对着**新目标**重算的（`:4657` 起用的就是 `_defenderCard`）。
    ///
    /// ⚠️ `seed` 把 `newDefender` 预置成 `defender`：蓝图那个出参槽是零初始化的
    ///（`Val.Nothing`），理论上"程序不写出参"会让 `NotEqual(null, defender)` 为真、
    /// 把目标置成 `null` —— 但**两张订阅卡都显式写回**，所以预置成 `defender`
    /// 与实际行为等价、且不会把目标弄成 null。
    ///
    /// ⚠️ **回放侧无信号**：2 张订阅卡在 22 局语料里 **0 命中** ⇒ 判据是「蓝图原文 + 自测」。
    /// </summary>
    public CardInstance SwitchAttackTargetIfAny(CardInstance attacker, CardInstance defender)
    {
        var seed = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cardAttacking"] = attacker,
            ["oldDefender"] = defender,
            ["newDefender"] = defender,
        };
        var named = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cardAttacking"] = attacker,
            ["oldDefender"] = defender,
        };

        foreach (var hit in BroadcastWithOutParams(
                     "OnOtherCardAttackSwitchTarget", attacker, attacker.Owner,
                     new[] { "newDefender" },
                     seed: seed,
                     eventArgs: new object?[] { attacker, defender },
                     eventSubject: attacker,
                     namedArgs: named))
        {
            if (Environment.GetEnvironmentVariable("KLINK_TRACE_T30") == "1")
            {
                object? raw = hit.Outs.GetValueOrDefault("newDefender");
                Console.Error.WriteLine(
                    $"[T30] hit={hit.Card.Name}#{hit.Card.CardId} outs=[{string.Join(",", hit.Outs.Keys)}] " +
                    $"newDefender={raw?.GetType().Name ?? "null"}:{raw} oldDefender={defender.Name}#{defender.CardId}");
            }

            if (hit.Outs.GetValueOrDefault("newDefender") is CardInstance nd
                && !ReferenceEquals(nd, defender))
            {
                return nd;   // 蓝图 `:4282-:4308`：胜出并跳出轮
            }
        }

        return defender;
    }

    /// <summary>
    /// 攻击反制窗口的返回（蓝图 `AttackCard` 的两个出参）。
    /// </summary>
    /// <param name="StopAttack">
    /// 蓝图 `:4389 SetStopAttack(True)` → `:4487 GetStopAttack()`：
    /// **整条攻击作废** —— 油费**不扣**（扣费在 `:4513`，在判定之后）、
    /// 不记"已攻击"、伤害不结算、`OnAfterAttack` / T4 都不发。
    /// </param>
    /// <param name="AttackedAndStopped">
    /// 蓝图 `:4643 if (tmpAttackedAndStopped)`：**油费照扣、照记"已攻击"，但整段伤害跳过**，
    /// 改走 `ExecuteStoppedAttack`（`:17699-17721`）。
    /// </param>
    public readonly record struct AttackIntercept(bool StopAttack, bool AttackedAndStopped);

    /// <summary>
    /// T31 `OnOtherCardAttacks`（枚举 31）——「别人攻击时」的反制窗口，带两个出参。
    ///
    /// ## 蓝图原文（本次逐行复核，`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs`）
    /// <code>
    /// :4334  SetStopAttack(GameStateRef, False)                 ; 每轮先复位
    /// :4336  tmpAttackedAndStopped = False
    /// :4338  FetchAllCardsWithEventTrigger(31)                  ; ★ 唯一 Fetch 点
    /// :4356      NotEqual_IntInt(attackerCardID, item.cardID)   ; ★ 排除**攻击者本人**
    /// :4385/:4434  item.OnOtherCardAttacks(_attackerCard, _defenderCard,
    ///                                      out stopAttack, out AttackedAndStopped)
    /// :4389      if (stopAttack) SetStopAttack(True)
    /// :4412      if (AttackedAndStopped) tmpAttackedAndStopped = True
    /// :4487  GetStopAttack() 真 ⇒ :4510 success = True 后**直接返回**（不扣油费）
    /// :4513  ChangeKreditsBySide(-costToPay)                    ; ★ 扣油费在窗口**之后**
    /// :4643  if (tmpAttackedAndStopped) ⇒ ExecuteStoppedAttack(…)（跳过全部伤害）
    /// </code>
    ///
    /// ## 三个必须照做的细节
    /// <list type="number">
    /// <item>**收件人是订阅表**（`docs/card-ir.json` 的 **`locals` 20 张**、`entrypoints` 0 张）
    ///   —— 这也是 README §9.5 P1 的原始发现。</item>
    /// <item>**只排除攻击者本人**（`:4356`）；**防御方照收**。内核
    ///   `BroadcastWithOutParams` 的广播分支不排除主体（与 `FireTrigger` 不同），
    ///   所以这里用新增的 `exclude: attacker` 精确表达"只排除攻击者"。</item>
    /// <item>蓝图**不跳出轮**（`:4389`/`:4438` 只置全局标记）⇒ 必须**走完整轮再判定**；
    ///   两个出参**可以同时为真**，而 `stopAttack` 优先（`:4487` 早于 `:4643`）。</item>
    /// </list>
    ///
    /// ⚠️ 实参名逐字取 `Generated/_index.g.cs:4096`
    /// = `{ "cardAttacking", "defenderCard", "stopAttack", "AttackedAndStopped" }`。
    ///
    /// ⚠️ **`stopAttack` 这条分支目前是死代码**：20 张订阅卡的全部出参写入里，
    /// `stopAttack` 的字面量**只有 `false`**（`card-ir.json` 实测）。但机制必须留着 ——
    /// 它是唯一的"整条攻击作废"通道，而且它与 `AttackedAndStopped` 的**代价不同**
    /// （一个不扣油费、一个扣）。
    /// </summary>
    public AttackIntercept FireOtherCardAttacks(CardInstance attacker, CardInstance defender)
    {
        var seed = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cardAttacking"] = attacker,
            ["defenderCard"] = defender,
            // 出参槽是零初始化：函数体没走到的分支读到的就是它
            ["stopAttack"] = false,
            ["AttackedAndStopped"] = false,
        };
        var named = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cardAttacking"] = attacker,
            ["defenderCard"] = defender,
        };

        bool stop = false;
        bool andStopped = false;
        int hits = 0;
        foreach (var hit in BroadcastWithOutParams(
                     "OnOtherCardAttacks", attacker, attacker.Owner,
                     new[] { "stopAttack", "AttackedAndStopped" },
                     seed: seed,
                     eventArgs: new object?[] { attacker, defender },
                     eventSubject: attacker,
                     namedArgs: named,
                     exclude: attacker))
        {
            hits++;
            if (Blueprint.KismetVm.Truthy(hit.Outs.GetValueOrDefault("stopAttack")))
            {
                stop = true;
            }

            if (Blueprint.KismetVm.Truthy(hit.Outs.GetValueOrDefault("AttackedAndStopped")))
            {
                andStopped = true;
            }
        }

        if (Environment.GetEnvironmentVariable("KLINK_TRACE_T31") == "1")
        {
            Console.Error.WriteLine(
                $"[T31] t={State.Turn} {attacker.Name}#{attacker.CardId} -> {defender.Name}#{defender.CardId} " +
                $"订阅者={hits} stopAttack={stop} AttackedAndStopped={andStopped}");
        }

        return new AttackIntercept(stop, andStopped);
    }

    /// <summary>
    /// 部署链的第二环：<c>BP_CardFunctions::ExecuteOnDeploymentTriggered</c>（24 条语句）。
    ///
    /// <code>
    /// si=5    _triggerMultipleDeployment = 0
    /// si=61   FetchAllCardsWithEventTrigger(23)   ; 23 = OnDeploymentEffectTriggered
    /// si=334  item
    /// si=397  _triggerMultipleDeployment += item.OnDeploymentEffectTriggered_TriggerMultiple
    /// si=443  _triggerMultipleDeployment = ...
    /// si=471  out:triggerMultiple = _triggerMultipleDeployment
    /// </code>
    ///
    /// 调用点 `CardPlayedFromHand` si=5702/5750：**只在 `targetCardID == 0` 时调用**
    /// —— 也就是「**非指向性**部署效果」才算。卡面互证：
    /// `card_unit_b_26_marauder`「Your **non-targeting** deployment effects trigger twice.」
    /// 它的函数体正是 `cardTriggered.side == self.side &amp;&amp; self.IsLocatedOnBoard()`
    /// ⇒ `TriggerMultiple = 1`（于是效果跑 `1 + 1 = 2` 次）。
    /// </summary>
    public int SumDeploymentTriggerMultiple(CardInstance card)
    {
        var seed = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cardTriggered"] = card,
            ["instigatorID"] = card.CardId,
        };

        var named = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cardTriggered"] = card,
            ["instigatorID"] = card.CardId,
        };

        var outs = BroadcastWithOutParam(
            "OnDeploymentEffectTriggered", card, card.Owner, "TriggerMultiple",
            seed: seed, eventArgs: new object?[] { card, card.CardId }, eventSubject: card,
            namedArgs: named);

        int total = 0;
        foreach (var v in outs)
        {
            total += AsInt(v);
        }

        return total;
    }

    /// <summary>
    /// 摧毁链的第二环：**「一个摧毁效果触发了」**（事件 24 <c>OnDestructionEffectTriggered</c>）。
    ///
    /// 出处 <c>out/bp-cardfn.json</c>：
    /// <code>
    /// TriggerDestruction（123 条语句）
    ///   si=905  BooleanAND(Not(CustomName1HasAttribute("StopDestructionEffect")), card.hasDestruction)
    ///   si=965  PopExecutionFlowIfNot(...)                       ; 假 ⇒ 整块跳过
    ///   si=1237 localCardUsedForTriggeringDestructionEffect.OnDestroyed(NoObject{}, true)
    ///   si=1286 ExecuteOnDestructionEffectTriggered(card, instigatorID,
    ///               MakeArray(cardID), localDestructionEffectTriggerCards, false, out TriggerMultiple)
    ///   si=1346 if (TriggerMultiple &gt; 0) { si=1390..1645
    ///               loop Temp_int = 1..TriggerMultiple:
    ///                   si=1515 ExecuteOnDestructionEffectTriggered(...) }   ; ★ 再整轮派发
    ///
    /// ExecuteOnDestructionEffectTriggered（27 条语句）
    ///   si=74..171  loop over DestructionEffectTriggerCards（= FetchAllCardsWithEventTrigger(24)）
    ///   si=276      localCardID_inLoop = item.cardID
    ///   si=325/710  if (!skipSuppressCheck &amp;&amp; cardTriggered.isSuppressed) return
    ///   si=398      contains = CardsToDestroy.Contains(localCardID_inLoop)
    ///   si=458      item.OnDestructionEffectTriggered(cardTriggered, instigatorID, contains, out TriggerMultiple)
    ///   si=530      localTriggerMultiple += TriggerMultiple
    ///   si=604      out:TriggerMultiple = localTriggerMultiple
    /// </code>
    ///
    /// 第 3 个入参（bool）在订阅者函数体里叫 <c>SelfAlsoDestroyed</c> —— 也就是
    /// 「这张订阅卡自己是不是也在被摧毁的那批里」。调用点传的是
    /// <c>MakeArray(被摧毁那张卡的 cardID)</c>，所以它 = 「订阅卡就是被摧毁的卡」。
    /// 卡面互证（4 张订阅者全是这个语义）：
    ///   · `card_unit_matsumoto_regiment`「Deal 1 damage to the enemy HQ when a **Destruction
    ///     effect** triggers.」   —— `BooleanOR(SelfAlsoDestroyed, IsLocatedOnBoard(self))`
    ///   · `card_unit_oita_regiment`「Gets +1+1 when a Destruction effect triggers.」—— 同上
    ///   · `card_unit_114th_infantry_regiment`「When a **friendly** Destruction effect triggers,
    ///     it triggers twice.」   —— `cardTriggered.side == side` ⇒ TriggerMultiple = 1
    ///   · `card_event_japan_duty`「Give your units: "Destruction effects on this unit trigger
    ///     an extra time."」      —— `HasCustomAbilityFromCard(能力名, cardTriggered, …)`
    /// </summary>
    /// <param name="card">触发摧毁效果的那张卡（`cardTriggered`）。</param>
    /// <param name="instigator">摧毁者（`instigatorID` 的来源）。</param>
    /// <returns>订阅者累加出来的 TriggerMultiple —— 调用方要再整轮派发这么多次。</returns>
    public int FireDestructionEffectTriggered(CardInstance card, CardInstance? instigator)
    {
        // si=325/710：skipSuppressCheck=false 且 cardTriggered 被压制 ⇒ 整轮不派发。
        if (card.Keywords.Contains(Keyword.Suppressed))
        {
            return 0;
        }

        var named = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cardTriggered"] = card,
            ["instigatorID"] = instigator?.CardId ?? 0,
        };

        var outs = BroadcastWithOutParam(
            "OnDestructionEffectTriggered", card, card.Owner, "TriggerMultiple",
            eventArgs: new object?[] { card, instigator?.CardId ?? 0, false },
            eventSubject: card,
            namedArgs: named,
            seedFactory: observer => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardTriggered"] = card,
                ["instigatorID"] = instigator?.CardId ?? 0,
                // si=398：CardsToDestroy 就是「被摧毁的那一张」，所以这个 bool 等价于
                // 「订阅卡自己也在被摧毁的那批里」。
                ["SelfAlsoDestroyed"] = observer.CardId == card.CardId,
            });

        int total = 0;
        foreach (var v in outs)
        {
            total += AsInt(v);
        }

        return total;
    }

    /// <summary>
    /// 这张卡被摧毁时，它的**摧毁效果**该不该触发（事件 24 的门）。
    ///
    /// 出处 <c>TriggerDestruction</c> si=905/965：
    /// <c>BooleanAND(Not(CustomName1HasAttribute("StopDestructionEffect")), card.hasDestruction)</c>；
    /// 另有 si=1728 的一条并列分支 <c>HasCustomAbility("destruction")</c>（同一门的另一份实现）。
    ///
    /// ✅ **2026-10-02：`StopDestructionEffect` 那一半补上了。**
    /// 以前这里是 TODO，理由是「`CustomName1*` 三件套内核一个都没进派发表（没有写方）」。
    /// 现在 `CustomName1Add/Remove/HasAttribute` 都进了派发表
    /// （<c>CardApiDispatch.cs</c>，语义出处 `ref/kards-sim/KardsSim/Bridge/EngineHost.cs:2160-2182`），
    /// 写方有了 ⇒ 读方必须一起补，否则那两个写方（`card_event_patrol` i=10、
    /// `card_event_usa_promo2` i=414）**写了也没人看**。
    /// 读的是同一份存储（<see cref="CustomNameHasAttribute"/>），所以两边不会漂。
    /// </summary>
    public bool ShouldTriggerDestructionEffect(CardInstance card)
        => !CustomNameHasAttribute(card, "customName1", "StopDestructionEffect")
           && (card.Keywords.Contains(Keyword.Destruction) || HasCustomAbility(card, "destruction"));

    /// <summary>
    /// `CustomName{1,2}HasAttribute(标记)` 的**静态读法**（引擎内部用，不走派发表）。
    ///
    /// 存储键与派发表里的 <c>SuffixAdd</c>/<c>SuffixHas</c> 完全一致
    /// （<c>customName1</c> / <c>customName2</c>，值是用逗号分隔的标记串）——
    /// 两处**必须**共用同一个读法，否则「谁写了、谁没看见」这种漂移没人能查。
    /// </summary>
    public static bool CustomNameHasAttribute(CardInstance card, string set, string attribute)
        => card.CustomJson.TryGetValue(set, out string? v)
           && v.Split(',', StringSplitOptions.RemoveEmptyEntries)
               .Contains(attribute, StringComparer.Ordinal);

    /// <summary>诊断用：非 null 时记录每一次实际派发（`事件名 → 卡名#ID`）。</summary>
    public List<string>? TriggerTrace { get; set; }

    private List<CardInstance> SnapshotBuffer(int depth)
    {
        while (_triggerBuffers.Count <= depth)
        {
            _triggerBuffers.Add(new List<CardInstance>(16));
        }

        return _triggerBuffers[depth];
    }

    /// <summary>
    /// 触发派发的收件人快照：**双方棋盘 + 弃牌堆 + 手牌**（按 左场/左弃/左手/右场/右弃/右手 顺序）。
    ///
    /// ## 为什么要包含手牌（2026-10-04 修）
    ///
    /// 蓝图里**大量事件程序带着「在手牌里」的分支**，判据是它们自己第一条就调
    /// `IsLocatedInHand()`。全卡池扫描 `card-ir.json`（只算 `entrypoints` 里
    /// `On*` 程序体，复现命令见下）：
    /// <code>
    /// IsLocatedInHand： 42 个触发名 / 90 个 (卡,触发) 对
    ///   例：OnPlayedFromHand 13 / OnCardDrawnFromDeck 6 / OnOtherCardPlayedFromHand 6 /
    ///       OnEndOfTurn 6 / OnOtherCardDestroyed 6 / OnCardSpawnedInHand 5 /
    ///       OnOtherCardEnterPlay 5 / OnStartOfTurn 2 / OnBeforeOtherCardPlayedFromHand 2 /
    ///       OnCounterMeasureTriggered 2 / OnAfterExtraKreditSlotGain（见下）
    /// IsLocatedInDeck： 12 个触发名 / 22 个 (卡,触发) 对
    /// </code>
    /// 只扫「棋盘 + 弃牌堆」时这些分支**永远不会执行** —— 它们是死代码。
    ///
    /// ★ **决定性证据（真实对局 711061 的 ⑤b 首漂开）**：
    /// `card_unit_5th_regiment`（卡面原文「When you lose a kredit slot, this unit gets
    /// +2+1 if on the battlefield or **-2 cost if in hand**.」）的整张卡**只有一个**
    /// 入口 `OnAfterExtraKreditSlotGain`（IR `i=178`），体内两条路：
    /// <code>
    /// i=178  side == sideGaining &amp;&amp; isNegativeGain   （输了槽位、且是自己这一方）
    /// i=268  IsLocatedOnBoard() 为假 ⇒ **jump to i=10**
    /// i=10   IsLocatedInHand() 为真 ⇒ i=110 ChangeKreditCost(self, -2)
    /// </code>
    /// 711061 里 左方 t3 连丢两个槽位（`#10 air_land_sea`、`#12 40th_cavalry_regiment`），
    /// 客户端因此把手里那张 5th_regiment 从 4 费降到 **0 费**；内核一直按 **4 费** 算，
    /// 而此刻池子只有 `kredits=1` ⇒ `#23 t5 PC` 被拒 ⇒ 那张牌留在手里 ⇒
    /// 下游 `#28 t7 ML`、`#41 t9 AC` 接连失败、右方 HQ 少掉 6 点伤害
    /// （④ 人类 HQ 差 10 条全部由这一条派生）。A/B 见 `out/audit/`。
    ///
    /// ⚠️ 顺序要紧：手牌**追加在每一项之后**（左：场→弃→手；右：场→弃→手），
    /// 这样原有的相对先后**逐位不变**，只是往后多了一批收件人。
    ///
    /// ⚠️ **不含牌库**：`IsLocatedInDeck` 只有 22 个 (卡,触发) 对，且回放快照里的
    /// 牌库顺序是宿主用非确定性洗牌造的（README §9.1 第②类），收录它会把
    /// **随机的牌序**变成触发收件人顺序 ⇒ 引入不可复现的差异。留作待确认项。
    /// </summary>
    private void FillTriggerSnapshot(List<CardInstance> snapshot)
    {
        snapshot.Clear();
        for (int i = 0; i < 2; i++)
        {
            Side s = i == 0 ? Side.Left : Side.Right;
            snapshot.AddRange(State.Board(s));
            snapshot.AddRange(State.Discard(s));
            snapshot.AddRange(State.Hand(s));
        }
    }

    /// <summary>
    /// 跑**这张卡自己的**局部函数（IR 的 `locals`，独立 export 的函数图），取回若干出参。
    ///
    /// 与 <see cref="BroadcastWithOutParams"/> 的区别：那条是「广播给所有订阅者」，
    /// 这条是「只问这一张卡」—— `OnCardDealDamage_ModifyDamageDealt` 在蓝图里就是
    /// `_damageDealerCard.OnCardDealDamage_ModifyDamageDealt(…)`（`si=692` 一次直接调用），
    /// 不是 `FetchAllCardsWithEventTrigger` 那种订阅表。
    ///
    /// 用 `FindLocalProgram` 而不是 `FindProgram`：后者会先查 `entrypoints`，
    /// 命中就合成 ubergraph 的分派前导 —— 对带出参的普通函数是错的
    /// （它们根本不在 ubergraph 里，见 P1 §1）。
    ///
    /// 返回 null = **这张卡没有这个函数**（全卡池 1735 张里只有 33 张有
    /// `OnCardDealDamage_ModifyDamageDealt`）。调用方必须把 null 当「默认体」处理，
    /// 不能拿一个猜的默认值顶替。
    /// </summary>
    private IReadOnlyDictionary<string, object?>? RunOwnLocal(
        CardInstance card, string functionName,
        IReadOnlyDictionary<string, object?>? seed, params string[] outNames)
    {
        var program = Blueprint.KismetLibrary.Default?.FindLocalProgram(card.Definition.Name, functionName);
        if (program is null)
        {
            return null;
        }

        if (_triggerDepth >= MaxTriggerDepth)
        {
            Vm.UnsupportedOps["<trigger-depth-limit>"] =
                Vm.UnsupportedOps.GetValueOrDefault("<trigger-depth-limit>") + 1;
            return null;
        }

        var ctx = new EffectContext
        {
            Engine = _engine,
            State = State,
            Self = card,
            Target = seed is not null && seed.TryGetValue("toCard", out var t) ? t as CardInstance : null,
            Controller = card.Owner,
            NamedArgs = EffectContext.EmptyNamedArgsPublic,
        };

        TriggerTrace?.Add($"{functionName}(out {string.Join("/", outNames)}) → {card.Name}#{card.CardId}" +
                          $"（本卡自己的函数体）");

        _triggerDepth++;
        try
        {
            return Vm.RunLocalProgramMulti(program, ctx, seed, outNames);
        }
        finally
        {
            _triggerDepth--;
        }
    }

    private CardInstance? FindOnBoard(string cardName, CardInstance? prefer)
    {        if (prefer is not null && prefer.IsAlive
            && (prefer.Name == cardName || prefer.Definition.Name == cardName))
        {
            return prefer;
        }

        foreach (var side in new[] { Side.Left, Side.Right })
        {
            foreach (var card in State.Board(side))
            {
                if (card.IsAlive && (card.Name == cardName || card.Definition.Name == cardName))
                {
                    return card;
                }
            }
        }

        return null;
    }

    private void RunTriggerProgram(Blueprint.KismetLibrary library, CardInstance card, string cardName,
                                   string programName, CardInstance? trigger,
                                   IReadOnlyList<object?>? eventArgs = null,
                                   CardLocation? goingToLocation = null,
                                   IReadOnlyDictionary<string, object?>? namedArgs = null,
                                   CardLocation? oldLocation = null,
                                   CardLocation? newLocation = null,
                                   IReadOnlyDictionary<string, object?>? localsSeed = null)
    {
        var program = library.FindProgram(cardName, programName);
        if (program is null)
        {
            return;
        }

        // 递归保护：一个效果触发另一个事件、那个事件又触发回来，是很容易出现的环。
        // 超过深度就直接放弃这次派发（真实客户端用「动作队列」串行化，不会无限递归）。
        if (_triggerDepth >= MaxTriggerDepth)
        {
            Vm.UnsupportedOps["<trigger-depth-limit>"] = Vm.UnsupportedOps.GetValueOrDefault("<trigger-depth-limit>") + 1;
            return;
        }

        var ctx = new EffectContext
        {
            Engine = _engine,
            State = State,
            Self = card,
            Target = trigger,
            Trigger = trigger,
            Controller = card.Owner,
            EventArgs = eventArgs ?? Array.Empty<object?>(),
            GoingToLocation = goingToLocation,
            NamedArgs = namedArgs ?? EffectContext.EmptyNamedArgsPublic,
            OldLocation = oldLocation,
            NewLocation = newLocation,
        };

        _triggerDepth++;
        try
        {
            if (localsSeed is null)
            {
                Vm.Run(program, ctx);
            }
            else
            {
                // ★ 卡内私有函数（IR 的 `locals`，例 `OnCounterMeasureTriggered`）的**形参**
                //   只能靠 `seed` 覆盖帧的局部槽：`namedArgs` 那条路只在
                //   `Frame.ResolveEventVar` 里生效，也就是**只认 `K2Node_Event_*` 开头的名字**
                //   （`KismetVm.cs:1102-1202`）。实测：把 `countermeasureTriggering` 放进
                //   `namedArgs` 时 `frame.Get("countermeasureTriggering")` 走的是
                //   "实例变量 → CDO 默认值 → null" 那条路 ⇒ 订阅者读到 **null**
                //   ⇒ `card_unit_the_tigers` 的 `side@countermeasureTriggering` 恒为 0，
                //   判据恒假、+1+1 不发生。
                //
                //   为什么不是 `Vm.Run(program, ctx, seed)`：`Run` 没有 seed 形参，而
                //   `KismetVm.cs` 本轮**不在改动范围内**。`RunLocalProgramMulti` 走同一个
                //   `RunCore`，差别只有步数预算（`MaxStepsPerLocalProgram` = 400000，
                //   **大于**事件预算），所以对事件入口同样安全。
                Vm.RunLocalProgramMulti(program, ctx, localsSeed);
            }
        }
        finally
        {
            _triggerDepth--;
        }
    }

    /// <summary>触发派发的最大嵌套深度。</summary>
    public const int MaxTriggerDepth = 8;

    private int _triggerDepth;



    // ==================================================================
    //  通用操作（被效果脚本使用）
    // ==================================================================

    /// <summary>
    /// **伤害修正链**：`BP_CardFunctions::ExecuteOnDealDamageAddDamage`（46 条语句，
    /// 带出参 `calculatedDamage`）—— 每一次伤害结算**之前**的唯一修正入口。
    ///
    /// 出处 `out/bp-cardfn.json`（`si` = StatementIndex，`Jump/JumpIfNot` 的 `Offset`
    /// 就是目标 `StatementIndex`，1599/1599 已验证）：
    /// <code>
    /// si=0    PushExecutionFlow(1453)                       ; 返回哨兵
    /// si=5    JumpIfNot(_damageDealerCard.isSuppressed) -> 692
    /// si=41       _dealerCalculatedDamage = damage          ; ★ 被压制：**不调** ModifyDamageDealt
    /// si=68   Array_Clear(addDamageToReRun)
    /// si=109  FetchAllCardsWithEventTrigger(37)             ; 37 = OnOtherCardDealDamageAddDamage
    /// si=382      item.OnOtherCardDealDamageAddDamage(_damageDealerCard, _damageRecieverCard,
    ///                     _dealerCalculatedDamage, _fromAttack, _isDefenderDamage,
    ///                     out damageToAdd, out reRunAtEnd)
    /// si=481/527  _dealerCalculatedDamage += damageToAdd
    /// si=554      PopExecutionFlowIfNot(reRunAtEnd)         ; 假 ⇒ 直接 continue
    /// si=623      addDamageToReRun.Add(item)                ; 真 ⇒ 记下来
    /// si=805   loop2 over addDamageToReRun                  ; 整轮再派发一次（出参 reRunAtEnd 不再读）
    /// si=1053     item.OnOtherCardDealDamageAddDamage(…, out damageToAdd, out reRunAtEnd)
    /// si=1198     _dealerCalculatedDamage += damageToAdd
    /// si=1300 Clamp(_dealerCalculatedDamage, 0, 99)  -> out calculatedDamage
    /// si=692  （dealer **没被压制**才到这里）
    ///         _damageDealerCard.OnCardDealDamage_ModifyDamageDealt(_damageRecieverCard, damage,
    ///                     _fromAttack, fromFight, out newDamage)
    /// si=773      _dealerCalculatedDamage = newDamage
    /// si=800  Jump -> 68                                    ; 再进上面那两轮观察者循环
    /// </code>
    ///
    /// ⚠️ **`si=5` 的极性**（容易读反）：`JumpIfNot` 是「条件为假才跳」，
    /// 所以 **没被压制 ⇒ 跳到 692 调 `ModifyDamageDealt`**；被压制 ⇒ 落到 si=41
    /// 直接 `_dealerCalculatedDamage = damage`。也就是「压制 = 关掉这张卡自己的伤害修正」，
    /// 与 `MatchEngine.Attack` 里那条「压制不禁止攻击、只不触发伤害修正」一致。
    ///
    /// ⚠️ 观察者循环**不**受压制影响：`OnOtherCardDealDamageAddDamage` 是**别人**的
    /// 加成（例如 `card_unit_the_rangers`「友方单位造成伤害时 +1」），压制某一张卡
    /// 不该关掉别人的能力。
    ///
    /// 入参顺序来自资产里的 `FunctionExport.LoadedProperties`（`CPF_Parm` 声明序，
    /// 见 `ref/kards-sim/KardsTranspiler/BlueprintSignatures.cs` 的取法）：
    /// <c>_damageDealerCard, _damageRecieverCard, damage, _fromAttack, fromFight,
    /// _isDefenderDamage, out calculatedDamage</c>。
    /// 调用点互证（同名入参在函数体里就是这些裸变量名）：
    /// <code>
    /// DamageCard si=690          (dealer, card, amount, False, fromFight, False)      ; 效果伤害
    /// MakeCardsFight si=303/488  (a, b, dmg, False, True, False)                     ; 互斗
    /// CalculateDamageDealt si=473 (dealer, recv, dmg, True, False, !dealerIsAttacker)
    /// CalculateDamageDealt si=734 (recv, dealer, dmg, True, False, dealerIsAttacker)
    ///   ⇒ 主伤害 isDefenderDamage=False、反击 isDefenderDamage=True（两次调用同结论）
    /// </code>
    ///
    /// 调用点（`out/bp-cardfn.json`）：`CalculateDamageDealt`(si=473/734/4552)、
    /// `DamageCard`(si=690)、`AttackCard`、`MakeCardsFight`、`ApplyDamageToMultipleCards`。
    ///
    /// ⚠️ **本内核的近似（写清楚）**：蓝图里 `AttackCard` 会调两次 `CalculateDamageDealt`
    /// （si=3807 / si=4004，各算两个方向），于是每个方向会被修正 **两次**。
    /// 33 张 `OnCardDealDamage_ModifyDamageDealt` 的函数体**全是纯函数**
    /// （只用 `IsTank`/`IsAirUnit`/`getTotalAttack` 这类只读谓词，见
    /// `out/audit/p1b-vars.py`），所以重复调用幂等、结果相同。
    /// 内核没有 `CalculateDamageDealt` 这一层，`DealDamage` 是唯一漏斗，
    /// 因此**每次伤害实例只修正一次** —— 对纯函数等价，且天然不会重复计副作用。
    /// </summary>
    /// <param name="dealer">造成伤害的那张卡（`_damageDealerCard`）。可为 null（无来源伤害）。</param>
    /// <param name="receiver">挨打的那张卡（`_damageRecieverCard`）。</param>
    /// <param name="damage">修正前的伤害值。</param>
    /// <param name="fromAttack">是不是攻击结算的伤害（`isCombatDamage`）。</param>
    /// <param name="fromFight">是不是「让两个单位互斗」产生的伤害（`MakeCardsFight` 传 true）。</param>
    /// <param name="isDefenderDamage">是不是**防御方的反击**伤害。</param>
    /// <returns>`Clamp(修正后的伤害, 0, 99)`。</returns>
    public int ExecuteOnDealDamageAddDamage(CardInstance? dealer, CardInstance receiver, int damage,
                                            bool fromAttack, bool fromFight, bool isDefenderDamage)
    {
        int calculated = damage;

        // si=5 / si=692：没被压制 ⇒ 问这张卡自己的修正函数；被压制 ⇒ 原样。
        // 入参喂的是**原始** `damage`（si=692 传的是 `damage`，不是累加值）。
        if (dealer is not null && !dealer.Keywords.Contains(Keyword.Suppressed))
        {
            var seed = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["toCard"] = receiver,
                ["damage"] = damage,
                ["fromAttack"] = fromAttack,
                ["fromFight"] = fromFight,
            };

            var outs = RunOwnLocal(dealer, "OnCardDealDamage_ModifyDamageDealt", seed, "newDamage");
            if (outs is not null)
            {
                calculated = AsInt(outs.GetValueOrDefault("newDamage"));
            }
            // outs == null ⇒ 这张卡没有这个函数（全卡池 1735 张里只有 33 张有），
            // 蓝图里 `BlueprintNativeEvent` 的默认体就是「不修改伤害」。
        }

        // si=109：事件 37 的订阅者。**含主体自己**（这一段没有 `NotEqual(item, dealer)`）。
        var first = BroadcastWithOutParams(
            "OnOtherCardDealDamageAddDamage", dealer, receiver.Owner, new[] { "damageToAdd", "reRunAtEnd" },
            eventArgs: new object?[] { dealer, receiver, calculated, fromAttack, isDefenderDamage },
            eventSubject: dealer,
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardDealingDamage"] = dealer,
                ["toCard"] = receiver,
                ["damage"] = calculated,
                ["fromAttack"] = fromAttack,
                ["isDefenderDamage"] = isDefenderDamage,
            },
            seedFactory: _ => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardDealingDamage"] = dealer,
                ["toCard"] = receiver,
                ["damage"] = calculated,
                ["fromAttack"] = fromAttack,
                ["isDefenderDamage"] = isDefenderDamage,
            });

        var reRun = new List<CardInstance>();
        foreach (var (card, outs) in first)
        {
            calculated += AsInt(outs.GetValueOrDefault("damageToAdd"));
            if (Blueprint.KismetVm.Truthy(outs.GetValueOrDefault("reRunAtEnd")))
            {
                // si=623：记的是**卡**（`Array_Add(addDamageToReRun, item)`），不是值。
                reRun.Add(card);
            }
        }

        // si=805..1295：对 reRunAtEnd 的那批再整轮派发一次（第二次不再看 reRunAtEnd）。
        if (reRun.Count > 0)
        {
            var second = BroadcastWithOutParams(
                "OnOtherCardDealDamageAddDamage", dealer, receiver.Owner, new[] { "damageToAdd", "reRunAtEnd" },
                eventArgs: new object?[] { dealer, receiver, calculated, fromAttack, isDefenderDamage },
                eventSubject: dealer,
                namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["cardDealingDamage"] = dealer,
                    ["toCard"] = receiver,
                    ["damage"] = calculated,
                    ["fromAttack"] = fromAttack,
                    ["isDefenderDamage"] = isDefenderDamage,
                },
                only: reRun);
            foreach (var (_, outs) in second)
            {
                calculated += AsInt(outs.GetValueOrDefault("damageToAdd"));
            }
        }

        // si=1300：Clamp(.., 0, 99)
        return Math.Clamp(calculated, 0, 99);
    }

    /// <summary>「造成伤害。所有伤害都走这里，保证事件顺序一致。」</summary>
    /// <param name="isCombatDamage">
    /// 是不是战斗伤害（攻击结算）。判据来自蓝图：`ExecuteAttackCard` 调
    /// `ExecuteOnCardDealDamageEffects(…, isCombatDamage=True, …)` 两次
    /// （si=3363 防守方受伤、si=3451 攻击方反击受伤），
    /// 而 `ApplyDamageToCard` si=1808 / `ApplyDamageToMultipleCards` si=4933
    /// 都是 `False` —— 也就是"效果伤害"。
    ///
    /// ⚠️ 这个标志同时是**重甲减伤的唯一判据**（`MatchEngine.ApplyDamage`）：
    /// 蓝图里重甲只长在 `CalculateDamageDealt` 里，而那个函数只被攻击链调用；
    /// 效果伤害走裸减的 `ApplyDamageToCard`（`g.cs:1053-1057`）。
    /// 漏传它 = 这笔效果伤害被重甲白白吃掉几点。
    /// </param>
    /// <param name="counterDamage">是不是反击伤害（`ExecuteAttackCard` si=3451 传 True）。</param>
    /// <param name="fromFight">
    /// 是不是「两个单位互斗」（`MakeCardsFight` 传 True）产生的伤害。
    /// 出处 `MakeCardsFight` si=303/488：`ExecuteOnDealDamageAddDamage(a, b, dmg, False, True, False)`。
    /// </param>
    public void DealDamage(CardInstance target, int amount, CardInstance? source,
                           bool isCombatDamage = false, bool counterDamage = false, bool isRedirected = false,
                           bool fromFight = false)
    {
        if (amount <= 0 || !target.IsAlive)
        {
            return;
        }

        // ---- 伤害修正链（P1，2026-09-30）----
        //
        // 位置与蓝图一致：`ExecuteOnDealDamageAddDamage` 在**伤害真正落地之前**跑
        // （`DamageCard` si=690 → si=190 `ExecuteOnDealDamageAddDamageAfterCalc` → si=251
        //  `ApplyDamageToCard`），所以这里放在 `ApplyDamage` 之前，
        // 后面的事件/ZAction 自然带的就是**修正后**的值。
        //
        // ⚠️ `isRedirected` 为 true 时**整条修正链跳过** —— 蓝图 `DamageCard`
        // si=149 `JumpIfNot(isRedirected) -> si=690`：重定向伤害（把伤害原样转给另一张卡）
        // 不再问一遍修正者，否则同一笔伤害会被加两次。
        if (!isRedirected)
        {
            amount = ExecuteOnDealDamageAddDamage(source, target, amount,
                                                  isCombatDamage, fromFight, counterDamage);
        }

        ApplyCalculatedDamage(target, amount, source, isCombatDamage, counterDamage, isRedirected);
    }

    /// <summary>
    /// 「伤害值**已经**算好了，直接落地」—— 只给 <c>MakeCardsFight</c>（互斗）用。
    ///
    /// ## 为什么必须把它从 <see cref="DealDamage"/> 里拆出来（2026-10-02）
    ///
    /// 蓝图 `BP_CardFunctions::MakeCardsFight`（`ref/kards-sim/KardsSim/Generated/
    /// BP_CardFunctions.g.cs:25907-26040`）的执行序是**先算完两个方向、再依次落地**：
    /// <code>
    /// si=6A   _damage_to_enemy_unit = getTotalAttack(unitThisSide)      ← 攻击值快照
    /// si=AE   _damage_to_my_unit    = getTotalAttack(unitOppositeSide)  ← 也在落地前
    /// si=12F  ExecuteOnDealDamageAddDamage(a→b, …, False, True, False)  ← 方向 1 的修正链
    /// si=164  ExecuteOnDealDamageAddDamageAfterCalc(…)
    /// si=1E8  ExecuteOnDealDamageAddDamage(b→a, …, False, True, False)  ← 方向 2 的修正链
    /// si=21D  ExecuteOnDealDamageAddDamageAfterCalc(…)
    /// si=26E  ApplyDamageToCard(b ← a, _final_damage_to_enemy_unit, False, False)   ← 才开始扣血
    /// si=299  ApplyDamageToCard(a ← b, _final_damage_to_my_unit,    False, True)
    /// </code>
    /// 也就是说：**方向 2 的伤害修正链看到的是"两笔伤害都没落地"的场面**。
    /// 内核的 <see cref="DealDamage"/> 把「修正链 + 落地」焊在一起，连调两次会让
    /// 方向 2 的修正者（例如 `OnOtherCardDealDamageAddDamage` 里数友方单位的那种）
    /// 看见方向 1 已经打死的尸体 ⇒ 数值可能与客户端不同。
    ///
    /// 所以这里只做**相位拆分**：修正链仍走
    /// <see cref="ExecuteOnDealDamageAddDamage"/>（同一个漏斗，重甲（仅战斗伤害）/ 免疫 /
    /// `OnCardDealDamage_ModifyDamageDealt` / `OnOtherCardDealDamageAddDamage` /
    /// `ZActionDamageCard` / `OnReceiveDamage` / `OnCardDealDamage` 一条不少），
    /// **没有第二套伤害结算**。
    ///
    /// ⚠️ `GetPassiveDefenseBuff`（被动防 buff）内核**没有实现**，也不打算在这里顶替：
    ///    它在蓝图里与重甲同块，但判据是另一个标志 `applyBeforeAttackBuffs`，
    ///    而真实攻击结算的两个调用点都传 False（证据见 `MatchEngine.ApplyDamage`）。
    ///
    /// ⚠️ 本方法**故意不加** `amount &gt; 0` 门 —— <see cref="DealDamage"/> 原先就没有
    /// 在修正链之后再判一次，加门会改变既有调用方的行为（修正链夹到 0 时那笔
    /// `ZActionDamageCard` 现在会发、加门后就不发了）。是否跳过零伤害由调用方决定。
    /// </summary>
    internal void ApplyCalculatedDamage(CardInstance target, int amount, CardInstance? source,
                                        bool isCombatDamage, bool counterDamage, bool isRedirected)
    {
        // ⚠️ `isCombatDamage` 必须传下去：重甲减伤**只对战斗伤害生效**，
        //    判据与证据链见 `MatchEngine.ApplyDamage` 里那段长注释
        //    （全库 `getTotalHeavyArmor` 的减伤只长在 `CalculateDamageDealt` 里，
        //      而它只被攻击链调用；效果伤害走的是裸减的 `ApplyDamageToCard`）。
        //    `Attack` 传 true（`MatchEngine.cs:1569` 主伤害 / `:1572` 反击），
        //    `DamageCard` / `DamageMultipleCards` / `MakeCardsFight` 都传 false。
        int applied = _engine.ApplyDamage(target, amount, source, isCombatDamage: isCombatDamage);

        // ⚠️ 2026-09-30：`damage` 填的是**修正后**的值、`oldDefense` 填的是**结算前**的防御。
        //    证据（`out/bp-cardfn.json` → `ApplyDamageToCard`，123 条语句）：
        //      si=62   oldDefense = card.getTotalDefense()        ; 扣血**之前**取
        //      si=1431 NotifyDamageCard(toCardID, finalDamage, damageDealerID, _isDestroyed,
        //                               oldLocation, oldDefense, isRedirected, isFightDefenderDamage)
        //    两个字段在 si=1431 的实参名就是 `finalDamage`（= `ExecuteOnDealDamageAddDamageAfterCalc`
        //    的出参）与 `oldDefense`。旧实现两个都填**请求值**（P1 重甲那轮记的「没证据」现在有证据了）。
        //    `target.Defense + applied` 而不是 `+ amount`：重甲减伤之后 `applied < amount`，
        //    用 amount 会把 oldDefense 算高（那是重甲那轮留下的偏差，这里一并修正）。
        _engine.FireSubAction("ZActionDamageCard", new[]
        {
            ActionValue2.Int("cardID", target.CardId),
            ActionValue2.Int("damage", amount),
            ActionValue2.Int("oldDefense", target.Defense + applied),
            ActionValue2.Bool("destroyed", target.Defense <= 0),
            ActionValue2.Int("attackerCardID", source?.CardId ?? 0),
        });

        FireTrigger("OnReceiveDamage", target, target.Owner, "OnOtherCardReceiveDamage");

        // ---- 「造成伤害」事件 ----
        //
        // 出处（`out/bp-cardfn.json`，函数 `ExecuteOnCardDealDamageEffects`，
        // 调用方 i=1808 / i=4933 / i=3363 / i=3451）：
        //   i=541  FetchAllCardsWithEventTrigger(36)      ; 36 = OnOtherCardDealDamage
        //   i=1582 item.OnOtherCardDealDamage(damageDealer, toCard, damage, isCombatDamage, CounterDamage, isRedirected)
        //   i=1198 damageDealer.OnCardDealDamage(toCard, damage, isCombatDamage, CounterDamage, isRedirected, out qqq)
        // 触发号 36 的依据：`ERegisteredCardFunction.h` 逐项数下来
        //   0=NotAvailable … 36=OnOtherCardDealDamage（第 37 项）。
        if (source is not null && source.IsAlive)
        {
            var named = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardDealingDamage"] = source,
                ["toCard"] = target,
                ["Damage"] = amount,
                ["damage"] = amount,
                ["isCombatDamage"] = isCombatDamage,
                ["CounterDamage"] = counterDamage,
                ["isRedirected"] = isRedirected,
            };

            FireTrigger("OnCardDealDamage", source, source.Owner, "OnOtherCardDealDamage",
                eventArgs: new object?[] { source, target, amount, isCombatDamage, counterDamage, isRedirected },
                eventSubject: source, namedArgs: named);
        }

        if (target.Defense <= 0 && !target.IsHq)
        {
            _engine.Destroy(target, source);
        }
    }

    /// <summary>
    /// 「在战斗里活下来了」—— 对应 `BP_CardFunctions::ExecuteOnSurvivedCombatEvents`
    /// （`out/bp-cardfn.json`，25 条语句；9 张订阅 `OnSurvivedCombat`、6 张订阅
    /// `OnOtherCardSurvivedCombat`）：
    /// <code>
    /// si=38   JumpIfNot(cardSurviving.isSuppressed) -> si=549   ; 被压制 ⇒ 不广播
    /// si=74   FetchAllCardsWithEventTrigger(59)                ; 59 = OnOtherCardSurvivedCombat
    /// si=343      NotEqual_IntInt(item.cardID, cardSurviving.cardID)   ; 广播**排除自己**
    /// si=494      item.OnOtherCardSurvivedCombat(cardSurviving, cardCombatted)
    /// si=549  cardSurviving.OnSurvivedCombat(cardCombatted)     ; ★ 自己：压制与否都发
    /// </code>
    /// 调用点（同一份 dump，`ExecuteAttackCard`）：si=3201 `(attacker, defender)`、
    /// si=3249 `(defender, attacker)`，两者的门都是"**没被摧毁**"
    /// （si=3186/3234 `JumpIfNot(xDestroyed)`），且整段在
    /// si=3130 `IsUnit(defender)` 的 `PopExecutionFlowIfNot`（si=3171）之内 ——
    /// 也就是**打 HQ 不触发这一族**。
    ///
    /// ⚠️ **本内核的近似**：蓝图先算好 `xDestroyed` 再发事件（语句顺序上事件在
    /// "造成伤害"之前），内核没有那套预算，只能在伤害结算**之后**按
    /// `IsAlive` 判"活下来了"。对活下来的卡，事件里的防御力因此已经是打完之后的值。
    /// </summary>
    public void FireSurvivedCombat(CardInstance surviving, CardInstance combatted)
    {
        if (!surviving.Keywords.Contains(Keyword.Suppressed))
        {
            FireTrigger("OnOtherCardSurvivedCombat", surviving, surviving.Owner,
                eventArgs: new object?[] { surviving, combatted },
                eventSubject: surviving,
                namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["cardSurviving"] = surviving,
                    ["cardCombatted"] = combatted,
                });
        }

        FireTrigger("OnSurvivedCombat", surviving, surviving.Owner,
            eventArgs: new object?[] { combatted },
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardCombatted"] = combatted,
            });
    }

    public void HealCard(CardInstance target, int amount)
    {
        if (!target.IsAlive || amount <= 0)
        {
            return;
        }

        int before = target.Defense;
        target.Defense = Math.Min(target.MaxDefense, target.Defense + amount);
        if (target.Defense != before)
        {
            _engine.FireSubAction("ZActionHealCard", new[]
            {
                ActionValue2.Int("cardID", target.CardId),
                ActionValue2.Int("amount", target.Defense - before),
            });
        }

        // 「被完全修复」—— 出处 `out/bp-cardfn.json` 函数 `FullyHealCard`
        // （i=1683 `card.OnFullyRepaired`），签名 `BaseCardObject.h:667`：
        //   `OnFullyRepaired(int32 amount)`
        //   `OnOtherCardFullyRepaired(UBaseCardObject* cardRepaired, int32 amount)`
        // 判据：修完之后防御力**等于上限**（"fully repaired"）。
        if (target.Defense == target.MaxDefense && target.Defense > before)
        {
            FireTrigger("OnFullyRepaired", target, target.Owner, "OnOtherCardFullyRepaired",
                eventArgs: new object?[] { target, target.Defense - before },
                namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["amount"] = target.Defense - before,
                    ["cardRepaired"] = target,
                });
        }
    }

    /// <summary>Blueprint `FullyHealCard`: set defense to max and return healed amount.</summary>
    public int FullyHealCard(CardInstance target)
    {
        if (!target.IsAlive || target.Defense <= 0) return 0;
        int amount = Math.Max(target.MaxDefense - target.Defense, 0);
        if (amount > 0) HealCard(target, amount);
        return amount;
    }

    /// <summary>
    /// 「这张卡能被加 buff 吗」—— 逐字对应 `BP_CardFunctions::CanCardBeBuffed`
    /// （`out/bp-cardfn.json`，30 条语句）：
    /// <code>
    /// si=0    IsUnrevealedCovertCard(Card)
    /// si=41   JumpIfNot(...) -> si=730
    ///         ★ JumpIfNot 是「条件为**假**才跳」⇒ **不是**未揭示的隐蔽卡 ⇒ si=730
    ///           `CanBeBuffed = True`（si=741 Jump 773 Return）
    /// si=55..711  switch(Card.location)      ; ★ 这张表**只对未揭示的隐蔽卡**生效
    ///             0(NotAvailable)                      → si=746  False
    ///             1/2(牌库) 3/4(手牌) 9(牌库)          → si=762  True
    ///             5/6(半场/HQ) 7(前线) 8(弃牌堆)        → si=746  False
    /// si=725  （无匹配）Jump si=773 Return   ; out 参数默认值 False
    /// </code>
    ///
    /// ⚠️⚠️ **2026-09-30 修正（旧实现是错的，而且是个地雷）**：
    /// 旧实现把 `si=55..711` 那张 switch **套在了所有卡上**，于是得出「在场单位 → false」。
    /// 那是把 `si=41` 的分支极性读反了（注释里当时还写着「未揭示的隐蔽卡 ⇒ 可以直接 buff」）。
    /// 三条**同资产内语义自明**的校准点把它钉死（都不是猜）：
    /// <code>
    /// ChangeAttack si=57   JumpIfNot(IsValid(card)) -> si=393              ; 卡**无效**才去报错
    /// GiveSalvage  si=141  JumpIfNot(_card.hasSalvage) -> si=254           ; **没有** salvage 才去发
    /// ChangeAttack si=934  JumpIfNot(EqualEqual(getAndDecryptAttack(card), amount)) -> si=960
    ///                                                                       ; 数值**没变**才提前返回
    /// </code>
    /// 反向读法（真时跳）会让 `ChangeAttack` 只在卡无效时继续、`GiveSalvage` 只给已有 salvage 的卡发。
    /// 完整取证见 `klink bot/docs/CanCardBeBuffed矛盾调查.md`（含 `xr-cardfunctions.bpasm` 与
    /// `ref/kards-sim` 的独立解码交叉验证，偏移 730/746/762/773 三方一致）。
    ///
    /// 调用点（同一份 dump，`CanCardBeBuffed` 出现在这些函数的守位）：
    /// `ChangeAttack` si=71/103、`ChangeDefense` si=48/80、`ChangeKreditCost` si=385/417、
    /// `CustomAbilityAdd` si=99/131，以及 `GiveGuard`/`GiveAlpine`/`GiveAmbush`/`GiveBlitz`/
    /// `GiveBond`/`GiveFury`/`GiveShock`/`GiveSmokescreen` 一族各 si=94/126，
    /// 以及 `GiveAlpineBonus` si=5。
    ///
    /// ⚠️ **本内核里这道门恒为 true**：`IsUnrevealedCovertCard` 需要 Covert 的
    /// 「已揭示 / 未揭示」状态机，而内核只做到 `Keyword.Covert` + `getHasCovert` 的**判据面**
    /// （P1 §2），没有揭示状态 ⇒ 恒假 ⇒ 恒走 `si=730` 那一支。
    /// 位置表保留在下面**不是为了留死代码**，而是等 Covert 状态落地时只改
    /// <see cref="IsUnrevealedCovertCard"/> 一处。
    /// </summary>
    public static bool CanCardBeBuffed(CardInstance card)
    {
        // si=41 `JumpIfNot(IsUnrevealedCovertCard(Card)) -> si=730`（730 = CanBeBuffed = True）
        if (!IsUnrevealedCovertCard(card))
        {
            return true;
        }

        return CanUnrevealedCovertBeBuffed(card.Location);
    }

    /// <summary>
    /// `CanCardBeBuffed` 的**位置表本体**（`si=55..711` 那 10 个
    /// `NotEqual_ByteByte(location, N)` + `JumpIfNot`，落点 si=746/762）。
    ///
    /// 单独抽出来有两个理由：
    /// 1. 蓝图的形状就是「先过 `IsUnrevealedCovertCard` 门，再查这张表」；
    /// 2. 本内核拿不到「未揭示的隐蔽卡」，这张表在 `CanCardBeBuffed` 上**不可达** ——
    ///    抽出来才能让自测**逐条**核对它（否则就是一段没人验的死代码）。
    /// </summary>
    public static bool CanUnrevealedCovertBeBuffed(CardLocation location) => location switch
    {
        CardLocation.DeckLeft or CardLocation.DeckRight => true,   // 1 / 2  → si=762
        CardLocation.HandLeft or CardLocation.HandRight => true,   // 3 / 4  → si=762
        CardLocation.Deck => true,                                 // 9      → si=762
        _ => false,                                                // 0 / 5 / 6 / 7 / 8 → si=746
    };

    /// <summary>
    /// `UBaseCardObject::IsUnrevealedCovertCard`（`CanCardBeBuffed` si=0 读的谓词）。
    ///
    /// ⚠️ **恒 false，如实说：内核没有建模「隐蔽卡的已揭示/未揭示」状态**。
    /// P1 §2 只打通了 `Keyword.Covert` 常量 + `getHasCovert` / 成员读 `hasCovert`
    /// 这一层**判据面**（11 张卡），揭示状态机（`IsUnrevealedCovertCard` 的真判据、
    /// 以及揭示时机）整条都还没做。
    ///
    /// 单独抽成函数而不是在门里写 `if (true)`：这样 Covert 状态落地时只改这一处，
    /// 而且调用方（`CanCardBeBuffed`）的形状与蓝图逐字一致。
    /// </summary>
    public static bool IsUnrevealedCovertCard(CardInstance card) => false;

    /// <summary>
    /// **山地（Alpine）加成** —— `BP_CardFunctions::GiveAlpineBonus`（39 条语句）。
    ///
    /// 出处 `out/bp-cardfn.json`（`si` = StatementIndex；`Jump`/`JumpIfNot` 的 `Offset`
    /// 就是目标 `StatementIndex`）：
    /// <code>
    /// si=0     PushExecutionFlow(1175)                       ; 返回哨兵
    /// si=5     CanCardBeBuffed(card, out CanBeBuffed)
    /// si=37    PopExecutionFlowIfNot(CanBeBuffed)            ; 假 ⇒ 返回
    /// si=47/88/129  card.getHasAlpine / card.IsLocatedOnBoard / card.getTotalDefense
    /// si=170..242   and = IsLocatedOnBoard && totalDefense &gt; 0 && hasAlpine
    /// si=280   PopExecutionFlowIfNot(and)                    ; 假 ⇒ 返回
    /// si=290   GetAllUnitsOnBoard(includeCovertCards=False, out cards)
    /// si=314..457   for (i = 0; i &lt; len; i++)
    /// si=562..761       item.getHasAlpine && item != card && item.side == card.side
    /// si=799            PopExecutionFlowIfNot(…)             ; 假 ⇒ continue
    /// si=809            bonus += 1
    /// si=906   if (bonus &gt; 0)
    /// si=950       ChangeAttack (card, card.cardID, bonus, changeType=1, False, out)
    /// si=1025      ChangeDefense(card, card.cardID, bonus, changeType=1, False, out)
    /// </code>
    /// 规则一句话：**带 Alpine 的单位在场上且防御 &gt; 0 时，得到 +N/+N，
    /// N = 场上其它同阵营 Alpine 单位的数量**。
    ///
    /// ⚠️ **`changeType = 1` 是「加」不是「设」** —— `ChangeAttack` 里那张
    /// `K2Node_SwitchEnum`（si=584/629/674/719/764/809 六次 `NotEqual_ByteByte(localChangeType, N)`）
    /// 的 **case 1** 落到 si=1224 那一支：si=1325 `Add_IntInt(getAndDecryptAttack(card), localInputAmount)`
    /// ⇒ 加法，si=1371 再 `Clamp(.., 0, 99)`。
    /// ⇒ **这个函数只能在一个单位「刚进场」那一刻调一次**，重复调会叠加。
    ///
    /// ⚠️ **调用点只有 5 个，全在「单位进入战场」的路径上**（`GiveAlpineBonus` 不在派发表里、
    /// `card-ir.json` 里调用它的卡 = **0** —— 它是引擎内函数，得由内核在部署流程里主动调）：
    /// <code>
    /// SpawnCardToBoard              si=872    ↔ 内核 CardApi.SpawnOnBattlefield
    /// SpawnMultipleCardsOnBattlefield si=2968 ↔ 内核没有；IR 里也没有调用点
    /// PlayCardFromHand              si=2207   ↔ 内核 MatchEngine.PlayCard 那条链
    /// PlayCardDirectlyFromHand      si=3053   ↔ 内核没有；IR 里 2 个调用点 ⇒ 记 Unimplemented
    /// AfterWaitCardPlayFromHand     si=974    ↔ 内核没有；IR 里 0 个调用点
    /// </code>
    /// `PlayCardFromHand` 那一路还有个互斥门：si=2147 `GameStateRef.GetExecuteWaitPlayFromHand`
    /// → si=2192 `JumpIfNot → 2207`，也就是「**没走 wait 路径**才在这里给加成」；
    /// 走 wait 路径的由 `AfterWaitCardPlayFromHand` si=974 自己给。两条互斥，不会双给。
    ///
    /// ⚠️ **「位置必须先设好」这一条核实过**（任务书点名要确认的那一步）：
    /// `SpawnCardToBoard` 里 si=872 排在 si=895 `SetSet` / si=919 `RefreshLocationStatus`
    /// **之前**，但那两条不是设这张卡自己的位置（si=895 建的是一个 location 集合，
    /// si=919 拿它去 `RearrangeLocation`/`UpdateGuarded`）。真正设位置的是
    /// si=1838 `InjectCardIntoLocation` → `SetCardLocationAndLocNumber` si=94
    /// `tmpCardToSet.location = Location`，而 si=1838 在 si=872 **之前**可达
    /// （si=1838 → si=1879 `Jump` → si=496 `GetCardFromID` → … → si=734 → si=872）。
    /// ⇒ 到 si=872 那一刻 `IsLocatedOnBoard` **已经是真**。内核在 `State.Move` 之后调，同序。
    ///
    /// ⚠️ **已知近似（写清楚，别当成完整实现）**：蓝图里没有 `RemoveAlpineBonus` 这种东西，
    /// 5 个调用点全在进场路径上 ⇒ 这是**进场那一刻的一次性快照**：
    /// 之后友方 Alpine 单位再进场 / 离场都不会重算已有的加成。
    /// 内核照蓝图实现，**不自己发明"离场重算"**（那需要另一条证据）。
    /// </summary>
    public void GiveAlpineBonus(CardInstance card)
    {
        // si=5 / si=37。修好极性之后这道门在本内核**恒真**（见 CanCardBeBuffed 的注释），
        // 照蓝图留着：Covert 的揭示状态一落地，它就会真的开始挡人。
        if (!CanCardBeBuffed(card))
        {
            return;
        }

        // si=47..280：hasAlpine && 在场上 && getTotalDefense > 0
        if (!card.Keywords.Contains(Keyword.Alpine) || !card.Location.IsBoard() || card.Defense <= 0)
        {
            return;
        }

        // si=290..905：数「场上**其它**同阵营 Alpine 单位」
        // （`GetAllUnitsOnBoard(includeCovertCards=False)` —— 内核的
        //  `GetAllUnitsOnBoard()` 就是"棋盘上的非 HQ 卡"，Covert 未建模）
        int bonus = 0;
        foreach (var item in GetAllUnitsOnBoard())
        {
            if (!ReferenceEquals(item, card)
                && item.Keywords.Contains(Keyword.Alpine)
                && item.Owner == card.Owner)
            {
                bonus++;
            }
        }

        // si=906 / si=940
        if (bonus <= 0)
        {
            return;
        }

        // si=950 / si=1025：changeType=1（加），instigatorID = 这张卡自己的 cardID
        ChangeAttack(card, bonus, card);
        ChangeDefense(card, bonus, card);
    }

    public void ChangeAttack(CardInstance target, int delta, CardInstance? source, int duration = -1,
                             bool temporary = false)
    {
        if (!target.IsAlive || delta == 0)
        {
            return;
        }

        target.Attack = Math.Max(0, target.Attack + delta);
        if (source is not null)
        {
            var buff = GetOrCreateBuff(target, source.CardId, temporary);
            buff.Attack += delta;
            buff.Duration = duration;
        }

        _engine.FireSubAction("ZActionGainAttack", new[]
        {
            ActionValue2.Int("instigatorID", source?.CardId ?? target.CardId),
            ActionValue2.Int("gained", delta),
            ActionValue2.Int("newAttackValue", target.Attack),
        });

        // 「攻击力变了」事件 —— 出处 `out/bp-cardfn.json` 函数
        // `ExecuteAfterChangeAttackEvents`（签名 `BaseCardObject.h:829/799`）：
        //   `OnAfterChangeAttack(int32 changedAmount, int32 instigatorID)`（自己）
        //   `OnAfterOtherCardChangeAttack(UBaseCardObject* cardChanged, int32 changedAmount, int32 instigatorID)`
        FireTrigger("OnAfterChangeAttack", target, target.Owner, "OnAfterOtherCardChangeAttack",
            eventArgs: new object?[] { target, delta, source?.CardId ?? target.CardId },
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["changedAmount"] = delta,
                ["instigatorID"] = source?.CardId ?? target.CardId,
                ["cardChanged"] = target,
            });
    }

    public void ChangeDefense(CardInstance target, int delta, CardInstance? source, bool temporary = false)
        => ApplyDefenseDelta(target, delta, source, temporary, fireGainDefenseEvent: true);

    /// <summary>
    /// 防御力增减的**共同实现**。
    ///
    /// ⚠️ <paramref name="fireGainDefenseEvent"/> 这条开关是**蓝图的分支互斥**要求的
    ///（2026-10-02 修）：蓝图 `BP_CardFunctions.g.cs` 的 `ChangeDefense` 里，
    /// **T6 `OnAfterOtherCardDefenseIsSet`** 在 `changeType == 2`（`SetValue`）那条分支
    ///（`:7721-7727`），而 **T7 `OnAfterOtherCardGainDefense`** 在**另一条**分支
    ///（`:7954-7956`，且带 `!cardToChangeRef.isSuppressed` 门）—— **两条互斥**。
    /// 而内核旧实现的 `SetDefenseValue` 先调 `ChangeDefense`（于是发了 T7）
    /// 再自己发 T6 ⇒ **每次「设为某值」都多发一次 T7**（T7 有 8 张订阅）。
    /// ⇒ `SetValue` 路径必须把 T7 关掉。
    /// </summary>
    private void ApplyDefenseDelta(CardInstance target, int delta, CardInstance? source,
                                   bool temporary, bool fireGainDefenseEvent)
    {
        if (!target.IsAlive || delta == 0)
        {
            return;
        }

        target.Defense += delta;
        target.MaxDefense = Math.Max(target.MaxDefense, target.Defense);

        if (source is not null)
        {
            GetOrCreateBuff(target, source.CardId, temporary).Defense += delta;
        }

        _engine.FireSubAction("ZActionGainDefense", new[]
        {
            ActionValue2.Int("instigatorID", source?.CardId ?? target.CardId),
            ActionValue2.Int("gained", delta),
            ActionValue2.Int("newDefenseValue", target.Defense),
        });

        // 「防御力变了」事件 —— 出处 `out/bp-cardfn.json` 函数 `ChangeDefense`
        // （i=4879 `cardToChangeRef.OnAfterGainDefense`、i=1589 `…OnAfterDefenseIsSet`），
        // 签名 `BaseCardObject.h:814/793`：
        //   `OnAfterGainDefense(int32 defenseGained)`（自己）
        //   `OnAfterOtherCardGainDefense(UBaseCardObject* cardGainingDefense, int32 defenseGained)`
        //
        // ⚠️ **只有「增量」那条分支才发**（`SetValue` 分支发 T6，见上）。
        //
        // ★★ **2026-10-04 更正：那道门只管"自己那一路"，T7 广播是两条路都会到的。**
        // 蓝图原文（`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs`）：
        // <code>
        // :7954   if (!cardToChangeRef.isSuppressed) goto L_130F;  ; 未压制 ⇒ 跳去自程序
        // :7956   L_10FD: FetchAllCardsWithEventTrigger(7)         ; ★ T7 广播：压制时直落这里
        // :7974       EqualEqual(cardToChangeRef, item) ⇒ 跳过      ; 广播排除被改的那张卡
        // :8017   L_130F: OnAfterGainDefense(target, …)            ; 自程序
        // :8020           goto L_10FD                              ; ★ 跑完又跳回 T7 那轮循环
        // </code>
        // ⇒ **T7 广播无条件发**（README §9.5 P3 那条"别再改 T7"的警告说的是这件事，
        //   旧实现无条件发广播**是对的**）；错的是它**同时无条件发了自程序**。
        // 旧实现把 self 与 T7 混在一次 `FireTrigger` 里 ⇒ **无法分别设门** ⇒
        // 被压制时多发了一次 `OnAfterGainDefense` 自程序。
        // ⚠️ `OnAfterOtherCardGainDefense` 不以 `OnOther` 开头 ⇒ 必须显式
        //   `broadcastName: true`（同一个坑见 `FireTrigger` 的 `broadcastName` 注释）。
        // ⚠️ 广播排除"被改的那张卡"由 `FireTrigger` 的广播分支**排除主体**天然满足
        //   （subject 就是 target = `cardToChangeRef`，蓝图 `:7974` 同义）。
        if (fireGainDefenseEvent && delta > 0)
        {
            var gainDefenseNamed = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["defenseGained"] = delta,
                ["cardGainingDefense"] = target,
            };
            var gainDefenseArgs = new object?[] { target, delta };

            if (!target.Keywords.Contains(Keyword.Suppressed))
            {
                FireTrigger("OnAfterGainDefense", target, target.Owner,
                    eventArgs: gainDefenseArgs, namedArgs: gainDefenseNamed);
            }

            FireTrigger("OnAfterOtherCardGainDefense", target, target.Owner,
                broadcastName: true,
                eventArgs: gainDefenseArgs, namedArgs: gainDefenseNamed);
        }

        if (target.Defense <= 0 && !target.IsHq)
        {
            _engine.Destroy(target, source);
        }
    }

    /// <summary>
    /// 「防御力被设成某个值」—— 对应蓝图 `ChangeDefense` 的 `SetValue` 分支
    /// （`EChangeType::SetValue`，i=1589 `OnAfterDefenseIsSet`）。
    /// 与 <see cref="ChangeDefense"/> 分开，因为事件名不同。
    ///
    /// ⚠️ **这条路径不发 T7**（`OnAfterGainDefense` / `OnAfterOtherCardGainDefense`）：
    /// 蓝图里 T6 与 T7 在**互斥分支**（见 `ApplyDefenseDelta` 的注释）。旧实现走
    /// `ChangeDefense` 转发 ⇒ 每次「设为某值」多发一次 T7（8 张订阅）。
    /// </summary>
    public void SetDefenseValue(CardInstance target, int value, CardInstance? source)
    {
        ApplyDefenseDelta(target, value - target.Defense, source,
                          temporary: false, fireGainDefenseEvent: false);

        if (!target.IsAlive)
        {
            return;
        }

        FireTrigger("OnAfterDefenseIsSet", target, target.Owner, "OnAfterOtherCardDefenseIsSet",
            eventArgs: new object?[] { target },
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardDefenseSet"] = target,
            });
    }

    public void ChangeKreditCost(CardInstance target, int delta)
    {
        target.KreditCost = Math.Max(0, target.KreditCost + delta);
        _engine.FireSubAction("ZActionSetKreditCost", new[]
        {
            ActionValue2.Int("cardID", target.CardId),
            ActionValue2.Int("kreditCost", target.KreditCost),
        });
    }

    public void ChangeOperationCost(CardInstance target, int delta, CardInstance? source)
        => ChangeOperationCost(target, delta, source?.CardId ?? 0);

    public void ChangeOperationCost(CardInstance target, int delta, int instigatorId)
    {
        target.OperationCost = Math.Max(0, target.OperationCost + delta);
        _engine.FireSubAction("ZActionChangeOperationCost", new[]
        {
            ActionValue2.Int("instigatorID", instigatorId),
            ActionValue2.Int("amount", delta),
        });
    }

    /// <summary>
    /// 「+N 攻击，直到回合结束」（`BP_CardFunctions.AddAttackUntilEndOfTurn`，8 条语句）。
    ///
    /// 蓝图逐字：
    /// <code>
    /// si=0  IsValid(card)                    ; si=1 JumpIfNot → 191 return
    /// si=2  NotEqual_IntInt(attackToAdd, 0)  ; si=3 JumpIfNot → 191 return
    /// si=4  ChangeAttack(card, instigatorID, attackToAdd, changeType=0, silent=False, out)
    /// si=5  GameStateRef.AddBuffsToRemoveEndOfTurn(0 /*buffType*/, instigatorID)
    /// </code>
    ///
    /// 实参形状（6 个独立调用点互证：`defiant_mk_i` / `plan_d` / `blitzkrieg` /
    /// `rally` / `armored_car_home` / `old_hares`，全部是
    /// `(卡, this.cardID, IntConst:N)`）⇒ `[0]` 目标卡、`[1]` 来源、`[2]` 加多少。
    ///
    /// `changeType=0` 是 `EChangeType::tempBuffGive` ⇒ 登记为临时 buff，
    /// 在本方回合结束时由 <see cref="RemoveTemporaryBuffs"/> 撤掉。
    /// </summary>
    public void AddAttackUntilEndOfTurn(CardInstance? target, CardInstance? source, int attackToAdd)
    {
        if (target is null || !target.IsAlive || attackToAdd == 0)
        {
            return;
        }

        // 没有来源就不再登记 buff —— 否则加的值没有任何东西能撤回来。
        // 蓝图的 instigatorID 永远是调用者自己，所以这一支只在异常数据下才走。
        var instigator = source ?? target;
        ChangeAttack(target, attackToAdd, instigator, temporary: true);
    }

    /// <summary>
    /// 撤掉本回合的临时修正 —— `BP_CardFunctions.RemoveBuffsEndOfTurn` 的等价物
    /// （71 条语句：遍历 buffType → instigatorArray → `GetCardFromID` → `RemoveTheBuff`）。
    ///
    /// 蓝图把「待清理」记在 `GameStateRef` 的一张 map 上
    /// （key = `ECardBuffTypes`，value = 带 `CardIDs` 数组的结构），
    /// 内核用 <see cref="CardBuff.Temporary"/> 直接标在 buff 上 —— 两者等价，
    /// 因为清理的粒度就是「(目标卡, 来源) 这一个 buff」。
    ///
    /// 调用点：`MatchEngine.EndTurn` 里 `OnEndOfTurn` 触发**之后**、回合数递增之前。
    /// 顺序有依据 —— 蓝图 `ExecuteEndOfTurnEvents` 先广播 `OnEndOfTurn`，
    /// 卡自己的收尾逻辑跑完才轮到统一清理。
    /// </summary>
    public void RemoveTemporaryBuffs()
    {
        // AllCards 是物化列表（`_byCardId.Values.OrderBy(...).ToList()`），循环里删 buff 安全。
        foreach (var card in State.AllCards)
        {
            // 键里已经带了 `Temporary`，所以这里只挑临时那一半 ——
            // 同一来源的**永久** buff 是另一个槽位，不会被误删。
            var doomed = card.BuffsBySource
                .Where(kv => kv.Key.Temporary)
                .Select(kv => (Key: kv.Key, Buff: kv.Value))
                .ToList();

            if (doomed.Count == 0)
            {
                continue;
            }

            foreach (var (key, buff) in doomed)
            {
                card.BuffsBySource.Remove(key);

                // ⚠️ 必须显式做**逆运算**，不能靠 `RecalculateStats()` ——
                //    那个函数只重算费用/行动费/重甲（见它的注释：「落点：KreditCost、
                //    OperationCost、重甲关键字」），**不动 Attack/Defense**。
                //    而 `ChangeAttack`/`ChangeDefense` 是直接写 `target.Attack += delta`，
                //    所以撤销也必须是对称的 `-=`，否则「+N 直到回合结束」会永远留着。
                if (buff.Attack != 0)
                {
                    card.Attack = Math.Max(0, card.Attack - buff.Attack);
                }

                if (buff.Defense != 0)
                {
                    card.Defense -= buff.Defense;
                    // 临时加的防御撤掉后，MaxDefense 要跟着回落，否则 `IsDamaged`
                    // （`Defense < MaxDefense`）会永远为真。
                    card.MaxDefense = Math.Max(card.Defense, card.Definition.Defense);
                }

                _engine.FireSubAction("ZActionGainAttack", new[]
                {
                    ActionValue2.Int("instigatorID", key.SourceCardId),
                    ActionValue2.Int("gained", -buff.Attack),
                    ActionValue2.Int("newAttackValue", card.Attack),
                });
            }

            card.RecalculateStats();
        }
    }

    /// <summary>增加 kredit 槽位上上限并回满（ZActionChangeKredits 的常见用法）。</summary>
    public void GainKreditSlot(Side side, int count)
    {
        State.AddMaxKredits(side, count);
        State.AddKredits(side, count);
        _engine.FireSubAction("ZActionChangeKredits", new[]
        {
            ActionValue2.Str("side", side.ToWire()),
            ActionValue2.Int("newMaxKredits", State.MaxKredits(side)),
            ActionValue2.Int("newKredits", State.Kredits(side)),
        });

        FireExtraKreditSlotGain(side, count, giver: null);
    }

    /// <summary>
    /// 「额外 kredit 槽位到手」事件 —— 出处 `out/bp-cardfn.json` 函数 `GainKreditSlot`
    /// （i=680）与 `LoseKreditSlot`（i=574），签名 `BaseCardObject.h:817`：
    /// <code>void OnAfterExtraKreditSlotGain(UBaseCardObject* cardGivingKredit,
    ///                                      ESideEnum sideGaining, bool isNegativeGain);</code>
    /// 没有「自己/别人」两个变体 —— 它是**广播给所有订阅者**的（`giver` 作为事件参数）。
    /// </summary>
    public void FireExtraKreditSlotGain(Side side, int count, CardInstance? giver)
        => FireTrigger("OnAfterExtraKreditSlotGain", null, side,
            eventArgs: new object?[] { giver, (int)side, count < 0 },
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardGivingKredit"] = giver,
                ["sideGaining"] = (int)side,
                ["isNegativeGain"] = count < 0,
            });

    public void DrawCards(Side side, int count) => DrawCardsAndCollect(side, count);

    /// <summary>
    /// 抽 <paramref name="count"/> 张牌，**并把抽到的那几张的 cardID 收集起来返回** ——
    /// 对应蓝图 `DrawCardsFromDeckBySide` 的**出参** `cardsIDs`。
    ///
    /// ## 为什么必须单独有一个"收集版"
    ///
    /// 蓝图体（`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:12298-12402`）：
    /// <code>
    /// L["instigatorID"] = args[0]; L["side"] = args[1]; L["numCards"] = args[2];
    /// L["cardSeen"] = args[3];     L["OpponentDraw"] = args[4];
    /// var __out_cardsIDs = args[5].As&lt;Action&lt;Val&gt;&gt;();      // ← 出参（第 5 参）
    /// L["drawDelay"] = args[6];
    /// loop: drawnCard = DrawTopCardFromDeck(side, instigatorID, OpponentDraw, cardSeen, …);
    ///       if (drawnCard &gt; 0) { Array_Add(drawnCards, drawnCard); … }
    /// L_021B: L["cardsIDs"] = GetLocal(L, "drawnCards");     // g.cs:12386
    /// __halt: __out_cardsIDs?.Invoke(L["cardsIDs"]);         // g.cs:12400
    /// </code>
    /// 派发表旧实现只调 `DrawCards(...)` 然后 `return null` ⇒ `KismetVm.cs:669` 那条
    /// 「`result is not null` 才写 out 槽」直接跳过 ⇒ **出参永远是 null**。
    ///
    /// 影响面（`docs/card-ir.json` 扫描，2026-10-03）：**9 张卡**真的读这个出参 ——
    /// `card_event_detailed_recon`（`Array_Length(cardsIDs)` + `Array_Get` 的整段循环）、
    /// `card_event_pact_of_steel`（`Array_Get(cardsIDs, 0)` → `GetCardFromID`）、
    /// `card_event_ijn_akagi`、`card_event_prolonged_siege`、`card_event_spring_offensive`、
    /// `card_event_top_deck_play_test`、`card_unit_289th_gatchina`、
    /// `card_unit_34th_infantry_regiment`、`card_unit_me_bf_109_fin`。
    ///
    /// ## 两个口径
    /// <list type="bullet">
    /// <item>元素是**整数 cardID**，不是卡实例 —— 蓝图的 `drawnCards` 是 `TArray&lt;int&gt;`，
    ///       元素直接喂 `GetCardFromID`（`card_event_pact_of_steel` i=151）。
    ///       口径与 <see cref="GetDeckBySide"/> 一致；返回卡实例会让 `AsInt(实例)=0`。</item>
    /// <item><c>DrawCard</c> 返回 null 的两条路径（牌库空 → 疲劳伤害 / 手牌满 → 烧牌）
    ///       **都不入列**，对应蓝图 `if (drawnCard &gt; 0)` 那道门。
    ///       ⚠️ 已知偏差：蓝图的 `DrawTopCardFromDeck` 在**手牌满**时仍返回 `drawnCardID`
    ///       （`g.cs:12558` 的 `L["drawnCard"] = drawnCardID` 在 `isHandFull` 分支汇合之后），
    ///       也就是"烧掉的那张"也进 `drawnCards`；而 `MatchEngine.DrawCard` 在烧牌时返回 null
    ///       （`MatchEngine.cs:713-726`），这里就收不到。要完全对齐需要引擎给出"被烧的是哪张"，
    ///       本轮**不改** `MatchEngine.cs`（它同时被另一处改动占用）。</item>
    /// </list>
    /// </summary>
    public List<int> DrawCardsAndCollect(Side side, int count)
    {
        var drawn = new List<int>();
        for (int i = 0; i < count; i++)
        {
            if (_engine.DrawCard(side) is { } card)
            {
                drawn.Add(card.CardId);
            }
        }

        return drawn;
    }

    /// <summary>把一张卡生成到手牌（对应 SpawnCardInHandBySide / doSpawnCardInHand）。</summary>
    /// <summary>
    /// T35 `OnOtherCardCreatedAlterCard` —— 「别的卡被生成 / 转化出来了」。
    ///
    /// ## 出处（`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs`，本次逐行复核）
    /// <code>
    /// :10584  NotifyCreateNonVisualCard(Notifier, createdCard)
    /// :10586  if (makeVeteran) → :10588 MakeVeteran(createdCard)     ; 先老兵化
    /// :10590  FetchAllCardsWithEventTrigger(35)                     ; ★ 广播（建卡 + 老兵化之后）
    /// :10608      item.OnOtherCardCreatedAlterCard(createdCard, Val.Of(0))
    /// </code>
    /// 另外两个调用点：`SalvageMultipleUnits` `:33519`、`SpawnMultipleCardsOnBattlefield`
    /// `:35410`，两处第 2 个实参都是 **3**。
    ///
    /// 实参 `(createdCard, method)`，形参名逐字取 `Generated/_index.g.cs:4100`
    /// = `{ "cardPlayed", "method" }`。`method`：`CreateCard` 路径 = **0**。
    ///
    /// ## 为什么必须在**各个 spawn 助手里**显式发
    /// 内核有一条"所有建卡的漏斗"钩子 `GameState.CardCreated`（`MatchEngine` 挂着它），
    /// 但**不能**把 T35 挂上去：`GameState.CreateWithId`（回放装载初始牌库 40+40 张）
    /// 也走那条路 ⇒ 挂上去会让**开局**给 11 张订阅卡各广播一次"有新卡被生成"。
    /// ⇒ 只在**生成类原语**里显式发（`SpawnOnBattlefield` / `SpawnCardInHand` /
    /// `DoSpawnInDeck`；`DoSpawnInFrontline` 复用 `SpawnOnBattlefield`）。
    ///
    /// ⚠️ **11 张订阅卡在现有 22 局回放语料里一张都没出现过** ⇒ 这条修复**无法用回放验证**，
    /// 判据只有"蓝图原文 + 自测"（README §7.4 那类）。如实标注。
    ///
    /// ⚠️ 已知的族级偏差：内核 `FireTrigger` 的广播分支**排除主体**，
    /// 而蓝图 `:10590-10608` 的循环里**没有**排除 `createdCard` 自己
    /// ⇒ 若某张卡自己订阅了 T35，内核会比客户端少发一次。与其它触发点同源，未单独改。
    /// </summary>
    public void FireCardCreatedAlterCard(CardInstance created, int method = 0)
        => FireTrigger("OnOtherCardCreatedAlterCard", created, created.Owner,
            eventArgs: new object?[] { created, method },
            eventSubject: created,
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardPlayed"] = created,
                ["method"] = method,
            });

    /// <summary>
    /// T3 `OnAfterDeckChanged(deckSide)` —— 「牌库变了」。
    ///
    /// ## 蓝图原文（本次逐行复核，`BP_CardFunctions.g.cs`）
    /// <code>
    /// :14456  IsActionProcess → 假则整段跳过
    /// :14467  FetchAllCardsWithEventTrigger(3)
    /// :14494      item.OnAfterDeckChanged(deckSide)     ; 实参只有一个 deckSide，循环内无排除
    /// </code>
    /// 8 个调用方（蓝图）：`AdjustCardPositionInDeck` / `ConvertCard` / `DiscardCardFromDeck` /
    /// `DrawTopCardFromDeck` / `MoveCardToTopOfDeck` / `ShuffleDeckBySide` /
    /// `SpawnCardInDeckBySide` / `DrawSpecificCardFromDeckBySide`。
    /// 内核里可达的是其中 6 个（`AdjustCardPositionInDeck` / `ConvertCard` 未实现）——
    /// 这 6 处都已接上。
    ///
    /// ⚠️ **近似**：蓝图那道 `IsActionProcess` 门内核**没有建模**（全内核一致地当作
    /// "是动作流程"处理，见 `CardApi.cs` 里几处同名注释）⇒ 这里也不加。
    /// ⚠️ 3 张订阅卡（`card_unit_lovat_scouts` / `card_event_betasom` / `card_event_rm_roma`）
    /// 在 22 局语料里 **0 命中** ⇒ **回放侧无信号**，判据是蓝图 + 自测。
    /// </summary>
    public void FireDeckChanged(Side deckSide)
        => FireTrigger("OnAfterDeckChanged", subject: null, deckSide,
            eventArgs: new object?[] { (int)deckSide },
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["deckSide"] = (int)deckSide,
            });

    /// <summary>
    /// T68 `OnOperationKreditsSpent`（自程序）+ `OnOtherCardOperationKreditsSpent`（广播）
    /// —— 「这张卡花掉了行动费（油费）」。
    ///
    /// ## 蓝图原文（本次逐行复核，`BP_CardFunctions.g.cs` 的 `ExecuteOnOperationKreditsSpent`）
    /// <code>
    /// :16743  OnOperationKreditsSpent(cardOperated, kreditsSpent)      ; ★ 自己那一路，**先**
    /// :16745  FetchAllCardsWithEventTrigger(68)
    /// :16772      NotEqual_ObjectObject(item, cardOperated) ⇒ 跳过     ; 排除被操作的卡自己
    /// :16787      item.OnOtherCardOperationKreditsSpent(cardOperated, kreditsSpent)
    /// </code>
    /// 实参名逐字取 `_index.g.cs:1978` = `{ "cardOperated", "kreditsSpent" }`。
    ///
    /// ⚠️ **事件名是复数 `…KreditsSpent`**，而枚举名 `OnOtherCardOperationKreditSpent`
    ///    （`Trigger.g.cs`）是**拼错的单数**、IR 里 **0 个订阅者** —— 必须按蓝图名发。
    ///
    /// 调用方（蓝图）：`AttackCard` `:4637` / `:4651`（攻击付油费）、
    /// `MoveCardToFrontline` `:26998`（移动到前线付油费）。
    /// 内核对应 `MatchEngine` 里**两处** `State.AddKredits(…, -OperationCost)`（移动 / 攻击）。
    ///
    /// ⚠️ **回放侧基本无信号**：3 张订阅卡里只有 `card_unit_2nd_michigan` 出现在语料文件里
    ///   （另两张 0 命中），且它是否真的被花过油费**未核实** ⇒ 判据以「蓝图原文 + 自测」为主。
    /// </summary>
    public void FireOperationKreditsSpent(CardInstance card, int kreditsSpent)
    {
        var args = new object?[] { card, kreditsSpent };
        var named = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cardOperated"] = card,
            ["kreditsSpent"] = kreditsSpent,
        };

        // 自己那一路（蓝图 `:16743`，**先**）。⚠️ 这个名字**不以 `OnOther` 开头**，
        // `FireTrigger` 的命名判据会把它当"只发给主体" —— 正好对上蓝图的自程序。
        FireTrigger("OnOperationKreditsSpent", card, card.Owner,
            eventArgs: args, namedArgs: named);

        // 广播（蓝图 `:16745-:16787`）。"排除被操作的卡自己"由 `OnOther*` 广播分支
        // 排除主体**天然满足**（subject 就是 `card`）。
        FireTrigger("OnOtherCardOperationKreditsSpent", card, card.Owner,
            eventArgs: args, namedArgs: named);
    }

    /// <summary>
    /// `WasLeftMostWhenPlayedFromHandKey` —— 蓝图 `SetRightLeftMostWhenPlayed`（`BP_CardFunctions.g.cs:34463`）
    /// 写入、`WasLeftMostCardWhenPlayedFromHand`（`:37662`）读取的那个 JSON 键。
    ///
    /// ⚠️ 蓝图里它是 CDO 上的一个 **String 成员**，**字面值在本仓库拿不到**
    /// （`cards.full.json` 不在仓库里，见 README §3.2）⇒ 这里用与成员同名的常量。
    /// 写入与读取走同一个常量，**自洽**；这一对之外没有别的消费者，所以字面值不影响行为。
    /// </summary>
    public const string LeftMostWhenPlayedFromHandKey = "WasLeftMostWhenPlayedFromHand";

    /// <inheritdoc cref="LeftMostWhenPlayedFromHandKey"/>
    public const string RightMostWhenPlayedFromHandKey = "WasRightMostWhenPlayedFromHand";

    public CardInstance SpawnCardInHand(Side side, string cardName)
    {
        // ★★ 手牌容量门（2026-10-04）—— 蓝图 `CreateCard` 的原文：
        // `ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs`
        // <code>
        // :10510  IsLocationFull(_location) → :10512 wasFullBeforeCreating
        // :10702  BooleanAND(Not(autoplay &amp;&amp; spawnCardInHand), wasFullBeforeCreating)
        // :10704  if (!that) → 正常
        // :10706      createdCard.location = 8          ; ★ 建卡前手牌就满 ⇒ 直接进弃牌堆
        // </code>
        //
        // ⚠️ **只在这一条路径上补**，不在换区漏斗上一刀切 —— 因为"往手牌加牌"的各条路径
        //    在蓝图里**待遇不同**：`DrawSpecificCardFromDeckBySide`（`:12406`，全函数 33 行）
        //    **根本没有容量门**，`DrawTopCardFromDeck`（`:12496`）与
        //    `MoveCardFromBoardToOwnersHand`（`:26405`）**有**，而内核这两条**都已经实现了**
        //    （`MatchEngine.DrawCard` / `DoMoveUnitFromBoardToOwnersHand`）。
        //    逐条对照后**唯一缺的就是这一条**（实测越界栈：`SpawnCardInHand` ← `DoSpawnInHand`）。
        //
        // ⚠️ 蓝图那个例外（`autoplay &amp;&amp; spawnCardInHand` 时不改送弃牌堆）内核**没有建模**
        //    `autoplay` 这个 gameplay tag ⇒ 这里按"无条件应用"实现，**如实标注为近似**。
        bool handWasFull = State.Hand(side).Count >= GameState.HandCapacity;
        CardLocation where = handWasFull ? CardLocation.Discard : side.HandOf();

        var card = State.Create(cardName, side, where, State.NextLocationNumber(side, where));
        _engine.FireSubAction("ZActionSpawnCard", new[]
        {
            ActionValue2.Int("cardID", card.CardId),
            ActionValue2.Int("location", (int)where),
        });

        // ---- T35 `OnOtherCardCreatedAlterCard` ----
        // 蓝图顺序：`CreateCard`（内含这次广播）**先于** `ExecuteOnSpawnedInHandEvents`
        //（后者 = 下面两条 `OnCardSpawnedInHand` / `OnOtherCardSpawnedInHand`）。
        FireCardCreatedAlterCard(card);

        // ⚠️ **两个事件都要发**，这是本轮的 bug 修复点之一。
        //
        // 出处：`out/bp-cardfn.json` 函数 `ExecuteOnSpawnedInHandEvents(spawnedCardID, spawnedSide)`
        //   i=132  `spawnedCard.OnCardSpawnedInHand()`        ← **自己**（此前从未派发）
        //   i=177  FetchAllCardsWithEventTrigger(57)         ; 57 = OnOtherCardSpawnedInHand
        //   i=750  `item.OnOtherCardSpawnedInHand(spawnedCardID, spawnedSide)`
        //
        // 旧实现只发了 `OnOtherCardSpawnedInHand`，而 `CardApi.FireTrigger` 的
        // `broadcast` 判定（`programName.StartsWith("OnOther")`）会**把主体自己排除**，
        // 于是"刚被生成到手牌的那张卡自己的进场逻辑"永远不跑（21 张卡订阅它）。
        FireTrigger("OnCardSpawnedInHand", card, side,
            eventArgs: new object?[] { (int)side },
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["spawnedSide"] = (int)side,
            });

        FireTrigger("OnOtherCardSpawnedInHand", card, side,
            eventArgs: new object?[] { card.CardId, (int)side },
            eventSubject: card,
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["spawnedCardID"] = card.CardId,
                ["spawnedSide"] = (int)side,
            });
        return card;
    }

    /// <summary>把一张卡生成到战场（对应 `SpawnCardOnBattlefield`）。</summary>
    /// <param name="frontline">
    /// 落在**前线**（true）还是**半场**（false）—— 就是蓝图签名的第 2 个参数 `bool Frontline`
    /// （`CardFunctionsStub.h:68`）。⚠️ 234 个调用点里 **215 个传 `false`**，
    /// 旧实现写死 `BoardFrontline`，等于把这些卡全放错地方。
    /// </param>
    /// <param name="locationNumber">
    /// 指定槽位；`-1`（IR 里 193/234 个调用点）表示"追加到队尾"。
    /// </param>
    public CardInstance SpawnOnBattlefield(Side side, string cardName,
                                           bool frontline = true,
                                           int locationNumber = -1,
                                           bool newGiveBlitz = false,
                                           bool forceGoldCard = false)
    {
        CardLocation where = frontline ? CardLocation.BoardFrontline : side.HqOf();
        var card = State.Create(cardName, side, where, 0, isGold: forceGoldCard);
        card.EnteredPlayOnTurn = State.Turn;

        int slot = locationNumber >= 0
            ? locationNumber
            : State.Cards(side, where).Where(c => c != card).Select(c => c.LocationNumber).DefaultIfEmpty(-1).Max() + 1;
        State.Move(card, where, slot);

        // ★★ 直接生成到**前线**时必须**显式**重算前线归属（2026-10-02 补）。
        //
        // 为什么这里必须显式写一句：`State.Create` 已经把 `Location` 设成 `where` 了，
        // 紧接着的 `State.Move(card, where, slot)` 是**同区移动** ⇒ `GameState.Move`
        // 里 `moved == false` ⇒ **换区钩子（`MatchEngine.FireLocationMoved`）不发**
        // ⇒ `FrontlineOwner` 不会更新。
        //
        // 后果：一张卡把单位**直接生成到空前线**之后，我们这边 `FrontlineOwner`
        // 仍是 `NotAvailable` ⇒ 对面随后可以推进到一个**已经被占**的前线
        // （互斥门形同虚设）。`SpawnCardInFrontline`（108 调用点 / 29 张卡）
        // 走的正是这条路。
        //
        // 蓝图也是**显式**补这一句的，不是靠换区钩子 ——
        // 出处 `ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs`
        // → `SpawnCardToBoard`（本文件 `SpawnOnBattlefield` 对应物）：
        // <code>
        //   L_03AE  ExecuteOnCardLocationMoved(spawnedID, 0, location, False, 12)
        //   L_03D3  EqualEqual_ByteByte(location, 7)
        //   L_03F2  JumpIfNot L_0417                 ; 不是前线 ⇒ 跳过
        //   L_0400  UpdateFrontlineIfNeeded(spawnedID)   ← ★ 就是这一句
        // </code>
        // `SpawnMultipleCardsOnBattlefield`（同文件 L_0AD6）也有一句同样的调用。
        if (where == CardLocation.BoardFrontline)
        {
            _engine.RefreshFrontlineOwner(card);
        }

        if (newGiveBlitz)
        {
            card.Keywords.Add(Keyword.Blitz);
        }

        // ---- 山地加成 ----
        // 出处 `out/bp-cardfn.json` → `SpawnCardToBoard`：
        //   si=734 IsActionProcess → si=757 JumpIfNot 895        ; 不是动作流程 ⇒ 整段跳过
        //   si=785 GiveBlitz
        //   si=817 cardSpawned.getHasAlpine → si=858 JumpIfNot 895 ; 没有 Alpine ⇒ 跳过
        //   si=872 GiveAlpineBonus(cardSpawned)
        //   si=895 SetSet / si=919 RefreshLocationStatus / si=942 ExecuteOnCardLocationMoved
        // 位置在 si=1838 `InjectCardIntoLocation`（→ `SetCardLocationAndLocNumber` si=94
        // `tmpCardToSet.location = Location`）里就已经设好，且 si=1838 在 si=872 之前可达 ——
        // 所以这里放在 `State.Move` **之后**，与蓝图同序（详见 `GiveAlpineBonus` 的注释）。
        GiveAlpineBonus(card);

        _engine.FireSubAction("ZActionSpawnCard", new[]
        {
            ActionValue2.Int("cardID", card.CardId),
            ActionValue2.Int("location", (int)where),
        });

        // ---- T35 `OnOtherCardCreatedAlterCard` ----
        // 蓝图 `CreateCard` `:10590` 的广播在建卡/老兵化之后、`SpawnCardToBoard` 的
        // 落位与后续通知之前；`CreateCard` 本身**带目标位置实参**，所以此处按
        // "卡已在最终位置"发（与蓝图订阅者看到的状态一致）。
        FireCardCreatedAlterCard(card);
        return card;
    }

    public void DestroyCard(CardInstance target, CardInstance? source)
    {
        if (target.IsHq)
        {
            DealDamage(target, target.Defense, source);
            return;
        }

        _engine.Destroy(target, source);
    }

    /// <summary>
    /// 弃一张牌（对应 `BP_CardFunctions::DiscardCardFromHand` / `DiscardCard`）。
    ///
    /// <paramref name="discarder"/> 是"谁弃的"（事件第 2 个入参 `discarderID`）。
    /// 内核大量调用点是"效果让某张牌被弃"，没有明确的施加者，
    /// 这时传 null ⇒ `discarderID = 0`。**这是近似**，不猜一个假的施动者。
    /// </summary>
    public void DiscardCard(CardInstance card, CardInstance? discarder = null)
    {
        if (card.Location == card.Owner.HandOf()
            && State.HasGameplayRestriction(card.Owner,
                GameplayRestrictionType.CannotDiscardAnyCardFromHand))
        {
            return;
        }

        bool suppressed = card.Keywords.Contains(Keyword.Suppressed);
        State.Move(card, CardLocation.Discard);
        _engine.FireSubAction("ZActionDiscardCard", new[]
        {
            ActionValue2.Int("discarderID", discarder?.CardId ?? card.CardId),
        });

        // 「别的卡被弃了」—— 出处 `out/bp-cardfn.json` 函数 `DiscardCardFromHand`
        // （签名 `CardFunctionsStub.h`；9 张订阅者）：
        //   si=500  JumpIfNot(tmpCardToDiscard.isSuppressed) -> si=1236   ; 被压制 ⇒ 不广播
        //   si=536  FetchAllCardsWithEventTrigger(41)
        //   si=809  item.OnOtherCardDiscarded(tmpCardToDiscard, discarderID)
        // 广播集**不排除被弃的那张卡自己**（蓝图里没有 cardID 比对），
        // 但内核 FireTrigger 的 `OnOther*` 约定会排除主体 —— 见 FireTrigger 的注释。
        if (!suppressed)
        {
            FireTrigger("OnOtherCardDiscarded", card, card.Owner,
                eventArgs: new object?[] { card, discarder?.CardId ?? 0 },
                eventSubject: card,
                namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["cardDiscarded"] = card,
                    ["discarderID"] = discarder?.CardId ?? 0,
                });
        }
    }

    public void GiveKeyword(CardInstance target, string keyword)
    {
        target.Keywords.Add(keyword);
        _engine.FireSubAction($"ZActionGive{keyword}", new[]
        {
            ActionValue2.Int("giverID", target.CardId),
        });

        FireAbilitiesChanged(target);
    }

    public void RemoveKeyword(CardInstance target, string keyword)
    {
        if (!target.Keywords.Remove(keyword))
        {
            return;
        }

        // ★ 钉住被摘掉时把剩余回合数清零 —— 蓝图 `RemovePin` i=1273
        //   （`BP_CardFunctions.g.cs:31799`）就是 `card.pinnedTurns = 0`。
        //   不清的话，下一次再被钉住时 `Max(旧值, 3或2)` 会把时长算长。
        if (keyword == Keyword.Pinned)
        {
            target.PinnedTurns = 0;
        }

        // ---- T48 `OnOtherCardLoseSmokescreen` ----
        // 蓝图 `RemoveSmokescreen` `:32691` Fetch(48) → `:32709 item.OnOtherCardLoseSmokescreen(_cardFromID)`
        //（形参名 `card`，`_index.g.cs:4114`）。这一段是那个函数的**最后一段** ——
        // 排在两条 `ChangeBuffsFromCards(…, "smokescreen")` 分支与客户端通知**之后**，
        // 所以这里也放在 `FireSubAction` / `FireAbilitiesChanged` 之后。
        // 上面那道 `Keywords.Remove` 返回假时已经 `return`，天然满足"烟幕真变了才发"。
        // ⚠️ 3 张订阅卡（`card_unit_hirosaki_regiment` / `card_event_shock_attack` /
        // `card_unit_p1y2_kasho`）在 22 局语料里都没出现过 ⇒ **回放侧无信号**，判据是蓝图 + 自测。
        if (keyword == Keyword.Smokescreen)
        {
            FireTrigger("OnOtherCardLoseSmokescreen", target, target.Owner,
                eventArgs: new object?[] { target },
                eventSubject: target,
                namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["card"] = target,
                });
        }

        // ---- T62 `OnOtherUnitUnpinned` ----
        // 蓝图 `RemovePin`（`BP_CardFunctions.g.cs:31799-31847`，本次逐行复核）：
        // <code>
        // :31799  card.pinnedTurns = 0                 ; ★ 内核这里早就实现了（上面那一段）
        // :31801  IsActionProcess → 假则跳过（内核未建模，一致近似）
        // :31816  NotifyUnpinUnit(Notifier, cardID)
        // :31818  FetchAllCardsWithEventTrigger(62)
        // :31847      item.OnOtherUnitUnpinned(card)   ; 实参 = 被解除钉住的那张卡
        // </code>
        // ⚠️ **事件名是小写 p 的 `OnOtherUnitUnpinned`**（IR 里 3 个订阅者）；
        //   枚举名 `OnOtherUnitUnPinned`（`Trigger.g.cs`，大写 P）在 IR 里 **0 个订阅者**
        //   —— 按枚举名查会得 0，必须按蓝图名发。
        // ⚠️ 3 张订阅卡（`card_event_creeping_barrage` / `card_event_forced_surrender` /
        //   `card_unit_14_panzergrenadier`）在 22 局语料里 **0 命中** ⇒ **回放侧无信号**。
        if (keyword == Keyword.Pinned)
        {
            FireTrigger("OnOtherUnitUnpinned", target, target.Owner,
                eventArgs: new object?[] { target },
                eventSubject: target,
                namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["card"] = target,
                });
        }

        _engine.FireSubAction($"ZActionRemove{keyword}", new[]
        {
            ActionValue2.Int("giverID", target.CardId),
        });

        FireAbilitiesChanged(target);
    }

    /// <summary>
    /// 钉住一个单位（对应 `BP_CardFunctions::PinUnit`，`BP_CardFunctions.g.cs:27852`）。
    ///
    /// 与 <see cref="SuppressUnit"/> 同构：加关键字 + **记下时长**。
    /// 时长照 `PinUnit` i=955（`BP_CardFunctions.g.cs:27957-27964`）：
    /// <code>
    ///   IsSideActive(_card, _card.side) -> active
    ///   SelectInt(3, 2, active)          ; 真取 3、假取 2
    ///   pinnedTurns = Max(pinnedTurns, 那个值)
    /// </code>
    /// 即"被钉住的那张卡**自己那一方**是否正在行动"。配上
    /// `DecrementPinnedTurnsEndTurn`（每个回合结束减 1）正好等于权威规则表的
    /// 「于单位所有者**下个回合结束时**移除」：
    /// · 我回合钉**敌方** → 2 次回合结束（我的、他的）⇒ 他的回合结束解除；
    /// · 我回合钉**自己** → 3 次回合结束（我的、他的、我的）⇒ 我的下回合结束解除。
    ///
    /// ⚠️ 蓝图在这之后还有三条守卫（`i=99` `cantBePinned` / `i=215` `IsLocatedOnBoard`
    ///    / `i=266` `IsUnit`），命中就**只写 `pinnedTurns`、不加关键字**。内核这里
    ///    **没有**实现那三条守卫（是另一个独立的缺口，不在本次改动范围内，如实记录）。
    /// </summary>
    public void PinUnit(CardInstance target)
    {
        GiveKeyword(target, Keyword.Pinned);

        int turns = IsSideActive(target.Owner) ? 3 : 2;
        target.PinnedTurns = Math.Max(target.PinnedTurns, turns);

        // ---- T61 `OnOtherUnitPinned` ----
        // 蓝图 `PinUnit` `:27980 NotifyPinUnit` → `:27982 Fetch(61)` → `:28010 item.OnOtherUnitPinned(_card)`
        //（形参名 `cardBeingPinned`，`_index.g.cs:4129`）。
        // 时机：`pinnedTurns` 写完、客户端通知**之后**。
        // ⚠️ 2 张订阅卡（`card_unit_cromwell_mk_iv` / `card_unit_14_panzergrenadier`）
        // 在 22 局语料里都没出现过 ⇒ **回放侧无信号**，判据是蓝图 + 自测。
        FireTrigger("OnOtherUnitPinned", target, target.Owner,
            eventArgs: new object?[] { target },
            eventSubject: target,
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardBeingPinned"] = target,
            });
    }

    /// <summary>
    /// 「这张卡的能力集变了」—— 出处 `out/bp-cardfn.json` 函数
    /// `ExecuteOnOtherCardsAbilitiesChanged`（i=307），签名 `BaseCardObject.h:643`：
    /// <code>void OnOtherCardAbilitiesChanged(UBaseCardObject* cardChanging);</code>
    /// 只有广播这一种形态（没有"自己"那个变体）。
    /// 调用点（同一份 dump）：`ChangeHeavyArmor`、以及 Give/Remove 关键字一族。
    /// </summary>
    public void FireAbilitiesChanged(CardInstance card)
        => FireTrigger("OnOtherCardAbilitiesChanged", card, card.Owner,
            eventArgs: new object?[] { card },
            eventSubject: card,
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardChanging"] = card,
            });

    /// <summary>
    /// 「抑制」摘除的关键词集 —— 出处 `BP_CardFunctions::SuppressMultipleUnits`
    /// 的 Remove* 链（`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs`）：
    /// <code>
    /// L_054E i=1358  RemoveGuard
    /// L_0582         RemoveFury
    /// L_05B5         RemoveBlitz
    /// L_05F1         RemoveImmune
    /// L_062D         RemoveAlpine
    /// L_0669         RemoveAmbush
    /// L_069D         RemoveMobilize
    /// L_06DA         RemoveSmokescreen
    /// L_070E         ChangeHeavyArmor(卡, 0, 0, changeType=3, skipAction=true) ; 重甲清零
    /// L_073F         RemoveSalvage                                          ; ★
    /// L_077B         RemoveShock
    /// L_07B8         card.hasDestruction = False
    /// </code>
    ///
    /// ⚠️⚠️ **`Salvage`（收缴）在表里 —— 2026-10-02 第二轮改**。上一轮把它排除在外，
    /// 引的是权威规则表 `KARDS基础规则参考.md:125`「抑制**不能**使单位失去
    /// 『压制』『被收缴』『被抑制』『被控制』」。**那条被读错了**：
    /// · `:109-110` 说「**收缴/被收缴**：收缴卡牌消灭敌方单位时，将其 1/1 复制加入手牌
    ///   …… **被收缴单位**与收缴它的卡牌属于同一个国家，属性 1/1，保留原始单位的所有效果」
    ///   ⇒ 「被收缴」指的是**那个 1/1 复制品的状态**（蓝图字段 `isSalvaged`），
    ///   不是"有收缴能力"（蓝图字段 `hasSalvage` = 内核 `Keyword.Salvage`，
    ///   映射见 `KismetVm.cs:1013`）。
    /// · 蓝图确实**没有**碰 `isSalvaged`（它只拿它决定 `_staticAttack/_staticDefense = 1`，
    ///   `:36108-36112`）—— 规则表那句在这个读法下**成立**；
    ///   而它**明确调了** `RemoveSalvage`（`L_073F` → `:35922`），
    ///   `RemoveSalvage` 的实现体第一条就是 `card.hasSalvage = False`（`:32127`）。
    /// ⇒ 「抑制不摘『被收缴』」与「抑制摘掉收缴**能力**」两件事不矛盾，按蓝图实现。
    ///
    /// `Pinned`（压制）**不入表** —— 蓝图整段没有任何 `RemovePin`，
    /// 且玩家（雪雾）明确说过「**压制不会受到抑制的影响**」（`Pin` 与 `Suppress`
    /// 在中文客户端分别译作「压制」与「抑制」，是两个关键字）。
    /// `Suppressed` 自己同理（由本函数自管）。
    /// </summary>
    private static readonly string[] SuppressStrips =
    {
        Keyword.Guard,
        Keyword.Fury,
        Keyword.Blitz,
        Keyword.Immune,
        Keyword.Alpine,
        Keyword.Ambush,
        Keyword.Mobilize,
        Keyword.Smokescreen,
        Keyword.HeavyArmor,
        Keyword.Shock,
        Keyword.Destruction,
        Keyword.Salvage,
    };

    /// <summary>
    /// 抑制一张单位（对应 `BP_CardFunctions::SuppressUnit` → `SuppressMultipleUnits`，
    /// 完整函数体 `BP_CardFunctions.g.cs:35659-36468`）。
    ///
    /// ## 函数体在做什么（逐段，`L_xxxx` = 字节偏移）
    ///
    /// <code>
    /// L_0061 i=97   IsActionProcess → 不是动作流程就整段跳过
    /// L_00BE        for each cardID in cardsToSuppress：
    /// L_01B6          GetCardFromID
    /// L_01E9          HasCustomAbility(card, "cantBeSuppressed")   ; ★ 守卫之一
    /// L_0224          IsLocatedOnBoard(card)                       ; ★ 守卫之二
    /// L_026A          BooleanAND(isOnBoard, Not(cantBeSuppressed))
    /// L_02A9          _wasAlreadySuppressed = card.isSuppressed
    /// L_02D2          card.isSuppressed = True                     ; ★ 先置位，再摘东西
    /// L_0314          if (!_wasAlreadySuppressed) goto L_174B      ; ★ 条件为**假**才跳
    ///                 （`out/bp-cardfn.json` si=788 是
    ///                   `JumpIfNot(Condition = _wasAlreadySuppressed, Offset = 6194)`，
    ///                   而 6194 正是 si=6194 的 `Context->OnSuppressed` ⇒ 只在首次发"自己"）
    /// L_174B          ★触发点A card.OnSuppressed()                  ; **自己**那一路，最先发
    /// L_176F          goto L_0322
    /// L_0322          ★触发点B 广播 OnOtherCardSuppressed(触发号 58) ; FetchAllCardsWithEventTrigger(58)
    ///                 （紧随其后的 `si=6230 Jump(Offset = 802)`，802 就是 58 号 Fetch 那条语句）
    /// L_047E          IsUnrevealedCovertCard → RevealCard
    /// L_054E/1358     ① 摘关键词（见 SuppressStrips）+ 清 customJson + 摘卡牌给的能力
    /// L_119E/4510     ② 数值回落（GetStaticCard → 攻/防/行动费）+ JSON_Clear("veteran")
    /// L_136A/4970     ③ 攻防差额分流（只在"当前值 ≠ 卡面值"时改写；防御只在更高时压下来）
    /// L_15A5/5541     ★触发点C 广播 OnAfterOtherCardSuppressed(触发号 11) ; 摘除/回落**之后**才发
    /// </code>
    ///
    /// ⇒ 三个触发点的真实次序是 **A 自己 OnSuppressed → B T58 广播 → C T11 广播**
    /// （①②③ 夹在 B 与 C 之间）。内核 2026-10-03 之前写成 C → A → B，三个全错位；
    /// 那段注释还把 `L_0314` 读成「跳到 T58 广播」，与它自己「`L_174B` = `OnSuppressed`」
    /// 自相矛盾 —— 已一并改正。
    /// 执行流栈的落点：58 那轮循环跑完弹到 `1358 = L_054E`，摘完弹到 `4510 = L_119E`，
    /// 回落完弹到 `5541 = L_15A5`（`__ef.Push(5541)/Push(4510)/Push(1358)` 见 `:35773-35777`）。
    ///
    /// ⚠️ ①②③ 各自还带 `IsActionProcess()` 分流（`PushExecutionFlow`/`PopExecutionFlow`
    /// 那套），内核不建模"动作流程/回放流程"这一层，一律按主流程实现
    /// —— 如实记录这个简化。
    ///
    /// ## ① 摘关键词 + 清 customJson + 卡牌给的能力
    /// <code>
    /// L_054E..L_073F  RemoveGuard/Fury/Blitz/Immune/Alpine/Ambush/Mobilize/Smokescreen
    ///                 /ChangeHeavyArmor(0)/RemoveSalvage/RemoveShock
    /// L_07B8/L_07D9   hasDestruction = False ; exileNation = 0
    /// L_0803/L_083A   hasActivePincerEffect → RemovePincerEffects(card)
    /// L_0851/L_0886   customName1 = None ; customName2 = None
    /// L_08BB          KreditsTax_AsEnemyTarget = 0
    /// L_0925          JsonHasField(customJson,"suppressionException") → **只保留这个字段**
    ///                 （`customJson = JsonMake()` + 把它原样写回；同时若 `IsVeteran(card,true)`
    ///                  会写一个 veteran=true 进去 —— 但紧接着 ② 的 `JSON_Clear("veteran")`
    ///                  又把它删掉，净效果 = veteran 字段不保留）
    /// L_0B69          effectType = 0
    /// L_0B93..L_1DE3  遍历 receivedAbilitiesFromCards：对 **trigger/destruction/passive/
    ///                 lethal/custom** 五类里的每个 giver →
    ///                 ChangeBuffsFromCards(卡, 0, giverID, buffType=5, changeType=7 /*customRemove*/,
    ///                                     给卡的名字)  ; 摘掉"卡牌给它的额外特效"
    ///                 其余类别（guard/fury/… 那些关键字）→ 把"自己给自己的那条"从 giver 表里剔掉
    /// </code>
    ///
    /// ## ②③ 数值回落 —— **"所有增益失效"的真正落点**
    /// <code>
    /// L_11A3  StaticCardRef = GetStaticCard(card, card.name)      ; 卡面静态卡
    /// L_11E9  if (card.isSalvaged) { _staticAttack = 1; _staticDefense = 1 }
    ///         else { _staticAttack  = getAndDecryptAttack(StaticCardRef)
    ///                _staticDefense = getAndDecryptDefense(StaticCardRef) }
    /// L_123B  _staticOperationCost = StaticCardRef.operationCost
    ///         card.range = StaticCardRef.range
    /// L_12B3  JSON_Clear(card, "veteran")                       ; ★★ 老兵变回原形
    /// L_136A  attackDifference = card.attack - _staticAttack
    ///         if (≠0) ChangeAttack(card, instigatorID, _staticAttack, changeType=3 /*Suppress*/)
    ///         else    ChangeBuffsFromCards(card, 0, instigatorID, 0 /*Attack*/, 3, "")  ; 清攻击 buff
    /// L_1441  card.maxDefense = _staticDefense
    ///         if (getTotalDefense(card) > _staticDefense)
    ///             ChangeDefense(card, instigatorID, _staticDefense, 3)  ; **只在更高时压下来**
    ///         else ChangeBuffsFromCards(card, 0, instigatorID, 1 /*Defense*/, 3, "")
    /// L_1504  ChangeOperationCost(card, instigatorID, _staticOperationCost, 3, false,false,false)
    /// </code>
    /// `changeType=3` 就是 `EChangeType::Suppress`（`EChangeType.h`：0 tempBuffGive /
    /// 1 permBuff / 2 SetValue / **3 Suppress** / 4 tempBuffRemove / 5 veteranSet / …），
    /// 而 `ChangeAttack` 的 `2/3/5` 走**同一个** `setAndEncryptAttack` 分支
    /// （见 `CardApiDispatch.DoChangeAttack` 那段注释）⇒ **3 = 把攻/防/行动费设成卡面值**。
    /// 注意"**防御只在更高时压下来**"这一条：低了**不补**（否则抑制会变成治疗）。
    ///
    /// ## 内核的取舍（如实记录）
    /// <list type="bullet">
    /// <item>`exileNation` / `effectType` / `range` 三个字段内核**没有建模** ⇒ 无法清零，
    ///   它们参与的判定本来也不在（记为已知缺口）。</item>
    /// <item>`isSalvaged`（1/1 收缴复制品）内核没建模 ⇒ 静态值一律取 `Definition` 卡面值。</item>
    /// <item>关于 `cantBeSuppressed` 守卫：蓝图**两条**都查（`IsLocatedOnBoard` +
    ///   `!HasCustomAbility(card,"cantBeSuppressed")`，IR 里 `cantBeSuppressed` 出现 9 次），
    ///   本轮补上（原先只查"在场 + 未被抑制"）⇒ `card_event_maginot_line` /
    ///   `card_event_no_retreat` / `card_unit_10th_guards_regiment` 这类
    ///   「Cannot Retreat or be Suppressed.」的卡不会再被误抑制。</item>
    /// <item>`RemovePincerEffects`（`L_083A`）内核没有实现（IR 里 0 个调用点，
    ///   `pincer_receiver`/`pincer_givers` 那套 JSON 也完全没建模）——
    ///   但**钳击给的加成本身在 `BuffsBySource` 里**，会被本函数的增益清洗一并摘掉，
    ///   所以可观测效果一致；`Pincer` 关键字按蓝图**不摘**。</item>
    /// </list>
    /// </summary>
    public void SuppressUnit(CardInstance target)
    {
        // 守卫：在场 && 尚未被抑制 && 没有 `cantBeSuppressed` 自定义能力
        // （蓝图 L_01E9/L_0224/L_026A；后一条是本轮补的）。
        if (!IsLocatedOnBoard(target)
            || target.Keywords.Contains(Keyword.Suppressed)
            || HasCustomAbility(target, "cantBeSuppressed"))
        {
            return;
        }

        // ★ 先置位再摘东西 —— 与蓝图同序（L_02D2 在 L_054E 之前）。
        //   顺序有观测意义：摘关键字会走 `RemoveKeyword` → `ZActionRemove*` +
        //   `FireAbilitiesChanged`，而被抑制的卡在这些派发里应当**已经被抑制**
        //   （`CardApi.FireTrigger` si=325/710：`cardTriggered.isSuppressed` ⇒ 整轮不派发）。
        target.Keywords.Add(Keyword.Suppressed);

        // 记下被抑制的回合号 —— 仅作诊断留痕，见 `CardInstance.SuppressedOnTurn`。
        // ⚠️ 2026-10-02（第三轮）：它原先驱动一条"到期解除"（`ClearExpiredSuppression`），
        //    那条已删 —— 抑制按玩家确认/蓝图是**永久**的，所以这个值不会再被复位。
        target.SuppressedOnTurn = _engine.State.Turn;

        // ---- ★触发点A 自己那一路：`OnSuppressed`（蓝图 L_0314 → L_174B → L_176F）----
        // ⚠️ 次序：它在**摘任何东西之前**就发（L_174B 在 L_0322 / L_054E 之前）。
        //    蓝图的门是 `if (!_wasAlreadySuppressed) goto L_174B;`（条件为**假**才跳），
        //    即"只在**首次**抑制时发自己那一路"；上面那道
        //    `Keywords.Contains(Keyword.Suppressed)` 早退已经保证了这里就是首次。
        FireTrigger("OnSuppressed", target, target.Owner);

        // ---- ★触发点B 广播：`OnOtherCardSuppressed`（触发号 58，蓝图 L_0322）----
        // ⚠️ 它同样在摘除/回落**之前**（58 那轮循环跑完才弹执行流栈到 L_054E 摘东西）。
        //    名字以 `OnOther` 开头，`FireTrigger` 的广播判据正好认得它。
        FireTrigger("OnOtherCardSuppressed", target, target.Owner,
            eventArgs: new object?[] { target },
            eventSubject: target,
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["card"] = target,
            });

        // ---- ①a 摘关键词（可逆）----
        // 蓝图用 `_wasAlreadySuppressed`（`:35779-35806`）把这一整段放在"首次"分支里；
        // 上面那道 `Contains(Suppressed)` 门已经保证了这里就是首次。
        target.SuppressStrippedKeywords ??= new List<string>();
        foreach (string keyword in SuppressStrips)
        {
            if (target.Keywords.Contains(keyword))
            {
                RemoveKeyword(target, keyword);
                target.SuppressStrippedKeywords.Add(keyword);
            }
        }

        // 重甲清零：`Keyword.HeavyArmor` 是派生关键字（RecalculateStats 会按点数重挂），
        // 所以必须同时把**卡面重甲**遮住（L_070E 的 ChangeHeavyArmor(0)）。
        // ⚠️ 这里**不**立刻重算：此刻重甲的 buff 还在（要到 ③ 才摘），
        //    现在算会把 `Keyword.HeavyArmor` 又挂回去。真正的重算在 ③ 之后统一做一次。
        target.HeavyArmorZeroedBySuppress = true;

        // `TARGET` 的自定义能力（= 卡牌给单位添加的额外特效）也要失效。
        // 蓝图走的是 L_0B93 那一遍 `receivedAbilitiesFromCards` 清洗
        // （buffType=5 / changeType=7 customRemove），内核没有那张 giver 表，
        // 只有 `CustomAbility` 这一个槽位 —— 摘掉并记下（解除时装回）。
        if (target.CustomAbility is { Length: > 0 } ability)
        {
            target.SuppressStrippedCustomAbility = ability;
            target.CustomAbility = null;
        }

        // ---- ①b 清 customJson：只保留 `suppressionException`（L_0925-0B38）----
        // `suppressionException` 是**卡自己的**恢复机制：抑制会洗掉整个 customJson，
        // 唯独把它原样写回，卡（如 `card_unit_gordon_highlanders`）才能在抑制后
        // 把自己保存的状态读回来。IR 里这个键出现 26 次。
        // `customName1/2` 也存在 customJson 里（见 `CardApiDispatch.SuffixAdd`），
        // 所以这一句同时完成了蓝图 L_0851/L_0886 的 `customName1/2 = None`。
        target.CustomJson.TryGetValue("suppressionException", out string? exception);
        target.CustomJson.Clear();
        if (!string.IsNullOrEmpty(exception))
        {
            target.CustomJson["suppressionException"] = exception;
        }

        // ---- ①c KreditsTax_AsEnemyTarget = 0（L_08BB）----
        target.KreditsTaxAsEnemyTarget = 0;

        // ---- ② 老兵变回普通形态（L_12B3 `JSON_Clear(card,"veteran")`）----
        // 内核的老兵是 `Keyword.Veteran`（`MakeVeteran` 只加这一个关键字，
        // 见那边的注释：蓝图还会把 vet_card 的攻/防/行动费/关键字整套搬过来，
        // 内核**没有** veteran 卡数据 ⇒ 那一半是既有缺口）。
        // 直接删关键字 + 删 customJson 的 `veteran` 键（`Clear()` 已经把它清掉了），
        // 与 `JSON_Clear` 一样**不发**任何子动作。
        target.Keywords.Remove(Keyword.Veteran);

        // ---- ③ 所有增益失效：攻/防/重甲/行动费 buff 逐条摘走（记原文，供解除时装回）----
        // 玩家原话：「所有的增益效果也全部失效 —— 包括友方贴膜、敌方贴膜、
        // 友方卡牌给单位添加的额外特效」。
        // ⚠️ 只摘 **攻/防/重甲/行动费** 四项：蓝图的回落段只处理这四项，
        //    **碰都没碰改费**（整段没有 ChangeKreditCost）——
        //    纯改费条目（KreditCost ≠ 0 而其余为 0）保持原样。
        target.SuppressStrippedBuffs ??= new List<KeyValuePair<(int, bool), CardBuff>>();
        foreach (var key in target.BuffsBySource.Keys.ToList())
        {
            CardBuff buff = target.BuffsBySource[key];
            if (buff.Attack == 0 && buff.Defense == 0 && buff.HeavyArmor == 0 && buff.OperationCost == 0)
            {
                continue;
            }

            target.SuppressStrippedBuffs.Add(new KeyValuePair<(int, bool), CardBuff>(key, buff.Clone()));

            buff.Attack = 0;
            buff.Defense = 0;
            buff.HeavyArmor = 0;
            buff.OperationCost = 0;
            if (buff.IsEmpty)
            {
                target.BuffsBySource.Remove(key);
            }
        }

        // ---- ④ 数值回落到卡面静态值（L_136A / L_1441 / L_1504）----
        target.Attack = target.Definition.Attack;        // ChangeAttack(…, _staticAttack, Suppress)

        // 防御：**只在当前更高时压下来**（蓝图 `getTotalDefense > _staticDefense` 才改写）
        // ⇒ 抑制**不会治疗**一个已经被打残的单位。MaxDefense 按蓝图直接取静态值。
        if (target.Defense > target.Definition.Defense)
        {
            target.Defense = target.Definition.Defense;
        }

        target.MaxDefense = target.Definition.Defense;

        // 行动费/重甲/关键字由 `RecalculateStats()` 按"卡面 + 剩余 buff"绝对值重算 ——
        // 上一步已经把攻/防/重甲/行动费的 buff 清干净，所以这里算出来就是卡面值
        // （即 `_staticOperationCost`），且不会破坏纯改费 buff。
        target.RecalculateStats();

        _engine.FireSubAction("ZActionSuppressUnit", new[]
        {
            ActionValue2.Int("cardID", target.CardId),
        });

        // ---- ★触发点C 广播：`OnAfterOtherCardSuppressed`（触发号 11，蓝图 L_15A5）----
        // 它在**摘除（L_054E）+ 数值回落（L_119E）之后**才发 —— 见上方次序表。
        // ⚠️ 名字以 `OnAfter` 开头、但语义是**广播**（蓝图 L_15C6 那一遍遍历的是
        //    `FetchAllCardsWithEventTrigger(11)` 的**全部订阅者**，没有排除自己）。
        //    `FireTrigger` 的广播判据是「程序名以 `OnOther` 开头」，这个名字不满足，
        //    所以必须**同时**用 `otherProgramName` 再发一遍 —— 只传 programName 的话
        //    它只会发给主体，订阅者里除主体外全部收不到。
        FireTrigger("OnAfterOtherCardSuppressed", target, target.Owner,
            otherProgramName: "OnAfterOtherCardSuppressed",
            eventArgs: new object?[] { target },
            eventSubject: target,
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["card"] = target,
            });
    }

    /// <summary>
    /// 抑制还原 —— 把 <see cref="SuppressUnit"/> 摘掉的东西**原样装回去**。
    ///
    /// ⚠️⚠️ 2026-10-02（第三轮）：**本方法当前没有任何产品调用方（不可达）**。
    /// 它原先唯一的调用方 `MatchEngine.ClearExpiredSuppression` 已**整条删除** ——
    /// 那条「抑制到期解除」是内核自己发明的：玩家（雪雾）确认抑制**【永不解除】**
    /// （「一直白板到游戏结束」），蓝图 `isSuppressed` 全库**只有一处写点且写的是 `True`**
    /// （`BP_CardFunctions.g.cs:35781`），**没有任何一处写 `False`**。
    /// ⇒ 抑制按蓝图/玩家确认是**永久的**，所以这条还原路径**不会被触发**。
    ///
    /// **但它不删**：摘除（<see cref="SuppressUnit"/>）与还原是**成对**的能力，删掉还原
    /// 就等于把"抑制到底摘了什么"的对照面也一起删了；将来若真出现需要还原的机制
    /// （例如某张卡的 `suppressionException` 语义被证实包含"取消抑制"），这里是唯一入口。
    /// 保留的代价是零（无人调用 = 无行为），收益是可读性与可复用性。
    ///
    /// 自测：`SuppressStripsEverythingAndRestores` 段⑥ **手工直调**本方法，
    /// 验证它仍能完整还原（对称性有守卫，不会被"顺手改坏"）。
    ///
    /// 各段与 <see cref="SuppressUnit"/> 的摘除一一对应：
    /// <list type="bullet">
    /// <item>关键词：逐条 `GiveKeyword`（发 `ZActionGive*` + `FireAbilitiesChanged`，
    ///   与蓝图"能力集变了"那条广播同序）。</item>
    /// <item>攻/防/重甲/行动费 buff：装回条目，并做**对称逆运算**把数值加回
    ///   （`Attack += saved.Attack` / `Defense += saved.Defense`）——
    ///   与 `RemoveTemporaryBuffs` 同一套写法。抑制期间挨的伤害因此**不会**被抹掉：
    ///   摘除时防御是"压到卡面值"，装回时只是把当初减掉的那一份加回去。</item>
    /// <item>卡面重甲遮蔽位复位。</item>
    /// <item>`CustomAbility` 装回。</item>
    /// <item>`suppressionException` / 被洗掉的其它 `customJson` 键**不还原** ——
    ///   蓝图那边也是单向洗掉（抑制本身就要求"失去所有特效"），
    ///   卡要靠 `OnSuppressed` 钩子自己重建状态。</item>
    /// </list>
    /// </summary>
    public void RestoreAfterSuppression(CardInstance card)
    {
        card.Keywords.Remove(Keyword.Suppressed);
        card.SuppressedOnTurn = -1;

        if (card.SuppressStrippedKeywords is { Count: > 0 } keywords)
        {
            foreach (string keyword in keywords)
            {
                GiveKeyword(card, keyword);
            }

            keywords.Clear();
        }

        if (card.SuppressStrippedBuffs is { Count: > 0 } buffs)
        {
            foreach (var (key, saved) in buffs)
            {
                CardBuff live = GetOrCreateBuff(card, key.SourceCardId, key.Temporary);
                live.Attack += saved.Attack;
                live.Defense += saved.Defense;
                live.HeavyArmor += saved.HeavyArmor;
                live.OperationCost += saved.OperationCost;
                if (saved.Duration > 0)
                {
                    live.Duration = saved.Duration;
                }

                card.Attack = Math.Max(0, card.Attack + saved.Attack);
                card.Defense += saved.Defense;
            }

            card.MaxDefense = Math.Max(card.Defense, card.Definition.Defense);
            buffs.Clear();
        }

        card.HeavyArmorZeroedBySuppress = false;
        card.RecalculateStats();

        if (card.SuppressStrippedCustomAbility is { Length: > 0 } ability)
        {
            card.CustomAbility = ability;
            card.SuppressStrippedCustomAbility = null;
        }
    }

    /// <summary>
    /// 让一张卡成为"老兵"（对应 `BP_CardFunctions::MakeVeteran`，81 条语句）。
    ///
    /// 蓝图控制流（UAssetCLI 的 **StatementIndex**；`out/bp-cardfn.json`）：
    /// <code>
    /// si=2427 JumpIfNot(card.isSuppressed) -> si=2806   ; 被压制 ⇒ 跳过整个广播段
    /// si=2463 FetchAllCardsWithEventTrigger(32)         ; 32 = OnOtherCardBecomingVeteran
    /// si=2736     item.OnOtherCardBecomingVeteran(card)
    /// si=2842     Jump -> si=2463                       ; 循环回边
    /// si=2782 ExecuteOnOtherCardsAbilitiesChanged(card) ; 能力集变了（同一段里，只一次）
    /// si=2806 card.OnBecomingVeteran()                  ; ★ 自己：**压制与否都发**
    /// </code>
    /// 所以本内核的顺序是：广播（仅未被压制）→ 能力变化 → 自己。
    ///
    /// ⚠️ **没读懂的一处（不猜，照实说）**：si=2842 那条回边指向 si=2463（连订阅表都重取），
    /// 而循环自增在 si=2847/2889 —— 两处读数在"什么条件下再跑一遍"上不自洽
    /// （si=2672 的 `PushExecutionFlow(2847)` + si=2805 的 `PopExecutionFlow`
    /// 也可能把流程直接送进出参段）。可观测的部分（广播一次 + 能力变化一次 + 自己一次）
    /// 两种读法一致，所以按这一种实现。
    /// </summary>
    public void MakeVeteran(CardInstance target)
    {
        if (target.Keywords.Add(Keyword.Veteran))
        {
            _engine.FireSubAction("ZActionMakeVeteran", new[]
            {
                ActionValue2.Int("cardID", target.CardId),
            });

            // ★★ **2026-10-04 更正：门装反了。** 蓝图原文
            // （`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs`）：
            // <code>
            // :26303  if (!card.isSuppressed) goto L_0AF6;         ; 未压制 ⇒ 跳去自程序
            // :26305  L_099F: FetchAllCardsWithEventTrigger(32)    ; ★ 广播：压制时**直落这里**
            // :26323      item.OnOtherCardBecomingVeteran(card)
            // :26335  L_0ADE: ExecuteOnOtherCardsAbilitiesChanged(self, card)
            // :26347  L_0AF6: OnBecomingVeteran(card)              ; 自程序
            // :26349  L_0B1A: goto L_099F                          ; ★ 自程序跑完**又跳回广播**
            // </code>
            // ⇒ T32 广播与「能力集变化」**无条件发**；被压制的卡只是**不发自己的**
            //   `OnBecomingVeteran`。旧实现恰好相反：门住广播、放开自程序。
            // ⚠️ 根因同 `MatchEngine.Destroy`：`JumpIfNot(cond) -> T` 是「cond 为假才跳」。
            FireTrigger("OnOtherCardBecomingVeteran", target, target.Owner,
                eventArgs: new object?[] { target },
                eventSubject: target,
                namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["card"] = target,
                });

            // si=2782 ExecuteOnOtherCardsAbilitiesChanged(card)
            FireAbilitiesChanged(target);

            if (!target.Keywords.Contains(Keyword.Suppressed))
            {
                FireTrigger("OnBecomingVeteran", target, target.Owner);
            }
        }
    }

    public void CustomAbilityAdd(CardInstance target, string ability, CardInstance? giver)
    {
        target.CustomAbility = ability;
        _engine.FireSubAction("ZActionCustomAbilityAdd", new[]
        {
            ActionValue2.Int("giverID", giver?.CardId ?? 0),
            ActionValue2.Str("ability", ability),
        });

        FireAbilitiesChanged(target);
    }

    /// <summary>
    /// 「这张卡的累积状态被重置了」—— 对应 `BP_CardFunctions::ResetCardInBattle(cardToChange)`。
    ///
    /// 出处：`out/bp-cardfn.json` 函数 `ResetCardInBattle`：
    /// <code>
    /// i=749  cardToChange.ResetCardAttributes()      ; 原生函数，实现体不在客户端
    /// i=782  IsActionProcess()
    /// i=815  cardToChange.OnCardReset()              ; ★ 自己（此前从未派发，64 张订阅）
    /// i=851  FetchAllCardsWithEventTrigger(53)       ; 53 = OnOtherCardReset
    /// i=1120 item.OnOtherCardReset(cardReset, resetCardID)
    /// </code>
    /// 触发号 53 的依据：`ERegisteredCardFunction.h` 逐项数下来第 54 项 = `OnOtherCardReset`。
    /// 签名 `BaseCardObject.h:718/565`。
    ///
    /// ⚠️ **触发点只有两处**（全 dump 搜 `ResetCardInBattle` 只有这两个调用方）：
    /// `MoveCardFromBoardToOwnersHand` i=626 与 `MoveCardToTopOfDeck` i=828 ——
    /// 也就是「**卡从场上回手 / 回牌库**」。
    /// `ResetCardAttributes` 是原生函数、实现体不在客户端 dump 里，
    /// 所以"它还重置了什么"**读不出来**；这里只落实两个可证的触发点，不猜别的时机。
    /// </summary>
    public void ResetCardInBattle(CardInstance card)
    {
        FireTrigger("OnCardReset", card, card.Owner);

        FireTrigger("OnOtherCardReset", card, card.Owner,
            eventArgs: new object?[] { card, card.CardId },
            eventSubject: card,
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardReset"] = card,
                ["resetCardID"] = card.CardId,
            });
    }

    public bool HasCustomAbility(CardInstance card, string? ability = null)
        => card.CustomAbility is not null && (ability is null || card.CustomAbility == ability);

    /// <summary>对应 PersistCustomFields —— 游戏用它把卡上的临时状态写进子动作。</summary>
    public void PersistCustomFields(CardInstance card)
    {
        _engine.FireSubAction("ZActionPersistCustomFields", new[]
        {
            ActionValue2.Int("cardID", card.CardId),
            ActionValue2.Str("customJson", string.Join(";", card.CustomJson.Select(kv => $"{kv.Key}={kv.Value}"))),
        });
    }

    // ---- 卡牌私有 JSON（JSON_* 一族，被 19~21 套牌需要）----

    public int JsonGetInt(CardInstance card, string key)
        => card.CustomJson.TryGetValue(key, out string? v) && int.TryParse(v, out int i) ? i : 0;

    public void JsonSetInt(CardInstance card, string key, int value) => card.CustomJson[key] = value.ToString();

    public bool JsonGetBool(CardInstance card, string key)
        => card.CustomJson.TryGetValue(key, out string? v) && v == "1";

    public void JsonSetBool(CardInstance card, string key, bool value) => card.CustomJson[key] = value ? "1" : "0";

    public string JsonGetString(CardInstance card, string key)
        => card.CustomJson.GetValueOrDefault(key, "");

    public void JsonSetString(CardInstance card, string key, string value) => card.CustomJson[key] = value;

    /// <summary>
    /// `JSON_Clear(card, variableName, out found)` —— **只删指定的那一个键**，
    /// 并回报它原本是否存在。
    ///
    /// ⚠️ 蓝图 `BP_CardFunctions.g.cs:24383-24395` 逐行是：
    ///   `existed = JsonHasField(card.customJson, variableName)`
    ///   `if (existed) card.customJson = JsonRemoveField(card.customJson, variableName)`
    ///   `found = existed`
    /// ⇒ **只删一个键**，而且 `found` 要写出去。
    ///
    /// 旧实现是 `card.CustomJson.Clear()` —— **清空整张卡的 JSON**、**忽略 `variableName`**，
    /// 且派发表那条 lambda 返回 null ⇒ `found` 从不写入（VM 只在 `result is not null`
    /// 时写出参，见 `KismetVm.cs:669`）。
    /// 有 **46 张卡**读 `CallFunc_JSON_Clear_found`；典型受害卡
    /// `card_unit_85_pioneer_company` 的 `buffActive` 正是
    /// `JSON_SetBool → JSON_Clear → JSON_GetBool` 三段 —— 整张表被清 ⇒ 兄弟键一起丢。
    /// </summary>
    public bool JsonClear(CardInstance card, string key)
        => !string.IsNullOrEmpty(key) && card.CustomJson.Remove(key);

    // ---- 卡牌私有 JSON 的数组变体（JSON_GetIntArray / JSON_AddToIntArray）----
    // 存成逗号分隔字符串，既省内存又天然确定性（字典遍历顺序不影响它）。

    private const char JsonListSeparator = ',';

    public List<int> JsonGetIntArray(CardInstance card, string key)
    {
        if (!card.CustomJson.TryGetValue(key, out string? raw) || raw.Length == 0)
        {
            return new List<int>();
        }

        var result = new List<int>();
        foreach (string part in raw.Split(JsonListSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, out int v))
            {
                result.Add(v);
            }
        }

        return result;
    }

    public void JsonSetIntArray(CardInstance card, string key, IReadOnlyList<int> values)
        => card.CustomJson[key] = string.Join(JsonListSeparator, values);

    public void JsonAddToIntArray(CardInstance card, string key, int value)
    {
        var list = JsonGetIntArray(card, key);
        list.Add(value);
        JsonSetIntArray(card, key, list);
    }

    /// <summary>把 VM 传来的值当作卡牌数组。</summary>
    internal static List<CardInstance> EvalArray(object? receiver, object?[] args)
    {
        if (receiver is List<CardInstance> rl)
        {
            return rl;
        }

        foreach (object? v in args)
        {
            if (v is List<CardInstance> list)
            {
                return list;
            }
        }

        if (receiver is CardInstance single)
        {
            return new List<CardInstance> { single };
        }

        return new List<CardInstance>();
    }

    /// <summary>
    /// 把 VM 传来的值当作**元素类型无关**的蓝图数组。
    ///
    /// 为什么需要它：UE 里 <c>TArray&lt;int&gt;</c> 与 <c>TArray&lt;UObject*&gt;</c> 是两种
    /// 不同的容器，而本内核的 VM 是**无类型**的 —— 数组原语必须两种都认。
    /// <see cref="EvalArray"/> 只认 <c>List&lt;CardInstance&gt;</c>，
    /// 于是拿一个 ID 数组（例：<c>GetDeckByside</c> 的 <c>deckCardIDs</c>，
    /// 出参是 <c>TArray&lt;int&gt;</c>，见 <see cref="GetDeckBySide"/> 的注释）
    /// 去问 <c>Array_Length</c> 会得到 0、问 <c>Array_Get</c> 会得到 null ——
    /// 那比"元素类型不对"更糟：整段循环直接被跳过。
    ///
    /// 元素类型在这里**故意不判**：`Array_Contains` / `Array_Remove*` 的值比较
    /// 由 <c>CardApiDispatch.SameArrayValue</c> 统一处理（卡实例 ↔ 整数 ID 互通）。
    /// </summary>
    internal static System.Collections.IList EvalList(object? receiver, object?[] args)
    {
        if (receiver is System.Collections.IList rl && receiver is not string)
        {
            return rl;
        }

        foreach (object? v in args)
        {
            if (v is System.Collections.IList list && v is not string)
            {
                return list;
            }
        }

        if (receiver is CardInstance single)
        {
            return new List<CardInstance> { single };
        }

        return new List<CardInstance>();
    }

    internal static List<int> AsIntList(object? v) => v switch
    {
        List<int> list => list,
        System.Collections.IEnumerable e and not string => e.Cast<object?>().Select(AsInt).ToList(),
        _ => new List<int>(),
    };

    // ==================================================================
    //  查询层（只读，实现成本低但被调用最频繁）
    // ==================================================================

    public bool IsLocatedOnBoard(CardInstance c) => c.Location.IsBoard() && !c.IsHq;
    public bool IsLocatedInHand(CardInstance c) => c.Location is CardLocation.HandLeft or CardLocation.HandRight;
    public bool IsLocatedInDeck(CardInstance c) => c.Location is CardLocation.DeckLeft or CardLocation.DeckRight;
    public bool IsUnit(CardInstance c) => c.Definition.IsUnit;
    public bool IsInfantry(CardInstance c) => c.Definition.Type == "infantry";
    public bool IsTank(CardInstance c) => c.Definition.Type == "tank";
    public bool IsArtillery(CardInstance c) => c.Definition.Type == "artillery";
    public bool IsAirUnit(CardInstance c) => c.Definition.Type is "fighter" or "bomber";
    public bool IsOrder(CardInstance c) => c.Definition.IsOrder;
    public bool IsLocationCard(CardInstance c) => c.Definition.IsLocationCard;
    /// <summary>
    /// 「**这张卡**是不是 <paramref name="side"/> 那一方的**单位**」——
    /// `UBaseCardObject::IsSameSideUnit(ESideEnum side)` 的**真实形状**（成员函数，接收者才是被查的卡）。
    ///
    /// ⚠️ 旧实现写成 `IsSameSideUnit(CardInstance a, CardInstance b)`（两张卡），
    /// 而全卡池**不存在** `(卡, 卡)` 形状的调用点 —— 直译产物
    /// `out/Generated-gap/_deps/*.g.cs` 里 17 种调用形状无一例外是
    /// `H.Call("IsSameSideUnit", [卡, side, out])`；IR 侧同样
    /// （`card_location_british_scen4` i=4008/4027/4152：`a[0]` = `{"var":"side"}` 或
    /// `GetOppositeSide(...)`，`a[1]` = out 槽，`recv` = `K2Node_Event_cardPlayed`）。
    /// 旧形状把 `a[0]`（int 阵营）当卡读 ⇒ **19/19 个调用点恒 false**。
    ///
    /// 「同阵营」和「是单位」两件事**都要判**：证据是
    /// `card_event_air_corps_ferrying.CanPlayFromHand` 的全部判据只有
    /// `GetTargetedCard` + `IsSameSideUnit(target, side)`，失败时 reason 是
    /// `"friendly_unit"`（卡面「Give a friendly unit +1+1」）——
    /// 只判阵营的话，把这张牌指向一个非单位目标也会放行。
    /// </summary>
    public bool IsSameSideUnit(CardInstance card, Side side) => IsUnit(card) && card.Owner == side;

    public bool IsSideActive(Side s) => State.ActiveSide == s;
    public Side GetOppositeSide(Side s) => s.Opposite();
    public int GetTurnNumber() => State.Turn;
    public bool DoesSideControlTheFrontline(Side s) => State.FrontlineOwner == s;

    public CardInstance? GetCardFromID(int cardId) => State.ById(cardId);
    public CardInstance? GetLocationCardBySide(Side s) => State.Hq(s);

    /// <summary>
    /// 客户端 `GetCardsOnBoardBySide(side, unitsOnly, includeCovertCards)` 的候选集。
    ///
    /// ★★ **顺序必须是「进入战斗的顺序」**（2026-10-02 从蓝图定案）。
    /// 客户端遍历场上一律走 `GetAllCardInBattle()` = `Map_Values(AllCardsInBattle)`
    ///（`_deps/BP_GameState_Battle.g.cs:1571`），而那个映射**只增不删**
    ///（全树 0 处 `Map_Remove`，见 `AddCardToAllCardsInBattle` `:301-319`）
    /// ⇒ 顺序 = **插入顺序**。详见 <see cref="GameState.BoardInBattleOrder"/>。
    ///
    /// ⚠️ 以前这里用的是 `State.Board(s)`（按 `LocationNumber` 排序）——
    /// 同一次消费、同一个下标会**取到不同的卡**，这正是「随机效果与客户端不一致」
    /// 的第三个成因（`GetRandomCard` 的注释里写过）。
    /// </summary>
    public IEnumerable<CardInstance> GetCardsOnBoardBySide(Side s) => State.BoardInBattleOrder(s);
    public IEnumerable<CardInstance> GetAllUnitsOnBoard() => State.Board(Side.Left).Concat(State.Board(Side.Right));
    public IEnumerable<CardInstance> GetAllCardsOnBoard() => GetAllUnitsOnBoard().Concat(new[] { State.Hq(Side.Left), State.Hq(Side.Right) });
    public IEnumerable<CardInstance> GetAllCards() => State.AllCards;
    public IEnumerable<CardInstance> GetCardsInHandBySide(Side s) => State.Hand(s);
    /// <summary>
    /// 「某一方牌库里的**卡 ID**」。蓝图链条：
    /// `BP_CardFunctions.GetDeckByside`（薄包装，反编译原文
    /// `out/Generated-gap/_deps/BP_CardFunctions.g.cs:20913-20931`）
    /// → `BP_GameState_Battle.GetDeckBySide`
    /// → `GameState.DeckCardIDs_Left` / `DeckCardIDs_Right`
    /// （`ref/kards-sim/KardsSim/Generated/_deps/BP_GameState_Battle.g.cs:1837/1841`，
    /// 成员名就写着 **IDs**）。
    ///
    /// ⚠️ 出参是 <c>TArray&lt;int&gt;</c>（卡 **ID**），**不是**卡实例。
    /// 全卡池 46 张调用它的卡里，`Array_Get` 出来的元素只有三种用法，**全部是整数语义**：
    /// <list type="bullet">
    /// <item><c>GetCardFromID(deck[i])</c>（29 张，例 <c>card_event_blockade</c>、
    ///       <c>card_event_colossus</c>、<c>card_event_flight_to_oblivion</c>）</item>
    /// <item><c>Greater_IntInt(deck[i], 0)</c> / <c>JSON_SetInt(…, deck[i])</c> /
    ///       <c>DiscardCardFromDeck(deck[i], …)</c>（例 <c>card_unit_lovat_scouts</c> i=180、
    ///       <c>card_event_alpenfestung</c> 的成员 <c>topID</c>）</item>
    /// <item><c>DrawSpecificCardFromDeckBySide(cardID, deck[i], side, false)</c>
    ///       （例 <c>card_event_defend_the_empire</c> i=869）</item>
    /// </list>
    /// 另外 <c>DrawSpecificCardFromDeckBySide</c> 的内联体第一步就是
    /// <c>Array_Contains(deckCardIDs, cardID)</c>（同文件 <c>:12460</c>）——
    /// 拿牌库数组和一个**整数** cardID 比。
    /// **没有一个调用点把元素当卡实例用。**
    ///
    /// 旧实现返回 <c>IEnumerable&lt;CardInstance&gt;</c> ⇒
    /// <c>AsInt(卡实例) = 0</c>（`CardApiDispatch.cs:2976` 的 <c>_ =&gt; 0</c>）⇒
    /// 上面三种判据**全部恒假**。实测症状（对局 542091 t7）：
    /// pams 开发出的 <c>card_event_convoy_175</c>（卡 id 5002）费用没被设成 0，
    /// 内核对它收 3 费（探针 <c>[CANPLAY] left card_event_convoy_175 cost=3</c>）。
    /// 回归用例：`tools/BotSim/SelfTest.cs` 的 <c>GetDeckBySideReturnsCardIds</c> /
    /// <c>PamsDevelopedCardCostZero</c>。
    /// </summary>
    public List<int> GetDeckBySide(Side s) => State.Deck(s).Select(c => c.CardId).ToList();

    public int GetTotalAttack(Side s) => State.Board(s).Sum(u => u.Attack);
    public int GetTotalDefense(Side s) => State.Board(s).Sum(u => u.Defense);

    /// <param name="applyCustomAlways">
    /// ★ 2026-10-04：对应蓝图第二个实参 `skipCustomAlways` 的**取反**。
    /// 蓝图 `GetRandomCard`（`BP_CardFunctions.g.cs:21669-21776`，本次逐行复核）：
    /// <code>
    /// :21686  if (!(Array_Length(cards) > 0)) → randomCard = null
    /// :21688  if (!skipCustomAlways) goto L_0179      ; ★ false ⇒ 走"自定义必选"那条
    /// :21689  L_007E:  r = RandomIntegerInRangeFromStream(cardsRandomStream, 0, len-1)
    /// :21696           randomCard = cards[r]           ; ← 全池随机（也是"必选集为空"的落点）
    /// :21705  L_0179:  收集 alwaysSelected = cards 里
    ///                  `CustomName1HasAttribute(card, "AlwaysSelectedAsRandom")` 为真的那些
    /// :21753  if (Array_Length(alwaysSelected) > 0):
    /// :21763      r = RandomIntegerInRangeFromStream(cardsRandomStream, 0, len(alwaysSelected)-1)
    /// :21765      randomCard = alwaysSelected[r]
    /// :21757  else → goto L_007E                       ; 必选集为空 ⇒ 退回全池随机
    /// </code>
    /// ⇒ **两条路都只消费 1 次随机数**，只有"从哪个池里取"不同（所以游标对账不受影响）。
    /// ⚠️ 全卡池 **176 个调用点里 153 个传 `skipCustomAlways = false`**（`true` 只有 23 个），
    /// 而内核旧实现**恒按全池随机**（等于恒按 `true` 那条）⇒ 对那 153 个点是**走错了分支**。
    /// ⚠️ 但"必选集"要非空才看得出差别，而 `AlwaysSelectedAsRandom` 这个属性
    /// **在本仓库的卡数据里查不到**（`cards.live.json` 0 处；`card-effects.json` 里只有
    /// `BP_CardFunctions` 自己的函数表提到这个字符串）⇒ 对当前 22 局语料
    /// **大概率是恒等变换**（A/B 逐位不变就是它的证据）。
    /// </param>
    public CardInstance? GetRandomCard(IReadOnlyList<CardInstance> pool, bool applyCustomAlways = false)
    {
        if (pool.Count == 0)
        {
            return null;
        }

        // `skipCustomAlways == false` 那条：池里有"必选"卡就只在它们之间随机。
        IReadOnlyList<CardInstance> candidates = pool;
        if (applyCustomAlways)
        {
            var always = pool
                .Where(x => CustomNameHasAttribute(x, "customName1", "AlwaysSelectedAsRandom"))
                .ToList();
            if (always.Count > 0)
            {
                candidates = always;
            }
        }

        // `GetRandomCard(cards, skipCustomAlways, out randomCard)` 在蓝图里是
        // `RandomIntegerInRangeFromStream(cardsRandomStream, 0, Length-1)`
        // （`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:21682-21698`）——
        // 一次消费、闭区间。游标探针记下候选集大小与选中下标，用来和客户端对账。
        uint seedBefore = State.Random.Seed;
        long cursorBefore = State.Random.ConsumedCount;
        int index = State.Random.Next(candidates.Count);
        CardInstance picked = candidates[index];
        // 候选集的**内容与顺序**也要记 —— 同一次消费、同一个下标，
        // 候选集排列不同就会取到不同的卡（这是"随机效果与客户端不一致"的第三个成因）。
        //
        // 仅诊断开启时构造完整候选表，避免正常自对弈路径上的字符串分配。
        if (State.CollectRandomTrace)
        {
            // Full order is required to distinguish a pool mismatch from an RNG mismatch.
            string poolDump = string.Join(",", candidates.Select(x => x.Name));
            State.TraceRandom($"GetRandomCard n={candidates.Count} idx={index} -> {picked.Name} " +
                $"cursor={cursorBefore}->{State.Random.ConsumedCount} " +
                $"seed={seedBefore}->{State.Random.Seed} 池=[{poolDump}]");
        }
        return picked;
    }

    // ==================================================================
    //  Gotcha（反制卡）子系统 —— 按蓝图重写
    // ==================================================================
    //
    // 权威：`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs`（`BP_CardFunctions`）
    // 与 `_deps/BP_GameState_Battle.g.cs`。下面每个方法头都给出**逐行**依据。
    //
    // 这一族在旧内核里**完全没有**：`GotchaTriggered`(54 个调用点) /
    // `ShouldGotchaTrigger`(53) / `IsGotcha`(16) / `GetHandLocationBySide`(5) /
    // `SetCardsSeenByCipher`(2) 全在派发表缺口里（见 `DispatchGap` 的基线）。
    //
    // ⚠️ 与「参考实现」`ref/kards-sim/KardsSim/Bridge/EngineHost.cs:1342-1355` 的**已知差异**：
    // 它把判据写成 `c.Covert && obj.gotcha`，而**这两项在它自己的引擎里恒为假** ——
    // `Card.Covert`（`Core/Card.cs:44`）在它全仓库**没有任何赋值点**，
    // `gotcha` 这个成员名在全部 38018 行生成代码里**只出现在它的 `IsGotcha` 里**
    // （没有任何蓝图写入方）。⇒ 参考实现的那两条 `IsGotcha`/`ShouldGotchaTrigger`
    // 实际上恒返回 false，**不能照抄**。
    // 权威判据只能是卡定义本身：`Type == gotcha`
    // （`docs/cards.live.json` 52 张；`decompiled/cards.all.json` 的 CDO
    // `Type = ETypeEnum::gotcha`，且这 52 张恰好就是 IR 里调用 `GotchaTriggered` 的 52 张）。

    /// <summary>触发点 21 的注册函数名（`OnCounterMeasureTriggered`）。</summary>
    private const string CounterMeasureTrigger = "OnCounterMeasureTriggered";

    /// <summary>触发点 28 的注册函数名（`OnIntelTriggered`）。</summary>
    private const string IntelTrigger = "OnIntelTriggered";

    /// <summary>
    /// `UBaseCardObject::IsGotcha` —— 这张卡是不是**反制卡（Gotcha）**。
    ///
    /// 蓝图里它是**原生函数**（不在字节码里），唯一可用的判据是卡定义的类型
    /// （见本区段开头那段：参考实现读的 `Covert` / `gotcha` 成员在本仓与它自己那里
    /// 都取不到值）。实测口径：
    /// <list type="bullet">
    /// <item>`docs/cards.live.json` 里 `type == "gotcha"` 的卡 = **52 张**；</item>
    /// <item>`docs/card-ir.json` 里调用 `GotchaTriggered` 的卡 = **52 张**，集合相同。</item>
    /// </list>
    ///
    /// ⚠️ 与参考实现的**近似**：它多要一个「未揭示的 covert」条件。本内核没有建模
    /// Covert 的揭示状态机（见 <see cref="IsUnrevealedCovertCard"/> 的注释），
    /// 所以这里**只判类型**。⇒ 「一张 gotcha 卡被揭示之后还算不算 gotcha」
    /// 这个问题本内核答不了，如实标注。
    /// </summary>
    public bool IsGotcha(CardInstance c) => c.Definition.Type == "gotcha";

    /// <summary>
    /// `ShouldGotchaTrigger(out shouldIt)` —— **这张卡自己**现在该不该响应反制触发。
    ///
    /// ## ★ 判据落在 **self**，不是实参
    ///
    /// 蓝图调用点（`Generated/Britain/Base/events/card_event_interception.g.cs:53`）：
    /// <code>
    /// H.Call("ShouldGotchaTrigger", new Val[] { self, K2Node_Event_cardPlayed, Val.Out(…) })
    /// </code>
    /// 而 `IR` 里的形状（`docs/card-ir.json`，53 个调用点**全部**如此）是
    /// <c>args = [触发卡, out]</c>、**没有 `recv`** —— 也就是**隐式 self**
    /// （<c>KismetVm.Eval</c> 的 `{self:true}` → `ctx.Self`）。
    /// ⇒ 判据必须落在 <c>ctx.Self</c>（= 正在跑这段程序的那张卡），
    /// **实参 `a[0]` 是"触发这件事的那张卡"，与判据无关**。
    ///
    /// 把 `a[0]` 当 self 的后果：整族反制卡的 `ShouldGotchaTrigger` 会去问
    /// 「刚被打出的那张牌是不是反制卡」—— 那是**普通卡**，恒假 ⇒ 53 个调用点
    /// 全部静默失效。
    ///
    /// ## ★★ 还必须要求「**这张反制卡已经装填（盖着）**」—— 2026-10-04 补
    ///
    /// 参考实现（唯一可读的第二个口径）`ref/kards-sim/KardsSim/Bridge/EngineHost.cs:1350-1356`
    /// 把语义**逐字写在注释里**：
    /// <code>
    /// // 参数：(triggerCard, out shouldIt)。**只有盖着的反制卡才响应**。
    /// var self = h.Card_(a[0]);
    /// var should = self is not null &amp;&amp; self.Covert &amp;&amp; !self.Destroyed
    ///              &amp;&amp; h.Obj(self).Get("gotcha").AsBool();
    /// </code>
    /// 而 `IsGotcha` 那边（`:1342-1347`）也写着「Gotcha 是**盖在场上没翻开**的状态，
    /// 等价于 covert + 未 reveal」。
    ///
    /// ⚠️ 旧实现是 `IsGotcha(self) &amp;&amp; self.IsAlive` —— **漏了"盖着"这一项**，
    /// 于是**手里任何一张还没打出的反制卡，只要它的 `OnOther*` 程序被派发到，
    /// 就会立刻触发**（不管它有没有被装填成陷阱）。
    ///
    /// **实测后果（回放 773639）**：右方手里那张未装填的
    /// `card_event_unexpected_resistance`（"Pin a unit that moves to the frontline"）
    /// 在左方单位 `card_unit_2nd_west_africa` 上前线时被触发，把它钉住 ⇒
    /// 人类 `#92 t20 AC` 被内核拒绝（`攻击者被钉住`），而客户端打得出来。
    ///
    /// ⇒ 内核里"盖着"就是 <see cref="CardInstance.GotchaActivated"/> `&gt; 0`：
    /// 它由 <see cref="AssignGotchaActivatedOnPlayFromHand"/> 在**打出反制卡时**赋值
    /// （`BP_CardFunctions.g.cs:28316`，**唯一**写入方），
    /// 而 <see cref="GetActiveGotchasOrdered"/> 早就用 `gotchaActivated &gt; 0` 过滤
    /// （`BP_CardFunctions.g.cs:18733`，见 `CardApi.cs` 那段注释）。
    /// 同一份状态，在这里也必须过同一道门。
    /// </summary>
    public bool ShouldGotchaTrigger(CardInstance? self)
        => self is not null && IsGotcha(self) && self.IsAlive && self.GotchaActivated > 0;

    /// <summary>
    /// `GetHandLocationBySide(side, out handLocation)`
    /// （`BP_CardFunctions.g.cs:20904-20930`）：
    /// <code>
    /// :20914  side == 1  →  handLocation = 3 (HandLeft)
    /// :20922  否则        →  handLocation = 4 (HandRight)
    /// </code>
    /// ⚠️ 是「**== 1** 就 3，其余一律 4」，不是 `side == 2 → 4` ——
    /// `side = 0 (NotAvailable)` 在蓝图里也落到 4。
    /// </summary>
    public CardLocation GetHandLocationBySide(Side side)
        => side == Side.Left ? CardLocation.HandLeft : CardLocation.HandRight;

    /// <summary>
    /// `RearrangeLocation(location)`（`BP_CardFunctions.g.cs:29227-29300`）：
    /// <code>
    /// :29239  FetchCardsByLocationSorted(location) → cardIDs
    ///         （`_deps/BP_GameState_Battle.g.cs:1414-1542`：先按 location 过滤，
    ///           再 `SortCardsByLocationNumber`（`:3657`，按 locationNumber **升序**的插入排序））
    /// :29278  逐个 `SetCardLocationAndLocNumber(cardID, location, index)`
    ///         （`:33960-33997`：`:33978 location = location`；
    ///           `:33980 location == 8(Discard) ⇒ 跳过 locationNumber`；
    ///           `:33990 否则 locationNumber = index`）
    /// </code>
    /// ⇒ 语义 = 「把**这个位置**里的卡按当前 `locationNumber` 升序压紧成 `0..n-1`」。
    ///
    /// ⚠️ 与既有的 <see cref="GameState.NormalizeLocationNumbers"/> 的区别：那个是
    /// **按阵营**（`Cards(side, loc)`），而蓝图的 `RearrangeLocation` 是**按位置**
    /// （手牌 3/4 各自只属于一方，两者等价；但前线 7 是双方共享的，不等价）。
    /// 这里按蓝图实现，调用方（`GotchaTriggered`）传的正是手牌位置。
    ///
    /// 排序稳定性：蓝图是插入排序（`>` 比较）⇒ 同值时保持**输入数组顺序**
    /// （= `AllCardsInBattle` 的插入序）。内核用 `State.AllCards`（按 `CardId` 升序）
    /// 作为输入，LINQ `OrderBy` 也是稳定排序 ⇒ 同值时按 `CardId`。
    /// 这是**近似**（内核没有 `AllCardsInBattle` 那张只增不删的映射），已标注。
    /// </summary>
    public void RearrangeLocation(CardLocation location)
    {
        var cards = State.AllCards
            .Where(c => c.Location == location)
            .OrderBy(c => c.LocationNumber)
            .ToList();

        // :33980 丢弃堆（8）**不写 locationNumber**（写了也没有意义，位置本身就是 8）。
        if (location == CardLocation.Discard)
        {
            return;
        }

        for (int i = 0; i < cards.Count; i++)
        {
            cards[i].LocationNumber = i;
        }
    }

    /// <summary>
    /// `SetStopFurtherActions(GameStateRef, inStopFurtherActions)` / `GetStopFurtherActions()`
    /// （`_deps/BP_GameState_Battle.g.cs:3603-3618` / `:2569-2586`，各只有一行读写）。
    ///
    /// ⚠️ 内核只建模**状态**；所有**读取点**都在 `AfterWaitCardPlayFromHand` 等
    /// **库函数**里，而它们不在 `card-ir.json` 的调用点集合里 ⇒ 目前**没有消费者**。
    /// 见 <see cref="GameState.StopFurtherActions"/> 的注释。
    /// </summary>
    public void SetStopFurtherActions(bool value) => State.StopFurtherActions = value;

    /// <inheritdoc cref="SetStopFurtherActions"/>
    public bool GetStopFurtherActions() => State.StopFurtherActions;

    /// <summary>
    /// `ApplySetCardsSeenByCipher(out card[], enemyTurn, showAnimation)`
    /// （`BP_CardFunctions.g.cs:3996-4071`；权威签名 `_index.g.cs:1828`）：
    /// <code>
    /// :4025  for each card in card[]：
    /// :4025      IsLocatedInHand(card)        ; ★ 只处理**还在手牌里**的
    /// :4040      IDs.Add(card.cardID)
    /// :4044      seen.Add(True)
    /// :4048      card.cardSeen = True         ; ★ 真正"标为已见"的那一步
    /// :4058  IsActionProcess ⇒ :4071 NotifyCardsSeen(Notifier, IDs, seen, showAnimation, enemyTurn)
    /// </code>
    /// `NotifyCardsSeen` 是纯客户端通知（内核无 `CardFunctionsNotifier`），不实现。
    ///
    /// ⚠️ 第一个参数在蓝图里是**按引用进出的数组**（`Val.Out(...)` 回写）——
    /// 本内核直接原地改传入的 `List&lt;CardInstance&gt;` 并返回，等价。
    /// </summary>
    public void ApplySetCardsSeenByCipher(List<CardInstance> cards, bool enemyTurn, bool showAnimation)
    {
        _ = enemyTurn;
        _ = showAnimation;

        foreach (var card in cards)
        {
            if (!IsLocatedInHand(card))
            {
                continue;
            }

            card.CardSeen = true;
        }
    }

    /// <summary>
    /// `SetCardsSeenByCipher(numberOfCardsSeen, instigatorID, side, out qqq)`
    /// （`BP_CardFunctions.g.cs:34036-34220`；权威签名 `_index.g.cs:4282`）。
    ///
    /// 逐行：
    /// <code>
    /// :34052  FetchAllCardsWithEventTrigger(28) → 订阅 OnIntelTriggered 的卡
    /// :34084  for each：OnIntelTriggered(item, GetCardFromID(instigatorID), numberOfCardsSeen)
    ///         ★ 注意次序：**先通知**，且传的是**原始** numberOfCardsSeen（不是 min）
    /// :34107  GetCardsInHandBySide(Switch(side, {0→0, 1→2, 2→1}))   ; ★ 取**对手**手牌
    /// :34137  for each handCard：if (handCard.cardSeen) 跳过
    /// :34201      否则 oppositeSideUnseenCards.Add(handCard)
    /// :34150  if (oppositeSideUnseenCards 非空)：
    /// :34166      Array_ShuffleFromStream(oppositeSideUnseenCards, cardsRandomStream)
    /// :34170      min = Min(numberOfCardsSeen, 长度)
    /// :34172      Array_Resize(oppositeSideUnseenCards, min)
    /// :34174      ApplySetCardsSeenByCipher(oppositeSideUnseenCards, enemyTurn=False, showAnimation=True)
    /// </code>
    /// ⇒ 语义一句话：**从对手的「未见面」手牌里随机挑 `min(n, 未见面数)` 张标成已见**，
    /// 并把 `OnIntelTriggered` 广播给订阅者。
    ///
    /// ⚠️ 与参考实现 `EngineHost.cs:2132-2156` 的差异（它**不忠实**，不要照抄）：
    /// 它洗的是**下标数组**、只写 `seenByCipher` 字段、不调 `ApplySetCardsSeenByCipher`、
    /// 也不逐个发 `OnIntelTriggered`（只发一次 `FireIntelTriggers(src, n)`）。
    /// 蓝图是**洗候选卡本身**（`Array_ShuffleFromStream(oppositeSideUnseenCards, …)`）
    /// 再截断 —— 这会**改变随机流消耗点**，进而影响后续所有随机效果。
    /// </summary>
    public void SetCardsSeenByCipher(int numberOfCardsSeen, int instigatorID, Side side)
    {
        // ---- ① 先广播触发点 28（:34052-34084）----
        var instigator = GetCardFromID(instigatorID);
        FireTrigger(IntelTrigger, subject: null, side,
                    eventSubject: instigator,
                    eventArgs: new object?[] { instigator, numberOfCardsSeen },
                    namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["intelCard"] = instigator,
                        ["intelValue"] = numberOfCardsSeen,
                    },
                    localsSeed: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        // 形参名逐字取蓝图卡版（`BP_CardFunctions.g.cs:34084`
                        // `OnIntelTriggered(item, card, numberOfCardsSeen)`；
                        // 权威形参名见 `_index.g.cs:4083` `{ "intelCard", "intelValue" }`）。
                        ["intelCard"] = instigator,
                        ["intelValue"] = numberOfCardsSeen,
                    });

        // ---- ② 攒「对手手牌里还没见过的卡」（:34107-34201）----
        var unseen = new List<CardInstance>();
        foreach (var card in State.Hand(side.Opposite()))
        {
            if (!card.CardSeen)
            {
                unseen.Add(card);
            }
        }

        if (unseen.Count == 0)
        {
            return;
        }

        // ---- ③ 洗牌 + 截断 + 标记（:34150-34174）----
        State.Random.Shuffle(unseen);
        State.TraceRandom($"SetCardsSeenByCipher n={numberOfCardsSeen} unseen={unseen.Count} " +
                          $"take={Math.Min(numberOfCardsSeen, unseen.Count)}");

        int take = Math.Min(numberOfCardsSeen, unseen.Count);
        unseen.RemoveRange(take, unseen.Count - take);
        ApplySetCardsSeenByCipher(unseen, enemyTurn: false, showAnimation: true);
    }

    /// <summary>
    /// `AddIntelToCard(cardID, instigatorID, amount, out qqq)`
    /// （`BP_CardFunctions.g.cs:356-400`）：
    /// <code>
    /// :368  cardID &gt; 0 且 :372 amount != 0
    /// :382      card.cipher = Clamp(card.cipher + amount, 0, 9)
    /// :388  IsActionProcess ⇒ :392 NotifyAddIntelToCard(Notifier, …)   ; 纯客户端通知
    /// </code>
    /// 加它是因为 `GotchaTriggered` 的 `cipher &gt; 0` 门（`:23766`）读的就是这个成员 ——
    /// **没有这个写入方，那道门永远是假**。
    ///
    /// ⚠️ CDO 默认值缺失（如实标注）：`cipher` 在客户端是卡 CDO 的成员，
    /// `decompiled/cards.all.json` 里 **25 张卡**带非 0 的 `cipher`
    /// （`card_event_espionage_skirm`=9 / `card_event_infiltrate`=2 / `card_unit_7th_scottish_borderers`=3 …），
    /// 但**52 张 gotcha 卡一张都没有** ⇒ gotcha 卡的 `cipher` 初值就是 0。
    /// 本内核的卡数据（`docs/cards.live.json` / `docs/card-effects.json`）
    /// **不含 `cipher` 字段**，而补 CDO 表要改 `Cards/CardVarDefaults.cs`（本轮不在改动范围内）
    /// ⇒ 那 25 张卡的 `cipher` 初值目前是 0（偏小）。影响面：`card_event_act_on_the_intel`
    /// 的 `unSeenCards - self.cipher` 与 `card_unit_16th_tarnow_regiment` 的
    /// `cardPlayed.cipher &gt; 0`。**已核实为已知缺口**。
    /// </summary>
    public void AddIntelToCard(int cardId, int amount)
    {
        if (cardId <= 0 || amount == 0)
        {
            return;
        }

        if (GetCardFromID(cardId) is not { } card)
        {
            return;
        }

        card.Cipher = Math.Clamp(card.Cipher + amount, 0, 9);
    }

    /// <summary>
    /// `GetActiveGotchasOrdered(out cardIDs)`（`BP_CardFunctions.g.cs:18623-18892`）。
    ///
    /// ## 逐行
    ///
    /// <code>
    /// :18634  backUpNextGotcha = 100
    /// :18640  for each item in GetAllCardInBattle()：
    /// :18658      if (item.name == "card_event_careless_talk") PrintString("bingo")   ; 纯调试
    /// :18733      if (!(IsGotcha(item) &amp;&amp; item.gotchaActivated &gt; 0)) continue
    /// :18764      if (item.name == "card_event_interception") Map_Add(activeGotchas, item.cardID * -1, item.cardID)
    /// :18790      if (item.name == "card_event_ultra")         Map_Add(activeGotchas, (item.cardID + 1000000) * -1, item.cardID)
    /// :18810      if (Map_Find(activeGotchas, item.gotchaActivated))     ; ★ 激活号撞车
    /// :18830          PrintString("Error: two gotchas w same number, IDs: …")
    /// :18838          if (Map_Contains(activeGotchas, backUpNextGotcha)) LogError("BACKUP GOTCHA ACTIVATED NOW WORKING")
    /// :18854          Map_Add(activeGotchas, backUpNextGotcha, item.cardID) ; backUpNextGotcha++
    /// :18874      否则 Map_Add(activeGotchas, item.gotchaActivated, item.cardID)
    /// :18687  keys = Map_Keys(activeGotchas)
    /// :18699  逐个取 MinOfIntArray(keys) → :18701 Map_Find → :18703 orderedCards.Add(value)
    /// :18707      Array_Remove(keys, indexOfMin)                        ; ⇒ 按键**升序**
    /// :18719  cardIDs = orderedCards
    /// </code>
    ///
    /// ## 输出顺序（这是它存在的唯一理由）
    ///
    /// 键的升序 = **`-(cardID+1000000)`（ultra） → `-cardID`（interception）
    /// → 激活号 1,2,3…（其余） → 100+（撞车备份）**。
    /// 也就是说 **ultra / interception 永远排在普通反制卡之前**，
    /// 而不是按 `LocationNumber` 或入场顺序 —— 这正是「反制结算顺序」的判据。
    ///
    /// ⚠️ 遍历集：蓝图是 `GetAllCardInBattle()`（= `AllCardsInBattle` 的插入序，
    /// `_deps/BP_GameState_Battle.g.cs:1571-1591`），内核用 `State.AllCards`（`CardId` 升序）。
    /// 因为输出按键排序，只有「撞车时分配备份键」的顺序会受影响（正常对局不会撞车）。
    /// </summary>
    public List<int> GetActiveGotchasOrdered()
    {
        const int BackUpStart = 100;

        var activeGotchas = new Dictionary<int, int>();
        int backUpNextGotcha = BackUpStart;

        foreach (var item in State.AllCards)
        {
            if (!IsGotcha(item) || item.GotchaActivated <= 0)
            {
                continue;
            }

            // ★ 特例键：interception / ultra 用**负数**，于是它们排在所有正激活号之前。
            if (item.Name == "card_event_interception")
            {
                activeGotchas[item.CardId * -1] = item.CardId;
                continue;
            }

            if (item.Name == "card_event_ultra")
            {
                activeGotchas[(item.CardId + 1000000) * -1] = item.CardId;
                continue;
            }

            if (activeGotchas.TryGetValue(item.GotchaActivated, out int clash))
            {
                // 激活号撞车（蓝图会 PrintString 报警）—— 退到 100, 101, …
                _ = clash;
                activeGotchas[backUpNextGotcha] = item.CardId;
                backUpNextGotcha++;
            }
            else
            {
                activeGotchas[item.GotchaActivated] = item.CardId;
            }
        }

        return activeGotchas.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
    }

    /// <summary>
    /// ★★ `GotchaTriggered(card, instigatorID, stopFurtherCardActions, skipDiscardingOrder, out qqq)`
    /// —— `BP_CardFunctions.g.cs:23719-23912`，**逐行**。
    ///
    /// <code>
    /// :23733  TmpGotcha = card
    /// :23735  TmpGotcha.gotchaActivated = 0                      ; ★ 复位，不是置 1
    /// :23737  GetTurnNumber()
    /// :23739  TmpGotcha.enterPlayOnTurn = turnNumber
    /// :23741  TmpGotcha.location = 8                             ; ★ Discard
    /// :23743  GetHandLocationBySide(TmpGotcha.side)
    /// :23745  RearrangeLocation(handLocation)                    ; ★ 压紧那一方的手牌
    /// :23751  IsActionProcess
    /// :23766  TmpGotcha.cipher &gt; 0
    /// :23781      SetCardsSeenByCipher(cipher, TmpGotcha.cardID, TmpGotcha.side, out qqq)
    /// :23793  IsActionProcess
    /// :23808      NotifyGotchaTriggered(Notifier, cardID, instigatorID, stopFurtherCardActions)
    /// :23810  FetchAllCardsWithEventTrigger(21)
    /// :23839      item.OnCounterMeasureTriggered(**TmpGotcha**, out qqq)   ; ★ 实参是 gotcha 卡
    /// :23857  if (!stopFurtherCardActions) → :23902 SetStopFurtherActions(False)
    /// :23859      SetStopFurtherActions(True)
    /// :23863      GetCardFromID(instigatorID).enterPlayOnTurn = 0
    /// :23869      IsOrder(instigator) &amp;&amp; skipDiscardingOrder
    /// :23890          instigator.customJson = JsonMakeField(customJson, "cancelOrderRemove",
    ///                                                       JsonMakeBool(skipDiscardingOrder))
    /// </code>
    ///
    /// ## ⚠️ 控制流：`SetStopFurtherActions(False)` 在 `True` 分支里**到不了**
    ///
    /// 这一段用的是 UE 的执行流栈（`PushExecutionFlow` / `PopExecutionFlow[IfNot]`），
    /// 直译产物见 `:23856-23904`。按 UE 语义
    /// （`PopExecutionFlowIfNot` **条件为假才弹栈并跳转**；这一点用
    /// `SuppressMultipleUnits` 的三级 push（`:35773-35777`）与它自己的文档注释交叉验证过）
    /// 把栈逐条走完，得到：
    /// <code>
    /// 栈 [1505, 1008, 492]（`:23731` / `:23749` / `:23750` 三次 Push）
    /// :23857 JumpIfNot(1463, stopFurtherCardActions)
    ///     真 → :23859 SetStopFurtherActions(True) → :23863 enterPlayOnTurn=0
    ///          → :23869 IsOrder &amp;&amp; skipDiscardingOrder
    ///             真 → :23890 写 customJson → :23904 PopExecutionFlow → **1505 = Return**
    ///             假 → :23900 PopExecutionFlowIfNot → **1505 = Return**
    ///     假 → :23902 SetStopFurtherActions(False) → :23904 Jump(1136)
    ///          → :23869 IsOrder &amp;&amp; skipDiscardingOrder → 同样两条路都回 Return
    /// </code>
    /// ⇒ **两条分支都做**「`IsOrder(instigator) &amp;&amp; skipDiscardingOrder` ⇒ 写
    /// `customJson["cancelOrderRemove"]`」，差别只有 `SetStopFurtherActions` 的取值与
    /// `instigator.enterPlayOnTurn = 0`（后者只在 `True` 分支）。
    /// 而 `True` 分支**不会**再调 `SetStopFurtherActions(False)` ——
    /// 标志会**一直保持 true**，直到调用方 `AfterWaitCardPlayFromHand`（`:624`）
    /// 在下一次动作开始时复位。这是蓝图的真实形状，**照抄不改**（如实记录这处可疑点）。
    ///
    /// ## ⚠️ `IsActionProcess`
    ///
    /// `:23751` / `:23793` 两道 `IsActionProcess` 门。内核不建模「动作流程 / 回放流程」
    /// 这一层（既有约定，见 `CardApi.SuppressUnit` 的注释
    /// 「①②③ 各自还带 IsActionProcess() 分流…一律按主流程实现」）⇒ 两道门都按**真**处理。
    /// 与直译产物交叉验证：`IsActionProcess` 为真时
    /// `:23782` 的 `PopExecutionFlow` 弹出的正是 **492**（→ `:23792` 的第二次
    /// `IsActionProcess` 检查），随后 `:23807` 不弹栈、直接落到 `:23808` 的
    /// `NotifyGotchaTriggered` —— 也就是**通知 + 反制循环确实会执行**。
    /// 若按假处理，`:23808-23855` 整段（含 `OnCounterMeasureTriggered` 的 13 个订阅者）
    /// 都进不去。
    /// </summary>
    public void GotchaTriggered(CardInstance gotcha, int instigatorID,
                                bool stopFurtherCardActions, bool skipDiscardingOrder)
    {
        // ---- :23735 ----
        // ★ 蓝图置的是 **0**（不是 bool true，也不是递增）。递增发生在
        //   `PlayCardDirectlyFromHand:28310-28316`（打出反制卡时）。
        gotcha.GotchaActivated = 0;

        // ---- :23737-23739 ----
        gotcha.EnteredPlayOnTurn = GetTurnNumber();

        // ---- :23741 ----
        gotcha.Location = CardLocation.Discard;

        // ---- :23743-23745 ----
        RearrangeLocation(GetHandLocationBySide(gotcha.Owner));

        // ---- :23751 / :23766 / :23781 ----
        // 内核不建模 IsActionProcess（按主流程 = 真）。
        if (gotcha.Cipher > 0)
        {
            SetCardsSeenByCipher(gotcha.Cipher, gotcha.CardId, gotcha.Owner);
        }

        // ---- :23793 / :23808 ----
        // `NotifyGotchaTriggered(CardFunctionsNotifier, …)` 是纯客户端通知，
        // 内核没有 notifier（与 `AddToBattleLog` 同一条约定），不实现。

        // ---- :23810-23855 ----
        // ★★ 实参必须是 **gotcha 卡**（`:23839` 的第二项是 `TmpGotcha`）。
        // 订阅者读的是 `countermeasureTriggering.originalSide / cardID / side`
        // （`card_unit_the_tigers` 的 locals 体，`docs/card-ir.json`），
        // 传错卡 ⇒ 整族 13 张卡的判据全部读错对象。
        FireTrigger(CounterMeasureTrigger, subject: null, gotcha.Owner,
                    eventSubject: gotcha,
                    namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["countermeasureTriggering"] = gotcha,
                    },
                    localsSeed: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        // ★ 形参名逐字取权威签名（`_index.g.cs:4071`
                        // `OnCounterMeasureTriggered` = `{ "countermeasureTriggering", "qqq" }`）。
                        // 13 张订阅者的 locals 体读的就是这个名字
                        // （`card_unit_the_tigers` / `card_unit_2_marine_division` / …）。
                        ["countermeasureTriggering"] = gotcha,
                    });

        // ---- :23857-23904 ----
        var instigator = GetCardFromID(instigatorID);

        if (stopFurtherCardActions)
        {
            SetStopFurtherActions(true);            // :23859
            if (instigator is not null)
            {
                instigator.EnteredPlayOnTurn = 0;   // :23863
            }
        }
        else
        {
            SetStopFurtherActions(false);           // :23902
        }

        // :23869 / :23890（两条分支的公共落点）
        if (instigator is not null && IsOrder(instigator) && skipDiscardingOrder)
        {
            JsonSetBool(instigator, "cancelOrderRemove", skipDiscardingOrder);
        }
    }

    /// <summary>
    /// ★ **反制卡装填**：`PlayCardDirectlyFromHand` 里给 `gotchaActivated` 赋值那一段
    /// （`BP_CardFunctions.g.cs:28080-28316`，逐行）。
    ///
    /// <code>
    /// :28080  card.location == 4 (HandRight) || :28082 card.location == 3 (HandLeft)
    /// :28088  IsGotcha(card)                                    ; 不是反制卡 ⇒ 整段跳过
    /// :28092  card.gotchaActivated &gt; 0                          ; 已经激活过
    /// :28096      card.gotchaActivated = 0                      ;   ⇒ 正常打出，取消激活
    /// :28239  否则 _nextGotchaActivated = 0（局部零初始化）
    /// :28243  for each item in GetAllCardInBattle()：
    /// :28259      IsGotcha(item)
    /// :28261      item.side == card.side                        ; ★ 只数**同阵营**
    /// :28263      item.gotchaActivated &gt; _nextGotchaActivated
    /// :28289          _nextGotchaActivated = item.gotchaActivated
    /// :28310  _nextGotchaActivated += 1
    /// :28316  card.gotchaActivated = _nextGotchaActivated       ; ★ 从 1 起
    /// </code>
    ///
    /// ## 为什么必须有这个写入方
    ///
    /// `gotchaActivated` 的**唯一**写入方就是这段（蓝图全量 grep：
    /// 只有 `:28096` / `:28316` / `GotchaTriggered:23735` 三处，后一处是**置 0**）。
    /// 没有它，`gotchaActivated` 恒为 0 ⇒ `GetActiveGotchasOrdered` 的 `&gt; 0` 过滤
    /// 恒空 ⇒ 整个反制子系统空转。
    ///
    /// ⚠️ **调用点未接线**：蓝图里这段在 `PlayCardDirectlyFromHand` 内部，而那个库函数
    /// 在本内核的派发表里**仍是缺口**（15 个 IR 调用点）⇒ 本方法目前**只能被自测调用**，
    /// 出牌流水线还没有走到它。如实标注为「写入方已就位、接线未做」。
    /// </summary>
    public void AssignGotchaActivatedOnPlayFromHand(CardInstance card)
    {
        // :28080/:28082 —— 只对**在手牌里**的卡
        if (card.Location is not (CardLocation.HandLeft or CardLocation.HandRight))
        {
            return;
        }

        // :28088
        if (!IsGotcha(card))
        {
            return;
        }

        // :28092/:28096 —— 已经激活过的（再次正常打出）⇒ 取消激活
        if (card.GotchaActivated > 0)
        {
            card.GotchaActivated = 0;
            return;
        }

        // :28239-28289 —— 取同阵营已激活反制卡里最大的激活号
        int next = 0;
        foreach (var other in State.AllCards)
        {
            if (IsGotcha(other) && other.Owner == card.Owner && other.GotchaActivated > next)
            {
                next = other.GotchaActivated;
            }
        }

        // :28310-28316 —— 递增赋值（第一张 = 1）
        card.GotchaActivated = next + 1;
    }

    // ==================================================================
    //  未实现统计
    // ==================================================================

    /// <summary>记录一次「内核还没实现」的调用。这是衡量进度最重要的指标。</summary>
    public void NotifyUnimplemented(string name)
    {
        State.UnimplementedCalls[name] = State.UnimplementedCalls.GetValueOrDefault(name) + 1;
    }

    /// <summary>
    /// 取（或建）某个来源在卡上的 buff 槽。
    /// <paramref name="temporary"/> 是键的一部分 —— 见
    /// <see cref="CardInstance.BuffsBySource"/> 上那段说明：临时的和永久的两笔
    /// 必须分开存，否则回合结束清理会把永久那笔一起删掉。
    /// </summary>
    private static CardBuff GetOrCreateBuff(CardInstance card, int sourceCardId, bool temporary = false)
    {
        var key = (sourceCardId, temporary);
        if (!card.BuffsBySource.TryGetValue(key, out var buff))
        {
            buff = new CardBuff { SourceCardId = sourceCardId, Temporary = temporary };
            card.BuffsBySource[key] = buff;
        }

        return buff;
    }
}
