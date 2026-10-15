# 兼容原型 go/no-go：Avalonia 托盘、UAC、单文件发布与安装契约

Status: 工程验证通过（8/8 项 QA 步骤；真实 UAC 交互与真实安装仍待人工/授权步骤）
Tracker: https://github.com/yuwen773/auto-net/issues/2
Date: 2026-10-15（Asia/Shanghai）
基线：`codex/ui-avalonia` 分支提交 `26eedd3`（本记录对应的代码修订）；Avalonia 12.1.4 + 官方 Fluent 主题（.NET 10，win-x64 自包含）

本记录是迁移工单 #2 的产出，对应 [UI-MIGRATION-SPEC.md](UI-MIGRATION-SPEC.md) 验收矩阵 T17 的前置关卡：先用最小原型验证托盘、UAC、单文件发布与安装文件布局，再扩大至完整迁移。**本票未安装、未替换、未重启任何服务**；服务与已安装托盘在全部核验前后保持同一进程与状态（见证据）。

## 结论表

| # | 工单 #2 验收项 | 结论 | 证据 |
|---|---|---|---|
| 1 | 自包含 win-x64 单文件发布，运行不依赖外置框架文件（T17） | **go** | 单文件 `AutoNet.Tray.exe` 46.8 MB（SHA256 `B9F0FF28…5DD53A`）；仅复制该 exe 到全新目录即可运行完整生命周期（`isolated/`） |
| 2 | 托盘、隐藏启动、关闭隐藏、退出不停服务的生命周期与既有语义一致（故事 3、4、6、7） | **go** | `lifecycle.json` 四步状态：隐藏启动 → 显示 → 关闭后隐藏且进程仍存活 → 托盘退出；全程服务与已安装托盘 PID/状态不变 |
| 3 | 普通实例单例互斥与独立提权入口并存：提权操作不被普通托盘互斥锁拦截（故事 8） | **go**（附环境说明） | 实例 A 持互斥锁时：第二个普通实例退出码 3 并报告已在运行；`--settings` 未被拦截、进入提权路径（`elevated-inline.json`，参数原样保留）。本机会话为管理员，未执行真实 UAC 弹窗 |
| 4 | 官方 Fluent 浅色与深色均可渲染（故事 26、27 前置） | **go** | 真实窗口捕获 `theme-light.png` / `theme-dark.png`（含深色标题栏与主题字典切换） |
| 5 | 现有安装器复制与回滚文件布局可接纳候选程序（故事 82、83） | **go**（文件布局层） | bundle 镜像满足 `install.ps1` 前置检查；覆盖与回滚字节级校验通过；服务停止/ACL/HKLM 等步骤明确未执行（见缺口说明） |
| 6 | 产出 go/no-go 记录；无安装、无服务重启、无破坏性网络测试 | **go** | 本记录 + `artifacts/validation/ui-avalonia-prototype/qa-result.json`；`service-untouched` 前后一致 |

## 产出物

- 原型工程：`src/AutoNet.Tray.Avalonia/`（Avalonia 12.1.4，官方 Fluent；托盘图标与菜单、`--tray` 隐藏启动、关闭隐藏、退出不停服务、单实例与提权路由、明暗主题）。
- 发布产物：`artifacts/validation/ui-avalonia-prototype/publish/AutoNet.Tray.exe`（自包含 win-x64 单文件，发布身份 `AutoNet.Tray.exe` 保持安装契约；工单 #3 起由 `-p:AutoNetPublishIdentity=true` 在 csproj 内激活，替代会传播到被引用工程的 `-p:AssemblyName` 全局覆盖；ProductVersion `0.2.1+26eedd3`）。
- QA 脚本：`scripts/qa-avalonia-prototype.ps1`（复现命令见下节）。
- 回归用例：`tests/AutoNet.Tests/AvaloniaPrototypeRegression.cs`（6 项，全部通过）。
- 证据目录（被忽略的 `artifacts/`，不随提交分发）：`publish/`、`isolated/`、`runtime-a|-b|-c/`、`theme/`、`layout/`、`diagnostics/`、`qa-result.json`。

## 复现方式

```text
pwsh.exe -NoLogo -NoProfile -File scripts/qa-avalonia-prototype.ps1
```

脚本边界：只在本证据目录内复制/创建文件；只管理自身启动的进程（按路径前缀排除已安装托盘）；不安装、不重启服务、不读取业务配置。人工观察原型：直接运行 `publish/AutoNet.Tray.exe`（窗口 + 托盘图标），关闭窗口应隐藏到托盘，托盘菜单“退出托盘（后台继续运行）”结束界面而不停服务。

## 证据明细（qa-result.json，2026-10-15）

