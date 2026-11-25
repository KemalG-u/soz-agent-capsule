> 🚧 **Status: active development.** It works for the author; interfaces may change.

# Söz (soz-agent-capsule)

[Türkçe](README.tr.md)

Söz ("word", Turkish) is a Windows desktop capsule and local server that runs a coding agent (Claude Code, optionally Codex) for you and does not take its word for "done". When the agent says it has finished, Söz runs the checks you approved (tests, a file that must exist, a URL that must open), and if they fail it sends the agent back with the name and output of the failing check, up to three rounds. A task is only shown as green when the checks passed; with no checks it is marked "unproven", never "passed".

## Why it exists

Agents often report success without proof, and watching a terminal to catch that is tedious. Söz moves that watching into a small panel with plain-language progress lines, a rule-based guard that asks before risky commands (deleting folders, writing outside the project), and an evidence gate that compares test files and test counts before and after the run, so "tests pass" cannot be reached by editing the tests.

## Features

- Task panel (desktop capsule or browser) with plain progress lines instead of a terminal.
- Evidence gate: runs approved checks when the agent stops and sends it back on failure.
- Test-tampering detection: changed or removed test files, emptied test commands and dropped test counts are flagged and need your decision.
- Rule-based guard (no model involved) for writes outside the project, protected paths, forbidden commands and ask-first commands.
- Ledger of decisions, rules and bans extracted from past conversations (nothing becomes a rule until you approve it), search over past sessions with a secret filter, usage view.
- Optional read-only phone view on the local network (off by default).
- Everything stays on the local machine; the data folder is a SQLite file next to the executable (or `SOZ_DATA`).

## Requirements

- Windows 10 or 11 (the desktop shell needs WebView2; the CLI and server are plain .NET).
- .NET 10 SDK to build from source (`global.json` pins 10.0.402).
- A locally installed and logged-in `claude` command (Claude Code); Söz uses your own login, no API key. Codex is optional.
- Node.js only if you want to rebuild the UI in `ui/`; a pre-built bundle is committed in `src/Soz.Server/wwwroot`.

## Quick start

```
git clone https://github.com/KemalG-u/soz-agent-capsule.git
cd soz-agent-capsule
dotnet build Soz.sln
dotnet test Soz.sln
dotnet run --project src/Soz.Cli -- serve --open
```

`serve` starts the local server and opens the UI in the browser (default port 49300). Add a project folder, write the task, press Ctrl+Enter. Run it from a normal terminal, not from inside a Claude Code session: Söz deliberately rejects accept/settings requests that come from inside a Claude session. The desktop shell is `src/Soz.Shell` (`Soz-Masaustu.exe`); publish commands are in the user guide.

More: [docs/KULLANIM.tr.md](docs/KULLANIM.tr.md) is the full user guide (Turkish only for now). The command line help (`soz help`) and the UI strings are Turkish by default; an English UI string table exists (`ui/src/lib/i18n/en.ts`) but some sentences still fall back to Turkish.

## Limits and unverified claims

Söz is a care layer, not a security boundary. It is meant to make an honest but careless agent visible; it does not stop a determined, malicious agent running under the same Windows account.

- The guard and isolation claims (guard rules, running project code inside a job object, rejecting accept/settings calls that come from an agent process) are **not independently verified**. They were checked by the author with unit tests and his own adversarial reviews only.
- The guard misses things by design: commands whose target is a variable, work done inside scripts or `python -c`, aliases, encoded PowerShell, writes through symlinks, non-shell tools (e.g. MCP tools). The full list is in the user guide ("Bilinen sınırlar").
- Codex is supported through a connector, but its guard is only the operating-system sandbox of the tool, and how strict that sandbox is on Windows was not verified.
- The secret filter is heuristic and can miss unusual secrets.
- The phone endpoint uses plain http on the local network; keep it off on shared networks.
- Real-window behaviour of the desktop shell (tray menu, global hotkey, folder picker) is covered by self-tests and unit tests but was not clicked through on every release.
- The installer (Velopack) is unsigned; Windows SmartScreen may warn. The installer build scripts are not part of this repository.
- The installed app checks an update address that is compiled in; copies run from a publish folder never go online. Override or disable with `SOZ_UPDATE_URL` or `"guncellemeDenetimi": false` in `veri\shell-tercihleri.json`.

## Known issues

- The end-to-end scripts (`test/e2e*.ps1`, `test/e2e-codex.mjs`, `test/kanca-sure.mjs`, `ui/scripts/*.mjs`) were written for the author's local layout and need a published build plus a small sample project that is not included here; they are not run in CI.
- The hook relay executable `soz-kanca.exe` is not included (binaries are not committed). Build it with `src/Soz.Kanca/derle.sh` (uses the .NET Framework `csc.exe` that ships with Windows) and place it next to `soz.exe`; without it hooks run through `soz.exe` itself (slower start-up).

## Test status

Measured on 24 Nov 2026 on the author's Windows 11 machine with .NET SDK 10.0.402, in a clean copy of this tree: `dotnet build Soz.sln` finished with 0 errors and 18 warnings; `dotnet test` passed 554 of 554 tests in `Soz.Capsule.Tests` and 1878 of 1878 in `Soz.Core.Tests` (the Core suite takes about 7 minutes). The GitHub Actions workflow (`.github/workflows/ci.yml`) has not run yet, so a green CI is not claimed.

## License

Apache-2.0 (see `LICENSE`, `NOTICE`, `THIRD-PARTY.md`). Copyright (c) 2026 Kemal (KarıncaAI).

AI use: this project is written with AI coding agents under the author's direction and review.
