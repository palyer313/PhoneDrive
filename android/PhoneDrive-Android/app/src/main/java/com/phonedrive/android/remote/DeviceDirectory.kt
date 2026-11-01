package com.phonedrive.android.remote

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/**
 * 已配对设备记录。
 *
 * hostKeyFingerprint 为配对时用户确认过的对端 Host Key 指纹（"SHA256:..."），
 * 每次建立 SFTP 连接都会强制比对，指纹变化视为身份冲突，禁止连接。
 */
data class PairedDevice(
    val deviceId: String,
    val name: String,
    val type: String,
    val hostKeyFingerprint: String,
    val lastKnownIp: String?,
    val lastKnownPort: Int,
    val lastSeenAt: Long,
    /** 对端客户端公钥（base64 blob）；手动配对时可能为空 */
    val peerKeyBlob: String? = null,
    /** 对端 Host Key 公钥行（OpenSSH 单行），配对响应成功后保存 */
    val peerHostPub: String? = null,
)

/**
 * 配对设备存储：filesDir/paired_devices.json。
 * 不引入 Room，直接用 org.json 持久化，读写均加锁并原子落盘。
 */
class DeviceDirectory private constructor(context: Context) {

    private val file = File(context.applicationContext.filesDir, "paired_devices.json")

    private val _devices = MutableStateFlow<List<PairedDevice>>(emptyList())
    val devices: StateFlow<List<PairedDevice>> = _devices.asStateFlow()

    init {
        synchronized(this) {
            _devices.value = load()
        }
    }

    fun list(): List<PairedDevice> = _devices.value

    fun find(deviceId: String): PairedDevice? =
        _devices.value.firstOrNull { it.deviceId == deviceId }

    fun observe(): StateFlow<List<PairedDevice>> = devices

    /** 新增或覆盖同 deviceId 的记录 */
    @Synchronized
    fun add(device: PairedDevice): Boolean {
        val list = list().filterNot { it.deviceId == device.deviceId } + device
        commit(list)
        return true
    }

    @Synchronized
    fun update(device: PairedDevice): Boolean {
        if (find(device.deviceId) == null) return false
        val list = list().map { if (it.deviceId == device.deviceId) device else it }
        commit(list)
        return true
    }

    /** 更新最近已知地址与最近见到时间（不改变配对信任信息） */
    @Synchronized
    fun touch(deviceId: String, ip: String?, port: Int, seenAt: Long = System.currentTimeMillis()): Boolean {
        val old = find(deviceId) ?: return false
        val updated = old.copy(lastKnownIp = ip ?: old.lastKnownIp, lastKnownPort = port, lastSeenAt = seenAt)
        val list = list().map { if (it.deviceId == deviceId) updated else it }
        commit(list)
        return true
    }

    @Synchronized
    fun remove(deviceId: String) {
        commit(list().filterNot { it.deviceId == deviceId })
    }

    private fun commit(list: List<PairedDevice>) {
        _devices.value = list
        val array = JSONArray()
        list.forEach { array.put(it.toJson()) }
        atomicWrite(file, array.toString())
    }

    private fun load(): List<PairedDevice> {
        if (!file.exists()) return emptyList()
        return runCatching {
            val array = JSONArray(file.readText())
            buildList {
                for (i in 0 until array.length()) {
                    add(parsePairedDevice(array.getJSONObject(i)))
                }
            }
        }.getOrDefault(emptyList())
    }

    companion object {
        @Volatile
        private var instance: DeviceDirectory? = null

        fun getInstance(context: Context): DeviceDirectory =
            instance ?: synchronized(this) {
                instance ?: DeviceDirectory(context).also { instance = it }
            }

        internal fun atomicWrite(target: File, content: String) {
            val tmp = File(target.parentFile, target.name + ".tmp")
            tmp.writeText(content)
            if (!tmp.renameTo(target)) {
                target.writeText(content)
                tmp.delete()
            }
        }
    }
}

internal fun PairedDevice.toJson(): JSONObject = JSONObject().apply {
    put("deviceId", deviceId)
    put("name", name)
    put("type", type)
    put("hostKeyFingerprint", hostKeyFingerprint)
    put("lastKnownIp", lastKnownIp ?: "")
    put("lastKnownPort", lastKnownPort)
    put("lastSeenAt", lastSeenAt)
    put("peerKeyBlob", peerKeyBlob ?: "")
    put("peerHostPub", peerHostPub ?: "")
}

internal fun parsePairedDevice(obj: JSONObject): PairedDevice = PairedDevice(
    deviceId = obj.optString("deviceId"),
    name = obj.optString("name"),
    type = obj.optString("type", "phone"),
    hostKeyFingerprint = obj.optString("hostKeyFingerprint"),
    lastKnownIp = obj.optString("lastKnownIp").ifBlank { null },
    lastKnownPort = obj.optInt("lastKnownPort", 2222),
    lastSeenAt = obj.optLong("lastSeenAt", 0L),
    peerKeyBlob = obj.optString("peerKeyBlob").ifBlank { null },
    peerHostPub = obj.optString("peerHostPub").ifBlank { null },
)
