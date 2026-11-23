//! 跨语言只读契约（P0/P1）。
//!
//! 这里只放纯数据与投影规则：状态快照的脱敏视图、日志摘要白名单、时间解析与受控词汇。
//! 不读文件、不访问网络、不持有凭据；任何新增字段都必须同时加入白名单与本文件末尾的测试。

#![forbid(unsafe_code)]

use serde::de::{Deserializer, IgnoredAny, MapAccess, Visitor};
use serde::Serialize;
use serde_json::{Map, Value};
use std::fmt;

/// 协议版本。桌面预览与（未来）服务端共享；不兼容时只允许只读展示。
pub const CONTRACT_VERSION: u32 = 1;
/// 预览应用的产品名；正式托盘仍使用 `AutoNet.Tray.exe`。
pub const PREVIEW_PRODUCT: &str = "AutoNet Preview";
/// 预览应用的 Tauri 标识；单实例键与正式托盘不同。
pub const PREVIEW_IDENTIFIER: &str = "com.autonet.preview";
/// 正式 SCM 服务名，只用于只读查询。
pub const SERVICE_NAME: &str = "AutoNet";
/// 与 C# `NetworkDisplayMap.FreshnessWindow` 一致。
pub const STATUS_FRESH_MILLIS: i64 = 15_000;
/// C# `DateTimeOffset.MinValue` 的 Unix 毫秒；日志读取把它视为不可渲染。
pub const DEFAULT_TIME_MILLIS: i64 = -62_135_596_800_000;
/// 允许的时钟正偏差；超过即视为不可信。
pub const STATUS_FUTURE_TOLERANCE_MILLIS: i64 = 5_000;

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ContractInfo {
    pub protocol_version: u32,
    pub product: String,
    pub read_only: bool,
    pub app_version: String,
}

impl ContractInfo {
    pub fn current(app_version: impl Into<String>) -> Self {
        Self {
            protocol_version: CONTRACT_VERSION,
            product: PREVIEW_PRODUCT.to_string(),
            read_only: true,
            app_version: app_version.into(),
        }
    }
}

/// 状态文件的读取结论；缺失、损坏、拒绝访问和过期必须显式区分（ADR-0023）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum SnapshotState {
    Ok,
    Missing,
    Corrupt,
    Denied,
    Stale,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SnapshotEnvelope {
    pub state: SnapshotState,
    pub snapshot: Option<StatusView>,
    /// 服务写入状态的时间；缺失或不可解析时为 null。
    pub updated_at_millis: Option<i64>,
    /// 读取发生的时间（调用方提供），便于前端判断展示年龄。
    pub read_at_millis: i64,
    /// 受控错误码，不含异常原文。
    pub reason: Option<String>,
}

/// `status.json` 的脱敏投影。刻意省略账号、网卡、配置名、代理/UU 地址、自由文本说明与最近异常。
#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct StatusView {
    pub state: String,
    pub wifi: String,
    pub direct: String,
    pub portal: String,
    pub tunnel: String,
    pub uu: String,
    pub enabled: bool,
    pub attempts: i32,
    pub disconnect_attempts: i32,
    pub disconnect_budget_max: i32,
    pub disconnect_cooldown_seconds: i32,
    pub disconnect_reset_after_seconds: i32,
    pub disconnect_request_unconfirmed: bool,
    pub outage_since_millis: Option<i64>,
    pub next_attempt_millis: Option<i64>,
    pub next_attempt_action: String,
    pub last_recovery_seconds: Option<f64>,
    pub last_recovery_at_millis: Option<i64>,
    pub cycle_milliseconds: Option<f64>,
    pub slowest_stage: String,
    pub slowest_stage_milliseconds: Option<f64>,
    pub updated_at_millis: i64,
    pub authentication_stage: String,
    pub authentication_failure: String,
    pub authentication_persistent_state: String,
    pub authentication_http_status: Option<i32>,
    pub credential_request_started: bool,
    pub credentials_blocked: bool,
    pub credentials_available: Option<bool>,
    pub settings_available: Option<bool>,
    pub submission_budget_code: String,
    pub submission_retry_code: String,
    pub submission_attempts: Option<i32>,
    pub submission_attempt_max: Option<i32>,
    pub submission_budget_exhausted: Option<bool>,
    pub submission_next_possible_at_millis: Option<i64>,
    pub submission_cooldown_seconds: i32,
    pub wifi_link_code: String,
    pub portal_layout_suspected: bool,
    pub notification_code: Option<String>,
    pub notification_at_millis: Option<i64>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StatusParseError {
    InvalidJson,
    MissingRequired,
    InvalidTimestamp,
}

