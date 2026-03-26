package com.example.audiooverlan.network;

import android.content.Context;
import android.net.nsd.NsdManager;
import android.net.nsd.NsdServiceInfo;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;

/**
 * Discovers AudioOverLAN servers using mDNS/DNS-SD (NsdManager).
 * Listens for "_audiooverlan._udp." services on the local network.
 * When a mode-3 server is found, notifies via callback for auto-connection.
 */
public class NsdDiscoveryManager {

    private static final String TAG = "NsdDiscovery";
    private static final String SERVICE_TYPE = "_audiooverlan._udp.";

    public interface OnServerDiscoveredListener {
        void onServerDiscovered(String name, String ip, int port);
        void onServerLost(String ip);
    }

    private final Context context;
    private NsdManager nsdManager;
    private NsdManager.DiscoveryListener discoveryListener;
    private OnServerDiscoveredListener listener;
    private volatile boolean isDiscovering = false;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());

    // Debounce: avoid spamming resolve for same service
    private volatile String lastResolvedName = null;

    public NsdDiscoveryManager(Context context) {
        this.context = context.getApplicationContext();
        this.nsdManager = (NsdManager) this.context.getSystemService(Context.NSD_SERVICE);
    }

    public void setListener(OnServerDiscoveredListener listener) {
        this.listener = listener;
    }

    public boolean isDiscovering() {
        return isDiscovering;
    }

    public void startDiscovery() {
        if (isDiscovering) {
            Log.w(TAG, "Already discovering, ignoring start request");
            return;
        }
        if (nsdManager == null) {
            nsdManager = (NsdManager) context.getSystemService(Context.NSD_SERVICE);
            if (nsdManager == null) {
                Log.e(TAG, "NsdManager not available");
                return;
            }
        }

        lastResolvedName = null;

        discoveryListener = new NsdManager.DiscoveryListener() {
            @Override
            public void onStartDiscoveryFailed(String serviceType, int errorCode) {
                Log.e(TAG, "Discovery start failed: " + errorCode);
                isDiscovering = false;
            }

            @Override
            public void onStopDiscoveryFailed(String serviceType, int errorCode) {
                Log.e(TAG, "Discovery stop failed: " + errorCode);
                isDiscovering = false;
            }

            @Override
            public void onDiscoveryStarted(String serviceType) {
                Log.i(TAG, "mDNS discovery started for " + serviceType);
                isDiscovering = true;
            }

            @Override
            public void onDiscoveryStopped(String serviceType) {
                Log.i(TAG, "mDNS discovery stopped for " + serviceType);
                isDiscovering = false;
            }

            @Override
            public void onServiceFound(NsdServiceInfo serviceInfo) {
                Log.i(TAG, "Service found: " + serviceInfo.getServiceName()
                        + " type=" + serviceInfo.getServiceType());

                // Avoid duplicate resolves for same service name
                String name = serviceInfo.getServiceName();
                if (name != null && name.equals(lastResolvedName)) {
                    Log.d(TAG, "Service already resolved, skipping: " + name);
                    return;
                }

                // Resolve to get IP and port
                resolveService(serviceInfo);
            }

            @Override
            public void onServiceLost(NsdServiceInfo serviceInfo) {
                Log.w(TAG, "Service lost: " + serviceInfo.getServiceName());
                lastResolvedName = null;

                if (listener != null) {
                    String host = serviceInfo.getHost() != null
                            ? serviceInfo.getHost().getHostAddress() : null;
                    if (host != null) {
                        mainHandler.post(() -> listener.onServerLost(host));
                    }
                }
            }
        };

        try {
            nsdManager.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, discoveryListener);
        } catch (Exception e) {
            Log.e(TAG, "Failed to start NSD discovery", e);
            isDiscovering = false;
        }
    }

    private void resolveService(NsdServiceInfo serviceInfo) {
        nsdManager.resolveService(serviceInfo, new NsdManager.ResolveListener() {
            @Override
            public void onResolveFailed(NsdServiceInfo serviceInfo, int errorCode) {
                Log.e(TAG, "Resolve failed for " + serviceInfo.getServiceName()
                        + " error=" + errorCode);
            }

            @Override
            public void onServiceResolved(NsdServiceInfo resolvedInfo) {
                String host = resolvedInfo.getHost() != null
                        ? resolvedInfo.getHost().getHostAddress() : null;
                int port = resolvedInfo.getPort();

                Log.i(TAG, "Service resolved: " + resolvedInfo.getServiceName()
                        + " -> " + host + ":" + port);

                lastResolvedName = resolvedInfo.getServiceName();

                if (host != null && listener != null) {
                    String name = resolvedInfo.getServiceName();
                    mainHandler.post(() -> listener.onServerDiscovered(name, host, port));
                }
            }
        });
    }

    public void stopDiscovery() {
        if (!isDiscovering || discoveryListener == null) return;

        try {
            nsdManager.stopServiceDiscovery(discoveryListener);
        } catch (Exception e) {
            Log.e(TAG, "Failed to stop NSD discovery", e);
        }
        isDiscovering = false;
        lastResolvedName = null;
    }

    public void destroy() {
        stopDiscovery();
        listener = null;
    }
}
