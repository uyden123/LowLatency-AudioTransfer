package com.example.audiooverlan.UI;

import android.content.Context;
import android.content.Intent;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ArrayAdapter;
import android.widget.ImageButton;
import android.widget.LinearLayout;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.core.content.ContextCompat;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.transition.TransitionManager;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ArrayAdapter;
import android.widget.ImageButton;
import android.widget.LinearLayout;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.core.content.ContextCompat;
import androidx.fragment.app.Fragment;
import androidx.lifecycle.ViewModelProvider;

import com.example.audiooverlan.R;
import com.example.audiooverlan.audio.AudioConstants;
import com.example.audiooverlan.audio.JitterBuffer;
import com.example.audiooverlan.audio.MixerSession;
import com.example.audiooverlan.services.AudioService;
import com.example.audiooverlan.utils.SettingsRepository;
import com.example.audiooverlan.viewmodels.PlayerState;
import com.example.audiooverlan.viewmodels.PlayerStateRepository;
import com.example.audiooverlan.viewmodels.PlayerViewModel;
import com.google.android.material.button.MaterialButtonToggleGroup;
import com.google.android.material.slider.Slider;
import com.google.android.material.switchmaterial.SwitchMaterial;
import com.google.android.material.textfield.TextInputEditText;
import android.text.TextWatcher;
import android.text.Editable;
import android.transition.AutoTransition;
import android.transition.Transition;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

public class ConnectedFragment extends Fragment {

    // Header & Status
    private TextView tvStatusText;
    private View statusDot;
    private ImageButton btnStop;
    private WaveformView waveformView;

    // 2x2 Stats
    private TextView tvBuffer;
    private TextView tvTotalLatency;
    private TextView tvNetworkPing;
    private TextView tvAudioEngine;

    // Stream Health
    private TextView tvBitrate;
    private TextView tvLostPackets;
    private TextView tvLatePackets;

    // Playback Strategy
    private MaterialButtonToggleGroup togglePlaybackStrategy;
    
    // Buffer Settings
    private MaterialButtonToggleGroup bufferModeToggleGroup;
    private LinearLayout layoutCustomBuffer;
    private TextInputEditText etMinBufferMs, etMaxBufferMs;

    private PlayerViewModel viewModel;
    private SettingsRepository settings;
    private final Handler updateHandler = new Handler(Looper.getMainLooper());
    private boolean pcMuted = false;

    // Bitrate Data
    private static final int[] BITRATE_VALUES = {32000, 64000, 96000, 128000, 192000, 256000, 320000};
    
    // Volume Mixer
    private VolumeMixerDialog mixerDialog;
    private TextView tvMixerStatusSummary;
    private final Map<Long, String> iconCache = new HashMap<>();
    private final Set<Long> pendingIconRequests = new HashSet<>();
    
    // Stream Configuration
    private TextView tvOpusBitrateValue;
    private Slider sliderBitrate;
    private SwitchMaterial switchDrc;

    private final Runnable waveformRunnable = new Runnable() {
        @Override
        public void run() {
            if (AudioService.isServiceRunning && isAdded() && isVisible()) {
                short[] samples = AudioService.getLatestSamples();
                if (samples != null && waveformView != null) {
                    waveformView.updateSamples(samples);
                }
                updateHandler.postDelayed(this, 30);
            } else if (AudioService.isServiceRunning && isAdded()) {
                updateHandler.postDelayed(this, 1000);
            }
        }
    };

    @Nullable
    @Override
    public View onCreateView(@NonNull LayoutInflater inflater, @Nullable ViewGroup container, @Nullable Bundle savedInstanceState) {
        View view = inflater.inflate(R.layout.fragment_connected, container, false);
        settings = SettingsRepository.getInstance(requireContext());
        
        initViews(view);
        setupControls();
        
        viewModel = new ViewModelProvider(this).get(PlayerViewModel.class);
        observePlayerState();

        // Initial settings sync
        syncSettingsToService();

        return view;
    }

