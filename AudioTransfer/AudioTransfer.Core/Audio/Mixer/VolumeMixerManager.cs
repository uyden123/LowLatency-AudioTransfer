using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AudioTransfer.Core.Logging;
using System.Runtime.Versioning;

namespace AudioTransfer.Core.Audio.Mixer;

public class AudioSessionModel
{
    public uint Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public float Volume { get; set; }
    public bool Mute { get; set; }
    public string? Icon { get; set; } // Base64 png, null means not included
}

[SupportedOSPlatform("windows")]
public class VolumeMixerManager : IDisposable
{
    private static readonly Lazy<VolumeMixerManager> _instance = new(() => new VolumeMixerManager());
    public static VolumeMixerManager Instance => _instance.Value;

    private IMMDeviceEnumerator? _deviceEnumerator;
    private IMMDevice? _defaultDevice;
    private IAudioSessionManager2? _sessionManager;
    private SessionNotificationSink? _sessionNotificationSink;

    private readonly ConcurrentDictionary<uint, SessionControlContext> _sessions = new();
    private readonly object _lock = new();
    private bool _isDisposed;

    public event Action<List<AudioSessionModel>>? OnSessionsUpdated;
    public event Action<AudioSessionModel>? OnSessionChanged;

    private VolumeMixerManager()
    {
    }

    public void Start()
    {
        try
        {
            Type? enumeratorType = Type.GetTypeFromCLSID(new Guid(CoreAudioConstants.MMDeviceEnumeratorGuid));
            if (enumeratorType == null)
            {
                CoreLogger.Instance.Log("[VolumeMixer] Could not get MMDeviceEnumerator type.");
                return;
            }

            _deviceEnumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(enumeratorType);
            if (_deviceEnumerator == null)
                return;

            RefreshDeviceAndSessions();
        }
        catch (Exception ex)
        {
            CoreLogger.Instance.LogError("[VolumeMixer] Start failed", ex);
        }
    }

    public void RefreshDeviceAndSessions()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            CleanupSessions();

            if (_deviceEnumerator == null) return;
            
