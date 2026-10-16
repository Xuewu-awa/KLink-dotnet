using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Nodes;
using fyserver.Models;
using fyserver.Services;

namespace fyserver.Endpoints;

public static class DeckEndpoints
{
    /// <summary>读取请求体为 JSON 对象；非法 JSON 返回 null。</summary>
    private static async Task<JsonObject?> ReadJsonObjectAsync(HttpContext context)
    {
        try
        {
            var text = await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted);
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static IEndpointRouteBuilder MapDeckEndpoints(this IEndpointRouteBuilder app)
    {
        // 与上游一致：**不校验字段**，只要 body 能反序列化就建卡组。
        //
        // 踩坑记录：曾经在这里加过"缺 name/main_faction/deck_code 就返回 400"的校验，
        // 理由是"字段名不对会抛空引用变成 500"。但客户端的「自动创建卡组」是
        // **本地生成卡组再提交**，提交时 deck_code 很可能还是空的（随后用
        // PUT /decks/{id} 的 "fill" 动作填进去）—— 校验直接把它挡成 400，
        // 客户端拿不到 deck id，继续往下走就读空指针崩溃（EXCEPTION_ACCESS_VIOLATION +0x18）。
        //
        // 结论：这个端点的宽容度就是协议的一部分，不要替客户端"纠正"输入。
        // 真要防 500，应该像 Deck 构造函数那样对 null 做兜底，而不是拒绝请求。
        app.MapPost("/players/{id}/decks", async (string id, HttpContext context, UserStoreService users) =>
        {
            if (!int.TryParse(id, out var playerId) || playerId <= 0)
                return Results.BadRequest($"Invalid user ID: {id}");

            // 仍然自己读 body，但只做"大小写/下划线无关"的取值，不做任何必填校验：
            // 客户端各端点命名不统一（卡组是 snake_case，商店是 camelCase），
            // 用归一化取值可以同时兼容，缺字段就按空字符串处理（与上游行为一致）。
            var body = await ReadJsonObjectAsync(context) ?? new JsonObject();
            string Get(string name) => body.FirstOrDefault(pair =>
                string.Equals(pair.Key.Replace("_", ""), name, StringComparison.OrdinalIgnoreCase)).Value is JsonValue v &&
                v.TryGetValue<string>(out var text) ? text : "";

            var createDeck = new CreateDeck(
                Get("name"),
                Get("mainfaction"),
                Get("allyfaction"),
                Get("deckcode"));

            var user = await users.GetByIdAsync(playerId);
            if (user == null)
                return Results.NotFound($"User with ID {id} not found");
            var deck = new Deck(createDeck, user.Id);
            user.Decks[deck.Id] = deck;

            await users.SaveUserAsync(user);
            users.RecordIncremental();

            return Results.Ok(new DeckSummaryDto(
                deck.Name,
                deck.MainFaction,
                deck.AllyFaction,
                deck.CardBack,
                deck.DeckCode,
                deck.Favorite,
                deck.Id,
                deck.PlayerId,
                deck.LastPlayed.ToString("o"),
                deck.CreateDate.ToString("o"),
                deck.ModifyDate.ToString("o")
            ));
        });

        // 与上游逻辑一致，但字段取值改成"大小写/下划线无关"。
        //
        // 上游这里靠模型绑定把 body 反序列化成 DeckAction，字段名一旦对不上（例如客户端发
        // deckCode 而 DTO 是 snake_case 的 deck_code），action.Action 就是 null，
        // switch 不进任何分支 —— **静默返回 200 但什么都没做**（实测 fill 后 deck_code 长度仍是 0）。
        // 「自动创建卡组」正是靠这个 fill 动作把本地生成的卡组写回服务器的，
        // 所以这里必须真正取到值，不能再静默失败。
        app.MapPut("/players/{player_id}/decks/{deck_id}", async (string player_id, int deck_id, HttpContext context, UserStoreService users) =>
        {
            if (!int.TryParse(player_id, out var playerId) || playerId <= 0)
                return Results.BadRequest($"Invalid user ID: {player_id}");
            var body = await ReadJsonObjectAsync(context) ?? new JsonObject();
            // 归一化取值：去掉下划线并忽略大小写，snake_case / camelCase / PascalCase 都能命中
            JsonNode? Raw(string name) => body.FirstOrDefault(pair =>
                string.Equals(pair.Key.Replace("_", ""), name, StringComparison.OrdinalIgnoreCase)).Value;
            string Text(string name) => Raw(name) is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

            var user = await users.GetByIdAsync(playerId);
            if (user == null)
                return Results.NotFound($"User with ID {player_id} not found");
            if (user.Decks.TryGetValue(deck_id, out var deck))
            {
                switch (Text("action"))
                {
                    case "fill":
                        var deckCode = Text("deckcode");
                        if (deckCode.Length > 0)
                        {
                            deck.DeckCode = deckCode;
                            deck.ModifyDate = DateTime.Now;
                        }
                        break;
                }
                await users.SaveUserAsync(user);
                users.RecordIncremental();
            }

            return Results.Ok(new EmptyResponseDto());
        });

        app.MapPut("/players/{player_id}/decks/", async (string player_id, ChangeDeck changeDeck, UserStoreService users) =>
        {
            if (!int.TryParse(player_id, out var playerId) || playerId <= 0)
                return Results.BadRequest($"Invalid user ID: {player_id}");
            var user = await users.GetByIdAsync(playerId);
            if (user == null)
                return Results.NotFound($"User with ID {player_id} not found");
            if (user.Decks.TryGetValue(changeDeck.Id, out var deck))
            {
                switch (changeDeck.Action)
                {
                    case "rename":
                        deck.Name = changeDeck.Name;
                        deck.ModifyDate = DateTime.Now;
                        break;
                    case "change_card_back":
                        deck.CardBack = changeDeck.Name;
                        deck.ModifyDate = DateTime.Now;
                        break;
                    case "make_favorite":
                        deck.Favorite = true;
                        deck.ModifyDate = DateTime.Now;
                        break;
                }

                await users.SaveUserAsync(user);
                users.RecordIncremental();
            }

            return Results.Ok(new EmptyResponseDto());
        });

        app.MapDelete("/players/{player_id}/decks/{deck_id}", async (string player_id, int deck_id, UserStoreService users) =>
        {
            if (!int.TryParse(player_id, out var playerId) || playerId <= 0)
                return Results.BadRequest($"Invalid user ID: {player_id}");
            var user = await users.GetByIdAsync(playerId);
            if (user == null)
                return Results.NotFound($"User with ID {player_id} not found");
            user.Decks.Remove(deck_id);
            await users.SaveUserAsync(user);
            users.RecordIncremental();

            return Results.Ok(new EmptyResponseDto());
        });

        // 这个端点获取卡组详情（预制卡组），暂未使用
        app.MapGet("/items/decks/{id}", (string id) => Results.Ok(""));

        return app;
    }
}