    private void initViews(View view) {
        tvStatusText = view.findViewById(R.id.tvStatusText);
        statusDot = view.findViewById(R.id.statusDot);
        btnStop = view.findViewById(R.id.btnStop);
        waveformView = view.findViewById(R.id.waveformView);

        tvBuffer = view.findViewById(R.id.tvBuffer);
        tvTotalLatency = view.findViewById(R.id.tvNetLatency);
        tvNetworkPing = view.findViewById(R.id.tvNetworkPing);
        tvAudioEngine = view.findViewById(R.id.tvAudioEngine);

        tvBitrate = view.findViewById(R.id.tvBitrate);
        tvLostPackets = view.findViewById(R.id.tvLostPackets);
        tvLatePackets = view.findViewById(R.id.tvLatePackets);

        togglePlaybackStrategy = view.findViewById(R.id.togglePlaybackStrategy);

        bufferModeToggleGroup = view.findViewById(R.id.bufferModeToggleGroup);
        layoutCustomBuffer = view.findViewById(R.id.layoutCustomBuffer);
        etMinBufferMs = view.findViewById(R.id.etMinBufferMs);
        etMaxBufferMs = view.findViewById(R.id.etMaxBufferMs);

        tvOpusBitrateValue = view.findViewById(R.id.tvOpusBitrateValue);
        sliderBitrate = view.findViewById(R.id.sliderBitrate);
        switchDrc = view.findViewById(R.id.switchDrc);
    }

