# NMKAcad — AutoCAD .NET Plugin Overview

> Tài liệu tóm tắt kiến trúc, chức năng, cấu trúc mã nguồn và hướng dẫn phát triển cho module **NMKAcad**.  
> Dùng để cung cấp nhanh ngữ cảnh (context) khi làm việc trong các phiên hội thoại tiếp theo.

---

## 1. Thông tin chung

- **Đường dẫn thư mục:** `d:\MCP\NMKRevit\NMKAcad`
- **Tập tin dự án:** `NMKAcad\NMKAcad.csproj` (thuộc Solution `NMKRevit.sln`)
- **Nền tảng / Target Framework:** `.NET Framework 4.8` (`net48`), Kiến trúc `x64`
- **AutoCAD API:** `AutoCAD.NET` (Core, Model) version `20.1.0` (tương thích AutoCAD 2017+)
- **Thư viện UI / MVVM:**
  - `CommunityToolkit.Mvvm` (8.4.0)
  - `Material.Icons.WPF` (2.4.1)
  - WPF lồng trong AutoCAD WinForms `PaletteSet` via `ElementHost`

---

## 2. Mục đích & Chức năng chính

`NMKAcad` là công cụ hỗ trợ trên AutoCAD phục vụ chuẩn bị bản vẽ và bóc tách dữ liệu cho Revit/Excel:

### 2.1. Lệnh gọi công cụ: `NMKWBLOCK`
- Đăng ký lệnh AutoCAD qua `[CommandMethod("NMKWBLOCK", CommandFlags.Session)]`.
- Hiển thị bảng điều khiển **NMK Wblock Palette** cố định hoặc dock bên cạnh màn hình AutoCAD.

### 2.2. Chức năng Wblock (Xuất khối đối tượng ra file DWG)
- **Cơ chế đặt tên:** `Prefix` + `Main` + `Suffix` (ví dụ: `BEAM_` + `B1` + `_01` $\rightarrow$ `BEAM_B1_01.dwg`).
- **Danh sách lựa chọn (ComboBox từ file CSV):**
  - Prefix, Main, Suffix sử dụng `ComboBox` có thể nhập tự do (`IsEditable="True"`) hoặc chọn từ danh sách mẫu.
  - Danh sách được đọc từ file `wblock_options.csv` (Cột 1: Prefix, Cột 2: Main, Cột 3: Suffix).
  - Có nút **Reload CSV** để cập nhật ngay danh sách sau khi chỉnh sửa file CSV mà không cần khởi động lại AutoCAD.
  - Có nút **Edit CSV** để mở nhanh file CSV bằng ứng dụng mặc định (Excel/Notepad).
- **Tùy chọn tự động tăng Suffix (Checkbox "Tự động tăng"):**
  - Cho phép người dùng bật/tắt cơ chế tự động tăng Suffix sau mỗi lần xuất DWG thành công (trạng thái lưu vào User Settings giữa các phiên làm việc).
  - Khi bật: tự động nhảy số/chữ (`SuffixIncrementer`, ví dụ: `01` $\rightarrow$ `02`, `A` $\rightarrow$ `B`, `Z` $\rightarrow$ `AA`, `Beam1` $\rightarrow$ `Beam2`).
  - Khi tắt: giữ nguyên giá trị Suffix hiện hành.
- **Gốc tọa độ xuất:** Xuất về gốc `Point3d.Origin` (0,0,0) chuẩn hóa cho việc link/import vào Revit.
- **Tự động ẩn/hiện Palette:** Tạm ẩn focus palette khi người dùng chọn đối tượng trên viewport và phục hồi sau khi chọn xong.

### 2.3. Chức năng Get Text (Trích xuất Text sang Excel)
- Chọn vùng chứa đối tượng `TEXT` và `MTEXT`.
- Tự động nhận diện tọa độ hình học `GeometricExtents` và **sắp xếp theo thứ tự đọc bảng**:
  1. Ưu tiên từ trên xuống dưới ($Y$ giảm dần).
  2. Cùng hàng thì xếp từ trái sang phải ($X$ tăng dần).
- Làm phẳng nội dung nhiều dòng (loại bỏ newline thừa).
- Tự động copy trực tiếp vào **Windows Clipboard** để người dùng chỉ cần `Ctrl + V` vào Excel.

### 2.4. Lưu trữ cấu hình (User Settings)
- Tự động lưu `SaveFolder`, `Prefix`, `Main`, `Suffix` vào `Properties.Settings.Default` giữa các phiên làm việc.
- Tự động cập nhật tên bản vẽ hiện hành (`CurrentDrawing`) khi chuyển đổi giữa các tab file DWG (`DocumentActivated`).

---

## 3. Cấu trúc Source Code

```text
NMKAcad/
├── NMKAcad.csproj                 # Cấu hình build SDK, net48, package refs
├── NmkAcadApp.cs                  # Entry point (IExtensionApplication), AssemblyResolve
├── wblock_options.csv             # Danh sách tùy chọn mẫu (Prefix, Main, Suffix)
├── Commands/
│   └── NmkWblockCommand.cs        # CommandMethod "NMKWBLOCK"
├── Services/
│   ├── WblockPalette.cs           # Khởi tạo PaletteSet, WPF Host, xử lý ẩn/hiện khi pick
│   ├── WblockService.cs           # Logic Wblock sang file DWG, sanitize tên file
│   ├── CsvOptionsService.cs       # Đọc/ghi và parse file wblock_options.csv
│   ├── TextCollectService.cs      # Lọc Text/MText, sắp xếp tọa độ Y/X, copy clipboard
│   ├── SuffixIncrementer.cs       # Thuật toán tăng suffix (số đệm 0, chữ cái, chuỗi hỗn hợp)
│   └── AcadCommandRunner.cs       # Điều phối chạy action trong AutoCAD Command Context
├── ViewModels/
│   └── WblockViewModel.cs         # MVVM ViewModel: xử lý state, commands, binding, settings, reload CSV
├── Views/
│   ├── WblockView.xaml            # XAML giao diện Dark Theme (ComboBox, Buttons)
│   ├── WblockView.xaml.cs         # Code-behind của View
│   └── NmkTheme.xaml              # ResourceDictionary màu sắc, style Dark theme (ComboBox, Button, TextBox)
└── Properties/
    ├── AssemblyInfo.cs
    └── Settings.cs                # User settings (Folder, Prefix, Main, Suffix)
```

---

## 4. Hướng dẫn Build & Debug

### 4.1. Lệnh Build
```powershell
dotnet build NMKAcad/NMKAcad.csproj -c Debug
```
- Output DLL: `NMKAcad\bin\Debug\NMKAcad.dll`

### 4.2. Lưu ý khi Build / Lock File
- Nếu AutoCAD đang mở và đã `NETLOAD NMKAcad.dll`, file DLL trong `bin\Debug\` sẽ bị tiến trình `acad.exe` khóa (lock).
- Khi sửa code và build lại:
  - Cần đóng AutoCAD hoặc dùng tiện ích reload assembly chuyên dụng, hoặc build ra cấu hình khác.

---

## 5. Định hướng mở rộng tiềm năng
- Bổ sung chức năng xuất text theo bảng/cột đa tiêu chí.
- Bổ sung tính năng liên kết thông số thép giữa các chi tiết block CAD sang bảng thuộc tính CSV của module `NMKRebar`.
