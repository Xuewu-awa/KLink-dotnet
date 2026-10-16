using fyserver;
using fyserver.Endpoints;
using fyserver.Middleware;
using fyserver.Models;
using fyserver.Serialization;
using fyserver.Services;
using Microsoft.AspNetCore.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

// 发行版可能从快捷方式、服务管理器或其它工作目录启动；所有相对路径
//（setting.json、config、library、data 与 wwwroot）都必须固定到 exe 所在目录。
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// PATCH(KLink): 控制台输出固定用 UTF-8。
//
// 默认情况下 .NET 按控制台代码页编码输出（中文系统是 936/GBK），
// 而托管我们的 KLink 启动器按 UTF-8 读取子进程输出 —— 两边不一致时
// 中文会被解成 U+FFFD 且不可恢复，日志里成片 ��������（踩过，12 处中文字符全丢）。
// 这里在**任何输出之前**显式声明 UTF-8，把两端的约定固定下来，不再依赖系统区域设置。
try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
catch { /* 无控制台（纯重定向）时忽略，此时 .NET 本就用默认编码 */ }

// ==================== 配置与共享服务 ====================
// 单例实例由本进程显式创建，HTTP 与 WebSocket 合并到同一个 host，
// 从而共享用户存储 / 匹配队列 / WebSocket 连接表等运行时状态。

// PATCH: 启动器托管模式（--no-console）跳过交互式命令循环
bool noConsole = args.Contains("--no-console", StringComparer.OrdinalIgnoreCase);
// PATCH: 优雅退出监听端口（0=不启用）。被重定向的 GUI 子进程收不到 Ctrl+C，
// 所以不能用 Console.CancelKeyPress，改用一个只绑回环的本地 TCP 端口作关闭信号。
int shutdownPort = 0;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i].Equals("--shutdown-port", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(args[i + 1], out int parsedShutdownPort))
    {
        shutdownPort = parsedShutdownPort;
    }
}

var serverOptions = new ServerOptions();
serverOptions.ReadFromFile(); // 读 ./setting.json；不存在则生成默认配置

FasterKvService? fasterKv = null;
var userDatabaseConfiguration = new UserDatabaseConfigurationService();
var appData = new AppDataStoreService(() => fasterKv ??= new FasterKvService());
var cardCatalog = new CardCatalogService(appData);
var users = new UserStoreService(() => fasterKv ??= new FasterKvService(), userDatabaseConfiguration, appData, cardCatalog);
users.TryInitializeConfiguredAsync().GetAwaiter().GetResult();
if (users.IsReady) cardCatalog.EnsureInitialized();
if (appData.IsReady && appData.Get("server:settings") is { } savedServerSettings)
{
    serverOptions.ReadFromJson(savedServerSettings);
    // 数据库里那份是历史快照，会把"这次启动器写的"网络配置顶回去
    // （实测：切到局域网模式后 fyserver 仍绑 127.0.0.1，别的设备连不上）。
    // 端口 / 监听地址 / 对外地址 / 名字等字段的所有者是启动器，覆盖完再还原。
    serverOptions.RestoreLauncherOwnedValues();
}
var codec = new CodecService();
var playerLibrary = new PlayerLibraryService();
playerLibrary.InitLibrary("./library/deckCodeIDsTable2.json", "./library/emojiLib.json", "./library/cardbackLib.json");
var playerCards = new PlayerCardService(cardCatalog);
var storeConfig = new StoreConfigService(appData);
var redeemCodes = new RedeemCodeService(appData);
var webSocketHub = new WebSocketHubService();
var playerLoginAccounts = new PlayerLoginAccountService(appData);
var auth = new AuthService(users, codec, playerLoginAccounts);
var matchHistory = new MatchHistoryService(userDatabaseConfiguration, appData);
try { await matchHistory.InitializeAsync(); }
catch (Exception ex) { Console.WriteLine($"对局历史数据库初始化失败：{ex.GetBaseException().Message}"); }
// PATCH: 对局配置（开局卡牌 is_gold 等），可由后台切换
var matchConfig = new MatchConfigService();
var matches = new MatchManagerService(users, playerLibrary, codec, serverOptions, matchHistory, matchConfig);

// ==================== 后台（静态页 /admin-ui 的数据层）服务 ====================
var adminUsers = new AdminUserService(users, webSocketHub);
var frontpage = new FrontpageConfigService(appData);
var contentEntries = new ContentEntriesService(appData);
var serverMetrics = new ServerMetricsService();
var adminAccount = new AdminAccountService(appData);
var adminAudit = new AdminAuditLogService(appData);
var clientServerConfig = new ClientServerConfigService(appData);
var patchPaks = new PatchPakService(appData);

