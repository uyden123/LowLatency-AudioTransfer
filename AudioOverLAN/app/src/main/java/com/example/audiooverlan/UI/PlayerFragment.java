package com.example.audiooverlan.UI;

import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.ImageButton;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.TextView;
import android.widget.Toast;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.core.content.ContextCompat;
import androidx.fragment.app.Fragment;

import com.example.audiooverlan.services.AudioService;
import com.example.audiooverlan.network.NsdDiscoveryManager;
import com.example.audiooverlan.R;
import com.example.audiooverlan.utils.Utils;
import com.google.android.material.textfield.TextInputEditText;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.List;
import java.util.Objects;
import java.util.Set;

public class PlayerFragment extends Fragment {

    private static final String PREFS_NAME = "AudioOverLAN_Prefs";
    private static final String KEY_HISTORY_IPS = "history_ips";
    private static final String KEY_JITTER_TARGET = "jitter_target";
    private static final String KEY_JITTER_MAX = "jitter_max";
    public static final String KEY_AUTO_CONNECT = "auto_connect";
    private static final int MAX_HISTORY = 5;

    /**
     * Session-level flag: when user manually STOPs, auto-connect is suppressed
     * until the app process is restarted. This is NOT persisted.
     */
    public static volatile boolean autoConnectSuppressed = false;

    private Button btnConnect;
    private ImageButton btnRefresh;
    private ProgressBar scanProgressBar;
    private TextInputEditText etIPAddress;
    private LinearLayout serverListContainer;
    private TextView tvNoServers;
    private TextView txtStatus;

    private final Set<String> discoveredServers = new HashSet<>();
    private final Handler statusHandler = new Handler(Looper.getMainLooper());
    private final Runnable statusRunnable = new Runnable() {
        @Override
        public void run() {
            updateButtonState();
            statusHandler.postDelayed(this, 1000);
        }
    };

    // mDNS/NSD discovery
    private NsdDiscoveryManager nsdDiscoveryManager;
    private boolean nsdStarted = false;

    @Nullable
    @Override
    public View onCreateView(@NonNull LayoutInflater inflater, @Nullable ViewGroup container, @Nullable Bundle savedInstanceState) {
        View view = inflater.inflate(R.layout.fragment_player, container, false);

        btnConnect = view.findViewById(R.id.btnConnect);
        btnRefresh = view.findViewById(R.id.btnRefresh);
        scanProgressBar = view.findViewById(R.id.scanProgressBar);
        etIPAddress = view.findViewById(R.id.etIPAddress);
        serverListContainer = view.findViewById(R.id.serverListContainer);
        tvNoServers = view.findViewById(R.id.tvNoServers);
        txtStatus = view.findViewById(R.id.txtStatus);

        loadLastIp();

        btnConnect.setOnClickListener(v -> {
            if (AudioService.isServiceRunning)
                return;
            startAudioService();
        });

        btnRefresh.setOnClickListener(v -> restartNsdDiscovery());

        // Start mDNS/NSD discovery
        startNsdDiscovery();

        // Proactive one-time auto-connect attempt on app start
        if (!proactiveAttemptDone && !AudioService.isServiceRunning && isAutoConnectEnabled() && !autoConnectSuppressed) {
            String lastIp = getLastConnectedIp();
            if (lastIp != null && !lastIp.isEmpty()) {
                proactiveAttemptDone = true;
                etIPAddress.setText(lastIp);
                // Small delay to ensure everything is ready
                new Handler(Looper.getMainLooper()).postDelayed(this::startAudioService, 500);
            }
        }

        return view;
    }

    private static boolean proactiveAttemptDone = false;

    // ================================================================
    //  mDNS/NSD Discovery (replaces legacy UDP broadcast)
    // ================================================================

