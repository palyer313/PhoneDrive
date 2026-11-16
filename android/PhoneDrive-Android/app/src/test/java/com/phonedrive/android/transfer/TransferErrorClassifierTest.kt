package com.phonedrive.android.transfer

import org.junit.Assert.assertEquals
import org.junit.Test
import java.io.FileNotFoundException
import java.io.IOException
import java.net.SocketTimeoutException

class TransferErrorClassifierTest {

    @Test
    fun classifiesNoSpaceBeforeGenericIoFailure() {
        assertEquals(
            TransferErrorKind.NO_SPACE,
            TransferErrorClassifier.classify(IOException("write failed: ENOSPC")),
        )
    }

    @Test
    fun classifiesIntegrityAndConflictMessages() {
        assertEquals(
            TransferErrorKind.INTEGRITY,
            TransferErrorClassifier.classify(IOException("目标临时文件 SHA-256 校验失败")),
        )
        assertEquals(
            TransferErrorKind.CONFLICT,
            TransferErrorClassifier.classify(IOException("目标已存在，未覆盖")),
        )
    }

    @Test
    fun followsNestedNetworkCause() {
        val nested = IOException("outer", SocketTimeoutException("timeout"))
        assertEquals(TransferErrorKind.NETWORK, TransferErrorClassifier.classify(nested))
    }

    @Test
    fun classifiesPermissionAndNotFound() {
        assertEquals(
            TransferErrorKind.PERMISSION,
            TransferErrorClassifier.classify(SecurityException("denied")),
        )
        assertEquals(
            TransferErrorKind.NOT_FOUND,
            TransferErrorClassifier.classify(FileNotFoundException("missing")),
        )
    }
}
