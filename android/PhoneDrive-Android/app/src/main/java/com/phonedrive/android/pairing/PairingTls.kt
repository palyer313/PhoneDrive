package com.phonedrive.android.pairing

import com.phonedrive.android.security.DeviceIdentity
import java.security.KeyStore
import java.security.PrivateKey
import java.security.cert.X509Certificate
import java.security.interfaces.RSAPublicKey
import java.net.Socket
import javax.net.ssl.*

/** TLS 1.2+ authenticated by the exact SSH host public key from the QR/device screen. */
object PairingTls {
    private fun fingerprint(cert: java.security.cert.Certificate): String =
        DeviceIdentity.fingerprintSha256(cert.publicKey as? RSAPublicKey ?: throw java.security.cert.CertificateException("RSA identity required"))

    fun serverContext(): SSLContext {
        val pair = DeviceIdentity.getOrCreateHostKeyPair()
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val chain = ks.getCertificateChain("phonedrive_host_key").map { it as X509Certificate }.toTypedArray()
        val manager = object : X509ExtendedKeyManager() {
            override fun getPrivateKey(alias: String?): PrivateKey = pair.private
            override fun getCertificateChain(alias: String?): Array<X509Certificate> = chain
            override fun getClientAliases(keyType: String?, issuers: Array<out java.security.Principal>?): Array<String>? = null
            override fun chooseClientAlias(keyType: Array<out String>?, issuers: Array<out java.security.Principal>?, socket: Socket?): String? = null
            override fun getServerAliases(keyType: String?, issuers: Array<out java.security.Principal>?): Array<String>? = if (keyType?.startsWith("RSA") == true) arrayOf("host") else null
            override fun chooseServerAlias(keyType: String?, issuers: Array<out java.security.Principal>?, socket: Socket?): String? = if (keyType?.startsWith("RSA") == true) "host" else null
        }
        return SSLContext.getInstance("TLS").apply { init(arrayOf(manager), null, java.security.SecureRandom()) }
    }

    fun clientContext(expected: String): SSLContext {
        require(expected.startsWith("SHA256:") && java.util.Base64.getDecoder().decode(expected.substring(7)).size == 32) { "必须提供并核对完整设备指纹" }
        val trust = object : X509TrustManager {
            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) { throw java.security.cert.CertificateException("No TLS client certificates") }
            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                if (chain.isNullOrEmpty() || fingerprint(chain[0]) != expected) throw java.security.cert.CertificateException("设备 TLS 公钥与已核对的 Host Key 指纹不符")
                chain[0].checkValidity()
            }
        }
        return SSLContext.getInstance("TLS").apply { init(null, arrayOf(trust), java.security.SecureRandom()) }
    }

    fun verifier(ip: String, expected: String) = HostnameVerifier { host, session ->
        host == ip && runCatching { fingerprint(session.peerCertificates[0]) == expected }.getOrDefault(false)
    }
}
