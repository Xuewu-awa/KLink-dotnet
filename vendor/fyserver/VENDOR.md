# fyserver vendor record

This directory is a source snapshot vendored into the KLink-dotnet repository.
It is kept here so a KLink-dotnet checkout contains the fyserver source used by
the integration build.

## Source

- Upstream repository: <https://github.com/CCB-TEAM/fyserver>
- Local source checkout when copied: branch `fyserver-verify`
- Local `HEAD`: `d5630a72a436d0fbe7e5feb7682da6ca538335ec`
- Local base used for the change set: `62d8d4c`
- Snapshot date: 2026-10-16

The local checkout had uncommitted changes and untracked files when it was
copied. In particular, the snapshot includes the local `Services/ServerBotService.cs`
file and the local AdminUi match-configuration additions. The snapshot should
therefore be treated as the recorded working tree, rather than as a clean copy
of the upstream `HEAD` commit.

## License and notices

The local source checkout did not contain a `LICENSE` file. The `LICENSE` in
this directory was copied from the upstream `origin/master` tree at commit
`98ceca53925a0a5df9ac10484b84e0c19cea2fbc` and is retained alongside the
source. It contains the required notice and the fyserver Noncommercial Source
License 1.0.0 restrictions. Read that file before building, distributing, or
running the server.

The upstream README and its copyright statements remain in this directory.
The KLink-dotnet repository's license does not replace or broaden the license
of this vendored project.

## KLink-dotnet integration changes

The following path changes make the checked-in source build from this
repository layout:

- `fyserver.csproj` references `../../KLink.Bot/KLink.Bot.csproj`.
- Its default `BotDataRoot` points to the sibling checkout at
  `../../../klink bot/klink bot/docs` when the repositories are arranged as
  described in `BUILDING.md`.
- The source `setting.json` was a local runtime configuration using port 1145,
  so it is omitted. `setting.example.json` provides a loopback 5231 example;
  the launcher writes the actual deployment `setting.json`.
- The local `NuGet.config` pointed to a machine-specific `nuget-local`
  directory, so it is omitted. Restore uses the normal NuGet configuration of
  the checkout or the user's machine.

Build outputs, FASTER data, deployment data, npm dependencies, and other local
runtime state are intentionally not part of this vendor snapshot.
