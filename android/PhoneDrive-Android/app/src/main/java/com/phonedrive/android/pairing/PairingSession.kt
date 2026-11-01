package com.phonedrive.android.pairing

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.security.SecureRandom
import java.util.Locale

/**
 * 一次配对窗口（发起方 A）的状态机。
 *
 * 生命周期：IDLE → PENDING →（SUCCESS | EXPIRED | CANCELED），
 * 默认 120 秒超时，配对码错误累计 3 次立即作废。
 * 配对码只存在于内存与二维码中，不写日志、不落盘。
 */
class PairingSession(
    val code: String = generateCode(),
    private val timeoutMillis: Long = DEFAULT_TIMEOUT_MS,
) {

    enum class State { IDLE, PENDING, SUCCESS, EXPIRED, CANCELED }

    data class Snapshot(
        val state: State = State.IDLE,
        val code: String = "",
        val remainingSeconds: Int = 0,
        val failedAttempts: Int = 0,
        val peerName: String? = null,
        val message: String? = null,
        val approvalPending: Boolean = false,
        val peerFingerprint: String? = null,
    )

    companion object {
        const val DEFAULT_TIMEOUT_MS = 120_000L
        const val MAX_FAILED_ATTEMPTS = 3

        private fun generateCode(): String =
            String.format(Locale.US, "%06d", SecureRandom().nextInt(1_000_000))
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val _state = MutableStateFlow(Snapshot(code = code))
    val state: StateFlow<Snapshot> = _state.asStateFlow()

    val nonce: String = java.util.Base64.getEncoder().encodeToString(ByteArray(32).also { SecureRandom().nextBytes(it) })
    @Volatile private var approved = false
    @Volatile private var approvalLatch = java.util.concurrent.CountDownLatch(1)
    fun requestApproval(name: String, fingerprint: String): Boolean {
        synchronized(this) {
            if (!isPending) return false
            approved = false
            approvalLatch = java.util.concurrent.CountDownLatch(1)
            _state.value = _state.value.copy(approvalPending = true, peerName = name, peerFingerprint = fingerprint)
        }
        approvalLatch.await(60, java.util.concurrent.TimeUnit.SECONDS)
        return approved && isPending
    }
    @Synchronized fun approvePeer(allow: Boolean) {
        approved = allow
        _state.value = _state.value.copy(approvalPending = false)
        approvalLatch.countDown()
    }

    private var ticker: Job? = null
    private var deadlineAt = 0L

    val isPending: Boolean get() = _state.value.state == State.PENDING

    @Synchronized
    fun start() {
        if (_state.value.state == State.PENDING) return
        deadlineAt = System.currentTimeMillis() + timeoutMillis
        _state.value = Snapshot(
            state = State.PENDING,
            code = code,
            remainingSeconds = (timeoutMillis / 1000).toInt(),
        )
        ticker?.cancel()
        ticker = scope.launch {
            while (isActive) {
                delay(1_000)
                tick()
                if (_state.value.state != State.PENDING) break
            }
        }
    }

    private fun tick() {
        synchronized(this) {
            val snapshot = _state.value
            if (snapshot.state != State.PENDING) return
            val remaining = ((deadlineAt - System.currentTimeMillis()) / 1000).toInt()
            if (remaining <= 0) {
                _state.value = snapshot.copy(
                    state = State.EXPIRED,
                    remainingSeconds = 0,
                    message = "配对窗口已超时，请重新生成二维码",
                )
                return
            }
            _state.value = snapshot.copy(remainingSeconds = remaining)
        }
    }

    /** 记录一次错误的配对码；返回 false 表示本次配对已作废 */
    @Synchronized
    fun registerBadCode(): Boolean {
        val snapshot = _state.value
        if (snapshot.state != State.PENDING) return false
        val attempts = snapshot.failedAttempts + 1
        return if (attempts >= MAX_FAILED_ATTEMPTS) {
            _state.value = snapshot.copy(
                state = State.EXPIRED,
                remainingSeconds = 0,
                failedAttempts = attempts,
                message = "配对码错误次数超限，本次配对已作废",
            )
            false
        } else {
            _state.value = snapshot.copy(failedAttempts = attempts)
            true
        }
    }

    @Synchronized
    fun succeed(peerName: String): Boolean {
        val snapshot = _state.value
        if (snapshot.state != State.PENDING) return false
        _state.value = snapshot.copy(
            state = State.SUCCESS,
            remainingSeconds = 0,
            peerName = peerName,
        )
        return true
    }

    @Synchronized
    fun cancel() {
        approvalLatch.countDown()
        val snapshot = _state.value
        if (snapshot.state != State.PENDING) return
        _state.value = snapshot.copy(
            state = State.CANCELED,
            remainingSeconds = 0,
            message = "配对已取消",
        )
    }

    @Synchronized
    fun expire(message: String = "配对窗口已关闭") {
        val snapshot = _state.value
        if (snapshot.state != State.PENDING) return
        _state.value = snapshot.copy(
            state = State.EXPIRED,
            remainingSeconds = 0,
            message = message,
        )
    }

    /** 释放倒计时协程（配对窗口关闭后必须调用） */
    fun close() {
        approvalLatch.countDown()
        ticker?.cancel()
        ticker = null
        scope.cancel()
    }
}
