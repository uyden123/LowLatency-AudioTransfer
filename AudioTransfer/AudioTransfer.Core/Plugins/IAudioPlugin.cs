namespace AudioTransfer.Core.Plugins
{
    /// <summary>
    /// Base interface for audio processing plugins.
    /// Allows modular modification of PCM data before it is encoded/sent.
    /// </summary>
    public interface IAudioPlugin
    {
        string Name { get; }
        bool IsEnabled { get; set; }
        
        /// <summary>
        /// Processes a buffer of PCM data.
        /// </summary>
        /// <param name="buffer">PCM samples (16-bit)</param>
        /// <param name="sampleRate">Frequency (e.g. 48000)</param>
        /// <param name="channels">Channel count (e.g. 2)</param>
        void Process(short[] buffer, int length, int sampleRate, int channels);

        /// <summary>
        /// Processes a "reverse" stream (far-end audio) for echo cancellation.
        /// Only relevant for plugins that implement AEC.
        /// </summary>
        void ProcessReverse(short[] buffer, int length, int sampleRate, int channels) { }
    }
}
