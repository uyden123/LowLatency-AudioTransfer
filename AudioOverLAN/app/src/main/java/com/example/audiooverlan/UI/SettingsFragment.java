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
import com.google.android.material.card.MaterialCardView;
import androidx.core.content.ContextCompat;
import android.graphics.Color;
import androidx.appcompat.app.AppCompatDelegate;

public class SettingsFragment extends Fragment {

    private static final String TAG = "SettingsFragment";

    public SettingsFragment() {
        // Required empty public constructor
    }

    @Nullable
    @Override
    public View onCreateView(@NonNull LayoutInflater inflater, @Nullable ViewGroup container, @Nullable Bundle savedInstanceState) {
        Log.d(TAG, "onCreateView started");
        try {
            final View view = inflater.inflate(R.layout.fragment_settings, container, false);
            
            SettingsRepository repo = SettingsRepository.getInstance(requireContext());

            // Handle App Theme
            com.google.android.material.card.MaterialCardView cardAuto = view.findViewById(R.id.cardThemeAuto);
            com.google.android.material.card.MaterialCardView cardLight = view.findViewById(R.id.cardThemeLight);
            com.google.android.material.card.MaterialCardView cardDark = view.findViewById(R.id.cardThemeDark);

            if (cardAuto != null && cardLight != null && cardDark != null) {
                int currentMode = repo.getThemeMode();
                updateThemeUI(currentMode, cardAuto, cardLight, cardDark, view);

                cardAuto.setOnClickListener(v -> {
                    if (repo.getThemeMode() == 0) return;
                    repo.setThemeMode(0);
                    updateThemeUI(0, cardAuto, cardLight, cardDark, view);
                    applyTheme(0);
                });

                cardLight.setOnClickListener(v -> {
                    if (repo.getThemeMode() == 1) return;
                    repo.setThemeMode(1);
                    updateThemeUI(1, cardAuto, cardLight, cardDark, view);
                    applyTheme(1);
                });

                cardDark.setOnClickListener(v -> {
                    if (repo.getThemeMode() == 2) return;
                    repo.setThemeMode(2);
                    updateThemeUI(2, cardAuto, cardLight, cardDark, view);
                    applyTheme(2);
                });
            }

            // Handle Language
            com.google.android.material.card.MaterialCardView cardLangEn = view.findViewById(R.id.cardLangEn);
            com.google.android.material.card.MaterialCardView cardLangVi = view.findViewById(R.id.cardLangVi);

            if (cardLangEn != null && cardLangVi != null) {
                String currentLang = repo.getLanguage();
                updateLanguageUI(currentLang, cardLangEn, cardLangVi, view);

                cardLangEn.setOnClickListener(v -> {
                    if (repo.getLanguage().equals("en")) return;
                    repo.setLanguage("en");
                    updateLanguageUI("en", cardLangEn, cardLangVi, view);
                    triggerLanguageRecreation();
                });

                cardLangVi.setOnClickListener(v -> {
                    if (repo.getLanguage().equals("vi")) return;
                    repo.setLanguage("vi");
                    updateLanguageUI("vi", cardLangEn, cardLangVi, view);
                    triggerLanguageRecreation();
                });
            }
            
            return view;
            
        } catch (Exception e) {
            Log.e(TAG, "Error in SettingsFragment.onCreateView", e);
        }
        
        return null;
    }

