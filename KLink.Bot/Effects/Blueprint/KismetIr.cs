using System.Text.Json;
using System.Text.Json.Serialization;

namespace KLink.Bot.Effects.Blueprint;

/// <summary>
/// Blueprint 字节码 IR —— 由 <c>klink bot/tools/gen-kismet-ir.py</c> 从完整字节码生成。
///
/// 设计依据（已验证）：
/// - 事件 stub 里的 <c>ExecuteUbergraph_X(&lt;entry&gt;)</c> 实参，等于 ubergraph 中该事件链
///   第一条语句的 StatementIndex（1636 张卡全部命中）
/// - 局部变量名 <c>CallFunc_&lt;函数名&gt;_&lt;参数名&gt;</c> 就是该调用的输出槽，数据流由此可还原
/// - <c>JumpIfNot</c> 的 Offset 给出控制流
/// </summary>
public sealed class KismetLibrary
{
    private readonly Dictionary<string, KismetCard> _cards = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>进程内共享的实例 —— 由 <see cref="Initialize"/> 首次加载。</summary>
    public static KismetLibrary? Default { get; private set; }

    /// <summary>加载失败时的原因（供运行器提示，不抛异常）。</summary>
    public static string? LoadError { get; private set; }

    public static void Initialize(string path)
    {
        try
        {
            Default = Load(path);
            LoadError = null;
        }
        catch (Exception ex)
        {
            Default = null;
            LoadError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    public int CardCount => _cards.Count;

    /// <summary>
    /// 事件名 → 实现了该事件的卡名列表。
    ///
    /// 这张索引让「触发事件」从 O(场上所有卡) 降到 O(订阅者)，
    /// 而且天然只命中真正实现了该事件的卡 —— IR 里没有的程序就不会被调用。
    /// </summary>
    private Dictionary<string, List<string>>? _triggerIndex;

    public IReadOnlyList<string> Subscribers(string programName)
    {
        _triggerIndex ??= BuildTriggerIndex();
        return _triggerIndex.TryGetValue(programName, out var list) ? list : Array.Empty<string>();
    }

    /// <summary>所有出现过的触发事件名（不含 OnPlayedFromHand，那是主动打出）。</summary>
    public IEnumerable<string> AllProgramNames()
    {
        _triggerIndex ??= BuildTriggerIndex();
        return _triggerIndex.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, List<string>> BuildTriggerIndex()
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cardName, card) in _cards)
        {
            foreach (string program in card.Entrypoints.Keys.Concat(card.Locals.Keys.Where(IsEventName)))
            {
                if (program.StartsWith("OnPlayedFromHand", StringComparison.OrdinalIgnoreCase))
                {
                    continue;   // 主动打出，不是触发
                }

                if (!index.TryGetValue(program, out var list))
                {
                    list = new List<string>();
                    index[program] = list;
                }

                list.Add(cardName);
            }
        }

        return index;
    }

    public KismetCard? Find(string cardName) => _cards.GetValueOrDefault(cardName);

    /// <summary>
    /// 全部卡（卡名 → 卡）。
    ///
    /// 加它是为了「派发表静态缺口守卫」（`tools/BotSim/DispatchGap.cs`）：
    /// 那个守卫必须遍历**全部**卡的 `Steps` 才能算出「IR 会调用、派发表里没有、
    /// 而且 locals 也兜不住」的完整集合。除此之外没有别的用途，
    /// 所以返回的是只读视图（`_cards` 本身不暴露）。
    ///
    /// ⚠️ 名字**不能**叫 `Cards`：本文件里已有 `Cards.CardDatabase.ResolveBaseName(...)`
    /// 这种限定写法，同名属性会把它遮蔽掉（编译期 CS0120）。
    /// </summary>
    public IReadOnlyDictionary<string, KismetCard> AllCards => _cards;

