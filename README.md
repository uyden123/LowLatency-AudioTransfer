# Audio Transfer Over LAN (Root Project)

Dự án này cho phép truyền âm thanh chất lượng cao từ thiết bị Android sang máy tính Windows qua mạng nội bộ (LAN). Âm thanh sau đó có thể được sử dụng như một đầu vào micro ảo (Virtual Microphone) trên Windows.

## Cấu trúc dự án

Dự án bao gồm 3 thành phần chính:

1.  **[AudioOverLAN](./AudioOverLAN/) (Android App):** 
    -   Ứng dụng Android (viết bằng Java & C++/AAudio) để thu âm thanh và truyền đi qua giao thức mạng.
    -   Sử dụng AAudio để có độ trễ thấp nhất có thể.

2.  **[AudioTransfer](./AudioTransfer/) (Windows Receiver/Bridge):**
    -   Bao gồm phiên bản GUI (WPF) và CLI.
    -   Nhận dữ liệu âm thanh từ Android qua mạng và chuyển tiếp nó đến driver âm thanh ảo.
    -   Xử lý việc giải mã và đồng bộ hóa.

3.  **[VirtualMicDriver](./VirtualMicDriver/) (System Driver):**
    -   Driver âm thanh ảo cho Windows.
    -   Tạo ra một thiết bị Microphone ảo trong hệ thống để các ứng dụng khác (Zoom, Discord, OBS...) có thể sử dụng âm thanh được truyền đến.

## Cài đặt & Sử dụng

### 1. Cài đặt Virtual Microphone Driver
-   Truy cập thư mục `VirtualMicDriver`.
-   Sử dụng Visual Studio để build dự án hoặc cài đặt driver đã được build sẵn (nếu có).
-   Đảm bảo thiết bị "Virtual Microphone" xuất hiện trong Sound Settings của Windows.

### 2. Chạy ứng dụng Windows (AudioTransfer)
-   Mở giải pháp `AudioTransfer.slnx` hoặc chạy bản GUI trong thư mục `AudioTransfer.GUI`.
-   Ứng dụng sẽ bắt đầu lắng nghe kết nối từ Android.

### 3. Chạy ứng dụng Android (AudioOverLAN)
-   Cài đặt ứng dụng lên điện thoại Android.
-   Nhập địa chỉ IP của máy tính Windows.
-   Nhấn "Start" để bắt đầu truyền âm thanh.

## Yêu cầu hệ thống
-   **Android:** Android 8.1 (Oreo) trở lên (khuyên dùng để hỗ trợ AAudio).
-   **Windows:** Windows 10/11.
-   Cả hai thiết bị phải ở trong cùng một mạng LAN.

## Giấy phép
Xác định thông tin bản quyền hoặc giấy phép tại đây (nếu có).