impl StatusParseError {
    pub fn code(self) -> &'static str {
        match self {
            Self::InvalidJson => "InvalidJson",
            Self::MissingRequired => "MissingRequiredFields",
            Self::InvalidTimestamp => "InvalidTimestamp",
        }
    }
}

impl StatusView {
    /// 解析并投影 `status.json`。与 C# `StatusSnapshotReader.IsValid` 一样，外部时间与
    /// `Enabled` 必须显式存在；缺字段不是“使用默认值”，而是不可确认。
    pub fn from_json(text: &str) -> Result<Self, StatusParseError> {
        let value: Value = serde_json::from_str(text).map_err(|_| StatusParseError::InvalidJson)?;
        let Value::Object(map) = value else {
            return Err(StatusParseError::InvalidJson);
        };
        if get(&map, "UpdatedAt").is_none() || get(&map, "Enabled").is_none() {
            return Err(StatusParseError::MissingRequired);
        }
        let updated_at_millis = required_time(&map, "UpdatedAt")?;
        let state = required_text(&map, "State")?;
        let wifi = required_text(&map, "Wifi")?;
        let direct = required_text(&map, "Direct")?;
        let portal = required_text(&map, "Portal")?;
        let tunnel = required_text(&map, "Tunnel")?;
        let uu = required_text(&map, "Uu")?;
        let authentication_stage = required_text(&map, "AuthenticationStage")?;
        let authentication_failure = required_text(&map, "AuthenticationFailure")?;
        let authentication_persistent_state = required_text(&map, "AuthenticationPersistentState")?;
        let notification = get(&map, "Notification").filter(|value| value.is_object());

        Ok(Self {
            state: controlled(Some(&state), KNOWN_STATES, "未知状态"),
            wifi: controlled(Some(&wifi), ALLOWED_WIFI, "未知"),
            direct: controlled(Some(&direct), ALLOWED_DIRECT, "未知"),
            portal: controlled(Some(&portal), ALLOWED_PORTAL, "未知"),
            tunnel: controlled(Some(&tunnel), ALLOWED_TUNNEL, "未知"),
            uu: uu_class(Some(&uu)),
            enabled: boolean(&map, "Enabled"),
            attempts: integer(&map, "Attempts").unwrap_or(0),
            disconnect_attempts: integer(&map, "DisconnectAttempts").unwrap_or(0),
            disconnect_budget_max: integer(&map, "DisconnectBudgetMax").unwrap_or(3),
            disconnect_cooldown_seconds: integer(&map, "DisconnectCooldownSeconds").unwrap_or(120),
            disconnect_reset_after_seconds: integer(&map, "DisconnectResetAfterSeconds")
                .unwrap_or(60),
            disconnect_request_unconfirmed: boolean(&map, "DisconnectRequestUnconfirmed"),
            outage_since_millis: optional_time(&map, "OutageSince"),
            next_attempt_millis: optional_time(&map, "NextAttempt"),
            next_attempt_action: controlled(
                string_field(&map, "NextAttemptAction").as_deref(),
                ALLOWED_ACTIONS,
                "Unknown",
            ),
            last_recovery_seconds: number(&map, "LastRecoverySeconds"),
            last_recovery_at_millis: optional_time(&map, "LastRecoveryAt"),
            cycle_milliseconds: number(&map, "CycleMilliseconds"),
            slowest_stage: controlled(
                string_field(&map, "SlowestStage").as_deref(),
                ALLOWED_SLOW_STAGES,
                "",
            ),
            slowest_stage_milliseconds: number(&map, "SlowestStageMilliseconds"),
            updated_at_millis,
            authentication_stage: controlled(
                Some(&authentication_stage),
                ALLOWED_STAGES,
                "Unknown",
            ),
            authentication_failure: controlled(
                Some(&authentication_failure),
                ALLOWED_FAILURES,
                "Unknown",
            ),
            authentication_persistent_state: controlled(
                Some(&authentication_persistent_state),
                ALLOWED_AUTH_STATES,
                "Unknown",
            ),
            authentication_http_status: integer(&map, "AuthenticationHttpStatus"),
            credential_request_started: boolean(&map, "CredentialRequestStarted"),
            credentials_blocked: boolean(&map, "CredentialsBlocked"),
            credentials_available: optional_boolean(&map, "CredentialsAvailable"),
            settings_available: optional_boolean(&map, "SettingsAvailable"),
            submission_budget_code: code(&map, "SubmissionBudgetCode"),
            submission_retry_code: code(&map, "SubmissionRetryCode"),
            submission_attempts: integer(&map, "SubmissionAttempts"),
            submission_attempt_max: integer(&map, "SubmissionAttemptMax"),
            submission_budget_exhausted: optional_boolean(&map, "SubmissionBudgetExhausted"),
            submission_next_possible_at_millis: optional_time(&map, "SubmissionNextPossibleAt"),
            submission_cooldown_seconds: integer(&map, "SubmissionCooldownSeconds").unwrap_or(0),
            wifi_link_code: controlled(
                string_field(&map, "WifiLinkCode").as_deref(),
                ALLOWED_WIFI_LINK_CODES,
                "",
            ),
            portal_layout_suspected: boolean(&map, "PortalLayoutSuspected"),
            notification_code: notification
                .and_then(|value| value.as_object())
                .and_then(|object| string_field(object, "Code"))
                .filter(|code| NotificationCodes::is_known(code)),
            notification_at_millis: notification
                .and_then(|value| value.as_object())
                .and_then(|object| optional_time(object, "At")),
        })
    }
}

