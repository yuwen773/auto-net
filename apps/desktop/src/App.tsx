import { useCallback, useEffect, useState } from "react";
import * as api from "./api";
import { LogPanel } from "./components/LogPanel";
import { ReadOnlyNotice } from "./components/ReadOnlyNotice";
import { ServicePanel } from "./components/ServicePanel";
import { StatusPanel } from "./components/StatusPanel";
import { ThemeToggle } from "./components/ThemeToggle";
import type { ContractInfo, LogView, ServiceHealth, SnapshotEnvelope } from "./types";
import {
  applyThemePreference,
  loadThemePreference,
  saveThemePreference,
  type ThemePreference,
} from "./theme";

export default function App() {
  const host = api.isTauriHost();
  const [envelope, setEnvelope] = useState<SnapshotEnvelope | null>(null);
  const [health, setHealth] = useState<ServiceHealth | null>(null);
  const [contract, setContract] = useState<ContractInfo | null>(null);
  const [logs, setLogs] = useState<LogView | null>(null);
  const [selectedDate, setSelectedDate] = useState<string | null>(null);
  const [theme, setTheme] = useState<ThemePreference>(() => loadThemePreference());
  const [statusError, setStatusError] = useState<string | null>(null);
  const [logError, setLogError] = useState<string | null>(null);
  const [loadingLogs, setLoadingLogs] = useState(false);

  const refreshStatus = useCallback(async () => {
    try {
      const [snapshot, service] = await Promise.all([api.getSnapshot(), api.getServiceHealth()]);
      setEnvelope(snapshot);
      setHealth(service);
      setStatusError(null);
    } catch {
      setEnvelope(null);
      setHealth(null);
      setStatusError("无法读取本机只读数据。请确认预览应用完整启动，或稍后重试。");
    }
  }, []);

  const refreshLogs = useCallback(async (date: string | null) => {
    setLoadingLogs(true);
    try {
      const view = await api.listLogSummaries(date, 200);
      setLogs(view);
      setLogError(null);
      if (date === null) setSelectedDate(view.page.date);
    } catch {
      setLogs(null);
      setLogError("日志目录无法读取；列表不会沿用上一次的内容。");
    } finally {
      setLoadingLogs(false);
    }
  }, []);

  useEffect(() => {
    if (!host) return;
    void refreshStatus();
    const timer = window.setInterval(() => void refreshStatus(), 2000);
    return () => window.clearInterval(timer);
  }, [host, refreshStatus]);

  useEffect(() => {
    if (!host) return;
    void api
      .getSupportedContractVersion()
      .then(setContract)
      .catch(() => setContract(null));
    void refreshLogs(null);
  }, [host, refreshLogs]);

  useEffect(() => {
    applyThemePreference(theme);
    if (theme !== "system" || typeof window.matchMedia !== "function") return;
    const query = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = () => applyThemePreference("system");
    query.addEventListener?.("change", onChange);
    return () => query.removeEventListener?.("change", onChange);
  }, [theme]);

  const changeTheme = (next: ThemePreference) => {
    setTheme(next);
    saveThemePreference(next);
  };

  if (!host) {
    return (
      <main className="blocked">
        <p className="blocked__badge">只读预览</p>
        <h1>请在桌面预览中打开</h1>
        <p className="muted">
          浏览器页面不会读取本机状态或日志。请运行 <code>AutoNet Preview</code>（Tauri 桌面应用）。
        </p>
      </main>
    );
  }

  return (
    <div className="shell">
      <header className="topbar">
        <div className="brand">
          <span className="brand__mark" aria-hidden="true">AN</span>
          <div>
            <h1 className="brand__title">AutoNet Preview</h1>
            <p className="brand__subtitle">只读预览 · 正式托盘仍为 AutoNet.Tray.exe</p>
          </div>
        </div>
        <div className="topbar__actions">
          <span className="badge badge--readonly">只读</span>
          <ThemeToggle value={theme} onChange={changeTheme} />
          <button type="button" className="button" onClick={() => void refreshStatus()}>
            刷新状态
          </button>
        </div>
      </header>

      <main className="layout">
        <StatusPanel envelope={envelope} />
        <div className="side">
          <ServicePanel health={health} contract={contract} error={statusError} />
          <ReadOnlyNotice />
        </div>
        <LogPanel
          view={logs}
          selectedDate={selectedDate}
          loading={loadingLogs}
          error={logError}
          onSelectDate={(date) => {
            setSelectedDate(date);
            void refreshLogs(date);
          }}
          onRefresh={() => void refreshLogs(selectedDate)}
        />
      </main>

      <footer className="footer">
        <span>协议 v{contract?.protocolVersion ?? api.EXPECTED_CONTRACT_VERSION} · {contract?.product ?? "AutoNet Preview"}</span>
        <span className="muted">关闭窗口只隐藏；退出预览不会停止后台服务，也不会修改开机自启。</span>
      </footer>
    </div>
  );
}
