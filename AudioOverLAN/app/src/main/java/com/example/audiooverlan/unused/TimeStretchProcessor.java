package com.example.audiooverlan.unused;

import android.util.Log;
import java.util.Random;

/**
 * NetEQ-style time-domain signal processor for audio quality enhancement.
 *
 * Implements two core algorithms:
 * 1. Expand: Stretch audio during packet loss by repeating pitch periods
 * 2. Accelerate: Compress audio during buffer overflow by merging similar waveforms
 *
 * Works on decoded PCM audio (16-bit stereo) in the time domain.
 */
public class TimeStretchProcessor {
    private static final String TAG = "TimeStretchProcessor";

    private final int sampleRate;
    private final int channels;

    // Pitch detection parameters
    private static final int MIN_PITCH_PERIOD_MS = 2;   // 500Hz max frequency
    private static final int MAX_PITCH_PERIOD_MS = 20;  // 50Hz min frequency
    private static final int CORRELATION_DOWNSAMPLE = 4; // Process at 12kHz for efficiency

    // WebRTC-style correlation threshold (Lowered to 0.5 for more flexibility)
    private static final double MIN_CORRELATION_THRESHOLD = 0.5;

    // Center-clipping threshold (Lowered to 0.3 to be less aggressive)
    private static final double CENTER_CLIP_THRESHOLD = 0.3;

    // Cross-fade parameters
    private static final int CROSSFADE_MS = 5; // 5ms crossfade to avoid clicks

    // Comfort noise generator
    private final Random noiseRandom = new Random();

    // Statistics
    private long totalExpands = 0;
    private long totalAccelerates = 0;
    private int lastDetectedPitchSamples = 0;
    private double lastCorrelationQuality = 0.0;

    // Pre-allocated buffers to avoid GC pressure in hot path
    private final short[] downsampleBuffer;
    private final double[] correlationBuffer;
    private final short[] centerClipBuffer;
    private final int MAX_ANALYSIS_SAMPLES;

    public TimeStretchProcessor(int sampleRate, int channels) {
        this.sampleRate = sampleRate;
        this.channels = channels;

        // Prepare fixed-size workspace (max 30ms analysis)
        this.MAX_ANALYSIS_SAMPLES = (sampleRate * 30) / 1000;
        this.downsampleBuffer = new short[MAX_ANALYSIS_SAMPLES / CORRELATION_DOWNSAMPLE];
        this.correlationBuffer = new double[MAX_ANALYSIS_SAMPLES / CORRELATION_DOWNSAMPLE];
        this.centerClipBuffer = new short[MAX_ANALYSIS_SAMPLES];

        Log.i(TAG, "TimeStretchProcessor initialized: " + sampleRate + "Hz, " + channels + " channels");
    }

    /**
     * Expand audio by repeating pitch periods when packet is lost.
     *
     * @param history PCM history buffer (circular buffer)
     * @param historyLength Number of valid samples in history
     * @param targetSamples Number of samples to generate
     * @return Synthesized PCM samples
     */
    public short[] expandAudio(short[] history, int historyLength, int targetSamples) {
        totalExpands++;

        // Case 1: No history - generate comfort noise
        if (historyLength < sampleRate / 100) { // Less than 10ms
            Log.w(TAG, "Insufficient history (" + historyLength + " samples), generating comfort noise");
            return generateComfortNoise(targetSamples);
        }

        // Case 2: Minimal history - just repeat last samples
        if (historyLength < sampleRate / 50) { // Less than 20ms
            Log.d(TAG, "Minimal history, repeating last samples");
            return repeatLastSamples(history, historyLength, targetSamples);
        }

        // Case 3: Sufficient history - use pitch-based expansion
        int pitchPeriod = findPitchPeriod(history, historyLength);

        if (pitchPeriod <= 0 || pitchPeriod > historyLength / 2) {
            // Pitch detection failed, fall back to repeating last segment
            Log.w(TAG, "Pitch detection failed, using fallback");
            pitchPeriod = Math.min(sampleRate / 100, historyLength / 2); // 10ms default
        }

        lastDetectedPitchSamples = pitchPeriod;

        // Generate expanded audio by repeating the pitch period
        return repeatPitchPeriod(history, historyLength, pitchPeriod, targetSamples);
    }