/// 单条日志摘要。只包含白名单字段；`detail`、异常原文、账号和网络标识从不进入此结构。
#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct LogSummary {
    pub time_millis: i64,
    pub action: String,
    pub wifi: String,
    pub portal: String,
    pub direct: String,
    pub seconds: Option<f64>,
    pub attempts: Option<i32>,
    pub disconnect_attempts: Option<i32>,
    pub authentication_stage: Option<String>,
    pub authentication_failure: Option<String>,
    pub event_code: Option<String>,
    pub notification_code: Option<String>,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct LogDates {
    pub dates: Vec<String>,
    /// 受控失败码；目录不存在不算失败，只有读取被拒绝等情形才设置。
    pub failure: Option<String>,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct LogPage {
    pub date: Option<String>,
    pub entries: Vec<LogSummary>,
    pub skipped: usize,
    /// 是否覆盖该日全部记录；达到条数上限或未读到文件头时为 false。
    pub complete: bool,
    /// 受控失败码（如 `Denied`/`InvalidDate`），不含异常原文。
    pub failure: Option<String>,
}

impl LogSummary {
    /// 与 C# `LogTailReader.IsRenderable` 同口径：时间、动作、说明字段必须存在且时间可解析。
    pub fn from_line(line: &str) -> Option<Self> {
        // C# 用 JsonDocument 逐属性读取并要求 time/action/detail 恰好出现一次；
        // serde_json 的 Value 会静默保留重复键，所以这里先做一次顶层计数。
        if !unique_identity_fields(line) {
            return None;
        }
        let value: Value = serde_json::from_str(line).ok()?;
        Self::from_value(&value)
    }

    pub fn from_value(value: &Value) -> Option<Self> {
        let object = value.as_object()?;
        let time = string_field(object, "Time")?;
        let time_millis = parse_iso8601_millis(&time)?;
        if time_millis == DEFAULT_TIME_MILLIS {
            return None;
        }
        let action = string_field(object, "Action").filter(|action| !action.trim().is_empty())?;
        string_field(object, "Detail")?;
        Some(Self {
            time_millis,
            action: controlled(Some(&action), ALLOWED_LOG_ACTIONS, "其他事件"),
            wifi: controlled(
                string_field(object, "Wifi").as_deref(),
                ALLOWED_WIFI,
                "未知",
            ),
            portal: controlled(
                string_field(object, "Portal").as_deref(),
                ALLOWED_PORTAL,
                "未知",
            ),
            direct: controlled(
                string_field(object, "Direct").as_deref(),
                ALLOWED_DIRECT,
                "未知",
            ),
            seconds: number(object, "Seconds"),
            attempts: integer(object, "Attempts"),
            disconnect_attempts: integer(object, "DisconnectAttempts"),
            authentication_stage: string_field(object, "AuthenticationStage")
                .filter(|stage| ALLOWED_STAGES.contains(&stage.as_str())),
            authentication_failure: string_field(object, "AuthenticationFailure").filter(
                |failure| failure != "None" && ALLOWED_FAILURES.contains(&failure.as_str()),
            ),
            event_code: string_field(object, "EventCode")
                .filter(|code| ALLOWED_EVENT_CODES.contains(&code.as_str())),
            notification_code: string_field(object, "NotificationCode")
                .filter(|code| NotificationCodes::is_known(code)),
        })
    }
}

// ---------- 受控词汇 ----------

/// 与 C# `NetworkDisplayMap.KnownStates` 逐字一致。
pub const KNOWN_STATES: &[&str] = &[
    "校园网在线",
    "正在重连校园 Wi-Fi",
    "需要处理认证",
    "已认证但外网不可达",
    "认证状态未知",
    "正在恢复",
    "网卡未就绪",
    "等待网络地址",
    "Wi-Fi 正在连接",
    "已切换其他 Wi-Fi",
    "等待设置",
    "检测异常",
    "已暂停恢复",
    "启动中",
];

pub const ALLOWED_WIFI: &[&str] = &[
    "已连接",
    "已关联，等待 IPv4",
    "正在连接",
    "网卡未就绪",
    "未连接",
    "其他配置",
    "待检测",
];

pub const ALLOWED_PORTAL: &[&str] = &[
    "已认证",
    "需认证",
    "未知，不自动认证",
    "未查询（外网检查已通过）",
    "待检测",
    "未知",
];

pub const ALLOWED_DIRECT: &[&str] = &["外网可达", "外网不可达或未知", "待检测"];

pub const ALLOWED_TUNNEL: &[&str] = &[
    "未配置检测",
    "本机代理可达",
    "本机代理不可达",
    "系统路径可达（路由样本推断为 TUN，出口未核验）",
    "系统路径不可达（路由样本推断为 TUN，出口未核验）",
    "系统路径可达（路由样本推断为 Wi-Fi，出口未核验）",
    "系统路径不可达（路由样本推断为 Wi-Fi，出口未核验）",
    "系统路径可达（路由未知，出口未核验）",
    "系统路径不可达（路由未知，出口未核验）",
];

pub const ALLOWED_ACTIONS: &[&str] = &["None", "ReconnectWifi", "Authenticate", "DisconnectWifi"];

pub const ALLOWED_WIFI_LINK_CODES: &[&str] = &[
    "Ready",
    "WaitingForAddress",
    "Associating",
    "Unavailable",
    "Disconnected",
    "OtherProfile",
];

/// 周期阶段名（`CycleTrace` / `LogSummary.AllowedOperations`）。与门户阶段名不同，
/// `SlowestStage` 绝不能套用 `ALLOWED_STAGES`，否则真实值会被投影成空串。
pub const ALLOWED_SLOW_STAGES: &[&str] = &[
    "MainCycle",
    "SettingsRead",
    "SettingsValidation",
    "DisconnectBudgetRead",
    "AuthenticationStateRead",
    "CredentialsRead",
    "WifiRead",
    "InternetProbe",
    "PortalInspection",
    "StatusWrite",
    "Authentication",
    "AuthenticationStateWrite",
    "WifiRequest",
    "DisconnectBudgetWrite",
    "Auxiliary",
    "AuxiliaryCancel",
    "Power",
    "ServiceLoop",
    "PreviousLogRead",
    "ReconnectWifi",
    "DisconnectWifi",
    "Authenticate",
    "DisconnectRecheckInternet",
    "DisconnectRecheckPortal",
    "SubmissionRecheck",
    "SubmissionBudgetRead",
    "RecheckSubmission",
    "AuxiliaryStart",
    "AuthenticationTotal",
];

pub const ALLOWED_STAGES: &[&str] = &[
    "Unknown",
    "Status",
    "GatewayRedirect",
    "HomePage",
    "ClientIpv4",
    "Configuration",
    "AuthenticationMode",
    "Submission",
    "Complete",
];

pub const ALLOWED_FAILURES: &[&str] = &[
    "None",
    "InvalidTarget",
    "AlreadyAuthenticated",
    "StatusUnknown",
    "HttpStatus",
    "MissingClientIpv4",
    "MalformedConfiguration",
    "MissingFlag",
    "InvalidFlagType",
    "UnsupportedFlag",
    "DuplicateFlag",
    "InvalidResponse",
    "Timeout",
    "Transport",
    "Tls",
    "CredentialsRejected",
    "InteractionRequired",
    "LoginRejected",
    "MissingGatewayParameters",
    "BindingRejected",
    "SafetyPersistence",
];

pub const ALLOWED_AUTH_STATES: &[&str] = &[
    "Ready",
    "SubmissionPending",
    "RetryCoolingDown",
    "BindingRejected",
    "CredentialsRejected",
    "InteractionRequired",
    "SafetyBlocked",
    "Unknown",
];

/// 与 C# `LogSummary.AllowedActions` 一致：固定动作加上 `NetworkDisplayMap.KnownStates`
/// （服务会把状态名直接写成日志动作）；未知动作降级为“其他事件”。
pub const ALLOWED_LOG_ACTIONS: &[&str] = &[
    "校园网在线",
    "正在重连校园 Wi-Fi",
    "需要处理认证",
    "已认证但外网不可达",
    "认证状态未知",
    "正在恢复",
    "网卡未就绪",
    "等待网络地址",
    "Wi-Fi 正在连接",
    "已切换其他 Wi-Fi",
    "等待设置",
    "检测异常",
    "已暂停恢复",
    "启动中",
    "重连校园 Wi-Fi",
    "连接校园 Wi-Fi",
    "恢复联网",
    "观测到联网恢复",
    "认证结果",
    "认证已接受",
    "检测异常",
    "状态写入异常",
    "日志写入异常",
    "设置已更改",
    "服务启动",
    "服务停止",
    "安全状态已恢复",
    "服务观测开始",
    "服务观测停止",
    "发现网络异常",
    "外网恢复验证通过",
    "继续追踪历史故障",
    "故障观测暂停",
    "恢复决策",
    "恢复动作开始",
    "恢复动作结束",
    "认证步骤",
    "Wi-Fi 请求结果",
    "安全状态写入开始",
    "安全状态写入完成",
    "操作异常",
    "操作恢复正常",
    "检测轮次中断",
    "日志证据缺口",
    "认证提交获准",
    "认证提交被阻止",
    "提交结果复检",
    "提交结果消解",
    "提交结果待人工",
    "断线前重新核验",
    "诊断事件",
    "开始恢复动作",
    "辅助检测失败",
    "取消主动断线",
    "主动断线请求失败",
    "重新认证",
    "门户疑似改版",
    "浏览器兜底请求",
    "提交额度已耗尽",
    "提交额度即将用尽",
    "提交额度已重置",
];

pub const ALLOWED_EVENT_CODES: &[&str] = &[
    "SessionStarted",
    "SessionStopped",
    "IncidentStarted",
    "IncidentRecovered",
    "IncidentResumed",
    "IncidentSuspended",
    "RecoveryDecision",
    "RecoveryActionStarted",
    "RecoveryActionCompleted",
    "PortalStep",
    "WifiRequestResult",
    "SafetyStateWriteStarted",
    "SafetyStateWritten",
    "OperationFailed",
    "OperationRecovered",
    "CycleInterrupted",
    "LoggingGap",
    "StatusChanged",
    "SubmissionAuthorized",
    "SubmissionNotAuthorized",
    "DisconnectRecheck",
    "SubmissionRechecking",
    "SubmissionResolved",
    "SubmissionUnresolved",
    "SubmissionBudgetExhausted",
    "SubmissionBudgetNearLimit",
    "SubmissionBudgetReset",
    "PortalLayoutChanged",
    "BrowserFallbackRequested",
];

pub struct NotificationCodes;

impl NotificationCodes {
    pub const ALL: &'static [&'static str] = &[
        "InternetRecovered",
        "AuthenticationPaused",
        "SubmissionRechecking",
        "SubmissionUnresolved",
        "BrowserFallbackRequested",
        "PortalLayoutChanged",
        "AuthChannelStarted",
        "AuthChannelResult",
        "AuthChannelNeedsHuman",
        "SubmissionBudgetExhausted",
        "SubmissionBudgetNearLimit",
        "SubmissionBudgetReset",
    ];

    pub fn is_known(code: &str) -> bool {
        Self::ALL.contains(&code)
    }
}

