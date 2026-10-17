# Veteran replay audit

`KLink.Bot.ReplayAudit` runs paired `replay-*.json` / `replay-*.actions.json`
inputs through the integrated kernel and reports:

- whether every wire action applied;
- pre-action `action_data["84"]` HQ checksums;
- real `ZActionMakeVeteran` events and the promoted card's kernel state;
- nearby wire actions, journals and board snapshots for each promotion and first
  HQ mismatch;
- unhandled dispatch names and input-load failures.

It does **not** validate promotions against independent client board-property
snapshots. The replay wire carries HQ checksums and actions, not full per-card
attributes. A clean audit therefore proves action application and HQ agreement,
while promoted attack, defense, keywords and cost remain kernel-state
observations backed by Blueprint-derived regression tests.

## Run

From the KLink-dotnet repository root (`src/` in the integration workspace):

```powershell
dotnet run --project tests/KLink.Bot.ReplayAudit/KLink.Bot.ReplayAudit.csproj `
  -c Release -p:NuGetAudit=false `
  "-p:BotDataRoot=C:/checkouts/KLink-bot/klink bot/docs" `
  -- tests/KLink.Bot.ReplayAudit/obj/veteran-audit.json `
  "C:/checkouts/KLink-bot/klink bot/docs/live-replays" `
  "C:/checkouts/KLink-bot/out/_server-replays"
```

Arguments are `<report.json> <replay-directory> [replay-directory ...]`.
Duplicate semantic replay pairs (same match, cards and actions) are counted and
skipped. Input-load errors make the command exit non-zero; rule mismatches do
not. Read the JSON report rather than treating exit code 0 as full agreement.

The audit uses `ReplayRunner` with `IdentityCorrection = true` and
`KreditSlotOnDuplicateStart = true`, matching the previously established
diagnostic configuration.

## Current pinned findings

Using the commit `89515569c036b295e14aeaa4d759d50891192b20` data:

| Match | Actions | HQ checks | Promotions |
| --- | --- | --- | --- |
| 310284 | 93/95 | 93/93 | 2 |
| 955337 | 19/20 | 16/16 | 1 |

The non-applied actions are trailing envelopes without a determinable actor;
310284's `#93` is additionally a full support-line play rejection. Neither is a
veteran failure. Earlier, 310284 reported its first HQ mismatch at `#49`
(`expectedHQ=15`, `actualHQ=16`); the frontline buff fix removed that mismatch.

Auditing all 16 pinned `out/_server-replays` pairs and the 7 pinned
`klink bot/docs/fresh-replays` pairs produced **zero** promotions. Only one
fresh-replay deck was a promotion candidate. Those older replays also have
pre-existing HQ divergences unrelated to veterans, so the live replays remain
the only real-promotion corpus.

Pinned regression coverage is in `KLink.Bot.RegressionTests` when it receives a
replay directory as its second argument. It asserts both matches' HQ counts,
the three exact promotion observations, and Brewster's surviving frontline
attack for 310284 action `#48`. `Run-BotVerification.ps1` stages these replay
files from the pinned commit and runs that coverage automatically.

Replay 955337 is a diagnostic live match containing `XActionCheat` actions; it
is not evidence of an unmodified game and should not be described as such.
