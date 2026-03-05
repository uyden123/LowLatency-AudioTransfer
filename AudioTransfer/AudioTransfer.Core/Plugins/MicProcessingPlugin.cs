using System;
using SoundFlow.Extensions.WebRtc.Apm;

namespace AudioTransfer.Core.Plugins
{
    /// <summary>
    /// Specialized plugin for Android Mic stream processing using WebRTC APM.
    /// handles framing (10ms WebRTC requirement) and conversion.
    /// </summary>
    public class MicProcessingPlugin : IAudioPlugin, IDisposable
    {
        public string Name => "MicProcessor";
        public bool IsEnabled { get; set; } = true;

        private readonly AudioProcessingModule _apm;
        private readonly StreamConfig _config;
        private readonly float[][] _inputChannels;
        private readonly float[][] _outputChannels;
        private readonly int _frameSize; // WebRTC expects 10ms (480 samples @ 48kHz)

        // Settings
        public bool EnableNs { get; set; } = true;
        public bool EnableAgc { get; set; } = true;
        public bool EnableNoiseGate { get; set; } = true;
        
        // Noise Gate state
        private float _envelope = 0.0f;
        private float _gateStatus = 0.0f; // 0 = closed, 1 = open
        public float GateThreshold { get; set; } = 0.01f; 

        // Internal buffers for Process


        public MicProcessingPlugin()
        {
            _apm = new AudioProcessingModule();
            _config = new StreamConfig(48000, 1);
            _frameSize = AudioProcessingModule.GetFrameSize(48000); 
            
            _inputChannels = new float[1][] { new float[_frameSize] };
            _outputChannels = new float[1][] { new float[_frameSize] };

            UpdateConfig();
            _apm.Initialize();
        }

        public void UpdateConfig()
        {
            var config = new ApmConfig();
            config.SetNoiseSuppression(EnableNs, NoiseSuppressionLevel.High);
            config.SetGainController1(EnableAgc, GainControlMode.AdaptiveDigital, 3, 9, true);
            config.SetGainController2(EnableAgc);
            config.SetEchoCanceller(false, false);
            config.SetHighPassFilter(true);
            _apm.ApplyConfig(config);
        }

        public void Process(short[] buffer, int length, int sampleRate, int channels)
        {
            if (!IsEnabled) return;
            if (sampleRate != 48000 || channels != 1) return;

            // totalSamples here is 'length' (total number of shorts in buffer)
            for (int offset = 0; offset <= length - _frameSize; offset += _frameSize)
            {
                // 1. Convert to Float
                for (int i = 0; i < _frameSize; i++)
                {
                    _inputChannels[0][i] = buffer[offset + i] / 32768.0f;
                }

                // 2. WebRTC APM Process
                _apm.ProcessStream(_inputChannels, _config, _config, _outputChannels);

                // 3. Post-process (Noise Gate) + Back to Short
                for (int i = 0; i < _frameSize; i++)
                {
                    float sample = _outputChannels[0][i];

                    if (EnableNoiseGate)
                    {
                        // 1. Simple peak envelope follower
                        float absSample = Math.Abs(sample);
                        // Fast attack, slow release for envelope detection
                        _envelope = (absSample > _envelope) ? 
                            (0.1f * absSample + 0.9f * _envelope) : 
                            (0.001f * absSample + 0.999f * _envelope);

                        // 2. Threshold check for gate status (open/closed)
                        float targetGate = (_envelope > GateThreshold) ? 1.0f : 0.0f;
                        
                        // 3. Smooth the gate status transition to avoid clicks
                        if (targetGate > _gateStatus)
                            _gateStatus = Math.Min(1.0f, _gateStatus + 0.01f); // Attack
                        else
                            _gateStatus = Math.Max(0.0f, _gateStatus - 0.001f); // Release (slow)

                        sample *= _gateStatus;
                    }

                    // Clamp and Convert
                    if (sample > 1.0f) sample = 1.0f;
                    else if (sample < -1.0f) sample = -1.0f;
                    
                    buffer[offset + i] = (short)(sample * 32767);
                }
            }
        }



        public void Dispose()
        {
            _apm?.Dispose();
        }
    }
}
