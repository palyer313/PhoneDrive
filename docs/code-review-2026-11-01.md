# PhoneDrive 全项目代码审查报告（2026-11-01）

范围：Android 端全部 Kotlin（35 文件）、Windows Core 全部 C#（9 文件）、Windows WPF 全部（约 40 文件）。
方法：三路独立审查 → 逐条人工复核代码证据 → 修复 → 双端重新编译 + 全量测试。

## 复核结论：21 个疑似项中确认 15 个真 bug 并全部修复

### Android（6 项）

| 严重度 | 问题 | 修复 |
|---|---|---|
| 高 | 源文件变化后断点续传会把旧临时文件内容拼进新文件，大小校验可通过 → 静默产出损坏文件（TransferEngine.kt） | sourceChanged 时删除临时文件后从头传 |
| 高 | 目录传输暂停后恢复，resolveTarget 把整棵树复制到 "dirname (2)"，产生全量重复副本 | 恢复时（transferred>0）跳过重命名沿用原路径；子文件大小一致则跳过 |
| 高 | 远端删除非空目录用非递归 rmdir 必然失败；跨设备移动目录死于删源步骤 | SftpClientSession.delete 改为递归删除（lstat + readDir，符号链接不跟随） |
| 中 | 同 provider 把目录复制到自身子目录导致自递归 | execute 中检测 dst 位于 src 子树内直接拒绝 |
| 中 | stopServer 把 sshd.stop 塞进即将被 cancel 的 scope，协程未调度即被取消 → 2222 端口泄漏，下次启动 BindException | 改为同步停止 |
| 中 | AuthorizedKeysStore 的 @Synchronized 锁在实例上，各调用点都 new 新实例 → 锁失效，add/remove 并发丢授权行 | 改为 companion 类级锁 |

### Windows Core（5 项）

| 严重度 | 问题 | 修复 |
|---|---|---|
| 高 | 本地目标断点续传必失败：OpenAppend 用 FileShare.None 打开后再 HashExisting 读同一文件 → IOException（TransferEngine.cs） | 先哈希前缀再开输出流；回退重传时 sha.Initialize() 重置 |
| 高 | Host Key 不匹配时 SSH.NET 在 Connect 内部直接抛 SshConnectionException，mismatch 分支不可达 → 调用方无法区分 MITM 与网络错误，且 client 泄漏（SftpSession.cs） | Connect 包 try/catch，释放 client，mismatch 优先抛出，普通错误保留原异常 |
| 中 | MaxConcurrency setter 替换 SemaphoreSlim 的竞态：在途任务持旧 gate，瞬时并发突破上限 | 移除信号量，改用 _activeWork 计数闸门；pump 对 Paused/超限任务放回队列 |
| 中 | DeviceStore 无锁读 vs 加锁写 → InvalidOperationException/撕裂；SaveAsync 非原子，崩溃截断丢全部配对记录 | 读进锁；SaveAsync 写临时文件后 Move 覆盖 |
| 中 | PinHostKey 读-改-写无锁，两台设备并发写 known_hosts 丢更新 | 加静态锁 |
| 低 | PairingClient 末步 UpsertAsync 磁盘 IO 失败时异常直接冒泡 | try/catch 返回配对失败结果 |

### Windows WPF（4 项）

| 严重度 | 问题 | 修复 |
|---|---|---|
| 高 | 同窗格内自拖放未排除：复制生成 "xxx (2)" 副本，Shift 移动把原文件改名（PaneView.xaml.cs） | payload.Source 与目标 pane 同一实例时直接忽略 |
| 中 | 传输任务进入终态后 ICollectionView.Filter 不重新评估，"显示历史"未勾选时已完成任务残留列表（TransferManager.cs） | 终态时 View.Refresh() |
| 中 | 导航失败（设备离线/路径不存在）污染历史栈：CurrentPath 未变但 _back 多一条、_forward 被清空（TabViewModel.cs） | LoadAsync 返回成功与否，成功才写栈，失败回滚 |
| 低 | "类型"排序实际按文件名比较，与按名称排序效果相同（TabViewModel.cs） | 改用扩展名比较 |

## 复核后判定为"非 bug / 可接受"的项（不修）

- mDNS 下线按实例名反查不完美：下线通告本身不带 TXT，是协议层限制，影响仅为设备列表短暂残留，重新发现会覆盖。
- mDNS TXT fp 与配对指纹格式：Android 端两处都带 "SHA256:" 前缀，实际一致；协议文档措辞已澄清。
- 手动配对的 TOFU 语义（指纹取自同一 HTTP 通道）：与扫码路径安全级别不同属设计取舍；UI 已在配对成功弹窗要求用户核对指纹，扫码路径不受影响。
- ProviderHub.providerForBlocking 双会话竞争、SettingsScreen 主线程生成密钥：影响轻微（首次进入设置页一次性开销），记录在案后续优化。
- RegisterMoveGroup 入字典时序：仅极端并发下源空目录残留，无数据风险。

## 验证

- Windows：`dotnet test` 17/17 通过（含新增 mpint 断言）；`dotnet publish` Release 成功。
- Android：`assembleDebug` + `assembleRelease` BUILD SUCCESSFUL；`testDebugUnitTest` 4/4 通过。
- 修复后的包已发布到 `dist/`。