// ---------- 时间 ----------

/// 解析 C# `DateTimeOffset` 的 JSON 形态：`YYYY-MM-DDTHH:MM:SS[.fffffff](Z|±HH:MM)`。
pub fn parse_iso8601_millis(text: &str) -> Option<i64> {
    let bytes = text.as_bytes();
    if bytes.len() < 20 || bytes[4] != b'-' || bytes[7] != b'-' || bytes[10] != b'T' {
        return None;
    }
    let year: i64 = text.get(0..4)?.parse().ok()?;
    let month: u32 = text.get(5..7)?.parse().ok()?;
    let day: u32 = text.get(8..10)?.parse().ok()?;
    let hour: i64 = text.get(11..13)?.parse().ok()?;
    let minute: i64 = text.get(14..16)?.parse().ok()?;
    let second: i64 = text.get(17..19)?.parse().ok()?;
    if !(1..=12).contains(&month)
        || !(1..=31).contains(&day)
        || hour > 23
        || minute > 59
        || second > 60
    {
        return None;
    }
    let mut index = 19;
    let mut fraction_millis = 0i64;
    if bytes.get(index) == Some(&b'.') {
        index += 1;
        let start = index;
        while index < bytes.len() && bytes[index].is_ascii_digit() {
            index += 1;
        }
        let digits = &text[start..index];
        if digits.is_empty() || digits.len() > 9 {
            return None;
        }
        let mut value: i64 = digits.parse().ok()?;
        for _ in digits.len()..3 {
            value *= 10;
        }
        for _ in 3..digits.len() {
            value /= 10;
        }
        fraction_millis = value;
    }
    let offset_minutes = match bytes.get(index) {
        Some(&b'Z') | Some(&b'z') => {
            index += 1;
            0
        }
        Some(&sign) if sign == b'+' || sign == b'-' => {
            let hours: i64 = text.get(index + 1..index + 3)?.parse().ok()?;
            let minutes: i64 = text.get(index + 4..index + 6)?.parse().ok()?;
            if bytes.get(index + 3) != Some(&b':') {
                return None;
            }
            index += 6;
            let magnitude = hours * 60 + minutes;
            if sign == b'-' {
                -magnitude
            } else {
                magnitude
            }
        }
        None => 0,
        _ => return None,
    };
    if index != bytes.len() {
        return None;
    }
    let days = days_from_civil(year, month, day);
    Some(
        days * 86_400_000 + hour * 3_600_000 + minute * 60_000 + second * 1000 + fraction_millis
            - offset_minutes * 60_000,
    )
}

