package com.example.audiooverlan.UI;

import android.os.Bundle;

import androidx.annotation.NonNull;
import androidx.fragment.app.Fragment;
import androidx.fragment.app.FragmentActivity;
import androidx.viewpager2.adapter.FragmentStateAdapter;

import com.example.audiooverlan.services.AudioService;
import com.example.audiooverlan.services.AudioTransmitterService;

public class MainPagerAdapter extends FragmentStateAdapter {

    private String connectedIp = null;

    public MainPagerAdapter(@NonNull FragmentActivity fragmentActivity) {
        super(fragmentActivity);
    }

    public void setConnectedIp(String ip) {
        this.connectedIp = ip;
        notifyItemChanged(0);
    }

    @NonNull
    @Override
    public Fragment createFragment(int position) {
        switch (position) {
            case 0:
                if (AudioService.isServiceRunning) {
                    ConnectedFragment fragment = new ConnectedFragment();
                    if (connectedIp != null) {
                        Bundle args = new Bundle();
                        args.putString("IP_ADDRESS", connectedIp);
                        fragment.setArguments(args);
                    }
                    return fragment;
                } else {
                    return new PlayerFragment();
                }
            case 1:
                if (AudioTransmitterService.isServiceRunning) {
                    return new TransmittingFragment();
                } else {
                    return new ServerFragment();
                }
            case 2:
                return new SettingsFragment();
            default:
                return new PlayerFragment();
        }
    }

    @Override
    public int getItemCount() {
        return 3;
    }

    @Override
    public long getItemId(int position) {
        if (position == 0) {
            return AudioService.isServiceRunning ? 100 : 0;
        }
        if (position == 1) {
            return AudioTransmitterService.isServiceRunning ? 200 : 1;
        }
        return super.getItemId(position);
    }

    @Override
    public boolean containsItem(long itemId) {
        return itemId == 0 || itemId == 100 || itemId == 1 || itemId == 200 || itemId == 2;
    }
}
