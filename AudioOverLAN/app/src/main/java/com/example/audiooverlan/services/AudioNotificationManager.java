package com.example.audiooverlan.services;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.support.v4.media.session.MediaSessionCompat;

import androidx.core.app.NotificationCompat;

import com.example.audiooverlan.R;
import com.example.audiooverlan.UI.MainActivity;

public class AudioNotificationManager {

    private static final String CHANNEL_ID = "AudioServiceChannel";
    public static final int NOTIFICATION_ID = 1;

    private final Context context;
    private final NotificationManager notificationManager;

    public AudioNotificationManager(Context context) {
        this.context = context;
        this.notificationManager = (NotificationManager) context.getSystemService(Context.NOTIFICATION_SERVICE);
        createNotificationChannel();
    }

    private void createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel serviceChannel = new NotificationChannel(
                    CHANNEL_ID,
                    "Audio Service Channel",
                    NotificationManager.IMPORTANCE_LOW
            );
            notificationManager.createNotificationChannel(serviceChannel);
        }
    }

    public Notification createNotification(String contentText, MediaSessionCompat mediaSession) {
        Intent notificationIntent = new Intent(context, MainActivity.class);
        PendingIntent pendingIntent = PendingIntent.getActivity(
                context, 0, notificationIntent, PendingIntent.FLAG_IMMUTABLE
        );

        Intent stopIntent = new Intent(context, AudioService.class);
        stopIntent.setAction(AudioService.ACTION_STOP);
        PendingIntent stopPendingIntent = PendingIntent.getService(
                context, 0, stopIntent, PendingIntent.FLAG_IMMUTABLE
        );

        NotificationCompat.Builder builder = new NotificationCompat.Builder(context, CHANNEL_ID)
                .setSmallIcon(R.drawable.ic_broadcast)
                .setContentTitle("AudioOverLAN Player")
                .setContentText(contentText)
                .setContentIntent(pendingIntent)
                .setPriority(NotificationCompat.PRIORITY_LOW)
                .addAction(new NotificationCompat.Action(
                        R.drawable.ic_stop,
                        "STOP",
                        stopPendingIntent
                ));

        if (mediaSession != null) {
            builder.setStyle(new androidx.media.app.NotificationCompat.MediaStyle()
                    .setMediaSession(mediaSession.getSessionToken())
                    .setShowActionsInCompactView(0));
        }

        return builder.build();
    }

    public void updateNotification(String contentText, MediaSessionCompat mediaSession) {
        notificationManager.notify(NOTIFICATION_ID, createNotification(contentText, mediaSession));
    }
}
