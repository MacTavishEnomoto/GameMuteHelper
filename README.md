# GameMuteHelper

Windows 后台省资源小工具：选定的游戏或程序**切到后台后自动静音**，并可按需限制后台资源占用（RTSS 精确限帧，或不用 RTSS 的 CPU 配额模式）。纯 C# 单文件实现，零外部依赖。

## 功能

- **后台自动静音**：程序不在前台（Alt-Tab、最小化、点击其他窗口）超过设定防抖时间后，把它的音频会话静音；回到前台自动恢复成原来的状态。
- **后台限帧**，每个应用可独立选择方式：
  - **RTSS 精确限帧**：按 FPS 限制（1–240，默认 5），需要安装 [RivaTuner Statistics Server](https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/)。
  - **CPU 配额**：用 Windows Job Object 限制后台 CPU 时间占比（1–100%），**不需要 RTSS**，但限制的是 CPU 占用，不保证具体帧率。
  - **挂起进程**：用 `NtSuspendProcess` 周期性挂起/放行进程，按每秒次数设置（1–240），**不需要 RTSS**。实验性功能，可能造成卡顿、声音异常或联机掉线。
- **每个应用独立设置**：静音开关、限帧开关、限帧方式（RTSS / CPU / 挂起）和目标值。
- **失焦防抖**：默认 500 ms，可调 0–5000 ms，避免快速切窗口时反复静音/恢复。
- **RTSS 生命周期管理**：启动时自动拉起 RTSS；手动关闭后，如果存在限帧规则会自动重启（10 秒内最多一次）；未运行时界面提供「启动 RTSS」按钮。
- **安全恢复**：暂停、退出、注销/关机时恢复所有音量和帧率；程序崩溃后再次启动会自动还原被改过的 RTSS 限帧值。
- 托盘常驻、开机自启（计划任务，管理员模式下不弹 UAC）、实时搜索、状态徽章。

## 下载

到 [Releases](https://github.com/MacTavishEnomoto/GameMuteHelper/releases) 下载 `GameMuteHelper.exe`，双击运行即可。首次启动会请求管理员权限（修改 RTSS 配置和给进程设置 Job 需要）。

也可以克隆仓库后双击 `Build_EXE.bat`，用 Windows 自带的 `csc.exe` 离线编译（约 1 秒）。

## 使用

1. 切到「运行中」标签页，找到正在运行的游戏/程序，点「+ 接管」；也可以点右上角「+ 添加EXE」手动选择。
2. 在「已接管」里为每个程序打开「静音」和/或「限帧」。
3. 每个卡片上都有一个方式按钮（默认 `RTSS`），点开可选择该应用自己的限帧方式：
   - `RTSS`：右侧数字是目标 FPS。
   - `CPU`：右侧数字是后台 CPU 上限百分比，可编辑（1–100）。
   - `挂起`：右侧数字是每秒挂起循环次数（1–240），数值越低越卡。
4. 切到别的窗口，超过防抖时间后目标程序会被静音/限速；切回前台自动恢复。
5. 底部「开机自启」可创建登录时自动运行的计划任务；「收起托盘」只隐藏窗口，真正退出请用托盘右键菜单。

## 配置与日志

- 配置：`%LOCALAPPDATA%\GameMuteHelper\config.json`
- 日志：`%LOCALAPPDATA%\GameMuteHelper\app.log`（超过 512 KB 自动滚动为 `.old`）
- 界面底部「数据目录」可直接打开该文件夹。

旧版配置里的 `Watch` / `TargetFps` / `LimitFps` 字段会在首次启动时自动迁移为按应用规则。

## 系统要求

- Windows 10 / 11（系统自带 .NET Framework 4.x，无需额外安装）
- RTSS 7.x：仅「RTSS 精确限帧」方式需要

## 实现简述

- **静音**：直接调用 Windows Core Audio（`IAudioSessionManager2` → `IAudioSessionControl2` → `ISimpleAudioVolume`），按音频会话实例 ID 操作，不改系统音量。
- **RTSS 限帧**：动态加载 `RTSSHooks64.dll`，调用 `LoadProfile` / `SetProfileProperty("FramerateLimit")` / `SaveProfile` / `UpdateProfiles` 修改对应 exe 的 profile，修改前记录原值以便恢复。
- **CPU 配额**：每个进程创建独立 Job Object，设置 `JobObjectCpuRateControlInformation` 硬上限（`ENABLE | HARD_CAP`）；回前台、暂停或进程退出时解除并释放句柄。
- **挂起进程**：调用 `ntdll!NtSuspendProcess` / `NtResumeProcess`，后台时按周期挂起约 90% 的时间、短暂放行，以便节省 CPU/GPU；回前台、暂停、退出时立即恢复并回收线程。
- **前台判定**：`GetForegroundWindow` 的 PID 是否属于被接管进程。

## 注意事项

- CPU 配额不是帧率上限：CPU 瓶颈的程序（模拟器、老游戏）会明显降帧，GPU 瓶颈的程序帧率可能几乎不变。
- 挂起进程是实验性方式：周期性挂起会导致画面卡顿、声音断续、网络超时，**不要用于联机游戏**。
- 带反作弊保护的进程（如 ACE、EAC、BattlEye）通常拒绝或立即恢复挂起，此时卡片会显示「挂起失败」，请改用 RTSS 或 CPU 配额。
- 挂起依赖本程序持续运行。正常退出、暂停监控、注销/关机时会自动恢复所有进程；但如果本程序被强制结束（任务管理器结束进程、崩溃、断电），被挂起的进程可能保持挂起状态，需要手动结束并重启该程序。
- RTSS 只会在存在「RTSS 方式且开启限帧」的规则时启动；手动关闭 RTSS 后，满足上述条件会自动重启（10 秒内最多一次）。
- RTSS 通过注入实现限帧，个别带反作弊的在线游戏可能拦截或判定异常；使用前请自行确认游戏规则。
- RTSS 需要在游戏启动前运行才能注入，已经在运行的游戏需要重启才生效。
- 程序需要管理员权限；开机自启用计划任务而非注册表 Run 键，以避免登录时弹 UAC。

## 目录结构

```
GameMuteHelper.cs    主程序（单文件 C#）
Build_EXE.bat        调用系统 csc.exe 编译
app.manifest         DPI 感知与 UAC 配置
GameMuteHelper.exe   预编译版本（也发布在 Releases）
README.md
```
