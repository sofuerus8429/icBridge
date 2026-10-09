# 架构

## 音频图

三路永久 MixerChannel 进入 MixingSampleProvider，再经总音量和 PeakLimiter 输出到选定的 WASAPI 播放端。所有输入转换到 48 kHz、双声道、32-bit float。

- 本地文件：AudioFileReader → 声道转换/重采样 → Music。
- 原麦克风：WasapiCapture → 有界 CaptureBuffer → PCM/Float 解码 → 声道转换/重采样 → 可选 80 Hz 高通 / 静音门 → Microphone。
- 程序声音：ActivateAudioInterfaceAsync + 进程树参数 → IAudioClient 的事件驱动采集 → CaptureBuffer → Application。
- 兼容采集：WasapiLoopbackCapture 从独立播放设备读取全部声音。
- 输出：Music + Application + Microphone → Master → PeakLimiter → WasapiOut。
- 监听：主输出读取时复制最终样本到独立有界缓冲，再由另一个 WasapiOut 播放。不会再次推进文件或混音时钟。

## 线程与生命周期

进程选择器默认枚举全部 Windows 进程，合并所有活动播放设备上的音频会话元数据，不按会话或窗口过滤。无法读取的可选元数据保留为空；自身、其他音桥实例及 PID 0 保留显示但拒绝采集，避免输出反馈。音频会话筛选不等同于“当前有声”。搜索使用进程名、窗口标题与 PID，接受选择后直接向现有 AudioEngine 传入 PID，自动启用进程采集及对应混音通道。选择和启动前比较进程启动时间，拒绝已退出或 PID 被复用的选择。刷新设备保留仍然有效的已选进程。

AudioEngine 用 SemaphoreSlim 串行化启动/停止。设备初始化和清理不阻塞 WPF Dispatcher。文件读取和播放控制共享锁，防止跳转、换曲、释放与读取并发。

指定进程采集拥有独立 MTA 线程，以 AutoResetEvent 等待 WASAPI 样本。激活回调实现 IAgileObject，使用真实 PROPVARIANT/VT_BLOB 传递进程参数。异步激活参数保留到回调完成；超时后晚到的客户端也会释放。采集线程负责停止并释放 COM 客户端。