    /**
     * Accelerate audio by merging similar pitch periods to reduce buffer size.
     *
     * @param pcmBuffer PCM buffer to compress
     * @param samplesToRemove Number of samples to remove
     * @return Compressed PCM buffer
     */
    public short[] accelerateAudio(short[] pcmBuffer, int samplesToRemove) {
        totalAccelerates++;

        if (samplesToRemove <= 0 || samplesToRemove >= pcmBuffer.length / 2) {
            Log.w(TAG, "Invalid samplesToRemove: " + samplesToRemove);
            return pcmBuffer;
        }

        // Find pitch period in the buffer
        int pitchPeriod = findPitchPeriod(pcmBuffer, pcmBuffer.length);

        if (pitchPeriod <= 0 || pitchPeriod * 2 > pcmBuffer.length) {
            // Safety: If we can't find a good pitch, DON'T accelerate this time.
            // Simple removal causes audible clicks; returning original is much safer.
            Log.w(TAG, "Pitch detection failed for acceleration, skipping this cycle to avoid artifacts");
            return pcmBuffer;
        }

        // Find two consecutive similar pitch periods and merge them
        return mergePitchPeriods(pcmBuffer, pitchPeriod, samplesToRemove);
    }

    /**
     * WebRTC-style center clipping preprocessing for robust pitch detection.
     * Clips samples below threshold to reduce noise impact.
     *
     * @param pcm Input PCM samples
     * @param length Number of valid samples
     * @return Center-clipped samples
     */
    private short[] applyCenterClipping(short[] pcm, int length) {
        // Find max amplitude
        short maxAmplitude = 0;
        for (int i = 0; i < length; i++) {
            short absValue = (short) Math.abs(pcm[i]);
            if (absValue > maxAmplitude) {
                maxAmplitude = absValue;
            }
        }

        // Calculate clipping threshold
        short clipThreshold = (short) (maxAmplitude * CENTER_CLIP_THRESHOLD);

        // Safety: If signal is very quiet, don't apply center clipping
        if (maxAmplitude < 500) {
            return pcm.clone();
        }

        // Apply center clipping reusing buffer
        for (int i = 0; i < length && i < centerClipBuffer.length; i++) {
            if (Math.abs(pcm[i]) > clipThreshold) {
                centerClipBuffer[i] = pcm[i];
            } else {
                centerClipBuffer[i] = 0;
            }
        }

        return centerClipBuffer;
    }

    /**
     * WebRTC-style parabolic interpolation for sub-sample pitch period accuracy.
     * Fits a parabola around the correlation peak for precise pitch estimation.
     *
     * @param correlations Array of correlation values
     * @param peakIndex Index of the peak
     * @return Sub-sample accurate peak position
     */
    private double parabolicInterpolation(double[] correlations, int peakIndex) {
        // Need at least 3 points for parabola fitting
        if (peakIndex <= 0 || peakIndex >= correlations.length - 1) {
            return peakIndex;
        }

        double y1 = correlations[peakIndex - 1];
        double y2 = correlations[peakIndex];
        double y3 = correlations[peakIndex + 1];

        // Parabolic interpolation formula: offset = (y1 - y3) / (2 * (y1 - 2*y2 + y3))
        double denominator = 2.0 * (y1 - 2.0 * y2 + y3);

        if (Math.abs(denominator) < 1e-10) {
            // Avoid division by zero
            return peakIndex;
        }

        double offset = (y1 - y3) / denominator;

        // Clamp offset to reasonable range [-0.5, 0.5]
        offset = Math.max(-0.5, Math.min(0.5, offset));

        return peakIndex + offset;
    }

