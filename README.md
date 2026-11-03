# PhoneDrive

<img src="assets/brand/phonedrive.png" width="104" alt="PhoneDrive icon">

Android 手机、平板与 Windows 电脑之间的局域网文件管理、传输和磁盘挂载工具。当前正式版本：**0.2.6**。

[下载正式版](https://github.com/palyer313/PhoneDrive/releases/tag/v0.2.6) · [更新说明与验证范围](docs/release-0.2.6.md)

![PhoneDrive 产品与配对界面演示](assets/showcase/overview.png)

界面图截取自用户提供的演示视频，画面为较早的预览版；当前下载版本为正式版 0.2.6。[截图来源说明](docs/media/README.md)

0.2.6 改进 Windows 多网卡配对地址选择与刷新，修正 Android 扫码强制横屏，并在手动启动共享服务前检查 Wi-Fi 与局域网状态。桌面端与 Android 端均提供正式安装包。

本版保留 0.2.5 的应用内上传流水线与空闲唤醒优化。此前 Note11 前台亮屏条件下，同一 1 GiB 随机文件连续三轮上传约 **36 MiB/s**，包含两次完整读回校验的全流程为 **11.79–12.08 MiB/s**；这是 0.2.5 的测试结果，本轮没有重复性能长测。资源管理器挂载盘符使用独立的 SSHFS-Win 路径，尚未完成同等长测验收，不能套用上述结果。

## 功能

- Windows 双栏文件管理器，使用 Windows Shell 文件类型图标。
- Android SFTP 共享服务、局域网设备发现、二维码配对和固定 Host Key 校验。
- 文件传输队列、断点续传及 SHA-256 校验。
- 首次本地→SFTP 上传采用有界流水线，保留固定 Host Key、临时文件、源文件复核与安全提交。
- 通过独立安装的 WinFsp 和 SSHFS-Win 将手机存储挂载成 Windows 盘符。
- Android 开机启动开关、熄屏共享设置、系统文件选择器集成。

![Windows 双栏文件管理演示，示例目录与占位文件](assets/showcase/dual-pane.png)

<details>
<summary>查看更多界面图：传输队列与盘符挂载</summary>

![文件传输队列演示](assets/showcase/transfer.png)

![手机存储盘符挂载演示](assets/showcase/mounted-drive.png)

</details>

## 安装与使用

从 Releases 下载 Windows 安装程序或 Android APK。Windows 安装程序面向当前用户，创建开始菜单入口和可选桌面快捷方式。

磁盘挂载需要另外安装 [WinFsp](https://github.com/winfsp/winfsp/releases) 和 [SSHFS-Win](https://github.com/winfsp/sshfs-win/releases)。这些驱动不随本项目分发。

手机授予存储访问权限并启动共享服务。电脑打开设备管理中的扫码连接入口，手机扫描电脑二维码，在电脑确认后完成配对。

Android 后台、自启动和熄屏运行受系统限制。开机启动和“服务被系统结束后自动恢复”为独立选项，自动恢复默认关闭；传输唤醒锁由真实文件读写触发，空闲后释放。长时间后台或熄屏可靠性仍需按设备验证；本轮性能测试没有要求开启自启动或电池无限制。强制停止应用后需要手动重新打开。

## 构建

Windows 需要 .NET 8 SDK；Android 需要 JDK 17 和 Android SDK 34。设置本机 JAVA_HOME、ANDROID_HOME，或通过 local.properties 指定 SDK 路径。

```powershell
dotnet restore windows/PhoneDrive-Windows/PhoneDrive.sln
dotnet build windows/PhoneDrive-Windows/PhoneDrive.sln -c Release
dotnet test windows/PhoneDrive-Windows/PhoneDrive.sln -c Release
cd android/PhoneDrive-Android
.\gradlew.bat assembleDebug testDebugUnitTest
```

Android release 签名由开发者自己的 keystore.properties 配置；私钥和密码不在仓库中。自行签名的版本通常不能覆盖官方签名版本。

Windows 安装器源码位于 installer/PhoneDrive.iss，需要 Inno Setup 6，可用 `/DBuildVersion=版本号` 指定安装包版本。公开版本不包含本机配置、配对记录或调试符号。

## 状态与验证边界

本版本作为正式版发布。Windows 与 Android 发布构建通过；Core 48 项、Android 11 项单元测试及实际 SFTP 集成验证通过。0.2.6 同功能候选包已在 Note11 验证扫码页竖屏及当前可用局域网下启动服务；正式包统一版本标识后重新构建。Windows 配对窗口实际操作、多网卡切换后重新扫码、Wi-Fi 关闭/未连接的完整真机提示流程，以及各厂商长时间后台兼容性仍需继续验证。详见[本版验证范围](docs/release-0.2.6.md)。

## 作者与开发协作

- **项目发起者与维护者：** [palyer313](https://github.com/palyer313)，负责项目方向、需求、设计取舍与实际使用反馈。
- **AI 开发协作：** OpenAI Codex，参与代码审查与修复、界面与图标设计、测试、安装包制作及发布准备。
- **演示截图：** 从用户提供的 MiniMax M3.1-Flash 演示视频中截取，见[素材说明](docs/media/README.md)。

## 参考项目与致谢

感谢以下开源项目及其作者。下表按实际用途说明参考关系；具体版权与许可证声明见 [NOTICE.md](NOTICE.md) 和 [third_party/](third_party/)。

### 界面布局改编

[WinSCP](https://github.com/winscp/winscp) 是 Windows 双栏 Commander 界面的主要来源。PhoneDrive 依据其 [ScpCommander.dfm](https://github.com/winscp/winscp/blob/master/source/forms/ScpCommander.dfm) 的面板、工具栏、路径栏、文件列表及状态栏结构，使用 WPF 重新实现并接入自身服务。原始资源与许可证保存在 [third_party/references/winscp/](third_party/references/winscp/)，对应关系见 [布局移植说明](docs/winscp-layout-port.md)。

### 方案研究与设计参考

以下项目在前期方案研究中提供了参考；列入此处不表示直接依赖或复制其代码。

| 项目 | 参考内容 |
| --- | --- |
| [Primitive FTPd](https://github.com/wolpi/prim-ftpd) | Android 文件共享、存储访问适配、SFTP 服务与公钥认证方案。 |
| [Sshd4a](https://github.com/tfonteyn/Sshd4a) | Android SSH/SFTP 服务及后台服务的产品形态。 |
| [sshfs-win-tray](https://github.com/huangzhenming/sshfs-win-tray) | Windows SSHFS 挂载管理、托盘交互与便携配置思路。 |
| [LocalSend](https://github.com/localsend/localsend) | 局域网设备发现、跨设备文件传输及低配置连接体验。 |
| [KDE Connect](https://github.com/KDE/kdeconnect-android) | 跨设备协作、配对确认与设备信任流程。 |

### 挂载组件与主要依赖

| 项目 | 在 PhoneDrive 中的用途 |
| --- | --- |
| [WinFsp](https://github.com/winfsp/winfsp) | Windows 文件系统挂载基础，由用户独立安装。 |
| [SSHFS-Win](https://github.com/winfsp/sshfs-win) | 以独立进程把远端 SFTP 存储挂载为 Windows 盘符，由用户独立安装。 |
| [Apache MINA SSHD](https://github.com/apache/mina-sshd) | Android SFTP 服务端与客户端。 |
| [SSH.NET](https://github.com/sshnet/SSH.NET) | Windows SFTP 连接与文件操作。 |
| [Tmds.MDns](https://github.com/tmds/Tmds.MDns) | Windows 局域网 mDNS 设备发现。 |
| [QRCoder](https://github.com/codebude/QRCoder) | Windows 配对二维码生成。 |
| [ZXing](https://github.com/zxing/zxing) / [ZXing Android Embedded](https://github.com/journeyapps/zxing-android-embedded) | Android 二维码生成与扫描。 |
| [Android Jetpack Compose](https://developer.android.com/compose) | Android 应用界面。 |

## 许可证

本项目按 GPL-3.0 发布，见 LICENSE。Windows Commander 布局改编自 WinSCP 的 ScpCommander.dfm；版权及第三方声明见 NOTICE.md 和 third_party/。第三方依赖保留各自许可证。
