using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Globalization;

namespace fyserver.Services;

/// <summary>游戏客户端 /session 的 server_options 模板、镜像注释和发送开关。</summary>
public sealed class ClientServerConfigService
{
    private const string ConfigPath = "./config/serverOptions.json";
    private const string SchemaPath = "./config/serverOptions.schema.json";
    private const string FlagsPath = "./config/serverOptions.flags.json";
    private const string CommentsPath = "./config/serverOptions.comments.json";

    /// <summary>
    /// PATCH(KLink): 允许连接的客户端版本号。
    /// 必须与 KLink 启动器的 AppSettings.GameVersion 默认值、
    /// 以及版本补丁 pak 里写入的 ProjectVersion= 保持一致，否则客户端会被版本检测挡住。
    /// </summary>
    private const string KLinkClientVersion = "KLink 29452.29452";

    private readonly AppDataStoreService _appData;
    private static readonly Regex KeyPattern = new("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> IncompleteMirrorDefaults = new(StringComparer.Ordinal)
    {
        "account_linking_url", "discount_products", "front_page_data", "most_popular_products", "wc_qualifiers_1", "wc_steam_url"
    };
    private readonly object _gate = new();

    public ClientServerConfigService(AppDataStoreService appData) => _appData = appData;

    /// <summary>
    /// 后台「服务器设置」页展示用的原始配置。
    /// 也套用 KLink 的 versions 覆盖，否则后台会显示一个改不动、也不会生效的旧版本列表。
    /// </summary>
    public string ReadTemplate()
    {
        lock (_gate)
        {
            if (JsonNode.Parse(_appData.Get("client:server-options") ?? "{}") is not JsonObject root)
                return "{}";
            ApplyKLinkOverrides(root);
            return root.ToJsonString();
        }
    }

    /// <summary>读取来自 qa-1939api-mirror 的配置类型、默认值和说明元数据。</summary>
    public string ReadSchema()
    {
        lock (_gate)
            return File.Exists(SchemaPath) ? File.ReadAllText(SchemaPath) : "{\"entries\":[]}";
    }

    public JsonObject ReadFlags()
    {
        lock (_gate) return ReadFlagsUnsafe();
    }

    /// <summary>管理员自定义注释；与镜像参考说明分开保存，避免更新参考数据时覆盖修改。</summary>
    public JsonObject ReadComments()
    {
        lock (_gate) return ReadCommentsUnsafe();
    }

    private JsonObject ReadCommentsUnsafe()
    {
        try
        {
            if (_appData.Get("client:comments") is { } raw && JsonNode.Parse(raw) is JsonObject saved)
                return saved;
        }
        catch (JsonException) { }
        return new JsonObject();
    }

    private JsonObject ReadFlagsUnsafe()
    {
        try
        {
            if (_appData.Get("client:flags") is { } raw && JsonNode.Parse(raw) is JsonObject saved)
                return saved;
        }
        catch (JsonException) { }
        return new JsonObject();
    }

    private bool IsEnabled(JsonObject flags, string key)
        => flags[key] is JsonValue value && value.TryGetValue<bool>(out var enabled) ? enabled : true;

    public string ReadForSession(string webSocketAddress)
    {
        lock (_gate)
        {
            var source = JsonNode.Parse(_appData.Get("client:server-options") ?? "{}") as JsonObject ?? new JsonObject();
            var flags = ReadFlagsUnsafe();
            var root = new JsonObject();
            foreach (var item in source)
            {
                if (IsEnabled(flags, item.Key) && !IsIncompletePlaceholder(item.Key, item.Value))
                    root[item.Key] = item.Value?.DeepClone();
            }
            if (root.ContainsKey("websocketurl"))
                root["websocketurl"] = webSocketAddress;

            ApplyKLinkOverrides(root);

            return root.ToJsonString();
        }
    }

    /// <summary>
    /// PATCH(KLink): 把「只由客户端本体决定」的配置项强制成 KLink 的值。
    ///
    /// versions —— 允许连接的客户端版本列表。config/serverOptions.json 是上游为正式版客户端准备的
    /// （Kards 1.53…1.58），而 KLink 启动器通过版本补丁 pak 把客户端版本注入成
    /// <see cref="KLinkClientVersion"/>，两边对不上就会被版本检测挡住。
    ///
    /// 为什么在这里强制而不是改配置文件：该文件只在首次导入数据库时作种子
    /// （AppDataStoreService.ImportLegacyFiles 仅在 meta:legacy-import:v1 缺失时执行），
    /// 已有存档库里存的是旧列表 —— 改文件对它们不生效，还会被后台「重新读取文件」覆盖回去。
    /// 换版本号（例如换模板 pak）时改 <see cref="KLinkClientVersion"/> 一处即可。
    /// </summary>
    private static void ApplyKLinkOverrides(JsonObject root)
    {
        // 用 JsonValue.Create 而不是 JsonArray { "字符串" }：
        // 后者会走 JsonNode 的隐式转换，内部调用的序列化器在 fyserver 的源生成配置下
        // 没有 System.String 的元数据，会抛 NotSupportedException（踩过：GET /server-config 直接 500）。
        root["versions"] = new JsonArray { JsonValue.Create(KLinkClientVersion) };
    }

    public (bool Ok, string Message) Save(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 131_072)
            return (false, "配置不能为空且不能超过 128 KiB");
        JsonObject root;
        try { root = JsonNode.Parse(raw) as JsonObject ?? throw new JsonException("顶层必须是 JSON 对象"); }
        catch (JsonException ex) { return (false, $"JSON 格式错误：{ex.Message}"); }

        var validation = ValidateKnownValues(root);
        if (validation != null) return (false, validation);

        root["websocketurl"] = "{WsAddress}";
        lock (_gate) return WriteConfigUnsafe(root);
    }

