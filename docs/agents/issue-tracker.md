# Issue tracker: GitHub

本仓库的规格与实施事项使用 yuwen773/auto-net 的 GitHub Issues。优先使用可用的 GitHub connector 工具；使用 gh CLI 时，正文从文件传入，保留实际换行。

- 发布规格：创建一个 GitHub issue，正文采用 to-spec 模板，添加 ready-for-agent 标签。
- 读取事项：读取正文、标签及相关评论。
- 实施事项：to-tickets 另行拆分并创建，不把规格发布当作代码实施或部署完成。
- 标签使用 triage-labels.md 的规范角色；不根据 Git 分支名猜测事项状态。
- 发布前检查不含凭据、原始门户响应或本机私有数据。

PRs as a request surface: no.
