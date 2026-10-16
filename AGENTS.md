# Repository Guidelines

## Project Structure & Module Organization

- `src/AutoNet.Core/`: recovery policy, Dr.COM authentication, Wi-Fi access, network probes, and credential storage.
- `src/AutoNet.Service/`: Windows service, monitoring loop, and AC power wake requests.
- `src/AutoNet.Tray/`: WinForms status window, settings, and tray controls.
- `src/AutoNet.Tray.Avalonia/`: Avalonia UI application under migration (tray, lifecycle, single instance, elevation routing, shell navigation and status pipeline, overview page with explicit recovery-switch/unblock actions, the full unblock flow — confirm, read-only recheck, revision check, durably audited release, the read-only five-category diagnostics page, the logs page — date entry, bounded list with explicit scope, per-record detail, sanitized copy, folder entry, gap disclosure, latest-selection-wins switching, the settings page — read-only guarded-config projection with a modify entry that elevates separately, plus the elevated editor window enforcing the existing credential rules with no pause release, and the theme preference — follow-system by default, manual light/dark applied at startup in every mode, per-user storage outside the guarded business model with no secrets); it replaces the WinForms tray only at the candidate-package stage.
- `tests/AutoNet.Tests/`: executable regression suite and simulated HTTP responses.
- `scripts/`: build, packaging, installation, uninstallation, and QA evidence (see `scripts/qa-avalonia-prototype.ps1` for the #2 compatibility gate, `scripts/qa-avalonia-shell.ps1` for the shell suite, `scripts/qa-avalonia-overview.ps1` for the overview state matrix and action-intent records, `scripts/qa-avalonia-unblock.ps1` for the unblock sequence and side-effect evidence, `scripts/qa-avalonia-diagnostics.ps1` for the diagnostics five-category matrix and read-only boundary evidence, `scripts/qa-avalonia-logs.ps1` for the logs-page scenario matrix, gap disclosure, latest-wins switching, and sanitized-copy evidence, `scripts/qa-avalonia-settings.ps1` for the settings read-only projection, modify-entry intent, and elevated-editor credential rules with block-preservation evidence, and `scripts/qa-avalonia-theme.ps1` for the theme preference — follow-system default, manual light/dark via real radio buttons, restart persistence, and per-user isolation evidence).
- `CONTEXT.md`: domain glossary shared by code, logs, and docs. `docs/adr/`: architecture decision records.
- `docs/VALIDATION.md`: verified behavior and remaining manual acceptance checks. `docs/UI-COMPAT-PROTOTYPE.md`: the Avalonia compatibility go/no-go record.

Keep generated binaries in ignored `artifacts/` and local SDK tooling in ignored `.tools/`. Application branding lives in `assets/AutoNet.png` and `assets/AutoNet.ico`; network-state glyphs are generated in code.

## Build, Test, and Development Commands

Use Windows, PowerShell 7, and a stable .NET 10 SDK. Run PowerShell commands through `pwsh.exe`, never `powershell.exe`. Before the first command, verify:

```text
pwsh.exe -NoLogo -NoProfile -Command "$PSVersionTable.PSVersion"
```

Stop if PowerShell 7 is unavailable.

```text
pwsh.exe -NoLogo -NoProfile -File scripts/build.ps1
pwsh.exe -NoLogo -NoProfile -File scripts/build.ps1 -Publish
```

The first command builds Release and runs regression checks; the second also creates self-contained Windows x64 executables in `artifacts/AutoNet-win-x64/`. The script prefers `.tools/dotnet/dotnet.exe`, then PATH.

Run the packaged `service/AutoNet.Service.exe --diagnose` for read-only network diagnostics, or `tray/AutoNet.Tray.exe` to inspect the UI locally.

## Coding Style & Naming Conventions

Match surrounding C# style: four-space indentation, PascalCase types and public members, camelCase locals, and `Async` suffixes for asynchronous methods. Keep namespaces aligned with project names and user-facing text in Chinese. Nullable references and warnings-as-errors are enabled centrally in `Directory.Build.props`. No dedicated formatter or linter is configured; avoid unrelated formatting changes.

## Testing Guidelines

Tests use a custom console runner, not xUnit or NUnit; failures return a nonzero exit code. Add behavior-focused cases to the named tuple list in `tests/AutoNet.Tests/Program.cs`, using descriptive Chinese names and simulated HTTP responses. Cover changed recovery, authentication, and parsing behavior. No numeric coverage threshold exists. Passing checks do not prove actual 15-second recovery; follow `docs/VALIDATION.md` for live acceptance.

## Commit & Pull Request Guidelines

History currently uses Chinese Conventional Commits, for example `feat: 添加 Windows 校园网自动恢复服务与托盘应用`. Continue with focused `feat:`, `fix:`, `docs:`, or `test:` commits. PRs should explain behavior changes, validation results, remaining limitations, and related issues when available. Include screenshots for UI changes.

## Security & Operational Boundaries

Never commit credentials, cookies, raw portal responses, or credential-bearing URLs. Preserve DPAPI encryption, restrictive configuration ACLs, and TLS validation. Repair only the selected campus network; do not reset adapters, reboot Windows, or modify UU/Mihomo. Obtain explicit authorization before disruptive live network tests.
