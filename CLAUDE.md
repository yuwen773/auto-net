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
- `DisconnectWifi` requires at least 30 continuous seconds and 3 completed failed observation rounds, credentials available, no authentication block, and `AllowActiveDisconnect=true` (the default, including old configurations). It has a separate persistent cap of 3 requests per outage and a fixed minimum 120s cooldown from durable request completion; failed requests also consume budget. Unconfirmed completion remains blocked across restart.
- Retries are tracked per action kind in `retries`; `Reset()` clears them on recovery but deliberately leaves `CredentialsBlocked` intact.
- Authentication permission comes from the versioned persistent `AuthenticationBlock`. `CredentialsBlocked` reflects its conservative state in the policy; reset, restart, saving settings or changing credentials never clears a terminal pause. Disconnect budget resets only after 60 continuous seconds online and a successful durable reset; an unconfirmed request is not reset this way.

### Detection: content, not status code

`HealthChecks.InternetAsync` fetches `/robots.txt` from two HTTPS hosts and requires the body to contain `User-agent:`. A captive portal's HTTP 302 or a blanket HTTP 200 does not count as being online. Clients are created with `AllowAutoRedirect = false`.

`DirectNetwork.CreateClient(link)` pins every socket to the selected Wi-Fi adapter — `IP_UNICAST_IF` plus an explicit bind to the adapter's IPv4 address — and resolves hostnames with a hand-rolled UDP DNS client (`DnsPacket`) against the adapter's own DNS servers, with a 30s per-host answer cache discarded when the client is recreated. This establishes socket-binding intent and content-probe evidence; it does not prove physical egress or exclude TUN interception. Physical egress requires separate live capture, and route samples remain inference. `DnsPacket.Parse` validates transaction ID, flags, question count, and CNAME chains and throws `InvalidDataException` on anything unexpected — several tests assert this on truncated input.

### Portal protocol: verify before submitting

`DrcomPortal` walks a staged sequence against `https://login.csust.edu.cn`, recording which stage it reached:

1. `Status` — `/drcom/chkstatus` JSONP → `LoggedIn` / `NeedsLogin` / `Unknown`, and the client IPv4 from its `v4ip` field
2. `GatewayRedirect` — plain-HTTP probe of `www.msftconnecttest.com` with redirects disabled; accept context only from HTTP 302 with an absolute HTTPS 443 root `Location` on `login.csust.edu.cn`, without userinfo or fragment. Relative/untrusted targets, other statuses with Location, and repeated context fields are retriable failures without credential submission. A 200 without Location supplies no context and retains the IPv4 fallback.
3. `HomePage` — **fallback only**, reached when neither the gateway nor the status response has an address; regex-extracts `v4ip` from `/`
4. `Configuration` — port **802** `/eportal/portal/page/loadConfig`, base64 of the client IP
5. `AuthenticationMode` — validate `en_md5`, `password_cut`, `enable_r3` are each present exactly once with value `0`
6. `Submission` — port **802** `/eportal/portal/login`, with `user_account` / `user_password` and the gateway fields

**Client IPv4 priority is gateway redirect → status response → home-page fallback.** The home page can omit `v4ip` before a session exists, so relying on it caused failures when login was needed. Keep the ordering if you touch it.

If every IPv4 source is temporarily unavailable, return `Rejected/MissingClientIpv4` and back off rather than persisting a credential block. Addresses must be canonical unicast IPv4, excluding unspecified, loopback and broadcast addresses. The gateway MAC remains an opaque nonblank token without whitespace/control characters; do not assume it is a physical MAC address.

**Submission is implemented but not verified end to end.** The parameter set comes from the portal's own request, but three things could not be confirmed without submitting a real password: the `,0,` account prefix, whether `wlanusermac` is stable per client or regenerated per redirect, and whether the `PHPSESSID` cookie the browser carried is required. A historical live test returned "只能绑定指定的VLAN设备"; that does not establish the cause or prove a MAC/AC defect. Without required gateway MAC/AC context, `Submission` returns `Rejected/MissingGatewayParameters` and sends nothing. This permits bounded preflight retries, not unlimited attempts: exhausted attempts require manual review.

`LoginDetailedAsync` and `DiagnoseAsync` share this path — diagnosis passes `null` credentials and checks required gateway MAC/AC context before returning success, so it exercises identical prerequisites and can never send them. Missing context returns `Rejected/MissingGatewayParameters` in both paths. Unknown historical block markers remain intact; successful read-only diagnosis alone does not clear them.

`Classify` maps portal messages to `LoginResult`; only messages on an explicit allowlist (verified against the portal's public `a78.js`) become `RejectedCredentials`. Generic `result: 0` does not. Terminal failures pause submissions and proactive disconnect until an explicit manual release. Only an explicit temporary refusal permits another credential submission; an ambiguous result after sending retains `SubmissionPending`. Authentication attempts, including preflight, are bounded at 3 per unresolved outage.

`Settings.IsSupportedPortal` pins the origin to exactly `https://login.csust.edu.cn:443/` with no userinfo, query, or fragment. `Settings.Validate` also requires the proxy to be a loopback HTTP address and the UU endpoint a token-free HTTPS URL.

### Credential blocking and storage

`Storage` writes under `C:\ProgramData\AutoNet`. Credentials are DPAPI-encrypted with `DataProtectionScope.LocalMachine` so the service can decrypt them while no user is logged on; the plaintext buffer is zeroed in a `finally`. The `private` directory ACLs are provisioned by the elevated installer, not by the app — `AtomicWrite` writes a temp file and moves it, and assumes the ACLs already exist.

`private/auth-block.txt` now holds a versioned `AuthenticationBlock` with state, failure, attempts, retry and revision; old fingerprint-shaped or damaged records remain conservatively blocked. `MonitorWorker` reloads and reconciles this state each cycle. Before sending credentials it must durably reserve `SubmissionPending`; write failure prevents submission, and an unknown result cannot grant a retry. Re-saving credentials or settings does not release a pause. The elevated `--unblock-auth` flow requires explicit confirmation, read-only diagnosis, final settings/link and revision checks, and a durable audit before deletion; audit failure prevents release. Disconnect budget is independent and is not cleared by this action.

### Logging and status

`StatusSnapshot` is the single object written to `status.json` and consumed by the tray. `ActivityLog.Create` projects it into the daily `logs/autonet-YYYY-MM-DD.jsonl` (14-day retention), and `ActivityLog.ToReadableText` renders one entry for the `LogForm` viewer, tolerating older escaped entries.

`MonitorWorker.Publish` de-duplicates log lines by a key built from the user-visible status fields, so a steady state does not spam the log. Storage and log failures are swallowed into `lastError` and surfaced in the next successful status write — logging never breaks the monitor loop.

### Recovery loop shape

`ExecuteAsync` runs `RunOnceAsync` in a loop, budgeting ~2 seconds per cycle via `clock.GetElapsedTime`, and calls `Environment.Exit(1)` on an unhandled fault (the service restart policy handles recovery). One `TickAsync`:

1. Load settings; bail to "等待设置" if absent; validate.
2. Load the persistent disconnect budget for the selected network and reconcile versioned authentication state, retry and pending writes.
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