void RegisterSharedServices(IServiceCollection services)
{
    services.AddSingleton(serverOptions);
    services.AddSingleton(users);
    services.AddSingleton(userDatabaseConfiguration);
    services.AddSingleton(appData);
    services.AddSingleton(cardCatalog);
    services.AddSingleton(codec);
    services.AddSingleton(playerLibrary);
    services.AddSingleton(playerCards);
    services.AddSingleton(storeConfig);
    services.AddSingleton(redeemCodes);
    services.AddSingleton(webSocketHub);
    services.AddSingleton(auth);
    services.AddSingleton(playerLoginAccounts);
    services.AddSingleton(matches);
    // 规则内核驱动器：`MatchEndpoints` 在训练局（Ex=="pw"）的回合结束时调它。
    // 单例 + 懒加载 —— 内核数据很大（card-ir.json 8.7 MB），
    // 第一次真正要用 bot 时才加载，不影响启动、也不影响不开 bot 的部署。
    services.AddSingleton<ServerBotService>();
    services.AddSingleton(matchHistory);
    services.AddSingleton(matchConfig);
    services.AddSingleton(adminUsers);
    services.AddSingleton(frontpage);
    services.AddSingleton(contentEntries);
    services.AddSingleton(serverMetrics);
    services.AddSingleton(adminAccount);
    services.AddSingleton(adminAudit);
    services.AddSingleton(clientServerConfig);
    services.AddSingleton(patchPaks);
}

// ============ HTTP host（含 WebSocket 端点，共用同一端口） ============
var httpBuilder = WebApplication.CreateSlimBuilder();
RegisterSharedServices(httpBuilder.Services);
httpBuilder.Services.AddHostedService<MatchHistoryCleanupWorker>();

httpBuilder.WebHost.UseUrls(serverOptions.GetAddressHttp());
httpBuilder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    // 输入同时接受 snake_case 与 camelCase。
    //
    // PropertyNamingPolicy 只管**输出**；输入默认是大小写敏感的，所以客户端若发
    // mainFaction 而不是 main_faction，就会反序列化成 null。之前 Deck 构造函数在
    // MainFaction 为 null 时抛空引用（表现为 500），我给端点补了"缺字段返回 400"的校验后，
    // 反而把这种请求变成了明确的 400 —— 卡组创建直接失败。
    // 客户端字段命名不能靠猜，这里放宽到大小写不敏感，两种写法都能收。
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    // 纯源生成：NativeAOT 兼容（所有响应类型注册到 FyJsonContext）
    options.SerializerOptions.TypeInfoResolverChain.Clear();
    options.SerializerOptions.TypeInfoResolverChain.Add(FyJsonContext.Default);
});

var httpApp = httpBuilder.Build();

httpApp.UseMiddleware<PathNormalizationMiddleware>();

// 全局异常处理：统一返回 JSON 500
httpApp.UseExceptionHandler(exceptionHandlerApp =>
{
    exceptionHandlerApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(
            new ErrorResponse(
                Error: "Internal server error",
                Message: "An unexpected error occurred",
                StatusCode: 500
            ),
            FyJsonContext.Default.ErrorResponse,
            cancellationToken: context.RequestAborted);
    });
});

// WebSocket 端点：与 HTTP 共用同一端口与管线（非 WS 请求继续走后面的 HTTP 管线）
httpApp.MapWebSocketEndpoint();

httpApp.UseMiddleware<ContentTypeCleanupMiddleware>();
httpApp.UseMiddleware<ServerInitializationMiddleware>();
// 静态后台（/admin-ui）的数据接口鉴权：只挡 /admin/api/*，静态页自身匿名可访问

// 后台数据接口鉴权（静态后台 /admin-ui）
httpApp.UseMiddleware<AdminApiAuthorizationMiddleware>();

// 显式路由注册：保证 PathNormalizationMiddleware 对 // 路径的改写先于路由匹配生效
httpApp.UseRouting();

