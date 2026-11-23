import type { LogView } from "../types";
import {
  failureLabel,
  formatClock,
  formatSeconds,
  logGapNote,
  logScopeNote,
  logStatusText,
  stageLabel,
} from "../format";

export function LogPanel(props: {
  view: LogView | null;
  selectedDate: string | null;
  loading: boolean;
  /** 调用失败时的受控提示；与页面内的 `page.failure` 分开显示。 */
  error: string | null;
  onSelectDate: (date: string) => void;
  onRefresh: () => void;
}) {
  const { view, selectedDate, loading, error } = props;
  const page = view?.page ?? null;
  const dates = view?.dates.dates ?? [];
  return (
    <section className="panel panel--logs" aria-labelledby="logs-heading">
      <div className="panel__header">
        <h2 id="logs-heading" className="panel__title">日志摘要</h2>
        <div className="panel__actions">
          <label className="field-inline">
            <span>日期</span>
            <select
              value={selectedDate ?? ""}
              onChange={(event) => props.onSelectDate(event.target.value)}
              disabled={dates.length === 0}
            >
              {dates.length === 0 ? <option value="">暂无日志</option> : null}
              {dates.map((date) => (
                <option key={date} value={date}>{date}</option>
              ))}
            </select>
          </label>
          <button type="button" className="button" onClick={props.onRefresh} disabled={loading}>
            {loading ? "读取中…" : "刷新"}
          </button>
        </div>
      </div>
      <p className="muted small">
        只显示受控白名单字段；异常原文、账号、网卡与网络标识不会进入预览。
      </p>
      {error ? <p className="notice notice--bad" role="alert">{error}</p> : null}
      {view?.dates.failure ? (
        <p className="notice notice--bad" role="alert">日志目录无法读取（受控原因：{view.dates.failure}）。</p>
      ) : null}
      {!page ? (
        <p className="muted" role="status">正在读取日志…</p>
      ) : page.failure ? (
        <p className="notice notice--bad" role="alert">
          日志读取失败（受控原因：{page.failure}）；列表不会沿用上一次的内容。
        </p>
      ) : page.entries.length === 0 ? (
        <p className="muted">该日没有可显示的日志记录。</p>
      ) : (
        <ol className="log-list">
          {page.entries.map((entry, index) => (
            <li key={`${entry.timeMillis}-${index}`} className="log-row">
              <time className="log-row__time" dateTime={new Date(entry.timeMillis).toISOString()}>
                {formatClock(entry.timeMillis)}
              </time>
              <span className="log-row__action">{entry.action}</span>
              <span className="log-row__status">{logStatusText(entry)}</span>
              <span className="log-row__meta">
                {entry.seconds !== null ? `观测 ${formatSeconds(entry.seconds)}` : ""}
                {entry.attempts !== null ? ` · 动作 ${entry.attempts} 次` : ""}
                {entry.authenticationStage ? ` · ${stageLabel(entry.authenticationStage)}` : ""}
                {entry.authenticationFailure ? ` · ${failureLabel(entry.authenticationFailure)}` : ""}
                {entry.eventCode ? ` · ${entry.eventCode}` : ""}
              </span>
            </li>
          ))}
        </ol>
      )}
      {page ? (
        <div className="log-notes">
          <p className="muted small">{logScopeNote(page)}</p>
          {logGapNote(page) ? <p className="notice notice--warn small">{logGapNote(page)}</p> : null}
        </div>
      ) : null}
    </section>
  );
}
