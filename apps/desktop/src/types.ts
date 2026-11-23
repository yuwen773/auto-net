export type SnapshotState = "ok" | "missing" | "corrupt" | "denied" | "stale";

export interface StatusView {
  state: string;
  wifi: string;
  direct: string;
  portal: string;
  tunnel: string;
  uu: string;
  enabled: boolean;
  attempts: number;
  disconnectAttempts: number;
  disconnectBudgetMax: number;
  disconnectCooldownSeconds: number;
  disconnectResetAfterSeconds: number;
  disconnectRequestUnconfirmed: boolean;
  outageSinceMillis: number | null;
  nextAttemptMillis: number | null;
  nextAttemptAction: string;
  lastRecoverySeconds: number | null;
  lastRecoveryAtMillis: number | null;
  cycleMilliseconds: number | null;
  slowestStage: string;
  slowestStageMilliseconds: number | null;
  updatedAtMillis: number;
  authenticationStage: string;
  authenticationFailure: string;
  authenticationPersistentState: string;
  authenticationHttpStatus: number | null;
  credentialRequestStarted: boolean;
  credentialsBlocked: boolean;
  credentialsAvailable: boolean | null;
  settingsAvailable: boolean | null;
  submissionBudgetCode: string;
  submissionRetryCode: string;
  submissionAttempts: number | null;
  submissionAttemptMax: number | null;
  submissionBudgetExhausted: boolean | null;
  submissionNextPossibleAtMillis: number | null;
  submissionCooldownSeconds: number;
  wifiLinkCode: string;
  portalLayoutSuspected: boolean;
  notificationCode: string | null;
  notificationAtMillis: number | null;
}

export interface SnapshotEnvelope {
  state: SnapshotState;
  snapshot: StatusView | null;
  updatedAtMillis: number | null;
  readAtMillis: number;
  reason: string | null;
}

export interface ContractInfo {
  protocolVersion: number;
  product: string;
  readOnly: boolean;
  appVersion: string;
}

export interface ServiceHealth {
  installed: boolean;
  state: string;
  checkedAtMillis: number;
}

export interface LogSummary {
  timeMillis: number;
  action: string;
  wifi: string;
  portal: string;
  direct: string;
  seconds: number | null;
  attempts: number | null;
  disconnectAttempts: number | null;
  authenticationStage: string | null;
  authenticationFailure: string | null;
  eventCode: string | null;
  notificationCode: string | null;
}

export interface LogDates {
  dates: string[];
  failure: string | null;
}

export interface LogPage {
  date: string | null;
  entries: LogSummary[];
  skipped: number;
  complete: boolean;
  failure: string | null;
}

export interface LogView {
  dates: LogDates;
  page: LogPage;
}