    /// <summary>
    /// 按卡名 + 事件名取程序，例如 ("card_event_aans", "OnPlayedFromHand")。
    /// 卡组里的 <c>xxx_bal</c> / <c>xxx_vet</c> 是数据变体，蓝图逻辑挂在基础卡上，
    /// 所以找不到时剥掉后缀再试。
    ///
    /// # ⚠️ 这个函数修的是一个「触发式效果大面积静默失效」的严重 bug（2026-09-27）
    ///
    /// IR 把一个卡的**整个 ubergraph** 编成一个程序，每个事件只是里面一个入口下标，
    /// 入口分派是**执行流栈**（`pushFlow` / `popFlow`）干的：
    ///
    /// <code>
    /// pos 0   pushFlow(895)     ← 分派第一条：记住返回地址，然后**继续往下**（不是跳转）
    /// pos 1   pushFlow(860)     ← 初始化块地址
    /// pos 2   jump 84           ← 去跑初始化块
    /// ...
    /// pos 14  popFlow           ← 初始化块结束 → 回到 pos 1
    /// pos 15  jump 895          ← 这次才真的进事件体
    /// ...
    /// pos N   popFlow           ← 事件体结束 → 回到 pos 0；栈空 ⇒ 本次事件结束
    /// </code>
    ///
    /// 旧实现是 `new KismetProgram(card.Steps, entry)`，**从入口下标直接开始跑**，
    /// 整段分派前导被跳过。后果：进入事件体后第一次遇到 `popFlow` 时栈是空的，
    /// VM 按「返回调度器」处理、立刻结束。实测
    /// `card_unit_214th_amur` 的 `OnEnterPlay` 只执行 **2 步**就退出，
    /// `card_unit_big_red_one` 的 `OnEnterPlay` 只跑到「取列表长度」。
    /// 而 IR 的 `entrypoints` 表看起来完全正常 —— 所以这个 bug 一直没被发现，
    /// 表现成「这些卡没实现效果」。
    ///
    /// 修法：在解释器层重建前导（`pushFlow` 前缀 + 从入口可达的语句闭包 + 收尾 `return`）。
    /// 用**可达闭包**而不是「入口 → 第一个 return」切片：实测循环体常常排在入口**前面**
    /// （`card_unit_big_red_one` 的循环在 0..403，入口在 895），按顺序切片会丢循环体。
    /// </summary>
    public KismetProgram? FindProgram(string cardName, string programName)
    {
        foreach (string candidate in Candidates(cardName))
        {
            if (!_cards.TryGetValue(candidate, out var card))
            {
                continue;
            }

            if (!card.Entrypoints.TryGetValue(programName, out int entry))
            {
                // ⚠️ 2026-09-30（P1 Deployment）：**回退查 `locals`**。
                //
                // 带 `out` 参数的事件处理函数是独立 export 的函数图（函数体自成一套
                // 语句下标，入口 = 第一条语句），生成器把它们编进了 `locals`。
                // 不回退的话，`FireTrigger("OnBeforeOtherCardDeploymentTrigger", …)`
                // 这类派发会**静默找不到程序**——163 个函数体白编。
                //
                // 用 `FindLocalProgram` 而不是 `BuildEntryProgram`：后者会合成
                // ubergraph 的分派前导（`pushFlow`/`jump`），对独立函数是错的。
                if (card.Locals.ContainsKey(programName))
                {
                    return FindLocalProgram(candidate, programName);
                }

                continue;
            }

            if (!_programCache.TryGetValue(candidate, out var perCard))
            {
                perCard = new Dictionary<string, KismetProgram>(StringComparer.OrdinalIgnoreCase);
                _programCache[candidate] = perCard;
            }

            if (!perCard.TryGetValue(programName, out var program))
            {
                program = BuildEntryProgram(card, entry);
                perCard[programName] = program;
            }

            return program;
        }

        return null;
    }

