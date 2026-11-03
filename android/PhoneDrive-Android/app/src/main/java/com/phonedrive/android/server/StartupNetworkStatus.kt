package com.phonedrive.android.server

enum class StartupNetworkStatus(val canStart: Boolean, val title: String, val message: String) {
    WIFI_READY(true, "Wi-Fi 已连接", "局域网连接已就绪"),
    LOCAL_LINK_READY(true, "局域网已就绪", "当前使用热点、USB 或有线局域网"),
    WIFI_DISABLED(false, "请先开启 Wi-Fi", "Wi-Fi 尚未开启。请打开 Wi-Fi 并连接与电脑可互访的局域网，再启动文件服务。"),
    WIFI_NOT_CONNECTED(false, "请先连接 Wi-Fi", "Wi-Fi 已开启，但尚未取得可用的局域网地址。请选择 Wi-Fi 网络，连接成功后重试。局域网无需连接互联网。"),
    UNKNOWN(false, "无法检查网络", "暂时无法读取局域网状态，请检查 Wi-Fi 连接后重试。"),
    ;

    companion object {
        fun evaluate(wifiEnabled: Boolean, wifiHasAddress: Boolean, otherLocalLinkHasAddress: Boolean): StartupNetworkStatus = when {
            wifiHasAddress -> WIFI_READY
            otherLocalLinkHasAddress -> LOCAL_LINK_READY
            !wifiEnabled -> WIFI_DISABLED
            else -> WIFI_NOT_CONNECTED
        }
    }
}