fn days_from_civil(year: i64, month: u32, day: u32) -> i64 {
    let year = if month <= 2 { year - 1 } else { year };
    let era = if year >= 0 { year } else { year - 399 } / 400;
    let year_of_era = year - era * 400;
    let shifted_month = (month + 9) % 12;
    let day_of_year = (153 * shifted_month as i64 + 2) / 5 + day as i64 - 1;
    let day_of_era = year_of_era * 365 + year_of_era / 4 - year_of_era / 100 + day_of_year;
    era * 146_097 + day_of_era - 719_468
}

// ---------- 投影辅助 ----------

/// 统计顶层 time/action/detail 的出现次数（大小写不敏感），要求各恰好一次。
fn unique_identity_fields(line: &str) -> bool {
    struct Counts;

    impl<'de> Visitor<'de> for Counts {
        type Value = (u8, u8, u8);

        fn expecting(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
            formatter.write_str("日志 JSON 对象")
        }

        fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> Result<Self::Value, A::Error> {
            let (mut time, mut action, mut detail) = (0u8, 0u8, 0u8);
            while let Some(key) = map.next_key::<String>()? {
                match key.to_ascii_lowercase().as_str() {
                    "time" => time += 1,
                    "action" => action += 1,
                    "detail" => detail += 1,
                    _ => {}
                }
                map.next_value::<IgnoredAny>()?;
            }
            Ok((time, action, detail))
        }
    }

    serde_json::Deserializer::from_str(line)
        .deserialize_map(Counts)
        .is_ok_and(|(time, action, detail)| time == 1 && action == 1 && detail == 1)
}

