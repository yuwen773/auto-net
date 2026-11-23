use crate::ReadFailure;
use std::io::ErrorKind;
use std::path::Path;

/// 以共享读方式读取文本；与 C# `Storage.ReadShared` 一样允许写入方继续持有句柄。
pub fn read_text(path: &Path) -> Result<String, ReadFailure> {
    match std::fs::read(path) {
        Ok(bytes) => Ok(String::from_utf8_lossy(&bytes).into_owned()),
        Err(error) => Err(classify(&error)),
    }
}

pub fn classify(error: &std::io::Error) -> ReadFailure {
    match error.kind() {
        ErrorKind::NotFound => ReadFailure::Missing,
        ErrorKind::PermissionDenied => ReadFailure::Denied,
        _ => ReadFailure::Unreadable,
    }
}
