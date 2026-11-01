package javax.management

/**
 * Android 平台没有 javax.management（JMX），而 Apache MINA SSHD 的
 * ExceptionUtils 在剥离异常链时会 `instanceof javax.management.ReflectionException`
 * 与 `MBeanException`，导致运行期抛出
 * `NoClassDefFoundError: Failed resolution of: Ljavax/management/ReflectionException;`
 * 从而 SFTP 服务无法启动。
 *
 * 这里提供最小桩实现，仅满足 SSHD 的判断与 getTargetException() 调用。
 * 不实现任何真正的 MBean 语义（PhoneDrive 不使用 JMX）。
 */
open class JMException : Exception {
    constructor() : super()
    constructor(message: String?) : super(message)
    constructor(message: String?, cause: Throwable?) : super(message, cause)
    constructor(cause: Throwable?) : super(cause)
}

open class ReflectionException : JMException {
    private val target: Exception?

    constructor(e: Exception?) : super(e) {
        target = e
    }

    constructor(e: Exception?, message: String?) : super(message, e) {
        target = e
    }

    fun getTargetException(): Exception? = target
}

open class MBeanException : JMException {
    private val target: Exception?

    constructor(e: Exception?) : super(e) {
        target = e
    }

    constructor(e: Exception?, message: String?) : super(message, e) {
        target = e
    }

    fun getTargetException(): Exception? = target
}
