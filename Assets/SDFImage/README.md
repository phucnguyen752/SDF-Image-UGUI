# SDF Image

Outline và shadow cho **Unity 6 / uGUI (Canvas)**. Thư viện độc lập, chỉ cần Unity uGUI.

## Dùng nhanh

1. Tạo **GameObject → UI → SDF Image**. Chỉ có một component `SdfImage`, kế thừa `UnityEngine.UI.Image`.
2. Gán sprite gốc vào **Source Image** ngay trên component này.
3. Nếu sprite chưa có SDF, nhấn **Generate SDF**. Ảnh vẫn hiển thị bình thường trong lúc chờ.
4. Các thuộc tính Image hiện trực tiếp theo Inspector chuẩn Unity: **Source Image**, **Color**, **Material**, Raycast, Maskable, Image Type và các tuỳ chọn theo kiểu ảnh; không bọc trong group, không cần chờ Generate.
5. Khi **SDF ready**, Inspector hiện thêm **Outline** và **Shadow** bên dưới. Bật toggle nào thì nhóm đó hiện thông số; tắt sẽ giữ giá trị để lần sau bật lại. **SDF Settings** được thu gọn mặc định.

Cấu hình thuộc **texture nguồn**, áp dụng cho mọi sprite con trong texture đó. Gán sprite thường không tự bake. Nút Generate SDF bật **Auto Update**: những lần đổi ảnh, thông số import hoặc cấu hình SDF sau đó sẽ tự cập nhật. Có thể chỉnh Auto Update trong SDF Settings hoặc bật Generate SDF ở Inspector của texture gốc.

Nút **Cancel** hoặc tắt Auto Update huỷ công việc đang chờ/đang chạy và dừng tạo mới. Kết quả đã hoàn thành được giữ lại. Muốn tắt hiệu ứng thì tắt toggle **Outline** / **Shadow**; tắt cả hai sẽ dùng đường render Image bình thường.

Component `SdfAutoBake` cũ được giữ để các prefab cũ vẫn tải được. Image sẽ tiếp nhận source đã lưu; nút **Remove Legacy Auto Bake** trong Inspector bỏ helper thừa với Undo. Object tạo mới không cần helper này.

## Texture nằm trong sprite gốc

Texture màu có padding, texture distance và mô tả `SdfSprite` là các subasset của **chính file ảnh nguồn**. Mô tả còn được gắn trực tiếp vào Sprite bằng API `Sprite.AddScriptableObject` của Unity 6; `SdfSprite.FromSprite(source)` lấy lại dữ liệu này trong player.

Không tạo `.asset`, PNG SDF hay thư mục ảnh bake riêng trong Assets. Cache tạm nằm ở `Library/SDFImage`, không cần đưa vào Git. Commit file ảnh nguồn, `.meta` của nó và thư viện. Máy mới tự bake lại những nguồn đã bật SDF khi Unity import. File ảnh gốc và các thiết lập import hình ảnh được giữ nguyên; cấu hình SDF thêm vào `TextureImporter.userData`, giữ nội dung trước đó.

`Image.sprite` giữ tham chiếu đến Sprite gốc trong player để tìm dữ liệu đính kèm. Runtime không chạy thuật toán bake. Chờ Ready trước khi build nguồn đã bật Auto Update; build kiểm tra image trong các scene được bật, Resources, preloaded assets và các prefab phụ thuộc. Sprite chưa Generate dùng Image bình thường.

## Bake có giới hạn và có thể huỷ

- Giảm kích thước **trước** khi tính distance; Max Size mặc định 512, chọn 64–1024. Không thay kích thước texture nguồn.
- Đọc GPU bằng `AsyncGPUReadback`, tính distance tuyến tính theo số pixel trên một worker. Không gọi đọc GPU đồng bộ hoặc chờ worker trên main thread.
- Chỉ chạy một job mỗi lúc. Đổi cấu hình huỷ kết quả cũ; chỉ thế hệ hiện hành mới được publish.
- Cache theo nguồn, Sprite ID và cấu hình; import lại dữ liệu đã hợp lệ không bake lặp.
- Mỗi texture tối đa 128 sprite hoặc 4 triệu texel sau padding. Vượt giới hạn sẽ báo lỗi để giảm Max Size/padding, trước khi cấp phát bộ đệm bake lớn.
- Main thread vẫn tạo tài nguyên GPU và publish subasset qua Unity import; bước import có thể khựng ngắn tuỳ máy. Không có cam kết tuyệt đối không khựng hay số FPS chưa đo.

Editor cần graphics device hỗ trợ AsyncGPUReadback. Chạy `-nographics` không tạo được SDF mới. GPU chỉ dùng để lấy ảnh nhập thực tế, bao gồm cấu hình alpha/import; nguồn không cần bật Read/Write.

## Hiệu ứng và giới hạn

- Outline ngoài/trong/giữa, width, color và softness; shadow có offset, blur, spread. Offset bằng 0 và shadow màu sáng tạo glow.
- Giữ RGB/alpha nguồn cho phần fill. `Graphic.color` tint fill, alpha làm mờ toàn bộ hình và hiệu ứng một lần.
- Simple, preserve aspect và nine-slice; layout, native size; quad mở rộng để không cắt outline/shadow.
- Hỗ trợ `Mask`, `RectMask2D` (cả softness), `CanvasGroup`; vùng nhận raycast vẫn là RectTransform gốc.
- SDF hỗ trợ Simple và Sliced với Fill Center. Filled/radial fill, Tiled và Sliced tắt Fill Center dùng Image Unity bình thường, không có hiệu ứng SDF. Chưa tích hợp SpriteRenderer, UI Toolkit, TMP, Coffee SoftMask/UIEffect.

