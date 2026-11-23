import type { ContractInfo, ServiceHealth } from "../types";
import { EXPECTED_CONTRACT_VERSION } from "../api";
import { formatClock, serviceStateLabel } from "../format";

export function ServicePanel(props: { health: ServiceHealth | null; contract: ContractInfo | null; error: string | null }) {
  const { health, contract, error } = props;
  const running = health?.state === "Running";
  return (
    <section className="panel panel--service" aria-labelledby="service-heading">
      <div className="panel__header">
        <h2 id="service-heading" className="panel__title">后台服务</h2>
        <span className={`badge ${running ? "badge--ok" : "badge--warn"}`}>
          {health ? serviceStateLabel(health.state) : "读取中"}
        </span>
      </div>
      <dl className="facts facts--compact">
        <div className="fact">
          <dt>SCM 服务</dt>
          <dd>
            AutoNet ·{" "}
            {health
              ? health.state === "NotInstalled"
                ? "未安装"
                : health.installed
                  ? "已安装"
                  : "无法确认"
              : "—"}
          </dd>
        </div>
        <div className="fact">
          <dt>预览身份</dt>
          <dd>{contract?.product ?? "AutoNet Preview"} · 只读</dd>
        </div>
        <div className="fact">
          <dt>预览协议</dt>
          <dd>
            v{contract?.protocolVersion ?? EXPECTED_CONTRACT_VERSION}
            {contract && contract.protocolVersion !== EXPECTED_CONTRACT_VERSION ? "（与后端不一致）" : ""}
          </dd>
        </div>
        <div className="fact">
          <dt>查询时间</dt>
          <dd>{formatClock(health?.checkedAtMillis ?? null)}</dd>
        </div>
      </dl>
      {contract && contract.protocolVersion !== EXPECTED_CONTRACT_VERSION ? (
        <p className="notice notice--warn" role="alert">
          预览与后台契约版本不一致；当前只允许只读展示，写操作（未来）必须拒绝。
        </p>
      ) : null}
      {error ? <p className="notice notice--bad" role="alert">{error}</p> : null}
      <p className="muted small">退出或关闭预览不会停止 AutoNet 服务。</p>
    </section>
  );
}
