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
        color = ContextCompat.getColor(getContext(), R.color.primary_blue);
        paint.setColor(color);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(4f);
        paint.setAntiAlias(true);
        paint.setStrokeCap(Paint.Cap.ROUND);
        paint.setStrokeJoin(Paint.Join.ROUND);
    }

    public synchronized void updateSamples(short[] newSamples) {
        if (newSamples == null || newSamples.length == 0) return;
        
        if (this.samples == null || this.samples.length != newSamples.length) {
            this.samples = new short[newSamples.length];
        }
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
        float step = (float) samples.length / width;

        path.moveTo(0, midY);

        // Down-sample for performance: draw every 2 pixels
        for (int i = 0; i < width; i += 2) {
            int sampleIdx = (int) (i * step);
            if (sampleIdx >= samples.length) sampleIdx = samples.length - 1;

            float x = i;
            // Normalize short (-32768 to 32767) to fit height
            float sampleVal = samples[sampleIdx] / 32768f;
            float y = midY + (sampleVal * (height / 2f) * 0.8f); // 80% height usage

            path.lineTo(x, y);
        }

        canvas.drawPath(path, paint);
    }
}
