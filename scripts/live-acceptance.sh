#!/usr/bin/env bash
#
# AutoNet 现场验收向导 — 由 /wizard 生成。
#
# 用法（Git Bash）：
#   bash scripts/live-acceptance.sh
#
# 本仓库其余脚本一律走 pwsh.exe；这个向导是人工验收工具，交互层用 bash，
# 实际诊断与安装仍调用 pwsh.exe / Windows 程序。
#
# 结果写入 artifacts/validation/live-acceptance/answers.env（gitignored），
# 结束后把该文件交给代理写回 docs/VALIDATION.md。
#
# 安全边界：
#   - 不读取、不回显账号密码；--diagnose / --diagnose-portal 不会提交凭据。
#   - 安装、断网、真实凭据提交、覆盖/回滚等破坏性阶段必须输入 AUTHORIZE。
#   - 跳过或失败的阶段只记录，不改变系统状态。
#   - 不要在远程连接（RDP/SSH）下执行安装、断网或回滚阶段。

set -euo pipefail

# ──────────────────────────────────────────────────────────────────────────
# Wizard library — delightful, consistent UX. Identical across every wizard.
# ──────────────────────────────────────────────────────────────────────────

if [[ -t 1 ]] && command -v tput >/dev/null 2>&1 && [[ "$(tput colors 2>/dev/null || echo 0)" -ge 8 ]]; then
  BOLD=$(tput bold); DIM=$(tput dim); RESET=$(tput sgr0)
  BLUE=$(tput setaf 4); GREEN=$(tput setaf 2); YELLOW=$(tput setaf 3); RED=$(tput setaf 1)
else
  BOLD=""; DIM=""; RESET=""; BLUE=""; GREEN=""; YELLOW=""; RED=""
fi

# Author sets these two at the top of the stages section.
TOTAL_STAGES=0
TOTAL_MINUTES=0

_STAGE_INDEX=0
_MINUTES_ELAPSED=0
ENV_FILE="${ENV_FILE:-.env}"
WRITTEN_ENV=()    # KEYs written to ENV_FILE this run
WRITTEN_SECRET=() # secret NAMEs set this run
SKIPPED=()        # things we couldn't do (e.g. gh missing)

# _clear — wipe the terminal so only the current step is on screen. No-op when
# output isn't a terminal, so piped logs stay readable.
_clear() {
  [[ -t 1 ]] || return 0
  if command -v tput >/dev/null 2>&1; then tput clear; else printf '\033[2J\033[3J\033[H'; fi
}

# banner "Title" — opening frame: what this wizard does and how long it takes.
banner() {
  _clear
  printf '\n%s%s  %s%s\n' "$BOLD" "$BLUE" "$1" "$RESET"
  printf '%s  %s stages · about %s minutes%s\n\n' \
    "$DIM" "$TOTAL_STAGES" "$TOTAL_MINUTES" "$RESET"
  printf '%s  You drive the browser; this wizard tells you exactly what to do and\n' "$DIM"
  printf '  captures the values you copy back. Stop any time with Ctrl-C and re-run\n'
  printf '  later — it remembers values already saved.%s\n' "$RESET"
  pause "Ready to start?"
}

# stage "Name" <minutes> — clear the screen, then announce a stage and show
# progress + time remaining. Clearing keeps only the current step on screen.
stage() {
  _clear
  _STAGE_INDEX=$((_STAGE_INDEX + 1))
  local remaining=$((TOTAL_MINUTES - _MINUTES_ELAPSED))
  (( remaining < 0 )) && remaining=0
  _MINUTES_ELAPSED=$((_MINUTES_ELAPSED + ${2:-0}))
  printf '\n%s%s▸ Stage %s/%s · %s%s  %s(~%s min left)%s\n' \
    "$BOLD" "$BLUE" "$_STAGE_INDEX" "$TOTAL_STAGES" "$1" "$RESET" "$DIM" "$remaining" "$RESET"
}

# say "..." — a plain instruction line.
say()  { printf '  %s\n' "$1"; }
# step "..." — a numbered-feeling action the human takes in the browser.
step() { printf '  %s•%s %s\n' "$BLUE" "$RESET" "$1"; }
note() { printf '  %s%s%s\n' "$DIM" "$1" "$RESET"; }
warn() { printf '  %s⚠ %s%s\n' "$YELLOW" "$1" "$RESET"; }

