# CI 发布完整性：哈希重算、真装冒烟矩阵与 tag 自动 Release

既有 workflow 只做构建 + 回归 + 产物上传，发布物完整性与安装契约回归靠本机人工核验。决定在 CI 增加三层：push/PR 上的**完整性校验**（重算 SHA256SUMS 逐项比对、BUILD-ID 源提交与触发提交对应；校验逻辑落为本地可独立运行的脚本，CI 与本机共用同一接缝）、**真装冒烟矩阵**（GitHub Windows runner 自带管理员权限，install.ps1 的提权分支无 UAC 直接执行：安装 → 断言服务 Running / 自启 / 快捷方式 / ACL 契约 / 数据保留 → 卸载 → 断言清理干净）、tag `v*` 触发的**发布校验与自动 Release**（校验通过后创建 GitHub Release，资产 = 发布 zip + SHA256SUMS.txt + BUILD-ID.txt，正文取 tag annotation）。CI 失败为红叉事后告警，暂不开启 branch protection（保持直推习惯，出现第二贡献者再议）。
