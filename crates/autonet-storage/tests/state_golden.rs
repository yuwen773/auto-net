use autonet_storage::auth_block::interpret;
use autonet_storage::submission_budget::{parse, BudgetOutcome};
use serde_json::Value;
use std::path::PathBuf;

fn fixture(name: &str) -> Value {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../fixtures/cross-language")
        .join(name);
    let text = std::fs::read_to_string(&path).unwrap_or_else(|_| panic!("缺少夹具 {name}"));
    serde_json::from_str(&text).unwrap_or_else(|_| panic!("夹具 {name} 不是有效 JSON"))
}

#[test]
fn 历史认证状态夹具逐字段一致() {
    let document = fixture("authentication-block.json");
    let cases = document["cases"].as_array().expect("cases 必须是数组");
    assert!(cases.len() >= 30, "历史状态夹具数量不足");
    for case in cases {
        let name = case["name"].as_str().unwrap_or("<未命名>");
        let content = case["content"].as_str();
        let read = interpret(content);
        let expect = &case["expect"];
        assert_eq!(
            read.kind.as_str(),
            expect["kind"].as_str().expect("kind 缺失"),
            "记录形态不一致：{name}"
        );
        let block = read.block;
        assert_eq!(
            block.as_ref().map(|block| block.blocks_submissions),
            expect.get("blocksSubmissions").and_then(Value::as_bool),
            "阻断判定不一致：{name}"
        );
        assert_eq!(
            block.as_ref().map(|block| block.state.as_str()),
            expect.get("state").and_then(Value::as_str),
            "认证状态不一致：{name}"
        );
        assert_eq!(
            block.as_ref().map(|block| block.failure.as_str()),
            expect.get("failure").and_then(Value::as_str),
            "失败分类不一致：{name}"
        );
        assert_eq!(
            block.as_ref().map(|block| block.attempts),
            expect
                .get("attempts")
                .and_then(Value::as_i64)
                .map(|value| value as i32),
            "次数不一致：{name}"
        );
        assert_eq!(
            block.as_ref().map(|block| block.retry_delay_seconds),
            expect
                .get("retryDelaySeconds")
                .and_then(Value::as_i64)
                .map(|value| value as i32),
            "退避秒数不一致：{name}"
        );
    }
}

#[test]
fn 提交额度夹具逐字段一致() {
    let document = fixture("submission-budget.json");
    let cases = document["cases"].as_array().expect("cases 必须是数组");
    assert!(cases.len() >= 15, "提交额度夹具数量不足");
    for case in cases {
        let name = case["name"].as_str().unwrap_or("<未命名>");
        let content = case["content"].as_str();
        let read = parse(content);
        let expect = &case["expect"];
        let expected_outcome = match expect["outcome"].as_str().expect("outcome 缺失") {
            "Missing" => BudgetOutcome::Missing,
            "Valid" => BudgetOutcome::Valid,
            "Invalid" => BudgetOutcome::Invalid,
            other => panic!("未知 outcome {other}"),
        };
        assert_eq!(read.outcome, expected_outcome, "判定不一致：{name}");
        let budget = read.budget;
        assert_eq!(
            budget.as_ref().map(|budget| budget.state.as_str()),
            expect.get("state").and_then(Value::as_str),
            "状态不一致：{name}"
        );
        assert_eq!(
            budget.as_ref().map(|budget| budget.attempts),
            expect
                .get("attempts")
                .and_then(Value::as_i64)
                .map(|value| value as i32),
            "次数不一致：{name}"
        );
        assert_eq!(
            budget.as_ref().and_then(|budget| budget.reserved_at_millis),
            expect.get("reservedAt").and_then(Value::as_i64),
            "预留时刻不一致：{name}"
        );
        assert_eq!(
            budget
                .as_ref()
                .and_then(|budget| budget.next_possible_at_millis),
            expect.get("nextPossibleAt").and_then(Value::as_i64),
            "下次时刻不一致：{name}"
        );
        assert_eq!(
            budget.as_ref().map(|budget| budget.exhausted),
            expect.get("exhausted").and_then(Value::as_bool),
            "耗尽判定不一致：{name}"
        );
    }
}

#[cfg(windows)]
#[test]
fn 已发布_dpapi_密文可在同机只读解密() {
    if std::env::var("AUTONET_DPAPI_SAME_MACHINE").ok().as_deref() != Some("1") {
        eprintln!("跳过 DPAPI 同机解密：未设置 AUTONET_DPAPI_SAME_MACHINE=1");
        return;
    }
    let document = fixture("dpapi-credential.json");
    let ciphertext = document["ciphertextBase64"].as_str().expect("密文缺失");
    let bytes = decode_base64(ciphertext).expect("密文不是有效 base64");
    let plaintext = autonet_storage::dpapi::unprotect_local_machine(&bytes).expect("同机解密失败");
    let digest = sha256_hex(&plaintext);
    assert_eq!(
        digest,
        document["plaintextUtf8Sha256"].as_str().expect("摘要缺失"),
        "解密内容与夹具摘要不一致"
    );
    assert_eq!(
        plaintext.len(),
        document["plaintextBytes"].as_u64().expect("长度缺失") as usize,
        "解密长度不一致"
    );
    let text = String::from_utf8(plaintext).expect("明文不是 UTF-8");
    assert!(text.contains("fixture-user"), "解密内容不是测试凭据");
}

#[cfg(windows)]
fn decode_base64(text: &str) -> Option<Vec<u8>> {
    const ALPHABET: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut output = Vec::with_capacity(text.len() / 4 * 3);
    let mut buffer = 0u32;
    let mut bits = 0u32;
    for byte in text.bytes() {
        if byte == b'=' {
            break;
        }
        let value = ALPHABET.iter().position(|candidate| *candidate == byte)? as u32;
        buffer = (buffer << 6) | value;
        bits += 6;
        if bits >= 8 {
            bits -= 8;
            output.push((buffer >> bits) as u8);
        }
    }
    Some(output)
}

#[cfg(windows)]
fn sha256_hex(bytes: &[u8]) -> String {
    use sha2::{Digest, Sha256};
    let digest = Sha256::digest(bytes);
    digest.iter().map(|byte| format!("{byte:02x}")).collect()
}

#[test]
fn 安装生命周期夹具保留关键契约() {
    let document = fixture("install-lifecycle.json");
    assert_eq!(
        document["schema"].as_str(),
        Some("autonet.golden.install-lifecycle/1")
    );
    let service = &document["service"];
    assert_eq!(service["name"].as_str(), Some("AutoNet"));
    assert_eq!(service["start"].as_str(), Some("Automatic"));
    let dependencies: Vec<&str> = service["dependsOn"]
        .as_array()
        .expect("dependsOn 缺失")
        .iter()
        .filter_map(Value::as_str)
        .collect();
    assert_eq!(dependencies, vec!["WlanSvc", "Tcpip"]);
    assert_eq!(
        document["paths"]["private"].as_str(),
        Some("C:\\ProgramData\\AutoNet\\private")
    );
    assert_eq!(
        document["acl"]["private"]
            .as_array()
            .map(|items| items.len()),
        Some(2),
        "private 目录只允许 Administrators 与 SYSTEM"
    );
}
