using System;
using System.Buffers;
using System.Collections.Generic;
using AudioTransfer.Core.Codec;

namespace AudioTransfer.Core.Audio
{
    /// <summary>
    /// Handles PCM accumulation and Opus encoding.
    /// Manages its own sequence numbering and timestamping.
    /// </summary>
    public class AudioEncoderEngine : IDisposable
    {
        private readonly OpusEncoderWrapper _opusEncoder;
        private readonly int _samplesPerFrameInterleaved;
        private readonly int _framesPerPacket;
        
        private readonly short[] _accumulator;
        private int _accumulatedSamples = 0;
        
        private readonly short[] _packetPcm;
        private readonly byte[] _opusOutBuffer;
        
        private ushort _seqNum = 0;
        private long _timestampSamples = 0;

        private const byte CODEC_AUDIO = 1;

        public AudioEncoderEngine(OpusEncoderWrapper encoder, int samplesPerFrameInterleaved, int framesPerPacket)
        {
            _opusEncoder = encoder;
            _samplesPerFrameInterleaved = samplesPerFrameInterleaved;
            _framesPerPacket = framesPerPacket;
            
            _accumulator = new short[samplesPerFrameInterleaved * 8]; // Large enough for bursts
            _packetPcm = new short[samplesPerFrameInterleaved];
            _opusOutBuffer = new byte[8192]; // Max opus frame size
        }

        /// <summary>
        /// Processes raw PCM samples. Returns a list of full encoded packets.
        /// Each packet is formatted with the [2B Seq] [1B Codec] [8B Timestamp] [8B WallClock] [N bytes Data] header.
        /// </summary>
        public List<byte[]> Process(short[] pcm, int samplesCount, long wallClock)
        {
            var results = new List<byte[]>();

            // 1. Add to accumulator
            if (_accumulatedSamples + samplesCount > _accumulator.Length)
            {
                // Safety: Avoid overflow by resetting or shifting if needed.
                // In a perfect world, we'd never hit this if real-time constraints are met.
                _accumulatedSamples = 0;
            }
            Array.Copy(pcm, 0, _accumulator, _accumulatedSamples, samplesCount);
            _accumulatedSamples += samplesCount;

            // 2. Extract and encode full frames
            while (_accumulatedSamples >= _samplesPerFrameInterleaved)
            {
                Array.Copy(_accumulator, 0, _packetPcm, 0, _samplesPerFrameInterleaved);

                int encodedLen = _opusEncoder.EncodeTo(_packetPcm, 0, _opusOutBuffer);
                if (encodedLen > 0)
                {
                    byte[] packet = new byte[19 + encodedLen];
                    packet[0] = (byte)(_seqNum >> 8);
                    packet[1] = (byte)(_seqNum & 0xFF);
                    packet[2] = CODEC_AUDIO;
                    
                    BitConverter.TryWriteBytes(new Span<byte>(packet, 3, 8), _timestampSamples);
                    BitConverter.TryWriteBytes(new Span<byte>(packet, 11, 8), wallClock);
                    Buffer.BlockCopy(_opusOutBuffer, 0, packet, 19, encodedLen);

                    results.Add(packet);
                    _seqNum++;
                    _timestampSamples += _framesPerPacket;
                }

                // Shift accumulator
                _accumulatedSamples -= _samplesPerFrameInterleaved;
                if (_accumulatedSamples > 0)
                {
                    Array.Copy(_accumulator, _samplesPerFrameInterleaved, _accumulator, 0, _accumulatedSamples);
                }
            }

            return results;
        }

        public void Dispose()
        {
            // _opusEncoder should be disposed by the owner?
            // Usually, if we didn't create it, we don't dispose it.
        }
    }
}
