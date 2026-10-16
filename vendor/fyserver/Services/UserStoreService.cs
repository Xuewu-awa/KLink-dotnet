using System.Collections.Concurrent;
using fyserver.Models;

namespace fyserver.Services;

/// <summary>可切换的玩家存储门面：本地 FASTER、MySQL 或 PostgreSQL。</summary>
public sealed class UserStoreService
{
    private readonly Func<FasterKvService> _localDatabaseFactory;
    private readonly UserDatabaseConfigurationService _configuration;
    private readonly AppDataStoreService _appData;
    private readonly CardCatalogService _cardCatalog;
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _userLocks = new();
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly SemaphoreSlim _identityLock = new(1, 1);
    private readonly SemaphoreSlim _backendLock = new(1, 1);
    private IUserStoreBackend? _backend;

    public bool IsReady => _backend != null;
    public string? Provider => _backend?.Provider;
    public string? LastInitializationError { get; private set; }

    public UserStoreService(Func<FasterKvService> localDatabaseFactory, UserDatabaseConfigurationService configuration, AppDataStoreService appData, CardCatalogService cardCatalog)
    {
        _localDatabaseFactory = localDatabaseFactory;
        _configuration = configuration;
        _appData = appData;
        _cardCatalog = cardCatalog;
    }
    public async Task TryInitializeConfiguredAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = _configuration.Load();
            if (settings == null) return;
            await ActivateAsync(settings, save: false, cancellationToken);
        }
        catch (Exception ex)
        {
            LastInitializationError = SafeError(ex);
            Console.WriteLine($"用户数据库初始化失败：{LastInitializationError}");
        }
    }

    public async Task<(bool Ok, string Message)> ConfigureAsync(UserDatabaseSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            await ActivateAsync(settings, save: true, cancellationToken);
            return (true, $"{ProviderName(settings.Provider)} 已连接并完成数据表初始化");
        }
        catch (Exception ex)
        {
            LastInitializationError = SafeError(ex);
            return (false, "数据库连接或初始化失败：" + LastInitializationError);
        }
    }

    public void RecordFull() => RequireBackend().CheckpointAsync().GetAwaiter().GetResult();
    public void RecordIncremental() => RequireBackend().CheckpointAsync().GetAwaiter().GetResult();

    public Task<User?> GetByUserNameAsync(string userName) => string.IsNullOrWhiteSpace(userName)
        ? Task.FromResult<User?>(null) : RequireBackend().GetByUserNameAsync(userName);
    public Task<User?> GetByIdAsync(int userId) => userId <= 0
        ? Task.FromResult<User?>(null) : RequireBackend().GetByIdAsync(userId);

    public async Task<TResult?> WithUserLockAsync<TResult>(int userId, Func<User, Task<TResult>> action) where TResult : class
    {
        var gate = _userLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var user = await GetByIdAsync(userId);
            return user == null ? null : await action(user);
        }
        finally { gate.Release(); }
    }

    public async Task SaveUserAsync(User user)
    {
        if (string.IsNullOrEmpty(user.UserName) || user.Id == 0) throw new ArgumentException("Invalid user data: Missing UserName or ID");
        user.UpdatedAt = DateTime.UtcNow;
        await RequireBackend().SaveAsync(user);
    }

    public async Task<User> CreateUserAsync(string userName)
    {
        await _createLock.WaitAsync();
        try
        {
            var existing = await GetByUserNameAsync(userName);
            if (existing != null) return existing;
            var user = new User(userName);

            // ★★ 2026-10-05：**固定玩家 id**（环境变量 `KLINK_FORCE_PLAYER_ID`）—— 为"回放调试"而加。
            //
            // ## 为什么需要
            //
            // 客户端用到的**所有** id 都来自服务端：`/players/{id}/…` 里的那个 id、
            // 重连载荷里的 `player_id_left/right`，以及**回放 `starting_info` 里内嵌的**同名 id。
            // 而本地模式的用户库一旦没命中（实测日志：`Find user: steam:… → 未找到 →
            // Created new user`），这里会给一个**随机 id** ⇒ **每次进游戏都是新号**
            //（实测同一 steam 账号：807083 → 460707 → 160931 → 556448 → 790660 → 863525）
            // ⇒ 注入一局**旧回放**时，它内嵌的 `player_id_left=807083` 永远对不上客户端
            // ⇒ 重连端点匹配成功、载荷也发出去了，**客户端就是不进对局**。
            //
            // ## 两条安全约束（2026-10-05 实测踩到过第一条）
            //
            // 1. **绝不打断会话**：固定 id 已被**别的**用户名占用时，**安全降级成随机**，
            //    而不是抛异常 —— 抛异常会让 `POST /session` 变 400、客户端直接"验证失败"
            //    （实测：内部账号 `1939Mother` 先占了 807083，随后真账号的会话就被打断了）。
            // 2. **按用户名限定**（`KLINK_FORCE_PLAYER_USERNAME`，可选）：只让指定账号吃固定 id，
            //    别的玩家照旧随机 ⇒ 对应"**人机固定 / 真人对战随机**"（两个玩家同一个 id 会出错）。
            //    不设这个变量时行为与原来一致（随机），发布版本不受影响。
            if (int.TryParse(Environment.GetEnvironmentVariable("KLINK_FORCE_PLAYER_ID"), out int forcedId)
                && forcedId > 0)
            {
                string? forceFor = Environment.GetEnvironmentVariable("KLINK_FORCE_PLAYER_USERNAME");
                bool wantForce = string.IsNullOrEmpty(forceFor)
                    || string.Equals(forceFor, userName, StringComparison.OrdinalIgnoreCase);
                var occupant = await GetByIdAsync(forcedId);
                bool free = occupant is null || occupant.UserName == userName;

                if (wantForce && free)
                {
                    user.Id = forcedId;
                    _cardCatalog.ApplyInitialCollection(user);
                    await SavePlayerIdentityAsync(user, user.Name);
                    Console.WriteLine($"[force-id] {userName} 使用固定玩家 id {forcedId}（KLINK_FORCE_PLAYER_ID）");
                    return user;
                }

                Console.WriteLine($"[force-id] {userName} **不**使用固定 id {forcedId}" +
                                  $"（wantForce={wantForce} free={free}" +
                                  $"{(occupant is null ? "" : $" 占用者={occupant.UserName}")}）⇒ 退回随机");
            }

            do { user.Id = Random.Shared.Next(100000, 1000000); }
            while (await GetByIdAsync(user.Id) != null);
            _cardCatalog.ApplyInitialCollection(user);
            await SavePlayerIdentityAsync(user, user.Name);
            return user;
        }
        finally { _createLock.Release(); }
    }

    /// <summary>保存公开昵称和四位 Tag；新 Tag 在同昵称用户中尽量避免重复。</summary>
    public async Task<bool> SavePlayerIdentityAsync(User user, string name, int? requestedTag = null)
    {
        await _identityLock.WaitAsync();
        try
        {
            var peers = (await GetAllUsersAsync())
                .Where(other => other.Id != user.Id && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(other => other.Tag)
                .Where(tag => tag is >= 0 and <= 9999)
                .ToHashSet();

            int tag;
            if (requestedTag is { } requested)
            {
                var unchangedLegacyIdentity = string.Equals(user.Name, name, StringComparison.OrdinalIgnoreCase) && user.Tag == requested;
                if (requested is < 0 or > 9999 || peers.Contains(requested) && !unchangedLegacyIdentity) return false;
                tag = requested;
            }
            else
            {
                var start = Random.Shared.Next(0, 10000);
                var found = -1;
                for (var offset = 0; offset < 10000; offset++)
                {
                    var candidate = (start + offset) % 10000;
                    if (peers.Contains(candidate)) continue;
                    found = candidate;
                    break;
                }
                if (found < 0) throw new InvalidOperationException("该昵称的四位玩家 Tag 已用完");
                tag = found;
            }

            user.Name = name;
            user.Tag = tag;
            await SaveUserAsync(user);
            return true;
        }
        finally { _identityLock.Release(); }
    }

    public async Task DeleteUserAsync(int userId)
    {
        var user = await GetByIdAsync(userId);
        if (user != null) await RequireBackend().DeleteAsync(user);
    }

    public Task<List<User>> GetAllUsersAsync() => RequireBackend().GetAllAsync();
    public void ClearAll() => RequireBackend().ClearAsync().GetAwaiter().GetResult();

    private async Task ActivateAsync(UserDatabaseSettings settings, bool save, CancellationToken cancellationToken)
    {
        await _backendLock.WaitAsync(cancellationToken);
        try
        {
            IUserStoreBackend candidate = settings.Provider == "local"
                ? new LocalUserStoreBackend(_localDatabaseFactory())
                : new RelationalUserStoreBackend(settings);
            await candidate.InitializeAsync(cancellationToken);
            _appData.Initialize(settings);
            if (save) _configuration.Save(settings);
            var previous = _backend;
            _backend = candidate;
            LastInitializationError = null;
            if (previous != null && !ReferenceEquals(previous, candidate)) await previous.DisposeAsync();
        }
        finally { _backendLock.Release(); }
    }

    private IUserStoreBackend RequireBackend() => _backend ?? throw new InvalidOperationException("用户数据库尚未完成初始化");
    private static string ProviderName(string provider) => provider switch { "mysql" => "MySQL", "postgresql" => "PostgreSQL", _ => "本地 FASTER" };
    private static string SafeError(Exception ex)
    {
        var message = ex.GetBaseException().Message.Replace("\r", " ").Replace("\n", " ");
        return message.Length > 300 ? message[..300] : message;
    }
}
