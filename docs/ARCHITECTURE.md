# Architecture (short)

Söz is a .NET 10 solution (`Soz.sln`) with a Svelte UI.

| Project | Role |
|---|---|
| `src/Soz.Core` | Engine: job runner, agent connectors (Claude Code, Codex), evidence gate (`Proof`), guard rules, secret filter (`Text/SecretFilter.cs`), SQLite storage and schema migrations, history import and search, decision ledger. |
| `src/Soz.Server` | Local HTTP API and the pre-built UI (`wwwroot`). Rejects accept/settings requests that come from an agent process. |
| `src/Soz.Cli` | `soz.exe`: command line (`soz help`), `serve`, `hooks install`, `job ...`, `doctor`. |
| `src/Soz.Shell` | `Soz-Masaustu.exe`: Windows tray and window host (WebView2), self-update check, installer integration (Velopack). |
| `src/Soz.Capsule` | The small always-on-top capsule window and its logic. |
| `src/Soz.Kanca` | Source of the tiny hook relay (`soz-kanca.exe`, .NET Framework, built with `derle.sh`). |
| `ui` | Svelte UI source; `npm run build` writes into `src/Soz.Server/wwwroot`. |
| `test` | xUnit tests (`Soz.Core.Tests`, `Soz.Capsule.Tests`), synthetic sample data (`ornek-veri` with session logs stored as `*.jsonl.sample` and renamed on copy by the test harness, `kayit-ornekleri`, `sema-ornekleri`) and fake agent scripts (`sahte-*`) used instead of the real tools. |

## Main flow

1. You describe a task and pick a project folder. Söz proposes checks (test command, file, URL) by reading project files; you approve them once.
2. Söz snapshots the project (test definitions and test files get a fingerprint), runs a baseline test pass, then starts the agent in the background (a job object, with billing environment keys stripped).
3. Agent events are turned into plain progress lines. Guard rules run before tool calls through a Claude Code hook (Claude) or an OS sandbox (Codex).
4. When the agent stops, the evidence gate runs the approved checks, compares the snapshot, and either marks the job green, sends the agent back with the failing check (up to three rounds), or asks you to decide when test files or counts changed.

Interpretation: this document is a summary written for the public repository; the user guide (`docs/KULLANIM.tr.md`) is the detailed reference.
