# TypeData.csv — vị trí và khoảng rải thép

`TypeData.csv` mô tả tối đa 50 instance của mỗi `Rebar Type` trong family
`NMK_Rebar_Array`. File có dạng đứng:

- Hàng đầu: tên các type, phải khớp `Rebar.txt`.
- `Z_1`…`Z_50`: khoảng rải theo trục array.
- `X_1`…`X_50`, `Y_1`…`Y_50`: khoảng cách từ instance tới hai cạnh
  tương ứng của profile đặt thép.

Đơn vị là millimet, dùng dấu `.` cho phần thập phân.

## Quy tắc Z

Giá trị Z trong CSV là **delta**, không phải tọa độ cộng dồn:

- `Z_1`: khoảng cách từ mặt bắt đầu rải tới cây đầu tiên.
- `Z_2`: khoảng cách từ cây 1 tới cây 2.
- `Z_3`: khoảng cách từ cây 2 tới cây 3; tiếp tục tương tự.
- Mọi Z của cây thật phải `<= 0`.
- Hai cây cùng trạm có thể dùng delta `0`.

Khi chạy **Set Data Type**, `VerticalCsvService.WithCumulativeZ` cộng dồn các
delta không dương. Ví dụ:

```text
CSV:       -400, -300, -300
Vào family: -400, -700, -1000
```

Nếu Z dương, code đặt lại tổng tại giá trị đó. Family đồng thời dùng điều kiện
`Z > 0` để ẩn instance.

## Số lượng cây

- `Qty > 50`: bỏ qua, để trống toàn bộ cột TypeData; không tự chia thành nhiều
  array.
- `Qty <= 50`: điền đúng `Qty` giá trị Z thực.
- Từ `Z_(Qty+1)` đến `Z_50`: điền `100` để ẩn các instance thừa.
- Không dùng `100` cho type vượt 50 vì toàn bộ type đó phải được bỏ qua.

## Quy tắc X/Y

- X/Y đọc từ mặt cắt đặt thép, không lấy từ kích thước shape.
- X/Y có thể giống nhau cho mọi cây nếu profile và lớp bảo vệ không đổi.
- Nếu cạnh profile dốc hoặc thay đổi, điền giá trị riêng cho từng cây khi bản
  vẽ xác định được.
- Nếu Z xác định chắc chắn nhưng hệ trục/cạnh X/Y không rõ, được phép chỉ điền
  Z và để X/Y trống. Loader bỏ qua ô trống, không ghi đè parameter tương ứng.
- Không suy ra X/Y chỉ từ cover nếu chưa xác định được cạnh nào tương ứng với
  trục local X hoặc Y của family.

## Cách đọc bản vẽ

1. Ghép đúng mark và diameter với cột trong `Rebar.txt`.
2. Xác định mặt bắt đầu rải và hướng đọc chuỗi kích thước.
3. Đọc khoảng từ cạnh tới cây đầu, rồi mở rộng các nhóm như
   `4@200=800` thành bốn delta `-200`.
4. Chuỗi sau khi mở rộng phải có đúng số phần tử bằng `Qty`.
5. Không dùng kích thước tổng cấu kiện, khoảng cách cọc, chiều dài thanh hoặc
   bảng gia công shape làm spacing.
6. Nếu một mark nằm trên nhiều mặt/lớp nhưng bản vẽ không định nghĩa một thứ
   tự array duy nhất, để trống thay vì ghép các chuỗi tùy ý.
7. Array vòng kín không có mặt bắt đầu được chỉ định thì không tự chọn `Z_1`.

## Kiểm tra trước khi dùng

- Header và thứ tự cột khớp chính xác `Rebar.txt`.
- File có đủ 150 hàng `Z_1…Z_50`, `X_1…X_50`, `Y_1…Y_50`.
- Cột được điền có đúng `Qty` giá trị Z không dương.
- Tất cả Z còn lại sau cây cuối bằng `100`.
- Cột `Qty > 50` và cột không đủ căn cứ phải trống hoàn toàn.
