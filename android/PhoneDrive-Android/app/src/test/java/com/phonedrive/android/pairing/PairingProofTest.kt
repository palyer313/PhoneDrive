package com.phonedrive.android.pairing

import com.phonedrive.android.security.DeviceIdentity
import org.junit.Assert.*
import org.junit.Test
import java.security.KeyPairGenerator
import java.security.interfaces.RSAPublicKey

class PairingProofTest {
    private fun key() = KeyPairGenerator.getInstance("RSA").apply { initialize(2048) }.generateKeyPair()
    @Test fun signedUnicodeTranscriptVerifiesWithCorrectKey() {
        val key = key(); val line = DeviceIdentity.toOpenSshPublicKey(key.public as RSAPublicKey)
        val transcript = PairingProof.transcript("fresh", "id", "测试手机", "phone", "fp", line, line)
        val proof = PairingProof.sign(key.private, transcript)
        assertTrue(PairingProof.verify(PairingProof.rsa(line), transcript, proof))
    }
    @Test fun replacedKeyOrReplayedNonceCannotReuseProof() {
        val key = key(); val original = DeviceIdentity.toOpenSshPublicKey(key.public as RSAPublicKey)
        val replaced = DeviceIdentity.toOpenSshPublicKey(key().public as RSAPublicKey)
        val transcript = PairingProof.transcript("fresh", "id", "phone", "phone", "fp", original, original)
        val proof = PairingProof.sign(key.private, transcript)
        assertFalse(PairingProof.verify(key.public as RSAPublicKey,
            PairingProof.transcript("fresh", "id", "phone", "phone", "fp", original, replaced), proof))
        assertFalse(PairingProof.verify(key.public as RSAPublicKey,
            PairingProof.transcript("old", "id", "phone", "phone", "fp", original, original), proof))
    }
    @Test fun malformedPublicKeyIsRejected() {
        assertTrue(runCatching { PairingProof.rsa("ssh-rsa AAAA") }.isFailure)
    }
}
