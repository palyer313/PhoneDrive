# PhoneDrive 配对协议

**当前实现为 [v2 固定 TLS 身份与密钥证明](v2.md)。下文 v1 是原始设计历史记录，已停用，不能作为当前配对实现契约。当前客户端拒绝不安全的 v1 配对。**

## 历史 v1 设计（不再使用）

首次配对建立双向信任；配对后不再需要用户介入。本文档是 Android 端与 Windows 端的**实现契约**，两端必须严格一致。

## 1. 密钥模型

每台设备持有两个 RSA-3072 密钥：

| 密钥 | 用途 | Android 存储 | Windows 存储 |
|---|---|---|---|
| Host Key | SFTP 服务端身份 | Keystore 别名 `phonedrive_host_key` | DPAPI `client_key.protected`（同一密钥兼作 host/client） |
| Client Key | SFTP 客户端身份 | Keystore 别名 `phonedrive_client_key` | 同上 |

- 设备唯一 ID：UUIDv4，首次启动生成后持久化。
- 指纹格式：`SHA256:<base64(sha256(sshWireEncode(hostPub)))>`，base64 去填充。
- 公钥格式：OpenSSH 单行 `ssh-rsa <base64> <comment>`。

## 2. 配对流程（二维码，主流程）

```
发起方 A（显示二维码）                     加入方 B（扫码 / 手输）
──────────────────────                    ──────────────────────
生成 6 位配对码 code（仅本次有效）
启动临时配对 HTTP 服务 :47821（≤120s）
二维码内容：                               扫码得到 JSON 后直连 A
{"v":1,"ip":"192.168.x.x","port":47821,
 "code":"123456","deviceid":"A",
 "name":"A","type":"phone","fp":"SHA256:..."}

                                          GET /info → 校验可达
                                          POST /pair（见 §3）
校验 code（错误 3 次本次作废）
写入 B.clientPub → A.authorized_keys
保存配对记录（B 的 hostFp / hostPub）        ← 200 响应含 A 的 hostFp/hostPub/clientPub
                                          写入 A.clientPub → B.authorized_keys
                                          保存配对记录
双方弹确认页：设备名 / 类型 / Host Key 指纹
用户确认（两端都确认后才算完成）             用户确认
关闭临时 HTTP 服务
```

配对码防止同网段陌生人抢连；二维码不在身边则无法获知 code。

## 3. 配对 HTTP 接口

**仅监听局域网接口**，端口 47821，配对窗口关闭即停服务。

### 3.1 `GET /info`

响应 200：
```json
{"v":1,"deviceid":"...","name":"...","type":"phone|tablet|windows",
 "fp":"SHA256:...","sftpport":2222}
```

### 3.2 `POST /pair`

请求体：
```json
{"v":1,"code":"123456",
 "deviceid":"B","name":"B","type":"phone",
 "hostFp":"SHA256:...","hostPub":"ssh-rsa AAAA... B",
 "clientPub":"ssh-rsa AAAA... B"}
```

响应：

| 状态 | 体 | 含义 |
|---|---|---|
| 200 | `{"ok":true,"deviceid","name","type","hostFp","hostPub","clientPub"}` | 成功，字段为**发起方 A** 的对应信息 |
| 401 | `{"ok":false,"error":"bad_code"}` | 配对码错误 |
| 409 | `{"ok":false,"error":"code_expired"}` | 窗口已过期 / 错误次数超限 |
| 400 | `{"ok":false,"error":"bad_request"}` | 字段缺失或格式非法 |

成功时 A 必须：把 `B.clientPub` 追加到 `authorized_keys`，保存 B 的 `hostFp/hostPub/deviceid`。

B 收到 200 后必须：把 `A.clientPub` 追加到 `authorized_keys`，保存 A 的 `hostFp/hostPub`，并校验 `A.hostFp` 与二维码里的 `fp` **完全一致**；不一致立即中止并提示"身份不匹配"。

## 4. 手动输入方式（扫码不可用时）

B 手动输入 `IP + 端口(默认47821) + 6 位配对码`，后续流程与 §3 完全相同。

## 5. 安全约束

1. 配对 HTTP 服务仅绑定局域网地址，窗口期 ≤ 120s，成功/超时立即关闭。
2. 配对码 6 位数字，错误 3 次本次会话作废。
3. Host Key 指纹双向固定（TOFU + 用户双方确认）；指纹变化必须重新配对确认。
4. `authorized_keys` 只保存公钥；私钥永不导出、永不写日志。
5. 撤销授权：删除配对记录 → 从 `authorized_keys` 移除对方公钥 → 对方后续连接被拒绝。
6. 不实现"跳过确认"路径；任何自动连接都必须在已配对记录 + 指纹一致的前提下进行。

## 6. 自动重连

| 场景 | 行为 |
|---|---|
| 设备重新上线（mDNS 重现） | 按 `deviceid` 关联配对记录，比对 mDNS TXT 的 `fp` 与已保存 `hostFp`；一致才连接 |
| 指纹不一致 | 标记身份冲突，停止自动连接，UI 提示用户重新配对 |
| 连接失败 | 指数退避 1s→2s→4s→8s→16s（上限 30s），持续重试；认证失败不重试 |
| IP 变化 | 用最新发现的 address/port 覆盖 `lastKnownIp/lastKnownPort` |