    private void updateThemeUI(int mode, MaterialCardView cardAuto, MaterialCardView cardLight, MaterialCardView cardDark, View rootView) {
        if (getContext() == null || rootView == null) return;
        
        int colorSelected = ContextCompat.getColor(requireContext(), R.color.card_background_selected);
        int colorUnselected = ContextCompat.getColor(requireContext(), R.color.card_background);
        int strokeSelected = ContextCompat.getColor(requireContext(), R.color.primary_blue);
        int strokeUnselected = ContextCompat.getColor(requireContext(), R.color.divider);
        int textSelected = ContextCompat.getColor(requireContext(), R.color.primary_blue);
        int textUnselected = ContextCompat.getColor(requireContext(), R.color.white);

        // Update card backgrounds and strokes
        cardAuto.setCardBackgroundColor(mode == 0 ? colorSelected : colorUnselected);
        cardAuto.setStrokeColor(mode == 0 ? strokeSelected : strokeUnselected);
        cardLight.setCardBackgroundColor(mode == 1 ? colorSelected : colorUnselected);
        cardLight.setStrokeColor(mode == 1 ? strokeSelected : strokeUnselected);
        cardDark.setCardBackgroundColor(mode == 2 ? colorSelected : colorUnselected);
        cardDark.setStrokeColor(mode == 2 ? strokeSelected : strokeUnselected);

        // Update icons and text tints
        updateSelectionState(rootView.findViewById(R.id.ivThemeAuto), rootView.findViewById(R.id.tvThemeAuto), mode == 0, textSelected, textUnselected);
        updateSelectionState(rootView.findViewById(R.id.ivThemeLight), rootView.findViewById(R.id.tvThemeLight), mode == 1, textSelected, textUnselected);
        updateSelectionState(rootView.findViewById(R.id.ivThemeDark), rootView.findViewById(R.id.tvThemeDark), mode == 2, textSelected, textUnselected);
    }

    private void updateLanguageUI(String lang, MaterialCardView cardEn, MaterialCardView cardVi, View rootView) {
        if (getContext() == null || rootView == null) return;

        int colorSelected = ContextCompat.getColor(requireContext(), R.color.card_background_selected);
        int colorUnselected = ContextCompat.getColor(requireContext(), R.color.card_background);
        int strokeSelected = ContextCompat.getColor(requireContext(), R.color.primary_blue);
        int strokeUnselected = ContextCompat.getColor(requireContext(), R.color.divider);
        int textSelected = ContextCompat.getColor(requireContext(), R.color.primary_blue);
        int textUnselected = ContextCompat.getColor(requireContext(), R.color.white);

        boolean isVi = "vi".equals(lang);

        cardEn.setCardBackgroundColor(!isVi ? colorSelected : colorUnselected);
        cardEn.setStrokeColor(!isVi ? strokeSelected : strokeUnselected);
        cardVi.setCardBackgroundColor(isVi ? colorSelected : colorUnselected);
        cardVi.setStrokeColor(isVi ? strokeSelected : strokeUnselected);

        updateSelectionState(null, rootView.findViewById(R.id.tvLangEn), !isVi, textSelected, textUnselected);
        updateSelectionState(null, rootView.findViewById(R.id.tvLangVi), isVi, textSelected, textUnselected);
    }

    private void triggerLanguageRecreation() {
        if (getActivity() != null) {
            androidx.viewpager2.widget.ViewPager2 vp = getActivity().findViewById(R.id.viewPager);
            if (vp != null) {
                getActivity().getSharedPreferences("AudioOverLAN_Prefs", android.content.Context.MODE_PRIVATE)
                    .edit().putInt("current_tab", vp.getCurrentItem()).commit();
            }
            getActivity().recreate();
        }
    }


    private void updateSelectionState(android.widget.ImageView iv, android.widget.TextView tv, boolean isSelected, int selectedColor, int unselectedColor) {
        int color = isSelected ? selectedColor : unselectedColor;
        if (iv != null) iv.setColorFilter(color);
        if (tv != null) tv.setTextColor(color);
    }

    private void applyTheme(int mode) {
        if (getContext() == null || getActivity() == null) return;
        
        // Save current tab BEFORE recreation — commit() is synchronous
        androidx.viewpager2.widget.ViewPager2 vp = getActivity().findViewById(R.id.viewPager);
        if (vp != null) {
            getActivity().getSharedPreferences("AudioOverLAN_Prefs", android.content.Context.MODE_PRIVATE)
                .edit().putInt("current_tab", vp.getCurrentItem()).commit();
        }
        
        int nightMode;
        switch (mode) {
            case 1: nightMode = AppCompatDelegate.MODE_NIGHT_NO; break;
            case 2: nightMode = AppCompatDelegate.MODE_NIGHT_YES; break;
            default: nightMode = AppCompatDelegate.MODE_NIGHT_FOLLOW_SYSTEM; break;
        }
        
        // setDefaultNightMode will automatically recreate the activity
        AppCompatDelegate.setDefaultNightMode(nightMode);
    }


}
