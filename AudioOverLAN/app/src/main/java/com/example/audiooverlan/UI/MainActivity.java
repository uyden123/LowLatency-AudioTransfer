package com.example.audiooverlan.UI;

import android.Manifest;
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

public class MainActivity extends AppCompatActivity {

    public static final String PREFS_NAME = "AudioOverLAN_Prefs";
    private ViewPager2 viewPager;
    private BottomNavigationView bottomNavigationView;
    private View navIndicator;
    private MainPagerAdapter adapter;

    private final ActivityResultLauncher<String> requestPermissionLauncher =
            registerForActivityResult(new ActivityResultContracts.RequestPermission(), isGranted -> {
                if (isGranted) {
                    // Try to start auto server if mic permission was just granted
                    startAutoServer();
                } else {
                    Toast.makeText(this, "Quyền truy cập là cần thiết để ứng dụng hoạt động", Toast.LENGTH_LONG).show();
                }
            });

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        EdgeToEdge.enable(this);
        setContentView(R.layout.activity_main);

        View appBarLayout = findViewById(R.id.appBarLayout);
        ViewCompat.setOnApplyWindowInsetsListener(appBarLayout, (v, insets) -> {
            Insets systemBars = insets.getInsets(WindowInsetsCompat.Type.systemBars());
            v.setPadding(0, systemBars.top, 0, 0);
            return insets;
        });

        viewPager = findViewById(R.id.viewPager);
        bottomNavigationView = findViewById(R.id.bottom_navigation);
        navIndicator = findViewById(R.id.nav_indicator);

        adapter = new MainPagerAdapter(this);
        viewPager.setAdapter(adapter);

        navIndicator.post(() -> {
            int width = bottomNavigationView.getWidth() / 3;
            ViewGroup.LayoutParams lp = navIndicator.getLayoutParams();
            lp.width = width;
            navIndicator.setLayoutParams(lp);
        });

        viewPager.registerOnPageChangeCallback(new ViewPager2.OnPageChangeCallback() {
            @Override
            public void onPageScrolled(int position, float positionOffset, int positionOffsetPixels) {
                int width = navIndicator.getWidth();
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
        if (adapter != null) {
            adapter.notifyItemChanged(1);
        }
    }

    private void askPermissions() {
        String[] permissions;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            permissions = new String[]{Manifest.permission.POST_NOTIFICATIONS, Manifest.permission.RECORD_AUDIO};
        } else {
            permissions = new String[]{Manifest.permission.RECORD_AUDIO};
        }

        for (String permission : permissions) {
            if (ContextCompat.checkSelfPermission(this, permission) != PackageManager.PERMISSION_GRANTED) {
                requestPermissionLauncher.launch(permission);
                break; // Launchers usually handle one at a time or we need multiple launchers. 
                       // For simplicity, we'll request if any are missing. 
                       // requestPermissionLauncher only takes one string.
            }
        }
    }
}
