# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

AutoNet is a Windows-only campus-network watchdog. A background service keeps one saved campus Wi-Fi profile connected and re-authenticates through a Dr.COM portal when the session lapses; a WinForms tray app shows status and holds credentials. Targets `net10.0-windows` on Windows 11 x64, .NET SDK pinned to `10.0.401` (`global.json`, `rollForward: disable`).

`AGENTS.md` holds the repo's own contribution guidelines (style, commit format, boundaries). This file covers commands and architecture; where they overlap, `AGENTS.md` wins.

## Commands

Everything runs through `pwsh.exe` (PowerShell 7, never `powershell.exe`), Windows only.

```powershell
pwsh.exe -NoLogo -NoProfile -File scripts/build.ps1            # Release build + regression suite
pwsh.exe -NoLogo -NoProfile -File scripts/build.ps1 -Publish   # also builds artifacts/AutoNet-win-x64/
pwsh.exe -NoLogo -NoProfile -File scripts/qa-tray.ps1          # renders tray/log UI to artifacts/ui-qa/*.png
```

`build.ps1` resolves the SDK from `.tools/dotnet/dotnet.exe` first, then PATH. It runs the tests by invoking `tests/AutoNet.Tests/bin/Release/net10.0-windows/AutoNet.Tests.dll` and fails the build on a nonzero exit. Build evidence (`build.json`, `tests.txt`) lands in the gitignored `artifacts/validation/`.

**Run a single test** — the suite is a custom console runner, not xUnit/NUnit:

```powershell
dotnet tests/AutoNet.Tests/bin/Release/net10.0-windows/AutoNet.Tests.dll --filter 重连
```

Filter is a substring match on the Chinese test name; no match exits 2.

Read-only diagnostics against a live machine (no Wi-Fi changes, no credentials):

```powershell
artifacts/AutoNet-win-x64/service/AutoNet.Service.exe --diagnose         # exit 0/1/2/3
artifacts/AutoNet-win-x64/service/AutoNet.Service.exe --diagnose-portal  # portal preconditions, never submits credentials
```

No linter or formatter is configured. `TreatWarningsAsErrors` is on for all projects, so any new warning breaks the build.

## Architecture

### The `IMonitorEnvironment` seam is the key design decision

`MonitorWorker` (`src/AutoNet.Service/MonitorWorker.cs`) contains all monitoring logic but touches no Windows API, HTTP, or filesystem directly. Every external boundary — Wi-Fi read/connect/disconnect, credential load, status write, log write, power request, HTTP client construction, auxiliary checks — goes through `IMonitorEnvironment` (`MonitorEnvironment.cs`). Production binds the real implementation; `MonitorFixture` in the tests binds a fake with a `ManualClock` and a scripted `HttpMessageHandler`.

That is why `MonitorWorker.RunOnceAsync(CancellationToken)` is public and why tests can drive multi-minute recovery scenarios in milliseconds. When adding a capability to the monitor, extend `IMonitorEnvironment` and update both implementations — never call into the OS directly from `MonitorWorker`.

### Time is split deliberately

`MonitorWorker` computes policy time as `clockOrigin + clock.GetElapsedTime(clockStart)` — monotonic, derived from `TimeProvider`. Wall-clock `GetUtcNow()` is used only for display timestamps, log correlation, and status persistence. Recovery timing and backoff must never depend on wall-clock, so tests can advance time without clock skew. Backoff schedules are converted from policy time to wall time (`PolicyNow`) before being exposed in `StatusSnapshot`.

### Pure decision logic lives in `RecoveryPolicy`

`src/AutoNet.Core/RecoveryPolicy.cs` is a dependency-free state machine: `Observation` (Wi-Fi state + direct-online + portal state) in, one of `ReconnectWifi` / `Authenticate` / `DisconnectWifi` / `None` out, plus `Attempted()` to record a completed attempt and compute the next delay.

Design invariants to preserve when editing it:

