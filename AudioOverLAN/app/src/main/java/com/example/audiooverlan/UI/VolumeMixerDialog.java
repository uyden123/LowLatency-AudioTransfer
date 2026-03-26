package com.example.audiooverlan.UI;

import android.content.Context;
import android.view.LayoutInflater;
import android.view.View;
import android.widget.FrameLayout;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.recyclerview.widget.LinearLayoutManager;
import androidx.recyclerview.widget.RecyclerView;

import com.example.audiooverlan.R;
import com.example.audiooverlan.audio.MixerSession;
import com.example.audiooverlan.services.AudioService;
import com.google.android.material.bottomsheet.BottomSheetBehavior;
import com.google.android.material.bottomsheet.BottomSheetDialog;

import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

/**
 * Manages creating and showing a BottomSheetDialog for the Volume Mixer.
 * Creates a fresh dialog each time show() is called for clean animation.
 * Session data is kept in memory for persistence across opens.
 */
public class VolumeMixerDialog {

    private final Context context;
    private final List<MixerSession> currentSessions = new ArrayList<>();
    private BottomSheetDialog activeDialog;

    public VolumeMixerDialog(@NonNull Context context) {
        this.context = context;
    }

    /** Show a fresh dialog with the latest session data. */
    public void show() {
        // Create a brand new dialog every time
        BottomSheetDialog dialog = new BottomSheetDialog(context, R.style.MixerBottomSheetTheme);
        View view = LayoutInflater.from(context).inflate(R.layout.layout_volume_mixer, null);
        dialog.setContentView(view);

        RecyclerView rvMixer = view.findViewById(R.id.rvMixerSessionsSheet);
        TextView tvEmpty = view.findViewById(R.id.tvMixerEmptySheet);
        TextView tvStatus = view.findViewById(R.id.tvMixerStatusSheet);

        MixerAdapter adapter = new MixerAdapter();
        rvMixer.setLayoutManager(new LinearLayoutManager(context));
        rvMixer.setAdapter(adapter);

        // Load current data
        if (!currentSessions.isEmpty()) {
            adapter.updateSessions(currentSessions);
            rvMixer.setVisibility(View.VISIBLE);
            tvEmpty.setVisibility(View.GONE);
            tvStatus.setText(currentSessions.size() + " apps");
        } else {
            rvMixer.setVisibility(View.GONE);
            tvEmpty.setVisibility(View.VISIBLE);
            tvStatus.setText("Syncing...");
        }

        adapter.setOnVolumeChangedListener((pid, volume) -> {
            try {
                JSONObject cmd = new JSONObject();
                cmd.put("command", "set_session_vol");
                cmd.put("pid", pid);
                cmd.put("vol", volume);
                AudioService.sendServerCommand(cmd);
            } catch (Exception e) {
                e.printStackTrace();
            }
        });

        view.findViewById(R.id.btnCloseMixer).setOnClickListener(v -> dialog.dismiss());

        dialog.setOnDismissListener(d -> activeDialog = null);
        activeDialog = dialog;

        // Show first, THEN configure behavior after the sheet is laid out
        dialog.show();

        // Now that dialog.show() has been called, the bottom sheet view exists
        FrameLayout bottomSheet = dialog.findViewById(com.google.android.material.R.id.design_bottom_sheet);
        if (bottomSheet != null) {
            BottomSheetBehavior<FrameLayout> behavior = BottomSheetBehavior.from(bottomSheet);
            behavior.setSkipCollapsed(true);
            behavior.setFitToContents(true);
            // Post to next frame so the initial peek animation completes,
            // then smoothly expand to full content
            bottomSheet.post(() -> behavior.setState(BottomSheetBehavior.STATE_EXPANDED));
        }
    }

    /** Store session list and update active dialog if visible. */
    public void updateSessions(List<MixerSession> sessions) {
        currentSessions.clear();
        currentSessions.addAll(sessions);
        if (activeDialog != null && activeDialog.isShowing()) {
            RecyclerView rv = activeDialog.findViewById(R.id.rvMixerSessionsSheet);
            TextView tvEmpty = activeDialog.findViewById(R.id.tvMixerEmptySheet);
            TextView tvStatus = activeDialog.findViewById(R.id.tvMixerStatusSheet);
            if (rv != null && rv.getAdapter() instanceof MixerAdapter) {
                ((MixerAdapter) rv.getAdapter()).updateSessions(sessions);
                rv.setVisibility(View.VISIBLE);
                if (tvEmpty != null) tvEmpty.setVisibility(View.GONE);
                if (tvStatus != null) tvStatus.setText(sessions.size() + " apps");
            }
        }
    }

    /** Update a single session and refresh active dialog if visible. */
    public void updateSession(MixerSession session) {
        boolean found = false;
        for (int i = 0; i < currentSessions.size(); i++) {
            if (currentSessions.get(i).pid == session.pid) {
                MixerSession existing = currentSessions.get(i);
                if (session.name != null && !session.name.isEmpty()) existing.name = session.name;
                if (session.icon != null && !session.icon.isEmpty() && !session.icon.equals("null")) existing.icon = session.icon;
                if (session.volume >= 0) existing.volume = session.volume;
                existing.mute = session.mute;
                found = true;
                break;
            }
        }
        if (!found && session.name != null && !session.name.isEmpty()) {
            currentSessions.add(session);
        }
        if (activeDialog != null && activeDialog.isShowing()) {
            RecyclerView rv = activeDialog.findViewById(R.id.rvMixerSessionsSheet);
            if (rv != null && rv.getAdapter() instanceof MixerAdapter) {
                ((MixerAdapter) rv.getAdapter()).updateSession(session);
            }
        }
    }

    /** Update icon for a specific session. */
    public void updateIcon(long pid, String icon) {
        MixerSession s = new MixerSession();
        s.pid = pid;
        s.icon = icon;
        s.volume = -1;
        updateSession(s);
    }

    public int getSessionCount() {
        return currentSessions.size();
    }
}
