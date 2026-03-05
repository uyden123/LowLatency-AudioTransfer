---
page_type: sample
description: "The Microsoft Simple Audio Sample Device Driver shows how to develop a simple WDM audio driver that exposes support for two basic audio devices (a speaker and microphone array)."
languages:
- cpp
products:
- windows
- windows-wdk
---

# VirtualMicDriver (Simple Audio Sample)

This is a WDM audio driver that creates a virtual microphone device on Windows. It is based on the Microsoft Simple Audio Sample.

[English](#english) | [Tiếng Việt](#tiếng-việt)

---

<a name="english"></a>
## English Introduction

The VirtualMicDriver allows the system to recognize a "Virtual Microphone" device. In this project, it serves as the final destination for audio streamed from Android, allowing other Windows apps (Discord, Zoom, etc.) to use that audio as a recording source.

### Key Features
-   **WDM Architecture:** Standard Windows Driver Model.
-   **WaveRT Support:** Modern Windows audio streaming.
-   **Virtual Device:** No physical hardware required.

### Build & Install
1.  **Requirement:** Install [Windows Driver Kit (WDK)](https://docs.microsoft.com/windows-hardware/drivers/download-the-wdk).
2.  **Build:** Open `VirtualMic.sln` in Visual Studio and build the "Package" project.
3.  **Test Signing:** Run `bcdedit /set TESTSIGNING ON` as Administrator and reboot.
4.  **Install:** Use `devcon.exe` (from WDK):
    `devcon install SimpleAudioSample.inf Root\SimpleAudioSample`

---

<a name="tiếng-việt"></a>
## Tiếng Việt (Giới thiệu)

VirtualMicDriver là một driver âm thanh WDM giúp tạo ra một thiết bị "Microphone ảo" trên Windows. Trong dự án này, nó đóng vai trò là điểm cuối nhận âm thanh truyền từ Android, giúp các ứng dụng Windows khác (Discord, Zoom, v.v.) có thể sử dụng âm thanh đó như một nguồn thu âm.

### Tính năng Chính
-   **Kiến trúc WDM:** Mô hình Driver chuẩn của Windows.
-   **Hỗ trợ WaveRT:** Công nghệ truyền dẫn âm thanh hiện đại trên Windows.
-   **Thiết bị ảo:** Không yêu cầu phần cứng vật lý.

### Xây dựng & Cài đặt
1.  **Yêu cầu:** Cài đặt [Windows Driver Kit (WDK)](https://docs.microsoft.com/windows-hardware/drivers/download-the-wdk).
2.  **Build:** Mở `VirtualMic.sln` trong Visual Studio và build dự án "Package".
3.  **Bật Test Signing:** Chạy `bcdedit /set TESTSIGNING ON` với quyền Administrator và khởi động lại máy.
4.  **Cài đặt:** Sử dụng công cụ `devcon.exe` (đi kèm WDK):
    `devcon install SimpleAudioSample.inf Root\SimpleAudioSample`