    private void setupControls() {
        btnStop.setOnClickListener(v -> stopAudioService());

        // Playback Strategy Init
        boolean isExclusive = settings.isExclusiveAudio();
        togglePlaybackStrategy.check(isExclusive ? R.id.btnSolo : R.id.btnShared);

        togglePlaybackStrategy.addOnButtonCheckedListener((group, checkedId, isChecked) -> {
            if (isChecked) {
                boolean targetExclusive = (checkedId == R.id.btnSolo);
                if (settings.isExclusiveAudio() != targetExclusive) {
                    settings.setExclusiveAudio(targetExclusive);
                    // Just update focus mode, no restart needed
                    AudioService.setExclusiveAudioMode(targetExclusive);
                }
            }
        });

        // Buffer Mode Init
        int savedModeIdx = settings.getBufferMode();
        int[] modeButtonIds = {R.id.btnModeLow, R.id.btnModeMedium, R.id.btnModeHigh, R.id.btnModeCustom};
        bufferModeToggleGroup.check(modeButtonIds[Math.min(savedModeIdx, 3)]);
        
        etMinBufferMs.setText(String.valueOf(settings.getBufferCustomMinMs()));
        etMaxBufferMs.setText(String.valueOf(settings.getBufferCustomMaxMs()));
        layoutCustomBuffer.setVisibility(savedModeIdx == 3 ? View.VISIBLE : View.GONE);

        bufferModeToggleGroup.addOnButtonCheckedListener((group, checkedId, isChecked) -> {
            if (isChecked) {
                int modeIdx = 1;
                if (checkedId == R.id.btnModeLow) modeIdx = 0;
                else if (checkedId == R.id.btnModeMedium) modeIdx = 1;
                else if (checkedId == R.id.btnModeHigh) modeIdx = 2;
                else if (checkedId == R.id.btnModeCustom) modeIdx = 3;

                settings.setBufferMode(modeIdx);
                
                Transition fast = new AutoTransition().setDuration(200);
                TransitionManager.beginDelayedTransition((ViewGroup) group.getParent(), fast);
                layoutCustomBuffer.setVisibility(modeIdx == 3 ? View.VISIBLE : View.GONE);

                updateBufferingConfig();
            }
        });

        TextWatcher bufferWatcher = new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int start, int count, int after) {}
            @Override public void onTextChanged(CharSequence s, int start, int before, int count) {}
            @Override public void afterTextChanged(Editable s) {
                try {
                    int min = Integer.parseInt(etMinBufferMs.getText().toString());
                    int max = Integer.parseInt(etMaxBufferMs.getText().toString());
                    settings.setBufferCustomMinMs(min);
                    settings.setBufferCustomMaxMs(max);
                    if (settings.getBufferMode() == 3) {
                        updateBufferingConfig();
                    }
                } catch (Exception ignored) {}
            }
        };
        etMinBufferMs.addTextChangedListener(bufferWatcher);
        etMaxBufferMs.addTextChangedListener(bufferWatcher);

        // Stream Configuration
        int bitIdx = settings.getBitrateIndex();
        if (bitIdx >= BITRATE_VALUES.length) bitIdx = 3; // 128kbps
        int currentBitrate = BITRATE_VALUES[bitIdx];
        sliderBitrate.setValue(currentBitrate / 1000f); 
        tvOpusBitrateValue.setText((currentBitrate / 1000) + " kbps");
        
        sliderBitrate.addOnChangeListener((slider, value, fromUser) -> {
            int kbps = (int) value;
            tvOpusBitrateValue.setText(kbps + " kbps");
            if (fromUser) {
                // Find matching index or find closest
                int foundIdx = 3;
                for(int i=0; i<BITRATE_VALUES.length; i++) {
                    if (BITRATE_VALUES[i] == kbps * 1000) {
                        foundIdx = i;
                        break;
                    }
                }
                settings.setBitrateIndex(foundIdx);
                sendServerCommand("set_bitrate", kbps * 1000);
            }
        });

        // DRC Toggle
        switchDrc.setChecked(settings.isDrcEnabled());
        switchDrc.setOnCheckedChangeListener((buttonView, isChecked) -> {
            settings.setDrcEnabled(isChecked);
            sendServerCommand("set_drc", isChecked);
        });
    }

    private void observePlayerState() {
        viewModel.getPlayerState().observe(getViewLifecycleOwner(), state -> {
            if (!isAdded()) return;
            if (state instanceof PlayerState.Playing) {
                updateUIWithState((PlayerState.Playing) state);
            } else if (state instanceof PlayerState.Connecting || state instanceof PlayerState.Reconnecting) {
                tvStatusText.setText("RECONNECTING...");
                int orange = ContextCompat.getColor(requireContext(), R.color.warning_orange);
                tvStatusText.setTextColor(orange);
                if (statusDot != null) {
                    statusDot.setBackgroundTintList(ColorStateList.valueOf(orange));
                }
            }
        });
    }

    private void updateUIWithState(PlayerState.Playing state) {
        tvStatusText.setText("CONNECTED");
        int green = ContextCompat.getColor(requireContext(), R.color.accent_green);
        tvStatusText.setTextColor(green);
        if (statusDot != null) {
            statusDot.setBackgroundTintList(ColorStateList.valueOf(green));
        }
        
        tvAudioEngine.setText(state.codecName);
        
        // BUFFER LEVEL: [Current / Target]
        float targetMs = state.targetPackets * AudioConstants.OPUS_FRAME_SIZE_MS;
        tvBuffer.setText(String.format(Locale.getDefault(), "%.1f / %.0f ms", state.bufferMs, targetMs));
        
        // TOTAL DELAY
        tvTotalLatency.setText(String.format(Locale.getDefault(), "%.1f ms", state.totalLatency));
        
        // NETWORK PING
        tvNetworkPing.setText(String.format(Locale.getDefault(), "%d ms", state.transitDelay));
        
        tvBitrate.setText(String.format(Locale.getDefault(), "%.1f kbps", state.bitrateKbps));
        tvLostPackets.setText(String.format(Locale.getDefault(), "%.1f%%", state.lossRate));
        tvLatePackets.setText(String.format(Locale.getDefault(), "%d", state.latePackets));
    }

    private void updateBufferingConfig() {
        if (!isAdded()) return;
        int modeIdx = settings.getBufferMode();
        JitterBuffer.BufferMode mode = JitterBuffer.BufferMode.values()[modeIdx];
        AudioService.setBufferingConfig(mode, settings.getBufferCustomMinMs(), settings.getBufferCustomMaxMs());
    }

    private void syncSettingsToService() {
        // Run slightly delayed to ensure connection is stable
        updateHandler.postDelayed(() -> {
            if (!isAdded()) return;
            sendServerCommand("set_bitrate", BITRATE_VALUES[settings.getBitrateIndex()]);
            sendServerCommand("set_drc", settings.isDrcEnabled());
            
            updateBufferingConfig();
            AudioService.setAAudioMode(settings.isAaudioEnabled());
        }, 1000);
    }

    private void sendServerCommand(String command, Object value) {
        try {
            JSONObject obj = new JSONObject();
            obj.put("command", command);
            obj.put("value", value);
            AudioService.sendServerCommand(obj);
        } catch (Exception e) {
            e.printStackTrace();
        }
    }

    private void stopAudioService() {
        PlayerStateRepository.getInstance().updateState(PlayerState.Disconnected.INSTANCE);
        PlayerFragment.autoConnectSuppressed = true;
        AudioService.isServiceRunning = false;
        AudioService.connectedIp = null;
        Intent intent = new Intent(getContext(), AudioService.class);
        if (getContext() != null) getContext().stopService(intent);
        if (getActivity() instanceof MainActivity) ((MainActivity) getActivity()).onConnectionStateChanged(null);
    }

    @Override
    public void onResume() {
        super.onResume();
        updateHandler.post(waveformRunnable);
    }

    @Override
    public void onPause() {
        super.onPause();
        updateHandler.removeCallbacks(waveformRunnable);
    }

    @Override
    public void onDestroyView() {
        super.onDestroyView();
        updateHandler.removeCallbacks(waveformRunnable);
    }
}
