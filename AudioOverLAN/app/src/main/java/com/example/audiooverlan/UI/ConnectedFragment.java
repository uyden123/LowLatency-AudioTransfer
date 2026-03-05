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
import android.widget.ArrayAdapter;
import android.widget.AutoCompleteTextView;
import android.widget.Button;
import com.google.android.material.button.MaterialButton;
import android.widget.LinearLayout;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.fragment.app.Fragment;

import com.example.audiooverlan.services.AudioService;
import com.example.audiooverlan.audio.JitterBuffer;
import com.example.audiooverlan.R;
import com.google.android.material.button.MaterialButtonToggleGroup;

import org.json.JSONObject;
import java.util.Locale;

public class ConnectedFragment extends Fragment {

    private static final String PREFS_NAME = "AudioOverLAN_Prefs";
    private static final String KEY_BITRATE_INDEX = "bitrate_index";
    private static final String KEY_ADAPTIVE_ENABLED = "adaptive_enabled";
    private static final String KEY_BUFFER_INDEX = "buffer_index";
    private static final String KEY_EXCLUSIVE_AUDIO = "exclusive_audio";
    private static final String KEY_EQ_PRESET = "eq_preset";
    private static final String KEY_AAUDIO_ENABLED = "aaudio_enabled";

    private TextView tvServerIp;
    private View viewStatusDot;
    private TextView tvStatusText;
    private TextView tvConnectionTime;
    private TextView tvBuffer;
    private TextView tvNetLatency;
    private TextView tvLatency;
    private TextView tvMaxLatency;
    private TextView tvAudioEngine;
    
    // Stream Health
    private TextView tvBitrate;
    private TextView tvLostPackets;
    private TextView tvLatePackets;

    // Audio Behavior
    private com.google.android.material.card.MaterialCardView cardAudioBehavior;
    private MaterialButtonToggleGroup toggleExclusive;
    private TextView tvExclusiveDescription;
    private MaterialButtonToggleGroup toggleDRC;
    private TextView tvDrcDescription;
    private AutoCompleteTextView actvEqPreset;
    private TextView tvEqDescription;
    private MaterialButtonToggleGroup toggleAAudio;
    private TextView tvAAudioDescription;

    // Buffer Settings
    private com.google.android.material.card.MaterialCardView cardBufferSettings;
    private com.google.android.material.switchmaterial.SwitchMaterial switchAdaptive;
    private LinearLayout llFixedBuffer;
    private AutoCompleteTextView actvBufferSize;
    private TextView tvBufferDescription;

    // Audio Settings
    private com.google.android.material.card.MaterialCardView cardAudioSettings;
    private AutoCompleteTextView actvBitrate;
    private TextView tvBitrateDescription;

    private View dimOverlay;
    private WaveformView waveformView;
    private Button btnStop;
    private MaterialButton btnMute;
    private boolean pcMuted = false;

    // Bitrate Data
    private static final String[] BITRATE_OPTIONS = {"64 kbps", "96 kbps", "128 kbps", "192 kbps", "256 kbps", "320 kbps"};
    private static final int[] BITRATE_VALUES = {64000, 96000, 128000, 192000, 256000, 320000};
    private static final String[] BITRATE_DESCS = {
            "Low quality, saves bandwidth.",
            "FM radio quality.",
            "Standard quality (default).",
            "High quality audio.",
            "Ultra-high quality.",
            "Lossless-like (Extreme)."
    };

    // Buffer Data
    private static final String[] BUFFER_OPTIONS = {"40 ms", "60 ms", "80 ms", "100 ms", "150 ms", "200 ms", "300 ms"};
    private static final int[] BUFFER_VALUES = {2, 3, 4, 5, 7, 10, 15}; // In packets (20ms)
    private static final String[] BUFFER_DESCS = {
            "Ultra low latency (Risky).",
            "Competitive gaming/Pro.",
            "Balanced latency (Default).",
            "Standard stable mode.",
            "Safe for unstable Wi-Fi.",
            "Very high stability.",
            "Maximum reliability."
    };

