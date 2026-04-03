package com.example.audiooverlan.UI;

import android.Manifest;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.net.wifi.WifiInfo;
import android.net.wifi.WifiManager;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ImageButton;
import android.widget.TextView;
import android.widget.Toast;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.core.app.ActivityCompat;
import androidx.core.content.ContextCompat;
import androidx.fragment.app.Fragment;

import com.example.audiooverlan.services.AudioTransmitterService;
import com.example.audiooverlan.R;
import com.google.android.material.card.MaterialCardView;

import java.util.Locale;

public class ServerFragment extends Fragment {

    private static final int REQ_RECORD_AUDIO = 1001;

    private TextView tvMyServerIP;
    private ImageButton btnCopyAddress;

    private final Handler handler = new Handler(Looper.getMainLooper());

    @Nullable
    @Override
    public View onCreateView(@NonNull LayoutInflater inflater, @Nullable ViewGroup container, @Nullable Bundle savedInstanceState) {
        View view = inflater.inflate(R.layout.fragment_server, container, false);

        tvMyServerIP = view.findViewById(R.id.tvMyServerIP);
        btnCopyAddress = view.findViewById(R.id.btnCopyAddressSv);
        MaterialCardView cardMicrophone = view.findViewById(R.id.cardMicrophone);
        MaterialCardView cardApps = view.findViewById(R.id.cardApps);
        String ip = getLocalIpAddress();

        tvMyServerIP.setText(ip);

        cardMicrophone.setOnClickListener(v -> {
            if (AudioTransmitterService.isServiceRunning) {
                stopTransmitterService();
            } else {
                if (checkPermission()) {
                    startTransmitterService("Microphone");
                } else {
                    requestPermission();
                }
            }
        });

        cardApps.setOnClickListener(v -> {
            if (AudioTransmitterService.isServiceRunning) {
                stopTransmitterService();
            } else {
                if (android.os.Build.VERSION.SDK_INT < android.os.Build.VERSION_CODES.Q) {
                    Toast.makeText(getContext(), "Streaming apps requires Android 10+", Toast.LENGTH_LONG).show();
                } else {
                    startAppsStreaming();
                }
            }
        });

        btnCopyAddress.setOnClickListener(v -> {
            ClipboardManager clipboard = (ClipboardManager) requireContext().getSystemService(Context.CLIPBOARD_SERVICE);
            ClipData clip = ClipData.newPlainText("Host IP", ip);
            clipboard.setPrimaryClip(clip);
            Toast.makeText(getContext(), "Address copied to clipboard", Toast.LENGTH_SHORT).show();
        });

        return view;
    }

    private void startAppsStreaming() {
        if (getActivity() instanceof MainActivity) {
            ((MainActivity) getActivity()).requestAppCapture();
        }
    }

    private boolean checkPermission() {
        return ContextCompat.checkSelfPermission(requireContext(), Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED;
    }

    private void requestPermission() {
        ActivityCompat.requestPermissions(requireActivity(), new String[]{Manifest.permission.RECORD_AUDIO}, REQ_RECORD_AUDIO);
    }

    private void startTransmitterService(String source) {
        AudioTransmitterService.isServiceRunning = true;
        Intent intent = new Intent(getContext(), AudioTransmitterService.class);
        intent.putExtra("SOURCE", source);
        intent.putExtra("PORT", 5003);
        ContextCompat.startForegroundService(requireContext(), intent);

        if (getActivity() instanceof MainActivity) {
            ((MainActivity) getActivity()).onTransmitterStateChanged();
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
        WifiManager wifiManager = (WifiManager) getContext().getApplicationContext().getSystemService(Context.WIFI_SERVICE);
        if (wifiManager != null) {
            WifiInfo wifiInfo = wifiManager.getConnectionInfo();
            int ip = wifiInfo.getIpAddress();
            return String.format(Locale.getDefault(), "%d.%d.%d.%d",
                    (ip & 0xff), (ip >> 8 & 0xff), (ip >> 16 & 0xff), (ip >> 24 & 0xff));
        }
        return "Unknown";
    }

    @Override
    public void onResume() {
        super.onResume();
        tvMyServerIP.setText(getLocalIpAddress());
    }

    @Override
    public void onPause() {
        super.onPause();
    }
}