Width/softness/offset/blur/spread dùng **đơn vị local của Canvas**. Padding và Distance Range dùng **pixel của ảnh SDF sau giảm kích thước**. Shader giới hạn hiệu ứng theo lượng padding/distance có sẵn; tăng padding và range nếu viền ngừng rộng thêm. Offset shadow độc lập với giới hạn distance. Biến đổi Canvas/object sẽ scale cả hiệu ứng.

Field dùng RHalf tuyến tính, distance dương ở trong hình. Thuật toán tính khoảng cách Euclidean đến lớp alpha đối diện, hiệu chỉnh nửa pixel. Alpha Threshold xác định đường biên. Đây là SDF từ raster, không tái dựng vector/MSDF; tăng Max Size giúp giữ chi tiết nhỏ.

Mỗi image có material riêng, không gom batch với các image dùng material khác. Shader lấy ba mẫu texture mỗi fragment. Shadow lớn tăng vùng overdraw. Texture không mipmap/compression: RGBA32 + RHalf khoảng 6 byte/texel GPU, và dữ liệu CPU đọc được khoảng 6 byte/texel nữa, chưa tính texture nguồn và overhead. Ảnh 256×256 với padding 32 dùng khoảng 600 KiB cho mỗi phía GPU/CPU.

## API

```csharp
using SDFUI;
using UnityEngine;

public sealed class ButtonStyle : MonoBehaviour
{
    [SerializeField] private SdfImage image;
    [SerializeField] private Sprite icon; // Đã bật Generate SDF trong Editor.

    private void Awake()
    {
        image.sprite = icon; // API Image chuẩn, cũng hỗ trợ overrideSprite.
        image.OutlineEnabled = true;
        image.OutlineWidth = 4;
        image.OutlineColor = Color.white;
        image.OutlinePosition = SdfOutlinePosition.Outer;
        image.ShadowEnabled = true;
        image.ShadowColor = new Color(0, 0, 0, 0.35f);
        image.ShadowOffset = new Vector2(0, -6);
        image.ShadowBlur = 10;
    }
}
```

`SdfImage` kế thừa `Image`, có thể gán vào field `UnityEngine.UI.Image` hoặc Button Target Graphic. `image.sprite` và `image.overrideSprite` tự tìm dữ liệu SDF tương ứng, kể cả đổi sprite trong cùng một sheet. API cũ `image.Sprite` nhận `SdfSprite` vẫn còn để tương thích; code mới dùng `image.sprite` và `image.SdfData`. Với Button, bật Raycast Target; Canvas cần GraphicRaycaster/EventSystem như uGUI thông thường.

## Demo, cài đặt và kiểm thử

**Tools → SDF Image → Create Demo Prefab** tạo mẫu riêng trong `Assets/SDFImageDemo`, gồm ba ảnh nguồn bật SDF và prefab minh hoạ outline, shadow, glow, Sliced, RectMask2D. Chờ Ready rồi kéo prefab vào scene trống. Lệnh không sửa scene đang mở.

![Demo render trong Unity URP](Documentation~/preview.png)

Hàng trên: outline ngoài, trong, giữa kèm shadow. Hàng dưới: glow, panel nine-slice, RectMask2D. Khi cài bằng UPM, có thể Import mẫu **Outline and Shadow Demo** trong Package Manager. Thư mục `Samples~` không tự import khi copy thư viện vào Assets.

Trong Package Manager, chọn cài package từ Git URL và nhập:

```text
https://github.com/phucnguyen752/sdf-image.git#0.3.1
```

Tag `0.3.1` chứa package `com.sdfimage.ugui` ngay tại root; không cần thêm `?path=`. Nhánh `main` chứa project Unity đầy đủ, thư viện ở `Assets/SDFImage`; nhánh `upm` dành cho Package Manager. Dùng tag để cố định phiên bản.

Cũng có thể copy `Assets/SDFImage` cùng `.meta` sang project Unity 6 có uGUI 2.0, hoặc để một bản ngoài Assets và dùng Package Manager → Add package from disk với `package.json`. Chỉ giữ một bản cài. Texture nguồn cần ở trong Assets để lưu cấu hình và import dữ liệu đính kèm. Shader trong Resources được giữ trong build. Quy trình phát hành xem [Publishing.md](Documentation~/Publishing.md).

Namespace và assembly dùng `SDFUI`, `SDFUI.Editor`, `SDFUI.Tests.Editor`. Khi cập nhật từ bản 0.2, cập nhật namespace trong code và package ID trong manifest; giữ `.meta` của script để component cũ vẫn được nhận diện. Cấu hình nguồn và tham chiếu dữ liệu bake cần được chuyển cùng thư viện. Icon component là PNG 64×64, xuất từ [SVG gốc](Documentation~/SdfImage.svg).

Chạy `SDFUI.Tests` trong Window → General → Test Runner → EditMode. Với cài UPM, thêm package vào `testables` trong manifest và cài Unity Test Framework. Kết quả kiểm tra thực tế và giới hạn xem [VALIDATION.md](VALIDATION.md).

Tham khảo theo yêu cầu: [SDF Image – Quality UI Outlines and Shadow](https://marketplace.unity.com/packages/tools/gui/sdf-image-quality-ui-outlines-and-shadow-244942). Đây là implementation độc lập với phạm vi ở trên.
