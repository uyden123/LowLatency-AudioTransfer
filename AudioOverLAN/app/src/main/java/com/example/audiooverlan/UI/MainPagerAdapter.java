package com.example.audiooverlan.UI;

import androidx.annotation.NonNull;
import androidx.fragment.app.Fragment;
import androidx.fragment.app.FragmentActivity;
import androidx.viewpager2.adapter.FragmentStateAdapter;

public class MainPagerAdapter extends FragmentStateAdapter {

    public MainPagerAdapter(@NonNull FragmentActivity fragmentActivity) {
        super(fragmentActivity);
    }

    public void setConnectedIp(String ip) {
        // Now handled by PlayerHostFragment observing the ViewModel
    }

    @NonNull
    @Override
    public Fragment createFragment(int position) {
        switch (position) {
            case 0:
                return new PlayerHostFragment(); // Permanent host
            case 1:
                return new ServerHostFragment(); // Permanent host
            case 2:
                return new SettingsFragment();
            default:
                return new PlayerHostFragment();
        }
    }

    @Override
    public int getItemCount() {
        return 3;
    }

    @Override
    public long getItemId(int position) {
        return position; // Fixed IDs ensure fragments are NEVER recreated by ViewPager
    }

    @Override
    public boolean containsItem(long itemId) {
        return itemId >= 0 && itemId < 3;
    }
}