    /// <summary>卡名 → (事件名 → 重建好前导的入口程序)。</summary>
    /// <remarks>
    /// 为什么要缓存：`FireTrigger` 一次派发就要对场上每张卡、每个程序名查一遍，
    /// 而重建前导是 O(语句数)。不缓存的话这里会变成整局最热的地方。
    /// </remarks>
    private readonly Dictionary<string, Dictionary<string, KismetProgram>> _programCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>卡名 → (局部函数名 → 程序)。见 <see cref="FindLocalProgram"/>。</summary>
    private readonly Dictionary<string, Dictionary<string, KismetProgram>> _localCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 取一张卡**自己的局部函数**（例如 <c>GetPlayFromHandDamage</c>）编成的程序。
    ///
    /// 和 <see cref="FindProgram"/> 的区别：<see cref="FindProgram"/> 取的是**事件入口**
    /// （入口点在 ubergraph 里，需要重建分派前导）；这里取的是独立 export 的
    /// **普通函数**，语句表就是函数体本身，入口 = 第一条语句，不需要任何前导。
    ///
    /// 找不到返回 null —— 调用方必须把 null 当「这张卡没有这个函数」处理，
    /// 不能拿一个默认值顶替（否则就是猜）。
    /// </summary>
    public KismetProgram? FindLocalProgram(string cardName, string functionName)
    {
        foreach (string candidate in Candidates(cardName))
        {
            if (!_cards.TryGetValue(candidate, out var card)
                || !card.Locals.TryGetValue(functionName, out var steps)
                || steps.Count == 0)
            {
                continue;
            }

            if (!_localCache.TryGetValue(candidate, out var perCard))
            {
                perCard = new Dictionary<string, KismetProgram>(StringComparer.OrdinalIgnoreCase);
                _localCache[candidate] = perCard;
            }

            if (!perCard.TryGetValue(functionName, out var program))
            {
                program = new KismetProgram(steps, steps[0].Index)
                {
                    InputNames = card.LocalInputs.GetValueOrDefault(functionName),
                    OutputNames = card.LocalOutputs.GetValueOrDefault(functionName),
                };
                perCard[functionName] = program;
            }

            return program;
        }

        return null;
    }

    /// <summary>合成语句（前导复用 + 收尾 return）用的下标，负数避开真实 StatementIndex。</summary>
    private const int SyntheticIndexBase = -1_000_000;

    /// <summary>把入口下标展开成一个**自带分派前导**的独立程序。见 <see cref="FindProgram"/>。</summary>
    private static KismetProgram BuildEntryProgram(KismetCard card, int entry)
    {
        // StatementIndex → 数组下标（**跳转目标**用）。
        var byIndex = new Dictionary<int, int>(card.Steps.Count);
        for (int i = 0; i < card.Steps.Count; i++)
        {
            byIndex[card.Steps[i].Index] = i;
        }

        if (!byIndex.TryGetValue(entry, out int entryPos))
        {
            // 入口不在语句表里：返回空程序（调用方拿不到可执行的步骤就跳过，不会崩）
            return new KismetProgram(Array.Empty<KismetStep>(), 0);
        }

        var steps = new List<KismetStep>();
        int synthetic = SyntheticIndexBase;

        // ---- 1) 入口分派前导 ----
        //
        // 客户端的入口分派长这样（`ExecuteUbergraph` 结尾是 `ComputedJump(EntryPoint)` 那条跳转表）：
        //
        //     i=0   PushExecutionFlow(Offset = 事件跑完该回到的下标)   ← 只有一部分卡有
        //     i=5   ComputedJump(EntryPoint)
        //     ...
        //
        // 也就是说：**分派在事件体的结尾弹回 i=0，靠 `ComputedJump` 直接结束**。
        // 事件体自己会在中间 push/pop 好几层（循环体、共享尾段），
        // 所以「刚进事件体时栈上有什么」决定了那些 pop 能不能对上 ——
        // 少了这条 pushFlow，第一次 `popFlowIfNot` 就会弹空、把整个事件提前结束。
        // 实测 `card_unit_214th_amur` 的 `OnEnterPlay` 只跑 2 步就退出、
        // `card_unit_big_red_one` 的 `OnEnterPlay` 只跑到「取列表长度」。
        //
        // 判据：**首条语句是 `pushFlow` 才有这层**（`card_unit_85_pioneer_company`
        // 的首条就是 `ComputedJump`，没有）。返回地址就是它的 `Offset`
        // （实测 214th=1195 / big_red_one=1499 / committed_crew=1885，与各自末尾 Return 一致）。
        int dispatchReturn = card.Steps.Count > 0 && card.Steps[0].Op == "pushFlow"
            ? card.Steps[0].JumpTarget
            : -1;
        if (dispatchReturn >= 0)
        {
            steps.Add(Synthetic("<dispatch pushFlow>", synthetic++, "pushFlow", to: dispatchReturn));
        }

        steps.Add(Synthetic("<dispatch jump>", synthetic++, "jump", to: entry));

        // ---- 2) 从入口可达的语句闭包 ----
        //
        // 顺序下落 = 数组里的**下一条**：IR 的步骤已按字节码偏移排好
        // （实测 1636 张卡全部 `bo` 单调，见 tools/gen-kismet-ir.py 里 `bo` 的注释）。
        // 跳转目标按 StatementIndex 查 `byIndex`。
        //
        // 为什么不按「入口 → 第一个 return」切片：实测循环体常常排在入口**前面**
        // （`card_unit_big_red_one` 的循环在 0..403，入口在 895），切片会丢循环体。
        var reachable = new HashSet<int> { entryPos };
        var queue = new Queue<int>();
        queue.Enqueue(entryPos);

        while (queue.Count > 0)
        {
            int pos = queue.Dequeue();
            KismetStep step = card.Steps[pos];

            // `jump` 不落空；`return` 之后没有下一条
            if (step.Op is not ("jump" or "return"))
            {
                int next = pos + 1;
                if (next < card.Steps.Count && reachable.Add(next))
                {
                    queue.Enqueue(next);
                }
            }

            if (step.Op is "jump" or "jumpIf" or "jumpIfNot" or "pushFlow"
                && byIndex.TryGetValue(step.JumpTarget, out int target)
                && reachable.Add(target))
            {
                queue.Enqueue(target);
            }
        }

        foreach (int pos in reachable.OrderBy(p => p))
        {
            steps.Add(card.Steps[pos]);
        }

        // ---- 3) 收尾 return ----
        // 客户端的 `ComputedJump` 是「执行流栈弹回 i=0 之后直接结束」；
        // IR 里没有那条指令，补一条 return 当落脚点。
        steps.Add(Synthetic("<end of entry>", synthetic++, "return"));

        // ⚠️ `KismetProgram.Entry` 存的是**语句的 StatementIndex**（不是数组下标）——
        //    `KismetVm.Run` 是拿它去查 `byIndex`。所以这里必须给首条语句的 Index，
        //    给 0 会直接撞 `<missing-entry>` 然后一步都不跑。
        return new KismetProgram(steps, steps[0].Index);
    }