    private void startNsdDiscovery() {
        if (nsdStarted || !isAdded()) return;
        nsdStarted = true;

        // Show scanning indicator
        btnRefresh.setVisibility(View.INVISIBLE);
        scanProgressBar.setVisibility(View.VISIBLE);

        nsdDiscoveryManager = new NsdDiscoveryManager(requireContext());
        nsdDiscoveryManager.setListener(new NsdDiscoveryManager.OnServerDiscoveredListener() {
            @Override
            public void onServerDiscovered(String ip, int port) {
                if (!isAdded()) return;

                // Add to discovered server list for UI
                if (discoveredServers.add(ip)) {
                    updateServerList();
                }

                // Show scan complete
                btnRefresh.setVisibility(View.VISIBLE);
                scanProgressBar.setVisibility(View.INVISIBLE);

                // Auto-connect if enabled and matches last connected IP
                if (!AudioService.isServiceRunning && isAutoConnectEnabled() && !autoConnectSuppressed) {
                    String lastIp = getLastConnectedIp();
                    if (ip.equals(lastIp)) {
                        txtStatus.setText("Auto-reconnecting to " + ip + "...");
                        txtStatus.setTextColor(ContextCompat.getColor(requireContext(), android.R.color.holo_blue_dark));

                        etIPAddress.setText(ip);
                        startAudioServiceWithParams(ip, port);
                    }
                }
            }

            @Override
            public void onServerLost(String ip) {
                if (!isAdded()) return;
                discoveredServers.remove(ip);
                updateServerList();
            }
        });

        nsdDiscoveryManager.startDiscovery();

        // After 8s, show the refresh button even if nothing found
        new Handler(Looper.getMainLooper()).postDelayed(() -> {
            if (isAdded() && scanProgressBar.getVisibility() == View.VISIBLE) {
                btnRefresh.setVisibility(View.VISIBLE);
                scanProgressBar.setVisibility(View.INVISIBLE);
            }
        }, 8000);
    }

    private void restartNsdDiscovery() {
        stopNsdDiscovery();
        discoveredServers.clear();
        updateServerList();
        startNsdDiscovery();
    }

    private void stopNsdDiscovery() {
        if (nsdDiscoveryManager != null) {
            nsdDiscoveryManager.destroy();
            nsdDiscoveryManager = null;
        }
        nsdStarted = false;
    }

    private boolean isAutoConnectEnabled() {
        if (!isAdded()) return false;
        SharedPreferences prefs = requireContext().getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        return prefs.getBoolean(KEY_AUTO_CONNECT, true); // default ON
    }

    // ================================================================
    //  Status / UI
    // ================================================================

    private void updateButtonState() {
        if (!isAdded()) return;
        if (AudioService.isServiceRunning) {
            if (AudioService.isConnectedToProcessor()) {
                txtStatus.setText("Status: Connected");
                txtStatus.setTextColor(ContextCompat.getColor(requireContext(), android.R.color.holo_green_dark));
            } else {
                txtStatus.setText("Status: Searching for Server...");
                txtStatus.setTextColor(ContextCompat.getColor(requireContext(), android.R.color.holo_orange_dark));
            }
        } else {
            if (nsdDiscoveryManager != null && nsdDiscoveryManager.isDiscovering()) {
                if (autoConnectSuppressed || !isAutoConnectEnabled()) {
                    txtStatus.setText("Status: Scanning (manual mode)");
                } else {
                    txtStatus.setText("Status: Scanning (auto-connect)...");
                }
                txtStatus.setTextColor(ContextCompat.getColor(requireContext(), android.R.color.holo_blue_dark));
            } else {
                txtStatus.setText("Status: Disconnected");
                txtStatus.setTextColor(ContextCompat.getColor(requireContext(), android.R.color.darker_gray));
            }
        }
    }

    // ================================================================
    //  Audio Service
    // ================================================================

    private void startAudioService() {
        String sIPAddress = Objects.requireNonNull(etIPAddress.getText()).toString().trim();
        if (sIPAddress.isEmpty()) {
            Toast.makeText(getContext(), "Vui lòng nhập địa chỉ IP", Toast.LENGTH_SHORT).show();
            return;
        }
        if (!Utils.isValidIPv4(sIPAddress)) {
            Toast.makeText(getContext(), "Địa chỉ IP đã nhập không hợp lệ", Toast.LENGTH_SHORT).show();
            return;
        }
        startAudioServiceWithParams(sIPAddress, 5000);
    }

