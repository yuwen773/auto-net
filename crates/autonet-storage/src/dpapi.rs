//! Windows DPAPI（LocalMachine）只读解密。
//!
//! 仅用于 P3 的“已发布密文同机只读解密”验收与 P0 夹具校验；本 crate 不提供加密、
//! 不写凭据文件。明文只存在于调用方内存，调用方负责及时清零与不落日志。

#![allow(unsafe_code)]

use std::ffi::c_void;

use windows_sys::Win32::Foundation::LocalFree;
use windows_sys::Win32::Security::Cryptography::{CryptUnprotectData, CRYPT_INTEGER_BLOB};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DpapiError {
    /// 系统调用失败（密文不可解密、跨机器或被篡改）。不携带 Win32 错误码以免泄露细节。
    Failed,
    Empty,
}

/// 解密 `DataProtectionScope.LocalMachine`、`optionalEntropy = null` 的密文。
pub fn unprotect_local_machine(ciphertext: &[u8]) -> Result<Vec<u8>, DpapiError> {
    if ciphertext.is_empty() {
        return Err(DpapiError::Empty);
    }
    let length = u32::try_from(ciphertext.len()).map_err(|_| DpapiError::Failed)?;
    let input = CRYPT_INTEGER_BLOB {
        cbData: length,
        pbData: ciphertext.as_ptr() as *mut u8,
    };
    let mut output = CRYPT_INTEGER_BLOB {
        cbData: 0,
        pbData: std::ptr::null_mut(),
    };
    // SAFETY: 输入缓冲区在调用期间有效且长度已校验；输出指针由系统分配并在下方释放。
    let ok = unsafe {
        CryptUnprotectData(
            &input,
            std::ptr::null_mut(),
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            0,
            &mut output,
        )
    };
    if ok == 0 || output.pbData.is_null() {
        return Err(DpapiError::Failed);
    }
    // SAFETY: 成功时系统返回 `cbData` 字节的有效缓冲区，复制后立即释放。
    let bytes =
        unsafe { std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec() };
    unsafe {
        LocalFree(output.pbData as *mut c_void);
    }
    Ok(bytes)
}
