# PhoneDrive 测试计划

所有验收项遵循任务书 §15：模拟器测试不能代替真机测试；未在真机验证的功能标记为"未验证"。

## 测试环境

| 项 | 配置 |
|---|---|
| Windows 开发机 | Windows 10/11，.NET 8.0.427，PhoneDrive.exe（本机构建） |
| Android 目标机 A | Redmi Note 11T Pro，HyperOS 2 / Android 14 |
| Android 目标机 B | Xiaomi Pad 6 |
| 网络 | 同一局域网 Wi-Fi（另需测试 AP 隔离/无组播环境） |

## Phase 1 测试项

### P1-T01 Android SFTP 服务启动（待真机）
- 步骤：安装 APK → 授予所有文件访问 → 点击"启动服务"
- 预期：前台服务通知出现，状态显示"运行中 · 端口 2222"，显示 IP 与指纹
- 结果：未验证

### P1-T02 mDNS 广播（待真机）
- 步骤：服务运行后，Windows 端 `dns-sd -B _phonedrive._tcp` 或 PhoneDrive.exe 设备列表
- 预期：发现设备，TXT 含 deviceid/name/type/proto/sftpport/fp
- 结果：未验证

### P1-T03 授权与连接（待真机）
- 步骤：Windows 端复制本机公钥 → Android"添加公钥"粘贴 → Windows 端"配对（保存指纹）"→"连接并浏览"
- 预期：连接成功，列出 `/`（= 共享存储根目录）
- 结果：未验证

### P1-T04 Host Key 校验（安全）
- 步骤：配对后篡改本机保存的指纹，重连
- 预期：连接被拒，报"指纹不匹配"
- 结果：单元级已覆盖逻辑；端到端未验证

### P1-T05 文件下载 + 完整性（待真机）
- 步骤：浏览到 DCIM，下载一个 >100MB 视频
- 预期：进度实时更新，SHA-256 校验通过，大小一致
- 结果：未验证

### P1-T06 图片/视频直接打开（待真机）
- 步骤：挂载盘符后双击 JPG / MP4
- 预期：照片应用直接打开；VLC 播放且可拖动进度条
- 依赖：WinFsp + SSHFS-Win 已安装
- 结果：未验证（本机未装 WinFsp）

### P1-T07 断线恢复（待真机）
- 步骤：传输中关闭手机 Wi-Fi → 恢复
- 预期：任务自动重试，设备重新上线后恢复，源文件不丢失
- 结果：未验证

## 已通过项（构建验证）

| 项 | 结果 |
|---|---|
| Windows PhoneDrive.sln 编译 | ✅ Debug 构建成功（PhoneDrive.exe） |
| Android assembleDebug | ✅ BUILD SUCCESSFUL（app-debug.apk 17.6 MB，2026-11-01） |
| SSH.NET 漏洞版本 | ✅ 已升级到 2025.0.0（修复 NU1903 公告） |

详见 docs/test-report.md。
