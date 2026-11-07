# Pathleap / 跃径

Windows 桌面快速定位工具：全局热键唤出、输入即搜、结果列表键盘直达。
UI 是常驻托盘进程，索引与查询放在独立的长驻服务进程，两者只通过命名管道通信。

> 当前版本：`1.0.1`（2026-11-07）。本 README 描述的是**已经实测验证**的状态，
> 未验证的能力统一放在「已知限制与未完成项」，不会写成已达成。数据来源见每节末尾与
> [文档索引](#8-文档索引)。

## 1. 产品定位与当前状态

**定位**：面向 Windows 10/11 x64 的轻量快速定位工具。目标是在按下热键后几十毫秒内可输入，
并在本地索引上以低延迟返回文件名匹配结果。

**设计取舍**：

- UI 只负责窗口、热键、列表与键盘交互；索引构建、装载与查询全部在服务进程，避免大索引拖慢 UI 线程。
- 索引是单个只读 mmap 文件，查询零拷贝；构建放到短生命周期子进程，构建期的 GB 级临时内存不留在常驻服务里。
- 优先走 USN/MFT 主路径；拿不到卷句柄时降级目录遍历，保证功能可用。

### 已实现能力

| 能力 | 状态 | 验证来源 |
| --- | --- | --- |
| WinUI 3 唤醒、托盘、全局热键、单实例与命令行转发 | 已实现 | `docs/M1-性能实测报告.md` |
| 服务进程：命名管道、状态、索引装载/构建、查询 | 已实现 | `docs/M2-索引与搜索报告.md`、`docs/1.0.0-发布说明.md` |
| v4 索引格式：16 字节记录 + 名字前缀编码 | 已实现 | `docs/M2-索引与搜索报告.md` §10 |
| USN 主路径（`FSCTL_ENUM_USN_DATA`） | 已实测（管理员） | `docs/1.0.0-发布说明.md` §1.3 |
| 非管理员自动降级目录遍历 | 已实测 | `docs/M2-索引与搜索报告.md` §3.4 |
| 查询：精确 / 前缀 / 子串，多 token AND，按 tier 输出 | 已实现 | `docs/M2-索引与搜索报告.md` §4、§10.3 |
| 结果列表、空状态、`↑`/`↓`/`PgUp`/`PgDn`/`Enter`/`Esc` | 已实现（`Enter` 打开目标未前台验收） | `docs/M3-结果列表与键盘交互.md` |
| 便携 zip 与 Windows 安装包（快捷方式、卸载项、目录选择） | 已实现 | `docs/1.0.1-发布说明.md` |
| 界面五屏（主窗、快速切换、设置四页、检查更新四态、托盘） | 已按设计稿还原，见 [界面对照](docs/界面对照.html)、[验收报告](docs/界面验收报告.md) | `docs/shots/` |

### 未完成项（明确不做已完成表述）

- **首次运行不会自动建索引**：当前需要显式触发 `INDEX_BUILD`（见 §2.3）。UI 在没有索引时显示「索引未就绪」。
- **没有 USN 增量更新**：只实现了 USN 全量枚举；`FSCTL_READ_USN_JOURNAL` 的持续增量未接入。
- **只维护一个活跃索引文件**（单卷）；多卷合并未实现。
- **没有 SCM 服务安装**：服务由 UI 以同目录子进程拉起（`--console`），未注册为 Windows 服务。
- **更新检查已接入 GitHub Release，但镜像切换未接入**：检查、发现新版本、下载/仅下载、安装/打开目录四态可用；镜像下拉当前只有 GitHub 官方一项。
- **快速切换的文件对话框注入未接入**：浮层本身可用，数字直选/当前文件夹/键帽提示为真实逻辑，但还不能注入到资源管理器的打开/保存对话框。
- **设置页只有四页**：外观与主题、快捷键、搜索范围与索引、更新；设计稿侧栏的「常规」「关于」未做，界面上以「未接入」标注相关项，不放假控件。
- **窗口圆角跟随 Windows 11 系统圆角（约 8 DIP）**：设计稿为 14 DIP；WinUI 3 无自定义窗口圆角 API，强行区域裁剪会丢阴影与抗锯齿，故保留系统圆角并在验收报告中列为已知差异。
- **前台唤醒基准的 alpha5 重试修复未取得有效前台数据**（采集时会话锁屏）；`Enter` 打开真实目标的最后一步也未前台验收。

## 2. 安装与运行

### 2.1 两种分发形式

GitHub Release 同时提供：

| 产物 | 形式 | 适用场景 | 与另一种的差异 |
| --- | --- | --- | --- |
| `Pathleap-<版本>-win-x64.zip` | 便携版 | 解压即用、绿色部署、放到 U 盘或受控目录 | 不写注册表、不建快捷方式、不产生卸载项；升级/删除需手动覆盖或删目录 |
| `Pathleap-<版本>-win-x64-setup.exe` | 安装包（Inno Setup） | 普通用户安装 | 可选择安装目录；创建开始菜单快捷方式，桌面快捷方式为可选任务；写入当前用户卸载项；卸载时移除程序与快捷方式 |

安装包按**当前用户**安装（默认 `%LOCALAPPDATA%\Programs\Pathleap`），不需要 UAC；安装完成可选择立即启动 UI。
UI 启动后会按配置 `startServiceWithUi` 自动拉起同目录的 `Pathleap.Service.exe --console`，不需要单独启动服务。

**卸载与用户数据取舍**：

- 程序文件、开始菜单/桌面快捷方式、卸载注册项一律清除。
- 配置、日志、索引缓存（`%LOCALAPPDATA%\Pathleap`）**默认保留**：升级或重装后可以直接沿用索引与配置。
- 交互式卸载会询问「是否同时删除个人配置、日志与索引缓存」；选「是」才删除该目录。静默卸载默认保留。
- 需要彻底清理时，请先退出 Pathleap，再删除 `%LOCALAPPDATA%\Pathleap` 即可。

### 2.2 运行前提

| 项 | 要求 | 说明 |
| --- | --- | --- |
| 系统 | Windows 10/11 x64 | 安装包声明 `x64compatible` |
| .NET | .NET 8 Desktop Runtime | 当前产物是框架依赖构建，未自带运行时 |
| Windows App SDK | Windows App Runtime 2.6.5 | UI 是 unpackaged WinUI 3 应用 |
| 管理员 | **非必需** | 非管理员可正常搜索；USN 主路径需要管理员或 LocalSystem，非管理员 `auto` 会降级目录遍历 |
| 构建环境 | .NET 8 SDK（不需要 Visual Studio） | 见 §6 |

### 2.3 首次使用：需要先建索引

当前版本没有「首次运行自动建索引」的 UI 入口。建索引通过服务 IPC 或随仓库提供的脚本触发，索引文件落在
`%LOCALAPPDATA%\Pathleap\service\index\<卷>.plidx`，服务启动时自动装载最新的 `.plidx`。

```powershell
# 目录遍历建索引（不需要管理员；示例索引 C:\Windows，便于快速验证）
pwsh -File .\tools\Run-LpIndexBench.ps1 -LpBuild -LpMode walk -LpRoot 'C:\Windows' -LpIterations 1

# USN 主路径建索引（需要一次 UAC；会枚举整卷 C:）
Start-Process pwsh -Verb RunAs -ArgumentList '-NoProfile','-File','.\tools\Test-LpUsnElevated.ps1'
```

> 注意：上述脚本会把结果写入默认索引目录并覆盖同卷已有的 `.plidx`；需要保留旧索引时先备份该文件。

热键默认 `Ctrl+Alt+Space`，可在 `%LOCALAPPDATA%\Pathleap\config.json` 的 `uiHotkey` 修改。搜索窗失焦自动隐藏，
`Esc` 隐藏并清空查询，`Enter` 打开选中项（目录用资源管理器、文件用系统默认程序）。

## 3. 索引与搜索的工作方式

### 3.1 索引来源：USN 主路径 + 降级

1. `mode=usn`：打开卷句柄 `\\.\C:` 的 `GENERIC_READ`，用 `FSCTL_ENUM_USN_DATA` 枚举 MFT 记录，直接得到
   文件名、父引用、大小与时间。**需要管理员或 LocalSystem**；非管理员会得到 Win32 错误 5。
2. `mode=auto`：优先 USN；拿不到卷句柄或 USN 失败时自动降级为目录遍历（`walk`），日志留痕。
3. `mode=walk` / 非卷路径：从指定根目录递归枚举，适合只索引某个目录或没有管理员权限时使用。

USN 实测（管理员，整卷 `C:\`）：1,110,379 条、构建 10,822 ms、索引 32.70 MB（30.88 B/条），
来源标记 `usn:\\.\C:` 证明走的是主路径而不是降级。数据来源：`docs/1.0.0-发布说明.md` §1.3。

### 3.2 索引文件格式（v4）

```
[Header 256B][Record 16B × N][PrefixLens 1B × N][名字后缀区]
```

- 记录 16 字节：`NameOffset u32 / ParentIndex i32 / SizeAndFlags u32 / LengthDepth u16 / ModifiedDays u16`。
- 名字按「折叠小写」排序；每 5 条一组做公共前缀编码，组首存全名，其余只存前缀之后的字节。
- 读取侧 `MemoryMappedFile` + `AcquirePointer`，查询零拷贝；不反序列化整表。
- 体积实测：`C:\` 全卷 30.88 B/条；`C:\Windows` 43.68 B/条；`C:\Windows\WinSxS` 48.72 B/条；
  5M 合成 25.46 B/条。按真实名字分布外推 5M 约 154 MB。数据来源：`docs/M2-索引与搜索报告.md` §10.2。

### 3.3 查询

- T0 完全相等（tier 100）、T1 前缀（85）、T2 子串（70）；多 token 按 AND 组合。
- 前缀用二分（`LowerBound` / `UpperBoundOfPrefix`）；子串用 `SearchValues<byte>` + 分片并行扫描。
- 结果直接按 `(tier, 深度)` 分桶，不做全量排序；前缀命中已够填满结果集时跳过整段子串扫描。
- junction 命中按前缀块成组遍历，先用后缀首字节快速否决。

实现入口：`src/Pathleap.Shared/Index/SearchEngine.cs`、`IndexSnapshot.cs`。

## 4. 实测数据（口径与环境）

**机器**：AMD Ryzen 7 7840H / 16 逻辑核 / 16 GB / Windows NT 10.0.26300.0 / .NET 8.0.33。
**口径**：索引与查询为后台/离屏（只走命名管道，无窗口、无键鼠注入）；唤醒为前台实测（真实 `SendInput`）。

### 4.1 查询延迟（Release，5M 合成索引，服务端 `QueryMs`）

| 查询 | P50 | P95 | max |
| --- | --- | --- | --- |
| `report` | 1.198 ms | 1.942 ms | 2.659 ms |
| `project_0000123` | 9.626 ms | 16.164 ms | 23.097 ms |
| `.dll` | 6.554 ms | 8.130 ms | 8.836 ms |
| `zzzzzz` | 6.244 ms | 7.846 ms | 9.155 ms |
| `report dll` | 8.362 ms | 13.189 ms | 15.227 ms |

全部 P95 ≤ 16.2 ms（预算 30 ms）。来源：`docs/M2-索引与搜索报告.md` §10.3。
判断预算必须用 **Release** 构建；同查询在 Debug 下会慢 3–5 倍。

### 4.2 索引体积（实测，不是估算）

| 数据集 | 条目数 | 索引体积 | 每条字节 |
| --- | --- | --- | --- |
| `C:\` 全卷（USN，真实长文件名） | 1,110,379 | 32.70 MB | 30.88 B |
| `C:\Windows`（遍历） | 241,560 | 10.1 MB | 43.68 B |
| `C:\Windows\WinSxS`（最坏子树） | 172,109 | 8.0 MB | 48.72 B |
| 5M 合成 | 5,000,000 | 121.4 MB | 25.46 B |

真实卷名字长度 P50 17 / P90 56 / P99 110 / max 182 字节；前缀编码额外省 6.35 MB。
来源：`docs/1.0.0-发布说明.md` §1.1、`docs/M2-索引与搜索报告.md` §10.2。

### 4.3 构建内存（构建移入子进程）

| 时点 | 服务工作集 | 服务 Private |
| --- | --- | --- |
| 第 1 次 5M 重建完成 | 36.3 MB | 9.1 MB |
| 第 2 次 5M 重建完成 | 37.1 MB | 9.5 MB |
| 查询后（峰值） | 41.6 MB | 9.6 MB |

构建子进程峰值约 1.37 GB，随进程退出释放；服务连续重建不累积。来源：`docs/1.0.0-发布说明.md` §1.2。

### 4.4 唤醒与结果列表

- M1 前台基准（Release，1000 次，历史数据）：可输入 P50 33.673 ms / P95 41.739 ms / P99 49.559 ms。
  来源：`docs/M1-性能实测报告.md` §4。
- M1 的首次唤醒冷样本失败问题，alpha5 已加入「唤出后重试激活」；**该修复尚未取得有效前台数据**
  （采集时会话锁屏，`SendInput` 返回 0）。alpha2 历史数据为可输入 P50 36.7 / P95 53.1 ms（未达标），
  不能与 M1 数据混比。来源：`docs/1.0.0-发布说明.md` §3。
- 结果列表后台回归 12/12、冒烟 13/13；Release 首查 8.695 ms（首次触摸索引页，之后 1–2 ms）。
  来源：`docs/M3-结果列表与键盘交互.md` §3、§4。

## 5. 目录结构与两个进程

```
Pathleap/
├── src/
│   ├── Pathleap.Shared/     品牌常量、配置、日志、IPC 协议、索引格式/构建/查询
│   ├── Pathleap.Service/    常驻索引服务：管道、索引装载/构建、搜索；--build-once 子进程入口
│   └── Pathleap.UI/         WinUI 3：托盘、全局热键、搜索窗、结果列表、唤醒基准
├── installer/               Inno Setup 脚本与中文语言文件
├── tools/                   PowerShell 构建/打包/测试/基准脚本（自定义变量统一 Lp 前缀）
├── docs/                    里程碑报告、发布说明、版本规则、发布检查清单
└── .github/workflows/       CI：构建、打包 zip/安装包、tag 发布 Release
```

| 进程 | 职责 | 生命周期 |
| --- | --- | --- |
| `Pathleap.UI.exe` | 单实例互斥、托盘、全局热键、搜索窗与结果列表、把 `--show/--status/--bench/--quit` 转发给主实例；按配置拉起服务 | 常驻托盘；可由用户退出 |
| `Pathleap.Service.exe` | 命名管道控制面（`PING/STATUS/SEARCH/INDEX_STATUS/INDEX_BUILD`）、索引装载、构建与查询 | 由 UI 拉起（`--console`），UI 退出时一并结束；也可手动以控制台/SCM 模式运行 |

服务 IPC 只绑定命名管道，不监听网络端口。索引构建通过 `Pathleap.Service.exe --build-once <mode> <root> <output>`
短生命周期子进程执行，父进程只读取 `.result.json` 摘要。

## 6. 构建、打包与 CI

```powershell
# 编译（先跑脚本变量命名检查，并把服务产物复制进 UI 输出目录）
pwsh -File .\tools\Build-Lp.ps1 -LpConfiguration Release

# 便携 zip：dist\Pathleap-<版本>-win-x64.zip
pwsh -File .\tools\Package-Lp.ps1

# 安装包：dist\Pathleap-<版本>-win-x64-setup.exe（需要 Inno Setup 6）
pwsh -File .\tools\Package-LpInstaller.ps1
```

本机安装包编译可用 Inno Setup 6（CI 使用 `choco install innosetup`）；脚本会依次查找 PATH、
`%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`、`%ProgramFiles%\Inno Setup 6\ISCC.exe`。

CI（`.github/workflows/build.yml`）：main/PR 构建并上传 `Pathleap-<版本>-win-x64` 产物（zip + setup）；
tag `v<版本>` 触发时以 tag 版本注入构建，并额外创建/更新 GitHub Release，同时挂上 zip 与 setup。

## 7. 版本号规则

版本号唯一来源是 [Directory.Build.props](Directory.Build.props) 的 `PathleapVersion`；程序集版本、程序内显示、
zip/安装包名、CI 产物名都由它派生。规则摘要：

- 格式 `<major>.<minor>.<patch>[-alpha<N>]`；`major` 不设上限，`minor` / `patch` 取 0–10，超过 10 向前进位。
- 预发布用 `alphaN`；正式版落到数字版本（当前 `1.0.1`）。
- tag 带 `v` 前缀（`v1.0.1`）；升级用 `pwsh -File .\tools\Next-LpVersion.ps1 -LpBump patch -LpApply`。
- 完整进位示例与「下一版是什么」见 [docs/版本号规则.md](docs/版本号规则.md)。

## 8. 文档索引

| 文档 | 内容 |
| --- | --- |
| [docs/M1-性能实测报告.md](docs/M1-性能实测报告.md) | 唤醒路径、1000 次前台基准、冷样本问题与 Plan B 判断 |
| [docs/M2-索引与搜索报告.md](docs/M2-索引与搜索报告.md) | 索引格式 v4、体积/查询/内存实测、USN 阻塞与降级、子进程构建 |
| [docs/M3-结果列表与键盘交互.md](docs/M3-结果列表与键盘交互.md) | 结果列表实现、后台回归 12/12、首查延迟、未前台验收项 |
| [docs/1.0.0-发布说明.md](docs/1.0.0-发布说明.md) | 1.0.0 阻塞项闭环：存储体积、内存回落、USN 主路径、查询性能 |
| [docs/1.0.1-发布说明.md](docs/1.0.1-发布说明.md) | 1.0.1：安装包、便携/安装差异、禁用词检索证据 |
| [docs/版本号规则.md](docs/版本号规则.md) | 单一来源、进位规则、tag 与 CI |
| [docs/发布检查清单.md](docs/发布检查清单.md) | 每次发版逐项核对（含 README 与截图对照项） |
| [docs/界面对照.html](docs/界面对照.html) | 五屏设计稿与实机截图并排对照、逐项差异与处理状态 |
| [docs/界面验收报告.md](docs/界面验收报告.md) | 设计稿要求 / 当前实现 / 差距 / 是否解决，含边框证据与未验证项 |

## 9. 已知限制与未完成项

1. **首次运行不自动建索引**：需要 §2.3 的显式触发；否则结果区显示「索引未就绪」。
2. **索引是单卷/单文件**：`LoadLatest` 只装载最新的一个 `.plidx`，多卷合并未实现。
3. **无增量更新**：USN 只做全量枚举，文件增删改后需要重新构建索引。
4. **无 SCM 服务安装**：服务随 UI 启动；不随系统开机常驻。
5. **更新检查已接入，镜像与静默安装未接入**：四态界面与 GitHub Release 检查、下载可用；镜像切换、后台静默安装未做。
6. **快速切换不能注入文件对话框**：浮层与键盘操作可用，资源管理器打开/保存对话框内嵌未接入。
7. **窗口圆角为系统值（约 8 DIP）**：与设计稿 14 DIP 存在差异，取舍理由见 `docs/界面验收报告.md`。
8. **前台唤醒基准待复测**：alpha5 重试修复没有有效前台样本，现有最新前台数据仍是 M1 的 1000 次结果。
9. **`Enter` 打开目标未前台验收**：选中项到路径的映射已由后台测试覆盖，真正拉起资源管理器/默认程序的最后一步未自动化。
10. **真实整卷索引在本地测试中会被覆盖**：基准脚本会把 5M 合成索引写到同一路径；需要真实卷数据时重新跑 USN 构建。

## 10. 文档维护约定

**每个里程碑完成、每次发版，必须同步更新 README 的对应章节**：

- 版本号、能力清单（已实现 / 未完成）、性能实测数据、已知限制；
- 新增或改变的安装/构建方式、产物名；
- 所有数字必须来自 `docs/` 下的实测报告，注明口径（后台 / 前台）与环境，不写未验证的能力。

**界面里程碑验收标准（硬性）**：任何涉及界面的里程碑，必须附真实应用截图与设计稿（`docs/design-reference.html`）的并排对照，并逐项列出「设计稿要求 / 当前实现 / 差距 / 是否解决」；没有截图对照的界面改动不算完成。截图通道、缩放比例、会话状态需在报告中写明；无法实测的项必须标注「未验证」或「被环境挡住」，不得写成已完成。

发版时逐项核对 [docs/发布检查清单.md](docs/发布检查清单.md)，其中「README 与当前版本一致」为必过项。

## 脚本安全约定

- 所有 PowerShell 自定义变量以 `Lp` 开头；`tools/Test-LpVariableNames.ps1` 用 AST 检查，`Build-Lp.ps1` 会先执行。
- 删除/清理只走 `tools/Lp.Safety.psm1` 的 `Remove-LpItemSafely`：路径白名单、`-WhatIf`、删除前打印绝对路径。
- 测试默认后台/离屏（UI `--tray`、服务 `--console`）；前台唤醒基准必须显式 `-LpAllowForeground`，并且每轮集中跑一次。
