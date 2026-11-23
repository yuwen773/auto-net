//! C# `StatusSnapshot` / `ActivityLog` 序列化结果的投影校验（P0 数据读写契约）。
//!
//! 夹具由 `--export-golden-fixtures` 生成，状态与日志都带敏感哨兵；这里同时证明
//! Rust 能读懂真实字段名，并且投影不会把账号、网卡、说明或异常原文带出去。

use autonet_contracts::{LogSummary, StatusView};
use serde_json::Value;

fn fixture() -> Value {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../fixtures/cross-language/status-log-projection.json");
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|error| panic!("读取夹具失败 {}: {error}", path.display()));
    serde_json::from_str(&text).expect("投影夹具不是合法 JSON")
}

fn markers(root: &Value) -> Vec<String> {
    root["expect"]["sensitiveMarkers"]
        .as_array()
        .expect("缺少 sensitiveMarkers")
        .iter()
        .map(|value| value.as_str().expect("哨兵必须是字符串").to_string())
        .collect()
}

#[test]
fn 状态投影读取_csharp_字段并丢弃敏感哨兵() {
    let root = fixture();
    let expect = &root["expect"];
    let status = root["status"].to_string();
    let view = StatusView::from_json(&status).expect("C# 状态快照应可解析");

    assert_eq!(view.state, expect["state"].as_str().unwrap());
    assert_eq!(
        view.updated_at_millis,
        expect["updatedAtMillis"].as_i64().unwrap()
    );
    assert_eq!(view.wifi, expect["wifi"].as_str().unwrap());
    assert_eq!(view.direct, expect["direct"].as_str().unwrap());
    assert_eq!(view.portal, expect["portal"].as_str().unwrap());
    assert_eq!(view.tunnel, expect["tunnel"].as_str().unwrap());
    assert_eq!(view.uu, expect["uuClass"].as_str().unwrap());
    assert_eq!(view.slowest_stage, expect["slowestStage"].as_str().unwrap());
    assert_eq!(
        view.submission_budget_code,
        expect["submissionBudgetCode"].as_str().unwrap()
    );
    assert_eq!(
        view.wifi_link_code,
        expect["wifiLinkCode"].as_str().unwrap()
    );
    assert_eq!(
        view.authentication_stage,
        expect["authenticationStage"].as_str().unwrap()
    );
    assert_eq!(view.authentication_failure, "None");
    assert_eq!(view.authentication_persistent_state, "Ready");
    assert_eq!(
        view.submission_attempts,
        Some(expect["submissionAttempts"].as_i64().unwrap() as i32)
    );
    assert_eq!(
        view.cycle_milliseconds,
        expect["cycleMilliseconds"].as_f64()
    );
    assert_eq!(
        view.notification_code.as_deref(),
        expect["notificationCode"].as_str()
    );
    assert_eq!(view.attempts, 1);
    assert_eq!(view.disconnect_budget_max, 3);
}

#[test]
fn 状态投影的序列化结果不含任何敏感哨兵() {
    let root = fixture();
    let view = StatusView::from_json(&root["status"].to_string()).expect("状态应可解析");
    let projected = serde_json::to_string(&view).expect("投影应可序列化");
    for marker in markers(&root) {
        assert!(
            !projected.contains(&marker),
            "状态投影泄漏了敏感哨兵：{marker}"
        );
    }
}

#[test]
fn 日志摘要读取_csharp_字段并丢弃敏感哨兵() {
    let root = fixture();
    let expect = &root["expect"];
    let summary = LogSummary::from_value(&root["log"]).expect("C# 日志记录应可摘要");
    assert_eq!(summary.action, expect["logAction"].as_str().unwrap());
    assert_eq!(
        summary.event_code.as_deref(),
        expect["logEventCode"].as_str()
    );
    assert_eq!(summary.attempts, Some(2));

    let projected = serde_json::to_string(&summary).expect("摘要应可序列化");
    for marker in markers(&root) {
        assert!(
            !projected.contains(&marker),
            "日志摘要泄漏了敏感哨兵：{marker}"
        );
    }
}