    /**
     * Find the pitch period using autocorrelation with WebRTC improvements.
     * Uses center-clipping preprocessing and parabolic interpolation.
     *
     * @param pcm PCM samples
     * @param length Number of valid samples
     * @return Pitch period in samples, or -1 if not found
     */
    private int findPitchPeriod(short[] pcm, int length) {
        int minPeriod = (sampleRate * MIN_PITCH_PERIOD_MS) / 1000;
        int maxPeriod = (sampleRate * MAX_PITCH_PERIOD_MS) / 1000;

        // Use last 30ms for analysis
        int analysisLength = Math.min(length, (sampleRate * 30) / 1000);
        int startPos = Math.max(0, length - analysisLength);

        // WebRTC: Apply center-clipping preprocessing for robust pitch detection
        applyCenterClipping(pcm, length);

        // Downsample for efficiency reusing buffer
        int downsampledLength = analysisLength / CORRELATION_DOWNSAMPLE;

        for (int i = 0; i < downsampledLength && i < downsampleBuffer.length; i++) {
            int srcIdx = startPos + (i * CORRELATION_DOWNSAMPLE);
            if (srcIdx < length) {
                // Average channels if stereo (use clipped signal stored in centerClipBuffer)
                if (channels == 2 && srcIdx + 1 < length) {
                    downsampleBuffer[i] = (short) ((centerClipBuffer[srcIdx] + centerClipBuffer[srcIdx + 1]) / 2);
                } else {
                    downsampleBuffer[i] = centerClipBuffer[srcIdx];
                }
            }
        }

        // Compute autocorrelation with normalization reusing buffer
        int downsampledMinPeriod = minPeriod / CORRELATION_DOWNSAMPLE;
        int downsampledMaxPeriod = Math.min(maxPeriod / CORRELATION_DOWNSAMPLE, downsampledLength / 2);

        // Calculate energy for normalization
        long energy = 0;
        for (int i = 0; i < downsampledLength; i++) {
            energy += (long) downsampleBuffer[i] * downsampleBuffer[i];
        }

        double maxCorrelation = -1.0;
        int bestLag = -1;

        for (int lag = downsampledMinPeriod; lag < downsampledMaxPeriod; lag++) {
            long correlation = 0;
            int count = 0;

            for (int i = 0; i < downsampledLength - lag; i++) {
                correlation += (long) downsampleBuffer[i] * downsampleBuffer[i + lag];
                count++;
            }

            if (count > 0 && energy > 0) {
                // Normalize correlation to [0, 1] range
                double normalizedCorr = (double) correlation / energy;
                int idx = lag - downsampledMinPeriod;
                if (idx < correlationBuffer.length) {
                    correlationBuffer[idx] = normalizedCorr;
                }

                if (normalizedCorr > maxCorrelation) {
                    maxCorrelation = normalizedCorr;
                    bestLag = lag;
                }
            }
        }

        // WebRTC: Check correlation quality threshold
        if (bestLag > 0 && maxCorrelation >= MIN_CORRELATION_THRESHOLD) {
            // WebRTC: Apply parabolic interpolation for sub-sample accuracy
            double refinedLag = parabolicInterpolation(correlationBuffer, bestLag - downsampledMinPeriod);
            refinedLag += downsampledMinPeriod;

            // Scale back to original sample rate
            int pitchPeriod = (int) Math.round(refinedLag * CORRELATION_DOWNSAMPLE);

            // Further refine at original sample rate
            pitchPeriod = refinePitchPeriod(centerClipBuffer, startPos, length, pitchPeriod);

            // Store correlation quality for statistics
            lastCorrelationQuality = maxCorrelation;

            Log.d(TAG, String.format("Detected pitch period: %d samples (%.2f ms, %.1f Hz) | correlation=%.3f",
                    pitchPeriod, (1000.0 * pitchPeriod / sampleRate),
                    (sampleRate / (float) pitchPeriod), maxCorrelation));

            return pitchPeriod;
        } else if (bestLag > 0) {
            // Low correlation quality - log warning
            Log.w(TAG, String.format("Low correlation quality: %.3f < %.3f (threshold)",
                    maxCorrelation, MIN_CORRELATION_THRESHOLD));
            lastCorrelationQuality = maxCorrelation;
        }

        return -1;
    }

    /**
     * Refine pitch period detection at full sample rate.
     */
    private int refinePitchPeriod(short[] pcm, int startPos, int length, int roughPeriod) {
        int searchRange = CORRELATION_DOWNSAMPLE;
        int minLag = Math.max(1, roughPeriod - searchRange);
        int maxLag = Math.min(length / 2, roughPeriod + searchRange);

        long maxCorrelation = Long.MIN_VALUE;
        int bestLag = roughPeriod;

        int analysisLength = Math.min(length - startPos, (sampleRate * 30) / 1000);

        for (int lag = minLag; lag <= maxLag; lag++) {
            long correlation = 0;
            int count = 0;

            for (int i = startPos; i < startPos + analysisLength - lag && i + lag < length; i++) {
                correlation += (long) pcm[i] * pcm[i + lag];
                count++;
            }

            if (count > 0 && correlation > maxCorrelation) {
                maxCorrelation = correlation;
                bestLag = lag;
            }
        }

        return bestLag;
    }

