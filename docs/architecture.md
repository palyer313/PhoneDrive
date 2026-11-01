# PhoneDrive 架构设计

版本：v1.0（Phase 0 产出）
依据：任务书 V1.0 + 开源审计结论（docs/license-audit.md）

## 1. 总体架构

```
                 局域网 / Wi-Fi
                       │
       ┌───────────────┼───────────────┐
       │               │               │
  Redmi 手机       Xiaomi 平板      Windows 电脑
  ┌─────────────┐ ┌─────────────┐ ┌──────────────────┐
  │ SFTP Server │ │ SFTP Server │ │ Device Manager   │
  │ (MINA SSHD) │ │ (MINA SSHD) │ │ SFTP Client      │
  │ SFTP Client │ │ SFTP Client │ │ (SSH.NET)        │
  │ (SSHJ/MINA) │ │ (SSHJ/MINA) │ │ File Manager     │
  │ FileManager │ │ FileManager │ │ (WPF 双栏)       │
  │ NSD/mDNS    │ │ NSD/mDNS    │ │ Drive Manager    │
  │ Keystore    │ │ Keystore    │ │ (sshfs-win svc)  │
  └─────────────┘ └─────────────┘ │ mDNS (Bonjour/   │
                                  │  Tmds.MDns)      │
                                  │ DPAPI            │
                                  └──────────────────┘
                       │
            _phonedrive._tcp（mDNS 发现）
            配对码 + SSH 公钥（配对与认证）
```

- 手机/平板**对等**：同时运行 SFTP 服务端与客户端，直接互传，Windows 不是中转。
- Windows 是管理终端 + 挂载终端，同样可被发现的客户端（Phase 1 不开放 Windows 的 SFTP 服务端）。

## 2. 模块划分与实现选型

| 模块 | Android 实现 | Windows 实现 |
|---|---|---|
| Device Discovery | Android NSD API（封装 mDNS）+ UDP 广播兜底 | Tmds.MDns（纯托管 mDNS）+ UDP 广播兜底 |
| Device Manager | Room 持久化设备表 | LiteDB 持久化 |
| Pairing Manager | 临时 Ktor HTTP 服务 + ZXing 二维码 | 临时 HttpListener + QRCoder |
| Security Manager | Android Keystore（EC P-256 主，RSA-3072 备） | DPAPI（ProtectedData）+ CNG |
| SFTP Server | Apache MINA SSHD 2.12.x（Apache-2.0） | 不开放 |
| SFTP Client | Apache MINA SSHD client | SSH.NET 2024.x（MIT） |
| File Provider | `FileProvider` 抽象接口（见 §4） | `IFileProvider` 抽象接口 |
| Transfer Engine | Kotlin Coroutines + Room 任务表 | TPL Dataflow + LiteDB 任务表 |
| Drive Manager | — | sshfs-win svc 子进程编排 + Host Key 预固定 |
| File Manager UI | Jetpack Compose（手机单栏/平板双栏响应式） | WPF（WinSCP Commander 双栏） |
| DocumentsProvider | Android DocumentsProvider（Phase 6） | — |
| Preview Engine | Coil（图片）/ Media3（视频流式） | Windows Shell 关联 + 缩略图缓存 |

## 3. 关键设计决策

### D1：SFTP 为唯一传输与远程访问协议
- 复用 SSH 的加密、认证、随机读、断点续传，不自研传输协议。
- 视频拖动进度条依赖 SFTP 的随机读语义，不做整文件预下载。

### D2：身份 = DeviceID + Host Key 指纹
- IP 仅瞬态寻址；设备重新发现后按 DeviceID 关联，按指纹校验（见 protocol/discovery）。
- 规避 sshfs-win-tray 的 `StrictHostKeyChecking=no` 缺陷：PhoneDrive 挂载时先把配对指纹写入 known_hosts 语义等效物，强制校验。

### D3：Windows 挂载不重复造轮子
- 挂载 = 编排 `sshfs-win.exe svc` 子进程，不链 WinFsp/sshfs 代码（GPL 隔离）。
- 双栏文件管理器直接走 SSH.NET，与挂载共用 Device Manager 的身份与连接信息。

### D4：跨设备移动安全序列
写临时文件 → 大小校验 → SHA-256 → 原子重命名 → 二次确认 → 删源文件。任何一步失败保源文件（protocol/transfer §4）。

### D5：共享协议文档先行
`protocol/` 下三份规范（discovery / pairing / transfer）为 Android 与 Windows 实现的共同契约，字段与状态机以文档为准。

## 4. 文件访问抽象层

```text
IFileProvider
  ├── LocalFileProvider        (Android 本地 / Windows 本地)
  ├── AndroidSafProvider       (SAF 授权目录)
  ├── SftpFileProvider         (远程已配对设备)
  └── WindowsDriveProvider     (已挂载盘符直读)

统一操作：ListDirectory / GetFileInfo / Read(stream, offset) /
Write(stream, offset) / CreateDirectory / Delete / Rename /
Move / Copy / Search / GetMetadata / GetCapabilities
```

