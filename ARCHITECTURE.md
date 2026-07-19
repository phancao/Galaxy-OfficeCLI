# OfficeCLI (Galaxy fork) — Architecture

> Scope: this repository is the **document engine** — a self-contained .NET CLI
> (`officecli`) that creates/reads/edits `.docx` / `.xlsx` / `.pptx` and ships a
> built-in stdio MCP server. In the Galaxy Vortex platform it is the engine
> behind the **`galaxy-office-mcp`** service (ADR-037). The identity wrapper,
> per-user jail, and network transport live in a **different** repo
> (`Galaxy-Nexus/mcp_servers/galaxy-office-mcp/`), not here. Every claim below is
> grounded in code at `file:line`; where a doc in this repo is stale, it is
> flagged. Verified against `officecli.csproj:8` version `1.0.136`.

## 1. Purpose

Give an AI agent a single, schema-driven surface to author and modify Office
documents. The agent writes ordinary `officecli` command lines; the tool parses
the OOXML, applies structural mutations by 1-based path (`/slide[1]/shape[2]`,
`/body/p[3]`, `/Sheet1/A1`), validates, and can render a slide/page to PNG for a
visual audit. There is **no GUI** — it is a tool for agents, not an app tile
(confirmed by the ADR-037 project note).

## 2. Engine vs. wrapper (read this first)

| Concern | This repo (engine) | `galaxy-office-mcp` wrapper (other repo) |
|---|---|---|
| Language | C# / .NET 10 | Python / FastMCP |
| Transport | MCP over **stdio** (`officecli mcp`, `Program.cs:84`) | MCP over **streamable-HTTP :3000** |
| MCP tools | exactly **one**: `officecli` (`McpServer.cs:538`) | higher-level `office_create`, `office_run`, `office_import`, `office_export_to_url`, … |
| Auth / identity | **none** | RS256/JWKS Vortex token, `mcp_access:office-mcp`, act-as |
| File sandbox | **none** (any path the process can reach) | per-user jail `/data/office/{tenant}/{sub}/`, verb whitelist, realpath guard |
| Dockerfile / compose | **none in this repo** | 2-stage Dockerfile (this binary + Chromium + libicu) |

The security-critical consequence: **the engine is unsandboxed by design.** Its
`officecli` tool can read/write/screenshot arbitrary filesystem paths. It is safe
to expose to agents *only* behind the wrapper's jail. See §7 and §9.

## 3. Tech stack

- **Runtime**: .NET 10, C# (`officecli.csproj:5`), `RootNamespace` `OfficeCli`,
  assembly name `officecli` (`officecli.csproj:6-7`).
- **Packaging**: single-file, self-contained, trimmed native binary
  (`officecli.csproj:13-15`); no shared runtime needed on the host.
- **Key dependencies** (`officecli.csproj:22-23`): `DocumentFormat.OpenXml`
  3.4.1 (OOXML read/write) and `System.CommandLine` 3.0.0-preview.2 (the one
  parser shared by CLI and MCP).
- **Embedded resources** (`officecli.csproj:26-56`): help schemas
  (`schemas/help/**`), skill markdown (`skills/**`), preview CSS/JS, chart/effect
  XML templates. No on-disk extraction — read via `Assembly.GetManifestResourceStream`.
- **Rendering**: `view … screenshot` / `view … html` shell out to a headless
  Chrome-family browser (or Playwright/Firefox), not an embedded engine
  (`Core/HtmlScreenshot.cs:10-11`, `--headless=new --no-sandbox`).

## 4. Component map

