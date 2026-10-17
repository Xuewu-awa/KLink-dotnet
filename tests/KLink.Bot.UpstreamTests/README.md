# Pinned BotSim verification

From the KLink-dotnet repository root (`src/` in the integration workspace):

```powershell
pwsh -File tests/Run-BotVerification.ps1 -BotSourceRoot 'D:/checkouts/KLink-bot'
```

The checkout must contain commit `89515569c036b295e14aeaa4d759d50891192b20`.
Pass `-BotDataRoot` if its `klink bot/docs` inputs live elsewhere.
The six required data files are checked against the SHA-256 hashes in
`upstream.lock.json`; data is not regenerated or downloaded by the runner.

The runner reads the five BotSim C# sources from the pinned Git commit into
ignored `obj/pinned/` together with replays 508065, 310284 and 955337, applies
the five exact fixture replacements recorded in `veteran-fixtures.json`, and
compiles them against this repository's live kernel. Each replacement must
match exactly once. These changes select cards with real `_vet` upgrades; all
upstream assertions are retained. Existing checkout files are not used as the
source of staged tests.

The local regression project runs first, including the two real-promotion replay
contracts, followed by all 530 upstream selftests.
The command fails on source, data, fixture, build, or test errors.
