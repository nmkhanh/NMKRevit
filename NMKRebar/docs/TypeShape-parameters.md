# TypeShape.csv — giải thích tham số để AI điền từ ảnh hình dạng thép

> Mục đích: tài liệu cho người và cho AI đọc ảnh shop drawing / hình dạng thanh thép rồi điền cột `TypeShape.csv`.  
> Workspace: `D:\MCP\NMKRevit`. Snapshot: 2026-09-03.  
> Đây **không** phải bản vá code. Không sửa `.cs`, `.xaml`, family. Chỉ dùng khi điền CSV.

## Execution ledger

| Phase | Work | Status | Do not repeat? | Next action |
|---|---|---|---|---|
| Docs | Giải thích từng para TypeShape + quy tắc điền từ ảnh | NEW | Không | Người dùng chụp hình; AI điền CSV theo tài liệu này |

**Không lặp:** không implement add-in, không đổi family `NMK_Rebar_Shape` / `NMK_Rebar_Array`.

## Kết luận ngắn

`TypeShape.csv` mô tả **hình học 2D của một type** trên family lồng `NMK_Rebar_Shape` (cột = tên type project, ví dụ `A1_F1_D38`). Mỗi type là một polyline tối đa **6 đoạn** đánh số `0`…`5`. Đoạn `1` là trục tham chiếu (không có `1_Angle`). `n_L` là chiều dài mm, `n_V` bật/tắt đoạn, `n_Angle` là góc gập (độ) tại khớp liên quan. Khi ảnh không có giá trị, điền mặc định `n_L=500` và `n_Angle=90`. `d` là đường kính thanh (mm, khớp hậu tố `_Dxx`). `Bending_Factor` hệ số bán kính uốn (`3` trên dữ liệu thật). `Curve=Yes` khi hình có đường cong hoặc ghi `R=`, nếu không thì `No`. `X`,`Y` trên file mẫu luôn `500` — **không** lấy từ kích thước thanh trên bản vẽ.

## Phạm vi / không làm

- **Làm:** giải thích para, format CSV, đơn vị, quy ước đọc ảnh, ví dụ đã quan sát.
- **Không làm:** điền hộ một project cụ thể; không giải thích `TypeData.csv` (`X_1`…`Z_50` là vị trí instance trong array, file khác).

## File và luồng (xác nhận từ code)

| File | Vai trò |
|---|---|
| `NMKRebar/Resources/TypeShape.csv` | Template dọc: chỉ hàng para, **chưa có cột type** |
| `{DataFolder}/TypeShape.csv` | File project; `Create Rebar Type` chèn cột tên type; `Set Shape Type` ghi vào mọi `NMK_Rebar_Array` trùng tên cột |
| `VerticalCsvService.LoadTypeShape` | Load dọc; đảm bảo hàng `Rebar Type` |
| `CsvValueConverter` | Ô trống = **không ghi**; Length = mm; Angle = **độ thập phân**; Yes/No |
| `FamilyTypeCsvApplier.InferSpec` | `Curve`, `*_V` → Yes/No; `*Angle*` → Angle; `Bending_Factor` → Number; `Rebar Type` → Text; còn lại → Length (`X`,`Y`,`d`,`n_L`) |

```text
Ảnh hình dạng
  → đo đoạn / góc / Ø
    → cột type trong TypeShape.csv
      → nút Project "Set Shape Type"
        → FamilySymbol NMK_Rebar_Array cùng tên
```

Nguồn mẫu đã điền (không phải template rỗng): `H:\My Drive\2025\V739\CAD\Rebar_A1\TypeShape.csv`.

## Format CSV (bắt buộc khi AI ghi file)

Bảng **dọc**:

- Hàng 1: `Rebar Type,` rồi các **tên type** (trùng `FamilySymbol`, thường `{mark}_D{dia}`).
- Cột 1: **đúng tên para** như template (không đổi, không dịch).
- Mỗi ô còn lại: giá trị của type đó.

Quy tắc parse (code):

