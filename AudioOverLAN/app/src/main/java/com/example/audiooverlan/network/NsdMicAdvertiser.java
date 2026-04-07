package com.example.audiooverlan.network;

import android.content.Context;
import android.net.nsd.NsdManager;
import android.net.nsd.NsdServiceInfo;
import android.os.Build;
import android.util.Log;

/**
 * Advertises the Android mic streaming service via NSD/mDNS.
 * Service type: _audiooverlan-mic._udp
 * 
 * This is the REVERSE of NsdDiscoveryManager:
 * - NsdDiscoveryManager: Android DISCOVERS PC server (_audiooverlan._udp)
 * - NsdMicAdvertiser: Android ADVERTISES mic service (_audiooverlan-mic._udp)
 *   so that the PC can discover and auto-connect.
 */
public class NsdMicAdvertiser {
    private static final String TAG = "NsdMicAdvertiser";
    private static final String SERVICE_TYPE = "_audiooverlan-mic._udp";
    private static final String SERVICE_NAME = "AudioOverLAN-Mic";

    private final Context context;
    private NsdManager nsdManager;
    private NsdManager.RegistrationListener registrationListener;
    private volatile boolean isRegistered = false;
    private String registeredName;

    /**
     * @param context Application context
     */
    public NsdMicAdvertiser(Context context) {
        this.context = context.getApplicationContext();
    }

    public void start() {
        if (isRegistered) {
            Log.w(TAG, "Already registered, ignoring start request");
            return;
        }

        nsdManager = (NsdManager) context.getSystemService(Context.NSD_SERVICE);
        if (nsdManager == null) {
            Log.e(TAG, "NsdManager not available");
            return;
        }

        NsdServiceInfo serviceInfo = new NsdServiceInfo();
        serviceInfo.setServiceName(SERVICE_NAME);
        serviceInfo.setServiceType(SERVICE_TYPE);
        
        // Add human-readable device name to TXT record
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
            String deviceName = Build.MANUFACTURER + " " + Build.MODEL;
            serviceInfo.setAttribute("device_name", deviceName);
        }

        registrationListener = new NsdManager.RegistrationListener() {
            @Override
            public void onServiceRegistered(NsdServiceInfo info) {
                registeredName = info.getServiceName();
                isRegistered = true;
                Log.i(TAG, "Service registered: " + registeredName
                        + " type=" + SERVICE_TYPE);
            }

            @Override
            public void onRegistrationFailed(NsdServiceInfo info, int errorCode) {
                Log.e(TAG, "Registration failed: error " + errorCode);
                isRegistered = false;
            }

            @Override
            public void onServiceUnregistered(NsdServiceInfo info) {
                Log.i(TAG, "Service unregistered: " + info.getServiceName());
                isRegistered = false;
            }

            @Override
            public void onUnregistrationFailed(NsdServiceInfo info, int errorCode) {
                Log.e(TAG, "Unregistration failed: error " + errorCode);
            }
        };

        try {
            nsdManager.registerService(serviceInfo, NsdManager.PROTOCOL_DNS_SD, registrationListener);
            Log.i(TAG, "Registering service: " + SERVICE_NAME + " type=" + SERVICE_TYPE);
        } catch (Exception e) {
            Log.e(TAG, "Failed to register NSD service", e);
        }
    }

    public void stop() {
        if (!isRegistered || registrationListener == null || nsdManager == null) return;

        try {
            nsdManager.unregisterService(registrationListener);
        } catch (Exception e) {
            Log.e(TAG, "Failed to unregister NSD service", e);
        }
        isRegistered = false;
        registeredName = null;
    }

    public boolean isRegistered() {
        return isRegistered;
    }
}
