# Audio Transfer Over LAN (Root Project)

[Tiếng Việt](#tiếng-việt) | [English](#english)

---

<a name="english"></a>
## English

This project allows high-quality audio streaming from an Android device to a Windows computer over a Local Area Network (LAN). The audio can then be used as a Virtual Microphone input on Windows.

### Project Structure

The project consists of three main components:

1.  **[AudioOverLAN](./AudioOverLAN/) (Android App):** 
    -   Android application (written in Java & C++/AAudio) to capture and stream audio over the network.
    -   Utilizes AAudio for the lowest possible latency.

2.  **[AudioTransfer](./AudioTransfer/) (Windows Receiver - C#/.NET):**
    -   Includes both GUI (WPF) and CLI versions.
    -   Standard Windows receiver using C# and .NET technology.

3.  **[AudioTransfer.Qt](./AudioTransfer.Qt/) (Advanced Windows Receiver - C++/Qt):**
    -   High-performance receiver using C++ and Qt Quick (QML) for lower latency and modern UI.
    -   Ideal for power users and professional-grade performance.

4.  **[VirtualMicDriver](./VirtualMicDriver/) (System Driver):**
    -   Virtual audio driver for Windows.
    -   Creates a virtual Microphone device in the system for other applications (Zoom, Discord, OBS...) to use.

### Installation & Usage

#### 1. Install Virtual Microphone Driver
-   Navigate to the `VirtualMicDriver` directory.
-   Use Visual Studio to build the project or install a pre-built driver.
-   Ensure "Virtual Microphone" appears in Windows Sound Settings.

#### 2. Run Windows Application (AudioTransfer)
-   Open the `AudioTransfer.slnx` solution or run the GUI version in the `AudioTransfer.GUI` folder.
-   The application will start listening for connections from Android.

#### 3. Run Android Application (AudioOverLAN)
-   Install the app on your Android device.
-   Enter the IP address of your Windows PC.
-   Press "Start" to begin audio transmission.

### System Requirements
-   **Android:** Android 8.1 (Oreo) or higher (recommended for AAudio support).
-   **Windows:** Windows 10/11.
-   Both devices must be on the same LAN.

---

<a name="tiếng-việt"></a>
## Tiếng Việt

Dự án này cho phép truyền âm thanh chất lượng cao từ thiết bị Android sang máy tính Windows qua mạng nội bộ (LAN). Âm thanh sau đó có thể được sử dụng như một đầu vào micro ảo (Virtual Microphone) trên Windows.

### Cấu trúc dự án

Dự án bao gồm 3 thành phần chính:

1.  **[AudioOverLAN](./AudioOverLAN/) (Android App):** 
    -   Ứng dụng Android (viết bằng Java & C++/AAudio) để thu âm thanh và truyền đi qua giao thức mạng.
    -   Sử dụng AAudio để có độ trễ thấp nhất có thể.

2.  **[AudioTransfer](./AudioTransfer/) (Windows Receiver - C#/.NET):**
    -   Bao gồm phiên bản GUI (WPF) và CLI.
    -   Bộ thu tiêu chuẩn sử dụng công nghệ C# và .NET.

3.  **[AudioTransfer.Qt](./AudioTransfer.Qt/) (Advanced Windows Receiver - C++/Qt):**
    -   Bộ thu hiệu năng cao sử dụng C++ và Qt Quick (QML) để tối ưu độ trễ và giao diện.
    -   Phù hợp cho các tác vụ chuyên nghiệp đòi hỏi hiệu năng cực hạn.

4.  **[VirtualMicDriver](./VirtualMicDriver/) (System Driver):**
    -   Driver âm thanh ảo cho Windows.
    -   Tạo ra một thiết bị Microphone ảo trong hệ thống để các ứng dụng khác (Zoom, Discord, OBS...) có thể sử dụng âm thanh được truyền đến.

### Cài đặt & Sử dụng

#### 1. Cài đặt Virtual Microphone Driver
-   Truy cập thư mục `VirtualMicDriver`.
-   Sử dụng Visual Studio để build dự án hoặc cài đặt driver đã được build sẵn (nếu có).
-   Đảm bảo thiết bị "Virtual Microphone" xuất hiện trong Sound Settings của Windows.

#### 2. Chạy ứng dụng Windows (AudioTransfer)
-   Mở giải pháp `AudioTransfer.slnx` hoặc chạy bản GUI trong thư mục `AudioTransfer.GUI`.
-   Ứng dụng sẽ bắt đầu lắng nghe kết nối từ Android.

#### 3. Chạy ứng dụng Android (AudioOverLAN)
-   Cài đặt ứng dụng lên điện thoại Android.
-   Nhập địa chỉ IP của máy tính Windows.
-   Nhấn "Start" để bắt đầu truyền âm thanh.

### Yêu cầu hệ thống
-   **Android:** Android 8.1 (Oreo) trở lên (khuyên dùng để hỗ trợ AAudio).
-   **Windows:** Windows 10/11.
-   Cả hai thiết bị phải ở trong cùng một mạng LAN.
