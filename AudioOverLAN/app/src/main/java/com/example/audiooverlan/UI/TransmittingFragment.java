package com.example.audiooverlan.UI;

import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.net.wifi.WifiInfo;
import android.net.wifi.WifiManager;
import android.os.Bundle;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ImageButton;
import android.widget.TextView;
import android.widget.Toast;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.core.content.ContextCompat;
import androidx.fragment.app.Fragment;
import androidx.lifecycle.ViewModelProvider;
import androidx.recyclerview.widget.LinearLayoutManager;
import androidx.recyclerview.widget.RecyclerView;

import com.example.audiooverlan.R;
import com.example.audiooverlan.services.AudioTransmitterService;
import com.example.audiooverlan.utils.SettingsRepository;
import com.example.audiooverlan.viewmodels.TransmitterState;
import com.example.audiooverlan.viewmodels.TransmitterViewModel;
import com.google.android.material.button.MaterialButton;
import com.google.android.material.button.MaterialButtonToggleGroup;
import com.google.android.material.slider.Slider;
import com.google.android.material.switchmaterial.SwitchMaterial;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

public class TransmittingFragment extends Fragment {

    private TextView tvHostAddress;
    private TextView tvHostPort;
    private ImageButton btnCopyAddress;
    private SwitchMaterial swVolumeBoost;
    private TextView tvBoostLevelValue;
    private Slider sliderVolumeLevel;
    private MaterialButtonToggleGroup toggleNS;
    private TextView tvNsStrengthValue;
    private Slider sliderNoiseLevel;
    private TextView tvActiveCount;
    private RecyclerView rvClients;
    private TextView tvNoClients;
    private MaterialButton btnStopBroadcast;

    private ClientAdapter clientAdapter;
    private TransmitterViewModel viewModel;

    @Nullable
    @Override
    public View onCreateView(@NonNull LayoutInflater inflater, @Nullable ViewGroup container, @Nullable Bundle savedInstanceState) {
        View view = inflater.inflate(R.layout.fragment_transmitting, container, false);

        viewModel = new ViewModelProvider(this).get(TransmitterViewModel.class);

        // Bindings
        tvHostAddress = view.findViewById(R.id.tvHostAddress);
        tvHostPort = view.findViewById(R.id.tvHostPort);
        btnCopyAddress = view.findViewById(R.id.btnCopyAddress);
        swVolumeBoost = view.findViewById(R.id.swVolumeBoost);
        tvBoostLevelValue = view.findViewById(R.id.tvBoostLevelValue);
        sliderVolumeLevel = view.findViewById(R.id.sliderVolumeLevel);
        toggleNS = view.findViewById(R.id.toggleNS);
        tvNsStrengthValue = view.findViewById(R.id.tvNsStrengthValue);
        sliderNoiseLevel = view.findViewById(R.id.sliderNoiseLevel);
        tvActiveCount = view.findViewById(R.id.tvActiveCount);
        rvClients = view.findViewById(R.id.rvClients);
        tvNoClients = view.findViewById(R.id.tvNoClients);
        btnStopBroadcast = view.findViewById(R.id.btnStopBroadcast);

        // Setup
        setupHostInfo();
        setupControls();
        setupClientList();
        observeState();

        return view;
    }

    private void setupHostInfo() {
        String ip = getLocalIpAddress();
        tvHostAddress.setText(ip);
        tvHostPort.setText(":5003"); // Default port

        btnCopyAddress.setOnClickListener(v -> {
            ClipboardManager clipboard = (ClipboardManager) requireContext().getSystemService(Context.CLIPBOARD_SERVICE);
            ClipData clip = ClipData.newPlainText("Host IP", ip + ":5003");
            clipboard.setPrimaryClip(clip);
            Toast.makeText(getContext(), "Address copied to clipboard", Toast.LENGTH_SHORT).show();
        });
    }