    /**
     * Repeat the last pitch period with cross-fading.
     */
    private short[] repeatPitchPeriod(short[] history, int historyLength, int pitchPeriod, int targetSamples) {
        short[] output = new short[targetSamples];
        int crossfadeSamples = Math.min((sampleRate * CROSSFADE_MS) / 1000, pitchPeriod / 4);

        // Start position: last complete pitch period
        int sourceStart = Math.max(0, historyLength - pitchPeriod);

        int outputPos = 0;
        int repetitionCount = 0;

        while (outputPos < targetSamples) {
            int remainingSamples = targetSamples - outputPos;
            int copyLength = Math.min(pitchPeriod, remainingSamples);

            // Copy pitch period
            for (int i = 0; i < copyLength && outputPos < targetSamples; i++) {
                int srcIdx = sourceStart + (i % pitchPeriod);
                if (srcIdx >= historyLength) {
                    srcIdx = sourceStart;
                }

                short currentSample = history[srcIdx];

                // Apply cross-fade at the SEAM between repetitions
                // Overlap: end of previous period fades out, start of current period fades in
                if (repetitionCount > 0 && i < crossfadeSamples) {
                    float fadeIn = (float) i / crossfadeSamples;
                    float fadeOut = 1.0f - fadeIn;

                    // Get sample from END of previous repetition (already written)
                    int prevEndIdx = outputPos - crossfadeSamples + i;
                    if (prevEndIdx >= 0 && prevEndIdx < outputPos) {
                        short prevSample = output[prevEndIdx];
                        // Blend: fade out previous, fade in current
                        currentSample = (short) (prevSample * fadeOut + currentSample * fadeIn);
                    }
                }

                output[outputPos++] = currentSample;
            }

            repetitionCount++;
        }

        Log.d(TAG, "Expanded " + targetSamples + " samples using pitch period " + pitchPeriod + " (" + repetitionCount + " repetitions)");
        return output;
    }

    /**
     * Merge two consecutive pitch periods by averaging them.
     */
    private short[] mergePitchPeriods(short[] pcm, int pitchPeriod, int samplesToRemove) {
        // Find the best position to merge (where two periods are most similar)
        int bestMergePos = findBestMergePosition(pcm, pitchPeriod);

        if (bestMergePos < 0 || bestMergePos + pitchPeriod * 2 > pcm.length) {
            return removeMiddleSegment(pcm, samplesToRemove);
        }

        int removeLength = Math.min(pitchPeriod, samplesToRemove);
        int crossfadeSamples = Math.min((sampleRate * CROSSFADE_MS) / 1000, pitchPeriod / 4);

        short[] output = new short[pcm.length - removeLength];
        int outPos = 0;

        // Copy everything before merge point
        System.arraycopy(pcm, 0, output, outPos, bestMergePos);
        outPos += bestMergePos;

        // Merge region: cross-fade between first and second pitch period
        for (int i = 0; i < pitchPeriod && outPos < output.length; i++) {
            int src1 = bestMergePos + i;
            int src2 = bestMergePos + removeLength + i;

            if (src1 < pcm.length && src2 < pcm.length) {
                if (i < crossfadeSamples) {
                    // Cross-fade from first period to second period
                    float weight = (float) i / crossfadeSamples;
                    output[outPos++] = (short) (pcm[src1] * (1.0f - weight) + pcm[src2] * weight);
                } else {
                    // After cross-fade, use second period
                    output[outPos++] = pcm[src2];
                }
            } else if (src1 < pcm.length) {
                output[outPos++] = pcm[src1];
            } else {
                break;
            }
        }

        // Copy everything after the merged region
        int srcRemaining = bestMergePos + removeLength + pitchPeriod;
        int copyLength = pcm.length - srcRemaining;

        if (copyLength > 0 && srcRemaining < pcm.length && outPos + copyLength <= output.length) {
            System.arraycopy(pcm, srcRemaining, output, outPos, copyLength);
        }

        Log.d(TAG, "Accelerated by merging pitch periods at pos " + bestMergePos + ", removed " + removeLength + " samples");
        return output;
    }