```
src/officecli/
  Program.cs                 Entry point + early dispatch (help, mcp, install, skills, config)
  McpServer.cs               Built-in stdio JSON-RPC 2.0 MCP server (the "server")
  McpInstaller.cs            Registers `officecli mcp` into Claude Code / Cursor / VS Code / LM Studio
  CommandBuilder*.cs         System.CommandLine root: every verb (view/get/set/add/…) + batch
  ResidentServer.cs /        Long-lived per-file process that keeps a doc in memory (named-pipe RPC)
    ResidentClient.cs
  BlankDocCreator.cs         `create` — writes a blank .docx/.xlsx/.pptx to disk
  Handlers/                  Word / Excel / PowerPoint OOXML mutation logic (bulk of the code)
  Core/                      SSRF guard, screenshot, update-checker (disabled), installer, helpers
  schemas/help/**            Per-element JSON schemas surfaced by `help <format> <element>`
  skills/**                  Bundled SKILL.md guides surfaced by `load_skill`
```

## 5. MCP server & tool inventory

The MCP server (`McpServer.RunAsync`, `McpServer.cs:21`) is a hand-written
JSON-RPC 2.0 loop over stdio. Protocol version `2024-11-05`
(`McpServer.cs:183`). Methods handled (`McpServer.cs:114-124`): `initialize`,
`notifications/initialized`, `tools/list`, `tools/call`, `ping`.

At the protocol level it advertises **exactly one tool, `officecli`**
(`McpServer.cs:212-216, 538`). That tool takes a single `command` argument — a
CLI command line as a string or a pre-split argv array (`McpServer.cs:550-558`)
— and runs it through the **same** `System.CommandLine` root the terminal CLI
uses (`RunCliRaw`, `McpServer.cs:449-460`). The command string is tokenized
in-process with a quote-aware tokenizer that **never invokes a shell**
(`Tokenize`, `McpServer.cs:332`), so there is no shell-injection surface.

The real "document operations" are therefore the CLI **verbs** dispatched inside
that one tool. Inventory (registered in `CommandBuilder.BuildRootCommand`,
`CommandBuilder.cs:166-200`; batch equivalents in `ExecuteBatchItem`,
`CommandBuilder.cs:626-949`):

| Verb | Operation | Anchor |
|---|---|---|
| `create` | Create a blank .docx/.xlsx/.pptx (optionally `--locale`, `--minimal`) | `CommandBuilder.Import.cs:197` |
| `view` | Render a doc: modes `text·annotated·outline·stats·issues·html·svg(pptx)·screenshot·forms(docx)` | `CommandBuilder.cs:176`, batch `:870` |
| `get` | Read one element by path (with `--depth`) | `CommandBuilder.cs:177`, `:628` |
| `query` | Select elements by predicate, e.g. `cell[bold=true]`, `row[Score>80]` | `CommandBuilder.cs:178`, `:646` |
| `set` | Set props on an element (typo auto-correct, unsupported-prop reporting) | `CommandBuilder.cs:179`, `:669` |
| `add` | Add a child element (`--type`) or copy from another (`from`) | `CommandBuilder.cs:180`, `:741` |
| `remove` | Delete an element (Excel cell `--shift`) | `CommandBuilder.cs:181`, `:828` |
| `move` | Move an element to a new parent/position | `CommandBuilder.cs:182`, `:839` |
| `swap` | Swap two elements | `CommandBuilder.cs:183`, `:851` |
| `refresh` | Recalculate/refresh (e.g. formula/derived state) | `CommandBuilder.cs:184` |
| `raw` / `raw-set` | Read / patch raw OOXML part XML (escape hatch) | `CommandBuilder.cs:185-186`, `:896` |
| `add-part` | Create a new OOXML part (chart, smartart, media, ole, …) | `CommandBuilder.cs:187`, `:910` |
| `validate` | OOXML schema validation | `CommandBuilder.cs:188`, `:919` |
| `save` | Flush in-memory edits to disk, keep resident warm | `CommandBuilder.cs:189` |
| `batch` | Apply many mutations in one open/save cycle (JSON items) | `CommandBuilder.cs:190` |
| `dump` | Serialize a doc to a batch/edit script | `CommandBuilder.cs:191` |
| `import` | Bulk CSV/TSV import into .xlsx | `CommandBuilder.cs:192`, `:792` |
| `merge` | Template/data merge | `CommandBuilder.cs:194` |
| `plugins` | List/inspect bundled plugins | `CommandBuilder.cs:195` |
| `open` / `close` | Start / stop the in-memory **resident** for a file | `CommandBuilder.cs:29, 71` |
| `help` | Schema-driven capability + element reference | `CommandBuilder.cs:200` |
| `load_skill` / `skills` | Read bundled SKILL.md guidance (MCP-served, `McpServer.cs:368`) | `Program.cs:125, 170` |