- Độ dài: số thập phân, **millimet**, dấu `.` (InvariantCulture). Ví dụ `1716`, `348.5`.
- Góc: **độ thập phân**, không DMS. `90` đúng; `178°34'4"` **sai** (parser lấy số đầu → `178`).
- Boolean: `Yes`/`No` (cũng nhận `true`/`false`/`1`/`0`).
- Ô trống: **bỏ qua**, không ghi đè giá trị family hiện có. Khi AI điền từ ảnh, không để trống nhóm `*_L`/`*_Angle`: giá trị không xuất hiện trên ảnh lần lượt mặc định là `500`/`90`. Muốn tắt đoạn phải ghi `No`, không để trống.
- Không để dấu `"` lẻ cuối ô (phá quoting CSV).
- Không thêm hàng para mới trừ khi family đã có para đó.

Hàng `Rebar Type`: text trỏ `RebarBarType`. Nếu để trống, loader **điền bằng tên cột type**. Khi điền từ ảnh: để trống hoặc ghi đúng tên cột / tên `RebarBarType`.

## Mô hình hình học (đoạn 0–5)

Family shape là polyline phẳng, tối đa 6 cạnh:

```text
  đoạn 0 (0_L, 0_V)
     \  0_Angle  (góc giữa đoạn 0 và đoạn 1)
      \
       đoạn 1 (1_L, 1_V)     ← trục tham chiếu, KHÔNG có 1_Angle
      /
     /  2_Angle
  đoạn 2 (2_L, 2_V)
     \
      \  3_Angle
       đoạn 3 …
         4_Angle → đoạn 4
         5_Angle → đoạn 5
```

**Vì sao không có `1_Angle`:** đoạn `1` nằm trên trục local của family (hướng thanh chính trên bản vẽ). Mọi góc khác đo **tại đỉnh**, so với đoạn liền kề, không phải so với phương Bắc bản vẽ.

**Cách chọn đoạn 1 trên ảnh (bắt buộc, tránh đánh số ngược):**

1. Thanh thẳng: chỉ đoạn `1` bật (`1_V=Yes`), `0_V` và `2_V`…`5_V` = `No`.
2. Hình L / móc một đầu: đoạn dài (thân) = `1`; chân/móc đầu kia = `0` **hoặc** `2` tùy đầu nào là “đầu 0” của family. Trên dữ liệu V739, móc/chân vuông thường là `0` (`0_Angle=90`, `0_V=Yes`) và/hoặc `2` (`2_Angle=90`).
3. Hình U / đai 3 cạnh: `0_V`,`1_V`,`2_V` = Yes; `3_V`…`5_V` = No. Hai chân `0_L` và `2_L`, đáy `1_L`.
4. Nhiều gãy hơn: bật lần lượt `3`, `4`, `5` theo thứ tự đi dọc thanh từ đầu 0 → đầu 5. Không nhảy số (không tắt `2` rồi bật `4`).
5. Nếu ảnh có **hơn 6 đoạn thẳng**: family không đủ para — ghi warning, không bịa `6_L`.
6. Nét đứt có thể là thanh ghép/đối ứng lặp lại cùng type. Chỉ đưa vào polyline hiện tại khi đường đi liên tục và tổng các đoạn + cung uốn khớp chiều dài triển khai (`ΣL`) trên bản vẽ; không cộng cả hai thanh ghép thành một shape.

`n_V=No` thì đoạn đó **không vẽ**, dù `n_L` / `n_Angle` còn số. Khi điền mới từ ảnh, AI phải tắt bằng `No`, đồng thời điền `500` cho `n_L` và `90` cho `n_Angle` nếu ảnh không cho giá trị.

## Từng tham số

### `Rebar Type` (Text)

Tên loại thép Revit (`RebarBarType`), thường trùng cột (`A1_F1_D38`). Không phải kích thước hình. Ô trống → code copy tên cột.

### `Curve` (Yes/No)

Có **cung uốn / fillet** tại góc gãy hay không.

- `Yes`: hình có đường cong hoặc có ký hiệu bán kính `R=`.
- `No`: hình không có đường cong và không có ký hiệu `R=`.

