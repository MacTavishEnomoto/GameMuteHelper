# GameMuteHelper

Windows 后台省资源小工具：选定的游戏或程序**切到后台后自动静音或降低音量**，并可按需限制后台资源占用（CPU 配额 / 挂起进程两种方式）。纯 C# 单文件实现，零外部依赖。

## 功能

- **后台自动静音 / 降音量**：程序不在前台（Alt-Tab、最小化、点击其他窗口）超过设定防抖时间后，把它的音频会话静音，或线性渐变到你指定的百分比音量（5–90%）；回到前台再渐变回原值。每个应用可独立选择「完全静音」或「降低音量」，渐变时长可调（0 = 瞬间切换）。
- **后台限帧**，每个应用可独立选择方式：
  - **CPU 配额**：用 Windows Job Object 限制后台 CPU 时间占比（1–100%），不保证具体帧率。
  - **挂起进程**：用 `NtSuspendProcess` 周期性挂起/放行进程，按每秒次数设置（1–240）。实验性功能，可能造成卡顿、声音异常或联机掉线。**挂起失败时默认自动改用 CPU 配额**（可在方式菜单关闭）。
- **每个应用独立设置**：音频开关、音频方式（完全静音 / 降低音量到指定百分比）、限帧开关、限帧方式和目标值；数值框支持箭头微调（±5 / 按住连发）与直接输入。
- **失焦防抖**：默认 500 ms，可调 0–5000 ms，避免快速切窗口时反复静音/恢复。
- **安全恢复**：暂停、退出、注销/关机时恢复所有音量与限速；会话被模拟器/游戏重建后也能恢复到真正的原始音量（自动识别被污染的会话状态）。
- 托盘常驻、开机自启（计划任务，管理员模式下不弹 UAC）、实时搜索、状态徽章。

## 下载

到 [Releases](https://github.com/MacTavishEnomoto/GameMuteHelper/releases) 下载 `GameMuteHelper.exe`，双击运行即可。首次启动会请求管理员权限（给进程设置 Job / 挂起恢复需要）。

也可以克隆仓库后双击 `Build_EXE.bat`，用 Windows 自带的 `csc.exe` 离线编译（约 1 秒）。

## 使用

1. 切到「运行中」标签页，找到正在运行的游戏/程序，点「+ 接管」；也可以点右上角「+ 添加EXE」手动选择。
2. 在「已接管」里为每个程序打开「音频」和/或「限帧」。
3. 音频方式：卡片上「音频」开关旁的按钮（默认 `完全静音`）点开可选择：
   - `完全静音`：后台时直接静音该应用的音频会话。
   - `降低音量`：后台时把会话音量渐变降到右侧数值框指定的百分比（5–90，默认 30），回前台渐变恢复原音量。
4. 每个卡片上都有一个限帧方式按钮（默认 `CPU`），点开可选择该应用自己的限帧方式：
   - `CPU`：右侧数字是后台 CPU 上限百分比（1–100）。
   - `挂起`：右侧数字是每秒挂起循环次数（1–240），数值越低越卡。方式菜单里还有「挂起失败时自动改用 CPU 配额」选项（默认开启）。
5. 切到别的窗口，超过防抖时间后目标程序会被静音/降音量/限速；切回前台自动恢复。
6. 底部「开机自启」可创建登录时自动运行的计划任务；「收起托盘」只隐藏窗口，真正退出请用托盘右键菜单。

## 配置与日志

- 配置：`%LOCALAPPDATA%\GameMuteHelper\config.json`
- 日志：`%LOCALAPPDATA%\GameMuteHelper\app.log`（超过 512 KB 自动滚动为 `.old`）
- 界面底部「数据目录」可直接打开该文件夹。

旧版配置里的 `Watch` / `TargetFps` / `LimitFps` 字段会在首次启动时自动迁移为按应用规则；旧版 `rtss` 限帧方式自动迁移为 CPU 配额。

## 系统要求

- Windows 10 / 11（系统自带 .NET Framework 4.x，无需额外安装）

## 实现简述

- **静音 / 降音量**：直接调用 Windows Core Audio（`IAudioSessionManager2` → `IAudioSessionControl2` → `ISimpleAudioVolume`），按音频会话实例 ID 操作，不改系统音量。「完全静音」用 `SetMute`，「降低音量」用 `SetMasterVolume`；应用前记录原值，回前台时写回（支持线性渐变）。
- **CPU 配额**：每个进程创建独立 Job Object，设置 `JobObjectCpuRateControlInformation` 硬上限（`ENABLE | HARD_CAP`）；回前台、暂停或进程退出时解除并释放句柄。
- **挂起进程**：调用 `ntdll!NtSuspendProcess` / `NtResumeProcess`，后台时按周期挂起约 90% 的时间、短暂放行，以便节省 CPU/GPU；回前台、暂停、退出时立即恢复并回收线程。启动时尝试启用 `SeDebugPrivilege`；未处理异常、`ProcessExit` 也会强制恢复（参考 RHI 的 GamePauser 做法）。
- **挂起失败回退**：`NtSuspendProcess` 返回错误（如反作弊返回 `0xC0000022`）时，如启用回退，则本会话内自动改用 CPU 配额，并在卡片状态中标注「回退CPU」。
- **前台判定**：`GetForegroundWindow` 的 PID 是否属于被接管进程。

## 注意事项

- CPU 配额不是帧率上限：CPU 瓶颈的程序（模拟器、老游戏）会明显降帧，GPU 瓶颈的程序帧率可能几乎不变。
- 挂起进程是实验性方式：周期性挂起会导致画面卡顿、声音断续、网络超时，**不要用于联机游戏**。
- 带反作弊保护的进程（如 ACE、EAC、BattlEye）通常拒绝或立即恢复挂起（常见返回 `STATUS_ACCESS_DENIED 0xC0000022`）。启用默认回退时会自动改用 CPU 配额；CPU 配额也被拒绝时状态显示「回退失败」，此时只能靠游戏内设置、驱动层限帧或真正退出游戏。
- 挂起依赖本程序持续运行。正常退出、暂停监控、注销/关机时会自动恢复所有进程；但如果本程序被强制结束（任务管理器结束进程、崩溃、断电），被挂起的进程可能保持挂起状态，需要手动结束并重启该程序。
- 程序需要管理员权限；开机自启用计划任务而非注册表 Run 键，以避免登录时弹 UAC。

## 参考实现

挂起与限流实现参考了以下开源项目（仅借鉴通用的 Windows API 用法，不包含任何绕过反作弊的手段）：

- [RankFTW/RHI — GamePauser.cs](https://github.com/RankFTW/RHI/blob/main/RenoDXCommander/Overlay/GamePauser.cs)：`OpenProcess(PROCESS_SUSPEND_RESUME)` + `NtSuspendProcess`，检查 NTSTATUS，失败时关闭句柄；`ProcessExit` / `UnhandledException` 兜底恢复。
- [lowleveldesign/process-governor](https://github.com/lowleveldesign/process-governor)：Job Object CPU 速率、效率模式（EcoQoS）、`SeDebugPrivilege` 启用等成熟做法。
- [googleprojectzero/sandbox-attacksurface-analysis-tools — NtCoreLib](https://github.com/googleprojectzero/sandbox-attacksurface-analysis-tools)：原生 NT API 与 NTSTATUS 处理参考。

## 目录结构

```
GameMuteHelper.cs    主程序（单文件 C#）
Build_EXE.bat        调用系统 csc.exe 编译
app.manifest         DPI 感知与 UAC 配置
GameMuteHelper.exe   预编译版本（也发布在 Releases）
README.md
```
