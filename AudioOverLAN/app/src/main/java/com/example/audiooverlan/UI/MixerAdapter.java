package com.example.audiooverlan.UI;

import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.util.Base64;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ImageView;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.recyclerview.widget.RecyclerView;

import com.example.audiooverlan.R;
import com.example.audiooverlan.audio.MixerSession;
import com.google.android.material.slider.Slider;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

public class MixerAdapter extends RecyclerView.Adapter<MixerAdapter.ViewHolder> {

    public interface OnVolumeChangedListener {
        void onVolumeChanged(long pid, float volume);
    }

    private final List<MixerSession> sessions = new ArrayList<>();
    private static final java.util.Map<Long, Bitmap> bitmapCache = new java.util.HashMap<>();
    private OnVolumeChangedListener listener;

    private static final String PAYLOAD_VOLUME = "PAYLOAD_VOLUME";

    public void setOnVolumeChangedListener(OnVolumeChangedListener listener) {
        this.listener = listener;
    }

    public void updateSessions(List<MixerSession> newSessions) {
        sessions.clear();
        // Do NOT clear bitmapCache here - persist icons across syncs
        sessions.addAll(newSessions);
        notifyDataSetChanged();
    }

    public void updateSession(MixerSession updated) {
        for (int i = 0; i < sessions.size(); i++) {
            MixerSession existing = sessions.get(i);
            if (existing.pid == updated.pid) {
                boolean iconChanged = updated.icon != null && !updated.icon.isEmpty() && !updated.icon.equals(existing.icon);
                boolean nameChanged = updated.name != null && !updated.name.equals("Unknown") && !updated.name.equals(existing.name);
                
                // Volume/Mute logic: Only update if it's a real volume update (not a dummy object from icon response)
                // or if the value actually changed.
                boolean volumeChanged = (updated.volume >= 0 && updated.volume <= 1.0f && updated.volume != existing.volume) 
                                     || (updated.mute != existing.mute);

                if (nameChanged) existing.name = updated.name;
                
                if (iconChanged) {
                    existing.icon = updated.icon;
                    // Important: Purge old bitmap so it re-decodes the new icon
                    bitmapCache.remove(existing.pid);
                }
                
                if (volumeChanged || (updated.name != null && !updated.name.equals("Unknown"))) {
                    existing.volume = updated.volume;
                    existing.mute = updated.mute;
                }
                
                if (iconChanged || nameChanged) {
                    notifyItemChanged(i);
                } else if (volumeChanged) {
                    notifyItemChanged(i, PAYLOAD_VOLUME);
                }
                return;
            }
        }
        // Not found — it's a new app
        sessions.add(updated);
        notifyItemInserted(sessions.size() - 1);
    }

    @NonNull
    @Override
    public ViewHolder onCreateViewHolder(@NonNull ViewGroup parent, int viewType) {
        View view = LayoutInflater.from(parent.getContext()).inflate(R.layout.item_mixer_session, parent, false);
        return new ViewHolder(view);
    }

    @Override
    public void onBindViewHolder(@NonNull ViewHolder holder, int position, @NonNull List<Object> payloads) {
        if (!payloads.isEmpty()) {
            for (Object payload : payloads) {
                if (PAYLOAD_VOLUME.equals(payload)) {
                    MixerSession session = sessions.get(position);
                    holder.tvVolume.setText(String.format(Locale.getDefault(), "%d%%", Math.round(session.volume * 100)));
                    holder.slider.removeOnChangeListener(holder.changeListener);
                    holder.slider.setValue(session.volume);
                    holder.slider.addOnChangeListener(holder.changeListener);
                    return;
                }
            }
        }
        super.onBindViewHolder(holder, position, payloads);
    }

    @Override
    public void onBindViewHolder(@NonNull ViewHolder holder, int position) {
        MixerSession session = sessions.get(position);
        holder.tvAppName.setText(session.name);
        holder.tvVolume.setText(String.format(Locale.getDefault(), "%d%%", Math.round(session.volume * 100)));

        // Optimized Icon Loading with Bitmap Cache
        Bitmap cachedBmp = bitmapCache.get(session.pid);
        if (cachedBmp != null) {
            holder.ivAppIcon.setImageBitmap(cachedBmp);
        } else if (session.icon != null && !session.icon.isEmpty()) {
            try {
                byte[] decoded = Base64.decode(session.icon, Base64.DEFAULT);
                Bitmap bmp = BitmapFactory.decodeByteArray(decoded, 0, decoded.length);
                if (bmp != null) {
                    bitmapCache.put(session.pid, bmp);
                    holder.ivAppIcon.setImageBitmap(bmp);
                } else {
                    holder.ivAppIcon.setImageResource(R.drawable.ic_computer);
                }
            } catch (Exception e) {
                holder.ivAppIcon.setImageResource(R.drawable.ic_computer);
            }
        } else {
            holder.ivAppIcon.setImageResource(R.drawable.ic_computer);
        }

        // Slider
        holder.slider.removeOnSliderTouchListener(holder.touchListener);
        holder.slider.removeOnChangeListener(holder.changeListener);

        holder.slider.setValue(session.volume);

        holder.changeListener = (slider, value, fromUser) -> {
            if (fromUser) {
                holder.tvVolume.setText(String.format(Locale.getDefault(), "%d%%", Math.round(value * 100)));
            }
        };

        holder.touchListener = new Slider.OnSliderTouchListener() {
            @Override
            public void onStartTrackingTouch(@NonNull Slider slider) {}

            @Override
            public void onStopTrackingTouch(@NonNull Slider slider) {
                if (listener != null) {
                    listener.onVolumeChanged(session.pid, slider.getValue());
                }
            }
        };

        holder.slider.addOnChangeListener(holder.changeListener);
        holder.slider.addOnSliderTouchListener(holder.touchListener);
    }

    @Override
    public int getItemCount() {
        return sessions.size();
    }

    static class ViewHolder extends RecyclerView.ViewHolder {
        ImageView ivAppIcon;
        TextView tvAppName;
        TextView tvVolume;
        Slider slider;
        Slider.OnSliderTouchListener touchListener;
        Slider.OnChangeListener changeListener;

        ViewHolder(@NonNull View itemView) {
            super(itemView);
            ivAppIcon = itemView.findViewById(R.id.ivAppIcon);
            tvAppName = itemView.findViewById(R.id.tvAppName);
            tvVolume = itemView.findViewById(R.id.tvMixerVolume);
            slider = itemView.findViewById(R.id.sliderMixerVolume);
        }
    }
}
