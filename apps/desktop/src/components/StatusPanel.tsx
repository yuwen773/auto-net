import type { SnapshotEnvelope } from "../types";
import {
  actionLabel,
  authStateLabel,
  failureLabel,
  formatClock,
  formatSeconds,
  formatTime,
  SNAPSHOT_STATE_LABELS,
  snapshotGuidance,
  stageLabel,
  statusTone,
  submissionBudgetLabel,
  submissionRetryLabel,
  submissionSummary,
  uuLabel,
} from "../format";

export function StatusPanel(props: { envelope: SnapshotEnvelope | null }) {
  const { envelope } = props;
  if (!envelope) {
    return (
      <section className="panel panel--status" aria-labelledby="status-heading">
        <h2 id="status-heading" className="panel__title">网络状态</h2>
        <p className="muted" role="status">正在读取状态…</p>
      </section>
    );
  }
  const view = envelope.snapshot;
  const tone = view ? statusTone(view.state, view.direct) : "neutral";
  return (
    <section className={`panel panel--status panel--${tone}`} aria-labelledby="status-heading">
      <div className="panel__header">
        <h2 id="status-heading" className="panel__title">网络状态</h2>
        <span className={`badge badge--${envelope.state}`}>{SNAPSHOT_STATE_LABELS[envelope.state]}</span>
      </div>
      {view ? (
        <>
          <div className="hero">
            <span className={`hero__dot hero__dot--${tone}`} aria-hidden="true" />
            <div>
              <p className="hero__state">{view.state}</p>
              <p className="muted hero__time">
                服务写入于 {formatTime(envelope.updatedAtMillis)}
                {envelope.state === "stale" ? "（已过期）" : ""}
              </p>
            </div>
          </div>
          {envelope.state !== "ok" ? (
            <p className="notice notice--warn" role="alert">{snapshotGuidance(envelope.state)}</p>
          ) : null}
          {view.portalLayoutSuspected ? (
            <p className="notice notice--warn">门户结构疑似变化，自动认证已安全暂停；请使用正式诊断页。</p>
          ) : null}
          <dl className="facts">
            <Fact label="Wi-Fi" value={view.wifi} />
            <Fact label="外网检查" value={view.direct} />
            <Fact label="门户认证" value={view.portal} />
            <Fact label="自动恢复" value={view.enabled ? "已启用" : "已暂停"} />
            <Fact label="系统路径" value={view.tunnel} />
            <Fact label="UU" value={uuLabel(view.uu)} />
            <Fact label="本轮自动动作" value={`${view.attempts} 次`} />
            <Fact
              label="主动断线"
              value={`${view.disconnectAttempts} / ${view.disconnectBudgetMax} 次${
                view.disconnectRequestUnconfirmed ? "（上次请求未确认）" : ""
              }`}
            />
            <Fact
              label="提交额度"
              value={`${submissionSummary(view)} · ${submissionBudgetLabel(view.submissionBudgetCode)}`}
            />
            <Fact label="提交判定" value={submissionRetryLabel(view.submissionRetryCode)} />
            <Fact
              label="认证诊断"
              value={`${stageLabel(view.authenticationStage)} · ${failureLabel(view.authenticationFailure)}${
                view.authenticationHttpStatus !== null ? ` · HTTP ${view.authenticationHttpStatus}` : ""
              }`}
            />
            <Fact label="持久认证" value={authStateLabel(view.authenticationPersistentState)} />
            <Fact
              label="下次动作"
              value={`${actionLabel(view.nextAttemptAction)}${
                view.nextAttemptMillis !== null ? ` · ${formatClock(view.nextAttemptMillis)}` : ""
              }`}
            />
            <Fact
              label="最近恢复"
              value={
                view.lastRecoverySeconds !== null
                  ? `${formatSeconds(view.lastRecoverySeconds)} · ${formatClock(view.lastRecoveryAtMillis)}`
                  : "—"
              }
            />
            <Fact
              label="最近检测"
              value={`${formatClock(envelope.updatedAtMillis)}${
                view.cycleMilliseconds !== null ? ` · ${view.cycleMilliseconds.toFixed(0)} ms` : ""
              }`}
            />
          </dl>
        </>
      ) : (
        <>
          <p className={`notice notice--${envelope.state === "denied" ? "bad" : "warn"}`} role="alert">
            {snapshotGuidance(envelope.state)}
          </p>
          <p className="muted">
            受控原因码：<code>{envelope.reason ?? "Unknown"}</code>
          </p>
        </>
      )}
    </section>
  );
}

function Fact(props: { label: string; value: string }) {
  return (
    <div className="fact">
      <dt>{props.label}</dt>
      <dd>{props.value}</dd>
    </div>
  );
}
