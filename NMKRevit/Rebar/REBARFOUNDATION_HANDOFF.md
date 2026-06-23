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
- Chỉ móc 180° mới dùng `RebarHookType`/native hook. Móc 90° và các móc còn lại phải dựng bằng các đoạn `Line` vì native hook không kiểm soát ổn định được góc quay.
- Revit được phép tự tạo bán kính uốn vật lý tại giao điểm các đoạn thẳng.
- `schemaVersion: 2` cho phép `bendRadiusMm` ở `shape` như metadata để đối chiếu bản vẽ; tool không truyền bán kính này vào API.
- Không giả định cố định 4 lớp thép. Số lớp và offset tim thép phải đọc từ hình section/detail; có dự án 8 lớp hoặc nhiều hơn.
- Với thanh split, `X####` là chiều dài khai triển/cắt thép; `straightLengthMm` phải là chiều dài đoạn thẳng thật trên hình, và chân một đầu phải dùng `hookStartMm` hoặc `hookEndMm`.
- Shape `polycurve` trong JSON luôn ưu tiên hơn hình dạng tự tính, nhưng chỉ chấp nhận segment `line`.
- Nếu JSON có dữ liệu rải cụ thể thì JSON luôn ưu tiên quy tắc mặc định.

## Lớp thép và offset

Không còn dùng giả định cố định bốn lớp thép. JSON phải mô tả đúng số lớp đọc được từ hình section/detail.

Quy tắc bắt buộc:

- Mỗi lớp phải có mark, phương thép, mặt tham chiếu và offset từ mặt tham chiếu tới tim thép.
- `z.offsetMm` luôn là khoảng cách tới tim thép, không phải cover, tangent, mép thanh, hoặc baseline của hình.
- Top layer dùng `topFace`; bottom layer dùng `bottomFace`.
- Không gom nhiều mark vào cùng một offset nếu hình thể hiện các tim thép khác nhau.
- Nếu hình dự án có 4 lớp trên và 4 lớp dưới thì JSON phải có đủ 8 lớp hoặc đủ các `barPieces` tương ứng.

Offset đáy mẫu hiện tại, theo hình và review:

- F4: `bottomFace` tới tim = 220 mm.
- F3: `bottomFace` tới tim = 247 mm.
- F6: `bottomFace` tới tim = 320 mm.
- F5: `bottomFace` tới tim = 347 mm.

Hình dạng:

- Móc 90° và các móc không phải 180° dựng bằng line geometry (`uBar`, `hairpin`, `piecewiseLine`).
- Chỉ móc 180° mới dùng `RebarHookType`.
- Thanh split phải dùng chiều dài đoạn ngang thật trong `straightLengthMm`; chiều dài `X####` giữ như developed/cut length trong metadata.
- Thanh split có một chân chỉ dùng `hookStartMm` hoặc `hookEndMm`, không dùng `legLengthMm` hai đầu.

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
- Đã tìm và loại bỏ toàn bộ `CreateFreeForm`, `Arc.Create`, `arc3pt` trong module RebarFoundation; `bendRadiusMm` chỉ còn là metadata JSON v2.

## Yêu cầu khi tiếp tục

- Giữ nguyên nguyên tắc JSON ưu tiên cao nhất.
- Không khôi phục FreeForm hoặc cung vì lỗi trước đây là `RebarCantBeBent` và không tạo được shape.
- Sau mỗi thay đổi phải build cấu hình `D2026`.
- Khi thay đổi model JSON phải cập nhật đồng thời class C#, JSON mặc định và JSON Schema.