    private void setupControls() {
        SettingsRepository repo = SettingsRepository.getInstance(requireContext());

        // Volume Boost
        swVolumeBoost.setChecked(AudioTransmitterService.appVolumeBoostEnabled);
        swVolumeBoost.setOnCheckedChangeListener((v, isChecked) -> {
            AudioTransmitterService.appVolumeBoostEnabled = isChecked;
            float gain = isChecked ? AudioTransmitterService.appVolumeBoostLevel : 1.0f;
            AudioTransmitterService service = AudioTransmitterService.getInstance();
            if (service != null) service.updateVolumeGain(gain);
            else AudioTransmitterService.volumeGain = gain;
            repo.setTransVolumeBoost(isChecked);
        });

        sliderVolumeLevel.setValue(Math.min(Math.max(AudioTransmitterService.appVolumeBoostLevel, 1.1f), 5.0f));
        tvBoostLevelValue.setText(String.format(Locale.getDefault(), "%.1fx", AudioTransmitterService.appVolumeBoostLevel));
        sliderVolumeLevel.addOnChangeListener((slider, value, fromUser) -> {
            tvBoostLevelValue.setText(String.format(Locale.getDefault(), "%.1fx", value));
            if (fromUser) {
                AudioTransmitterService.appVolumeBoostLevel = value;
                repo.setTransVolumeBoostLevel(value);
                
                if (AudioTransmitterService.appVolumeBoostEnabled) {
                    AudioTransmitterService service = AudioTransmitterService.getInstance();
                    if (service != null) service.updateVolumeGain(value);
                    else AudioTransmitterService.volumeGain = value;
                }
            }
        });

        // Noise Suppression
        updateNSToggleUI(AudioTransmitterService.appNsMode);
        toggleNS.addOnButtonCheckedListener((group, checkedId, isChecked) -> {
            if (isChecked) {
                int mode = 0;
                if (checkedId == R.id.btnNsRNNoise) mode = 1;
                else if (checkedId == R.id.btnNsDeepFilter) mode = 2;
                
                if (mode != AudioTransmitterService.appNsMode) {
                    AudioTransmitterService.appNsMode = mode;
                    repo.setTransNsMode(mode);
                }
            }
        });

        sliderNoiseLevel.setValue(AudioTransmitterService.appNoiseSuppressionLevel);
        tvNsStrengthValue.setText(String.format(Locale.getDefault(), "%.0fdB", AudioTransmitterService.appNoiseSuppressionLevel));
        sliderNoiseLevel.addOnChangeListener((slider, value, fromUser) -> {
            tvNsStrengthValue.setText(String.format(Locale.getDefault(), "%.0fdB", value));
            if (fromUser) {
                repo.setTransNsLevel(value);
                AudioTransmitterService service = AudioTransmitterService.getInstance();
                if (service != null) {
                    service.updateNoiseSuppressionLevel(value);
                } else {
                    AudioTransmitterService.appNoiseSuppressionLevel = value;
                }
            }
        });

        btnStopBroadcast.setOnClickListener(v -> stopTransmitterService());
    }

    private void updateNSToggleUI(int mode) {
        if (mode == 0) toggleNS.check(R.id.btnNsOff);
        else if (mode == 1) toggleNS.check(R.id.btnNsRNNoise);
        else if (mode == 2) toggleNS.check(R.id.btnNsDeepFilter);
    }

    private void setupClientList() {
        rvClients.setLayoutManager(new LinearLayoutManager(getContext()));
        clientAdapter = new ClientAdapter();
        rvClients.setAdapter(clientAdapter);
    }

    private void observeState() {
        viewModel.getTransmitterState().observe(getViewLifecycleOwner(), state -> {
            if (state instanceof TransmitterState.Transmitting) {
                TransmitterState.Transmitting t = (TransmitterState.Transmitting) state;
                List<String> clients = new ArrayList<>();
                if (t.isConnected) {
                    clients.add(t.targetIp);
                }
                updateClientsUI(clients);
            } else {
                updateClientsUI(new ArrayList<>());
            }
        });
    }

    private void updateClientsUI(List<String> clients) {
        if (clients.isEmpty()) {
            tvNoClients.setVisibility(View.VISIBLE);
            rvClients.setVisibility(View.GONE);
            tvActiveCount.setText("0 ACTIVE");
        } else {
            tvNoClients.setVisibility(View.GONE);
            rvClients.setVisibility(View.VISIBLE);
            tvActiveCount.setText(String.format(Locale.getDefault(), "%d ACTIVE", clients.size()));
            clientAdapter.setClients(clients);
        }
    }

    private void stopTransmitterService() {
        AudioTransmitterService.isServiceRunning = false;
        Intent intent = new Intent(getContext(), AudioTransmitterService.class);
        if (getContext() != null) getContext().stopService(intent);
        if (getActivity() instanceof MainActivity) ((MainActivity) getActivity()).onTransmitterStateChanged();
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

    private static class ClientAdapter extends RecyclerView.Adapter<ClientAdapter.ViewHolder> {
        private List<String> clients = new ArrayList<>();

        public void setClients(List<String> clients) {
            this.clients = clients;
            notifyDataSetChanged();
        }

        @NonNull
        @Override
        public ViewHolder onCreateViewHolder(@NonNull ViewGroup parent, int viewType) {
            View view = LayoutInflater.from(parent.getContext()).inflate(R.layout.item_connected_client, parent, false);
            return new ViewHolder(view);
        }

        @Override
        public void onBindViewHolder(@NonNull ViewHolder holder, int position) {
            String ip = clients.get(position);
            holder.tvIp.setText(ip);
            holder.tvPing.setText("Connected"); // Mock ping for now
        }

        @Override
        public int getItemCount() { return clients.size(); }

        static class ViewHolder extends RecyclerView.ViewHolder {
            TextView tvIp, tvPing;
            ViewHolder(View v) {
                super(v);
                tvIp = v.findViewById(R.id.tvClientIp);
                tvPing = v.findViewById(R.id.tvClientPing);
            }
        }
    }
}