- Fast retries (2s) only for the first 15 seconds of an outage; then backoff to a 60s ceiling.
- `DisconnectWifi` (the stalled-link fallback, after 10 continuous seconds offline with credentials available and not blocked) has its own 60/120/240/300s cooldown keyed separately from the other actions, so it can never become a disconnect storm.
- Retries are tracked per action kind in `retries`; `Reset()` clears them on recovery but deliberately leaves `CredentialsBlocked` intact.
- `CredentialsBlocked` is a settable latch, not derived — it survives `Reset()` and process restarts.

### Detection: content, not status code

`HealthChecks.InternetAsync` fetches `/robots.txt` from two HTTPS hosts and requires the body to contain `User-agent:`. A captive portal's HTTP 302 or a blanket HTTP 200 does not count as being online. Clients are created with `AllowAutoRedirect = false`.

`DirectNetwork.CreateClient(link)` pins every socket to the selected Wi-Fi adapter — `IP_UNICAST_IF` plus an explicit bind to the adapter's IPv4 address — and resolves hostnames with a hand-rolled UDP DNS client (`DnsPacket`) against the adapter's own DNS servers, with a 30s per-host answer cache that is discarded when the association changes. This guarantees probes measure the campus path rather than some other route. `DnsPacket.Parse` validates transaction ID, flags, question count, and CNAME chains and throws `InvalidDataException` on anything unexpected — several tests assert this on truncated input.

### Portal protocol: verify before submitting

`DrcomPortal` walks a staged sequence against `https://login.csust.edu.cn`, recording which stage it reached:

1. `Status` — `/drcom/chkstatus` JSONP → `LoggedIn` / `NeedsLogin` / `Unknown`
2. `HomePage` — fetch `/`, regex-extract `v4ip` (the client IPv4)
3. `Configuration` — port **802** `/eportal/portal/page/loadConfig`, base64 of the client IP
4. `AuthenticationMode` — validate `en_md5`, `password_cut`, `enable_r3` are each present exactly once with value `0`
5. `Submission` — only now `/drcom/login` with the credentials

The credential-bearing GET exists in exactly one guarded branch, after all prerequisite checks pass. `LoginDetailedAsync` and `DiagnoseAsync` share this path — diagnosis simply passes `null` credentials, so it exercises identical prerequisites and can never send them.

