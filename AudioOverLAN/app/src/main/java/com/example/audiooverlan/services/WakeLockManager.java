package com.example.audiooverlan.services;

import android.content.Context;
import android.net.wifi.WifiManager;
import android.os.PowerManager;

public class WakeLockManager {
    private final PowerManager.WakeLock wakeLock;
    private final WifiManager.WifiLock wifiLock;

    public WakeLockManager(Context context) {
        PowerManager pm = (PowerManager) context.getSystemService(Context.POWER_SERVICE);
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "AudioOverLAN:WakeLock");

        WifiManager wm = (WifiManager) context.getApplicationContext().getSystemService(Context.WIFI_SERVICE);
        wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "AudioOverLAN:WifiLock");
    }

    public void acquireLocks() {
        if (!wakeLock.isHeld()) {
            wakeLock.acquire();
        }
        if (!wifiLock.isHeld()) {
            wifiLock.acquire();
        }
    }

    public void releaseLocks() {
        if (wakeLock != null && wakeLock.isHeld()) {
            wakeLock.release();
        }
        if (wifiLock != null && wifiLock.isHeld()) {
            wifiLock.release();
        }
    }
}