    private static KismetStep Synthetic(string label, int index, string op, int to = -1)
        => new(index, op, label, Array.Empty<KismetExpr>(), Array.Empty<int>(),
               null, null, to, null, null);

    /// <summary>该卡注册了哪些事件（含变体回退）。</summary>
    /// <remarks>
    /// ⚠️ 2026-09-30（P1 Deployment）：**必须并上 `locals` 里的独立事件函数**。
    ///
    /// 带 `out` 参数的事件处理函数（`OnBeforeOtherCardDeploymentTrigger` /
    /// `OnDeploymentEffectTriggered` / `OnDestructionEffectTriggered` /
    /// `OnCardDealDamage_ModifyDamageDealt` …）在蓝图里是**独立 export 的函数图**，
    /// 不经过 ubergraph，所以既没有 `entrypoints` 条目、也不在 `Steps` 里 ——
    /// 它们被编进了 `locals`（见 `gen-kismet-ir.py` 的 `is_standalone_event`）。
    ///
    /// 这里漏掉的后果有两处，都是静默的：
    ///   1. <see cref="CardApi.RunCardEffect"/> 第 3 步用 `ProgramNames` 判
    ///      「这张卡是触发式卡，不是缺口」—— 只收 `entrypoints` 的话，
    ///      **99 张只有独立事件函数的卡**会被误报成"没有蓝图逻辑"。
    ///   2. <see cref="BuildTriggerIndex"/> 用它建订阅表。
    /// </remarks>
    public IEnumerable<string> ProgramNames(string cardName)
    {
        foreach (string candidate in Candidates(cardName))
        {
            if (_cards.TryGetValue(candidate, out var card))
            {
                return card.Entrypoints.Keys
                    .Concat(card.Locals.Keys.Where(IsEventName));
            }
        }

        return Enumerable.Empty<string>();
    }

    /// <summary>函数名看起来是不是事件（`On*`，且不是 `__DelegateSignature`）。</summary>
    private static bool IsEventName(string name)
        => name.StartsWith("On", StringComparison.OrdinalIgnoreCase)
           && !name.EndsWith("__DelegateSignature", StringComparison.OrdinalIgnoreCase);

