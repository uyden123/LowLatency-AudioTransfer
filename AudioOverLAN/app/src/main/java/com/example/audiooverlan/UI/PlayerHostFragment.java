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
import com.example.audiooverlan.viewmodels.PlayerState;
import com.example.audiooverlan.viewmodels.PlayerViewModel;

public class PlayerHostFragment extends Fragment {

    private PlayerViewModel viewModel;
    private boolean lastIsConnected = false;
    private String lastIp = null;

    @Nullable
    @Override
    public View onCreateView(@NonNull LayoutInflater inflater, @Nullable ViewGroup container, @Nullable Bundle savedInstanceState) {
        return inflater.inflate(R.layout.fragment_host_container, container, false);
    }

    @Override
    public void onViewCreated(@NonNull View view, @Nullable Bundle savedInstanceState) {
        super.onViewCreated(view, savedInstanceState);
        viewModel = new ViewModelProvider(this).get(PlayerViewModel.class);
        
        viewModel.getPlayerState().observe(getViewLifecycleOwner(), state -> {
            // Reconnecting is considered "connected UI" state
            boolean connected = (state instanceof PlayerState.Playing || state instanceof PlayerState.Reconnecting);
            String ip = (state instanceof PlayerState.Playing) ? ((PlayerState.Playing) state).ip : null;
            
            if (getChildFragmentManager().findFragmentById(R.id.container) == null || connected != lastIsConnected) {
                lastIsConnected = connected;
                
                Fragment fragment = connected ? new ConnectedFragment() : new PlayerFragment();
                if (ip != null) {
                    Bundle args = new Bundle();
                    args.putString("IP_ADDRESS", ip);
                    fragment.setArguments(args);
                }
                
                getChildFragmentManager().beginTransaction()
                        .setCustomAnimations(android.R.anim.fade_in, android.R.anim.fade_out)
                        .replace(R.id.container, fragment)
                        .commit();
            }
        });
    }
}
