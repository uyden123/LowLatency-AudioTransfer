package com.example.audiooverlan.UI;

import android.content.Context;
import android.content.Intent;
import android.media.MediaRecorder;
import android.net.wifi.WifiInfo;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.TextView;
import android.widget.Toast;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.fragment.app.Fragment;

import com.example.audiooverlan.services.AudioTransmitterService;
import com.example.audiooverlan.R;
import com.google.android.material.card.MaterialCardView;
import com.google.android.material.dialog.MaterialAlertDialogBuilder;
import com.google.android.material.floatingactionbutton.FloatingActionButton;
import com.google.android.material.switchmaterial.SwitchMaterial;

import java.util.Locale;

public class TransmittingFragment extends Fragment {

    private TextView tvDeviceName;
    private TextView tvMyIP;
    private TextView tvTimer;
    private TextView tvConnectionStatus;
    private FloatingActionButton btnStopTransmitting;
    
    // Switches
    private SwitchMaterial swVolumeBoost;
    private SwitchMaterial swAppNoiseSuppression;
    private SwitchMaterial swAAudio;
    private SwitchMaterial swExclusive;
    // AI AEC removed
    
    // Controls
    private MaterialCardView cardMicMode;
    private TextView tvMicModeValue;

    private final Handler handler = new Handler(Looper.getMainLooper());
    
    private final Runnable updateUIThread = new Runnable() {
        @Override
        public void run() {
            updateTimer();
            updateConnectionStatus();
            handler.postDelayed(this, 1000);
        }
    };

    @Nullable
    @Override
    public View onCreateView(@NonNull LayoutInflater inflater, @Nullable ViewGroup container, @Nullable Bundle savedInstanceState) {
        View view = inflater.inflate(R.layout.fragment_transmitting, container, false);

        tvDeviceName = view.findViewById(R.id.tvDeviceName);
        tvMyIP = view.findViewById(R.id.tvMyIP);
        tvTimer = view.findViewById(R.id.tvTimer);
        tvConnectionStatus = view.findViewById(R.id.tvConnectionStatus);
        btnStopTransmitting = view.findViewById(R.id.btnStopTransmitting);
        
        swVolumeBoost = view.findViewById(R.id.swVolumeBoost);
        swAppNoiseSuppression = view.findViewById(R.id.swAppNoiseSuppression);
        swAAudio = view.findViewById(R.id.swAAudio);
        swExclusive = view.findViewById(R.id.swExclusive);
        // AI AEC removed
        
        cardMicMode = view.findViewById(R.id.cardMicMode);
        tvMicModeValue = view.findViewById(R.id.tvMicModeValue);

        // Set device name
        tvDeviceName.setText(Build.MANUFACTURER + " " + Build.MODEL);

        // Set IP
        tvMyIP.setText(getLocalIpAddress());

        // Stop button
        btnStopTransmitting.setOnClickListener(v -> stopTransmitterService());

        setupSwitches();
        setupMicMode();

        handler.post(updateUIThread);

        return view;
    }

    private void setupSwitches() {
        android.content.SharedPreferences prefs = requireContext().getSharedPreferences(MainActivity.PREFS_NAME, Context.MODE_PRIVATE);

        // App Volume Boost
        swVolumeBoost.setChecked(AudioTransmitterService.volumeGain > 1.0f);
        swVolumeBoost.setOnCheckedChangeListener((bv, isChecked) -> {
            AudioTransmitterService.volumeGain = isChecked ? 2.0f : 1.0f;
            prefs.edit().putBoolean(AudioTransmitterService.KEY_VOLUME_BOOST, isChecked).apply();
        });

        // App Noise Suppression (DeepFilterNet)
        swAppNoiseSuppression.setChecked(AudioTransmitterService.appNoiseSuppressionEnabled);
        swAppNoiseSuppression.setOnCheckedChangeListener((bv, isChecked) -> {
            AudioTransmitterService.appNoiseSuppressionEnabled = isChecked;
            prefs.edit().putBoolean(AudioTransmitterService.KEY_NOISE_SUPPRESSION, isChecked).apply();
        });

        // AAudio
        swAAudio.setChecked(AudioTransmitterService.useAAudio);
        swAAudio.setOnCheckedChangeListener((bv, isChecked) -> {
            AudioTransmitterService.useAAudio = isChecked;
            prefs.edit().putBoolean(AudioTransmitterService.KEY_AAUDIO_ENABLED, isChecked).apply();
            notifyServiceRestart();
        });

        // Exclusive Mode
        swExclusive.setChecked(AudioTransmitterService.isExclusiveMode);
        swExclusive.setOnCheckedChangeListener((bv, isChecked) -> {
            AudioTransmitterService.isExclusiveMode = isChecked;
            prefs.edit().putBoolean(AudioTransmitterService.KEY_EXCLUSIVE_MODE, isChecked).apply();
            // Restart if currently using AAudio
            if (AudioTransmitterService.useAAudio) {
                notifyServiceRestart();
            }
        });
    }

