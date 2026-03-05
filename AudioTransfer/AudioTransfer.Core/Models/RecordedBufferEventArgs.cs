

namespace AudioTransfer.Core.Models
{
    public sealed class RecordedBufferEventArgs : EventArgs
    {
        public RecordedBufferEventArgs(byte[] buffer, int bytesRecorded, AudioFormat format)
        {
            Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            BytesRecorded = bytesRecorded;
            Format = format ?? throw new ArgumentNullException(nameof(format));
        }

        public byte[] Buffer { get; }
        public int BytesRecorded { get; }
        public AudioFormat Format { get; }
    }
}