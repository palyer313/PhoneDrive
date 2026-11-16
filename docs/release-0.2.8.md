# PhoneDrive 0.2.8 发布说明

## 改动

- Windows 和 Android 的传输队列区分失败、跳过和非致命警告。两端加入传输错误分类，网络错误按退避策略重试。
- Windows 增加同名目录保护，文件覆盖提交失败时尝试恢复原目标，并记录保留的备份位置。同步遇到文件与目录的类型冲突时会停止该项操作。
- Android 目录移动在目标提交后先暂存源文件，再分阶段删除。删除失败时报告部分结果，并尝试恢复尚未删除的项。
- Android 调整会话连接状态和异常处理，改进授权列表与传输记录的写入和故障诊断。
- Windows 盘符挂载使用 Windows DPAPI 保护挂载凭据，并缩短供 SSHFS-Win 使用的临时明文密钥的保留时间。重新挂载由 PhoneDrive 管理。

## 安装与回退

- Windows 退出托盘中的旧版后，可用安装包覆盖升级，或解压免安装包并运行 Windows/PhoneDrive.exe。保留原有 AppData/Local/PhoneDrive 配对和设置目录。
- Android 使用官方 APK 覆盖安装。版本号为 0.2.8，versionCode 为 10，沿用 0.2.7 的发布签名。请保留应用数据。
- Windows 可退出新版并重新安装 0.2.7 回退。Android 系统通常会阻止覆盖安装较低的 versionCode，回退需用同签名且版本号满足系统要求的包。

## 验证范围

- Windows x64 自带运行时发布构建通过，52 项 Core 测试通过。
- Android 签名 Release 构建、发布版 lint 检查和 22 项单元测试通过。
- 独立测试目录中的 10 项分享接收检查通过，包含实际认证和固定 Host Key 的 SFTP 回环传输。
- 日用环境覆盖安装、真实手机往返传输与相册分享、SSHFS-Win 实际挂载和断线重挂、大文件性能尚未验收。

发布页提供 Windows 安装版和免安装版、Android APK、对应源码和 SHA-256 校验清单。
