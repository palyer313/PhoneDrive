package com.phonedrive.android.security

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.io.ByteArrayOutputStream
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.interfaces.RSAPublicKey
import java.util.UUID

/**
 * 设备身份 = 持久化 Device ID + Android Keystore 中的 SSH Host Key（RSA-3072）。
 * 私钥不可导出，签名运算由 Keystore 完成。
 */
object DeviceIdentity {

    private const val PREFS = "phonedrive_identity"
    private const val KEY_DEVICE_ID = "device_id"
    private const val HOST_KEY_ALIAS = "phonedrive_host_key"

    @Volatile
    private var cachedDeviceId: String? = null

    @Volatile
    private var cachedHostKey: java.security.KeyPair? = null

    @Synchronized
    fun getDeviceId(context: Context): String {
        cachedHostKey // touch to keep field order obvious; no-op
        cachedDeviceId?.let { return it }
        val prefs = context.applicationContext
            .getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val existing = prefs.getString(KEY_DEVICE_ID, null)
        if (existing != null) {
            cachedDeviceId = existing
            return existing
        }
        val created = UUID.randomUUID().toString()
        prefs.edit().putString(KEY_DEVICE_ID, created).apply()
        cachedDeviceId = created
        return created
    }

    @Synchronized
    fun getOrCreateHostKeyPair(): KeyPair {
        cachedHostKey?.let { return it }
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val entry = ks.getEntry(HOST_KEY_ALIAS, null) as? KeyStore.PrivateKeyEntry
        if (entry != null) {
            val pair = KeyPair(entry.certificate.publicKey, entry.privateKey)
            cachedHostKey = pair
            return pair
        }
        val kpg = KeyPairGenerator.getInstance(
            KeyProperties.KEY_ALGORITHM_RSA, "AndroidKeyStore"
        )
        kpg.initialize(
            KeyGenParameterSpec.Builder(
                HOST_KEY_ALIAS,
                KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY
            )
                .setKeySize(3072)
                .setDigests(KeyProperties.DIGEST_SHA256, KeyProperties.DIGEST_SHA512)
                .setSignaturePaddings(KeyProperties.SIGNATURE_PADDING_RSA_PKCS1)
                .build()
        )
        val pair = kpg.generateKeyPair()
        cachedHostKey = pair
        return pair
    }

    /** RFC 4253 §6.6 ssh-rsa wire encoding: string "ssh-rsa" || mpint e || mpint n */
    fun sshWireEncode(key: RSAPublicKey): ByteArray {
        val out = ByteArrayOutputStream()
        fun writeInt(v: Int) {
            out.write((v ushr 24) and 0xFF)
            out.write((v ushr 16) and 0xFF)
            out.write((v ushr 8) and 0xFF)
            out.write(v and 0xFF)
        }
        fun writeBytes(b: ByteArray) {
            writeInt(b.size)
            out.write(b)
        }
        val type = "ssh-rsa".toByteArray(Charsets.US_ASCII)
        writeBytes(type)
        // BigInteger.toByteArray() 输出二进制补码，正数高位为 1 时自动补 0x00 —— 即 mpint
        writeBytes(key.publicExponent.toByteArray())
        writeBytes(key.modulus.toByteArray())
        return out.toByteArray()
    }

    /** OpenSSH authorized_keys 第二字段内容："ssh-rsa AAAA..." */
    fun toOpenSshPublicKey(key: RSAPublicKey): String {
        val blob = sshWireEncode(key)
        return "ssh-rsa " + java.util.Base64.getEncoder().encodeToString(blob)
    }

    /** OpenSSH 风格指纹："SHA256:xxxx"（base64 无填充） */
    fun fingerprintSha256(key: RSAPublicKey): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(sshWireEncode(key))
        val b64 = java.util.Base64.getEncoder().encodeToString(digest).trimEnd('=')
        return "SHA256:$b64"
    }
}