    /// <summary>变体回退之后的实际卡名（IR 里存的是基础卡）。</summary>
    public string? ResolveCardName(string cardName)
    {
        foreach (string candidate in Candidates(cardName))
        {
            if (_cards.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 卡组里的 <c>xxx_bal</c> / <c>xxx_vet</c> 是**数据变体**，蓝图逻辑挂在基础卡上，
    /// 所以找不到时剥掉后缀再试。
    /// </summary>
    private static IEnumerable<string> Candidates(string cardName)
    {
        yield return cardName;

        string baseName = Cards.CardDatabase.ResolveBaseName(cardName);
        if (!string.Equals(baseName, cardName, StringComparison.OrdinalIgnoreCase))
        {
            yield return baseName;
        }
    }

    public static KismetLibrary Load(string path)
    {
        var library = new KismetLibrary();
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        foreach (var cardProp in doc.RootElement.EnumerateObject())
        {
            var entries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (cardProp.Value.TryGetProperty("entrypoints", out var epEl))
            {
                foreach (var ep in epEl.EnumerateObject())
                {
                    entries[ep.Name] = ep.Value.GetInt32();
                }
            }

            var steps = new List<KismetStep>();
            if (cardProp.Value.TryGetProperty("steps", out var stepsEl))
            {
                foreach (var s in stepsEl.EnumerateArray())
                {
                    steps.Add(ParseStep(s));
                }
            }

            // 卡自己的局部函数（`locals`）。见 gen-kismet-ir.py 的 LOCAL_FUNCTIONS：
            // 这些是**独立 export**，语句下标从 0 开始，与 ubergraph 的 StatementIndex
            // 不共享命名空间 —— 所以单独存，绝不能拼进 `steps`（拼进去跳转目标会串）。
            var locals = new Dictionary<string, IReadOnlyList<KismetStep>>(StringComparer.OrdinalIgnoreCase);
            if (cardProp.Value.TryGetProperty("locals", out var localsEl)
                && localsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var fn in localsEl.EnumerateObject())
                {
                    var fnSteps = new List<KismetStep>();
                    foreach (var s in fn.Value.EnumerateArray())
                    {
                        fnSteps.Add(ParseStep(s));
                    }

                    if (fnSteps.Count > 0)
                    {
                        locals[fn.Name] = fnSteps;
                    }
                }
            }

            var inputs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (cardProp.Value.TryGetProperty("local_inputs", out var inputsEl))
            {
                foreach (var fn in inputsEl.EnumerateObject())
                    inputs[fn.Name] = fn.Value.EnumerateArray().Select(p => p.GetString()!).ToArray();
            }

            var outputs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (cardProp.Value.TryGetProperty("local_outputs", out var outputsEl))
            {
                foreach (var fn in outputsEl.EnumerateObject())
                    outputs[fn.Name] = fn.Value.EnumerateArray().Select(p => p.GetString()!).ToArray();
            }

            var defaults = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (cardProp.Value.TryGetProperty("instance_defaults", out var defaultsEl))
                foreach (var member in defaultsEl.EnumerateObject())
                    defaults[member.Name] = member.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.String => member.Value.GetString(),
                        JsonValueKind.Number when member.Value.TryGetInt32(out var integer) => integer,
                        JsonValueKind.Number => member.Value.GetDouble(),
                        _ => null,
                    };

            library._cards[cardProp.Name] = new KismetCard(cardProp.Name, entries, steps)
            {
                Locals = locals,
                LocalInputs = inputs,
                LocalOutputs = outputs,
                InstanceDefaults = defaults,
            };
        }

        return library;
    }

    private static KismetStep ParseStep(JsonElement s)
    {
        string op = s.TryGetProperty("op", out var o) ? o.GetString() ?? "" : "";
        int index = s.TryGetProperty("i", out var i) ? i.GetInt32() : 0;
        int offset = s.TryGetProperty("bo", out var bo) ? bo.GetInt32() : 0;

        string? fn = s.TryGetProperty("fn", out var f) ? f.GetString() : null;
        string? dst = s.TryGetProperty("dst", out var d) ? d.GetString() : null;
        string? inst = s.TryGetProperty("inst", out var ins) ? ins.GetString() : null;
        int to = s.TryGetProperty("to", out var t) ? t.GetInt32() : -1;

        var args = new List<KismetExpr>();
        if (s.TryGetProperty("args", out var argsEl))
        {
            foreach (var a in argsEl.EnumerateArray())
            {
                args.Add(ParseExpr(a));
            }
        }
        else if (s.TryGetProperty("values", out var valuesEl))
        {
            // setArray：把要组装的元素放进 args
            foreach (var a in valuesEl.EnumerateArray())
            {
                args.Add(ParseExpr(a));
            }
        }

        var outs = new List<int>();
        if (s.TryGetProperty("outs", out var outsEl))
        {
            foreach (var item in outsEl.EnumerateArray())
            {
                if (item.TryGetProperty("param", out var p))
                {
                    outs.Add(p.GetInt32());
                }
            }
        }

        KismetExpr? src = s.TryGetProperty("src", out var srcEl) ? ParseExpr(srcEl) : null;
        KismetExpr? cond = s.TryGetProperty("cond", out var condEl) ? ParseExpr(condEl) : null;
        KismetExpr? recv = s.TryGetProperty("recv", out var recvEl) ? ParseExpr(recvEl) : null;

        return new KismetStep(index, op, fn, args, outs, src, cond, to, dst, inst)
        {
            Receiver = recv, ByteOffset = offset,
            DestinationScope = s.TryGetProperty("dst_scope", out var ds) && ds.ValueKind == JsonValueKind.String
                ? ds.GetString() : null,
        };
    }

    private static KismetExpr ParseExpr(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return KismetExpr.Empty;
        }

        string? var = el.TryGetProperty("var", out var v) ? v.GetString() : null;
        string? call = el.TryGetProperty("call", out var c) ? c.GetString() : null;
        string? math = el.TryGetProperty("math", out var m) ? m.GetString() : null;
        string? str = el.TryGetProperty("str", out var s) ? s.GetString() : null;
        string? obj = el.TryGetProperty("obj", out var ob) ? ob.GetString() : null;
        string? name = el.TryGetProperty("name", out var nm) ? nm.GetString() : null;
        string? unknown = el.TryGetProperty("unknown", out var un) ? un.GetString() : null;
        string? prop = el.TryGetProperty("prop", out var pr) ? pr.GetString() : null;

        // 结构体常量（实测是 GameplayTag）。生成器把成员摊平成 array，
        // 所以这里只保留结构体名，值走 Array 那条路。
        string? structName = el.TryGetProperty("struct", out var st) ? st.GetString() : null;

        int? intVal = el.TryGetProperty("int", out var iv) && iv.ValueKind == JsonValueKind.Number ? iv.GetInt32() : null;
        double? floatVal = el.TryGetProperty("float", out var fv) && fv.ValueKind == JsonValueKind.Number ? fv.GetDouble() : null;
        bool? boolVal = el.TryGetProperty("bool", out var bv) && bv.ValueKind is JsonValueKind.True or JsonValueKind.False ? bv.GetBoolean() : null;
        bool self = el.TryGetProperty("self", out var se) && se.ValueKind == JsonValueKind.True;
        bool none = el.TryGetProperty("none", out var ne) && ne.ValueKind == JsonValueKind.True;

        var args = new List<KismetExpr>();
        if (el.TryGetProperty("args", out var argsEl))
        {
            foreach (var a in argsEl.EnumerateArray())
            {
                args.Add(ParseExpr(a));
            }
        }

        var array = new List<KismetExpr>();
        if (el.TryGetProperty("array", out var arrEl))
        {
            foreach (var a in arrEl.EnumerateArray())
            {
                array.Add(ParseExpr(a));
            }
        }

        KismetExpr? ctx = el.TryGetProperty("ctx", out var ctxEl) ? ParseExpr(ctxEl) : null;

        return new KismetExpr
        {
            Var = var, Call = call, Math = math, Str = str, Obj = obj,
            Scope = el.TryGetProperty("scope", out var scope) ? scope.GetString() : null,
            ElementType = el.TryGetProperty("element_type", out var et) ? et.GetString() : null,
            Name = name, Unknown = unknown, Prop = prop,
            Int = intVal, Float = floatVal, Bool = boolVal,
            Self = self, None = none, Args = args, Array = array, Context = ctx,
            Struct = structName,
        };
    }
}

