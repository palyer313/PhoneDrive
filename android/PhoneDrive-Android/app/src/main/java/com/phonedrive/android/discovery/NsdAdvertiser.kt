package com.phonedrive.android.discovery

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.util.Log

/**
 * mDNS / DNS-SD 服务注册：_phonedrive._tcp
 * TXT 记录遵循 protocol/discovery/README.md。
 */
class NsdAdvertiser(private val context: Context) {

    companion object {
        const val SERVICE_TYPE = "_phonedrive._tcp."
        private const val TAG = "NsdAdvertiser"
    }

    private val nsdManager: NsdManager =
        context.getSystemService(Context.NSD_SERVICE) as NsdManager
    private var multicastLock: WifiManager.MulticastLock? = null
    private var registrationListener: NsdManager.RegistrationListener? = null

    fun start(
        port: Int,
        deviceId: String,
        deviceName: String,
        deviceType: String,
        fingerprint: String,
    ) {
        stop()

        // 组播锁：保证能收发 mDNS 组播包
        val wifi = context.applicationContext
            .getSystemService(Context.WIFI_SERVICE) as WifiManager
        multicastLock = wifi.createMulticastLock("phonedrive_nsd").apply {
            setReferenceCounted(true)
            acquire()
        }

        val info = NsdServiceInfo().apply {
            serviceName = deviceName
            serviceType = SERVICE_TYPE
            setPort(port)
            setAttribute("deviceid", deviceId)
            setAttribute("name", deviceName)
            setAttribute("type", deviceType)
            setAttribute("proto", "1")
            setAttribute("sftpport", port.toString())
            setAttribute("fp", fingerprint)
            setAttribute("caps", "rw")
            setAttribute("sw", "0.1.0")
        }

        val listener = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(serviceInfo: NsdServiceInfo) {
                Log.i(TAG, "mDNS registered: ${serviceInfo.serviceName}")
            }

            override fun onRegistrationFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                Log.e(TAG, "mDNS registration failed: $errorCode")
            }

            override fun onServiceUnregistered(serviceInfo: NsdServiceInfo) {
                Log.i(TAG, "mDNS unregistered")
            }

            override fun onUnregistrationFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                Log.e(TAG, "mDNS unregistration failed: $errorCode")
            }
        }
        registrationListener = listener
        nsdManager.registerService(info, NsdManager.PROTOCOL_DNS_SD, listener)
    }

    fun stop() {
        registrationListener?.let {
            runCatching { nsdManager.unregisterService(it) }
        }
        registrationListener = null
        multicastLock?.let { if (it.isHeld) it.release() }
        multicastLock = null
    }
}
