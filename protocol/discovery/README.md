# PhoneDrive 设备发现协议（Discovery Protocol）v1

基于 mDNS / DNS-SD（RFC 6762 / RFC 6763）的局域网设备发现。

## 1. 服务类型

```
_phonedrive._tcp.local.
```

每台设备（Android 手机、平板、Windows 电脑）在 SFTP 服务启动后注册该服务。

## 2. 实例名

```
<DeviceName>._phonedrive._tcp.local.
```

DeviceName 为用户可读名称（如 `Redmi Note 11T Pro`），冲突时按 RFC 6762 追加 ` (2)`。

## 3. TXT 记录

| Key | 必填 | 说明 |
|---|---|---|
| `deviceid` | 是 | 设备唯一 ID，UUIDv4，首次启动生成后持久化，永不改变 |
| `name` | 是 | 设备显示名 |
| `type` | 是 | `phone` / `tablet` / `windows` |
| `proto` | 是 | 协议版本，当前为 `1` |
| `sftpport` | 是 | SFTP 服务端口（默认 2222，可配置） |
| `fp` | 是 | SSH Host Key 的 SHA-256 指纹（Base64 无填充，43 字符），用于配对后快速校验 |
| `caps` | 否 | 能力位，逗号分隔：`rw`=读写共享，`ro`=只读共享，`docs`=DocumentsProvider |
| `sw` | 否 | 客户端软件版本，如 `1.0.0` |

**禁止**在 TXT 记录中携带：密码、私钥、共享目录路径、用户名。

## 4. 端口

- mDNS：UDP 5353（系统标准）。
- SFTP：TCP 2222（Android 无 root，不能使用 22；Windows 端默认同为 2222，可配置）。

## 5. 动态 IP 处理

1. 网络变化（Wi-Fi 切换、DHCP 续租）→ 设备重新注册 mDNS 服务。
2. 发现端以 `deviceid` 作为设备身份主键，IP 仅作为瞬态寻址信息。
3. 已配对设备重新出现时，必须比对 `fp` 与本地保存的 Host Key 指纹：
   - 一致 → 自动恢复连接。
   - 不一致 → 标记为身份冲突，提示用户重新确认，**禁止静默连接**。

## 6. 备用发现机制（UDP 广播）

当路由器屏蔽 mDNS（组播被隔离）时降级：

- 向 `255.255.255.255:47820` 发送 JSON 广播（每 5s，仅服务启动后的前 60s 与收到查询时）。
- 负载：`{"proto":1,"deviceid":"...","type":"...","sftpport":2222,"fp":"..."}`。
- 无线客户端隔离（AP isolation）下两种方式均会失败，此时 UI 必须给出明确提示（验收标准 S06）。

## 7. 状态机

```
DISCOVERING → FOUND(unpaired) → PAIRING → PAIRED → CONNECTED
                  │                              │
                  └──────── LOST (3 次查询未响应) ┘
```