- **publish**：SDK 10.0.401；发布目录仅含 `AutoNet.Tray.exe`（49,085,455 字节）与调试符号（`*.pdb`，非运行时依赖，与 0.2.1 发布包惯例一致）。
- **isolated-run**：单 exe 复制进空目录运行，报告确认进程路径为隔离副本，隐藏启动/显示/关闭隐藏/保持运行四步状态正确，退出码 0。
- **single-instance**：实例 B 退出码 3，`already-running.json` 已写入；实例 A 不受影响。
- **elevated-entry**：`--settings` 在普通实例运行时进入管理员路径并保持参数逐字一致；`already-running.json` 未出现。
- **theme-render**：`theme-light.png` / `theme-dark.png` 为真实窗口捕获（PrintWindow），非离屏渲染。
- **installer-layout**：候选哈希 `B9F0FF28…`；按 `install.ps1` 的 `.rollback-<guid>` + `manifest.json` + 随机备份名布局复现覆盖与回滚后，恢复字节等于旧版文件（`F516CA52…`）。
- **service-untouched**：服务状态 Running、PID 21156 前后一致；已安装托盘 PID 54520 前后一致。

## 已知发现（不阻断 go）

1. **RenderTargetBitmap 离屏渲染缺陷（Avalonia 12.1.4）**：某些长中文文本在离屏渲染路径下字形步进错误（`diagnostics/render-rtb-defect.png`）；真实窗口在 GPU 与软件渲染模式下均正常（`diagnostics/render-window-gpu.png`、`render-window-software.png`）。本票的主题证据因此改用真实窗口捕获（`WindowCapture`）。**约束**：后续视觉证据必须来自真实渲染；离屏/Headless 渲染不得用于中文文本断言。
2. **原型互斥名有意不同**：原型使用 `Local\AutoNet.Tray.Avalonia`，避免与本机已安装的 WinForms 托盘（`Local\AutoNet.Tray`）相互阻塞。**约束**：候选版替换旧实现时必须沿用 `Local\AutoNet.Tray`，并验证同一会话的普通实例单实例语义。
3. **本机会话为管理员**：核验覆盖了“已提权直接执行”分支；非管理员 `runas` 请求分支由单元回归覆盖、脚本亦兼容记录分支，但真实 UAC 交互未执行（人工待验）。
4. **命令行比既有实现更严格**：同一次启动出现多个管理员操作参数时拒绝执行并明确报错（既有 WinForms 为首个匹配优先）。这是有意的防歧义取舍；安装器、自启与快捷方式等既有单一操作调用不受影响。
5. **既有回归的机器相关失败（与本票工作无关，已用 baseline `79ccc02` 对照）**：
   - `诊断包：缺少设置、状态与日志时仍导出并逐项说明缺口`：本机确定性失败，baseline 同样失败。机制已定位：用例第一轮传入 `LoadSettings = () => null` 期望“无设置”，但 `LoadUsableSettings` 的 `options.LoadSettings?.Invoke() ?? Storage.LoadSettings()` 会回退读取**本机真实安装的配置**（本机存在且校验通过），于是进入实时采集、包内多出 `net_state.json`，断言 `只应保留 README` 失败。该用例只在“无可用已保存设置”的机器上通过，属既有测试的环境依赖缺陷，待单独修复（例如由用例显式阻断真实存储回退或注入 `Collect`）。
   - `托盘加固：五万次固定标志刷新复用控件且资源稳定`：本机 60 秒预算内无法完成（baseline 同样超时），超时后残留的测试线程会使随后 UI 用例出现随机失败（不同轮次受害者不同：`两千次日志切换`、`空值、未来、过期状态…`、`日志界面按全部结构化代码筛选` 出现 GDI “Parameter is not valid.”）。本票新增 6 用例全绿；本树全量 200/203，失败项不因本票工作引入。
   - 建议：为 T17 全回归门槛单独立项修复（提高该压测预算或消除残留线程干扰、修掉诊断包用例的环境依赖）。

## 契约缺口

- **无阻断性缺口**：单文件、启动参数（`--tray`/`--settings` 等）、EXE 身份、隐藏启动与安装文件布局均可接纳候选程序。
- 明确未执行（真实安装验收层，属工单 #16）：服务停止/启动、ACL 设置、HKLM 自启写入、快捷方式创建、真实覆盖安装与回退。

## 未覆盖 / 人工待验

- 真实 UAC 提示与“拒绝 UAC”路径（需非管理员会话 + 人工点击）。
- 托盘图标在真实任务栏的可见性与菜单点击的视觉确认（可运行 `publish/AutoNet.Tray.exe` 人工查看；QA 运行期间托盘图标为程序级创建成功，非视觉验收）。
- 真实安装与回退（工单 #16，需单独授权）。
- 真实 Windows DPI 100%/125%/150%（T05 阶段）。
- 主题“跟随系统”的持久化与跨重启保留（工单 #9）；托盘菜单与完整版的差异（自动恢复开关、查看日志等）由 #4–#8 补齐，本原型保持最小集：打开、设置认证信息…、退出。

## 对后续工单的约束（由本票结论导出）

- 保留 `AutoNet.Tray.exe` 身份、`--tray` 隐藏启动、关闭隐藏与“退出不停服务”语义；候选版互斥名沿用 `Local\AutoNet.Tray`。
- 新增框架依赖必须能被自包含单文件发布接住；候选包不得遗漏外置依赖或静默更换安装方式。
- 视觉证据统一使用真实窗口捕获；离屏渲染只能作为辅助诊断。
- 完整菜单与四页界面按 #3–#9 推进，不得借迁移引入重复开页、错误解锁或凭据泄露。
