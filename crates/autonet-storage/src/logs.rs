//! 每日 JSONL 日志的受限尾部读取与脱敏摘要。
//!
//! 只读窗口有上限，达到上限即报告不完整；损坏行计入缺口而不是让整页失败。
//! 输出只包含 `LogSummary` 的白名单字段，`detail` 与异常原文不会离开本模块。

use autonet_contracts::{LogDates, LogPage, LogSummary};
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};

use crate::fs::classify;

pub const DEFAULT_MAX_RECORDS: usize = 300;
const INITIAL_WINDOW: u64 = 128 * 1024;
const MAX_WINDOW: u64 = 4 * 1024 * 1024;
const PREFIX: &str = "autonet-";
const SUFFIX: &str = ".jsonl";

/// 列出 `logs/` 下形如 `autonet-YYYY-MM-DD.jsonl` 的日期，最新在前。
pub fn list_dates(directory: &Path) -> LogDates {
    let entries = match std::fs::read_dir(directory) {
        Ok(entries) => entries,
        Err(error) => {
            let failure = classify(&error);
            return LogDates {
                dates: Vec::new(),
                failure: (failure != crate::ReadFailure::Missing)
                    .then(|| failure.code().to_string()),
            };
        }
    };
    let mut dates: Vec<String> = entries
        .flatten()
        .filter_map(|entry| entry.file_name().to_str().map(str::to_string))
        .filter_map(|name| {
            let date = name.strip_prefix(PREFIX)?.strip_suffix(SUFFIX)?;
            is_date(date).then(|| date.to_string())
        })
        .collect();
    dates.sort();
    dates.reverse();
    LogDates {
        dates,
        failure: None,
    }
}

/// 读取某一天日志的尾部。`date` 必须是 `YYYY-MM-DD`，不会拼接任意路径。
pub fn read_page(directory: &Path, date: &str, max_records: usize) -> LogPage {
    let max_records = max_records.max(1);
    if !is_date(date) {
        return LogPage {
            date: None,
            entries: Vec::new(),
            skipped: 0,
            complete: false,
            failure: Some("InvalidDate".into()),
        };
    }
    let path = log_path(directory, date);
    let failure_page = |code: &str| LogPage {
        date: Some(date.to_string()),
        entries: Vec::new(),
        skipped: 0,
        complete: false,
        failure: Some(code.to_string()),
    };
    let mut file = match std::fs::File::open(&path) {
        Ok(file) => file,
        Err(error) => return failure_page(classify(&error).code()),
    };
    let length = match file.metadata() {
        Ok(metadata) => metadata.len(),
        Err(error) => return failure_page(classify(&error).code()),
    };
    if length == 0 {
        return LogPage {
            date: Some(date.to_string()),
            entries: Vec::new(),
            skipped: 0,
            complete: true,
            failure: None,
        };
    }
    let mut window = INITIAL_WINDOW.min(length);
    let mut records;
    let mut skipped;
    let mut covers_file_start;
    loop {
        let start = length.saturating_sub(window);
        if file.seek(SeekFrom::Start(start)).is_err() {
            return failure_page("Unreadable");
        }
        let mut buffer = Vec::with_capacity((length - start) as usize);
        if file.read_to_end(&mut buffer).is_err() {
            return failure_page("Unreadable");
        }
        let ends_with_newline = buffer.last() == Some(&b'\n');
        let lines = split_lines(&buffer, start == 0, ends_with_newline);
        let parsed = parse_lines(&lines);
        records = parsed.0;
        skipped = parsed.1;
        covers_file_start = start == 0;
        if records.len() >= max_records || window >= MAX_WINDOW.min(length) {
            break;
        }
        window = (window * 2).min(MAX_WINDOW);
    }
    let complete = covers_file_start && records.len() < max_records;
    let mut entries: Vec<LogSummary> = records.into_iter().rev().take(max_records).collect();
    entries.shrink_to_fit();
    LogPage {
        date: Some(date.to_string()),
        entries,
        skipped,
        complete,
        failure: None,
    }
}

