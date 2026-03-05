package com.example.audiooverlan.UI;

import android.content.Context;
import android.content.SharedPreferences;
import android.os.Bundle;
import android.util.Log;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.fragment.app.Fragment;

import com.example.audiooverlan.R;
import com.google.android.material.switchmaterial.SwitchMaterial;

public class SettingsFragment extends Fragment {

    private static final String TAG = "SettingsFragment";
    private static final String PREFS_NAME = "AudioOverLAN_Prefs";

    public SettingsFragment() {
        // Required empty public constructor
    }

    @Nullable
    @Override
    public View onCreateView(@NonNull LayoutInflater inflater, @Nullable ViewGroup container, @Nullable Bundle savedInstanceState) {
        Log.d(TAG, "onCreateView started");
        View view = null;
        try {
            view = inflater.inflate(R.layout.fragment_settings, container, false);
            
            SharedPreferences prefs = requireContext().getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
            SwitchMaterial switchAutoConnect = view.findViewById(R.id.switchAutoConnect);

            if (switchAutoConnect != null) {
                boolean autoConnectEnabled = prefs.getBoolean(PlayerFragment.KEY_AUTO_CONNECT, true);
                switchAutoConnect.setChecked(autoConnectEnabled);
                switchAutoConnect.setOnCheckedChangeListener((buttonView, isChecked) -> {
                    prefs.edit().putBoolean(PlayerFragment.KEY_AUTO_CONNECT, isChecked).apply();
                });
            } else {
                Log.e(TAG, "switchAutoConnect button not found in layout");
            }
            
        } catch (Exception e) {
            Log.e(TAG, "Error in SettingsFragment.onCreateView", e);
        }
        
        return view;
    }
}
