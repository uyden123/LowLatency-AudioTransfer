package com.example.audiooverlan.UI;

import android.content.Context;
import android.content.Intent;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.Editable;
import android.text.TextWatcher;
import android.transition.AutoTransition;
import android.transition.Transition;
import android.transition.TransitionManager;
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
import androidx.lifecycle.ViewModelProvider;

import com.example.audiooverlan.viewmodels.PlayerViewModel;
import com.example.audiooverlan.viewmodels.PlayerState;

import com.example.audiooverlan.services.AudioService;
import com.example.audiooverlan.network.NsdDiscoveryManager;
import com.example.audiooverlan.R;
import com.example.audiooverlan.utils.SettingsRepository;
import com.example.audiooverlan.utils.Utils;
import com.google.android.material.textfield.TextInputEditText;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.List;
import java.util.Objects;
import java.util.Set;

public class PlayerFragment extends Fragment {

    public static final String KEY_AUTO_CONNECT = "auto_connect";
    private static final int MAX_HISTORY = 5;

    /**
     * Session-level flag: when user manually STOPs, auto-connect is suppressed
     * until the app process is restarted. This is NOT persisted.
     */
    public static volatile boolean autoConnectSuppressed = false;

    private com.google.android.material.button.MaterialButton btnConnect;
    private ImageButton btnRefresh;
    private ProgressBar scanProgressBar;
    private TextInputEditText etIPAddress;
    private TextView tvIPError;
    private LinearLayout serverListContainer;
    private View layoutSearchingPlaceholder;
    private PlayerViewModel viewModel;
    private com.google.android.material.button.MaterialButtonToggleGroup engineToggleGroup;
    private com.google.android.material.button.MaterialButton btnMediaPlayback, btnAAudio;
    private boolean isNavigatingToConnected = false;

    private final java.util.Map<String, String> discoveredServers = new java.util.HashMap<>();

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
        tvIPError = view.findViewById(R.id.tvIPError);
        serverListContainer = view.findViewById(R.id.serverListContainer);
        layoutSearchingPlaceholder = view.findViewById(R.id.layoutSearchingPlaceholder);
        engineToggleGroup = view.findViewById(R.id.engineToggleGroup);
        btnMediaPlayback = view.findViewById(R.id.btnMediaPlayback);
        btnAAudio = view.findViewById(R.id.btnAAudio);

        viewModel = new ViewModelProvider(this).get(PlayerViewModel.class);
        observePlayerState();

        SettingsRepository repo = SettingsRepository.getInstance(requireContext());
        if (repo.isAaudioEnabled()) {
            engineToggleGroup.check(R.id.btnAAudio);
        } else {
            engineToggleGroup.check(R.id.btnMediaPlayback);
        }

        engineToggleGroup.addOnButtonCheckedListener((group, checkedId, isChecked) -> {
            if (isChecked) {
                if (checkedId == R.id.btnAAudio) {
                    repo.setAaudioEnabled(true);
                } else if (checkedId == R.id.btnMediaPlayback) {
                    repo.setAaudioEnabled(false);
                }
            }
        });

        com.google.android.material.switchmaterial.SwitchMaterial switchAutoConnect = view.findViewById(R.id.switchAutoConnect);
        switchAutoConnect.setChecked(repo.isAutoConnect());
        switchAutoConnect.setOnCheckedChangeListener((buttonView, isChecked) -> {
            repo.setAutoConnect(isChecked);
        });

        loadLastIp();

        btnConnect.setOnClickListener(v -> {
            if (AudioService.isServiceRunning)
                return;
            startAudioService();
        });

        btnRefresh.setOnClickListener(v -> restartNsdDiscovery());

        // Start mDNS/NSD discovery
        startNsdDiscovery();


