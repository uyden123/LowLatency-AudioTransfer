package com.example.audiooverlan.UI;

import android.Manifest;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Toast;

import androidx.activity.EdgeToEdge;
import androidx.activity.result.ActivityResultLauncher;
import androidx.activity.result.contract.ActivityResultContracts;
import androidx.appcompat.app.AppCompatActivity;
import androidx.core.content.ContextCompat;
import androidx.core.graphics.Insets;
import androidx.core.view.ViewCompat;
import androidx.core.view.WindowInsetsCompat;
import androidx.viewpager2.widget.ViewPager2;

import com.example.audiooverlan.R;
import com.google.android.material.bottomnavigation.BottomNavigationView;
import java.util.ArrayList;
import java.util.List;
import androidx.annotation.NonNull;

public class MainActivity extends AppCompatActivity {

    @Override
    protected void attachBaseContext(android.content.Context newBase) {
        com.example.audiooverlan.utils.SettingsRepository repo = com.example.audiooverlan.utils.SettingsRepository.getInstance(newBase);
        String lang = repo.getLanguage();
        java.util.Locale locale = new java.util.Locale(lang);
        java.util.Locale.setDefault(locale);
        android.content.res.Configuration config = new android.content.res.Configuration(newBase.getResources().getConfiguration());
        config.setLocale(locale);
        super.attachBaseContext(newBase.createConfigurationContext(config));
    }

    public static final String PREFS_NAME = "AudioOverLAN_Prefs";
    private ViewPager2 viewPager;
    private BottomNavigationView bottomNavigationView;
    private View navIndicator;
    private MainPagerAdapter adapter;

    private final ActivityResultLauncher<Intent> mediaProjectionLauncher =
            registerForActivityResult(new ActivityResultContracts.StartActivityForResult(), result -> {
                if (result.getResultCode() == android.app.Activity.RESULT_OK && result.getData() != null) {
                    startAppsStreamingWithToken(result.getData());
                } else {
                    Toast.makeText(this, getString(R.string.permission_denied_capture), Toast.LENGTH_SHORT).show();
                }
            });

    private final ActivityResultLauncher<String[]> requestPermissionsLauncher =
            registerForActivityResult(new ActivityResultContracts.RequestMultiplePermissions(), result -> {
                boolean granted = true;
                for (Boolean b : result.values()) {
                    if (b != null && !b) granted = false;
                }
                if (granted) {
                    startAutoServer();
                } else {
                    Toast.makeText(this, getString(R.string.permission_required), Toast.LENGTH_LONG).show();
                }
            });

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        // Apply theme settings
        com.example.audiooverlan.utils.SettingsRepository repo = com.example.audiooverlan.utils.SettingsRepository.getInstance(this);
        int themeMode = repo.getThemeMode();
        int nightMode;
        switch (themeMode) {
            case 1: nightMode = androidx.appcompat.app.AppCompatDelegate.MODE_NIGHT_NO; break;
            case 2: nightMode = androidx.appcompat.app.AppCompatDelegate.MODE_NIGHT_YES; break;
            default: nightMode = androidx.appcompat.app.AppCompatDelegate.MODE_NIGHT_FOLLOW_SYSTEM; break;
        }
        androidx.appcompat.app.AppCompatDelegate.setDefaultNightMode(nightMode);
        
        // Log or Toast current mode
        String modeDesc = themeMode == 0 ? "System" : (themeMode == 1 ? "Light" : "Dark");
        // android.widget.Toast.makeText(this, "Theme: " + modeDesc, android.widget.Toast.LENGTH_SHORT).show();

        super.onCreate(savedInstanceState);
        EdgeToEdge.enable(this);
        setContentView(R.layout.activity_main);

        View mainContent = findViewById(R.id.main_content);
        ViewCompat.setOnApplyWindowInsetsListener(mainContent, (v, insets) -> {
            Insets systemBars = insets.getInsets(WindowInsetsCompat.Type.systemBars());
            v.setPadding(0, systemBars.top, 0, 0);
            return insets;
        });

        viewPager = findViewById(R.id.viewPager);
        bottomNavigationView = findViewById(R.id.bottom_navigation);
        navIndicator = findViewById(R.id.nav_indicator);

        // Read saved tab FIRST (set by SettingsFragment before theme change)
        final int savedTab = getSharedPreferences(PREFS_NAME, MODE_PRIVATE).getInt("current_tab", -1);
        if (savedTab >= 0) {
            getSharedPreferences(PREFS_NAME, MODE_PRIVATE).edit().remove("current_tab").apply();
        }