# open_url URL — open in the human's browser, cross-platform incl. WSL.
open_url() {
  local url="$1"
  printf '  %s↗ opening%s %s\n' "$GREEN" "$RESET" "$url"
  { if   command -v wslview     >/dev/null 2>&1; then wslview "$url"
    elif command -v explorer.exe >/dev/null 2>&1; then explorer.exe "$url"
    elif command -v xdg-open    >/dev/null 2>&1; then xdg-open "$url"
    elif command -v open        >/dev/null 2>&1; then open "$url"
    else warn "couldn't open a browser — visit it manually: $url"; fi
  } >/dev/null 2>&1 || warn "couldn't open a browser — visit it manually: $url"
}

# pause "msg" — wait for the human to confirm they've done the manual part.
pause() {
  printf '  %s%s%s ' "$DIM" "${1:-Press Enter to continue}" "$RESET"
  read -r _ || true
}

# confirm "question" — y/N gate; returns success on yes.
confirm() {
  local reply=""
  printf '  %s? %s [y/N] ' "$YELLOW" "$1"
  read -r reply || true
  [[ "$reply" =~ ^[Yy] ]]
}

# _existing KEY — current value of KEY in ENV_FILE, if any.
_existing() {
  [[ -f "$ENV_FILE" ]] || return 1
  local line; line=$(grep -E "^${1}=" "$ENV_FILE" | tail -n1) || return 1
  printf '%s' "${line#*=}"
}

# ask KEY "Prompt" — read a value into $KEY. Offers the existing .env value as
# a default on re-runs (Enter keeps it). Visible input (non-secret).
ask() {
  local key="$1" prompt="$2" current input
  current=$(_existing "$key" || true)
  if [[ -n "$current" ]]; then
    printf '  %s%s%s %s[Enter keeps current]%s ' "$BOLD" "$prompt" "$RESET" "$DIM" "$RESET"
  else
    printf '  %s%s%s ' "$BOLD" "$prompt" "$RESET"
  fi
  read -r input || true
  [[ -z "$input" && -n "$current" ]] && input="$current"
  printf -v "$key" '%s' "$input"
}

# ask_secret KEY "Prompt" — like ask, but input is hidden.
ask_secret() {
  local key="$1" prompt="$2" current input
  current=$(_existing "$key" || true)
  if [[ -n "$current" ]]; then
    printf '  %s%s%s %s[Enter keeps current]%s ' "$BOLD" "$prompt" "$RESET" "$DIM" "$RESET"
  else
    printf '  %s%s%s ' "$BOLD" "$prompt" "$RESET"
  fi
  read -rs input || true
  printf '\n'
  [[ -z "$input" && -n "$current" ]] && input="$current"
  printf -v "$key" '%s' "$input"
}

# write_env KEY VALUE — upsert KEY=VALUE into ENV_FILE (creates it; replaces
# any existing line). Idempotent.
write_env() {
  local key="$1" value="$2" tmp
  touch "$ENV_FILE"
  tmp=$(mktemp)
  grep -vE "^${key}=" "$ENV_FILE" > "$tmp" || true
  printf '%s=%s\n' "$key" "$value" >> "$tmp"
  mv "$tmp" "$ENV_FILE"
  WRITTEN_ENV+=("$key")
  printf '  %s✓ wrote%s %s → %s\n' "$GREEN" "$RESET" "$key" "$ENV_FILE"
}

# set_secret NAME VALUE — set a GitHub Actions repo secret via gh. Falls back
# to a warning (and records it) if gh is unavailable or unauthenticated.
set_secret() {
  local name="$1" value="$2"
  if command -v gh >/dev/null 2>&1 && gh auth status >/dev/null 2>&1; then
    if printf '%s' "$value" | gh secret set "$name" >/dev/null 2>&1; then
      WRITTEN_SECRET+=("$name")
      printf '  %s✓ set%s GitHub secret %s\n' "$GREEN" "$RESET" "$name"
      return
    fi
  fi
  SKIPPED+=("GitHub secret $name (set it manually: gh secret set $name)")
  warn "skipped GitHub secret $name — gh not ready; set it later"
}

# set_var NAME VALUE — set a GitHub Actions repo variable (non-secret).
set_var() {
  local name="$1" value="$2"
  if command -v gh >/dev/null 2>&1 && gh auth status >/dev/null 2>&1; then
    if gh variable set "$name" --body "$value" >/dev/null 2>&1; then
      printf '  %s✓ set%s GitHub variable %s\n' "$GREEN" "$RESET" "$name"
      return
    fi
  fi
  SKIPPED+=("GitHub variable $name")
  warn "skipped GitHub variable $name — gh not ready"
}