        etIPAddress.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int start, int count, int after) {}
            @Override public void onTextChanged(CharSequence s, int start, int before, int count) {
                if (tvIPError.getVisibility() == View.VISIBLE) {
                    Transition fast = new AutoTransition().setDuration(150);
                    TransitionManager.beginDelayedTransition((ViewGroup) tvIPError.getParent().getParent(), fast);
                    tvIPError.setVisibility(View.GONE);
                }
            }
            @Override public void afterTextChanged(Editable s) {}
        });

        return view;
    }

    private void updateBufferingConfig() {
        SettingsRepository repo = SettingsRepository.getInstance(requireContext());
        int modeIdx = repo.getBufferMode();
        com.example.audiooverlan.audio.JitterBuffer.BufferMode mode = com.example.audiooverlan.audio.JitterBuffer.BufferMode.values()[modeIdx];
        AudioService.setBufferingConfig(mode, repo.getBufferCustomMinMs(), repo.getBufferCustomMaxMs());
    }

    private void observePlayerState() {
        viewModel.getPlayerState().observe(getViewLifecycleOwner(), state -> {
            if (!isAdded()) return;

            if (state instanceof PlayerState.Idle || state instanceof PlayerState.Disconnected) {
                updateUIForDisconnected();
                isNavigatingToConnected = false;
            } else if (state instanceof PlayerState.Connecting) {
                updateUIForConnecting(((PlayerState.Connecting) state).ip);
            } else if (state instanceof PlayerState.Playing) {
                updateUIForConnected();
                handleNavigationToConnected(((PlayerState.Playing) state).ip);
            }
        });
    }

    private void updateUIForDisconnected() {
        if (!isAdded()) return;
        btnConnect.setEnabled(true);
        // Status labels removed as per request
    }

    private void updateUIForConnecting(String ip) {
        if (!isAdded()) return;
        btnConnect.setEnabled(false);
    }

    private void updateUIForConnected() {
        if (!isAdded()) return;
        btnConnect.setEnabled(false);
    }

    private void handleNavigationToConnected(String ip) {
        if (!isNavigatingToConnected && getActivity() instanceof MainActivity) {
            isNavigatingToConnected = true;
            ((MainActivity) getActivity()).onConnectionStateChanged(ip);
        }
    }


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
            public void onServerDiscovered(String name, String ip, int port) {
                if (!isAdded()) return;

                // Add to discovered server list for UI
                boolean isNew = !discoveredServers.containsKey(ip);
                discoveredServers.put(ip, name);
                if (isNew) {
                    updateServerList();
                }

                // Show scan complete
                btnRefresh.setVisibility(View.VISIBLE);
                scanProgressBar.setVisibility(View.INVISIBLE);

                // Auto-connect if enabled and matches last connected IP
                if (!AudioService.isServiceRunning && isAutoConnectEnabled() && !autoConnectSuppressed) {
                    String lastIp = getLastConnectedIp();
                    if (ip.equals(lastIp)) {
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
        return SettingsRepository.getInstance(requireContext()).isAutoConnect();
    }

    // ================================================================
    //  Status / UI
    // ================================================================

    // Removed updateButtonState as it is replaced by LiveData observation


    // ================================================================
    //  Audio Service
    // ================================================================

    private void startAudioService() {
        String sIPAddress = Objects.requireNonNull(etIPAddress.getText()).toString().trim();
        Transition fast = new AutoTransition().setDuration(150);
        if (sIPAddress.isEmpty()) {
            TransitionManager.beginDelayedTransition((ViewGroup) tvIPError.getParent().getParent(), fast);
            tvIPError.setText("Vui lòng nhập địa chỉ IP");
            tvIPError.setVisibility(View.VISIBLE);
            return;
        }
        if (!Utils.isValidIPv4(sIPAddress)) {
            TransitionManager.beginDelayedTransition((ViewGroup) tvIPError.getParent().getParent(), fast);
            tvIPError.setText("Địa chỉ IP không hợp lệ");
            tvIPError.setVisibility(View.VISIBLE);
            return;
        }
        startAudioServiceWithParams(sIPAddress, 5000);
    }

    private void startAudioServiceWithParams(String ip, int port) {
        if (AudioService.isServiceRunning) return;

        saveIpToHistory(ip);

        AudioService.isServiceRunning = true;

        Intent serviceIntent = new Intent(getContext(), AudioService.class);
        serviceIntent.putExtra("IP_ADDRESS", ip);
        serviceIntent.putExtra("PORT", port);
        ContextCompat.startForegroundService(requireContext(), serviceIntent);

        // Show connecting state
        btnConnect.setEnabled(false);

        // Timeout logic for connection attempt (local to this request)
        final long connectStartTime = System.currentTimeMillis();
        final int CONNECT_TIMEOUT_MS = 2500;

        Handler timeoutHandler = new Handler(Looper.getMainLooper());
        timeoutHandler.postDelayed(new Runnable() {
            @Override
            public void run() {
                if (!isAdded()) return;

                PlayerState currentInfo = viewModel.getPlayerState().getValue();
                
                // If we are already playing or idle (stopped), don't trigger timeout
                if (currentInfo instanceof PlayerState.Playing || currentInfo instanceof PlayerState.Idle || currentInfo instanceof PlayerState.Disconnected) {
                    return;
                }

                if (System.currentTimeMillis() - connectStartTime < CONNECT_TIMEOUT_MS) {
                    timeoutHandler.postDelayed(this, 500);
                } else {
                    // Actual Timeout
                    btnConnect.setEnabled(true);
                    Transition fast = new AutoTransition().setDuration(150);
                    TransitionManager.beginDelayedTransition((ViewGroup) tvIPError.getParent().getParent(), fast);
                    tvIPError.setText("Không tìm thấy server");
                    tvIPError.setVisibility(View.VISIBLE);
                    
                    autoConnectSuppressed = true;

                    // Stop the service
                    Intent stopIntent = new Intent(getContext(), AudioService.class);
                    if (getContext() != null) getContext().stopService(stopIntent);
                }
            }
        }, 1000);
    }

    // ================================================================
    //  Server List
    // ================================================================

    private void updateServerList() {
        if (!isAdded()) return;
        
        Transition fast = new AutoTransition().setDuration(150);
        TransitionManager.beginDelayedTransition(serverListContainer, fast);
        serverListContainer.removeAllViews();
        
        if (discoveredServers.isEmpty()) {
            layoutSearchingPlaceholder.setVisibility(View.VISIBLE);
            serverListContainer.addView(layoutSearchingPlaceholder);
        } else {
            layoutSearchingPlaceholder.setVisibility(View.GONE);
            LayoutInflater inflater = LayoutInflater.from(requireContext());
            for (java.util.Map.Entry<String, String> entry : discoveredServers.entrySet()) {
                String ip = entry.getKey();
                String name = entry.getValue();
                View serverView = inflater.inflate(R.layout.item_server, serverListContainer, false);
                TextView tvInfo = serverView.findViewById(R.id.tvServerInfo);
                TextView tvStatus = serverView.findViewById(R.id.tvStatus);
                tvInfo.setText(name);
                tvStatus.setText("READY"); // For now, assume ready
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
        String savedIps = SettingsRepository.getInstance(requireContext()).getHistoryIps();
        if (!savedIps.isEmpty()) {
            return savedIps.split(",")[0];
        }
        return null;
    }

    private void saveIpToHistory(String ip) {
        SettingsRepository repo = SettingsRepository.getInstance(requireContext());
        String savedIps = repo.getHistoryIps();
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
        repo.setHistoryIps(sb.toString());
    }

    // ================================================================
    //  Lifecycle
    // ================================================================

    @Override
    public void onResume() {
        super.onResume();
        if (!nsdStarted) {
            startNsdDiscovery();
        }
    }

    @Override
    public void onPause() {
        super.onPause();
    }

    @Override
    public void onDestroy() {
        super.onDestroy();
        stopNsdDiscovery();
    }
}
