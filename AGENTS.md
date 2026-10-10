# Repository Guidelines

## Project Structure & Module Organization

- `src/AutoNet.Core/`: recovery policy, Dr.COM authentication, Wi-Fi access, network probes, and credential storage.
- `src/AutoNet.Service/`: Windows service, monitoring loop, and AC power wake requests.
- `src/AutoNet.Tray/`: WinForms status window, settings, and tray controls.
- `tests/AutoNet.Tests/`: executable regression suite and simulated HTTP responses.
- `scripts/`: build, packaging, installation, and uninstallation.
- `docs/VALIDATION.md`: verified behavior and remaining manual acceptance checks.

Keep generated binaries in ignored `artifacts/` and local SDK tooling in ignored `.tools/`. The tray icon is generated in code; there is no separate asset pipeline.

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
