//! `status.json` 的只读适配：缺失、损坏、拒绝访问、过期都显式区分（ADR-0023）。

use autonet_contracts::{
    SnapshotEnvelope, SnapshotState, StatusView, STATUS_FRESH_MILLIS,
    STATUS_FUTURE_TOLERANCE_MILLIS,
};
use std::path::Path;

use crate::fs::read_text;

/// 读取并投影状态文件。`now_millis` 由调用方提供，便于测试固定时间。
pub fn read(path: &Path, now_millis: i64) -> SnapshotEnvelope {
    let (state, reason) = match read_text(path) {
        Ok(text) => match StatusView::from_json(&text) {
            Ok(view) => {
                let age = now_millis - view.updated_at_millis;
                let stale = !(-STATUS_FUTURE_TOLERANCE_MILLIS..STATUS_FRESH_MILLIS).contains(&age);
                return SnapshotEnvelope {
                    state: if stale {
                        SnapshotState::Stale
                    } else {
                        SnapshotState::Ok
                    },
                    updated_at_millis: Some(view.updated_at_millis),
                    snapshot: Some(view),
                    read_at_millis: now_millis,
                    reason: stale.then(|| "Stale".to_string()),
                };
            }
            Err(error) => (SnapshotState::Corrupt, error.code().to_string()),
        },
        Err(error) => (
            match error {
                crate::ReadFailure::Missing => SnapshotState::Missing,
                crate::ReadFailure::Denied => SnapshotState::Denied,
                crate::ReadFailure::Unreadable => SnapshotState::Corrupt,
            },
            error.code().to_string(),
        ),
    };
    SnapshotEnvelope {
        state,
        snapshot: None,
        updated_at_millis: None,
        read_at_millis: now_millis,
        reason: Some(reason),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    fn status(updated_at: &str) -> String {
        format!(
            r#"{{"UpdatedAt":"{updated_at}","Enabled":true,"State":"校园网在线",
                "Wifi":"已连接","Direct":"外网可达","Portal":"未查询（外网检查已通过）",
                "Tunnel":"未配置检测","Uu":"未核验","AuthenticationStage":"Unknown",
                "AuthenticationFailure":"None","AuthenticationPersistentState":"Ready"}}"#
        )
    }

    fn temp_file(name: &str, content: &str) -> std::path::PathBuf {
        let directory = std::env::temp_dir().join(format!(
            "autonet-status-test-{}-{}",
            std::process::id(),
            name
        ));
        let _ = std::fs::create_dir_all(&directory);
        let path = directory.join("status.json");
        let mut file = std::fs::File::create(&path).unwrap();
        file.write_all(content.as_bytes()).unwrap();
        path
    }

    #[test]
    fn 新鲜状态为_ok_过期状态为_stale() {
        let now = 1_767_225_600_000;
        let fresh = temp_file("fresh", &status("2026-01-01T00:00:00+00:00"));
        assert_eq!(read(&fresh, now + 5_000).state, SnapshotState::Ok);
        let stale = read(&fresh, now + 60_000);
        assert_eq!(stale.state, SnapshotState::Stale);
        assert!(stale.snapshot.is_some());
    }

    #[test]
    fn 缺失损坏与未来时间都不可确认() {
        let missing = read(std::path::Path::new("definitely-not-a-status-file.json"), 0);
        assert_eq!(missing.state, SnapshotState::Missing);
        let corrupt_path = temp_file("corrupt", "{broken");
        assert_eq!(read(&corrupt_path, 0).state, SnapshotState::Corrupt);
        let future = temp_file("future", &status("2026-01-01T00:05:00+00:00"));
        assert_eq!(read(&future, 1_767_225_600_000).state, SnapshotState::Stale);
    }
}