// ── 请求日志（2026-10-05，为"回放重连"调试而加）─────────────────────────
// 为什么需要：ASP.NET 默认**不记请求**，而"客户端到底调没调
// `/matches/v2/reconnect`、拿到的是 401 还是 `null`"是排查重连的唯一入口
//（实测日志里只有 `客户端已连接/已断开`，看不出请求了什么）。
// 只记「方法 + 路径 + 状态码」，**不记 body / header**（免得把 token 写进日志）。
httpApp.Use(async (context, next) =>
{
    await next();
    Console.WriteLine($"[req] {context.Request.Method} " +
                      $"{context.Request.Path}{context.Request.QueryString} " +
                      $"-> {context.Response.StatusCode}");
});

httpApp.MapUserEndpoints();
httpApp.MapPlayerEndpoints();
httpApp.MapRedeemEndpoints();
httpApp.MapDeckEndpoints();
httpApp.MapLobbyEndpoints();
httpApp.MapMatchEndpoints();
httpApp.MapMatchHistoryEndpoints();
httpApp.MapAdminApiEndpoints();
// PATCH: 启动器房管接口（/launcher/room/*）—— KLink.App 的服务器/房间/控制台页依赖
httpApp.MapLauncherEndpoints();
httpApp.MapGet("/patch-paks", (ServerOptions options, PatchPakService paks) =>
{
    var baseUrl = options.GetAddressHttpR().TrimEnd('/');
    var patches = new System.Text.Json.Nodes.JsonArray(paks.List().Select(item => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
    {
        ["id"] = item.Id, ["fileName"] = item.FileName, ["version"] = item.Version,
        ["description"] = item.Description, ["size"] = item.Size, ["sha256"] = item.Sha256,
        ["createdAt"] = item.CreatedAt.ToString("O"), ["downloadUrl"] = baseUrl + "/patch-paks/" + item.Id + "/download"
    }).ToArray());
    return Results.Text(new System.Text.Json.Nodes.JsonObject { ["algorithm"] = "SHA-256", ["patches"] = patches }.ToJsonString(), "application/json");
});
httpApp.MapGet("/patch-paks/{id}/download", (string id, PatchPakService paks) =>
{
    if (id.Length != 32 || id.Any(c => !char.IsAsciiHexDigit(c))) return Results.NotFound();
    var item = paks.List().FirstOrDefault(candidate => candidate.Id == id);
    var bytes = item == null ? null : paks.Read(id);
    if (item == null || bytes == null) return Results.NotFound();
    return Results.File(bytes, "application/octet-stream", item.FileName, lastModified: item.CreatedAt,
        entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue("\"" + item.Sha256 + "\""), enableRangeProcessing: true);
});
httpApp.MapGet("/admin-ui/uploads/{**assetPath}", (string assetPath, AppDataStoreService data) =>
{
    var relative = assetPath.Replace('\\', '/');
    if (relative.Split('/').Any(segment => segment is "" or "." or "..")) return Results.NotFound();
    var encoded = data.Get("asset:admin:" + relative);
    if (encoded == null) return Results.NotFound();
    try
    {
        var extension = Path.GetExtension(relative).ToLowerInvariant();
        var contentType = extension switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", _ => "application/octet-stream" };
        return Results.File(Convert.FromBase64String(encoded), contentType);
    }
    catch (FormatException) { return Results.NotFound(); }
});
// 后台入口别名：/admin-ui/ → index.html、/admin-ui/login → login.html
httpApp.UseMiddleware<AdminUiEntryMiddleware>();
// 静态资源（wwwroot/admin-ui）：CreateSlimBuilder 默认未启用静态文件中间件
httpApp.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        // Always revalidate admin pages and assets after replacing a distribution.
        if (context.Context.Request.Path.StartsWithSegments("/admin-ui"))
            context.Context.Response.Headers.CacheControl = "no-cache";
    }
});


// 未找到路由的处理
httpApp.UseStatusCodePages(async statusCodeContext =>
{
    var response = statusCodeContext.HttpContext.Response;
    if (response.StatusCode == 404)
    {
        await response.WriteAsJsonAsync(
            new ErrorResponse(
                Error: "Not found",
                Message: "The requested resource was not found",
                StatusCode: 404
            ),
            FyJsonContext.Default.ErrorResponse,
            cancellationToken: statusCodeContext.HttpContext.RequestAborted);
    }
});

