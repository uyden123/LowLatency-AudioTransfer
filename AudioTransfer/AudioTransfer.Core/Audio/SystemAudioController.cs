using System;
using System.Runtime.InteropServices;
using AudioTransfer.Core.Audio;

namespace AudioTransfer.Core.Audio
{
    /// <summary>
    /// Manages system-level audio controls (Mute, Volume) using WASAPI IMMDevice.
    /// </summary>
    public class SystemAudioController
    {
        private readonly Action<string> _logger;

        public SystemAudioController(Action<string> logger)
        {
            _logger = logger ?? (m => { });
        }

        public void SetMute(bool muted)
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);

                var iid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
                IntPtr volumePtr;
                device.Activate(ref iid, 0, IntPtr.Zero, out volumePtr);

                try
                {
                    var volume = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(volumePtr);
                    Guid context = Guid.Empty;
                    volume.SetMute(muted, ref context);
                    _logger($"[SystemAudio] Mute set to: {muted}");
                }
                finally
                {
                    Marshal.Release(volumePtr);
                }
            }
            catch (Exception ex)
            {
                _logger($"[SystemAudio] Error setting mute: {ex.Message}");
            }
        }
    }
}
