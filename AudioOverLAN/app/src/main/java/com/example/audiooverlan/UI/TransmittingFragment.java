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
import androidx.transition.TransitionManager;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

public class TransmittingFragment extends Fragment {

    private TextView tvHostAddress;
    private TextView tvHostPort;
    private TextView tvActiveSource;
    private ImageButton btnCopyAddress;
    private SwitchMaterial swVolumeBoost;
    private TextView tvBoostLevelValue;
    private Slider sliderVolumeLevel;
    private View rowNsOff, rowNsRNNoise, rowNsDeepFilter;
    private View indicatorNsOff, indicatorNsRNNoise, indicatorNsDeepFilter;
    private TextView tvNsStrengthValue;
    private Slider sliderNoiseLevel;
    private TextView tvActiveCount;
    private RecyclerView rvClients;
    private View vNoClients;
    private MaterialButton btnStopBroadcast;
    private View llNsStrength;
    private View llBoostControls;
    private ViewGroup llMainContent;

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
        tvActiveSource = view.findViewById(R.id.tvActiveSource);
        btnCopyAddress = view.findViewById(R.id.btnCopyAddress);
        swVolumeBoost = view.findViewById(R.id.swVolumeBoost);
        tvBoostLevelValue = view.findViewById(R.id.tvBoostLevelValue);
        sliderVolumeLevel = view.findViewById(R.id.sliderVolumeLevel);
        rowNsOff = view.findViewById(R.id.rowNsOff);
        rowNsRNNoise = view.findViewById(R.id.rowNsRNNoise);
        rowNsDeepFilter = view.findViewById(R.id.rowNsDeepFilter);
        indicatorNsOff = view.findViewById(R.id.indicatorNsOff);
        indicatorNsRNNoise = view.findViewById(R.id.indicatorNsRNNoise);
        indicatorNsDeepFilter = view.findViewById(R.id.indicatorNsDeepFilter);
        tvNsStrengthValue = view.findViewById(R.id.tvNsStrengthValue);
        sliderNoiseLevel = view.findViewById(R.id.sliderNoiseLevel);
        tvActiveCount = view.findViewById(R.id.tvActiveCount);
        rvClients = view.findViewById(R.id.rvClients);
        vNoClients = view.findViewById(R.id.vNoClients);
        btnStopBroadcast = view.findViewById(R.id.btnStopBroadcast);
        llNsStrength = view.findViewById(R.id.llNsStrength);
        llBoostControls = view.findViewById(R.id.llBoostControls);
        llMainContent = view.findViewById(R.id.llMainContent);

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
        tvHostPort.setVisibility(View.GONE);

        btnCopyAddress.setOnClickListener(v -> {
            ClipboardManager clipboard = (ClipboardManager) requireContext().getSystemService(Context.CLIPBOARD_SERVICE);
            ClipData clip = ClipData.newPlainText("Host IP", ip);
            clipboard.setPrimaryClip(clip);
            Toast.makeText(getContext(), "Address copied to clipboard", Toast.LENGTH_SHORT).show();
        });
    }

    private void setupControls() {
        SettingsRepository repo = SettingsRepository.getInstance(requireContext());

        // Volume Boost
        swVolumeBoost.setChecked(AudioTransmitterService.appVolumeBoostEnabled);
        llBoostControls.setVisibility(AudioTransmitterService.appVolumeBoostEnabled ? View.VISIBLE : View.GONE);
        
        swVolumeBoost.setOnCheckedChangeListener((v, isChecked) -> {
            TransitionManager.beginDelayedTransition(llMainContent);
            llBoostControls.setVisibility(isChecked ? View.VISIBLE : View.GONE);
            
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
        rowNsOff.setOnClickListener(v -> handleNsModeChange(0));
        rowNsRNNoise.setOnClickListener(v -> handleNsModeChange(1));
        rowNsDeepFilter.setOnClickListener(v -> handleNsModeChange(2));

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

    private void handleNsModeChange(int mode) {
        if (mode != AudioTransmitterService.appNsMode) {
            SettingsRepository repo = SettingsRepository.getInstance(requireContext());
            AudioTransmitterService.appNsMode = mode;
            repo.setTransNsMode(mode);
            
            AudioTransmitterService service = AudioTransmitterService.getInstance();
            if (service != null) {
                service.requestAudioSourceRestart();
            }
            updateNSToggleUI(mode);
        }
    }

    private void updateNSToggleUI(int mode) {
        TransitionManager.beginDelayedTransition(llMainContent);
        
        // Update selection indicators and highlights
        indicatorNsOff.setVisibility(mode == 0 ? View.VISIBLE : View.INVISIBLE);
        indicatorNsRNNoise.setVisibility(mode == 1 ? View.VISIBLE : View.INVISIBLE);
        indicatorNsDeepFilter.setVisibility(mode == 2 ? View.VISIBLE : View.INVISIBLE);

        rowNsOff.setAlpha(1.0f);
        rowNsRNNoise.setAlpha(1.0f);
        rowNsDeepFilter.setAlpha(1.0f);

        // State background highlights with dedicated drawables
        rowNsOff.setBackgroundResource(mode == 0 ? R.drawable.bg_ns_mode_selected : R.drawable.bg_ns_mode_unselected);
        rowNsRNNoise.setBackgroundResource(mode == 1 ? R.drawable.bg_ns_mode_selected : R.drawable.bg_ns_mode_unselected);
        rowNsDeepFilter.setBackgroundResource(mode == 2 ? R.drawable.bg_ns_mode_selected : R.drawable.bg_ns_mode_unselected);

        llNsStrength.setVisibility(mode == 2 ? View.VISIBLE : View.GONE);
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
                tvActiveSource.setText(t.activeSource.toUpperCase());
            } else {
                updateClientsUI(new ArrayList<>());
            }
        });
    }

    private void updateClientsUI(List<String> clients) {
        TransitionManager.beginDelayedTransition(llMainContent);
        
        if (clients.isEmpty()) {
            vNoClients.setVisibility(View.VISIBLE);
            rvClients.setVisibility(View.GONE);
            tvActiveCount.setText("0 ACTIVE");
        } else {
            vNoClients.setVisibility(View.GONE);
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
