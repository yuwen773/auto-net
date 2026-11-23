const UNAVAILABLE = [
  ["设置与账号密码", "只读预览不读取 private 目录，也不提供保存入口。"],
  ["认证诊断与诊断包导出", "需要读取受限状态并经过提权交接；请使用正式 AutoNet 托盘。"],
  ["解除认证暂停", "属于提权写操作，预览不提供入口。"],
  ["打开校园门户认证页", "门户页面不会加载到拥有 Tauri 命令能力的窗口。"],
  ["主动断网 / 重新认证", "预览不能触发任何网络动作。"],
];

export function ReadOnlyNotice() {
  return (
    <section className="panel panel--limits" aria-labelledby="limits-heading">
      <div className="panel__header">
        <h2 id="limits-heading" className="panel__title">只读边界</h2>
        <span className="badge badge--readonly">不写数据</span>
      </div>
      <p className="muted small">
        本窗口只展示状态与脱敏日志。以下能力在预览中明确不可用，正式入口仍由 AutoNet 托盘持有。
      </p>
      <ul className="limits">
        {UNAVAILABLE.map(([title, description]) => (
          <li key={title}>
            <span className="limits__title">{title}</span>
            <span className="muted"> {description}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}
