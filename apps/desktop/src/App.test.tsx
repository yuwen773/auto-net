import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import App from "./App";
import * as api from "./api";
import type { LogPage, LogView, SnapshotEnvelope, StatusView } from "./types";

vi.mock("./api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./api")>();
  return {
    ...actual,
    isTauriHost: vi.fn(() => true),
    getSupportedContractVersion: vi.fn(),
    getSnapshot: vi.fn(),
    getServiceHealth: vi.fn(),
    listLogSummaries: vi.fn(),
  };
});

const mocked = {
  isTauriHost: vi.mocked(api.isTauriHost),
  contract: vi.mocked(api.getSupportedContractVersion),
  snapshot: vi.mocked(api.getSnapshot),
  health: vi.mocked(api.getServiceHealth),
  logs: vi.mocked(api.listLogSummaries),
};

function view(overrides: Partial<StatusView> = {}): StatusView {
  return {
    state: "校园网在线",
    wifi: "已连接",
    direct: "外网可达",
    portal: "未查询（外网检查已通过）",
    tunnel: "系统路径可达（路由样本推断为 Wi-Fi，出口未核验）",
    uu: "running",
    enabled: true,
    attempts: 1,
    disconnectAttempts: 0,
    disconnectBudgetMax: 3,
    disconnectCooldownSeconds: 120,
    disconnectResetAfterSeconds: 60,
    disconnectRequestUnconfirmed: false,
    outageSinceMillis: null,
    nextAttemptMillis: null,
    nextAttemptAction: "None",
    lastRecoverySeconds: 12.5,
    lastRecoveryAtMillis: 1_767_225_600_000,
    cycleMilliseconds: 180,
    slowestStage: "InternetProbe",
    slowestStageMilliseconds: 150,
    updatedAtMillis: 1_767_225_600_000,
    authenticationStage: "Unknown",
    authenticationFailure: "None",
    authenticationPersistentState: "Ready",
    authenticationHttpStatus: null,
    credentialRequestStarted: false,
    credentialsBlocked: false,
    credentialsAvailable: true,
    settingsAvailable: true,
    submissionBudgetCode: "Available",
    submissionRetryCode: "Authorized",
    submissionAttempts: 1,
    submissionAttemptMax: 3,
    submissionBudgetExhausted: false,
    submissionNextPossibleAtMillis: null,
    submissionCooldownSeconds: 120,
    wifiLinkCode: "Ready",
    portalLayoutSuspected: false,
    notificationCode: null,
    notificationAtMillis: null,
    ...overrides,
  };
}

function envelope(state: SnapshotEnvelope["state"], snapshot: StatusView | null, reason: string | null = null): SnapshotEnvelope {
  return {
    state,
    snapshot,
    updatedAtMillis: snapshot?.updatedAtMillis ?? null,
    readAtMillis: 1_767_225_600_000,
    reason,
  };
}

function logView(page: Partial<LogPage> = {}): LogView {
  return {
    dates: { dates: ["2026-01-01"], failure: null },
    page: {
      date: "2026-01-01",
      entries: [
        {
          timeMillis: 1_767_225_600_000,
          action: "恢复决策",
          wifi: "已连接",
          portal: "需认证",
          direct: "外网不可达或未知",
          seconds: null,
          attempts: 2,
          disconnectAttempts: 0,
          authenticationStage: "Status",
          authenticationFailure: null,
          eventCode: "RecoveryDecision",
          notificationCode: null,
        },
      ],
      skipped: 0,
      complete: true,
      failure: null,
      ...page,
    },
  };
}

beforeEach(() => {
  mocked.isTauriHost.mockReturnValue(true);
  mocked.contract.mockResolvedValue({ protocolVersion: 1, product: "AutoNet Preview", readOnly: true, appVersion: "0.1.0" });
  mocked.snapshot.mockResolvedValue(envelope("ok", view()));
  mocked.health.mockResolvedValue({ installed: true, state: "Running", checkedAtMillis: 1_767_225_600_000 });
  mocked.logs.mockResolvedValue(logView());
  window.localStorage.clear();
});

afterEach(() => {
  vi.clearAllMocks();
  document.documentElement.dataset.theme = "light";
});