    private void setupMicMode() {
        updateMicModeUI();
        android.content.SharedPreferences prefs = requireContext().getSharedPreferences(MainActivity.PREFS_NAME, Context.MODE_PRIVATE);
        
        cardMicMode.setOnClickListener(v -> {
            String[] modes = {"Voice communication", "General (Music/High Quality)"};
            int checkedItem = (AudioTransmitterService.micSource == MediaRecorder.AudioSource.VOICE_COMMUNICATION) ? 0 : 1;
            
            new MaterialAlertDialogBuilder(requireContext())
                    .setTitle("Select Microphone Mode")
                    .setSingleChoiceItems(modes, checkedItem, (dialog, which) -> {
                        int newSource = (which == 0) ? MediaRecorder.AudioSource.VOICE_COMMUNICATION : MediaRecorder.AudioSource.MIC;
                        if (newSource != AudioTransmitterService.micSource) {
                            AudioTransmitterService.micSource = newSource;
                            prefs.edit().putInt(AudioTransmitterService.KEY_MIC_SOURCE, newSource).apply();
                            updateMicModeUI();
                            notifyServiceRestart();
                            Toast.makeText(getContext(), "Restarting capture with new mode...", Toast.LENGTH_SHORT).show();
                        }
                        dialog.dismiss();
                    })
                    .show();
        });
    }

    private void updateMicModeUI() {
        if (AudioTransmitterService.micSource == MediaRecorder.AudioSource.VOICE_COMMUNICATION) {
            tvMicModeValue.setText("Voice communication");
        } else {
            tvMicModeValue.setText("General (Default Mic)");
        }
    }

    private void notifyServiceRestart() {
        AudioTransmitterService service = AudioTransmitterService.getInstance();
        if (service != null) {
            service.restartCapture();
        }
    }

    private void updateTimer() {
        if (AudioTransmitterService.startTimeMillis == 0) return;
        
        long millis = System.currentTimeMillis() - AudioTransmitterService.startTimeMillis;
        int seconds = (int) (millis / 1000);
        int hours = seconds / 3600;
        int minutes = (seconds % 3600) / 60;
        seconds = seconds % 60;
        
        if (hours > 0) {
            tvTimer.setText(String.format(Locale.getDefault(), "%02d:%02d:%02d", hours, minutes, seconds));
        } else {
            tvTimer.setText(String.format(Locale.getDefault(), "%02d:%02d", minutes, seconds));
        }
    }

    private void updateConnectionStatus() {
        if (AudioTransmitterService.currentUdpSender != null && AudioTransmitterService.currentUdpSender.isConnected()) {
            tvConnectionStatus.setText("Connected to PC");
            tvConnectionStatus.setTextColor(getResources().getColor(R.color.primary_green));
        } else {
            tvConnectionStatus.setText("Waiting for PC... (Reconnecting)");
            tvConnectionStatus.setTextColor(getResources().getColor(R.color.text_gray));
        }
    }

    private void stopTransmitterService() {
        AudioTransmitterService.isServiceRunning = false;
        Intent intent = new Intent(getContext(), AudioTransmitterService.class);
        if (getContext() != null) {
            getContext().stopService(intent);
        }

        if (getActivity() instanceof MainActivity) {
            ((MainActivity) getActivity()).onTransmitterStateChanged();
        }
    }

    private String getLocalIpAddress() {
        if (getContext() == null) return "Unknown";
        WifiManager wifiManager = (WifiManager) requireContext().getApplicationContext().getSystemService(Context.WIFI_SERVICE);
        if (wifiManager != null) {
            WifiInfo wifiInfo = wifiManager.getConnectionInfo();
            int ip = wifiInfo.getIpAddress();
            return String.format(Locale.getDefault(), "%d.%d.%d.%d",
                    (ip & 0xff), (ip >> 8 & 0xff), (ip >> 16 & 0xff), (ip >> 24 & 0xff));
        }
        return "Unknown";
    }

    @Override
    public void onDestroyView() {
        super.onDestroyView();
        handler.removeCallbacks(updateUIThread);
    }
}
