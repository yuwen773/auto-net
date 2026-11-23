//! `SafetyStateStore.ReadAuthenticationBlock` 的只读对照实现。
//!
//! 语义与 C# 一致：历史指纹、失败-only 记录、缺字段、未知版本/枚举、损坏 JSON 一律保守阻断；
//! 读取从不写回。实现保留重复字段与大小写差异的检测能力，因为 C# 的 `HasFields` 按
//! 忽略大小写、恰好一次计数。

use autonet_contracts::parse_iso8601_millis;
use serde::de::{MapAccess, Visitor};
use serde::{Deserialize, Deserializer};
use serde_json::Value;
use std::fmt;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AuthenticationRecordKind {
    Missing,
    LegacyFingerprintShape,
    NonObject,
    LegacyRecord,
    MissingFields,
    InvalidState,
    UnsupportedVersion,
    InconsistentState,
    InvalidJson,
    CurrentRecord,
}

impl AuthenticationRecordKind {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Missing => "Missing",
            Self::LegacyFingerprintShape => "LegacyFingerprintShape",
            Self::NonObject => "NonObject",
            Self::LegacyRecord => "LegacyRecord",
            Self::MissingFields => "MissingFields",
            Self::InvalidState => "InvalidState",
            Self::UnsupportedVersion => "UnsupportedVersion",
            Self::InconsistentState => "InconsistentState",
            Self::InvalidJson => "InvalidJson",
            Self::CurrentRecord => "CurrentRecord",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AuthenticationState {
    Ready,
    SubmissionPending,
    RetryCoolingDown,
    BindingRejected,
    CredentialsRejected,
    InteractionRequired,
    SafetyBlocked,
}

impl AuthenticationState {
    fn from_value(value: i32) -> Option<Self> {
        Some(match value {
            0 => Self::Ready,
            1 => Self::SubmissionPending,
            2 => Self::RetryCoolingDown,
            3 => Self::BindingRejected,
            4 => Self::CredentialsRejected,
            5 => Self::InteractionRequired,
            6 => Self::SafetyBlocked,
            _ => return None,
        })
    }