能力位（Capabilities）：`RandomRead` `AtomicRename` `FileLock` `Resume` `Search` `Mtime`，UI 按能力位启用/禁用功能。

## 5. Android 后台运行策略（务实版）

- Foreground Service（`dataSync` 类型）持有 SFTP 服务与 NSD 注册。
- `BOOT_COMPLETED` + `CONNECTIVITY_ACTION` 接收器尝试自恢复。
- **明确限制**：HyperOS 对自启动/后台有额外管控，Boot Receiver 不保证生效；App 内提供"自启动权限 / 电池优化白名单"引导页跳转系统设置。未经实机验证的功能一律标记"未验证"。

## 6. 安全边界

- 仅局域网；不申请 INTERNET 之外的敏感网络能力；无任何公网服务器。
- SFTP 仅 `publickey` 认证，禁用密码认证与 shell/exec 通道（只开 SFTP 子系统）。
- 共享根目录默认 = 外部共享存储，SAF 授权目录可追加；私有目录不暴露。
- 私钥不出 Keystore/DPAPI；日志脱敏（不记录密钥、指纹全串、路径白名单外目录）。

## 7. 已实现模块清单（实际代码位置）

### Android（`android/PhoneDrive-Android/app/src/main/java/com/phonedrive/android/`）

| 包 | 职责 |
|---|---|
`server/` | SFTP 服务端（MINA SSHD）、authorized_keys、开机自启、前台服务
`security/` | DeviceIdentity：Device ID + Keystore Host Key（RSA-3072）
`discovery/` | `NsdAdvertiser`：mDNS 注册 `_phonedrive._tcp`
`remote/` | `PeerDiscovery`（NsdManager 发现）、`DeviceDirectory`（配对库）、`ClientKey`（Keystore 客户端密钥 + SSHD 签名）、`SftpClientSession`（指纹强校验的 SFTP 会话）、`ConnectionSupervisor`（自动重连）
`filemanager/` | `FileEntry` / `FileProvider` / `LocalFileProvider` / `SftpFileProvider` / `ProviderHub` / `BookmarkStore`
`transfer/` | `TransferEngine`（并发/续传/冲突策略/move 安全序列）、`TransferService`（前台服务）、`TransferStore`
`pairing/` | `PairingSession`（6 位码状态机）、`PairingServer`（手写 HTTP :47821）、`PairingQr`、`PairingClient`
`docs/` | `PhoneDriveDocumentsProvider`（系统文件选择器集成）
`ui/` | `AppRoot`（底部导航）、`FilesScreen`（双栏/多标签/操作/预览/搜索）、`DevicesScreen`（配对/撤销）、`TransfersScreen`、`FavoritesScreen`、`SettingsScreen`

### Windows（`windows/PhoneDrive-Windows/src/`）

| 目录 | 职责 |
|---|---|
`PhoneDrive.Core/Models` | `PairedDevice` / `DiscoveredDevice`
`PhoneDrive.Core/Security` | `ClientIdentity`（DPAPI 私钥、OpenSSH 公钥与 SHA256 指纹）
`PhoneDrive.Core/Sftp` | `SftpSession`（SSH.NET + `HostKeyReceived` 强制指纹校验）
`PhoneDrive.Core/Discovery` | `DeviceDiscovery`（Tmds.MDns + UDP 兜底）
`PhoneDrive.Core/Files` | `IFileProvider` / `LocalFileProvider` / `SftpFileProvider` / `MountedDriveProvider` / `SubReadStream`（随机读）
`PhoneDrive.Core/Transfer` | `TransferEngine`（每任务独立 provider、并发、续传、SHA-256、重试、持久化、速度/ETA）
`PhoneDrive.Core/Pairing` | `PairingClient`（加入方）、`PairingModels`、`PeerKeyStore`
`PhoneDrive.Core/Mount` | `DriveMountService`（sshfs-win 编排、known_hosts 预固定、盘符冲突）
`PhoneDrive.Core/Storage` | `DeviceStore`（devices.json）
`PhoneDrive.Wpf/Views` | `PaneView`（窗格+标签）、`DeviceManagerWindow`、`SettingsWindow`、`PropertiesWindow`、`PairInputWindow`
`PhoneDrive.Wpf/ViewModels` | `MainViewModel`、`PaneViewModel`、`TabViewModel`、`DeviceManagerViewModel`、`SettingsViewModel`
`PhoneDrive.Wpf/Services` | `ConnectionManager`、`ConnectionSupervisor`、`TransferManager`、`FileOperationService`、`TrayIconService`、`StartupService`、`DeviceMountService`、`UiStateStore`、`ThemeManager`
`tests/PhoneDrive.Core.Tests` | 17 个 xUnit 用例

### 协议与文档

`protocol/discovery`、`protocol/pairing`（含 `GET /info`、`POST /pair` 的完整接口契约）、`protocol/transfer`（copy/move 安全序列、续传、冲突策略）。

## 8. 工程结构

见任务书 §17，已按此创建：`android/ windows/ protocol/ third_party/ scripts/ docs/ dist/`。