    // EQ Data
    private static final String[] EQ_OPTIONS = {"None", "BassBoost", "VocalBoost", "NightMode"};
    private static final String[] EQ_DESCS = {
            "No adjustment (Flat).",
            "Enhanced low end.",
            "Clearer voices.",
            "Reduced bass and harsh highs."
    };

    private final Handler updateHandler = new Handler(Looper.getMainLooper());
    private long startTime;

    private final Runnable updateRunnable = new Runnable() {
        @Override
        public void run() {
            if (AudioService.isServiceRunning) {
                updateUI();
                updateHandler.postDelayed(this, 500);
            }
        }
    };
    
    private final Runnable waveformRunnable = new Runnable() {
        @Override
        public void run() {
            if (AudioService.isServiceRunning && isAdded()) {
                short[] samples = AudioService.getLatestSamples();
                if (samples != null && waveformView != null) {
                    waveformView.updateSamples(samples);
                }
                updateHandler.postDelayed(this, 20); // 50 fps
            }
        }
    };

    @Nullable
    @Override
    public View onCreateView(@NonNull LayoutInflater inflater, @Nullable ViewGroup container, @Nullable Bundle savedInstanceState) {
        View view = inflater.inflate(R.layout.fragment_connected, container, false);

        // Basic Info
        tvServerIp = view.findViewById(R.id.tvServerIp);
        viewStatusDot = view.findViewById(R.id.viewStatusDot);
        tvStatusText = view.findViewById(R.id.tvStatusText);
        tvConnectionTime = view.findViewById(R.id.tvConnectionTime);
        tvBuffer = view.findViewById(R.id.tvBuffer);
        tvNetLatency = view.findViewById(R.id.tvNetLatency);
        tvLatency = view.findViewById(R.id.tvLatency);
        tvMaxLatency = view.findViewById(R.id.tvMaxLatency);
        tvAudioEngine = view.findViewById(R.id.tvAudioEngine);
        
        // Stream Health
        tvBitrate = view.findViewById(R.id.tvBitrate);
        tvLostPackets = view.findViewById(R.id.tvLostPackets);
        tvLatePackets = view.findViewById(R.id.tvLatePackets);
        
        // Audio Behavior
        cardAudioBehavior = view.findViewById(R.id.cardAudioBehavior);
        toggleExclusive = view.findViewById(R.id.toggleExclusive);
        tvExclusiveDescription = view.findViewById(R.id.tvExclusiveDescription);

        // Buffer Settings
        cardBufferSettings = view.findViewById(R.id.cardBufferSettings);
        switchAdaptive = view.findViewById(R.id.switchAdaptive);
        llFixedBuffer = view.findViewById(R.id.llFixedBuffer);
        actvBufferSize = view.findViewById(R.id.actvBufferSize);
        tvBufferDescription = view.findViewById(R.id.tvBufferDescription);

        // Audio Settings
        cardAudioSettings = view.findViewById(R.id.cardAudioSettings);
        actvBitrate = view.findViewById(R.id.actvBitrate);
        tvBitrateDescription = view.findViewById(R.id.tvBitrateDescription);
        toggleDRC = view.findViewById(R.id.toggleDRC);
        tvDrcDescription = view.findViewById(R.id.tvDrcDescription);
        actvEqPreset = view.findViewById(R.id.actvEqPreset);
        tvEqDescription = view.findViewById(R.id.tvEqDescription);
        toggleAAudio = view.findViewById(R.id.toggleAAudio);
        tvAAudioDescription = view.findViewById(R.id.tvAAudioDescription);

        dimOverlay = view.findViewById(R.id.dimOverlay);
        waveformView = view.findViewById(R.id.waveformView);
        btnStop = view.findViewById(R.id.btnStop);
        btnMute = view.findViewById(R.id.btnMute);

        setupAudioBehavior();
        setupEqDropdown();
        setupBufferSettings();
        setupBitrateDropdown();

        if (getArguments() != null) {
            String ip = getArguments().getString("IP_ADDRESS");
            tvServerIp.setText(ip);
        } else if (AudioService.connectedIp != null) {
            tvServerIp.setText(AudioService.connectedIp);
        }

        startTime = System.currentTimeMillis();

        btnStop.setOnClickListener(v -> stopAudioService());
        
        btnMute.setOnClickListener(v -> {
            pcMuted = !pcMuted;
            updateMuteButtonUI();
            sendMuteUpdate(pcMuted);
        });

        updateHandler.post(updateRunnable);
        updateHandler.post(waveformRunnable);
        
        // Initial sync of settings to service
        syncAllSettings();

        return view;
    }

