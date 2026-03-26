package com.example.audiooverlan.UI;

import android.os.Bundle;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.fragment.app.Fragment;
import androidx.lifecycle.ViewModelProvider;

import com.example.audiooverlan.R;
import com.example.audiooverlan.viewmodels.TransmitterState;
import com.example.audiooverlan.viewmodels.TransmitterViewModel;

public class ServerHostFragment extends Fragment {

    private TransmitterViewModel viewModel;
    private boolean lastIsRunning = false;

    @Nullable
    @Override
    public View onCreateView(@NonNull LayoutInflater inflater, @Nullable ViewGroup container, @Nullable Bundle savedInstanceState) {
        return inflater.inflate(R.layout.fragment_host_container, container, false);
    }

    @Override
    public void onViewCreated(@NonNull View view, @Nullable Bundle savedInstanceState) {
        super.onViewCreated(view, savedInstanceState);
        viewModel = new ViewModelProvider(this).get(TransmitterViewModel.class);
        
        viewModel.getTransmitterState().observe(getViewLifecycleOwner(), state -> {
            boolean running = !(state instanceof TransmitterState.Idle);
            
            if (getChildFragmentManager().findFragmentById(R.id.container) == null || running != lastIsRunning) {
                lastIsRunning = running;
                
                Fragment fragment = running ? new TransmittingFragment() : new ServerFragment();
                
                getChildFragmentManager().beginTransaction()
                        .setCustomAnimations(android.R.anim.fade_in, android.R.anim.fade_out)
                        .replace(R.id.container, fragment)
                        .commit();
            }
        });
    }
}
