package com.phonedrive.android.server

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.wifi.WifiManager
import java.net.Inet4Address
import java.net.NetworkInterface

/** A one-shot local-network check: no SSID/location permission, Internet validation, polling, or Wi-Fi toggling. */
object ServiceNetworkReadiness {
    private fun usable(address: java.net.InetAddress) = address is Inet4Address &&
        !address.isAnyLocalAddress && !address.isLoopbackAddress && !address.isLinkLocalAddress

    fun inspect(context: Context): StartupNetworkStatus = runCatching {
        val app = context.applicationContext
        val wifi = app.getSystemService(Context.WIFI_SERVICE) as WifiManager
        val wifiEnabled = wifi.isWifiEnabled
        val connectivity = app.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        var wifiHasAddress = false
        var otherLocalLinkHasAddress = false
        connectivity.allNetworks.forEach { network ->
            val caps = connectivity.getNetworkCapabilities(network) ?: return@forEach
            if (caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) return@forEach
            val hasAddress = connectivity.getLinkProperties(network)?.linkAddresses
                ?.any { usable(it.address) } == true
            if (hasAddress && caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) wifiHasAddress = true
            if (hasAddress && caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)) otherLocalLinkHasAddress = true
        }
        // Tethering downstream interfaces do not necessarily appear as ConnectivityManager networks.
        val localInterfaces = NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()
        if (localInterfaces.any { link ->
            val name = link.name.lowercase(java.util.Locale.ROOT)
            val localName = listOf("swlan", "ap", "eth", "rndis", "usb", "ncm")
                .any { prefix -> name.startsWith(prefix) } ||
                (!wifiEnabled && (name.startsWith("wlan") || name.startsWith("wifi")))
            localName && link.isUp && !link.isLoopback && link.inetAddresses.toList().any(::usable)
        }) otherLocalLinkHasAddress = true
        StartupNetworkStatus.evaluate(wifiEnabled, wifiHasAddress, otherLocalLinkHasAddress)
    }.getOrDefault(StartupNetworkStatus.UNKNOWN)
}