    private void syncAllSettings() {
        new Handler(Looper.getMainLooper()).postDelayed(() -> {
            if (!isAdded()) return;
            SharedPreferences prefs = requireContext().getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
            
            // Sync Bitrate
            int bIdx = prefs.getInt(KEY_BITRATE_INDEX, 2);
            sendBitrateUpdate(BITRATE_VALUES[bIdx]);

            // Sync Buffer mode
            boolean adaptive = prefs.getBoolean(KEY_ADAPTIVE_ENABLED, true);
            int bufIdx = prefs.getInt(KEY_BUFFER_INDEX, 2);
            AudioService.setBufferMode(adaptive, BUFFER_VALUES[bufIdx]);

            // Sync Audio Behavior
            boolean exclusive = prefs.getBoolean(KEY_EXCLUSIVE_AUDIO, false);
            AudioService.setExclusiveAudioMode(exclusive);

            // Sync DRC
            boolean drc = prefs.getBoolean(AudioService.KEY_DRC_ENABLED, false);
            sendDrcUpdate(drc);

            // Sync EQ
            String eq = prefs.getString(KEY_EQ_PRESET, "None");
            sendEqUpdate(eq);

            // Sync Mute
            sendMuteUpdate(pcMuted);
        }, 1000);
    }

    private void setupAudioBehavior() {
        SharedPreferences prefs = requireContext().getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        
        // Exclusive Audio
        boolean exclusive = prefs.getBoolean(KEY_EXCLUSIVE_AUDIO, false);
        toggleExclusive.check(exclusive ? R.id.btnExclusiveOn : R.id.btnExclusiveOff);
        tvExclusiveDescription.setText(exclusive ? "Auto-pause when other apps play." : "Play along with other apps.");
        
        toggleExclusive.addOnButtonCheckedListener((group, checkedId, isChecked) -> {
            if (!isChecked) return;
            boolean on = checkedId == R.id.btnExclusiveOn;
            prefs.edit().putBoolean(KEY_EXCLUSIVE_AUDIO, on).apply();
            tvExclusiveDescription.setText(on ? "Auto-pause when other apps play." : "Play along with other apps.");
            AudioService.setExclusiveAudioMode(on);
        });

        // DRC
        boolean drc = prefs.getBoolean(AudioService.KEY_DRC_ENABLED, false);
        toggleDRC.check(drc ? R.id.btnDrcOn : R.id.btnDrcOff);
        tvDrcDescription.setText(drc ? "Active on server." : "Bypassed.");
        
        toggleDRC.addOnButtonCheckedListener((group, checkedId, isChecked) -> {
            if (!isChecked) return;
            boolean on = checkedId == R.id.btnDrcOn;
            prefs.edit().putBoolean(AudioService.KEY_DRC_ENABLED, on).apply();
            tvDrcDescription.setText(on ? "Active on server." : "Bypassed.");
            sendDrcUpdate(on);
        });

        // AAudio
        boolean aaudio = prefs.getBoolean(KEY_AAUDIO_ENABLED, false);
        if (!AudioService.isAAudioAvailable()) {
            toggleAAudio.check(R.id.btnAudioTrack);
            for (int i = 0; i < toggleAAudio.getChildCount(); i++) {
                toggleAAudio.getChildAt(i).setEnabled(false);
            }
            tvAAudioDescription.setText("Not available: native library not loaded.");
        } else {
            toggleAAudio.check(aaudio ? R.id.btnAAudio : R.id.btnAudioTrack);
            tvAAudioDescription.setText(aaudio 
                ? "Native AAudio — lowest latency."
                : "Standard AudioTrack playback.");
        }

        toggleAAudio.addOnButtonCheckedListener((group, checkedId, isChecked) -> {
            if (!isChecked) return;
            boolean on = checkedId == R.id.btnAAudio;
            prefs.edit().putBoolean(KEY_AAUDIO_ENABLED, on).apply();
            AudioService.setAAudioMode(on);
            tvAAudioDescription.setText(on
                ? "Native AAudio — lowest latency."
                : "Standard AudioTrack playback.");
        });
    }