// 数据库初始化和清理
httpApp.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"Application started on {serverOptions.GetAddressHttp()}");
    Console.WriteLine($"WebSocket endpoint: {serverOptions.GetAddressWsR()}");
    Console.WriteLine(users.IsReady ? $"用户数据库已准备：{users.Provider}" : "用户数据库尚未配置，游戏接口已暂停");
    if (!adminAccount.IsInitialized)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("============================================================");
        Console.WriteLine("管理员后台尚未初始化，请在浏览器完成初始设置：");
        Console.WriteLine($"http://127.0.0.1:{serverOptions.portHttp}/admin-ui/login.html");
        Console.WriteLine("首次设置仅允许从服务器本机访问。");
        Console.WriteLine("============================================================");
        Console.ResetColor();
    }
});
httpApp.Lifetime.ApplicationStopping.Register(() =>
{
    Console.WriteLine("Application stopping. Cleaning up...");
    fasterKv?.Dispose(); // 仅本地存储模式会创建
});

Console.ForegroundColor = ConsoleColor.Blue;
Console.WriteLine("正在启动http服务器");
Console.WriteLine("等待两秒确保初始化成功");
if (File.Exists("./YCDR"))
    Console.WriteLine("发现持久化数据，已加载");
Console.ForegroundColor = ConsoleColor.White;

// PATCH: 保留 task 引用（原版 `_ = httpApp.RunAsync()` 丢弃引用，外部无法触发优雅退出）
var appTask = httpApp.RunAsync();

// PATCH: Kestrel 绑定失败（端口被占用）时 RunAsync 的异常只在日志里，进程会继续活着
// —— 表现是"只剩 --shutdown-port 在监听、游戏接口全 404"，启动器那边只看到"启动超时"，
// 排查时很容易误以为是产物不全。这里挂个 continuation，绑定失败即以非 0 退出码结束进程。
//
// 注意不能用 `if (appTask.IsFaulted)` 就地判断：RunAsync 是异步的，刚返回时绑定还没执行完，
// 那时 IsFaulted 恒为 false。必须等它真正结束。
_ = appTask.ContinueWith(t =>
{
    var reason = t.Exception?.GetBaseException() ?? t.Exception;
    Console.WriteLine($"fyserver 启动失败：{reason?.Message}");
    Console.WriteLine($"常见原因：端口 {serverOptions.portHttp} 已被占用。请关闭占用该端口的程序后重试。");
    Environment.Exit(1);
}, TaskContinuationOptions.OnlyOnFaulted);

// ==================== 进程存活与优雅退出（KLink 启动器托管模式） ====================
if (noConsole)
{
    // 启动器托管模式：不做交互式命令循环，等关闭信号。
    // 信号来源：只绑回环的本地 TCP 端口（收到任意连接即触发）。
    // 用 TCP 而不是 Console.CancelKeyPress —— 被重定向的 GUI 子进程没有控制台，
    // CancelKeyPress 不会触发，那样就成了"假优雅退出"。
    using var shutdown = new ManualResetEventSlim(false);

    if (shutdownPort > 0)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, shutdownPort);
            listener.Start();
            _ = Task.Run(() =>
            {
                try
                {
                    using var client = listener.AcceptTcpClient();
                }
                catch { /* 监听被关闭时忽略 */ }
                shutdown.Set();
            });
            Console.WriteLine($"[launcher] 关闭信号端口：127.0.0.1:{shutdownPort}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[launcher] 关闭信号端口启动失败（将只能强杀）：{ex.Message}");
        }
    }
    else
    {
        Console.WriteLine("[launcher] 未指定 --shutdown-port，将只能被强杀（可能丢失最后一次检查点）");
    }

    Console.WriteLine("[launcher] --no-console：跳过命令循环，等待关闭信号");
    shutdown.Wait();

    Console.WriteLine("[launcher] 正在优雅停止…");
    try
    {
        // PATCH: 退出前强制落盘。
        // FasterKvService.Dispose() 里仍是 `if (File.Exists("./YCDR")) Checkpoint(...)`，
        // 首次运行会整个跳过 → 这里显式做一次全量检查点（Checkpoint() 会无条件写 YCDR 标记）。
        users.RecordFull();
        Console.WriteLine("[save] 退出前检查点完成");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[save] 退出前检查点失败：{ex.Message}");
    }

    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await httpApp.StopAsync(cts.Token);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[launcher] 停止时出错：{ex.Message}");
    }
    try { await appTask; } catch { /* 停止过程中的取消异常可忽略 */ }
    Console.WriteLine("[launcher] 已停止");
}
else
{
    // 原行为：控制台命令循环（阻塞主线程 = 进程存活期）
    Command.StartCommandLoop(users, storeConfig, matches);
}
