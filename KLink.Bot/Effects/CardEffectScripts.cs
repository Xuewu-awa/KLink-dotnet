using KLink.Bot.Engine;

namespace KLink.Bot.Effects;

/// <summary>Explicit effect overrides for tests or callers. Production cards execute pak IR.</summary>
public static class CardEffectScripts
{
    private static readonly Dictionary<string, Action<EffectContext>> ByCardName = new(StringComparer.Ordinal);

    public static bool TryGet(string cardName, out Action<EffectContext> script)
        => ByCardName.TryGetValue(cardName, out script!);

    public static int Count => ByCardName.Count;

    public static IEnumerable<string> ImplementedCards => ByCardName.Keys.OrderBy(k => k, StringComparer.Ordinal);

    /// <summary>显式覆盖一张卡的效果；生产数据由完整 Blueprint IR 执行。</summary>
    public static void Register(string cardName, Action<EffectContext> script) => ByCardName[cardName] = script;

    /// <summary>移除临时覆盖；主要供隔离测试恢复真实 Blueprint IR。</summary>
    public static bool Unregister(string cardName) => ByCardName.Remove(cardName);


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
