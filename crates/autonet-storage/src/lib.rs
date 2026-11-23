//! 只读存储解释（P0/P1）。
//!
//! 这里实现“读取不迁移”：历史认证记录、提交额度、状态文件与日志尾部只被解释，
//! 从不重写。所有写路径仍属于 C# 服务；预览应用只调用本 crate 的只读函数。

#![deny(unsafe_code)]

pub mod auth_block;
pub mod fs;
pub mod logs;
pub mod status;
pub mod submission_budget;

#[cfg(all(windows, feature = "dpapi"))]
pub mod dpapi;

/// 受控读取错误码，绝不携带异常原文或路径。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ReadFailure {
    Missing,
    Denied,
    Unreadable,
}

impl ReadFailure {
    pub fn code(self) -> &'static str {
        match self {
            Self::Missing => "Missing",
            Self::Denied => "Denied",
            Self::Unreadable => "Unreadable",
        }
    }
}
