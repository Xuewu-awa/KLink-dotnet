namespace fyserver.Models;

/// <summary>
/// 对局配置（config/match.json）。
/// </summary>
public class MatchConfig
{
    /// <summary>
    /// 开局卡牌是否全部按金卡下发（对应开局数据里每张牌的 is_gold）。
    /// true = 金卡（KLink.Server 原本的行为）；false = 普通卡（fyserver 原实现）。
    /// </summary>
    public bool AllGoldCards { get; set; } = true;
}