fn get<'a>(map: &'a Map<String, Value>, name: &str) -> Option<&'a Value> {
    map.get(name).or_else(|| {
        map.iter()
            .find(|(key, _)| key.eq_ignore_ascii_case(name))
            .map(|(_, value)| value)
    })
}

fn string_field(map: &Map<String, Value>, name: &str) -> Option<String> {
    get(map, name)?.as_str().map(str::to_string)
}

fn required_text(map: &Map<String, Value>, name: &str) -> Result<String, StatusParseError> {
    string_field(map, name)
        .filter(|value| !value.trim().is_empty())
        .ok_or(StatusParseError::MissingRequired)
}

fn required_time(map: &Map<String, Value>, name: &str) -> Result<i64, StatusParseError> {
    let text = required_text(map, name)?;
    let millis = parse_iso8601_millis(&text).ok_or(StatusParseError::InvalidTimestamp)?;
    // 与 C# StatusSnapshotReader.IsValid 一致：DateTimeOffset.MinValue 视为不可确认。
    if millis == DEFAULT_TIME_MILLIS {
        return Err(StatusParseError::InvalidTimestamp);
    }
    Ok(millis)
}

fn optional_time(map: &Map<String, Value>, name: &str) -> Option<i64> {
    string_field(map, name).and_then(|value| parse_iso8601_millis(&value))
}