Two MCP-side special cases short-circuit `RunCliRaw`: `load_skill`/`skill(s)` is
served from the embedded `SkillInstaller` (`McpServer.cs:292`), and
`view … screenshot` renders to a temp PNG and returns it **inline as a base64
`image` content block** (`RunScreenshotArgv`, `McpServer.cs:388-421`).

`McpInstaller.cs` registers this stdio server into local AI clients (Claude Code
via `claude mcp add -s user`, else `~/.claude.json`; Cursor `~/.cursor/mcp.json`;
VS Code; LM Studio plugin) — dev-machine convenience, not used by the prod
wrapper.

## 6. Auth & identity model

**There is no authentication or authorization in this repository.** Grep for
`bearer|authorization|jwt|act_as|x-galaxy-user` over `src/` returns only
unrelated hits (a PowerPoint transition literally named `vortex`). The engine
trusts its caller completely and acts on whatever file paths it is handed.

Identity is entirely the wrapper's job (ADR-037): `galaxy-office-mcp/auth.py`
validates a Vortex RS256/JWKS token (`iss=galaxy-nexus`, `aud=galaxy-mcp`),
requires the `mcp_access:office-mcp` grant, and only honors `act-as` from a
service token. The wrapper then shells out to *this* binary inside a per-user
jail. Consequently the engine's own `officecli mcp` server must never be exposed
to an agent directly — see §9.

## 7. File handling

- **Input/output is by filesystem path.** Every verb takes the document path as
  an argument; the caller owns where files live. `create` writes a blank OOXML
  file to the given path (`BlankDocCreator.Create`, `CommandBuilder.Import.cs:197`).
- **Resident process model.** To avoid re-parsing on every command and to
  serialize concurrent writers, the first mutation on a file may fork a
  `__resident-serve__` subprocess that holds the doc in memory and owns saves
  (`CommandBuilder.cs:119-163`, `TryResident` `:365`). An exclusive `.lock` file
  in temp makes it a per-file singleton (`CommandBuilder.cs:136-157`).
- **Durability.** Non-resident commands eager-save to disk; a resident uses a
  deferred flush (adaptive 2–10 s, env `OFFICECLI_RESIDENT_FLUSH`) and flushes on
  `save`/`close`/idle (`CommandBuilder.cs:71`). `save`/`close` guarantee bytes on
  disk before hand-off.
- **MCP defaults.** The stdio server forces `OFFICECLI_NO_AUTO_RESIDENT=1` and
  `OFFICECLI_BATCH_ALLOW_STDIN_REDIRECT=1` unless already set
  (`McpServer.cs:41-52`); the wrapper also pins `OFFICECLI_NO_AUTO_RESIDENT=1` in
  its multi-user container.
- **Screenshots** render to `Path.GetTempPath()` and are returned inline; an
  auto-injected temp PNG is deleted after capture (`McpServer.cs:411-414`).
- **Large batch output** (>8 KB JSON) spills to a temp file and a slim envelope
  is returned (`CommandBuilder.cs:1040-1078`).
- **Remote fetches** (`picture=URL`, table/model/media `data=URL`) go through the
  SSRF guard (§9), capped at 100 MB (`Core/SsrfGuard.cs:78`).

## 8. Deploy topology

- **No Dockerfile/compose in this repo** — verified. It produces binaries only.
- **Build**: `build.sh` (local, current RID) or `build.sh all` →
  `dotnet publish -c Release -r <rid> --self-contained` for 8 targets
  (mac/linux/win × arm64/x64 + linux-musl). CI `.github/workflows/build.yml`
  builds the same matrix on tag `v*`, code-signs + notarizes macOS, runs
  create/add/get/close + install smoke tests, and publishes a **draft** GitHub
  release with `SHA256SUMS`.
