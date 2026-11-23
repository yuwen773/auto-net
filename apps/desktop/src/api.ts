import { invoke } from "@tauri-apps/api/core";
import type {
  ContractInfo,
  LogView,
  ServiceHealth,
  SnapshotEnvelope,
} from "./types";

export const EXPECTED_CONTRACT_VERSION = 1;

/** 只有 Tauri 宿主内才能读取本机只读数据；浏览器预览不提供任何退路。 */
export function isTauriHost(): boolean {
  return typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;
}

async function call<T>(command: string, args?: Record<string, unknown>): Promise<T> {
  if (!isTauriHost()) throw new Error("preview-unavailable");
  return invoke<T>(command, args);
}

export const getSupportedContractVersion = () => call<ContractInfo>("get_supported_contract_version");
export const getSnapshot = () => call<SnapshotEnvelope>("get_snapshot");
export const getServiceHealth = () => call<ServiceHealth>("get_service_health");
export const listLogSummaries = (date: string | null, limit = 200) =>
  call<LogView>("list_log_summaries", { date, limit });
