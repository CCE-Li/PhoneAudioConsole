# 第三方来源 (Vendored)

本目录下的 `Vendor/` 与 `Runtime/` 内容复制自参考项目，用于零安装的手机音频抓取。

## 来源

- 仓库：`SonicRoute`（MIT License）
- 目录：`SonicRoute/SonicRoute.AndroidAudio/`、`SonicRoute/AndroidRuntime/`

## Vendor/（源码，保持逐字节一致，未做修改）

| 文件 | 作用 |
|---|---|
| `AndroidModels.cs` | 设备/应用/传输接口等数据模型 |
| `AdbClient.cs` | ADB 子进程调度、设备列举、push/forward |
| `AndroidRuntimeBundle.cs` | 内嵌运行时懒加载与校验 |
| `AndroidDeviceDiscovery.cs` | 共享扫描的设备列表模型 |
| `ScrcpyOpusReceiver.cs` | scrcpy 3.3.4 音频协议解析 + Opus 解码 |
| `AndroidPcmSource.cs` | 每流 float PCM 环形缓冲 + 增益 + WASAPI 播放器 |
| `WasapiDeviceLease.cs` | NAudio 端点租约 |

> 这些文件保留原命名空间 `SonicRoute.AndroidAudio`，因此 `AndroidRuntimeBundle` 内的
> 资源名字面量（`SonicRoute.AndroidRuntime.*`、`SonicRoute.AndroidAudio.AndroidRuntime.files.json`）
> 无需改动，由 `PhoneAudioConsole.Audio.csproj` 以相同 `LogicalName` 内嵌。

## Runtime/（二进制）

`adb.exe`、`AdbWinApi.dll`、`AdbWinUsbApi.dll`（Google Android SDK，见 `ADB-NOTICE.txt`）、
`scrcpy-server`（改版 scrcpy 3.3.4，Apache-2.0，见 `SCRCPY-LICENSE.txt`）、
`sonicroute-catalog.jar`。

`AndroidRuntime.files.json` 同时列有 `video`/`control` 组（虚拟屏相关）的文件条目，
但本工程不内嵌这些文件，也不会调用对应分组，属正常。

按 MIT / Apache-2.0 要求保留上述版权与许可声明。
