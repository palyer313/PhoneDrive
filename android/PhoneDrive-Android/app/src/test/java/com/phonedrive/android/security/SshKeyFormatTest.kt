package com.phonedrive.android.security

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.security.KeyPairGenerator
import java.security.interfaces.RSAPublicKey

/**
 * 纯 JVM 单元测试：SSH 公钥线格式与指纹计算。
 * 不依赖 Android Keystore（测试中用普通 KeyPairGenerator 生成 RSA 密钥）。
 */
class SshKeyFormatTest {

    private fun rsaKeyPair(): RSAPublicKey {
        val kpg = KeyPairGenerator.getInstance("RSA")
        kpg.initialize(2048)
        return kpg.generateKeyPair().public as RSAPublicKey
    }

    @Test
    fun `公钥为 OpenSSH ssh-rsa 单行格式`() {
        val pub = rsaKeyPair()
        val line = DeviceIdentity.toOpenSshPublicKey(pub)

        val parts = line.split(" ")
        assertEquals(2, parts.size)
        assertEquals("ssh-rsa", parts[0])
        assertTrue(parts[1].isNotEmpty())
    }

    @Test
    fun `线格式以长度前缀 ssh-rsa 开头`() {
        val pub = rsaKeyPair()
        val blob = DeviceIdentity.sshWireEncode(pub)

        // 前 4 字节是大端长度，应为 7（"ssh-rsa" 的字节数）
        val len = ((blob[0].toInt() and 0xFF) shl 24) or
                ((blob[1].toInt() and 0xFF) shl 16) or
                ((blob[2].toInt() and 0xFF) shl 8) or
                (blob[3].toInt() and 0xFF)
        assertEquals(7, len)
        assertEquals("ssh-rsa", String(blob, 4, 7, Charsets.US_ASCII))
    }

    @Test
    fun `指纹为 SHA256 前缀且长度 43 且无填充符`() {
        val pub = rsaKeyPair()
        val fp = DeviceIdentity.fingerprintSha256(pub)

        assertTrue(fp.startsWith("SHA256:"))
        assertEquals(43, fp.length - "SHA256:".length)
        assertTrue(!fp.contains("="))
    }

    @Test
    fun `同一密钥指纹稳定_不同密钥指纹不同`() {
        val pub1 = rsaKeyPair()
        val pub2 = rsaKeyPair()

        assertEquals(DeviceIdentity.fingerprintSha256(pub1), DeviceIdentity.fingerprintSha256(pub1))
        assertTrue(DeviceIdentity.fingerprintSha256(pub1) != DeviceIdentity.fingerprintSha256(pub2))
    }
}
