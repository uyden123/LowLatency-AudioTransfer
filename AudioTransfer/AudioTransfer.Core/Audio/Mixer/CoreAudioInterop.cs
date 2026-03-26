using System;
using System.Runtime.InteropServices;

namespace AudioTransfer.Core.Audio.Mixer;

// Enums and Guid's
public static class CoreAudioConstants
{
    public const string MMDeviceEnumeratorGuid = "bcde0395-e52f-467c-8e3d-c4579291692e";
    public const string IMMDeviceEnumeratorGuid = "A95664D2-9614-4F35-A746-DE8DB63617E6";
    public const string IAudioSessionManager2Guid = "77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F";
    public const string IAudioSessionControl2Guid = "bfb7ff88-7239-4fc9-8fa2-07c950be9c6d";
    public const string ISimpleAudioVolumeGuid = "87CE5498-68D6-44E5-9215-6DA47EF883D8";
    public const string IAudioSessionEnumeratorGuid = "E2F5BB11-0570-40CA-ACDD-3AA01277DEE8";
    public const string IAudioSessionEventsGuid = "24918CE3-0388-4a2a-9697-293b09009360";
}

public enum EDataFlow
{
    eRender,
    eCapture,
    eAll,
    EDataFlow_enum_count
}

public enum ERole
{
    eConsole,
    eMultimedia,
    eCommunications,
    ERole_enum_count
}

public enum AudioSessionState
{
    AudioSessionStateInactive = 0,
    AudioSessionStateActive = 1,
    AudioSessionStateExpired = 2
}

public enum AudioSessionDisconnectReason
{
    DisconnectReasonDeviceRemoval = 0,
    DisconnectReasonServerShutdown = 1,
    DisconnectReasonFormatChanged = 2,
    DisconnectReasonSessionLogoff = 3,
    DisconnectReasonSessionDisconnected = 4,
    DisconnectReasonExclusiveModeOverride = 5
}

[Guid(CoreAudioConstants.IMMDeviceEnumeratorGuid), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr ppDevices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
}

[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid id, int clsCtx, IntPtr activationParams, out IntPtr interfacePointer);
    [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
    [PreserveSig] int GetId(out IntPtr ppstrId);
    [PreserveSig] int GetState(out int pdwState);
}

[Guid(CoreAudioConstants.IAudioSessionManager2Guid), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr AudioSessionGuid, int StreamFlags, out IntPtr SessionControl);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr AudioSessionGuid, int StreamFlags, out IntPtr AudioVolume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
    [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification SessionNotification);
    [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification SessionNotification);
    [PreserveSig] int RegisterDuckNotification(string sessionID, IntPtr duckNotification);
    [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
}

[Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionNotification
{
    [PreserveSig] int OnSessionCreated(IAudioSessionControl2 NewSession);
}

[Guid(CoreAudioConstants.IAudioSessionEnumeratorGuid), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int SessionCount);
    [PreserveSig] int GetSession(int SessionIndex, out IAudioSessionControl2 Session);
}

[Guid(CoreAudioConstants.IAudioSessionControl2Guid), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionControl2
{
    // IAudioSessionControl methods
    [PreserveSig] int GetState(out AudioSessionState pRetVal);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, [In] ref Guid EventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, [In] ref Guid EventContext);
    [PreserveSig] int GetGroupingParam(out Guid pRetVal);
    [PreserveSig] int SetGroupingParam([In] ref Guid Override, [In] ref Guid EventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IAudioSessionEvents NewNotifications);
    [PreserveSig] int UnregisterAudioSessionNotification(IAudioSessionEvents NewNotifications);
    // IAudioSessionControl2 methods
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    [PreserveSig] int GetProcessId(out uint pRetVal);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[Guid(CoreAudioConstants.ISimpleAudioVolumeGuid), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float fLevel, [In] ref Guid EventContext);
    [PreserveSig] int GetMasterVolume(out float pfLevel);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, [In] ref Guid EventContext);
    [PreserveSig] int GetMute(out bool pbMute);
}

[Guid(CoreAudioConstants.IAudioSessionEventsGuid), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionEvents
{
    [PreserveSig] int OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string NewDisplayName, [In] ref Guid EventContext);
    [PreserveSig] int OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string NewIconPath, [In] ref Guid EventContext);
    [PreserveSig] int OnSimpleVolumeChanged(float NewVolume, [MarshalAs(UnmanagedType.Bool)] bool NewMute, [In] ref Guid EventContext);
    [PreserveSig] int OnChannelVolumeChanged(int ChannelCount, IntPtr NewChannelVolumeArray, uint ChangedChannel, [In] ref Guid EventContext);
    [PreserveSig] int OnGroupingParamChanged([In] ref Guid NewGroupingParam, [In] ref Guid EventContext);
    [PreserveSig] int OnStateChanged(AudioSessionState NewState);
    [PreserveSig] int OnSessionDisconnected(AudioSessionDisconnectReason DisconnectReason);
}
