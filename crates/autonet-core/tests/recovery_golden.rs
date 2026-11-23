use autonet_core::{
    ActionKind, DisconnectBudget, DisconnectRequestState, Observation, PortalState, RecoveryPolicy,
    WifiState, AUTHENTICATION_ATTEMPT_CAP, BACKOFF_CEILING_SECONDS, DISCONNECT_ATTEMPT_CAP,
    DISCONNECT_COOLDOWN_SECONDS, FAST_RETRY_SECONDS, FAST_RETRY_WINDOW_SECONDS,
    ONLINE_RESET_SECONDS, STALLED_OBSERVATION_SECONDS, STALLED_ROUND_THRESHOLD,
};
use serde_json::Value;
use std::path::PathBuf;

fn fixture() -> Value {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../fixtures/cross-language/recovery-policy.json");
    let text = std::fs::read_to_string(&path).expect("缺少恢复策略夹具");
    serde_json::from_str(&text).expect("恢复策略夹具不是有效 JSON")
}

fn i64_of(value: &Value, key: &str) -> Option<i64> {
    value.get(key).and_then(Value::as_i64)
}

fn bool_of(value: &Value, key: &str) -> bool {
    value.get(key).and_then(Value::as_bool).unwrap_or(false)
}

#[test]
fn 常量与_csharp_导出一致() {
    let document = fixture();
    let constants = &document["constants"];
    assert_eq!(
        i64_of(constants, "fastRetrySeconds"),
        Some(FAST_RETRY_SECONDS)
    );
    assert_eq!(
        i64_of(constants, "fastRetryWindowSeconds"),
        Some(FAST_RETRY_WINDOW_SECONDS)
    );
    assert_eq!(
        i64_of(constants, "backoffCeilingSeconds"),
        Some(BACKOFF_CEILING_SECONDS)
    );
    assert_eq!(
        i64_of(constants, "disconnectCooldownSeconds"),
        Some(DISCONNECT_COOLDOWN_SECONDS)
    );
    assert_eq!(
        i64_of(constants, "disconnectAttemptCap"),
        Some(DISCONNECT_ATTEMPT_CAP as i64)
    );
    assert_eq!(
        i64_of(constants, "stalledRoundThreshold"),
        Some(STALLED_ROUND_THRESHOLD as i64)
    );
    assert_eq!(
        i64_of(constants, "stalledObservationSeconds"),
        Some(STALLED_OBSERVATION_SECONDS)
    );
    assert_eq!(
        i64_of(constants, "onlineResetSeconds"),
        Some(ONLINE_RESET_SECONDS)
    );
    assert_eq!(
        i64_of(constants, "authenticationAttemptCap"),
        Some(AUTHENTICATION_ATTEMPT_CAP as i64)
    );
}

#[test]
fn 恢复策略夹具逐字段一致() {
    let document = fixture();
    let cases = document["cases"].as_array().expect("cases 必须是数组");
    assert!(!cases.is_empty(), "夹具没有用例");
    for case in cases {
        let name = case["name"].as_str().unwrap_or("<未命名>");
        let mut policy = RecoveryPolicy::new();
        let steps = case["steps"].as_array().expect("steps 必须是数组");
        for (index, step) in steps.iter().enumerate() {
            let op = step["op"].as_str().expect("op 缺失");
            let now = i64_of(step, "now").expect("now 缺失");
            let context = format!("{name} / 第 {index} 步 / {op}");
            match op {
                "observe" => {
                    let wifi = WifiState::parse(step["wifi"].as_str().expect("wifi 缺失"))
                        .expect("未知 wifi");
                    let portal = PortalState::parse(step["portal"].as_str().expect("portal 缺失"))
                        .expect("未知 portal");
                    let action = policy.decide(
                        Observation::new(wifi, bool_of(step, "directOnline"), portal),
                        now,
                        bool_of(step, "enabled"),
                        bool_of(step, "hasCredentials"),
                        bool_of(step, "allowActiveDisconnect"),
                    );
                    assert_eq!(
                        action.as_str(),
                        step["action"].as_str().expect("action 缺失"),
                        "动作不一致：{context}"
                    );
                }
                "attempted" => policy.attempted(now),
                "restoreDisconnectBudget" => policy.restore_disconnect_budget(
                    DisconnectBudget::new(
                        i64_of(step, "budgetAttempts").expect("budgetAttempts 缺失") as i32,
                        parse_state(step["budgetState"].as_str().expect("budgetState 缺失")),
                        i64_of(step, "budgetCompletedAt"),
                    ),
                    now,
                ),
                "disconnectCompleted" => policy.disconnect_completed(
                    DisconnectBudget::new(
                        i64_of(step, "budgetAttempts").unwrap_or(0) as i32,
                        DisconnectRequestState::Completed,
                        Some(now),
                    ),
                    now,
                ),
                "cancelUnissuedDisconnectReservation" => {
                    policy.cancel_unissued_disconnect_reservation()
                }
                "interruptObservation" => policy.interrupt_observation(),
                "deferAuthentication" => policy.defer_authentication_until(
                    i64_of(step, "deferUntil").expect("deferUntil 缺失"),
                ),
                "setCredentialsBlocked" => {
                    policy.set_credentials_blocked(bool_of(step, "credentialsBlocked"))
                }
                other => panic!("未知操作 {other}：{context}"),
            }
            let expect = &step["expect"];
            assert_eq!(
                policy.decision_reason(),
                expect["reason"].as_str().expect("reason 缺失"),
                "判定原因不一致：{context}"
            );
            assert_eq!(
                policy.attempts(),
                expect["attempts"].as_i64().expect("attempts 缺失") as i32,
                "自动动作次数不一致：{context}"
            );
            assert_eq!(
                policy.disconnect_attempts(),
                expect["disconnectAttempts"]
                    .as_i64()
                    .expect("disconnectAttempts 缺失") as i32,
                "断线次数不一致：{context}"
            );
            assert_eq!(
                policy.next_attempt(),
                i64_of(expect, "nextAttempt"),
                "下次尝试不一致：{context}"
            );
            assert_eq!(
                policy.next_attempt_action().as_str(),
                expect["nextAttemptAction"]
                    .as_str()
                    .expect("nextAttemptAction 缺失"),
                "下次动作不一致：{context}"
            );
            assert_eq!(
                policy.outage_since(),
                i64_of(expect, "outageSince"),
                "故障起点不一致：{context}"
            );
            assert_eq!(
                policy.failed_rounds(),
                expect["failedRounds"].as_i64().expect("failedRounds 缺失") as i32,
                "连续失败轮次不一致：{context}"
            );
            let expected_seconds = expect["observationSeconds"]
                .as_f64()
                .expect("observationSeconds 缺失");
            assert!(
                (policy.observation_seconds(now) - expected_seconds).abs() < 0.0011,
                "观察秒数不一致：{context}"
            );
            assert_eq!(
                policy.credentials_blocked(),
                bool_of(expect, "credentialsBlocked"),
                "凭据阻断状态不一致：{context}"
            );
        }
    }
}

fn parse_state(value: &str) -> DisconnectRequestState {
    match value {
        "RequestPending" => DisconnectRequestState::RequestPending,
        "Completed" => DisconnectRequestState::Completed,
        other => panic!("未知断线额度状态 {other}"),
    }
}

#[test]
fn 动作枚举覆盖全部名称() {
    for action in [
        ActionKind::None,
        ActionKind::ReconnectWifi,
        ActionKind::Authenticate,
        ActionKind::DisconnectWifi,
    ] {
        assert_eq!(ActionKind::parse(action.as_str()), Some(action));
    }
}
