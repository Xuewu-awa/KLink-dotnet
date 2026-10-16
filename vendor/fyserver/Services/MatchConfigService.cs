using System.Text.Json;
using fyserver.Models;
using fyserver.Serialization;

namespace fyserver.Services;

/// <summary>
/// 对局相关配置（开局卡牌外观等）的读取与热重载。
///
/// 背景：开局数据里的每张牌带 is_gold 字段，客户端据此决定显示金卡还是普通卡。
/// 原实现把它硬编码为 false（MatchManagerService 里三处），没有可配置入口；
/// KLink.Server 那边则是恒为 true。这里做成 config/match.json 可配置项，
/// 并由 Web 后台（基础信息页）切换、热重载，无需重启。
/// </summary>
public class MatchConfigService
{
    private readonly object _lock = new();
    private MatchConfig? _config;
    private const string ConfigPath = "./config/match.json";

    public MatchConfig GetConfig()
    {
        lock (_lock)
        {
            if (_config != null)
                return _config;

            try
            {
                if (File.Exists(ConfigPath))
                {
                    _config = JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), ConfigJsonContext.Default.MatchConfig)
                              ?? CreateDefaultConfig();
                }
                else
                {
                    _config = CreateDefaultConfig();
                    Save(_config);   // 首次运行落一份，方便手工编辑
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[match-config] 读取 config/match.json 失败（使用默认值）：{ex.Message}");
                _config = CreateDefaultConfig();
            }

            return _config;
        }
    }

    /// <summary>当前是否所有牌都按金卡下发（开局数据里的 is_gold）。</summary>
    public bool AllGoldCards => GetConfig().AllGoldCards;

    /// <summary>保存并立即生效。</summary>
    public void Update(bool allGoldCards)
    {
        lock (_lock)
        {
            _config = new MatchConfig { AllGoldCards = allGoldCards };
            Save(_config);
        }
    }

    /// <summary>丢弃缓存，下次读取重新载入文件（热重载）。</summary>
    public void Reload()
    {
        lock (_lock)
        {
            _config = null;
        }
    }

    private static void Save(MatchConfig config)
    {
        try
        {
            string? dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(ConfigPath,
                JsonSerializer.Serialize(config, ConfigJsonContext.Default.MatchConfig));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[match-config] 写入 config/match.json 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 默认：只发普通卡（is_gold=false），与上游 fyserver 的行为一致。
    ///
    /// 之前默认 true（沿用 KLink.Server 的"全金卡"），但客户端收藏里
    /// gold_card_count 全是 0（等于没有金卡），对局里却把每张牌都标成金卡 ——
    /// 两边对不上。等金卡能正常发放/渲染后再打开这个开关。
    /// 后台「对局配置」页可以随时改，改完立即写进 config/match.json。
    /// </summary>
    private static MatchConfig CreateDefaultConfig() => new() { AllGoldCards = false };
}
