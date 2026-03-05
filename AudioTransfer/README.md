# AudioTransfer (Windows Receiver/Bridge)

Windows application that receives audio packets from an Android device over the network and forwards them to a Virtual Microphone driver.

## Project Modules
-   **AudioTransfer.GUI:** A WPF application for visual setup and monitoring.
-   **AudioTransfer.CLI:** Command-line interface for more efficient or professional usage.
-   **AudioTransfer.Core:** Shared logic for network handling and audio processing.

## Components
-   **Network Receiver:** Listens for UDP/TCP packets containing audio data.
-   **Audio Decoder:** Decodes and prepares audio for output.
-   **Virtual Mic Bridge:** Connects to the system's Virtual Mic Driver.

## Build Instructions
1. Open `AudioTransfer.slnx` or any `.sln` file in Visual Studio.
2. Ensure you have the `.NET SDK` installed (version 6.0 or higher).
3. Build the solution and run the GUI or CLI.

---

# AudioTransfer (Windows Receiver/Bridge)

Ứng dụng Windows nhận các gói tin âm thanh từ thiết bị Android qua mạng và chuyển tiếp chúng tới driver Virtual Microphone.

## Các Dự án con
-   **AudioTransfer.GUI:** Ứng dụng WPF cung cấp giao diện trực quan để thiết lập.
-   **AudioTransfer.CLI:** Giao diện dòng lệnh cho các tác vụ cần hiệu năng cao.
-   **AudioTransfer.Core:** Logic dùng chung để xử lý mạng và âm thanh.

## Thành phần Chính
-   **Bộ thu Mạng (Network Receiver):** Lắng nghe các gói tin UDP/TCP chứa dữ liệu âm thanh.
-   **Bộ giải mã Âm thanh (Audio Decoder):** Giải mã và chuẩn bị âm thanh để xuất ra.
-   **Cầu nối Micro ảo (Virtual Mic Bridge):** Kết nối với driver Virtual Mic đã cài đặt trên hệ thống.
