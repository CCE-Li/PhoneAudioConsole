# 手机音频接入控制台 (PhoneAudioConsole)

Windows 常驻托盘的**零安装**手机音频接管工具：把手机音频接到电脑播放，USB / WiFi 自动接管，
并按手机应用分别调音量。手机端**不需要安装任何 App**（只需授权 USB 调试）。

## 功能

- **三种连接方式自选**：USB / WiFi / 蓝牙（设置里直接选，互斥，避免出两份声音）。
- **USB**：插上手机即自动接管；拔出后手机保持静音（接管时把手机媒体音量置 0）。
- **WiFi**：选 WiFi 后，插一次 USB 会自动开启无线调试（`adb tcpip` + `adb connect`），之后拔掉线也能用。
- **蓝牙**：电脑作 A2DP 接收端（WinRT `AudioPlaybackConnection`），免数据线、免调试；仅整机音量。
- **抓取方式（USB / WiFi）**：按应用（每应用独立流 + 分音量，需 Android 13+，低版本自动回退整机）或整机。
- **托盘常驻**：无主窗口、不占任务栏；设置都在托盘里。开机自启（`HKCU\...\Run`）。
- **全局热键**：`Ctrl+Alt+M` 全部静音；`Ctrl+Alt+↑/↓` 调整整体音量。
- **电脑播放设备可选**：设置里选择输出到哪个耳机/音箱。
- **息屏控制手机**：设置里的按钮（或托盘菜单）一键开启 scrcpy 镜像——手机屏幕熄灭，仍可在电脑窗口里操作手机（需 USB/WiFi 已连接）。
- **插手机不弹资源管理器**：识别到手机接入后，自动关闭 Windows 弹出的资源管理器窗口
  （Windows 无法阻止 MTP 挂载，只能事后关窗；按设备友好名如 `iQOO Neo3` 精确匹配，仅接入后短时间生效）。
- **蓝牙接管**：电脑作为 A2DP 接收端（WinRT `AudioPlaybackConnection`），手机蓝牙配对后即可把音频送到电脑，
  无需数据线、无需调试。**仅支持整机音量**（A2DP 是单一混音流，无法按应用）。

## 构建与运行

需要 **.NET 8 SDK**（Windows）。

```powershell
cd PhoneAudioConsole
dotnet build -c Debug
.\bin\Debug\net8.0-windows10.0.19041.0\PhoneAudioConsole.exe
```

启动后只在托盘出现图标（右键菜单：打开设置 / 全部静音 / 插上 USB 自动接管 / 开机自启 / 退出）。

配置与日志：`%LocalAppData%\PhoneAudioConsole\`（`settings.json`、`logs\console.log`、`PhoneRuntime\`）。

## 工作原理

```
手机（零安装：只被 push 一个临时 jar，以 app_process 运行）
  └─ 改版 scrcpy-server 3.3.4
       整机:  audio_source=output      (REMOTE_SUBMIX，手机自身静音)
       按应用: audio_source=playback audio_target_packages=<包> audio_mix_mode=uid
  ↓ Opus over adb forward (USB 或 WiFi)
电脑
  设备监测(adb devices -l) → 会话编排 → Opus 解码(Concentus) → 每应用 PCM + 增益 → WASAPI 输出
```

- 采集用 `adb` + `app_process`，**不安装 APK**，退出时删临时 jar、移除端口转发、只杀自己的进程。
- 按应用的"正在播放"来自解析 `dumpsys media_session`（已兼容 AOSP 与 vivo 的两种格式）。
- 启动时清理上次崩溃残留的 `/data/local/tmp/phonaudio_*.jar`。

## 已知限制

- **按应用接管需要 Android 13+**；低版本按应用模式会自动回退整机（也可直接在设置里选"整机"）。
- **WiFi 模式**若当前没有无线端点，会先通过 USB 开启无线调试；`adb tcpip` 在手机重启后失效，需重新插一次 USB。
- **蓝牙模式需先在 Windows 蓝牙设置里把手机配对**；只有整机音量，且音频走系统默认输出设备。
- 若手机应用主动 opt-out（部分受保护/DRM 内容），或音频没有 media session（如部分游戏），按应用模式可能识别不到。
- WiFi 需手机与电脑在同一局域网。
- 非正常退出（崩溃、强杀）会在手机上留下临时文件，下次启动会自动清扫。
- 一次只接管一台手机。

## 目录结构

```
PhoneAudioConsole/
├─ App.xaml(.cs)            应用入口（单实例、托盘、无窗口启动）
├─ Tray/                    托盘图标与菜单
├─ Settings/                设置窗口（接管方式/输出设备/音量/应用列表）
├─ Platform/
│   ├─ PhoneAudioController 设备监测 + 会话编排 + 静音策略
│   ├─ PerAppAudioManager   每应用流的建立/回收与独立音量
│   ├─ PhoneAppWatcher      解析 dumpsys media_session
│   ├─ GlobalHotkeys        全局热键
│   ├─ AutoStart / SingleInstance / AppSettings
└─ Audio/                   音频库
    ├─ ScrcpyAudioTransport ADB 传输（整机 / 按应用）
    ├─ PhoneCaptureSession  传输+解码+PCM+WASAPI
    ├─ AudioDeviceService   枚举 Windows 播放设备
    ├─ Vendor/              SonicRoute 的抓取栈（逐字节复制）
    └─ Runtime/             内嵌 adb.exe 与 scrcpy-server 等二进制
```

## 第三方来源与许可

`Audio/Vendor` 与 `Audio/Runtime` 复制自 **SonicRoute**（MIT）与 **scrcpy**（Apache-2.0）及
Google Android SDK 的 `adb`。详见 `Audio/VENDOR.md` 与其中的许可文件。本项目保留其版权声明。
