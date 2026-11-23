//! `SafetyStateStore.ReadSubmissionBudget` 的只读对照实现。
//!
//! 记录结构、字段集合、版本、状态字符串与 0–3 次 / 120 秒间隔规则都按 C# 校验；
//! 无效记录返回 `Invalid`，调用方必须停止提交（预览只展示，不提交）。

use crate::auth_block::{integer, text, RawObject};
use autonet_contracts::parse_iso8601_millis;
use serde_json::Value;

const FIELDS: [&str; 6] = [
    "Version",
    "State",
    "Attempts",
    "ReservedAt",
    "NextPossibleAt",
    "Revision",
];
const DEFAULT_TIME_MILLIS: i64 = -62_135_596_800_000; // 0001-01-01T00:00:00+00:00
const SUBMISSION_INTERVAL_MILLIS: i64 = 120_000;
const ATTEMPT_CAP: i32 = 3;
const EMPTY_GUID: &str = "00000000000000000000000000000000";

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BudgetOutcome {
    Missing,
    Valid,
    Invalid,
}

impl BudgetOutcome {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Missing => "Missing",
            Self::Valid => "Valid",
            Self::Invalid => "Invalid",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BudgetView {
    pub state: String,
    pub attempts: i32,
    pub reserved_at_millis: Option<i64>,
    pub next_possible_at_millis: Option<i64>,
    pub exhausted: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BudgetRead {
    pub outcome: BudgetOutcome,
    pub budget: Option<BudgetView>,
}

impl BudgetRead {
    fn invalid() -> Self {
        Self {
            outcome: BudgetOutcome::Invalid,
            budget: None,
        }
    }
}

/// 解释 `submission-budget.json` 的文本；`None` 表示文件不存在。
pub fn parse(content: Option<&str>) -> BudgetRead {
    let Some(content) = content else {
        return BudgetRead {
            outcome: BudgetOutcome::Missing,
            budget: None,
        };
    };
    let value: Value = match serde_json::from_str(content) {
        Ok(value) => value,
        Err(_) => return BudgetRead::invalid(),
    };
    if !value.is_object() {
        return BudgetRead::invalid();
    }
    let object: RawObject = match serde_json::from_str(content) {
        Ok(object) => object,
        Err(_) => return BudgetRead::invalid(),
    };
    if FIELDS.iter().any(|name| object.count(name) != 1)
        || !object
            .0
            .iter()
            .all(|(key, _)| FIELDS.contains(&key.as_str()))
    {
        return BudgetRead::invalid();
    }
    let version = match integer(object.last("Version")) {
        Some(value) => value,
        None => return BudgetRead::invalid(),
    };
    let attempts = match integer(object.last("Attempts")) {
        Some(value) => value,
        None => return BudgetRead::invalid(),
    };
    let Some(state) = text(object.last("State")) else {
        return BudgetRead::invalid();
    };
    let Some(reserved_at) =
        text(object.last("ReservedAt")).and_then(|value| parse_iso8601_millis(&value))
    else {
        return BudgetRead::invalid();
    };
    let Some(next_possible_at) =
        text(object.last("NextPossibleAt")).and_then(|value| parse_iso8601_millis(&value))
    else {
        return BudgetRead::invalid();
    };
    let Some(revision) = text(object.last("Revision")) else {
        return BudgetRead::invalid();
    };
    if version != 1
        || !(0..=ATTEMPT_CAP).contains(&attempts)
        || !matches!(state.as_str(), "Reserved" | "PossiblySent" | "Concluded")
        || !is_guid(&revision)
        || normalize_guid(&revision) == EMPTY_GUID
    {
        return BudgetRead::invalid();
    }
    let valid_times = if attempts == 0 {
        state == "Concluded"
            && reserved_at == DEFAULT_TIME_MILLIS
            && next_possible_at == DEFAULT_TIME_MILLIS
    } else {
        reserved_at != DEFAULT_TIME_MILLIS
            && next_possible_at - reserved_at >= SUBMISSION_INTERVAL_MILLIS
    };
    if !valid_times {
        return BudgetRead::invalid();
    }
    BudgetRead {
        outcome: BudgetOutcome::Valid,
        budget: Some(BudgetView {
            state,
            attempts,
            reserved_at_millis: if reserved_at == DEFAULT_TIME_MILLIS {
                None
            } else {
                Some(reserved_at)
            },
            next_possible_at_millis: if next_possible_at == DEFAULT_TIME_MILLIS {
                None
            } else {
                Some(next_possible_at)
            },
            exhausted: attempts >= ATTEMPT_CAP,
        }),
    }
}

fn is_guid(value: &str) -> bool {
    let bytes = value.as_bytes();
    bytes.len() == 36
        && bytes.iter().enumerate().all(|(index, byte)| match index {
            8 | 13 | 18 | 23 => *byte == b'-',
            _ => byte.is_ascii_hexdigit(),
        })
}

fn normalize_guid(value: &str) -> String {
    value
        .chars()
        .filter(|c| c.is_ascii_hexdigit())
        .map(|c| c.to_ascii_lowercase())
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn 零次结束记录有效且间隔不足无效() {
        let valid = r#"{"Version":1,"State":"Concluded","Attempts":0,
            "ReservedAt":"0001-01-01T00:00:00+00:00","NextPossibleAt":"0001-01-01T00:00:00+00:00",
            "Revision":"22222222-2222-2222-2222-222222222222"}"#;
        assert_eq!(parse(Some(valid)).outcome, BudgetOutcome::Valid);
        let short = r#"{"Version":1,"State":"Reserved","Attempts":1,
            "ReservedAt":"2026-01-01T00:00:00+00:00","NextPossibleAt":"2026-01-01T00:01:00+00:00",
            "Revision":"22222222-2222-2222-2222-222222222222"}"#;
        assert_eq!(parse(Some(short)).outcome, BudgetOutcome::Invalid);
        assert_eq!(parse(None).outcome, BudgetOutcome::Missing);
    }

    #[test]
    fn 额外字段与重复字段无效() {
        let extra = r#"{"Version":1,"State":"Reserved","Attempts":1,
            "ReservedAt":"2026-01-01T00:00:00+00:00","NextPossibleAt":"2026-01-01T00:02:00+00:00",
            "Revision":"22222222-2222-2222-2222-222222222222","Extra":1}"#;
        assert_eq!(parse(Some(extra)).outcome, BudgetOutcome::Invalid);
        let duplicate = r#"{"Version":1,"State":"Reserved","Attempts":1,"Attempts":1,
            "ReservedAt":"2026-01-01T00:00:00+00:00","NextPossibleAt":"2026-01-01T00:02:00+00:00",
            "Revision":"22222222-2222-2222-2222-222222222222"}"#;
        assert_eq!(parse(Some(duplicate)).outcome, BudgetOutcome::Invalid);
    }
}
