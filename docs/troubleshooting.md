# PhoneDrive 故障排查

## 1. 找不到设备（手机 ↔ 电脑 / 手机 ↔ 手机）

| 现象 | 排查步骤 |
|---|---|
| 双方都看不到对方 | 1) 确认同一 Wi-Fi（不是"访客网络"、不是手机热点与路由混用）<br>2) 路由器若开了「AP 隔离 / 无线客户端隔离」，设备之间不可见 —— 必须关闭<br>3) 部分路由器默认屏蔽组播（mDNS），PhoneDrive 会用 UDP 广播兜底，但 AP 隔离下同样失效<br>4) 确认手机端 SFTP 服务已启动（通知栏应有常驻通知） |
| 电脑能搜到手机，手机搜不到电脑 | 正常。Windows 端不提供 SFTP 服务端，配对与连接由手机侧发起；Windows 只作为"加入方" |
| 之前能见，现在不见 | IP 变化属正常现象。PhoneDrive 以 Device ID 为身份、mDNS 为寻址，重新上线后会自动恢复；若仍不见，检查手机是否被系统杀掉后台服务 |

## 2. 配对失败

| 错误提示 | 原因与处理 |
|---|---|
| 「配对码错误（bad_code）」 | 6 位码输错。手机界面上的配对码 3 次输错后本次作废，需在手机上重新生成 |
| 「配对窗口已过期（code_expired）」 | 配对窗口仅 120 秒，重新在手机上点「配对这台设备」 |
| 「身份不匹配」 | 对端 Host Key 指纹与二维码/发现记录不一致。**这可能是中间人攻击**，不要重试，先确认网络环境；若是手机重装 App 导致 Host Key 重建，则需在 Windows 端撤销旧配对记录后重新配对 |
| 无法连接配对服务 | 确认 IP 与端口 47821；确认手机端配对页面还开着；Windows 防火墙不要拦截出站连接 |

## 3. 连接被拒绝（认证失败）

- 手机端 `authorized_keys` 中没有本机的客户端公钥 → 重新配对，或在手机「已授权设备」里手动添加公钥。
- 手机端撤销了授权 → 需要重新配对。
- 本机私钥损坏（DPAPI 解密失败，例如换了 Windows 用户）→ 删除 `%LOCALAPPDATA%\PhoneDrive\identity` 后重新配对。

## 4. 挂载盘符失败

| 现象 | 处理 |
|---|---|
| 「未找到 sshfs-win.exe」 | 安装 [SSHFS-Win](https://github.com/winfsp/sshfs-win/releases)（需先装 [WinFsp](https://github.com/winfsp/winfsp/releases)） |
| 「盘符 X: 已被占用」 | 在设备管理界面换一个盘符（Y:/Z:…） |
| 挂载后资源管理器打不开 | 1) `net use` 检查是否存在残留连接，先 `net use X: /delete`<br>2) 确认 WinFsp 服务在运行（服务名 `WinFsp.Launcher`）<br>3) 确认 `%USERPROFILE%\.ssh\known_hosts` 中存在 `[手机IP]:2222` 条目 |
| 挂载后文件名乱码 | sshfs 的 locale 设置问题；在 sshfs-win 命令行加 `-o locale=zh_CN.UTF-8` |

## 5. 视频无法播放 / 拖动进度条卡死

- 挂载方式（sshfs-win）：依赖 SFTP 随机读，网络抖动会导致卡顿。优先使用局域网 5GHz。
- PhoneDrive 内置播放器：已按 offset/length 做范围读取，但高码率 4K 视频仍受 Wi-Fi 带宽限制。
- 若第三方播放器把整个文件复制到本地后才播放，那是播放器行为，不是 PhoneDrive 的远程访问失效。

## 6. 传输失败 / 中断

- 传输任务会写入 `xxx.phonedrive-tmp`，校验通过后才改名并（move 时）删除源文件。**失败不会丢源文件**。
- 断线后任务进入失败待重试；重连后点「重试」会从临时文件大小处续传。
- 若源文件在传输期间被修改（大小/mtime 变化），会判定失败并提示重新传输。

## 7. Android 后台服务被杀

HyperOS / MIUI 默认策略较激进：

1. 设置 → 应用管理 → PhoneDrive → **自启动**：允许
2. 设置 → 应用管理 → PhoneDrive → **省电策略**：无限制
3. 设置 → 应用管理 → PhoneDrive → 通知：允许（前台服务通知不能关闭）
4. 最近任务里给 PhoneDrive **加锁**（避免一键清理杀死）

上述设置因机型/系统版本差异较大，**必须实机验证**；Boot Receiver 不保证开机自启一定成功。

## 8. 构建问题

| 现象 | 处理 |
|---|---|
| `Unsupported class file major version` | 用 JDK 17 构建 Android（`. scripts/env.ps1` 已设置 JAVA_HOME） |
| `SDK location not found` | 确认 `android/PhoneDrive-Android/local.properties` 中 `sdk.dir` 指向 Android SDK |
| Gradle wrapper 下载超时 | 把 `gradle-8.10.2-bin.zip` 放到 `%USERPROFILE%\.gradle\wrapper\dists\gradle-8.10.2-bin\<hash>\` 并创建同名 `.ok` 文件 |
| `dotnet: 找不到` | `. scripts/env.ps1` 把 `本机 .NET SDK 目录` 加入 PATH，或用系统安装的 .NET 8 SDK |

## 9. 日志位置

- Windows：`%LOCALAPPDATA%\PhoneDrive\`（devices.json / transfers.json / authorized_keys / identity）
- Android：`/data/data/com.phonedrive.android/files/`（authorized_keys / transfers.json / paired_devices.json）
- 日志中不记录私钥、配对码与公钥全文（仅记录指纹前缀）。