Chỉ cần một trong hai dấu hiệu — đường cong hoặc `R=` — là điền `Yes`.

### `0_Angle` (độ)

Góc gập **giữa đoạn 0 và đoạn 1**.

- `90` = vuông (móc/chân thẳng góc) — giá trị phổ biến nhất trên file thật.
- `0` = đoạn 0 thẳng hàng với đoạn 1 (vẫn có thể `0_V=Yes` với `0_L` ngắn).
- Góc tù/nhọn khác: điền số thập phân (`135`, `45`, `88.5`).

Nếu `0_V=No`, góc này không ảnh hưởng hình vẽ; có thể `90` hoặc trống.

### `0_L` (mm, Length)

Chiều dài **đoạn 0** (thường chân/móc/đầu thanh), theo tâm thanh hoặc cạnh như trên bản vẽ — **cùng quy ước kích thước shop drawing** (thường tim-tim hoặc ngoài-ngoài; nếu ảnh ghi rõ, theo ghi chú ảnh; không đổi đơn vị sang cm).

Ví dụ V739: móc `200`; chân U `1716`; chân nhỏ `348`, `285`, `128`.

### `0_V` (Yes/No)

Hiện đoạn 0. `No` = thanh bắt đầu từ đoạn 1 (thẳng hoặc không có móc đầu 0).

### `1_L` (mm)

Chiều dài **đoạn chính / đáy U / thân**. Trên V739 thường là số lớn nhất (`500` mẫu, `4196`, `8040`, `10483`…).

Không có góc riêng: hướng đoạn 1 = trục family.

### `1_V` (Yes/No)

Hầu như luôn `Yes`. `No` chỉ khi type không dùng shape (hiếm). Không tắt `1` nếu vẫn còn đoạn `0` hoặc `2` đang bật — hình sẽ gãy logic.

### `2_Angle` (độ)

Góc giữa đoạn **1 và 2**. Vuông = `90`.

### `2_L` (mm) / `2_V` (Yes/No)

Đoạn sau đoạn chính (chân U thứ hai, hoặc cạnh tiếp theo của đai). `2_V=No` → hình chỉ còn 0+1 (L) hoặc chỉ 1 (thẳng nếu `0_V=No`).

### `3_Angle`, `3_L`, `3_V`

Cạnh thứ tư trên polyline (sau đoạn 2). Mẫu V739: `3_V=No`, `3_L` placeholder `1000`, `3_Angle=90`. Chỉ bật khi ảnh có đoạn thẳng thứ 4.

### `4_Angle`, `4_L`, `4_V`

Đoạn 5 trên polyline (index 4). Placeholder mẫu: `4_L=1200`, `4_V=No`, `4_Angle=90`.

### `5_Angle`, `5_L`, `5_V`

Đoạn cuối. Placeholder mẫu: `5_Angle=45`, `5_L=200`, `5_V=No`. `45` là default template, **không** suy ra mọi thanh đều có móc 45° — chỉ dùng khi đoạn 5 thật sự bật và ảnh cho góc đó.

### `Bending_Factor` (Number, không đổi đơn vị)

Hệ số bán kính/đường kính uốn so với `d`. Mọi ô đã điền trên V739 = **`3`**.

Điền `3` trừ khi bản vẽ / tiêu chuẩn ghi hệ số khác (ví dụ `4`, `6`). Không ghi `3d` hay `3*16`.

Giả thuyết (cần family để xác nhận 100%): bán kính uốn trong family ≈ `Bending_Factor * d`. Gắn nhãn giả thuyết — không đổi tên para.

### `X`, `Y` (mm, Length)

Trên toàn bộ cột đã điền V739: **`500` và `500`**. Không trùng `0_L`/`1_L`.

Đây là **offset gốc / vị trí đặt** của instance shape trong array (mặt phẳng local), **không** phải kích thước ghi trên hình dạng thanh.

Khi điền từ ảnh hình dạng: **giữ `500`,`500`** (hoặc giữ nguyên nếu cột đã có). Không gán `X=1_L`. Không nhầm với `X_1`… trong `TypeData.csv`.

### `d` (mm, Length)

