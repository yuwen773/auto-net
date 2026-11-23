//! AutoNet 只读预览的 Tauri 后端（P1）。
//!
//! 边界见 [ADR-0023](../../../docs/adr/0023-readonly-preview-boundary.md)：
//! - 只有四个只读命令，没有保存设置、导出诊断、解除暂停、启动认证或执行命令的入口；
//! - 数据读取全部经过 `autonet-storage` 的脱敏投影，前端拿不到文件句柄或原始 JSON；
//! - 单实例键来自独立标识 `com.autonet.preview`，与正式 `AutoNet.Tray.exe` 互斥体无关；
//! - 关闭窗口只隐藏，退出不影响 SCM 服务；不写 `HKLM Run`。

use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};

use autonet_contracts::{ContractInfo, LogDates, LogPage, SnapshotEnvelope};
use autonet_storage::{logs, status};
use serde::Serialize;
use tauri::{AppHandle, Manager};

const LOG_DIRECTORY: [&str; 2] = ["AutoNet", "logs"];
const STATUS_FILE: [&str; 2] = ["AutoNet", "status.json"];
const MAX_LOG_RECORDS: usize = 300;

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ServiceHealth {
    pub installed: bool,
    /// NotInstalled / Stopped / StartPending / StopPending / Running / ContinuePending /
    /// PausePending / Paused / AccessDenied / Unavailable
    pub state: String,
    pub checked_at_millis: i64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LogView {
    pub dates: LogDates,
    pub page: LogPage,
}

fn now_millis() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| i64::try_from(duration.as_millis()).unwrap_or(i64::MAX))
        .unwrap_or(0)
}

fn program_data() -> PathBuf {
    std::env::var_os("ProgramData")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from(r"C:\ProgramData"))
}

fn status_path() -> PathBuf {
    let mut path = program_data();
    for segment in STATUS_FILE {
        path.push(segment);
    }
    path
}

fn log_directory() -> PathBuf {
    let mut path = program_data();
    for segment in LOG_DIRECTORY {
        path.push(segment);
    }
    path
}

/// 协议与产品身份；前端用它判断是否兼容，只读路径不因版本不同而放宽。
#[tauri::command]
fn get_supported_contract_version(app: AppHandle) -> ContractInfo {
    ContractInfo::current(app.package_info().version.to_string())
}

/// `status.json` 的脱敏投影；缺失、损坏、拒绝访问与过期都是显式状态。
#[tauri::command]
fn get_snapshot() -> SnapshotEnvelope {
    status::read(&status_path(), now_millis())
}

/// SCM 中 `AutoNet` 服务的只读状态。绝不启动、停止或配置服务。
#[tauri::command]
fn get_service_health() -> ServiceHealth {
    service::query(now_millis())
}

/// 日期列表与某一天的受限尾部摘要；`date` 不是合法日期时返回受控失败。
#[tauri::command]
fn list_log_summaries(date: Option<String>, limit: Option<usize>) -> LogView {
    let directory = log_directory();
    let dates = logs::list_dates(&directory);
    let selected = date
        .filter(|candidate| dates.dates.iter().any(|known| known == candidate))
        .or_else(|| dates.dates.first().cloned());
    let page = match selected {
        Some(date) => logs::read_page(
            &directory,
            &date,
            limit.unwrap_or(MAX_LOG_RECORDS).clamp(1, MAX_LOG_RECORDS),
        ),
        None => LogPage {
            date: None,
            entries: Vec::new(),
            skipped: 0,
            complete: true,
            failure: None,
        },
    };
    LogView { dates, page }
}

#[cfg(windows)]
mod service {
    use windows_sys::Win32::Foundation::{
        GetLastError, ERROR_ACCESS_DENIED, ERROR_SERVICE_DOES_NOT_EXIST,
    };
    use windows_sys::Win32::System::Services::{
        CloseServiceHandle, OpenSCManagerW, OpenServiceW, QueryServiceStatusEx, SC_MANAGER_CONNECT,
        SC_STATUS_PROCESS_INFO, SERVICE_CONTINUE_PENDING, SERVICE_PAUSED, SERVICE_PAUSE_PENDING,
        SERVICE_QUERY_STATUS, SERVICE_RUNNING, SERVICE_START_PENDING, SERVICE_STATUS_PROCESS,
        SERVICE_STOPPED, SERVICE_STOP_PENDING,
    };

    use super::ServiceHealth;

