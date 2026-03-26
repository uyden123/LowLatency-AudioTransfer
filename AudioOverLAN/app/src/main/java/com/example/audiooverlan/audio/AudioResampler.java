package com.example.audiooverlan.audio;

public class AudioResampler {
    private final int channels;
    private double currentPhase = 0.0;
    private short[] resampleWorkspace = new short[4096];

    public AudioResampler(int channels) {
        this.channels = channels;
    }

    public int resample(short[] in, int numSamplesIn, double ratio, short[][] outBufferRef) {
        if (Math.abs(ratio - 1.0) < 0.0001) {
            // Passthrough, but keep phase continuous
            if (resampleWorkspace.length < numSamplesIn) {
                resampleWorkspace = new short[numSamplesIn + 1024];
            }
            System.arraycopy(in, 0, resampleWorkspace, 0, numSamplesIn);
            outBufferRef[0] = resampleWorkspace;
            return numSamplesIn;
        }

        int numFramesIn = numSamplesIn / channels;
        int maxFramesOut = (int) Math.ceil((numFramesIn - currentPhase) * ratio) + 2;
        int totalSamplesOut = maxFramesOut * channels;
        
        if (resampleWorkspace.length < totalSamplesOut) {
            resampleWorkspace = new short[totalSamplesOut + 1024];
        }
        
        int outIdx = 0;
        int stepFixed = (int) Math.round((1.0 / ratio) * 65536.0);
        long phaseFixed = (long) (currentPhase * 65536.0);
        long numFramesInFixed = ((long) numFramesIn) << 16;
        
        if (channels == 2) {
            while (phaseFixed < numFramesInFixed) {
                int idx = (int) (phaseFixed >> 16);
                int fracFixed = (int) (phaseFixed & 0xFFFF);
                
                int baseIdx = idx << 1;
                short l1 = in[baseIdx];
                short r1 = in[baseIdx + 1];
                
                short l2 = (idx + 1 < numFramesIn) ? in[baseIdx + 2] : l1;
                short r2 = (idx + 1 < numFramesIn) ? in[baseIdx + 3] : r1;
                
                resampleWorkspace[outIdx << 1] = (short) (l1 + ((fracFixed * (l2 - l1)) >> 16));
                resampleWorkspace[(outIdx << 1) + 1] = (short) (r1 + ((fracFixed * (r2 - r1)) >> 16));
                
                outIdx++;
                phaseFixed += stepFixed;
            }
        } else {
            while (phaseFixed < numFramesInFixed) {
                int idx = (int) (phaseFixed >> 16);
                int fracFixed = (int) (phaseFixed & 0xFFFF);
                
                short v1 = in[idx];
                short v2 = (idx + 1 < numFramesIn) ? in[idx + 1] : v1;
                
                resampleWorkspace[outIdx] = (short) (v1 + ((fracFixed * (v2 - v1)) >> 16));
                
                outIdx++;
                phaseFixed += stepFixed;
            }
        }
        
        currentPhase = (double) (phaseFixed - numFramesInFixed) / 65536.0;
        outBufferRef[0] = resampleWorkspace;
        return outIdx * channels;
    }
}