**Đường kính thanh**, cùng số với hậu tố type: `…_D38` → `38`, `…_D16` → `16`, `NMK_D13` trên mẫu lại ghi `d=16` (cột mẫu cũ — **ưu tiên số trên tên type / ghi chú Ø trên ảnh**).

Không ghi `D16`, chỉ `16`. Dùng mm, không inch.

## Bảng nhanh cho AI

| Para | Kiểu CSV | Đơn vị | Đọc từ ảnh? | Ghi chú |
|---|---|---|---|---|
| Rebar Type | text | — | Tên type / cột | Trống OK |
| Curve | Yes/No | — | Có cung tại góc? | |
| 0_Angle | số | độ | Góc đoạn 0–1 | Thường 90 |
| 0_L | số | mm | Dài đoạn 0 | |
| 0_V | Yes/No | — | Có đoạn 0? | |
| 1_L | số | mm | Dài đoạn chính | Không có 1_Angle |
| 1_V | Yes/No | — | Gần như luôn Yes | |
| 2_Angle | số | độ | Góc 1–2 | |
| 2_L | số | mm | Dài đoạn 2 | |
| 2_V | Yes/No | — | Có đoạn 2? | |
| 3_Angle / 3_L / 3_V | độ / mm / YesNo | | Đoạn 3 | Tắt nếu không có |
| 4_Angle / 4_L / 4_V | | | Đoạn 4 | |
| 5_Angle / 5_L / 5_V | | | Đoạn 5 | Default góc 45 chỉ khi bật |
| Bending_Factor | số | — | Hiếm khi ghi trên ảnh | Mặc định `3` |
| X, Y | số | mm | **Không** từ kích thước thanh | Mặc định `500` |
| d | số | mm | Ø hoặc `_Dxx` | |

## Protocol điền từ ảnh (AI phải làm theo thứ tự)

1. Đọc tên type (cột CSV) và Ø → điền `d`, kiểm tra khớp `_Dxx`.
2. Phác polyline: đánh số đoạn 0→5, **đoạn dài/thân = 1**.
3. Với mỗi đoạn tồn tại: `n_V=Yes`, `n_L` = mm trên ảnh (một quy ước duy nhất cho cả cột).
4. Với mỗi khớp tồn tại: `0_Angle` hoặc `2_Angle`…`5_Angle` = độ thập phân. Vuông → `90`.
5. Đoạn không có trên ảnh: `n_V=No`; nếu không có giá trị thì vẫn điền `n_L=500` và `n_Angle=90`. Không thêm đoạn hình học chỉ vì có giá trị mặc định.
6. `Curve`: `Yes` nếu hình có đường cong hoặc ghi `R=`; nếu không có cả hai thì `No`.
7. `Bending_Factor=3` trừ khi ảnh ghi hệ số khác.
8. `X=500`, `Y=500` trừ khi user nói rõ origin khác.
9. `Rebar Type`: trống hoặc = tên cột.
10. Trước khi xuất, mọi ô thuộc nhóm `*_L` chưa có giá trị phải là `500`, mọi ô thuộc nhóm `*_Angle` chưa có giá trị phải là `90`.
11. Xuất CSV UTF-8, không BOM bắt buộc; không DMS; không ký tự `°` `'` `"` trong ô số.
12. Báo các type không đọc được ảnh / >6 đoạn — **để trống cả cột** còn hơn bịa. Với type đã xác định được hình, góc hoặc chiều dài không ghi trên ảnh dùng giá trị mặc định nêu trên.
13. Khi phạm vi yêu cầu bỏ qua shape tròn, chỉ bỏ thanh có đường tâm là vòng tròn/ring hoàn chỉnh; giữ lại hairpin, móc 180°, cung đầu thanh và khung có móc. Cột type tròn bị bỏ phải để trống toàn bộ và không tạo file varies.

## Shape có giá trị biến thiên

Một shape được xem là **biến thiên** nếu có ít nhất một trong các dấu hiệu:

- Có bảng liệt kê nhiều bộ kích thước.
- Kích thước ghi bằng ký hiệu/chữ thay vì một số cố định, ví dụ `L`, `L1`, `L2`.
- Kích thước ghi theo khoảng `từ số đến số`.