public sealed record KismetCard(string Name, IReadOnlyDictionary<string, int> Entrypoints, IReadOnlyList<KismetStep> Steps)
{
    public IReadOnlyDictionary<string, object?> InstanceDefaults { get; init; } =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    /// <summary>该卡注册了哪些事件（入口名）。</summary>
    public IEnumerable<string> ProgramNames => Entrypoints.Keys;

    /// <summary>
    /// 卡自己的**局部函数**（不是事件）的语句表，函数名 → 步骤。
    ///
    /// 这些函数编译成独立 export、**不在 ubergraph 里**，所以既不是入口点、
    /// 也不在 <see cref="Steps"/> 里。生成器收录非 UI 的独立函数及参数顺序。
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<KismetStep>> Locals { get; init; } =
        new Dictionary<string, IReadOnlyList<KismetStep>>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IReadOnlyList<string>> LocalInputs { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, IReadOnlyList<string>> LocalOutputs { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}

public sealed record KismetProgram(IReadOnlyList<KismetStep> Steps, int Entry)
{
    /// <summary>Formal input parameter order from the current FunctionExport; null for legacy IR.</summary>
    public IReadOnlyList<string>? InputNames { get; init; }
    /// <summary>Formal output order; generated caller slots may carry numeric suffixes.</summary>
    public IReadOnlyList<string>? OutputNames { get; init; }
}

public sealed record KismetStep(
    int Index,
    string Op,
    string? Function,
    IReadOnlyList<KismetExpr> Args,
    IReadOnlyList<int> OutParams,
    KismetExpr? Source,
    KismetExpr? Condition,
    int JumpTarget,
    string? DestinationVar,
    string? UnknownInst)
{
    /// <summary>
    /// 调用接收者。很多谓词只有一个输出参数（例如 <c>card.IsUnit()</c> 编译成
    /// <c>Context(card) → IsUnit(out isIt)</c>），主语就在这个字段里。
    /// </summary>
    public KismetExpr? Receiver { get; init; }
    public string? DestinationScope { get; init; }

    /// <summary>
    /// 字节码偏移（IR 的 <c>bo</c>）。
    ///
    /// ⚠️ **不能用 <see cref="Index"/> 推"下一条语句"**：IR 是按 StatementIndex 排序存的，
    /// 而真正的执行顺序要按字节码偏移排，两者实测**不一致**
    /// （`card_unit_85_pioneer_company` 的初始化块 StatementIndex 是 10..472，
    /// 而事件体 477..677 的字节码在其后 —— 但 `jump 10` 这种回跳要求把
    /// 「初始化块 → 事件体」按字节码顺序串起来）。
    /// 见 <see cref="KismetLibrary.FindProgram"/> 里重建入口程序的注释。
    /// </summary>
    public int ByteOffset { get; init; }
}

public sealed class KismetExpr
{
    public string? Var { get; init; }
    public string? Scope { get; init; }
    public string? Call { get; init; }
    public string? Math { get; init; }
    public string? Str { get; init; }
    public string? Obj { get; init; }
    public string? Name { get; init; }
    public string? Unknown { get; init; }
    public string? Prop { get; init; }
    /// <summary>Array element category from the serialized Blueprint property.</summary>
    public string? ElementType { get; init; }

    /// <summary>
    /// 结构体常量的类型名（实测只有 <c>/Script/GameplayTags.GameplayTag</c>）。
    /// 结构体的值在 <see cref="Array"/> 里（生成器把成员摊平了）。
    /// </summary>
    public string? Struct { get; init; }
    public int? Int { get; init; }
    public double? Float { get; init; }
    public bool? Bool { get; init; }
    public bool Self { get; init; }
    public bool None { get; init; }
    public IReadOnlyList<KismetExpr> Args { get; init; } = System.Array.Empty<KismetExpr>();
    public IReadOnlyList<KismetExpr> Array { get; init; } = System.Array.Empty<KismetExpr>();
    public KismetExpr? Context { get; init; }

    public static readonly KismetExpr Empty = new() { None = true };

    public override string ToString() =>
        Var is not null ? $"var({Var})" :
        Call is not null ? $"call({Call})" :
        Math is not null ? $"math({Math})" :
        Int is not null ? Int.Value.ToString() :
        Str is not null ? $"\"{Str}\"" :
        Bool is not null ? Bool.Value.ToString() :
        Self ? "self" :
        Unknown is not null ? $"<{Unknown}>" :
        "?";
}


