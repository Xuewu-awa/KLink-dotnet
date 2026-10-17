using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using KLink.Bot.Cards;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.Engine;
using KLink.Bot.Replay;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: ReplayAudit <report.json> <replay-directory> [replay-directory ...]");
    return 2;
}
string data = Path.Combine(AppContext.BaseDirectory, "Data");
var db = CardDatabase.Load(data);
KismetLibrary.Initialize(Path.Combine(data, "card-ir.json"));
var upgradeable = db.All.Where(c => c.Name.EndsWith("_vet", StringComparison.Ordinal))
    .Select(c => CardDatabase.ResolveBaseName(c.Name)).ToHashSet(StringComparer.Ordinal);
var snapshots = args.Skip(1).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
    .SelectMany(root => Directory.EnumerateFiles(root, "replay-*.json", SearchOption.AllDirectories))
    .Where(path => !path.EndsWith(".actions.json", StringComparison.OrdinalIgnoreCase))
    .OrderBy(path => path, StringComparer.Ordinal).ToArray();
var seen = new HashSet<string>(StringComparer.Ordinal);
var matches = new List<MatchAudit>();
var inputErrors = new List<string>();
int duplicatePairs = 0;
foreach (string snapshot in snapshots)
{
    string actions = Path.ChangeExtension(snapshot, "actions.json");
    if (!File.Exists(actions)) continue;
    string snapshotHash = Hash(snapshot), actionsHash = Hash(actions);
    try
    {
        var replay = ReplayData.Load(snapshot, actions);
        string semantic = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { replay.MatchId, replay.LeftPlayerId, replay.RightPlayerId, replay.ClientSide, replay.Cards, replay.Actions }))));
        if (!seen.Add(semantic)) { duplicatePairs++; continue; }
        var names = replay.Cards.Select(c => c.Name).Concat(replay.Actions.SelectMany(a => a.CardCodes)
            .Where(code => db.DeckCodeIds.ContainsKey(code)).Select(code => db.DeckCodeIds[code]))
            .Where(name => upgradeable.Contains(CardDatabase.ResolveBaseName(name)))
            .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var promotions = new List<Promotion>();
        var observed = new HashSet<int>();
        var journal = new List<PromotionNotification>();
        var states = new List<ActionEvidence>();
        CardSnapshot[] beforeBoard = [];
        int cursor = 0;
        var runner = new ReplayRunner(db) { IdentityCorrection = true, KreditSlotOnDuplicateStart = true };
        var report = runner.Run(replay, verbose: false, (action, state) =>
        {
            var newJournal = state.ActionLog.Skip(cursor).ToArray();
            foreach (var entry in newJournal)
            {
                if (entry.ActionType == "ZActionMakeVeteran")
                {
                    int id = Convert.ToInt32(entry.ActionData.GetValueOrDefault("cardID") ?? 0);
                    journal.Add(new(action.ActionId, id));
                }
                foreach (var sub in entry.SubActions.Where(s => s.Name == "ZActionMakeVeteran"))
                    journal.Add(new(action.ActionId, sub.Values.FirstOrDefault(v => v.Name == "cardID")?.Value ?? 0));
            }
            cursor = state.ActionLog.Count;
            var board = state.AllCards.Where(c => c.Location.IsBoard()).Select(c => c.Snapshot()).ToArray();
            states.Add(new(action.ActionId, action.TurnNumber, action.ActionType, action.ActionData,
                beforeBoard, board, newJournal));
            beforeBoard = board;
            foreach (var card in state.AllCards.Where(c => c.Keywords.Contains(Keyword.Veteran)))
            {
                if (!observed.Add(card.CardId)) continue;
                promotions.Add(new(action.ActionId, action.TurnNumber, card.CardId, card.Name, card.Attack,
                    card.Defense, card.MaxDefense, card.OperationCost, card.HeavyArmor, card.AttacksThisTurn,
                    card.RemainingAttacks, card.Keywords.OrderBy(k => k, StringComparer.Ordinal).ToArray()));
            }
        });
        var firstFailure = report.Steps.FirstOrDefault(s => !s.Applied && !s.Duplicate);
        var firstHqMismatch = report.FirstMismatch;
        string Describe(ReplayRunner.StepResult? step)
        {
            if (step is null) return "none";
            var action = replay.Actions.First(a => a.ActionId == step.ActionId);
            string cards = string.Join(",", action.CardCodes.Select(code => db.DeckCodeIds.GetValueOrDefault(code, code)));
            return $"#{step.ActionId} t{step.Turn} {step.ActionType} [{cards}] expectedHQ={step.ExpectedHq} actualHQ={step.ActualHq} {step.Failure}";
        }
        var eventIds = promotions.Select(p => p.ActionId).ToList();
        if (names.Length > 0 && firstHqMismatch is not null) eventIds.Add(firstHqMismatch.ActionId);
        var evidence = states.Where(s => eventIds.Any(id => Math.Abs(id - s.ActionId) <= 2)).ToArray();
        matches.Add(new(replay.MatchId, snapshot, snapshotHash, actionsHash, report.TotalActions,
            report.AppliedCount, report.HqChecked, report.HqMatched, names, promotions, journal,
            Describe(firstFailure), Describe(firstHqMismatch), evidence, report.Unimplemented
                .Select(kv => $"{kv.Key} x{kv.Value}").ToArray()));
        Console.WriteLine($"Match {replay.MatchId}: actions {report.AppliedCount}/{report.TotalActions}, HQ {report.HqMatched}/{report.HqChecked}, veteranCandidates={names.Length}, promotions={promotions.Count}; firstMismatch={Describe(firstHqMismatch)}");
    }
    catch (Exception ex)
    {
        inputErrors.Add($"{snapshot}: {ex.GetType().Name}: {ex.Message}");
    }
}
var output = new
{
    Description = "Real wire-action application and pre-action HQ checksum audit; no independent client board snapshots supplied. Veteran template state is only observable where a promotion actually occurs.",
    SourceDirectories = args.Skip(1).Select(Path.GetFullPath).ToArray(),
    PairedInputs = seen.Count, DuplicatePairs = duplicatePairs, Matches = matches, InputErrors = inputErrors,
    PromotionCount = matches.Sum(m => m.Promotions.Count),
    CandidateMatchCount = matches.Count(m => m.VeteranCandidates.Length > 0),
};
string outputPath = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Audited {matches.Count} replay pairs; duplicates={duplicatePairs}; candidate matches={output.CandidateMatchCount}; promotions={output.PromotionCount}; input errors={inputErrors.Count}. Report: {outputPath}");
return inputErrors.Count == 0 && matches.Count > 0 ? 0 : 1;

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
sealed record Promotion(int ActionId, int Turn, int CardId, string Name, int Attack, int Defense,
    int MaxDefense, int OperationCost, int HeavyArmor, int AttacksThisTurn, int RemainingAttacks, string[] Keywords);
sealed record PromotionNotification(int ActionId, int CardId);
sealed record ActionEvidence(int ActionId, int Turn, string ActionType, IReadOnlyDictionary<string, string> WireData,
    CardSnapshot[] BeforeBoard, CardSnapshot[] AfterBoard, GameAction[] Journal);
sealed record MatchAudit(int MatchId, string Snapshot, string SnapshotSha256, string ActionsSha256,
    int TotalActions, int AppliedActions, int HqChecked, int HqMatched, string[] VeteranCandidates,
    List<Promotion> Promotions, List<PromotionNotification> PromotionNotifications, string FirstRejectedAction,
    string FirstHqMismatch, ActionEvidence[] Evidence, string[] Unimplemented);