            int hr = _deviceEnumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out _defaultDevice);
            if (hr != 0 || _defaultDevice == null)
            {
                CoreLogger.Instance.Log($"[VolumeMixer] No default render device found (HR={hr:X8})");
                return;
            }

            Guid IID_IAudioSessionManager2 = new Guid(CoreAudioConstants.IAudioSessionManager2Guid);
            hr = _defaultDevice.Activate(ref IID_IAudioSessionManager2, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out IntPtr sessionManagerPtr);
            if (hr != 0 || sessionManagerPtr == IntPtr.Zero)
            {
                CoreLogger.Instance.Log($"[VolumeMixer] Failed to activate session manager (HR={hr:X8})");
                return;
            }

            _sessionManager = (IAudioSessionManager2)Marshal.GetObjectForIUnknown(sessionManagerPtr);
            Marshal.Release(sessionManagerPtr);

            _sessionNotificationSink = new SessionNotificationSink(this);
            _sessionManager.RegisterSessionNotification(_sessionNotificationSink);

            hr = _sessionManager.GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
            if (hr == 0 && sessionEnum != null)
            {
                try
                {
                    hr = sessionEnum.GetCount(out int count);
                    if (hr == 0)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            if (sessionEnum.GetSession(i, out IAudioSessionControl2 control) == 0 && control != null)
                            {
                                AddSession(control);
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(sessionEnum);
                }
            }

            BroadcastFullState();
        }
    }

    public void HandleSessionCreated(IAudioSessionControl2 NewSession)
    {
        if (NewSession == null) return;
        AddSession(NewSession);
        BroadcastFullState();
    }

    private void AddSession(IAudioSessionControl2 control)
    {
        uint pid = 0;
        try
        {
            if (control.GetState(out AudioSessionState state) == 0 && state == AudioSessionState.AudioSessionStateExpired)
            {
                Marshal.ReleaseComObject(control);
                return;
            }

            if (control.GetProcessId(out pid) != 0 || pid == 0)
            {
                Marshal.ReleaseComObject(control);
                return;
            }

            if (control.IsSystemSoundsSession() == 0) // S_OK means it is system sounds
            {
                Marshal.ReleaseComObject(control);
                return;
            }

            var context = new SessionControlContext(pid, control, this);
            if (_sessions.TryAdd(pid, context))
            {
                context.PopulateInfo();
            }
            else
            {
                context.Dispose();
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Instance.Log($"[VolumeMixer] Failed to add session for PID {pid}: {ex.Message}");
            try { Marshal.ReleaseComObject(control); } catch { }
        }
    }

    public void RemoveSession(uint pid)
    {
        if (_sessions.TryRemove(pid, out var context))
        {
            // Defer disposal to avoid deadlocks/crashes if called from a COM callback
            _ = Task.Run(() => {
                try { context.Dispose(); } catch { }
                BroadcastFullState();
            });
        }
    }

    public void NotifySessionVolumeChanged(uint pid)
    {
        if (_sessions.TryGetValue(pid, out var context))
        {
            var model = context.ToModel(false); // Never include icon in small volume updates
            if (model != null) OnSessionChanged?.Invoke(model);
        }
    }

    public AudioSessionModel? GetSessionInfo(uint pid, bool includeIcon)
    {
        if (_sessions.TryGetValue(pid, out var context))
            return context.ToModel(includeIcon);
        return null;
    }

    public string? GetSessionIcon(uint pid)
    {
        if (_sessions.TryGetValue(pid, out var context))
            return context.Base64Icon;
        return null;
    }

    public void SetVolume(uint pid, float volume)
    {
        if (_sessions.TryGetValue(pid, out var context))
        {
            context.SetVolume(volume);
        }
    }

    public void BroadcastFullState(bool includeIcons = false)
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            var list = new List<AudioSessionModel>();
            foreach (var ctx in _sessions.Values)
            {
                var model = ctx.ToModel(includeIcons);
                if (model != null) list.Add(model);
            }
            OnSessionsUpdated?.Invoke(list);
        }
    }

    private void CleanupSessions()
    {
        // Must be called under _lock
        try
        {
            if (_sessionManager != null && _sessionNotificationSink != null)
            {
                _sessionManager.UnregisterSessionNotification(_sessionNotificationSink);
            }
            foreach (var ctx in _sessions.Values)
            {
                ctx.Dispose();
            }
            _sessions.Clear();
            _sessionNotificationSink = null;
            if (_sessionManager != null) Marshal.ReleaseComObject(_sessionManager);
            _sessionManager = null;
            if (_defaultDevice != null) Marshal.ReleaseComObject(_defaultDevice);
            _defaultDevice = null;
        }
        catch (Exception ex)
        {
            CoreLogger.Instance.Log($"[VolumeMixer] Cleanup error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            CleanupSessions();
            if (_deviceEnumerator != null) Marshal.ReleaseComObject(_deviceEnumerator);
            _deviceEnumerator = null;
        }
    }
}

[SupportedOSPlatform("windows")]
internal class SessionNotificationSink : IAudioSessionNotification
{
    private readonly VolumeMixerManager _owner;
    public SessionNotificationSink(VolumeMixerManager owner) => _owner = owner;

    public int OnSessionCreated(IAudioSessionControl2 NewSession)
    {
        // COM reference NewSession must be managed carefully
        _owner.HandleSessionCreated(NewSession);
        return 0;
    }
}

[SupportedOSPlatform("windows")]
internal class SessionControlContext : IAudioSessionEvents, IDisposable
{
    private readonly IAudioSessionControl2 _control;
    private readonly ISimpleAudioVolume _volumeCtrl;
    private readonly VolumeMixerManager _owner;
    private bool _isDisposed;
    private readonly object _stateLock = new();

    public uint Pid { get; }
    public string Name { get; private set; } = string.Empty;
    public string Base64Icon { get; private set; } = string.Empty;

    public float Volume
    {
        get 
        {
            try 
            { 
                if (_volumeCtrl.GetMasterVolume(out float v) == 0) return v;
            }
            catch { }
            return 0;
        }
    }

    public bool Mute
    {
        get 
        {
            try 
            { 
                if (_volumeCtrl.GetMute(out bool m) == 0) return m;
            }
            catch { }
            return false;
        }
    }

    public SessionControlContext(uint pid, IAudioSessionControl2 control, VolumeMixerManager owner)
    {
        Pid = pid;
        _control = control;
        _volumeCtrl = (ISimpleAudioVolume)control;
        _owner = owner;
        _control.RegisterAudioSessionNotification(this);
    }