该线程在启动采集前调用 `AvSetMmThreadCharacteristicsW("Audio", ...)`，加入 Windows MMCSS 的 Audio 任务；返回空句柄时继续普通采集。线程的 finally 在释放音频客户端后调用 `AvRevertMmThreadCharacteristics`，正常停止和异常退出走同一路径。登记和恢复都在采集线程执行，遵守 [Windows 同线程释放要求](https://learn.microsoft.com/en-us/windows/win32/api/avrt/nf-avrt-avrevertmmthreadcharacteristics)；不修改全局计时器、服务或注册表，不新增正式运行线程。

输出设备的读取是混音时钟。运行时 UI 每 60 ms 读取电平快照，最小化时改为 250 ms，空闲时停止定时器；音频线程不操作 UI。灯柱使用冻结并共享的画刷，仅灯段数量改变时重绘；数值显示按 0.1 dB、时间按整秒变化更新。设备刷新只读取音频端点，打开进程选择器才枚举全部进程。停止混音暂停文件，释放所有输出、采集和设备对象，清空采集通道。设备故障回报 UI，停止会话，用户刷新后重启。

采集缓冲上限 500 ms，写入前若积压超过 150 ms 就丢弃旧样本，避免时钟漂移导致延迟持续增长。发生丢弃可能有短暂跳变；没有声称实现自适应硬件时钟同步。

## 音量与峰值

每路线性音量（0–3）乘以 `MixWeight × 10^(gainDb / 20)`。正常推子上限 1，GAIN 模式上限 3；关闭模式时超出 1 的音量回落到 1。高级 dB 增益独立叠加。音量/增益/混音权重变化使用 5 ms 立体声一致的斜坡；静音、零权重与取消加入混音立即归零。被静音的文件仍推进播放位置。

主界面为三路竖向调音台，顶部选择麦克风、来源与输出。音乐条通过 SourceChannel 绑定到当前本地 / 进程通道，切换保留各自音量与 GAIN。MixerChannel 分别记录乘法前 InputPeak 和乘法后 Peak；主界面拾音灯读取后者，总输出灯读取 PeakLimiter 后的实际峰值。界面每 60 ms 更新，将 −60 至 0 dBFS 映射到 32 个细分段灯，快速响应上升并平滑衰减。绿色、黄色和红色的边界分别为 −12 与 −3 dBFS；数值可显示高于 0 dBFS 的分路峰值。

交叉混音放在高级设置，位置 p 为 0–100：本地及程序通道权重为 `min(1, (100-p)/50)`，麦克风权重为 `min(1, p/50)`。中点保留两路完整音量，两端关闭其中一侧。监听、格式、缓冲、dB 增益和播放列表由共享 ViewModel 的高级窗口处理。

麦克风处理位于重采样后、推子之前。高通为 80 Hz 一阶滤波；静音门按每个立体声帧的最大绝对幅度检测，左右声道使用同一包络，5 ms 打开、120 ms 保持、80 ms 指数衰减。选项线程安全，默认全部关闭。它不提供语音期间的频谱降噪。

玻璃主题使用半透明 WPF 卡片；支持的 Windows 11 用 DWM 系统背景与圆角。未提供自定义 UI 自动化注入，系统不支持时使用深色背景。

PeakLimiter 对左右声道使用同一衰减系数，即时压低过高峰值，以 100 ms 释放恢复。最后钳制至 ±0.97，清除非有限值。它是实用的峰值保护，没有前瞻，也不是专业母带响度处理。电平显示单位为 dBFS。

## 虚拟设备边界

应用把最终混音写入线缆的播放端，驱动将它送到录音端。普通用户态程序不能凭空创建一个系统录音设备。本版嵌入官方 VB-CABLE Pack45 原始 ZIP，保留全部 31 个文件；驱动本身由 VB-Audio 提供并签名，安装由官方安装器完成。

启动时枚举活动及禁用的播放、录音端点。已装驱动但 CABLE Output 被禁用时，提供启用说明，不打开安装器。未装驱动时显示专用安装窗口；用户点击后再次检查设备，再释放包到唯一临时目录。按固定 SHA-256 验证 ZIP、安装器与释放文件，以 WinVerifyTrust 校验官方安装器签名；准备失败不会请求提权。通过 ShellExecute 的 runas 打开官方安装界面，等待结束后刷新设备并清理本次临时目录。取消 UAC、未安装或设备尚待重启时显示实际状态，不将安装器退出等同于安装成功。

音桥主程序保持 asInvoker；只有驱动安装器需要管理员权限。首次安装需要重启。驱动会在系统注册设备及相关注册表项，音桥配置仍保存在 JSON。驱动测试只检查内置资源、释放文件、签名、提权参数和清理，不卸载已有驱动或实际启动安装器。

输出端和监听端不得相同；整设备采集端不得与其中任何一个相同。UI 原麦克风列表排除常见虚拟线缆，避免将输出重新混回输入。虚拟设备名称识别是启发式；其他线缆可手动选择输出。

## 上游资料

- [Microsoft Application Loopback Sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/)
- [ActivateAudioInterfaceAsync](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-activateaudiointerfaceasync)
- [按 PID 包含目标进程及其子进程](https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_process_loopback_params)
- [NAudio v2.2.1](https://github.com/naudio/NAudio/tree/v2.2.1)
- [VB-CABLE 播放端/录音端](https://vb-audio.com/Cable/)
- [Windows 系统背景材质与版本要求](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type)
- [Microsoft .NET 单文件部署](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
- [SSL Live L650 界面参考](https://solidstatelogic.com/products/l650)

## 单文件发布

`scripts/publish-single-file.ps1` 默认构建 Windows x64 / .NET Framework 4.8，Costura 将运行所需 Copy Local 托管依赖压缩嵌入应用，排除未使用的 ASIO、MIDI、WinForms 音频模块；在内存中加载，不释放 DLL 或整套运行时。Windows 11 和 Windows 10 1903 及以上已有该框架。发布脚本确认主程序不需要旁侧文件，再生成大小、SHA-256 和运行时信息，并生成可选卸载启动器。

`-BundledRuntime` 可选构建 .NET 8.0.31 的自包含单 EXE，保留原生库自解压，不裁剪 WPF。较大的兼容包输出到 `dist/<版本>/bundled-runtime/`，不会覆盖默认精简版。可选发布使用独立的 `packages.singlefile.lock.json`，普通多目标构建使用 `packages.lock.json`。平台兼容辅助方法共享 Windows 版本读取、哈希、浮点边界、任务超时和进程等待；.NET Framework 不依赖会错误报告版本的兼容性 OSVersion 判断。

项目、NAudio、Newtonsoft.Json、Costura 和 VB-CABLE 的说明嵌入应用资源，可从高级设置查看；可选自包含版另嵌入 .NET 与 Windows Desktop 许可，VB-CABLE 原始许可从内置 ZIP 读取。隔离验证将 EXE 复制到只有它的目录，检查实际运行时、许可与托管资源，再运行 WPF 和真实 WASAPI 音频操作验证。

卸载界面通过同一个签名的官方维护工具移除普通 VB-CABLE，不为其他线缆运行卸载器。驱动检测包含禁用设备；要求停止混音且没有另一个音桥实例。清除设置为单独的选项，只删除 settings.json、旧 .tmp、严格 GUID 格式的配置暂存及 errors.log，保留相似名称的用户文件；保存抑制标志防止退出重建配置。便携 EXE 由用户关闭后删除，不执行自删除或删除音乐文件。