    pub fn query(checked_at_millis: i64) -> ServiceHealth {
        let unavailable = |state: &str| ServiceHealth {
            // 查询失败或拒绝访问时不能断言“已安装”；只有成功查询才置 true。
            installed: false,
            state: state.to_string(),
            checked_at_millis,
        };
        let name: Vec<u16> = "AutoNet".encode_utf16().chain(std::iter::once(0)).collect();
        // SAFETY: 只打开 SCM 的连接句柄；所有句柄在下方按路径关闭。
        let manager =
            unsafe { OpenSCManagerW(std::ptr::null(), std::ptr::null(), SC_MANAGER_CONNECT) };
        if manager.is_null() {
            return unavailable("Unavailable");
        }
        // SAFETY: 服务名以 NUL 结尾；只请求查询状态权限。
        let handle = unsafe { OpenServiceW(manager, name.as_ptr(), SERVICE_QUERY_STATUS) };
        if handle.is_null() {
            let error = unsafe { GetLastError() };
            unsafe { CloseServiceHandle(manager) };
            return if error == ERROR_SERVICE_DOES_NOT_EXIST {
                unavailable("NotInstalled")
            } else if error == ERROR_ACCESS_DENIED {
                unavailable("AccessDenied")
            } else {
                unavailable("Unavailable")
            };
        }
        // SAFETY: 缓冲区大小取自结构体本身；失败时结构体保持零值且不会被读取。
        let mut status: SERVICE_STATUS_PROCESS = unsafe { std::mem::zeroed() };
        let mut needed = 0u32;
        let size = std::mem::size_of::<SERVICE_STATUS_PROCESS>() as u32;
        // SAFETY: 缓冲区与长度匹配，句柄在本分支内有效。
        let ok = unsafe {
            QueryServiceStatusEx(
                handle,
                SC_STATUS_PROCESS_INFO,
                &mut status as *mut _ as *mut u8,
                size,
                &mut needed,
            )
        };
        unsafe {
            CloseServiceHandle(handle);
            CloseServiceHandle(manager);
        }
        if ok == 0 {
            return unavailable("Unavailable");
        }
        ServiceHealth {
            installed: true,
            state: map_state(status.dwCurrentState).to_string(),
            checked_at_millis,
        }
    }

    fn map_state(state: u32) -> &'static str {
        match state {
            SERVICE_STOPPED => "Stopped",
            SERVICE_START_PENDING => "StartPending",
            SERVICE_STOP_PENDING => "StopPending",
            SERVICE_RUNNING => "Running",
            SERVICE_CONTINUE_PENDING => "ContinuePending",
            SERVICE_PAUSE_PENDING => "PausePending",
            SERVICE_PAUSED => "Paused",
            _ => "Unavailable",
        }
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn 服务状态映射覆盖_scm_取值() {
            assert_eq!(map_state(SERVICE_RUNNING), "Running");
            assert_eq!(map_state(SERVICE_STOPPED), "Stopped");
            assert_eq!(map_state(SERVICE_START_PENDING), "StartPending");
            assert_eq!(map_state(u32::MAX), "Unavailable");
        }
    }
}

#[cfg(not(windows))]
mod service {
    use super::ServiceHealth;

    pub fn query(checked_at_millis: i64) -> ServiceHealth {
        ServiceHealth {
            installed: false,
            state: "Unavailable".to_string(),
            checked_at_millis,
        }
    }
}

fn show_main(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
}

fn setup_tray(app: &AppHandle) -> tauri::Result<()> {
    use tauri::menu::{Menu, MenuItem};
    use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};

    let show = MenuItem::with_id(app, "show", "显示主窗口", true, None::<&str>)?;
    let quit = MenuItem::with_id(app, "quit", "退出预览", true, None::<&str>)?;
    let menu = Menu::with_items(app, &[&show, &quit])?;
    let mut builder = TrayIconBuilder::with_id("preview-tray")
        .menu(&menu)
        .show_menu_on_left_click(false)
        .tooltip("AutoNet Preview（只读）")
        .on_menu_event(|app, event| match event.id.as_ref() {
            "show" => show_main(app),
            "quit" => app.exit(0),
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            } = event
            {
                show_main(tray.app_handle());
            }
        });
    if let Some(icon) = app.default_window_icon() {
        builder = builder.icon(icon.clone());
    }
    builder.build(app)?;
    Ok(())
}

/// 启动预览应用。`single-instance` 必须最先注册，重复启动只唤起已有窗口。
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            show_main(app);
        }))
        .invoke_handler(tauri::generate_handler![
            get_supported_contract_version,
            get_snapshot,
            get_service_health,
            list_log_summaries
        ])
        .setup(|app| {
            setup_tray(app.handle())?;
            Ok(())
        })
        .on_window_event(|window, event| {
            if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                api.prevent_close();
                let _ = window.hide();
            }
        })
        .run(tauri::generate_context!())
        .expect("AutoNet Preview 启动失败");
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn 日志记录上限保持在预览范围() {
        assert_eq!(MAX_LOG_RECORDS, 300);
    }
}
