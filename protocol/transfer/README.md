# PhoneDrive 传输协议（Transfer Semantics）v1

传输层 = SFTP（SSH File Transfer Protocol），本文件定义其上的任务语义。

## 1. 为什么用 SFTP

- 成熟加密与认证（SSH），无需自研加密协议。
- 原生支持随机读写（视频拖动进度条的根基）、断点续传（append/seek）。
- Android 端由 Apache MINA SSHD 提供服务；Windows 端由 SSH.NET / sshfs-win 消费。

## 2. 统一传输任务模型

```json
{
  "taskId": "uuid",
  "type": "copy | move",
  "source": {"deviceId": "...", "path": "/Download/a.zip"},
  "target": {"deviceId": "...", "path": "/Documents/a.zip"},
  "size": 123456789,
  "transferred": 0,
  "state": "queued|running|paused|verifying|done|failed|canceled",
  "error": null,
  "resumeOffset": 0,
  "sha256": "hex-or-null",
  "createdAt": 0, "updatedAt": 0
}
```

## 3. 复制流程（copy）

1. `STAT` 源文件，记录大小/mtime。
2. 目标端写入 `name.ext.phonedrive-tmp`（临时文件，同目录）。
3. 流式分块传输（256 KiB 块），实时更新进度/速度/剩余时间。
4. 写完后目标端 `STAT` 校验大小；后台计算 SHA-256（大文件抽样 + 全量可选）。
5. 原子 `RENAME` 去掉 `.phonedrive-tmp` 后缀。
6. 任务标记 done。

## 4. 移动流程（move）——安全序列

**目标文件未确认完整前，禁止删除源文件（验收 S08）。**

1. 执行完整 copy 流程（含校验）。
2. 二次 `STAT` 确认目标文件存在且大小一致。
3. 删除源文件。
4. 若步骤 1-2 任一步失败：保留源文件，清理目标临时文件，任务标记 failed。

## 5. 断点续传

- 任务持久化（Android: Room；Windows: LiteDB/JSON）。
- 恢复时检查目标 `.phonedrive-tmp` 大小，作为 `resumeOffset`。
- 以 append 模式继续写入；续传完成后照常校验。
- 源文件 mtime/size 变化则从头重传并提示用户。

## 6. 同名冲突策略

目标已存在同名文件时询问用户：

| 策略 | 行为 |
|---|---|
| 覆盖 | 删除旧文件后走正常流程 |
| 跳过 | 任务标记 done(skipped) |
| 重命名 | 自动改为 `name (2).ext` |
| 保留两份 | 同重命名 |

禁止默认静默覆盖。

## 7. 并发与限速

- 全局并发任务数默认 2，可在设置中调整（1-8）。
- 同一目标设备的并发连接 ≤ 3，避免打满手机端 SSH 服务。
- 单连接分块流水线（SFTP outstanding requests = 16）。

## 8. 错误与重试

- 网络错误：指数退避（1s/2s/4s/8s，上限 30s），最多自动重试 5 次，之后标记 failed 等待手动重试。
- 认证错误（Host Key 变化 / 公钥被拒）：立即停止并提示，不重试。
- 磁盘满：fail-fast，清理临时文件。