        adapter = new MainPagerAdapter(this);
        viewPager.setAdapter(adapter);
        viewPager.setOffscreenPageLimit(3);

        // Set initial page BEFORE registering any callbacks
        // This way onPageScrolled will never fire for page 0
        if (savedTab >= 0) {
            viewPager.setCurrentItem(savedTab, false);
        }

        navIndicator.post(() -> {
            int width = bottomNavigationView.getWidth() / 3;
            ViewGroup.LayoutParams lp = navIndicator.getLayoutParams();
            lp.width = width;
            navIndicator.setLayoutParams(lp);
            // Also set indicator position immediately for restored tab
            int currentPage = viewPager.getCurrentItem();
            navIndicator.setTranslationX(currentPage * width);
        });

        viewPager.registerOnPageChangeCallback(new ViewPager2.OnPageChangeCallback() {
            @Override
            public void onPageScrolled(int position, float positionOffset, int positionOffsetPixels) {
                int width = navIndicator.getWidth();
                if (width == 0 && bottomNavigationView != null && bottomNavigationView.getWidth() > 0) {
                    width = bottomNavigationView.getWidth() / 3;
                }
                navIndicator.setTranslationX((position + positionOffset) * width);
            }

            @Override
            public void onPageSelected(int position) {
                switch (position) {
                    case 0:
                        bottomNavigationView.getMenu().findItem(R.id.nav_player).setChecked(true);
                        break;
                    case 1:
                        bottomNavigationView.getMenu().findItem(R.id.nav_server).setChecked(true);
                        break;
                    case 2:
                        bottomNavigationView.getMenu().findItem(R.id.nav_settings).setChecked(true);
                        break;
                }
            }
        });

        bottomNavigationView.setOnItemSelectedListener(item -> {
            int itemId = item.getItemId();
            if (itemId == R.id.nav_player) {
                viewPager.setCurrentItem(0);
                return true;
            } else if (itemId == R.id.nav_server) {
                viewPager.setCurrentItem(1);
                return true;
            } else if (itemId == R.id.nav_settings) {
                viewPager.setCurrentItem(2);
                return true;
            }
            return false;
        });

        // Sync bottom nav checked state (without triggering listener)
        if (savedTab >= 0) {
            int menuId;
            if (savedTab == 1) menuId = R.id.nav_server;
            else if (savedTab == 2) menuId = R.id.nav_settings;
            else menuId = R.id.nav_player;
            bottomNavigationView.getMenu().findItem(menuId).setChecked(true);
        }

        askPermissions();
        startAutoServer();
    }

    private void startAutoServer() {
        if (!com.example.audiooverlan.services.AudioTransmitterService.isServiceRunning) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) {
                android.content.Intent intent = new android.content.Intent(this, com.example.audiooverlan.services.AudioTransmitterService.class);
                intent.putExtra("SOURCE", "Microphone");
                intent.putExtra("PORT", 5003);
                ContextCompat.startForegroundService(this, intent);
                com.example.audiooverlan.services.AudioTransmitterService.isServiceRunning = true;
            }
        }
    }

    public void onConnectionStateChanged(String ip) {
        if (adapter != null) {
            adapter.setConnectedIp(ip);
        }
    }

    public void onTransmitterStateChanged() {
        // Now handled by ServerHostFragment observing TransmitterViewModel
    }

    private void askPermissions() {
        String[] permissions;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            permissions = new String[]{Manifest.permission.POST_NOTIFICATIONS, Manifest.permission.RECORD_AUDIO};
        } else {
            permissions = new String[]{Manifest.permission.RECORD_AUDIO};
        }

        List<String> missing = new ArrayList<>();
        for (String p : permissions) {
            if (ContextCompat.checkSelfPermission(this, p) != PackageManager.PERMISSION_GRANTED) {
                missing.add(p);
            }
        }
        
        if (!missing.isEmpty()) {
            requestPermissionsLauncher.launch(missing.toArray(new String[0]));
        }
    }

    public void requestAppCapture() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            android.media.projection.MediaProjectionManager mpm =
                    (android.media.projection.MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE);
            mediaProjectionLauncher.launch(mpm.createScreenCaptureIntent());
        }
    }

    private void startAppsStreamingWithToken(Intent data) {
        Intent intent = new Intent(this, com.example.audiooverlan.services.AudioTransmitterService.class);
        intent.putExtra("SOURCE", "Apps");
        intent.putExtra("PROJECTION_DATA", data);
        intent.putExtra("PORT", 5003);
        ContextCompat.startForegroundService(this, intent);
        com.example.audiooverlan.services.AudioTransmitterService.isServiceRunning = true;
        onTransmitterStateChanged();
    }
}