fn boolean(map: &Map<String, Value>, name: &str) -> bool {
    get(map, name).and_then(Value::as_bool).unwrap_or(false)
}

fn optional_boolean(map: &Map<String, Value>, name: &str) -> Option<bool> {
    get(map, name).and_then(Value::as_bool)
}

fn integer(map: &Map<String, Value>, name: &str) -> Option<i32> {
    let value = get(map, name)?;
    value.as_i64().and_then(|number| i32::try_from(number).ok())
}

fn number(map: &Map<String, Value>, name: &str) -> Option<f64> {
    get(map, name)?.as_f64()
}

fn code(map: &Map<String, Value>, name: &str) -> String {
    string_field(map, name)
        .filter(|value| {
            value.len() <= 64 && value.chars().all(|c| c.is_ascii_alphanumeric() || c == '_')
        })
        .unwrap_or_default()
}

fn controlled(value: Option<&str>, allowed: &[&str], fallback: &str) -> String {
    match value {
        Some(value) if allowed.contains(&value) => value.to_string(),
        _ => fallback.to_string(),
    }
}

fn uu_class(value: Option<&str>) -> String {
    match value {
        Some(value) if value.starts_with("UU 服务运行") => "running",
        Some("UU 服务未运行") | Some("未检测到 UU 服务") => "stopped",
        Some("无法读取 UU 服务状态")
        | Some("UU 状态未知，检测超时")
        | Some("UU 状态未知，读取失败") => "unknown",
        None | Some("未核验") => "notProbed",
        _ => "unknown",
    }
    .to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn minimal_status() -> String {
        r#"{
            "UpdatedAt": "2026-01-01T00:00:00+00:00",
            "Enabled": true,
            "State": "校园网在线",
            "Wifi": "已连接",
            "Direct": "外网可达",
            "Portal": "未查询（外网检查已通过）",
            "Tunnel": "未配置检测",
            "Uu": "未核验",
            "AuthenticationStage": "Unknown",
            "AuthenticationFailure": "None",
            "AuthenticationPersistentState": "Ready"
        }"#
        .to_string()
    }

    #[test]
    fn 投影保留受控字段并丢弃敏感字段() {
        let mut value: Value = serde_json::from_str(&minimal_status()).unwrap();
        let object = value.as_object_mut().unwrap();
        object.insert("Username".into(), Value::String("student-01".into()));
        object.insert("InterfaceId".into(), Value::String("guid".into()));
        object.insert("WifiProfile".into(), Value::String("campus-profile".into()));
        object.insert(
            "ProxyUrlDisplay".into(),
            Value::String("http://127.0.0.1:7897".into()),
        );
        object.insert(
            "LastError".into(),
            Value::String("secret error text".into()),
        );
        object.insert("Detail".into(), Value::String("自由文本".into()));
        let view = StatusView::from_json(&serde_json::to_string(&value).unwrap()).unwrap();
        let json = serde_json::to_string(&view).unwrap();
        for forbidden in [
            "student-01",
            "campus-profile",
            "secret error",
            "自由文本",
            "127.0.0.1",
        ] {
            assert!(!json.contains(forbidden), "投影泄露了 {forbidden}");
        }
        assert_eq!(view.state, "校园网在线");
        assert_eq!(view.uu, "notProbed");
    }

    #[test]
    fn 未知状态与异常词汇降级而不透传() {
        let mut value: Value = serde_json::from_str(&minimal_status()).unwrap();
        let object = value.as_object_mut().unwrap();
        object.insert("State".into(), Value::String("portal says hello".into()));
        object.insert("Wifi".into(), Value::String("连到 8 号楼".into()));
        object.insert(
            "AuthenticationStage".into(),
            Value::String("../../etc".into()),
        );
        let view = StatusView::from_json(&serde_json::to_string(&value).unwrap()).unwrap();
        assert_eq!(view.state, "未知状态");
        assert_eq!(view.wifi, "未知");
        assert_eq!(view.authentication_stage, "Unknown");
    }

    #[test]
    fn 缺失必填字段与坏时间不可确认() {
        assert_eq!(
            StatusView::from_json("{}").unwrap_err(),
            StatusParseError::MissingRequired
        );
        let mut value: Value = serde_json::from_str(&minimal_status()).unwrap();
        value.as_object_mut().unwrap().remove("Wifi");
        assert_eq!(
            StatusView::from_json(&serde_json::to_string(&value).unwrap()).unwrap_err(),
            StatusParseError::MissingRequired
        );
        let mut value: Value = serde_json::from_str(&minimal_status()).unwrap();
        value
            .as_object_mut()
            .unwrap()
            .insert("UpdatedAt".into(), Value::String("yesterday".into()));
        assert_eq!(
            StatusView::from_json(&serde_json::to_string(&value).unwrap()).unwrap_err(),
            StatusParseError::InvalidTimestamp
        );
        assert_eq!(
            StatusView::from_json("{broken").unwrap_err(),
            StatusParseError::InvalidJson
        );
    }

    #[test]
    fn 时间解析覆盖偏移与默认值() {
        assert_eq!(parse_iso8601_millis("1970-01-01T00:00:00Z"), Some(0));
        assert_eq!(
            parse_iso8601_millis("2026-01-01T00:00:00+00:00"),
            Some(1_767_225_600_000)
        );
        assert_eq!(
            parse_iso8601_millis("2026-01-01T08:00:00+08:00"),
            Some(1_767_225_600_000)
        );
        assert_eq!(
            parse_iso8601_millis("2026-01-01T00:00:00.500Z"),
            Some(1_767_225_600_500)
        );
        assert_eq!(
            parse_iso8601_millis("0001-01-01T00:00:00+00:00"),
            Some(-62_135_596_800_000)
        );
        assert_eq!(parse_iso8601_millis("2026-13-01T00:00:00Z"), None);
        assert_eq!(parse_iso8601_millis("2026-01-01"), None);
    }

    #[test]
    fn 日志摘要只包含白名单字段() {
        let value: Value = serde_json::from_str(
            r#"{"time":"2026-01-01T00:00:00Z","action":"恢复决策","detail":"secret detail",
                "wifi":"已连接","portal":"需认证","direct":"外网不可达或未知","attempts":2,
                "authenticationStage":"Status","authenticationFailure":"None",
                "eventCode":"RecoveryDecision","notificationCode":"InternetRecovered"}"#,
        )
        .unwrap();
        let summary = LogSummary::from_value(&value).unwrap();
        let json = serde_json::to_string(&summary).unwrap();
        assert!(!json.contains("secret detail"));
        assert_eq!(summary.action, "恢复决策");
        assert_eq!(summary.authentication_failure, None);
        assert_eq!(
            summary.notification_code.as_deref(),
            Some("InternetRecovered")
        );
        let unknown: Value = serde_json::from_str(
            r#"{"time":"2026-01-01T00:00:00Z","action":"账号 12345 密码错误","detail":""}"#,
        )
        .unwrap();
        assert_eq!(LogSummary::from_value(&unknown).unwrap().action, "其他事件");
        assert!(LogSummary::from_value(&serde_json::json!({"detail": ""})).is_none());
    }

    #[test]
    fn 状态名作为日志动作不会被降级() {
        for state in KNOWN_STATES {
            let value = serde_json::json!({
                "Time": "2026-01-01T00:00:00+00:00",
                "Action": state,
                "Detail": "状态变化"
            });
            assert_eq!(
                LogSummary::from_value(&value).unwrap().action,
                *state,
                "状态动作被错误降级：{state}"
            );
        }
    }

    #[test]
    fn 日志行要求身份字段恰好一次且时间不是默认值() {
        let line = r#"{"Time":"2026-01-01T00:00:00+00:00","Action":"恢复决策","Detail":""}"#;
        assert!(LogSummary::from_line(line).is_some());
        let duplicate = r#"{"Time":"2026-01-01T00:00:00+00:00","Action":"恢复决策","action":"其他","Detail":""}"#;
        assert!(LogSummary::from_line(duplicate).is_none());
        let default_time =
            r#"{"Time":"0001-01-01T00:00:00+00:00","Action":"恢复决策","Detail":""}"#;
        assert!(LogSummary::from_line(default_time).is_none());
        assert!(LogSummary::from_line("[]").is_none());
    }
}
