package com.phonedrive.android.remote

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import com.phonedrive.android.security.DeviceIdentity
import org.apache.sshd.common.NamedFactory
import org.apache.sshd.common.session.SessionContext
import org.apache.sshd.common.signature.Signature
import org.apache.sshd.common.signature.SignatureFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.PublicKey
import java.security.interfaces.RSAPublicKey

/**
 * 客户端身份密钥（SFTP publickey 认证用）。
 *
 * 与 Host Key（别名 phonedrive_host_key）严格分离，使用独立别名
 * phonedrive_client_key，私钥不可导出，SHA-256/SHA-512 签名由 Android Keystore 完成。
 *
 * 由于 Keystore 私钥不能交给默认 JCA Provider 使用，这里为 Apache SSHD 提供
 * 自定义 RSA 签名实现（只替换 RSA 相关算法，其它算法仍走默认实现，保证
 * 对端 Host Key 签名验证不受影响）。
 */
object ClientKey {

    private const val ANDROID_KEYSTORE = "AndroidKeyStore"
    private const val ALIAS = "phonedrive_client_key"

    /** 默认实现中需要被 Keystore 版本替换掉的 RSA 签名算法名 */
    private val RSA_SIGNATURE_NAMES = setOf(
        "ssh-rsa",
        "rsa-sha2-256",
        "rsa-sha2-512",
        "ssh-rsa-cert-v01@openssh.com",
        "rsa-sha2-256-cert-v01@openssh.com",
        "rsa-sha2-512-cert-v01@openssh.com",
    )

    @Volatile
    private var cached: KeyPair? = null

    @Synchronized
    fun getOrCreateKeyPair(): KeyPair {
        cached?.let { return it }
        val ks = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        val entry = ks.getEntry(ALIAS, null) as? KeyStore.PrivateKeyEntry
        if (entry != null) {
            val pair = KeyPair(entry.certificate.publicKey, entry.privateKey)
            cached = pair
            return pair
        }
        val kpg = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_RSA, ANDROID_KEYSTORE)
        kpg.initialize(
            KeyGenParameterSpec.Builder(
                ALIAS,
                KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY
            )
                .setKeySize(3072)
                .setDigests(KeyProperties.DIGEST_SHA256, KeyProperties.DIGEST_SHA512)
                .setSignaturePaddings(KeyProperties.SIGNATURE_PADDING_RSA_PKCS1)
                .build()
        )
        val pair = kpg.generateKeyPair()
        cached = pair
        return pair
    }

    fun publicKey(): RSAPublicKey = getOrCreateKeyPair().public as RSAPublicKey

    /** 本机客户端公钥（OpenSSH 单行格式），供对端加入 authorized_keys */
    fun openSshPublicKeyLine(): String = DeviceIdentity.toOpenSshPublicKey(publicKey())

    fun fingerprint(): String = DeviceIdentity.fingerprintSha256(publicKey())

    /**
     * 构造 SSHD 的签名算法工厂列表：保留默认的非 RSA 实现，
     * 用 Keystore 驱动的实现替换 RSA 算法（不支持 SHA-1 的 ssh-rsa 签名）。
     */
    fun buildSignatureFactories(
        defaults: List<NamedFactory<Signature>>,
    ): List<NamedFactory<Signature>> {
        val nonRsa = defaults.filterNot { it.name in RSA_SIGNATURE_NAMES }
        return nonRsa + listOf(
            RsaSignatureFactory("rsa-sha2-512", "SHA512withRSA", keystoreSign = true),
            RsaSignatureFactory("rsa-sha2-256", "SHA256withRSA", keystoreSign = true),
            // 仅用于校验对端 Host Key 的 SHA-1 兼容路径；Keystore 不支持 SHA-1 签名，
            // 因此客户端认证实际使用的是上面的 rsa-sha2-* 算法。
            RsaSignatureFactory("ssh-rsa", "SHA1withRSA", keystoreSign = false),
        )
    }

    private class RsaSignatureFactory(
        private val sshName: String,
        private val jcaName: String,
        private val keystoreSign: Boolean,
    ) : SignatureFactory {
        override fun getName(): String = sshName
        override fun create(): Signature = KeystoreAwareSignature(sshName, jcaName, keystoreSign)
        override fun isSupported(): Boolean = true
    }

    /**
     * 签名与验签都通过标准 JCA 算法名完成；
     * 签名时固定使用 AndroidKeyStore Provider（私钥只在 Keystore 内可用）。
     */
    private class KeystoreAwareSignature(
        private val sshAlgorithm: String,
        private val jcaAlgorithm: String,
        private val keystoreSign: Boolean,
    ) : Signature {

        private var delegate: java.security.Signature? = null

        override fun getAlgorithm(): String = sshAlgorithm

        override fun initVerifier(session: SessionContext?, key: PublicKey?) {
            val publicKey = requireNotNull(key) { "验签缺少公钥" }
            delegate = java.security.Signature.getInstance(jcaAlgorithm).apply {
                initVerify(publicKey)
            }
        }

        override fun initSigner(session: SessionContext?, key: PrivateKey?) {
            val privateKey = requireNotNull(key) { "签名缺少私钥" }
            delegate = if (keystoreSign) {
                java.security.Signature.getInstance(jcaAlgorithm, ANDROID_KEYSTORE).apply {
                    initSign(privateKey)
                }
            } else {
                java.security.Signature.getInstance(jcaAlgorithm).apply {
                    initSign(privateKey)
                }
            }
        }

        override fun update(session: SessionContext?, data: ByteArray?) {
            delegate?.update(data)
        }

        override fun update(session: SessionContext?, data: ByteArray?, off: Int, len: Int) {
            delegate?.update(data, off, len)
        }

        override fun verify(session: SessionContext?, signature: ByteArray?): Boolean =
            delegate?.verify(signature) ?: false

        override fun sign(session: SessionContext?): ByteArray =
            delegate?.sign() ?: throw IllegalStateException("签名器未初始化")
    }
}
