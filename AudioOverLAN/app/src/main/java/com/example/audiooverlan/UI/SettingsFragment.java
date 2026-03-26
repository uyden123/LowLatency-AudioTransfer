package com.example.audiooverlan.UI;

import android.content.Context;
import android.os.Bundle;
import android.util.Log;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.fragment.app.Fragment;

import com.example.audiooverlan.R;
import com.example.audiooverlan.utils.SettingsRepository;
import com.google.android.material.switchmaterial.SwitchMaterial;

public class SettingsFragment extends Fragment {

    private static final String TAG = "SettingsFragment";

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
            
            SettingsRepository repo = SettingsRepository.getInstance(requireContext());

            // Handle App Theme
            SwitchMaterial switchTheme = view.findViewById(R.id.switchTheme);
            if (switchTheme != null) {
                switchTheme.setChecked(repo.isThemeDark());
                switchTheme.setOnCheckedChangeListener((buttonView, isChecked) -> {
                    repo.setThemeDark(isChecked);
                    android.widget.Toast.makeText(getContext(), "Theme will be applied on restart", android.widget.Toast.LENGTH_SHORT).show();
                });
            }

            // Handle Language
            View layoutLanguage = view.findViewById(R.id.layoutLanguage);
            android.widget.TextView tvLanguageValue = view.findViewById(R.id.tvLanguageValue);
            if (layoutLanguage != null && tvLanguageValue != null) {
                // Initialize current value
                String currentLang = repo.getLanguage();
                tvLanguageValue.setText(currentLang.equals("vi") ? "Tiếng Việt" : "English (Default)");

                layoutLanguage.setOnClickListener(v -> {
                    String[] options = {"English", "Tiếng Việt"};
                    new com.google.android.material.dialog.MaterialAlertDialogBuilder(requireContext())
                        .setTitle("Select Language")
                        .setItems(options, (dialog, which) -> {
                            String selectedLang = which == 0 ? "en" : "vi";
                            repo.setLanguage(selectedLang);
                            tvLanguageValue.setText(options[which]);
                            android.widget.Toast.makeText(getContext(), "Language set to " + options[which], android.widget.Toast.LENGTH_SHORT).show();
                        })
                        .show();
                });
            }

            // Handle Wake Lock
            SwitchMaterial switchWakeLock = view.findViewById(R.id.switchWakeLock);
            if (switchWakeLock != null) {
                switchWakeLock.setChecked(repo.isWakeLockEnabled());
                switchWakeLock.setOnCheckedChangeListener((buttonView, isChecked) -> {
                    repo.setWakeLockEnabled(isChecked);
                    android.widget.Toast.makeText(getContext(), isChecked ? "Wake lock enabled" : "Wake lock disabled", android.widget.Toast.LENGTH_SHORT).show();
                });
            }
            
        } catch (Exception e) {
            Log.e(TAG, "Error in SettingsFragment.onCreateView", e);
        }
        
        return view;
    }
}