- **Galaxy production**: the wrapper pins this fork at a specific commit
  (ADR-037 note: `e56cc568`), builds the linux binary inside its own 2-stage
  Dockerfile (adding Chromium for screenshots and `libicu` — without ICU .NET
  aborts with "Process terminated"), and runs it non-root behind the FastMCP
  HTTP service on `galaxy_network`. Public entry:
  `https://ai.skyplatform.net/office-mcp/mcp` (nginx strip-prefix → `:3000`).
- **Asset mirror**: HTML/preview KaTeX + mermaid URLs come from
  `OFFICECLI_ASSET_MIRROR_BASE` when set, else the public jsdelivr CDN
  (`Core/KatexAssets.cs:30-33`) — the fork never hardcodes the upstream VPS.

## 9. Galaxy fork delta & security notes

Fork hardening (see `GALAXY-FORK.md`, `NOTICE`), all done as early-returns to
keep upstream-sync diffs minimal:

- **Self-update disabled**: `UpdateChecker.CheckInBackground()` returns
  immediately (`Core/UpdateChecker.cs:46-53`); `__update-check__` is a no-op that
  prints a notice (`Program.cs:27-34`).
- **Implicit self-install disabled**: `Installer.MaybeAutoInstall()` returns
  immediately (`Core/Installer.cs:185-191`); explicit `officecli install` still
  works for dev machines.
- **No upstream asset mirror at runtime** (`Core/KatexAssets.cs:26-33`).

Security posture:

- **SSRF guard is solid.** `Core/SsrfGuard.cs` validates the *actual* connect-time
  IP on every hop (`CreateGuardedHandler:36`), blocking loopback/RFC1918/link-local
  169.254 (cloud metadata)/CGNAT/ULA (`IsPublicAddress:110`), closing the
  DNS-rebind window. 100 MB body cap (`:88`).
- **No shell injection** in the MCP tool — in-process tokenizer, no shell
  (`McpServer.cs:332`).
- **Engine is unsandboxed (by design).** The `officecli` tool accepts any path;
  flags like `--file` (host read), `-o/--out` (arbitrary write), and
  `view <anypath> screenshot` are all reachable. The wrapper's own sec-review
  (ADR-037 PR #621) confirmed these and added the jail/verb-whitelist that makes
  the service safe. **Do not register `officecli mcp` directly into an agent that
  handles untrusted input without an equivalent jail.**

### Stale / dead code found (reported, not modified)

- **Dead hourly upgrade loop in the MCP server.** `McpServer.cs:70-72` and
  `RunPeriodicUpgradeCheckAsync` (`:146-173`) fire `UpdateChecker.CheckInBackground()`
  at startup and every hour — but that method now returns on line one
  (`UpdateChecker.cs:53`), so the loop, its `CancellationTokenSource`, and the
  background task are inert. The surrounding comments (`McpServer.cs:54-69`) still
  describe upstream auto-upgrade behavior and reference a removed
  `Program.cs:112` auto-upgrade path — **stale**. Harmless (no network, no
  spawn), but misleading; safe to delete on the next upstream sync.
- **Dead self-update machinery retained**: `UpdateChecker.RunRefresh` /
  `SpawnRefreshProcess` / download+verify (`Core/UpdateChecker.cs:102-282`) and
  the `d.officecli.ai` mirror constant (`:38`) are unreachable in the fork
  (kept for minimal diff per fork policy). `AppConfig.AutoUpdate` still defaults
  `true` (`:810`) with no effect.
- **Dev installers still hit upstream**: `install.sh` / `install.ps1` download
  from `d.officecli.ai` (github fallback), and CI exercises them. These are for
  dev machines; the prod binary is built from source and never runs them.
- **Upstream READMEs are partly stale for this fork**: `README*.md` still
  describe auto-update / auto-install as live. This document is the corrected
  source of truth for the fork.
