
namespace AudioTransfer.Core.Models
{
    public sealed class AudioFormat
    {
        public AudioFormat(int sampleRate, int channels, int bitsPerSample)
        {
            SampleRate = sampleRate;
            Channels = channels;
            BitsPerSample = bitsPerSample;
        }

        public int SampleRate { get; }
        public int Channels { get; }
        public int BitsPerSample { get; }
        public int BytesPerSample => (BitsPerSample / 8) * Channels;
    }
}