    private void setupEqDropdown() {
        SharedPreferences prefs = requireContext().getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        String savedEq = prefs.getString(KEY_EQ_PRESET, "None");

        SettingsAdapter adapter = new SettingsAdapter(requireContext(), EQ_OPTIONS, EQ_DESCS);
        actvEqPreset.setAdapter(adapter);
        actvEqPreset.setText(savedEq, false);
        // Reset filter so all items show (setText triggers filtering)
        actvEqPreset.post(() -> {
            actvEqPreset.setAdapter(new SettingsAdapter(requireContext(), EQ_OPTIONS, EQ_DESCS));
        });
        
        // Find index for description
        int idx = 0;
        for (int i = 0; i < EQ_OPTIONS.length; i++) {
            if (EQ_OPTIONS[i].equals(savedEq)) {
                idx = i;
                break;
            }
        }
        tvEqDescription.setText(EQ_DESCS[idx]);

        actvEqPreset.setOnItemClickListener((parent, view, position, id) -> {
            String preset = EQ_OPTIONS[position];
            tvEqDescription.setText(EQ_DESCS[position]);
            prefs.edit().putString(KEY_EQ_PRESET, preset).apply();
            sendEqUpdate(preset);
        });
    }

    private void setupBufferSettings() {
        SharedPreferences prefs = requireContext().getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        boolean adaptive = prefs.getBoolean(KEY_ADAPTIVE_ENABLED, true);
        int bufIdx = prefs.getInt(KEY_BUFFER_INDEX, 2);

        switchAdaptive.setChecked(adaptive);
        llFixedBuffer.setVisibility(adaptive ? View.GONE : View.VISIBLE);
        tvBufferDescription.setText(adaptive ? "Automatically adjusts for network jitter." : BUFFER_DESCS[bufIdx]);

        // Dropdown Adapter
        SettingsAdapter bufAdapter = new SettingsAdapter(requireContext(), BUFFER_OPTIONS, BUFFER_DESCS);
        actvBufferSize.setAdapter(bufAdapter);
        actvBufferSize.setText(BUFFER_OPTIONS[bufIdx], false);
        // Reset filter so all items show
        actvBufferSize.post(() -> {
            actvBufferSize.setAdapter(new SettingsAdapter(requireContext(), BUFFER_OPTIONS, BUFFER_DESCS));
        });

        switchAdaptive.setOnCheckedChangeListener((buttonView, isChecked) -> {
            prefs.edit().putBoolean(KEY_ADAPTIVE_ENABLED, isChecked).apply();
            llFixedBuffer.setVisibility(isChecked ? View.GONE : View.VISIBLE);
            tvBufferDescription.setText(isChecked ? "Automatically adjusts for network jitter." : BUFFER_DESCS[prefs.getInt(KEY_BUFFER_INDEX, 2)]);
            
            AudioService.setBufferMode(isChecked, BUFFER_VALUES[prefs.getInt(KEY_BUFFER_INDEX, 2)]);
        });

        actvBufferSize.setOnItemClickListener((parent, view, position, id) -> {
            prefs.edit().putInt(KEY_BUFFER_INDEX, position).apply();
            tvBufferDescription.setText(BUFFER_DESCS[position]);
            AudioService.setBufferMode(false, BUFFER_VALUES[position]);
        });

        // UI Feedback
        actvBufferSize.setOnFocusChangeListener((v, hasFocus) -> {
            if (hasFocus) showFocusEffects(cardBufferSettings);
            else hideFocusEffects(cardBufferSettings);
        });
        actvBufferSize.setOnClickListener(v -> showFocusEffects(cardBufferSettings));
    }

