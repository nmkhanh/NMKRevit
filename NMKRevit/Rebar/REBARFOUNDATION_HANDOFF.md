# RebarFoundation — bàn giao để tiếp tục phát triển

## Mục tiêu

Command `RebarFoundation` chạy bằng Add-in Manager trong Revit 2026, không tạo Ribbon Panel.
Người dùng chọn file JSON, sau đó chọn mặt của một solid hình hộp chữ nhật thuộc `FamilyInstance` (gồm cả Model In-Place và loadable family) để tạo thép đài cọc/bệ móng.

## Đường dẫn quan trọng

- Source: `NMKRevit/NMKRevit/Rebar/RebarFoundation.cs`
- JSON mặc định: `NMKRevit/NMKRevit/Rebar/RebarFoundation.json`
- JSON Schema: `NMKRevit/NMKRevit/Rebar/RebarFoundation.schema.json`
- Project: `NMKRevit/NMKRevit/NMKRevit.csproj`
- DLL Revit 2026: `NMKRevit/NMKRevit/bin/Debug/2026/net80-windows/NMKRevit.dll`

Build:

```powershell
dotnet build NMKRevit\NMKRevit\NMKRevit.csproj -c D2026 --no-restore
```

## Quy tắc bắt buộc

- Tuyệt đối không dùng `Rebar.CreateFreeForm` hoặc FreeForm Rebar.
- Không truyền cung (`Arc`) vào API và JSON không hỗ trợ `arc3pt`.
- Tất cả hình dạng đầu vào chỉ được cấu tạo từ các đoạn `Line` liên tục.
- Tạo thép Shape-Driven bằng `Rebar.CreateFromCurves`.
- Revit được phép tự tạo bán kính uốn vật lý tại giao điểm các đoạn thẳng.
- Shape `polycurve` trong JSON luôn ưu tiên hơn hình dạng tự tính, nhưng chỉ chấp nhận segment `line`.
- Nếu JSON có dữ liệu rải cụ thể thì JSON luôn ưu tiên quy tắc mặc định.

## Bốn lớp thép

Thứ tự cao độ từ trên xuống dưới:

1. L1 — D19 mặc định
2. L2 — D22 mặc định
3. L3 — D35 mặc định
4. L4 — D38 mặc định

Phương thép mặc định:

- L1 và L4 cùng phương X.
- L2 và L3 cùng phương Y.

Cao độ:

- Tim L2 cách mặt trên 150 mm.
- Tim L3 cách đáy solid 225 mm.
- L1 nằm trên L2 theo đường kính thực tế.
- L4 nằm dưới L3 theo đường kính thực tế.

Hình dạng:

- L1/L2: ba đoạn thẳng `chân xuống — đoạn ngang — chân xuống`.
- Chiều dài chân L1/L2 lấy tự động từ HookType 90° tương thích.
- L3/L4: ba đoạn thẳng `chân lên — đoạn ngang — chân lên`.
- Hai chân L3/L4 kết thúc tại tim L2.

## RebarBarType

- Nếu type đúng `typeName` trong JSON đã tồn tại thì dùng trực tiếp, không kiểm tra `CSSDiameter`.
- Nếu chưa tồn tại, tìm type mẫu tên `CSS{diameter}`, ví dụ `CSS19`, rồi duplicate thành tên trong JSON.
- Hình học và khoảng cách lớp dùng `BarModelDiameter` thực tế của type đã chọn.
- Không coi `CSSDiameter` là parameter. `CSS19`, `CSS22`... chỉ là tên type mẫu.

## Cây móc L2–L3

- D16 mặc định, có thể đổi bằng JSON.
- Hình dạng gồm đúng ba đoạn thẳng:
  - chân dài từ đáy L3 đi lên;
  - đoạn ngang phía trên L2;
  - chân ngắn đi xuống.
- Chân dài kết thúc tại mặt dưới của L3:
  - `Z = tim L3 − BarModelDiameter_L3 / 2 − offsetMm`.
- Đầu trên ôm quanh L2; độ cao đỉnh lấy từ bán kính L2 + bán kính cây móc + clearance.
- Chiều rộng vẫn đủ cho giới hạn uốn của type để Revit tạo Shape-Driven.
- Chân ngắn lấy từ HookType 180° hoặc `shortLegLengthMm` trong JSON.
- Coupler chưa được tạo; endpoint chân dài đã ổn định để bổ sung sau.

### Rải cây móc mặc định

- Khoảng cách theo cả hai phương bằng `4 × bước thực tế của L2`.
- Bỏ hai thanh L2 tại mỗi cạnh; bắt đầu từ thanh thứ ba.
- Các hàng liên tiếp rải so le nửa bước móc.
- Toàn bộ vị trí dọc theo phương L2 dịch mặc định 100 mm để tránh chạm thép khác.

Các trường JSON mặc định:

```json
{
  "defaultSpacingMultiplier": 4,
  "defaultSkipL2BarsAtEdges": 2,
  "defaultStaggerRows": true,
  "defaultLongitudinalShiftMm": 100,
  "rowDistribution": null,
  "longitudinalDistribution": {
    "layout": "autoFromL2Staggered"
  }
}
```

Ưu tiên JSON:

- `rowDistribution`: điều khiển các hàng theo phương vuông góc L2.
- `longitudinalDistribution`: điều khiển vị trí dọc theo L2.
- Khi có `offsetsMm`, `spacingsMm`, `zones`, hoặc layout khác `autoFromL2Staggered`, dùng dữ liệu JSON thay cho tự động.
- Layout hỗ trợ: `fixedSpacingWithRemainderAtEnd`, `maximumSpacingEven`, `autoFromL2Staggered`.

## Chọn FamilyInstance và host

- Selection filter hiện cho phép mọi `FamilyInstance`, không còn giới hạn `Family.IsInPlace`.
- Solid được lấy cả từ `GeometryInstance.GetInstanceGeometry()`.
- Solid phải gần đúng hình hộp chữ nhật.
- Sau khi chọn, command kiểm tra `RebarHostData.GetRebarHostData(host)`.
- FamilyInstance thuộc category không hỗ trợ reinforcement sẽ chọn được mặt nhưng báo không thể host rebar.

## Distribution JSON

Hỗ trợ:

- Bước cố định và phần dư ở cuối.
- Chia đều với bước tối đa.
- Danh sách vị trí `offsetsMm`.
- Chuỗi khoảng cách không đều `spacingsMm`.
- Nhiều vùng `zones`.

## Trạng thái kiểm tra gần nhất

- JSON và schema parse hợp lệ.
- Build cấu hình `D2026` thành công, 0 error.
- Có warning `NU1701` từ package `iTextSharp 5.5.13.4`; warning này không liên quan RebarFoundation.
- Đã tìm và loại bỏ toàn bộ `CreateFreeForm`, `Arc.Create`, `arc3pt`, `bendRadiusMm` trong module RebarFoundation.

## Yêu cầu khi tiếp tục

- Giữ nguyên nguyên tắc JSON ưu tiên cao nhất.
- Không khôi phục FreeForm hoặc cung vì lỗi trước đây là `RebarCantBeBent` và không tạo được shape.
- Sau mỗi thay đổi phải build cấu hình `D2026`.
- Khi thay đổi model JSON phải cập nhật đồng thời class C#, JSON mặc định và JSON Schema.