    pub fn as_str(self) -> &'static str {
        match self {
            Self::Ready => "Ready",
            Self::SubmissionPending => "SubmissionPending",
            Self::RetryCoolingDown => "RetryCoolingDown",
            Self::BindingRejected => "BindingRejected",
            Self::CredentialsRejected => "CredentialsRejected",
            Self::InteractionRequired => "InteractionRequired",
            Self::SafetyBlocked => "SafetyBlocked",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PortalFailure {
    None,
    InvalidTarget,
    AlreadyAuthenticated,
    StatusUnknown,
    HttpStatus,
    MissingClientIpv4,
    MalformedConfiguration,
    MissingFlag,
    InvalidFlagType,
    UnsupportedFlag,
    DuplicateFlag,
    InvalidResponse,
    Timeout,
    Transport,
    Tls,
    CredentialsRejected,
    InteractionRequired,
    LoginRejected,
    MissingGatewayParameters,
    BindingRejected,
    SafetyPersistence,
}

impl PortalFailure {
    fn from_value(value: i32) -> Option<Self> {
        Some(match value {
            0 => Self::None,
            1 => Self::InvalidTarget,
            2 => Self::AlreadyAuthenticated,
            3 => Self::StatusUnknown,
            4 => Self::HttpStatus,
            5 => Self::MissingClientIpv4,
            6 => Self::MalformedConfiguration,
            7 => Self::MissingFlag,
            8 => Self::InvalidFlagType,
            9 => Self::UnsupportedFlag,
            10 => Self::DuplicateFlag,
            11 => Self::InvalidResponse,
            12 => Self::Timeout,
            13 => Self::Transport,
            14 => Self::Tls,
            15 => Self::CredentialsRejected,
            16 => Self::InteractionRequired,
            17 => Self::LoginRejected,
            18 => Self::MissingGatewayParameters,
            19 => Self::BindingRejected,
            20 => Self::SafetyPersistence,
            _ => return None,
        })
    }

    pub fn as_str(self) -> &'static str {
        match self {
            Self::None => "None",
            Self::InvalidTarget => "InvalidTarget",
            Self::AlreadyAuthenticated => "AlreadyAuthenticated",
            Self::StatusUnknown => "StatusUnknown",
            Self::HttpStatus => "HttpStatus",
            Self::MissingClientIpv4 => "MissingClientIpv4",
            Self::MalformedConfiguration => "MalformedConfiguration",
            Self::MissingFlag => "MissingFlag",
            Self::InvalidFlagType => "InvalidFlagType",
            Self::UnsupportedFlag => "UnsupportedFlag",
            Self::DuplicateFlag => "DuplicateFlag",
            Self::InvalidResponse => "InvalidResponse",
            Self::Timeout => "Timeout",
            Self::Transport => "Transport",
            Self::Tls => "Tls",
            Self::CredentialsRejected => "CredentialsRejected",
            Self::InteractionRequired => "InteractionRequired",
            Self::LoginRejected => "LoginRejected",
            Self::MissingGatewayParameters => "MissingGatewayParameters",
            Self::BindingRejected => "BindingRejected",
            Self::SafetyPersistence => "SafetyPersistence",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AuthBlock {
    pub state: AuthenticationState,
    pub failure: PortalFailure,
    pub attempts: i32,
    pub retry_delay_seconds: i32,
    pub blocks_submissions: bool,
}

impl Default for AuthBlock {
    fn default() -> Self {
        Self {
            state: AuthenticationState::SafetyBlocked,
            failure: PortalFailure::None,
            attempts: 0,
            retry_delay_seconds: 0,
            blocks_submissions: true,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AuthBlockRead {
    pub kind: AuthenticationRecordKind,
    pub block: Option<AuthBlock>,
}

impl AuthBlockRead {
    fn corrupt(kind: AuthenticationRecordKind) -> Self {
        Self {
            kind,
            block: Some(AuthBlock::default()),
        }
    }
}

const DEFAULT_TIME_MILLIS: i64 = -62_135_596_800_000; // 0001-01-01T00:00:00+00:00
const RECORD_FIELDS: [&str; 7] = [
    "Version",
    "State",
    "Failure",
    "Attempts",
    "RetryDelaySeconds",
    "RetryNotBefore",
    "Revision",
];

/// 解释 `auth-block.txt` 的文本；`None` 表示文件不存在。
pub fn interpret(content: Option<&str>) -> AuthBlockRead {
    let Some(content) = content else {
        return AuthBlockRead {
            kind: AuthenticationRecordKind::Missing,
            block: None,
        };
    };
    if content.chars().count() == 64 && content.chars().all(|c| c.is_ascii_hexdigit()) {
        return AuthBlockRead::corrupt(AuthenticationRecordKind::LegacyFingerprintShape);
    }
    let value: Value = match serde_json::from_str(content) {
        Ok(value) => value,
        Err(_) => return AuthBlockRead::corrupt(AuthenticationRecordKind::InvalidJson),
    };
    if !value.is_object() {
        return AuthBlockRead::corrupt(AuthenticationRecordKind::NonObject);
    }
    let object: RawObject = match serde_json::from_str(content) {
        Ok(object) => object,
        Err(_) => return AuthBlockRead::corrupt(AuthenticationRecordKind::InvalidJson),
    };
    if !object.has_exact("Version") {
        return interpret_legacy(&object);
    }
    if RECORD_FIELDS.iter().any(|name| object.count(name) != 1) {
        return AuthBlockRead::corrupt(AuthenticationRecordKind::MissingFields);
    }
    interpret_current(&object)
}

fn is_guid(value: &str) -> bool {
    let bytes = value.as_bytes();
    bytes.len() == 36
        && bytes.iter().enumerate().all(|(index, byte)| match index {
            8 | 13 | 18 | 23 => *byte == b'-',
            _ => byte.is_ascii_hexdigit(),
        })
}

fn interpret_legacy(object: &RawObject) -> AuthBlockRead {
    let Some(failure_value) = object.last("Failure") else {
        return AuthBlockRead::corrupt(AuthenticationRecordKind::LegacyRecord);
    };
    let Some(failure_number) = failure_value.as_i64() else {
        return AuthBlockRead::corrupt(AuthenticationRecordKind::InvalidJson);
    };
    let Ok(failure_number) = i32::try_from(failure_number) else {
        return AuthBlockRead::corrupt(AuthenticationRecordKind::InvalidJson);
    };
    let Some(failure) = PortalFailure::from_value(failure_number) else {
        return AuthBlockRead::corrupt(AuthenticationRecordKind::LegacyRecord);
    };
    AuthBlockRead {
        kind: AuthenticationRecordKind::LegacyRecord,
        block: Some(AuthBlock {
            state: state_for(failure),
            failure,
            attempts: 0,
            retry_delay_seconds: 0,
            blocks_submissions: true,
        }),
    }
}

fn interpret_current(object: &RawObject) -> AuthBlockRead {
    let version = match integer(object.last("Version")) {
        Some(value) => value,
        None => return AuthBlockRead::corrupt(AuthenticationRecordKind::InvalidJson),
    };
    if version != 1 {
        return AuthBlockRead::corrupt(AuthenticationRecordKind::UnsupportedVersion);
    }
    let state_value = match integer(object.last("State")) {
        Some(value) => value,
        None => return AuthBlockRead::corrupt(AuthenticationRecordKind::InvalidJson),
    };
    let failure_value = match integer(object.last("Failure")) {
        Some(value) => value,
        None => return AuthBlockRead::corrupt(AuthenticationRecordKind::InvalidJson),
    };
    let attempts = match integer(object.last("Attempts")) {
        Some(value) => value,
        None => return AuthBlockRead::corrupt(AuthenticationRecordKind::InvalidJson),
    };
    let retry_delay_seconds = match integer(object.last("RetryDelaySeconds")) {
        Some(value) => value,
        None => return AuthBlockRead::corrupt(AuthenticationRecordKind::InvalidJson),
    };
    let retry_not_before =
        match text(object.last("RetryNotBefore")).and_then(|value| parse_iso8601_millis(&value)) {
            Some(value) => value,
            None => return AuthBlockRead::corrupt(AuthenticationRecordKind::InvalidJson),
        };
    let Some(revision) = text(object.last("Revision")) else {
        return AuthBlockRead::corrupt(AuthenticationRecordKind::InvalidJson);
    };
    // C# 把 Revision 反序列化为 Guid，只接受标准 "D" 形式；其他写法是无效 JSON 而不是可忽略字段。
    if !is_guid(&revision) {
        return AuthBlockRead::corrupt(AuthenticationRecordKind::InvalidJson);
    }
    let fallback = match object.last("ChannelFallbackCause") {
        None => 0,
        Some(value) => match integer(Some(value)) {
            Some(value) => value,
            None => return AuthBlockRead::corrupt(AuthenticationRecordKind::InvalidJson),
        },
    };
    let (Some(state), Some(failure)) = (
        AuthenticationState::from_value(state_value),
        PortalFailure::from_value(failure_value),
    ) else {
        return AuthBlockRead::corrupt(AuthenticationRecordKind::InvalidState);
    };
    if !(0..=3).contains(&attempts)
        || state == AuthenticationState::SubmissionPending && attempts == 0
        || state == AuthenticationState::RetryCoolingDown
            && !((2..=60).contains(&retry_delay_seconds) && retry_not_before != DEFAULT_TIME_MILLIS)
        || state != AuthenticationState::RetryCoolingDown
            && (retry_delay_seconds != 0 || retry_not_before != DEFAULT_TIME_MILLIS)
    {
        return AuthBlockRead::corrupt(AuthenticationRecordKind::InvalidState);
    }
    let consistent = match state {
        AuthenticationState::Ready | AuthenticationState::SubmissionPending => {
            failure == PortalFailure::None
        }
        AuthenticationState::RetryCoolingDown => {
            attempts > 0
                && matches!(
                    failure,
                    PortalFailure::None
                        | PortalFailure::HttpStatus
                        | PortalFailure::MissingClientIpv4
                        | PortalFailure::MissingGatewayParameters
                        | PortalFailure::Timeout
                        | PortalFailure::Transport
                        | PortalFailure::StatusUnknown
                        | PortalFailure::LoginRejected
                        | PortalFailure::InvalidTarget
                )
        }
        AuthenticationState::BindingRejected => failure == PortalFailure::BindingRejected,
        AuthenticationState::CredentialsRejected => failure == PortalFailure::CredentialsRejected,
        AuthenticationState::InteractionRequired => failure == PortalFailure::InteractionRequired,
        AuthenticationState::SafetyBlocked => true,
    };
    let fallback_consistent = fallback == 0
        || state == AuthenticationState::InteractionRequired
            && attempts == 3
            && PortalFailure::from_value(fallback).is_some_and(is_structural);
    if !consistent || !fallback_consistent {
        return AuthBlockRead::corrupt(AuthenticationRecordKind::InconsistentState);
    }
    AuthBlockRead {
        kind: AuthenticationRecordKind::CurrentRecord,
        block: Some(AuthBlock {
            state,
            failure,
            attempts,
            retry_delay_seconds,
            blocks_submissions: !matches!(
                state,
                AuthenticationState::Ready | AuthenticationState::RetryCoolingDown
            ),
        }),
    }
}

fn state_for(failure: PortalFailure) -> AuthenticationState {
    match failure {
        PortalFailure::BindingRejected => AuthenticationState::BindingRejected,
        PortalFailure::CredentialsRejected => AuthenticationState::CredentialsRejected,
        PortalFailure::InteractionRequired => AuthenticationState::InteractionRequired,
        _ => AuthenticationState::SafetyBlocked,
    }
}

fn is_structural(failure: PortalFailure) -> bool {
    matches!(
        failure,
        PortalFailure::MissingFlag
            | PortalFailure::InvalidFlagType
            | PortalFailure::UnsupportedFlag
            | PortalFailure::DuplicateFlag
            | PortalFailure::MalformedConfiguration
            | PortalFailure::InvalidResponse
            | PortalFailure::MissingGatewayParameters
    )
}

pub(crate) fn integer(value: Option<&Value>) -> Option<i32> {
    let value = value?;
    value.as_i64().and_then(|number| i32::try_from(number).ok())
}

pub(crate) fn text(value: Option<&Value>) -> Option<String> {
    value?.as_str().map(str::to_string)
}

/// 保留重复键与原始大小写的对象，用于复刻 C# 的 `HasFields` / `JsonDocument` 语义。
#[derive(Default)]
pub(crate) struct RawObject(pub(crate) Vec<(String, Value)>);

impl RawObject {
    pub(crate) fn count(&self, name: &str) -> usize {
        self.0
            .iter()
            .filter(|(key, _)| key.eq_ignore_ascii_case(name))
            .count()
    }

    pub(crate) fn last(&self, name: &str) -> Option<&Value> {
        self.0
            .iter()
            .rev()
            .find(|(key, _)| key.eq_ignore_ascii_case(name))
            .map(|(_, value)| value)
    }

    pub(crate) fn has_exact(&self, name: &str) -> bool {
        self.0.iter().any(|(key, _)| key == name)
    }
}

impl<'de> Deserialize<'de> for RawObject {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct RawObjectVisitor;

        impl<'de> Visitor<'de> for RawObjectVisitor {
            type Value = RawObject;

            fn expecting(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
                formatter.write_str("an object")
            }

            fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> Result<RawObject, A::Error> {
                let mut entries = Vec::new();
                while let Some(key) = map.next_key::<String>()? {
                    let value = map.next_value::<Value>()?;
                    entries.push((key, value));
                }
                Ok(RawObject(entries))
            }
        }

        deserializer.deserialize_map(RawObjectVisitor)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn 指纹与失败_only_都保持阻断() {
        assert_eq!(
            interpret(Some(&"A".repeat(64))).kind,
            AuthenticationRecordKind::LegacyFingerprintShape
        );
        let read = interpret(Some(r#"{"Failure":15}"#));
        assert_eq!(read.kind, AuthenticationRecordKind::LegacyRecord);
        let block = read.block.unwrap();
        assert_eq!(block.state, AuthenticationState::CredentialsRejected);
        assert!(block.blocks_submissions);
    }

    #[test]
    fn 重复字段与大小写按_csharp_规则() {
        assert_eq!(
            interpret(Some(r#"{"Version":1,"Version":1}"#)).kind,
            AuthenticationRecordKind::MissingFields
        );
        // C# 的 Version 探测区分大小写：小写 version 会走历史记录分支。
        assert_eq!(
            interpret(Some(r#"{"version":1,"Failure":15}"#)).kind,
            AuthenticationRecordKind::LegacyRecord
        );
    }

    #[test]
    fn 冷却记录允许重试且当前记录被识别() {
        let read = interpret(Some(
            r#"{"Version":1,"State":2,"Failure":13,"Attempts":2,"RetryDelaySeconds":60,
                "RetryNotBefore":"2026-01-01T00:01:00+00:00","Revision":"11111111-1111-1111-1111-111111111111"}"#,
        ));
        assert_eq!(read.kind, AuthenticationRecordKind::CurrentRecord);
        assert!(!read.block.unwrap().blocks_submissions);
    }
}
