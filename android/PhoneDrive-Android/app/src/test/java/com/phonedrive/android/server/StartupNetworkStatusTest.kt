package com.phonedrive.android.server

import org.junit.Assert.*
import org.junit.Test

class StartupNetworkStatusTest {
    @Test fun wifiOffAndNoLocalLinkRequiresEnablingWifi() {
        val result = StartupNetworkStatus.evaluate(false, false, false)
        assertEquals(StartupNetworkStatus.WIFI_DISABLED, result)
        assertFalse(result.canStart)
    }

    @Test fun wifiSwitchOnWithoutAddressIsNotReady() {
        val result = StartupNetworkStatus.evaluate(true, false, false)
        assertEquals(StartupNetworkStatus.WIFI_NOT_CONNECTED, result)
        assertFalse(result.canStart)
    }

    @Test fun connectedLocalWifiNeedsNoInternetValidation() {
        assertEquals(StartupNetworkStatus.WIFI_READY, StartupNetworkStatus.evaluate(true, true, false))
        assertTrue(StartupNetworkStatus.evaluate(true, true, false).canStart)
    }

    @Test fun hotspotUsbAndEthernetRemainUsableWithWifiOff() {
        val result = StartupNetworkStatus.evaluate(false, false, true)
        assertEquals(StartupNetworkStatus.LOCAL_LINK_READY, result)
        assertTrue(result.canStart)
    }
}