    private void setupBitrateDropdown() {
        SharedPreferences prefs = requireContext().getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        int savedIdx = prefs.getInt(KEY_BITRATE_INDEX, 2);

        SettingsAdapter adapter = new SettingsAdapter(requireContext(), BITRATE_OPTIONS, BITRATE_DESCS);
        actvBitrate.setAdapter(adapter);
        actvBitrate.setText(BITRATE_OPTIONS[savedIdx], false);
        tvBitrateDescription.setText(BITRATE_DESCS[savedIdx]);
        // Reset filter so all items show (setText triggers filtering)
        actvBitrate.post(() -> {
            actvBitrate.setAdapter(new SettingsAdapter(requireContext(), BITRATE_OPTIONS, BITRATE_DESCS));
        });

        actvBitrate.setOnFocusChangeListener((v, hasFocus) -> {
            if (hasFocus) showFocusEffects(cardAudioSettings);
            else hideFocusEffects(cardAudioSettings);
        });

        actvBitrate.setOnClickListener(v -> showFocusEffects(cardAudioSettings));

        actvBitrate.setOnItemClickListener((parent, view, position, id) -> {
            int bitrate = BITRATE_VALUES[position];
            tvBitrateDescription.setText(BITRATE_DESCS[position]);
            prefs.edit().putInt(KEY_BITRATE_INDEX, position).apply();
            sendBitrateUpdate(bitrate);
            hideFocusEffects(cardAudioSettings);
        });

        dimOverlay.setOnClickListener(v -> {
            actvBitrate.clearFocus();
            actvBufferSize.clearFocus();
            actvEqPreset.clearFocus();
            actvBitrate.dismissDropDown();
            actvBufferSize.dismissDropDown();
            actvEqPreset.dismissDropDown();
            hideFocusEffects(cardAudioSettings);
            hideFocusEffects(cardBufferSettings);
            hideFocusEffects(cardAudioBehavior);
        });
    }

    private void showFocusEffects(com.google.android.material.card.MaterialCardView card) {
        dimOverlay.setVisibility(View.VISIBLE);
        dimOverlay.animate().alpha(1f).setDuration(300).start();
        card.setStrokeWidth(4);
        card.setZ(10f);
    }

    private void hideFocusEffects(com.google.android.material.card.MaterialCardView card) {
        dimOverlay.animate().alpha(0f).setDuration(300).withEndAction(() -> {
            dimOverlay.setVisibility(View.GONE);
        }).start();
        card.setStrokeWidth(0);
        card.setZ(0f);
    }

    private void sendBitrateUpdate(int bitrate) {
        try {
            JSONObject command = new JSONObject();
            command.put("command", "set_bitrate");
            command.put("value", bitrate);
            AudioService.sendServerCommand(command);
        } catch (Exception e) {
            e.printStackTrace();
        }
    }

    private void sendDrcUpdate(boolean enabled) {
        try {
            JSONObject command = new JSONObject();
            command.put("command", "set_drc");
            command.put("value", enabled);
            AudioService.sendServerCommand(command);
        } catch (Exception e) {
            e.printStackTrace();
        }
    }

    private void sendEqUpdate(String preset) {
        try {
            JSONObject command = new JSONObject();
            command.put("command", "set_eq");
            command.put("value", preset);
            AudioService.sendServerCommand(command);
        } catch (Exception e) {
            e.printStackTrace();
        }
    }

    private void sendMuteUpdate(boolean muted) {
        try {
            JSONObject command = new JSONObject();
            command.put("command", "set_mute");
            command.put("value", muted);
            AudioService.sendServerCommand(command);
        } catch (Exception e) {
            e.printStackTrace();
        }
    }

    private void updateMuteButtonUI() {
        if (pcMuted) {
            btnMute.setText("UNMUTE");
            btnMute.setIconResource(R.drawable.ic_volume_off);
        } else {
            btnMute.setText("MUTE");
            btnMute.setIconResource(R.drawable.ic_volume_up);
        }
    }

