# 跨语言黄金夹具

这些文件是 P0 的兼容基线（[ADR-0022](../../docs/adr/0022-tauri-rust-gradual-migration.md)）。金标准是当前 `main` 的 C# 行为，不是某份手写 schema。

| 文件 | 内容 | 生产者 |
|---|---|---|
| `recovery-policy.json` | `RecoveryPolicy` 的观察序列 → 动作、原因、退避与断线额度 | `AutoNet.Tests --export-golden-fixtures` |
| `authentication-block.json` | `auth-block.txt` 历史/当前形态 → 记录分类与阻断判定 | 同上 |
| `submission-budget.json` | `submission-budget.json` → `Missing/Valid/Invalid` 与字段 | 同上 |
| `status-log-projection.json` | C# `StatusSnapshot`/`ActivityLog` 真实序列化结果与脱敏投影期望（含敏感哨兵） | 同上 |
| `dpapi-credential.json` | `LocalMachine` 已发布密文、明文摘要与长度（不含可读密码） | 同上（需 `--dpapi`） |
| `install-lifecycle.json` | 服务定义、安装路径、ACL、自启与卸载保留契约 | 手工维护，来源 `scripts/install.ps1` |

## 重新生成

```powershell
dotnet tests/AutoNet.Tests/bin/Release/net10.0-windows/AutoNet.Tests.dll --export-golden-fixtures fixtures/cross-language --dpapi
```

`manifest.json` 不含时间戳，同一提交重复导出应字节一致；`dpapi-credential.json` 因 DPAPI 随机化，只在刷新夹具时重写。

## 消费方

- `crates/autonet-core/tests/recovery_golden.rs`
- `crates/autonet-contracts/tests/projection_golden.rs`
- `crates/autonet-storage/tests/state_golden.rs`
- `scripts/qa-crosslang-parity.ps1`：重新导出到临时目录并与仓库文件逐字节比对（`dpapi-credential.json` 除外），再运行 Rust 测试。

DPAPI 解密用例只允许在同机执行：先设置 `AUTONET_DPAPI_SAME_MACHINE=1`。其他机器必须跳过，跨机解密失败不能说明兼容性。