    public (bool Ok, string Message) Upsert(string? key, JsonNode? value, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 96 || !KeyPattern.IsMatch(key))
            return (false, "配置名只能包含英文字母、数字和下划线");
        if (value == null) return (false, "配置值不能为空");
        if (description?.Length > 2000) return (false, "注释不能超过 2000 个字符");
        var valueError = ValidateKnownValue(key, value);
        if (valueError != null) return (false, valueError);
        lock (_gate)
        {
            var root = ReadConfigUnsafe();
            if (key.Equals("websocketurl", StringComparison.OrdinalIgnoreCase))
            {
                if (description == null) return (false, "websocketurl 由 FYServer 自动生成，只能修改注释");
                var socketComments = ReadCommentsUnsafe();
                socketComments["websocketurl"] = description;
                return WriteCommentsUnsafe(socketComments);
            }
            root[key] = value.DeepClone();
            var result = WriteConfigUnsafe(root);
            if (!result.Ok || description == null) return result;
            var comments = ReadCommentsUnsafe();
            comments[key] = description;
            var commentResult = WriteCommentsUnsafe(comments);
            return commentResult.Ok ? (true, "配置和注释已保存；客户端下次登录时生效") : commentResult;
        }
    }

    public (bool Ok, string Message) Delete(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Equals("websocketurl", StringComparison.OrdinalIgnoreCase))
            return (false, "websocketurl 不能删除");
        lock (_gate)
        {
            var root = ReadConfigUnsafe();
            if (!root.Remove(key)) return (false, "配置项不存在");
            var result = WriteConfigUnsafe(root);
            if (result.Ok)
            {
                var flags = ReadFlagsUnsafe();
                flags.Remove(key);
                WriteFlagsUnsafe(flags);
                var comments = ReadCommentsUnsafe();
                if (comments.Remove(key)) WriteCommentsUnsafe(comments);
            }
            return result;
        }
    }

    public (bool Ok, string Message) SetEnabled(string? key, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(key) || !KeyPattern.IsMatch(key))
            return (false, "配置名无效");
        lock (_gate)
        {
            var root = ReadConfigUnsafe();
            if (!root.ContainsKey(key)) return (false, "配置项不存在，请先新增配置");
            if (enabled && IsIncompletePlaceholder(key, root[key]))
                return (false, "镜像默认值不完整，请先填写有效配置值再启用");
            var flags = ReadFlagsUnsafe();
            flags[key] = enabled;
            return WriteFlagsUnsafe(flags);
        }
    }

    private JsonObject ReadConfigUnsafe()
    {
        try
        {
            if (_appData.Get("client:server-options") is { } raw && JsonNode.Parse(raw) is JsonObject root)
                return root;
        }
        catch (JsonException) { }
        return new JsonObject();
    }

    private static bool IsIncompletePlaceholder(string key, JsonNode? value) =>
        IncompleteMirrorDefaults.Contains(key) &&
        (value == null || value is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String && scalar.GetValue<string>().Length == 0 ||
         value is JsonObject obj && obj.Count == 0);

    private string? ValidateKnownValues(JsonObject root)
    {
        foreach (var item in root)
        {
            if (item.Value == null) continue;
            var error = ValidateKnownValue(item.Key, item.Value);
            if (error != null) return error;
        }
        return null;
    }

    private string? ValidateKnownValue(string key, JsonNode value)
    {
        if (!File.Exists(SchemaPath)) return null;
        JsonArray? entries;
        try { entries = (JsonNode.Parse(File.ReadAllText(SchemaPath)) as JsonObject)?["entries"] as JsonArray; }
        catch (JsonException) { return null; }
        var type = entries?.OfType<JsonObject>().FirstOrDefault(entry =>
            string.Equals((string?)entry["key"], key, StringComparison.Ordinal))?["type"]?.GetValue<string>();
        var json = value.ToJsonString();
        var valid = type switch
        {
            "string" => value is JsonValue v && v.GetValueKind() == JsonValueKind.String,
            "int" => long.TryParse(json, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "double" => double.TryParse(json, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number),
            _ => true
        };
        return valid ? null : $"{key} 需要 {type} 类型的值";
    }

    private (bool Ok, string Message) WriteConfigUnsafe(JsonObject root)
    {
        try
        {
            _appData.Set("client:server-options", root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return (true, "已保存到数据库；客户端下次登录时生效");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Data.Common.DbException) { return (false, $"数据库写入失败：{ex.Message}"); }
    }

    private (bool Ok, string Message) WriteFlagsUnsafe(JsonObject flags)
    {
        try
        {
            _appData.Set("client:flags", flags.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return (true, "发送开关已保存到数据库；客户端下次登录时生效");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Data.Common.DbException) { return (false, $"数据库写入失败：{ex.Message}"); }
    }

    private (bool Ok, string Message) WriteCommentsUnsafe(JsonObject comments)
    {
        try
        {
            _appData.Set("client:comments", comments.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return (true, "注释已保存到数据库");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Data.Common.DbException) { return (false, $"数据库写入失败：{ex.Message}"); }
    }
}