    public void PopulateInfo()
    {
        try
        {
            using var proc = Process.GetProcessById((int)Pid);
            Name = proc.MainWindowTitle;
            if (string.IsNullOrEmpty(Name))
                Name = proc.ProcessName;

            string? fullPath = GetProcessPath(proc);
            if (fullPath != null && File.Exists(fullPath))
            {
                ExtractIcon(fullPath);
            }
            else
            {
                CoreLogger.Instance.Log($"[VolumeMixer] Could not find path for PID {Pid}");
            }
        }
        catch (Exception ex)
        {
            Name = "App_" + Pid;
            CoreLogger.Instance.Log($"[VolumeMixer] PopulateInfo error for PID {Pid}: {ex.Message}");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, System.Text.StringBuilder lpExeName, ref int lpdwSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private string? GetProcessPath(Process proc)
    {
        try
        {
            return proc.MainModule?.FileName;
        }
        catch 
        { 
            // Fallback for access denied on MainModule
            try
            {
                IntPtr hProc = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)proc.Id);
                if (hProc != IntPtr.Zero)
                {
                    try
                    {
                        var sb = new System.Text.StringBuilder(1024);
                        int size = sb.Capacity;
                        if (QueryFullProcessImageName(hProc, 0, sb, ref size))
                            return sb.ToString();
                    }
                    finally { CloseHandle(hProc); }
                }
            }
            catch { }
            return null; 
        }
    }

    private void ExtractIcon(string path)
    {
        try
        {
            using Icon? icon = Icon.ExtractAssociatedIcon(path);
            if (icon != null)
            {
                using Bitmap bmp = icon.ToBitmap();
                using MemoryStream ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                Base64Icon = Convert.ToBase64String(ms.ToArray());
                CoreLogger.Instance.Log($"[VolumeMixer] Extracted icon for {Path.GetFileName(path)} ({Base64Icon.Length} bytes)");
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Instance.Log($"[VolumeMixer] Icon extraction failed for {path}: {ex.Message}");
        }
    }

    public AudioSessionModel? ToModel(bool includeIcon)
    {
        lock (_stateLock)
        {
            if (_isDisposed) return null;
            return new AudioSessionModel
            {
                Pid = Pid,
                Name = Name,
                Icon = includeIcon ? Base64Icon : null,
                Volume = this.Volume,
                Mute = this.Mute
            };
        }
    }

    public void SetVolume(float vol)
    {
        lock (_stateLock)
        {
            if (_isDisposed) return;
            Guid empty = Guid.Empty;
            _volumeCtrl.SetMasterVolume(Math.Max(0, Math.Min(1, vol)), ref empty);
        }
    }

    public int OnDisplayNameChanged(string NewDisplayName, ref Guid EventContext) => 0;
    public int OnIconPathChanged(string NewIconPath, ref Guid EventContext) => 0;

    private long _lastNotifyTick = 0;
    public int OnSimpleVolumeChanged(float NewVolume, bool NewMute, ref Guid EventContext)
    {
        // Throttle updates to max 10 per second to avoid flooding the network
        long now = DateTime.UtcNow.Ticks;
        if (now - _lastNotifyTick > TimeSpan.TicksPerSecond / 10)
        {
            _lastNotifyTick = now;
            _owner.NotifySessionVolumeChanged(Pid);
        }
        return 0;
    }

    public int OnChannelVolumeChanged(int ChannelCount, IntPtr NewChannelVolumeArray, uint ChangedChannel, ref Guid EventContext) => 0;
    public int OnGroupingParamChanged(ref Guid NewGroupingParam, ref Guid EventContext) => 0;

    public int OnStateChanged(AudioSessionState NewState)
    {
        if (NewState == AudioSessionState.AudioSessionStateExpired)
        {
            Log($"[VolumeMixer] Session expired: PID {Pid}");
            _owner.RemoveSession(Pid);
        }
        return 0;
    }

    public int OnSessionDisconnected(AudioSessionDisconnectReason DisconnectReason)
    {
        Log($"[VolumeMixer] Session disconnected: PID {Pid}, Reason: {DisconnectReason}");
        _owner.RemoveSession(Pid);
        return 0;
    }

    private void Log(string msg) => CoreLogger.Instance.Log(msg);

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            try
            {
                if (_control != null)
                {
                    _control.UnregisterAudioSessionNotification(this);
                    Marshal.ReleaseComObject(_control);
                }
            }
            catch (Exception ex)
            {
                CoreLogger.Instance.Log($"[VolumeMixer] Error disposing session PID {Pid}: {ex.Message}");
            }
        }
    }
}