    private void updateUI() {
        if (!isAdded()) return;

        tvAudioEngine.setText(AudioService.getAudioEngineName());
        tvLatency.setText(String.format(Locale.getDefault(), "Buf Avg: %.1f ms", AudioService.getAvgLatency()));
        tvMaxLatency.setText(String.format(Locale.getDefault(), "Buf Max: %d ms", AudioService.getMaxLatency()));
        
        JitterBuffer.Statistics stats = AudioService.getJitterBufferStats();
        if (stats != null) {
            int targetMs = stats.targetPackets * 20;
            tvBuffer.setText(String.format(Locale.getDefault(), "%d / %d ms", stats.delayMs, targetMs));
            tvNetLatency.setText(String.format(Locale.getDefault(), "%d ms", stats.lastTransitDelay));
            tvBitrate.setText(String.format(Locale.getDefault(), "%.1f kbps", stats.bitrateKbps));
            tvLostPackets.setText(String.format(Locale.getDefault(), "%.1f%%", stats.lossRate));
            tvLatePackets.setText(String.format(Locale.getDefault(), "%d pkts", stats.latePackets));
            
            if (stats.lossRate > 5.0) tvLostPackets.setTextColor(getResources().getColor(android.R.color.holo_red_light));
            else if (stats.lossRate > 2.0) tvLostPackets.setTextColor(getResources().getColor(android.R.color.holo_orange_light));
            else tvLostPackets.setTextColor(getResources().getColor(android.R.color.white));
        }

        // Update status indicator
        boolean connected = AudioService.isConnectedToProcessor();
        if (connected) {
            long elapsed = (System.currentTimeMillis() - startTime) / 1000;
            long mins = elapsed / 60;
            long secs = elapsed % 60;
            tvConnectionTime.setText(String.format(Locale.getDefault(), "%02d:%02d", mins, secs));

            viewStatusDot.setBackgroundTintList(android.content.res.ColorStateList.valueOf(getResources().getColor(R.color.primary_green)));
            tvStatusText.setText("Status: Connected");
            tvStatusText.setTextColor(getResources().getColor(R.color.primary_green));
        } else {
            viewStatusDot.setBackgroundTintList(android.content.res.ColorStateList.valueOf(getResources().getColor(android.R.color.holo_orange_light)));
            tvStatusText.setText("Status: RECONNECTING...");
            tvStatusText.setTextColor(getResources().getColor(android.R.color.holo_orange_light));
            
            // Also reset some fields to indicate no data
            tvBitrate.setText("0.0 kbps");
            tvNetLatency.setText("-- ms");
        }
    }

    private void stopAudioService() {
        PlayerFragment.autoConnectSuppressed = true;
        AudioService.isServiceRunning = false;
        AudioService.connectedIp = null;
        Intent intent = new Intent(getContext(), AudioService.class);
        if (getContext() != null) getContext().stopService(intent);
        if (getActivity() instanceof MainActivity) ((MainActivity) getActivity()).onConnectionStateChanged(null);
    }

    @Override
    public void onDestroyView() {
        super.onDestroyView();
        updateHandler.removeCallbacks(updateRunnable);
        updateHandler.removeCallbacks(waveformRunnable);
    }

    private static class SettingsAdapter extends ArrayAdapter<String> {
        private final String[] titles;
        private final String[] descs;

        public SettingsAdapter(@NonNull Context context, String[] titles, String[] descs) {
            super(context, R.layout.item_bitrate, titles);
            this.titles = titles;
            this.descs = descs;
        }

        @NonNull
        @Override
        public View getView(int position, @Nullable View convertView, @NonNull ViewGroup parent) {
            if (convertView == null) {
                convertView = LayoutInflater.from(getContext()).inflate(R.layout.item_bitrate, parent, false);
            }
            TextView tvTitle = convertView.findViewById(R.id.tvItemTitle);
            TextView tvDesc = convertView.findViewById(R.id.tvItemDesc);
            tvTitle.setText(titles[position]);
            tvDesc.setText(descs[position]);
            return convertView;
        }
    }
}