Quy tắc xuất dữ liệu:

1. Trong file chính `TypeShape.csv`, dùng bộ giá trị nhỏ nhất **cùng tồn tại trên một dòng hợp lệ của bảng**. Không ghép giá trị nhỏ nhất từ hai dòng khác nhau nếu chúng tạo thành một bộ kích thước không có thật.
2. Nếu chỉ một para biến thiên, dùng giá trị nhỏ nhất của para đó.
3. Nếu khoảng ghi `a-b` hoặc `a~b`, dùng đầu nhỏ hơn cho `TypeShape.csv`.
4. Tạo một file riêng cho từng type theo tên `TypeShape_Varies_<RebarType>.csv`, ví dụ `TypeShape_Varies_P1_B9_D22.csv`.
5. File varies giữ format dọc giống `TypeShape.csv`: cột đầu là tên para; mỗi cột tiếp theo là một biến thể; hàng tiêu đề `Rebar Type` phải duy nhất, dùng dạng `<RebarType>_<số thứ tự>`.
6. Phần `<RebarType>` trong tên file và tiêu đề biến thể phải lấy đúng từ file chính, ví dụ `P1_B9_D22`; chỉ hậu tố thứ tự `_1`, `_2`… được thêm để phân biệt các cột.
7. Mỗi biến thể phải có đủ `Curve`, `0_Angle`…`5_V`, `Bending_Factor`, `X`, `Y`, `d`; tiếp tục áp dụng mặc định `*_L=500`, `*_Angle=90`.
8. Thứ tự cột biến thể giữ nguyên thứ tự dòng trong bảng trên bản vẽ. Không bỏ các dòng trùng nhau nếu bản vẽ liệt kê riêng.

## Ví dụ quan sát (V739 `NMK_D13` và type đã fill)

Mẫu `NMK_D13`: Curve Yes; 0: 90° / 200 / Yes; 1: 500 / Yes; 2: 90° / 800 / **No**; 3–5 tắt; Bending 3; X,Y 500; d 16. → Hình thực tế chỉ **hai đoạn bật** (0 và 1): L hoặc móc + thân.

Một type U điển hình (cùng file): `0_V=Yes`, `1_V=Yes`, `2_V=Yes`, `3_V`…`5_V=No`; `0_Angle=2_Angle=90`; `0_L` ≈ `2_L` (hai chân); `1_L` lớn (đáy).

Type chỉ thân: `0_V=No`, `1_V=Yes`, `2_V=No`.

## Phân biệt TypeData (đừng điền nhầm)

`TypeData.csv`: `Z_1`…`Z_50`, `X_1`…, `Y_1`… — vị trí từng instance lồng trong **array**, không phải cạnh thanh. Nút **Set Data Type**. `Z≤0` được cộng dồn khi apply; **không** áp dụng cho TypeShape.

## Rủi ro đã biết

- Góc DMS / dấu `"` phá CSV hoặc làm mất phần phút/giây.
- Đánh số ngược (lấy chân làm đoạn 1) làm `0_Angle`/`2_Angle` đổi chỗ, shape mirror/sai.
- Để trống `n_V` khi muốn tắt: family giữ Yes cũ → thừa đoạn.
- Nhầm `d` với `Bending_Factor` hoặc với `0_L`.
- Spec family thật (công thức trong `.rfa`) không có trong repo; góc trong/ngoài (interior vs complementary) nếu family dùng góc bù 180−θ thì ảnh 90° vẫn là 90 trên CSV như mọi cột V739 — **ưu tiên 90 cho góc vuông trên shop drawing**, không tự đổi thành 270 trừ khi ảnh ghi rõ.

## Kiểm tra sau khi điền

- Mỗi cột: đúng một type; `d` khớp tên.
- Không có `1_Angle` (không thêm hàng).
- Số `Yes` trên `*_V` = số đoạn thẳng thấy trên ảnh (≤6).
- `Set Shape Type` sau khi lưu CSV vào folder project (không pick instance).