    private void startAudioServiceWithParams(String ip, int port) {
        if (AudioService.isServiceRunning) return;

        saveIpToHistory(ip);

        SharedPreferences prefs = requireContext().getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        int target = prefs.getInt(KEY_JITTER_TARGET, 60);
        int max = prefs.getInt(KEY_JITTER_MAX, 120);

        // Update state immediately for UX
        AudioService.isServiceRunning = true;

        Intent serviceIntent = new Intent(getContext(), AudioService.class);
        serviceIntent.putExtra("IP_ADDRESS", ip);
        serviceIntent.putExtra("PORT", port);
        serviceIntent.putExtra("JITTER_TARGET", target);
        serviceIntent.putExtra("JITTER_MAX", max);
        serviceIntent.putExtra("EXCLUSIVE_MODE", prefs.getBoolean("exclusive_audio", false));
        serviceIntent.putExtra("USE_AAUDIO", prefs.getBoolean("aaudio_enabled", false));
        ContextCompat.startForegroundService(requireContext(), serviceIntent);

        // Show connecting state
        btnConnect.setEnabled(false);
        txtStatus.setText("Connecting to " + ip + "...");
        txtStatus.setTextColor(ContextCompat.getColor(requireContext(), android.R.color.holo_orange_light));

        // Poll for actual server response before navigating
        final long connectStartTime = System.currentTimeMillis();
        final int CONNECT_TIMEOUT_MS = 2000;

        Handler connectHandler = new Handler(Looper.getMainLooper());
        connectHandler.postDelayed(new Runnable() {
            @Override
            public void run() {
                if (!isAdded()) return;

                if (AudioService.hasServerResponded()) {
                    // Server responded — navigate to ConnectedFragment
                    btnConnect.setEnabled(true);
                    if (getActivity() instanceof MainActivity) {
                        ((MainActivity) getActivity()).onConnectionStateChanged(ip);
                    }
                } else if (System.currentTimeMillis() - connectStartTime < CONNECT_TIMEOUT_MS) {
                    // Still waiting — poll again
                    connectHandler.postDelayed(this, 500);
                } else {
                    // Timeout — no server response
                    btnConnect.setEnabled(true);
                    txtStatus.setText("Không tìm thấy server tại " + ip);
                    txtStatus.setTextColor(ContextCompat.getColor(requireContext(), android.R.color.holo_red_light));
                    Toast.makeText(getContext(), "Không tìm thấy server. Kiểm tra IP và thử lại.", Toast.LENGTH_LONG).show();
                    
                    // "Forget it" - suppress future auto-connects for this session
                    autoConnectSuppressed = true;

                    // Stop the service
                    AudioService.isServiceRunning = false;
                    AudioService.connectedIp = null;
                    Intent stopIntent = new Intent(getContext(), AudioService.class);
                    if (getContext() != null) getContext().stopService(stopIntent);
                }
            }
        }, 500);
    }

    // ================================================================
    //  Server List
    // ================================================================

    private void updateServerList() {
        if (!isAdded()) return;
        serverListContainer.removeAllViews();
        if (discoveredServers.isEmpty()) {
            tvNoServers.setVisibility(View.VISIBLE);
        } else {
            tvNoServers.setVisibility(View.GONE);
            LayoutInflater inflater = LayoutInflater.from(requireContext());
            for (String ip : discoveredServers) {
                View serverView = inflater.inflate(R.layout.item_server, serverListContainer, false);
                TextView tvInfo = serverView.findViewById(R.id.tvServerInfo);
                tvInfo.setText("SERVER (" + ip + ")");
                serverView.setOnClickListener(v -> {
                    etIPAddress.setText(ip);
                    if (!AudioService.isServiceRunning) {
                        startAudioService();
                    }
                });
                serverListContainer.addView(serverView);
            }
        }
    }

    // ================================================================
    //  Persistence
    // ================================================================

    private void loadLastIp() {
        String lastIp = getLastConnectedIp();
        if (lastIp != null) {
            etIPAddress.setText(lastIp);
        }
    }

    private String getLastConnectedIp() {
        if (!isAdded()) return null;
        SharedPreferences prefs = requireContext().getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        String savedIps = prefs.getString(KEY_HISTORY_IPS, "");
        if (!savedIps.isEmpty()) {
            return savedIps.split(",")[0];
        }
        return null;
    }

    private void saveIpToHistory(String ip) {
        SharedPreferences prefs = requireContext().getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        String savedIps = prefs.getString(KEY_HISTORY_IPS, "");
        List<String> history = new ArrayList<>(Arrays.asList(savedIps.split(",")));
        history.remove("");
        history.remove(ip);
        history.add(0, ip);
        if (history.size() > MAX_HISTORY) history = history.subList(0, MAX_HISTORY);
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < history.size(); i++) {
            sb.append(history.get(i));
            if (i < history.size() - 1) sb.append(",");
        }
        prefs.edit().putString(KEY_HISTORY_IPS, sb.toString()).apply();
    }

    // ================================================================
    //  Lifecycle
    // ================================================================

    @Override
    public void onResume() {
        super.onResume();
        statusHandler.post(statusRunnable);
        if (!nsdStarted) {
            startNsdDiscovery();
        }
    }

    @Override
    public void onPause() {
        super.onPause();
        statusHandler.removeCallbacks(statusRunnable);
    }

    @Override
    public void onDestroy() {
        super.onDestroy();
        stopNsdDiscovery();
    }
}
