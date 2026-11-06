# Pathleap / 跃径 · M1 骨架

Windows 桌面快速定位工具（对标同类工具）的 M1 空壳：只验证 WinUI 3 唤醒性能与双进程骨架，
**不包含真实索引与搜索**。

产品名集中在 `src/Pathleap.Shared/Branding.cs` 的 `ProductId = "Pathleap"` 一处，改名只需改这一处
（互斥体名、管道名、数据目录、服务名都由它派生）。

## 目录结构

```
Pathleap/
├── src/
│   ├── Pathleap.Shared/      品牌常量、配置读写、文件日志、命名管道协议
│   ├── Pathleap.Service/     常驻索引服务（M1 只有管道 + SCM 骨架，索引 not-implemented）
│   └── Pathleap.UI/          WinUI 3 前端：托盘、全局热键、空搜索窗、唤醒基准
└── tools/                    PowerShell 脚本（全部使用 Lp 前缀变量）
```

## 构建与运行

环境：Windows 10/11 x64、.NET 8 SDK。**不需要 Visual Studio**：UI 使用
`Microsoft.WindowsAppSDK 2.6.5`（1.6 的 MSIX/PRI MSBuild 任务在本机缺失，2.6.5 可纯 CLI 编译）。

```powershell
# 编译（会先跑变量命名检查，并把服务产物复制到 UI 输出目录）
pwsh -File .\tools\Build-Lp.ps1

# 开发期启动：服务控制台进程 + UI 常驻
pwsh -File .\tools\Start-LpDev.ps1
```

- UI 可执行文件：`src\Pathleap.UI\bin\x64\Debug\net8.0-windows10.0.22621.0\Pathleap.UI.exe`
- 热键：`Ctrl+Alt+Space`（可在配置里改），托盘右键菜单可显示搜索 / 跑基准 / 打开目录 / 退出
- 单实例：`Mutex Local\Pathleap.UI`；第二个实例把 `--show/--status/--bench/--quit` 转发给主实例后退出
- 搜索窗失焦自动隐藏，Esc 隐藏，窗口 `AppWindow.Closing` 被拦截为隐藏

## 本地数据（不进仓库）

| 内容 | 路径 |
| --- | --- |
| 配置 | `%LOCALAPPDATA%\Pathleap\config.json` |
| 服务配置 | `%LOCALAPPDATA%\Pathleap\service\service.json` |
| 日志 | `%LOCALAPPDATA%\Pathleap\logs\ui-yyyyMMdd.log`、`service-yyyyMMdd.log` |
| 基准结果 | `%LOCALAPPDATA%\Pathleap\perf\wake-latency-*.csv/.json` 与 `last-bench-summary.json` |

配置为 JSON（camelCase，忽略 null，允许注释与尾逗号），首次运行自动生成；写入使用
`.tmp` + `File.Move(overwrite: true)` 的原子替换。可用环境变量 `PATHLEAP_DATA_ROOT` 覆盖数据根目录。

## 测试

```powershell
# 冒烟：进程 / 单实例 / 热键 / 两条管道 / 配置 / 日志
pwsh -File .\tools\Test-LpSmoke.ps1

# 唤醒基准（真实 SendInput 注入热键，默认 200 次）
pwsh -File .\tools\Run-LpBench.ps1 -LpSamples 200
```

基准要求当前为解锁的交互式桌面，测试期间不要碰键鼠。测量方法：
T0 取原生消息线程处理 `WM_HOTKEY` 的第一条指令（QPC），首帧取 `CompositionTarget.Rendering`
首次回调，可输入取 UI 线程 `TextBox.KeyDown` 收到 SendInput 注入的探测键。

## 版本号

版本号只有一个来源：仓库根目录 [Directory.Build.props](Directory.Build.props) 的 `PathleapVersion`
（当前 `1.0.0-alpha1`）。程序集版本、程序内显示、打包 zip 名、CI 产物名都由它派生，代码里不写第二份。

- 起始 `1.0.0`，首次推送为 `1.0.0-alpha1`；`minor` / `patch` 取值 0–10，超过 10 向前进位。
- Git tag 用 `v` 前缀（`v1.0.0-alpha1`），CI 在 tag 触发时把版本带进产物名。
- 完整规则、进位示例与「下一版是什么」见 [docs/版本号规则.md](docs/版本号规则.md)；
  升级用 `pwsh -File .\tools\Next-LpVersion.ps1 -LpBump patch -LpApply`（不带 `-LpApply` 只预览）。

## 脚本安全约定

- 所有自定义变量必须以 `Lp` 开头，且不得与 PowerShell 自动/只读变量重名；
  `tools/Test-LpVariableNames.ps1` 用 AST 检查，`Build-Lp.ps1` 会先执行它。
- 删除/清理只走 `tools/Lp.Safety.psm1` 的 `Remove-LpItemSafely`：路径白名单校验、
  `SupportsShouldProcess`（支持 `-WhatIf`）、删除前打印解析后的绝对路径，禁止对拼接路径直接递归删除。

## M1 进度

- 已完成：双进程骨架、单实例、配置、日志、托盘、全局热键、空搜索窗、唤醒基准与报告落盘
- 未做（等 M1 性能结论）：USN 索引、快速切换对话框驱动、更新模块、服务安装脚本
