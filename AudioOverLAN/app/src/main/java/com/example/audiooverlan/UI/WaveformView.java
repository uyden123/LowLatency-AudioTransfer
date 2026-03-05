package com.example.audiooverlan.UI;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.Path;
import android.util.AttributeSet;
import android.view.View;

import androidx.annotation.Nullable;
import androidx.core.content.ContextCompat;

import com.example.audiooverlan.R;

public class WaveformView extends View {

    private final Paint paint = new Paint();
    private final Path path = new Path();
    private short[] samples;
    private int color;

    public WaveformView(Context context, @Nullable AttributeSet attrs) {
        super(context, attrs);
        init();
    }

    private void init() {
        color = ContextCompat.getColor(getContext(), R.color.primary_green);
        paint.setColor(color);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(4f);
        paint.setAntiAlias(true);
        paint.setStrokeCap(Paint.Cap.ROUND);
        paint.setStrokeJoin(Paint.Join.ROUND);
    }

    public synchronized void updateSamples(short[] newSamples) {
        // Deep copy interested part or keep reference if we know it won't change immediately
        // For visualizers, a reference is usually okay if we draw fast enough, but copy is safer.
        if (newSamples == null || newSamples.length == 0) return;
        
        // Downsample for performance if needed, but for 960 samples (20ms) it's fine.
        this.samples = new short[newSamples.length];
        System.arraycopy(newSamples, 0, this.samples, 0, newSamples.length);
        postInvalidateOnAnimation();
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        int width = getWidth();
        int height = getHeight();
        int midY = height / 2;

        if (samples == null || samples.length == 0) {
            canvas.drawLine(0, midY, width, midY, paint);
            return;
        }

        path.reset();
        
        // We might be getting stereo (LRLR), so let's just draw one channel or mix
        int step = samples.length / width;
        if (step < 1) step = 1;

        float lastX = 0;
        float lastY = midY;
        path.moveTo(0, midY);

        for (int i = 0; i < width; i++) {
            int sampleIdx = i * step;
            if (sampleIdx >= samples.length) break;

            float x = i;
            // Normalize short (-32768 to 32767) to fit height
            float sampleVal = samples[sampleIdx] / 32768f;
            float y = midY + (sampleVal * (height / 2f) * 0.8f); // 80% height usage

            path.lineTo(x, y);
        }

        canvas.drawPath(path, paint);
    }
}