fn log_path(directory: &Path, date: &str) -> PathBuf {
    directory.join(format!("{PREFIX}{date}{SUFFIX}"))
}

fn is_date(date: &str) -> bool {
    let bytes = date.as_bytes();
    bytes.len() == 10
        && bytes[4] == b'-'
        && bytes[7] == b'-'
        && bytes[..4].iter().all(u8::is_ascii_digit)
        && bytes[5..7].iter().all(u8::is_ascii_digit)
        && bytes[8..].iter().all(u8::is_ascii_digit)
}

fn split_lines(bytes: &[u8], starts_at_file_start: bool, ends_with_newline: bool) -> Vec<String> {
    if bytes.is_empty() {
        return Vec::new();
    }
    let mut body = bytes;
    if !starts_at_file_start {
        match body.iter().position(|byte| *byte == b'\n') {
            Some(index) => body = &body[index + 1..],
            None => return Vec::new(),
        }
    }
    if !ends_with_newline {
        match body.iter().rposition(|byte| *byte == b'\n') {
            Some(index) => body = &body[..=index],
            None => return Vec::new(),
        }
    }
    if starts_at_file_start && body.starts_with(&[0xEF, 0xBB, 0xBF]) {
        body = &body[3..];
    }
    let text = String::from_utf8_lossy(body);
    text.split('\n')
        .map(|line| line.strip_suffix('\r').unwrap_or(line))
        .filter(|line| !line.trim().is_empty() && !line.contains('\u{FFFD}'))
        .map(str::to_string)
        .collect()
}

fn parse_lines(lines: &[String]) -> (Vec<LogSummary>, usize) {
    let mut records = Vec::new();
    let mut skipped = 0;
    for line in lines {
        match LogSummary::from_line(line) {
            Some(summary) => records.push(summary),
            None => skipped += 1,
        }
    }
    (records, skipped)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    fn temp_dir(name: &str) -> PathBuf {
        let directory =
            std::env::temp_dir().join(format!("autonet-log-test-{}-{name}", std::process::id()));
        let _ = std::fs::remove_dir_all(&directory);
        std::fs::create_dir_all(&directory).unwrap();
        directory
    }

    #[test]
    fn 尾部读取最新在前并统计损坏行() {
        let directory = temp_dir("tail");
        let path = directory.join("autonet-2026-01-01.jsonl");
        let mut file = std::fs::File::create(&path).unwrap();
        for index in 0..3 {
            writeln!(
                file,
                r#"{{"time":"2026-01-01T00:0{index}:00Z","action":"恢复决策","detail":"secret","wifi":"已连接"}}"#
            )
            .unwrap();
        }
        writeln!(file, "{{broken").unwrap();
        let page = read_page(&directory, "2026-01-01", 2);
        assert_eq!(page.failure, None);
        assert_eq!(page.skipped, 1);
        assert!(!page.complete);
        assert_eq!(page.entries.len(), 2);
        assert_eq!(page.entries[0].time_millis, 1_767_225_720_000);
        assert!(page.entries.iter().all(|entry| entry.action == "恢复决策"));
        let json = serde_json::to_string(&page).unwrap();
        assert!(!json.contains("secret"));
    }

    #[test]
    fn 日期列表忽略其他文件且拒绝路径穿越() {
        let directory = temp_dir("dates");
        std::fs::write(directory.join("autonet-2026-01-01.jsonl"), "").unwrap();
        std::fs::write(directory.join("autonet-2026-01-02.jsonl"), "").unwrap();
        std::fs::write(directory.join("other.txt"), "").unwrap();
        let dates = list_dates(&directory);
        assert_eq!(dates.dates, vec!["2026-01-02", "2026-01-01"]);
        assert_eq!(
            read_page(&directory, "../status", 10).failure.as_deref(),
            Some("InvalidDate")
        );
    }
}
