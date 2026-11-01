# PhoneDrive

<img src="assets/brand/phonedrive.png" width="104" alt="PhoneDrive icon">

Android 手机、平板与 Windows 电脑之间的局域网文件管理、传输和磁盘挂载工具。当前预发布版本：**0.2.4**。

## 功能

- Windows 双栏文件管理器，使用 Windows Shell 文件类型图标。
- Android SFTP 共享服务、局域网设备发现、二维码配对和固定 Host Key 校验。
- 文件传输队列、断点续传及 SHA-256 校验。
- 通过独立安装的 WinFsp 和 SSHFS-Win 将手机存储挂载成 Windows 盘符。
- Android 开机启动开关、熄屏共享设置、系统文件选择器集成。

## 安装与使用

从 Releases 下载 Windows 安装程序或 Android APK。Windows 安装程序面向当前用户，创建开始菜单入口和可选桌面快捷方式。

磁盘挂载需要另外安装 [WinFsp](https://github.com/winfsp/winfsp/releases) 和 [SSHFS-Win](https://github.com/winfsp/sshfs-win/releases)。这些驱动不随本项目分发。

手机授予存储访问权限并启动共享服务。电脑打开设备管理中的扫码连接入口，手机扫描电脑二维码，在电脑确认后完成配对。

Android 后台、自启动和熄屏运行受系统限制；部分厂商需要手动允许自启动及取消电池限制。强制停止应用后需要手动重新打开。

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

Windows 安装器源码位于 installer/PhoneDrive.iss，需要 Inno Setup 6。公开版本不包含本机配置、配对记录或调试符号。

## 状态与验证边界

这是预发布软件。Windows 与 Android 发布构建通过；已有核心测试及隔离 SFTP/SSHFS 读写、随机读取、错误指纹拒绝和容量协议测试。曾完成亮屏共享条件下的实际连接与挂载检查。当前 Android 长时间熄屏、自启动和各厂商系统兼容性仍需更多真机验证。

## 许可证

本项目按 GPL-3.0 发布，见 LICENSE。Windows Commander 布局改编自 WinSCP 的 ScpCommander.dfm；版权及第三方声明见 NOTICE.md 和 third_party/。第三方依赖保留各自许可证。