    /**
     * Find the best position to merge two pitch periods (highest similarity).
     */
    private int findBestMergePosition(short[] pcm, int pitchPeriod) {
        long minDifference = Long.MAX_VALUE;
        int bestPos = -1;

        // Search in the middle portion of the buffer
        int searchStart = pcm.length / 4;
        int searchEnd = (pcm.length * 3) / 4 - pitchPeriod * 2;

        for (int pos = searchStart; pos < searchEnd; pos += pitchPeriod / 4) {
            long difference = 0;
            int compareLength = Math.min(pitchPeriod, pcm.length - pos - pitchPeriod);

            for (int i = 0; i < compareLength; i++) {
                int diff = pcm[pos + i] - pcm[pos + pitchPeriod + i];
                difference += (long) diff * diff;
            }

            if (difference < minDifference) {
                minDifference = difference;
                bestPos = pos;
            }
        }

        return bestPos;
    }

    /**
     * Simple removal of middle segment with cross-fade (fallback).
     */
    private short[] removeMiddleSegment(short[] pcm, int samplesToRemove) {
        if (samplesToRemove >= pcm.length) {
            return new short[0];
        }

        int removeStart = (pcm.length - samplesToRemove) / 2;
        int crossfadeSamples = Math.min((sampleRate * CROSSFADE_MS) / 1000, Math.min(removeStart, samplesToRemove / 2));

        short[] output = new short[pcm.length - samplesToRemove];
        int outPos = 0;

        // Copy everything before the crossfade region
        int beforeCrossfade = removeStart - crossfadeSamples;
        if (beforeCrossfade > 0) {
            System.arraycopy(pcm, 0, output, outPos, beforeCrossfade);
            outPos += beforeCrossfade;
        }

        // Cross-fade region: blend audio before removal with audio after removal
        for (int i = 0; i < crossfadeSamples && outPos < output.length; i++) {
            float weight = (float) i / crossfadeSamples;
            int srcBefore = removeStart - crossfadeSamples + i;
            int srcAfter = removeStart + samplesToRemove + i;

            if (srcBefore >= 0 && srcBefore < pcm.length && srcAfter >= 0 && srcAfter < pcm.length) {
                output[outPos++] = (short) (pcm[srcBefore] * (1.0f - weight) + pcm[srcAfter] * weight);
            } else if (srcBefore >= 0 && srcBefore < pcm.length) {
                output[outPos++] = pcm[srcBefore];
            } else if (srcAfter >= 0 && srcAfter < pcm.length) {
                output[outPos++] = pcm[srcAfter];
            }
        }

        // Copy everything after the crossfade region
        int afterCrossfadeStart = removeStart + samplesToRemove + crossfadeSamples;
        int remainingLength = pcm.length - afterCrossfadeStart;

        if (remainingLength > 0 && afterCrossfadeStart < pcm.length && outPos + remainingLength <= output.length) {
            System.arraycopy(pcm, afterCrossfadeStart, output, outPos, remainingLength);
        }

        Log.d(TAG, "Removed middle segment: " + samplesToRemove + " samples with " + crossfadeSamples + " sample crossfade");
        return output;
    }

    /**
     * Repeat the last available samples (for minimal history).
     */
    private short[] repeatLastSamples(short[] history, int historyLength, int targetSamples) {
        short[] output = new short[targetSamples];

        for (int i = 0; i < targetSamples; i++) {
            output[i] = history[(historyLength - targetSamples + i) % historyLength];
        }

        return output;
    }

    /**
     * Generate comfort noise (pink noise at low volume).
     */
    private short[] generateComfortNoise(int targetSamples) {
        short[] output = new short[targetSamples];

        // Generate very quiet pink noise (-60dB)
        for (int i = 0; i < targetSamples; i++) {
            output[i] = (short) (noiseRandom.nextGaussian() * 32); // Very quiet
        }

        Log.d(TAG, "Generated " + targetSamples + " samples of comfort noise");
        return output;
    }

    /**
     * Get statistics.
     */
    public String getStatistics() {
        return String.format("TimeStretch: expands=%d, accelerates=%d, lastPitch=%d samples (%.1f ms), corr=%.3f",
                totalExpands, totalAccelerates, lastDetectedPitchSamples,
                (1000.0 * lastDetectedPitchSamples / sampleRate), lastCorrelationQuality);
    }

    public long getTotalExpands() {
        return totalExpands;
    }

    public long getTotalAccelerates() {
        return totalAccelerates;
    }
}
