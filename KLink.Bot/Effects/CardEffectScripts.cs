using KLink.Bot.Engine;

namespace KLink.Bot.Effects;

/// <summary>
/// 卡牌效果的**编排层**：一张卡具体怎么把原语组合起来。
///
/// 为什么单独一层：反编译产物只给「调用了哪些函数」的集合，不给数据流。
/// 所以每张卡需要一句话描述「先取谁、再改什么、数值多少」——
/// 这句话目前靠人工写，将来可以用 Kismet 字节码里的
/// <c>CallFunc_X_ReturnValue</c> 变量名把数据流自动恢复出来（见文档 §下一阶段）。
///
/// 写法参考 <see cref="ExampleAans"/>：读起来应该和反编译结果一一对应。
/// </summary>
public static class CardEffectScripts
{
    private static readonly Dictionary<string, Action<EffectContext>> ByCardName = new(StringComparer.Ordinal)
    {
        // 样例：card_event_aans（NZANS）
        // 原文：Your HQ gets +3 defense. Gain 1 extra kredit slot.
        // 反编译：GetLocationCardBySide / ChangeDefense / GainKreditSlot
        ["card_event_aans"] = ExampleAans,
    };

    public static bool TryGet(string cardName, out Action<EffectContext> script)
        => ByCardName.TryGetValue(cardName, out script!);

    public static int Count => ByCardName.Count;

    public static IEnumerable<string> ImplementedCards => ByCardName.Keys.OrderBy(k => k, StringComparer.Ordinal);

    /// <summary>注册一张卡的效果（供手工扩表，或将来由反编译器批量生成）。</summary>
    public static void Register(string cardName, Action<EffectContext> script) => ByCardName[cardName] = script;

    // ==================================================================
    //  样例实现 —— 演示「反编译结果 → 内核代码」的对应关系
    // ==================================================================

    private static void ExampleAans(EffectContext ctx)
    {
        var api = ctx.Engine.Api;

        // GetLocationCardBySide → 取自己方的 HQ
        var hq = api.GetLocationCardBySide(ctx.Controller);
        if (hq is null)
        {
            return;
        }

        // ChangeDefense(3)
        api.ChangeDefense(hq, 3, ctx.Self);

        // GainKreditSlot
        api.GainKreditSlot(ctx.Controller, 1, ctx.Self);
    }
}

/// <summary>
/// 触发式效果的注册表。
///
/// 游戏的触发时机来自反编译出的 <c>doOn*</c> / <c>On*</c> 函数名，
/// 例如 <c>doOnStartOfTurn</c> / <c>doOnCardEnterPlay</c> / <c>doOnOtherCardDestroyed</c>。
/// v0 只搭骨架，尚未填内容。
/// </summary>
public static class CardEventScripts
{
    private static readonly Dictionary<(string Card, GameEvent Event), Action<EffectContext>> ByCardEvent = new();

    public static bool TryGet(string cardName, GameEvent evt, out Action<EffectContext> script)
        => ByCardEvent.TryGetValue((cardName, evt), out script!);

    public static void Register(string cardName, GameEvent evt, Action<EffectContext> script)
        => ByCardEvent[(cardName, evt)] = script;

    public static int Count => ByCardEvent.Count;
}
