package com.phonedrive.android.pairing

import java.io.ByteArrayInputStream
import java.io.DataInputStream
import java.math.BigInteger
import java.security.KeyFactory
import java.security.PrivateKey
import java.security.Signature
import java.security.interfaces.RSAPublicKey
import java.security.spec.RSAPublicKeySpec
import java.util.Base64

object PairingProof {
    fun rsa(line: String): RSAPublicKey {
        val parts = line.trim().split(Regex("\\s+"))
        require(parts.size >= 2 && parts[0] == "ssh-rsa")
        val blob = Base64.getDecoder().decode(parts[1]); require(blob.size in 256..2048)
        val input = DataInputStream(ByteArrayInputStream(blob))
        fun field(): ByteArray { val size = input.readInt(); require(size in 1..2048 && size <= input.available()); return ByteArray(size).also { input.readFully(it) } }
        require(String(field(), Charsets.US_ASCII) == "ssh-rsa")
        val e = BigInteger(field()); val n = BigInteger(field())
        require(input.available() == 0 && e.signum() > 0 && n.bitLength() in 2048..8192)
        return KeyFactory.getInstance("RSA").generatePublic(RSAPublicKeySpec(n, e)) as RSAPublicKey
    }
    fun transcript(nonce: String, id: String, name: String, type: String, fp: String, host: String, client: String): ByteArray =
        listOf("PhoneDrive-Pair-v2", nonce, id, name, type, fp, host, client).joinToString("\n").toByteArray(Charsets.UTF_8)
    fun sign(key: PrivateKey, bytes: ByteArray): String = Signature.getInstance("SHA256withRSA").run {
        initSign(key); update(bytes); Base64.getEncoder().encodeToString(sign())
    }
    fun verify(key: RSAPublicKey, bytes: ByteArray, proof: String): Boolean = runCatching {
        Signature.getInstance("SHA256withRSA").run { initVerify(key); update(bytes); verify(Base64.getDecoder().decode(proof)) }
    }.getOrDefault(false)
}