# finish — clear, then a closing summary of everything configured.
finish() {
  _clear
  printf '\n%s%s  ✓ Setup complete%s\n' "$BOLD" "$GREEN" "$RESET"
  (( ${#WRITTEN_ENV[@]} ))    && note "wrote ${#WRITTEN_ENV[@]} value(s) to $ENV_FILE: ${WRITTEN_ENV[*]}"
  (( ${#WRITTEN_SECRET[@]} )) && note "set ${#WRITTEN_SECRET[@]} GitHub secret(s): ${WRITTEN_SECRET[*]}"
  if (( ${#SKIPPED[@]} )); then
    printf '\n'; warn "still to do by hand:"
    for s in "${SKIPPED[@]}"; do note "  - $s"; done
  fi
  printf '\n'
}

# ──────────────────────────────────────────────────────────────────────────
# STAGES — author this section. One stage() per step the human takes.
# ──────────────────────────────────────────────────────────────────────────

TOTAL_STAGES=12
TOTAL_MINUTES=102

REPO_ROOT=$(git rev-parse --show-toplevel 2>/dev/null || true)
if [[ -z "$REPO_ROOT" ]]; then
  printf '必须在 auto-net 仓库内运行：找不到 git 工作树根目录。\n' >&2
  exit 1
fi
cd "$REPO_ROOT"

# 库在顶部已把 ENV_FILE 默认成 .env；这里必须无条件覆盖，否则结果会落到
# 仓库根目录的 .env。需要自定义路径时用 AUTONET_ACCEPTANCE_ENV。
ENV_FILE="${AUTONET_ACCEPTANCE_ENV:-artifacts/validation/live-acceptance/answers.env}"
OUT_DIR="$(dirname "$ENV_FILE")"
mkdir -p "$OUT_DIR"

SERVICE_EXE="${SERVICE_EXE:-C:/Program Files/AutoNet/service/AutoNet.Service.exe}"
AUTHCHANNEL_EXE="${AUTHCHANNEL_EXE:-C:/Program Files/AutoNet/authchannel/AutoNet.AuthChannel.exe}"
INSTALLER="${INSTALLER:-artifacts/AutoNet-win-x64/install.ps1}"
STATUS_JSON="${STATUS_JSON:-C:/ProgramData/AutoNet/status.json}"

_ps() { pwsh.exe -NoLogo -NoProfile -NonInteractive -Command "$1"; }

_is_admin() {
  _ps 'if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { exit 0 } else { exit 1 }' >/dev/null 2>&1
}

# capture NAME CMD... — run CMD, save combined output to OUT_DIR/NAME.txt, and
# leave the exit code in $CAPTURE_CODE. Never aborts the wizard.
capture() {
  local name="$1"; shift
  local out="$OUT_DIR/$name.txt"
  set +e
  "$@" >"$out" 2>&1
  CAPTURE_CODE=$?
  set -e
  say "输出：$out（退出码 $CAPTURE_CODE）"
  return 0
}

# verdict KEY "question" — record PASS / FAIL / NA (default NA).
verdict() {
  local key="$1" q="$2" v
  ask "$key" "$q [PASS/FAIL/NA]"
  v=$(printf '%s' "${!key}" | tr '[:lower:]' '[:upper:]')
  case "$v" in
    PASS|FAIL|NA) ;;
    *) warn "未识别为 PASS/FAIL/NA，记为 NA"; v=NA ;;
  esac
  write_env "$key" "$v"
}

# authorize "reason" — destructive-stage gate. Requires the literal token.
authorize() {
  local reason="$1"
  warn "$reason"
  ask AUTHORIZATION_TOKEN "输入 AUTHORIZE 明确授权本次操作（其他输入将跳过）:"
  [[ "${AUTHORIZATION_TOKEN:-}" == "AUTHORIZE" ]]
}

# online_now — true when status.json reports 外网可达. Reads the JSON through
# pwsh and compares code points, so the check does not depend on the console
# code page or on System.Text.Json escaping non-ASCII.
online_now() {
  local out
  out=$(_ps "\$s = Get-Content -LiteralPath '$STATUS_JSON' -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json; if (\$s) { [int[]][char[]]\$s.Direct -join ',' }" 2>/dev/null | tr -d '\r')
  [[ "$out" == "22806,32593,21487,20542" ]]
}

# data_fingerprint — SHA256 of the six durable private records, one per line.
# Hashes only; never file contents.
data_fingerprint() {
  _ps "\$p='C:/ProgramData/AutoNet/private'; foreach (\$n in @('settings.json','credentials.dpapi','auth-block.txt','submission-budget.json','submission-restart.json','auth-release-audit.jsonl')) { \$f=Join-Path \$p \$n; if (Test-Path -LiteralPath \$f) { '{0} {1}' -f \$n,(Get-FileHash -LiteralPath \$f -Algorithm SHA256).Hash } else { '{0} MISSING' -f \$n } }" 2>/dev/null | tr -d '\r'
}

banner "AutoNet 现场验收"

# ── 阶段 1：前置检查 ─────────────────────────────────────────────────────
stage "前置检查" 5
say "本向导只驱动人工步骤并记录结果；只读诊断不会提交凭据。"
if ! command -v pwsh.exe >/dev/null 2>&1; then
  warn "未找到 pwsh.exe。请从 PowerShell 7 环境打开 Git Bash 后重试。"
  exit 1
fi
if _is_admin; then
  write_env PRECHECK_ADMIN PASS
  note "当前会话有管理员权限。"
else
  write_env PRECHECK_ADMIN FAIL
  warn "当前会话不是管理员。安装、诊断与 ACL 检查需要管理员权限。"
  say "请以管理员身份重新打开终端，或先完成只读阶段后再补做。"
  pause "按 Enter 退出"
  exit 1
fi
if [[ -n "${SSH_CONNECTION:-}" || -n "${RDP_SESSION:-}" ]]; then
  warn "检测到远程会话标记。不要在远程连接下执行安装、断网或回滚。"
  if confirm "确认当前是本地物理会话，继续？"; then
    write_env PRECHECK_REMOTE PASS
  else
    write_env PRECHECK_REMOTE FAIL
    exit 1
  fi
else
  write_env PRECHECK_REMOTE PASS
fi
say "备份受限存储（只复制 DPAPI 密文，不读取内容）："
say "  Copy-Item -Recurse \"C:\\ProgramData\\AutoNet\\private\" \"\$env:USERPROFILE\\AutoNet-private-backup-$(date +%Y%m%d-%H%M%S)\""
pause "备份完成后按 Enter"
if confirm "备份已完成？"; then write_env PRECHECK_BACKUP PASS; else write_env PRECHECK_BACKUP FAIL; fi
if [[ -f "$INSTALLER" ]]; then
  note "发布包就绪：$INSTALLER"
  write_env PRECHECK_BUNDLE PASS
else
  warn "未找到发布包：$INSTALLER"
  write_env PRECHECK_BUNDLE MISSING
fi

# ── 阶段 2：安装新包 ─────────────────────────────────────────────────────
stage "安装新包" 10
if [[ ! -f "$INSTALLER" ]]; then
  warn "缺少发布包，跳过安装；后续阶段基于现有安装。"
  SKIPPED+=("安装新包（缺少发布包）")
  write_env INSTALL_RESULT SKIPPED
else
  if authorize "安装会停止并替换当前 AutoNet 程序；配置与凭据保留。"; then
    capture install pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "$INSTALLER" -Quiet
    write_env INSTALL_EXIT "$CAPTURE_CODE"
    if [[ "$CAPTURE_CODE" -eq 0 ]]; then write_env INSTALL_RESULT PASS; else write_env INSTALL_RESULT FAIL; fi
  else
    SKIPPED+=("安装新包（未授权）")
    write_env INSTALL_RESULT SKIPPED
  fi
fi
say "核对安装结果："
capture service-status pwsh.exe -NoLogo -NoProfile -NonInteractive -Command "(Get-Service -Name AutoNet -ErrorAction SilentlyContinue).Status"
SERVICE_STATUS=$(tr -d '\r' <"$OUT_DIR/service-status.txt" 2>/dev/null || true)
write_env SERVICE_STATUS "${SERVICE_STATUS:-UNKNOWN}"
capture service-version pwsh.exe -NoLogo -NoProfile -NonInteractive -Command "if (Test-Path -LiteralPath '$SERVICE_EXE') { (Get-Item -LiteralPath '$SERVICE_EXE').VersionInfo.ProductVersion } else { 'MISSING' }"
SERVICE_VERSION=$(tr -d '\r' <"$OUT_DIR/service-version.txt" 2>/dev/null || true)
write_env SERVICE_VERSION "${SERVICE_VERSION:-MISSING}"
verdict INSTALL_VERIFIED "服务 Running、版本与发布包一致、辅助进程存在"

# ── 阶段 3：只读诊断 ─────────────────────────────────────────────────────
stage "只读诊断" 5
say "运行 --diagnose 与 --diagnose-portal；两者都不会提交凭据。"
capture diagnose "$SERVICE_EXE" --diagnose
write_env DIAGNOSE_EXIT "$CAPTURE_CODE"
capture diagnose-portal "$SERVICE_EXE" --diagnose-portal
write_env DIAGNOSE_PORTAL_EXIT "$CAPTURE_CODE"
note "先看两个输出文件，再判断是否符合预期。"
verdict DIAGNOSE_RESULT "只读诊断（--diagnose）结果符合预期"
verdict PORTAL_DIAGNOSE_RESULT "门户前置条件（--diagnose-portal）符合预期"

# ── 阶段 4：真实断网恢复 ─────────────────────────────────────────────────
stage "真实断网恢复" 15
warn "本阶段会主动断开校园 Wi-Fi，可能中断远程会话。"
if authorize "断网测试会中断当前网络。"; then
  if online_now; then note "当前状态：外网可达。"; else warn "当前不是外网可达；建议先恢复在线再开始计时。"; fi
  step "断开校园 Wi-Fi（Windows 网络面板或 netsh wlan disconnect）。"
  pause "断开后按 Enter 开始计时"
  start=$SECONDS
  say "等待服务自动重连与重新认证（每 2 秒检查一次，最多 10 分钟）…"
  recovered=no
  for _ in $(seq 1 300); do
    if online_now; then recovered=yes; break; fi
    sleep 2
  done
  elapsed=$((SECONDS - start))
  write_env OUTAGE_RECOVERY_SECONDS "$elapsed"
  if [[ "$recovered" == yes ]]; then
    write_env OUTAGE_RECOVERY_RESULT PASS
    note "已恢复在线，用时约 ${elapsed} 秒。"
  else
    write_env OUTAGE_RECOVERY_RESULT FAIL
    warn "10 分钟内未观察到外网可达；请记录当时状态与日志。"
  fi
  ask OUTAGE_NOTES "备注（是否触发主动断线、认证次数、异常）："
  write_env OUTAGE_NOTES "$OUTAGE_NOTES"
else
  SKIPPED+=("真实断网恢复（未授权）")
  write_env OUTAGE_RECOVERY_RESULT SKIPPED
fi

# ── 阶段 5：真实门户提交 ─────────────────────────────────────────────────
stage "真实门户提交" 10
warn "本阶段会真实提交凭据，消耗共享提交额度，可能触发门户风控。"
if authorize "真实凭据提交会发出账号密码。"; then
  step "在设置页保存真实校园网账号与密码。"
  step "确认概览页显示“等待重试”或门户需要登录。"
  step "观察结果：成功 / 结果未知 / 明确拒绝，并记录门户文案。"
  pause "观察完成后按 Enter"
  verdict AUTH_RESULT "真实门户提交结果符合预期"
  ask AUTH_NOTES "备注（门户文案、耗时、是否进入结果未知）："
  write_env AUTH_NOTES "$AUTH_NOTES"
else
  SKIPPED+=("真实门户提交（未授权）")
  write_env AUTH_RESULT SKIPPED
fi

# ── 阶段 6：认证页真实承载 ───────────────────────────────────────────────
stage "认证页真实承载" 10
step "打开托盘 →「上网认证」页。"
verdict AUTH_PAGE_LOAD "真实门户页面在 WebView2 中加载成功（无 TLS 证书错误）"
verdict AUTH_PAGE_REDIRECT "门户重定向与子资源经允许列表代理正常加载"
verdict AUTH_PAGE_WINDOW "新窗口与 Service Worker 行为符合预期"
verdict AUTH_PAGE_802 "802 端口的 loadConfig/login 经代理可达"
verdict AUTH_PAGE_RESULT "认证页真实承载整体通过"

# ── 阶段 7：内嵌通道互斥与手动接管 ───────────────────────────────────────
stage "内嵌通道互斥与手动接管" 10
step "在后台内嵌通道会话进行中打开「上网认证」页。"
verdict CHANNEL_MUTEX "认证页显示“内嵌通道会话进行中”，未另起浏览器"
step "会话结束后确认页面恢复一次只读附着。"
verdict CHANNEL_REATTACH "会话结束后恢复只读附着且不重放页面操作"
step "观察虚拟指针的位置与可见性（含系统多 DPI 缩放）。"
verdict POINTER_RESULT "虚拟指针位置与可见性符合预期"
step "在页面上手动输入，确认进入“正在手动操作”且自动填表暂停。"
verdict TAKEOVER_RESULT "手动接管与 15 秒提示清除符合预期"
step "确认人工观察窗口在联网后提前收尾并写入最终结果。"
verdict HUMAN_WAIT_RESULT "人工观察与提前收尾符合预期"

# ── 阶段 8：跨管理员账户 UAC ─────────────────────────────────────────────
stage "跨管理员账户 UAC" 5
note "本机内置管理员已禁用、当前账户为 Medium 拆分令牌；跨管理员账户路径可能无法构造。"
verdict UAC_SETTINGS "跨管理员账户：设置保存交接"
verdict UAC_UNBLOCK "跨管理员账户：解除认证暂停"
verdict UAC_EXPORT "跨管理员账户：诊断包导出"
verdict UAC_CHANNEL "跨管理员账户：内嵌通道启动"
verdict UAC_RESULT "跨管理员账户 UAC 整体结果（不可构造时记 NA）"

# ── 阶段 9：WebRTC/STUN 走向 ─────────────────────────────────────────────
stage "WebRTC/STUN 走向" 10
step "用 pktmon 或 Wireshark 抓取 WebView2 进程的 UDP 流量。"
step "打开认证页并让页面运行约 1 分钟。"
step "检查是否存在绕过环回代理的 STUN/UDP 连接。"
note "PASS 表示全部流量经代理；FAIL 表示发现非代理直连。"
verdict WEBRTC_RESULT "WebRTC/STUN 走向符合预期"
ask WEBRTC_NOTES "备注（进程、端口、目标地址）："
write_env WEBRTC_NOTES "$WEBRTC_NOTES"

# ── 阶段 10：覆盖安装与失败回滚 ──────────────────────────────────────────
stage "覆盖安装与失败回滚" 15
warn "不要在远程连接下执行失败回滚测试。"
BEFORE_FP=$(data_fingerprint)
if [[ -f "$INSTALLER" ]] && authorize "覆盖安装会替换程序文件；配置与凭据保留。"; then
  capture reinstall pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "$INSTALLER" -Quiet
  write_env REINSTALL_EXIT "$CAPTURE_CODE"
  AFTER_FP=$(data_fingerprint)
  if [[ "$BEFORE_FP" == "$AFTER_FP" ]]; then write_env DATA_PRESERVED PASS; else write_env DATA_PRESERVED FAIL; fi
  note "服务在安装重启期间可能自行更新提交额度/认证状态；若 DATA_PRESERVED=FAIL，先确认是安装改写还是运行期写入。"
  verdict REINSTALL_RESULT "覆盖安装结果符合预期"
else
  SKIPPED+=("覆盖安装（未授权或缺少发布包）")
  write_env REINSTALL_RESULT SKIPPED
fi
note "失败回滚（坏包）需要单独授权的会话；不要在远程连接下执行。"
verdict ROLLBACK_RESULT "失败回滚已另行验证（未验证记 NA）"

# ── 阶段 11：存储故障与 ACL ──────────────────────────────────────────────
stage "存储故障与 ACL" 5
capture icacls icacls.exe "C:\\ProgramData\\AutoNet\\private"
step "确认 private 目录无 Users 条目、受保护且归管理员。"
step "确认非管理员无法读取 private 下的记录。"
verdict ACL_RESULT "ACL 与受限读取行为符合预期"

# ── 阶段 12：汇总 ────────────────────────────────────────────────────────
stage "汇总" 2
write_env RUN_AT "$(date -Iseconds)"
write_env RUN_HOST "$(hostname)"
write_env REPO_COMMIT "$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
finish
say "结果文件：$ENV_FILE"
say "下一步：把该文件交给代理，写回 docs/VALIDATION.md 与 #38/#39/#40/#27。"
