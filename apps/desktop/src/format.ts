import type { LogPage, LogSummary, SnapshotState, StatusView } from "./types";

const STAGE_LABELS: Record<string, string> = {
  Unknown: "未知",
  Status: "状态检查",
  GatewayRedirect: "网关跳转",
  HomePage: "首页兜底",
  ClientIpv4: "客户端地址",
  Configuration: "配置读取",
  AuthenticationMode: "认证模式",
  Submission: "提交",
  Complete: "完成",
};

const FAILURE_LABELS: Record<string, string> = {
  None: "无",
  InvalidTarget: "目标无效",
  AlreadyAuthenticated: "已认证",
  StatusUnknown: "状态未知",
  HttpStatus: "HTTP 状态异常",
  MissingClientIpv4: "缺少客户端地址",
  MalformedConfiguration: "配置结构异常",
  MissingFlag: "缺少配置字段",
  InvalidFlagType: "配置字段类型异常",
  UnsupportedFlag: "不支持的配置字段",
  DuplicateFlag: "配置字段重复",
  InvalidResponse: "响应无法解析",
  Timeout: "超时",
  Transport: "传输失败",
  Tls: "TLS 失败",
  CredentialsRejected: "凭据被拒绝",
  InteractionRequired: "需要人工交互",
  LoginRejected: "登录被拒绝",
  MissingGatewayParameters: "缺少网关参数",
  BindingRejected: "设备绑定被拒绝",
  SafetyPersistence: "安全状态写入失败",
};

const AUTH_STATE_LABELS: Record<string, string> = {
  Ready: "就绪",
  SubmissionPending: "提交结果待确认",
  RetryCoolingDown: "退避中",
  BindingRejected: "设备绑定拒绝",
  CredentialsRejected: "凭据被拒绝",
  InteractionRequired: "需要人工确认",
  SafetyBlocked: "安全暂停",
  Unknown: "不可确认",
};

const ACTION_LABELS: Record<string, string> = {
  None: "无",
  ReconnectWifi: "重连 Wi-Fi",
  Authenticate: "认证",
  DisconnectWifi: "主动断线",
  Unknown: "不可确认",
};

const SUBMISSION_BUDGET_LABELS: Record<string, string> = {
  Unused: "尚未使用",
  Available: "仍可用",
  NearLimit: "仅剩一次",
  Exhausted: "已用尽",
  Reset: "已重置",
  Unknown: "不可确认",
};

const SUBMISSION_RETRY_LABELS: Record<string, string> = {
  Authorized: "允许提交",
  Waiting: "等待窗口",
  WaitClock: "时间不可信",
  Exhausted: "额度耗尽",
  InFlight: "执行中",
  Concluded: "已结束",
  InternetOnline: "外网在线",
  InvalidTime: "时间无效",
};

const UU_LABELS: Record<string, string> = {
  running: "服务运行中（会话待实测）",
  stopped: "服务未运行",
  unknown: "状态未知",
  notProbed: "未核验",
};

const SERVICE_STATE_LABELS: Record<string, string> = {
  NotInstalled: "未安装",
  Stopped: "已停止",
  StartPending: "正在启动",
  StopPending: "正在停止",
  Running: "正在运行",
  ContinuePending: "正在恢复",
  PausePending: "正在暂停",
  Paused: "已暂停",
  AccessDenied: "无查询权限",
  Unavailable: "状态不可读取",
};

export const SNAPSHOT_STATE_LABELS: Record<SnapshotState, string> = {
  ok: "实时",
  stale: "已过期",
  missing: "尚无状态",
  corrupt: "状态无法解析",
  denied: "无权读取",
};

export function snapshotGuidance(state: SnapshotState): string {
  switch (state) {
    case "missing":
      return "status.json 不存在。请确认 AutoNet 服务已安装，并至少完成过一个检测轮次。";
    case "corrupt":
      return "状态文件存在但字段缺失或无法解析。预览不会猜测缺失字段；请等待服务重写或查看正式日志。";
    case "denied":
      return "当前普通用户没有读取状态文件的权限。预览不会请求提权。";
    case "stale":
      return "状态文件有一段时间未更新，下面的内容可能已经过时。请确认服务仍在运行。";
    default:
      return "状态由后台服务写入，预览只读展示。";
  }
}

function label(map: Record<string, string>, value: string | null | undefined, fallback: string): string {
  if (!value) return fallback;
  return map[value] ?? fallback;
}

export const stageLabel = (value: string | null | undefined) => label(STAGE_LABELS, value, "不可确认");
export const failureLabel = (value: string | null | undefined) =>
  value === "None" ? "无" : label(FAILURE_LABELS, value, "不可确认");
export const authStateLabel = (value: string | null | undefined) => label(AUTH_STATE_LABELS, value, "不可确认");
export const actionLabel = (value: string | null | undefined) => label(ACTION_LABELS, value, "不可确认");
export const submissionBudgetLabel = (value: string | null | undefined) =>
  value ? label(SUBMISSION_BUDGET_LABELS, value, "不可确认") : "不可确认";
export const submissionRetryLabel = (value: string | null | undefined) =>
  value ? label(SUBMISSION_RETRY_LABELS, value, "不可确认") : "不可确认";
export const uuLabel = (value: string | null | undefined) => label(UU_LABELS, value, "不可确认");
export const serviceStateLabel = (value: string | null | undefined) =>
  label(SERVICE_STATE_LABELS, value, "不可确认");

export function formatTime(millis: number | null | undefined): string {
  if (millis === null || millis === undefined) return "—";
  const date = new Date(millis);
  if (Number.isNaN(date.getTime())) return "—";
  return date.toLocaleString("zh-CN", {
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  });
}

export function formatClock(millis: number | null | undefined): string {
  if (millis === null || millis === undefined) return "—";
  const date = new Date(millis);
  if (Number.isNaN(date.getTime())) return "—";
  return date.toLocaleTimeString("zh-CN", { hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
}

export function formatSeconds(value: number | null | undefined): string {
  return value === null || value === undefined ? "—" : `${value.toFixed(1)} 秒`;
}

export function statusTone(state: string, direct: string | null | undefined): "ok" | "warn" | "bad" | "neutral" {
  if (!direct) return "neutral";
  if (direct === "外网可达") return "ok";
  if (state === "检测异常" || state === "网卡未就绪") return "bad";
  return "warn";
}

export function logStatusText(entry: LogSummary): string {
  return `${entry.wifi} · ${entry.portal} · ${entry.direct}`;
}

export function logScopeNote(page: LogPage): string {
  if (page.failure) return "日志读取失败，列表为空。";
  if (!page.date) return "还没有可读取的日志。";
  return `显示 ${page.entries.length} 条摘要${page.complete ? "（已覆盖当日全部记录）" : "（仅当日最新部分）"}，按时间最新在前。`;
}

export function logGapNote(page: LogPage): string {
  return page.skipped > 0 ? `读取窗口内有 ${page.skipped} 条损坏记录被跳过；这不代表其余记录完整。` : "";
}

export function submissionSummary(view: StatusView): string {
  if (view.submissionAttempts === null || view.submissionAttemptMax === null) return "不可确认";
  return `${view.submissionAttempts} / ${view.submissionAttemptMax} 次`;
}