`Classify` maps portal messages to `LoginResult`; only messages on an explicit allowlist (verified against the portal's public `a78.js`) become `RejectedCredentials`. Generic `result: 0` does not. This distinction matters: `RejectedCredentials`, `NeedsInteraction`, and `Unsupported` set `CredentialsBlocked`, which stops credential submission until credentials are re-saved — while plain `Rejected` merely backs off.

`Settings.IsSupportedPortal` pins the origin to exactly `https://login.csust.edu.cn:443/` with no userinfo, query, or fragment. `Settings.Validate` also requires the proxy to be a loopback HTTP address and the UU endpoint a token-free HTTPS URL.

### Credential blocking and storage

`Storage` writes under `C:\ProgramData\AutoNet`. Credentials are DPAPI-encrypted with `DataProtectionScope.LocalMachine` so the service can decrypt them while no user is logged on; the plaintext buffer is zeroed in a `finally`. The `private` directory ACLs are provisioned by the elevated installer, not by the app — `AtomicWrite` writes a temp file and moves it, and assumes the ACLs already exist.

`CredentialFingerprint()` is a SHA-256 of the encrypted credential file. `MonitorWorker` recomputes it each tick; when it changes, the auth-block flag is re-read, which is what makes re-saving credentials clear a block. `private/auth-block.txt` holds the fingerprint of the credentials that triggered the block.

### Logging and status

`StatusSnapshot` is the single object written to `status.json` and consumed by the tray. `ActivityLog.Create` projects it into the daily `logs/autonet-YYYY-MM-DD.jsonl` (14-day retention), and `ActivityLog.ToReadableText` renders one entry for the `LogForm` viewer, tolerating older escaped entries.

`MonitorWorker.Publish` de-duplicates log lines by a key built from the user-visible status fields, so a steady state does not spam the log. Storage and log failures are swallowed into `lastError` and surfaced in the next successful status write — logging never breaks the monitor loop.

### Recovery loop shape

`ExecuteAsync` runs `RunOnceAsync` in a loop, budgeting ~2 seconds per cycle via `clock.GetElapsedTime`, and calls `Environment.Exit(1)` on an unhandled fault (the service restart policy handles recovery). One `TickAsync`:

1. Load settings; bail to "等待设置" if absent; validate.
2. Reconcile the credential fingerprint and block state.
3. Read Wi-Fi. If another profile is connected, `policy.Reset()` and leave the user's choice alone.
4. If `Ready`, probe direct-internet, then inspect the portal (inspection is skipped entirely when online).
5. Poll the auxiliary system-path/UU task (cancelled at 3s, restarted every 20s, never overlapping, never blocking the main loop).
6. Record recovery timing when online.
7. `policy.Decide(...)` → execute at most one action.
8. Build and publish the snapshot.

Only one action runs per tick, and every action goes through `policy.Attempted()` for backoff, so an action cannot be retried before its cooldown regardless of how often the loop spins.

### Auxiliary checks are advisory

`HealthChecks.SystemPathAsync` samples DNS + `GetBestInterface` once and names a `RouteSelection` (`Tun` / `Wifi` / `Unknown`) — explicitly labelled as inference, not proof of physical egress. `UuService` reports whether the `GameViewerService` service is running and how many established TCP connections its processes hold, always with "会话待实测". None of this feeds into recovery decisions; it exists so the tray can display why traffic still isn't reaching the internet when the campus probe passes.

## Safety boundaries — do not relax

These are enforced by tests and are load-bearing, not stylistic:

- **Never** commit credentials, cookies, raw portal responses, or credential-bearing URLs. `SafeSummary` and `lastError` are the only exception surfaces, and they carry stage/failure classifications, never exception text or URLs (tests assert `!detail.Contains("secret")` against fixtures that deliberately inject secret-bearing exception messages). The service clears log providers so HTTP failures cannot leak a credential-bearing URL.
- **Never** reset or disable the adapter, reboot Windows, or touch UU/Mihomo. `Wifi.Connect` only requests a saved profile; `Wifi.Disconnect` rechecks the profile is still the configured one first.
- TLS validation stays on; redirects are disabled on the direct client. `BuildLoginQuery` percent-encodes everything, and a test pins the exact encoded output.
- `WlanQueryInterface` returning `ERROR_INVALID_STATE` (5023) means "no connection info", **not** "disconnected" — treating it as disconnected would cause spurious reconnects. Other errors must propagate rather than being read as a disconnected link.
- Credentials are submitted only on confirmed `NeedsLogin`, never while authenticated-but-offline or when the portal state is unknown.
- Get explicit authorization before any live network test that could disrupt connectivity. Never test install rollback or data purge on the machine you're connected to remotely through.

## Style

Match surrounding code: four-space indent, PascalCase types and members, `Async` suffixes, file-scoped namespaces matching the project name, and **all user-facing strings in Chinese** — status text, exception messages, log details. Keep `RecoveryPolicy` and `DrcomPortal` free of I/O so they stay testable. Avoid unrelated formatting changes.

## Adding tests

Add cases to the named tuple array in `tests/AutoNet.Tests/Program.cs` (or the `Cases` array in `MonitorRegression.cs` / `PortalRegression.cs` / `NetworkRegression.cs` / `LogRegression.cs`, which `Program.cs` concatenates). Names are descriptive Chinese sentences describing behavior. Use `MonitorFixture` with `ManualClock.Advance` to exercise backoff windows, and `MonitorHttp` flags (`Online`, `PortalResult`, `LoginResult`, `HomePage`, `ConfigJson`, `FailurePath`, `FailureException`) to drive portal states. There is no coverage threshold.

Green tests do not demonstrate the ~15-second recovery target — that requires the live acceptance in `docs/VALIDATION.md`.