describe("只读预览", () => {
  it("展示在线状态、服务状态与脱敏日志，且没有写操作入口", async () => {
    render(<App />);
    expect(await screen.findByText("校园网在线")).toBeInTheDocument();
    expect(await screen.findByText("正在运行")).toBeInTheDocument();
    expect(await screen.findByText("恢复决策")).toBeInTheDocument();
    expect(screen.getByText("只读")).toBeInTheDocument();
    expect(screen.getByText(/异常原文、账号、网卡与网络标识不会进入预览/)).toBeInTheDocument();
    for (const label of ["保存", "导出", "解除", "断网", "打开门户"]) {
      expect(screen.queryByRole("button", { name: new RegExp(label) })).toBeNull();
    }
  });

  it("状态文件缺失时给出明确指引而不是空白页", async () => {
    mocked.snapshot.mockResolvedValue(envelope("missing", null, "Missing"));
    render(<App />);
    expect(await screen.findByText(/status\.json 不存在/)).toBeInTheDocument();
    expect(screen.getByText(/受控原因码/)).toHaveTextContent("Missing");
  });

  it("状态损坏时显示受控原因且不猜测字段", async () => {
    mocked.snapshot.mockResolvedValue(envelope("corrupt", null, "MissingRequiredFields"));
    render(<App />);
    expect((await screen.findAllByText(/无法解析/)).length).toBeGreaterThan(0);
    expect(screen.getByText("MissingRequiredFields")).toBeInTheDocument();
  });

  it("状态过期时继续显示内容但明确标注", async () => {
    mocked.snapshot.mockResolvedValue(envelope("stale", view()));
    render(<App />);
    expect(await screen.findByText("校园网在线")).toBeInTheDocument();
    expect(screen.getByText(/可能已经过时/)).toBeInTheDocument();
  });

  it("日志缺口与读取失败都显式披露", async () => {
    mocked.logs.mockResolvedValue(
      logView({ skipped: 3, complete: false, entries: [] }),
    );
    const { unmount } = render(<App />);
    await waitFor(() => expect(screen.getByText(/仅当日最新部分/)).toBeInTheDocument());
    expect(screen.getByText(/3 条损坏记录被跳过/)).toBeInTheDocument();
    unmount();
    mocked.logs.mockResolvedValue(logView({ failure: "Denied", entries: [] }));
    render(<App />);
    const failures = await screen.findAllByText(/日志读取失败/);
    expect(failures.some((node) => node.textContent?.includes("Denied"))).toBe(true);
  });

  it("主题切换立即生效并持久化", async () => {
    const user = userEvent.setup();
    render(<App />);
    await screen.findByText("校园网在线");
    await user.click(screen.getByRole("button", { name: "深色" }));
    expect(document.documentElement.dataset.theme).toBe("dark");
    expect(window.localStorage.getItem("autonet.preview.theme")).toBe("dark");
    await user.click(screen.getByRole("button", { name: "浅色" }));
    expect(document.documentElement.dataset.theme).toBe("light");
  });

  it("浏览器环境不读取任何本机数据", async () => {
    mocked.isTauriHost.mockReturnValue(false);
    render(<App />);
    expect(await screen.findByText("请在桌面预览中打开")).toBeInTheDocument();
    expect(mocked.snapshot).not.toHaveBeenCalled();
    expect(mocked.logs).not.toHaveBeenCalled();
  });

  it("读取失败时保留受控提示而不是清空成成功状态", async () => {
    mocked.snapshot.mockRejectedValue(new Error("boom"));
    render(<App />);
    expect(await screen.findByText(/无法读取本机只读数据/)).toBeInTheDocument();
  });

  it("刷新失败时清除旧状态，避免把陈旧内容显示成功", async () => {
    const user = userEvent.setup();
    mocked.snapshot.mockResolvedValueOnce(envelope("ok", view()));
    render(<App />);
    await screen.findByText("校园网在线");
    mocked.snapshot.mockRejectedValue(new Error("boom"));
    await user.click(screen.getByRole("button", { name: "刷新状态" }));
    expect(await screen.findByText(/无法读取本机只读数据/)).toBeInTheDocument();
    expect(screen.queryByText("校园网在线")).toBeNull();
  });

  it("日志刷新失败时清空旧列表", async () => {
    const user = userEvent.setup();
    render(<App />);
    await screen.findByText("恢复决策");
    mocked.logs.mockRejectedValue(new Error("boom"));
    await user.click(screen.getByRole("button", { name: "刷新" }));
    expect(await screen.findByText(/列表不会沿用上一次的内容/)).toBeInTheDocument();
    expect(screen.queryByText("恢复决策")).toBeNull();
  });
});
