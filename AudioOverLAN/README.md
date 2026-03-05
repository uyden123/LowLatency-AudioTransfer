# AudioOverLAN (Android App)

Android application designed to capture audio from your device and stream it over the network to a Windows receiver.

## Features
-   **Low Latency:** Uses Android's **AAudio** API for high-performance audio.
-   **JNI Integration:** Core recording logic implemented in C++ for maximum efficiency.
-   **Configurable:** Easily set the target IP address and port.

## Technical Details
-   **Language:** Java (UI & Services), C++ (Audio Engine).
-   **Minimum SDK:** Android 8.1 (Orea) / API Level 27.
-   **Network:** Uses UDP/TCP (depending on configuration) to transmit audio packets.

## Build Instructions
1. Open this folder in **Android Studio**.
2. Sync Project with Gradle Files.
3. Build and install to your Android device.

---

# AudioOverLAN (Ứng dụng Android)

Ứng dụng Android dùng để thu âm thanh từ thiết bị và truyền qua mạng tới bộ thu trên Windows.

## Tính năng
-   **Độ trễ thấp:** Sử dụng **AAudio** API của Android để có hiệu năng cao nhất.
-   **Tích hợp JNI:** Logic thu âm cốt lõi được viết bằng C++ để tối ưu hóa.
-   **Cấu hình linh hoạt:** Dễ dàng thiết lập địa chỉ IP và cổng đích.

## Thông tin kỹ thuật
-   **Ngôn ngữ:** Java (Giao diện & Dịch vụ), C++ (Engine âm thanh).
-   **Phiên bản Android tối thiểu:** Android 8.1 (Oreo) / API Level 27.
-   **Mạng:** Sử dụng UDP/TCP để truyền các gói tin âm thanh.
