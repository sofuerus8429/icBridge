# 贡献指南

开发需要 Windows 和 .NET SDK 8 或以上。拉取项目后执行 `scripts/build.ps1` 和 `scripts/test.ps1`。提交前确保 Release 构建没有警告、测试通过，并检查实际 WPF 界面。

默认发布目标是 net48（系统内置运行时），另保留 net8.0-windows。`dotnet run --project src/MicBridge.App -f net48` 启动精简版；不要移除 .NET Framework 的硬件验证，也不要对 WPF 启用未经验证的裁剪。NAudio 核心行为测试在两个目标都运行。Costura 只用于 net48 的托管依赖压缩嵌入，不释放 DLL 到磁盘。

音频核心置于 `src/MicBridge.Audio`，不要引入 WPF 依赖。界面与设置置于 `src/MicBridge.App`，音频回调不得操作 Dispatcher 或写阻塞文件。

修复音频行为时添加针对实际问题的确定性测试。硬件检查使用 `tools/MicBridge.Diagnostics`，不要让普通测试依赖本机设备。修改依赖后更新 `packages.lock.json`，CI 使用锁定恢复。

Issue 请附 Windows build、设备名称、采集模式、复现步骤以及经过脱敏的错误日志。不要上传私人录音或歌曲。

Pull Request 请说明具体问题、最终行为和验证结果。避免顺手修改系统默认设备、安装驱动或扩大权限。
