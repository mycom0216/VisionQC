// ============================================================================================
// HeatmapRenderer.cs — 불량 위치 히트맵(CAM heatmap)을 원본 이미지 위에 겹쳐 그리는 화면용 도우미(FR-06).
//
// 용어: 히트맵(CAM heatmap) = 분류 모델이 "이미지의 어느 부분 때문에 불량이라고 판단했는지"를 격자(기본 7x7) 값으로 낸 것.
//       값이 클수록 불량 판정에 기여한 영역이다. 정확한 윤곽이 아니라 "대략 이 부근"을 알려 주는 참고용이다.
// 검사·이력·재판정 세 화면의 ViewModel이 모두 Compose()를 불러 NG 결과의 이미지에 히트맵을 입힌다.
// ViewOptions는 "히트맵 표시" 체크박스 상태를 세 화면이 공유하기 위한 앱 전역 객체다.
// ============================================================================================
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using VisionQC.Core;
using VisionQC.App.ViewModels;

namespace VisionQC.App;

/// <summary>화면 표시 옵션(앱 전체 공유). 히트맵을 끄면 원본 이미지만 보인다.</summary>
public sealed partial class ViewOptions : ObservableObject
{
    /// <summary>앱 전체에서 하나만 쓰는 인스턴스(싱글톤). XAML 체크박스가 x:Static으로 이 객체의 ShowHeatmap에 바인딩한다.</summary>
    public static ViewOptions Instance { get; } = new();
    /// <summary>히트맵을 보일지 여부. 바뀌면 PropertyChanged가 발생해 각 ViewModel이 현재 이미지를 다시 그린다.</summary>
    [ObservableProperty] private bool _showHeatmap = true;
}

/// <summary>
/// 원본 이미지 위에 불량 위치 히트맵을 반투명으로 겹친 이미지를 만든다(한 장의 비트맵으로 합성해 화면 맞춤 계산이 필요 없다).
/// 색은 양의 증거(불량 쪽)만: 약하면 투명한 노랑 → 강하면 불투명한 빨강. 7×7 격자를 부드럽게 확대하므로 윤곽이 아니라 "대략 이 부근"이다.
/// </summary>
internal static class HeatmapRenderer
{
    // (아래 상수) 정규화된 증거 강도가 이 값 미만이면 아예 칠하지 않는다.
    private const float HideBelow = 0.15f;      // 이보다 약한 증거는 그리지 않는다(잡음 억제)

    /// <summary>
    /// 원본 이미지 파일을 읽고, 조건이 맞으면 히트맵을 합성한 이미지를 돌려준다.
    /// </summary>
    /// <param name="imagePath">원본 이미지 파일 경로.</param>
    /// <param name="heatmap">NG 결과의 히트맵. null이면(OK·ERROR 등) 합성하지 않는다.</param>
    /// <param name="show">"히트맵 표시" 옵션. false면 원본만.</param>
    /// <returns>화면의 Image.Source에 넣을 이미지. 파일을 열 수 없으면 null(화면은 빈 칸이 된다).</returns>
    public static ImageSource? Compose(string imagePath, Heatmap? heatmap, bool show)
    {
        var baseImage = ImageLoader.Load(imagePath);
        if (!show || heatmap is null || baseImage is not BitmapSource bmp) return baseImage;

        // 격자 값을 0~1 범위의 "증거 강도"로 정규화한다. 모두 HideBelow 미만이면 칠할 곳이 없으므로 원본을 그대로 쓴다.
        var norm = HeatmapMath.Normalize(heatmap);
        if (norm.All(v => v < HideBelow)) return baseImage;                  // 양의 증거가 없으면 원본 그대로

        try
        {
            int w = heatmap.Width, h = heatmap.Height;
            // 1단계: 격자(w×h) 크기의 작은 반투명 색 이미지를 만든다. 칸마다 BGRA 4바이트.
            // 강한 칸일수록 알파(불투명도)가 커지고 색이 노랑에서 빨강으로 변한다.
            var px = new byte[w * h * 4];                                      // Pbgra32(미리 곱한 알파)
            for (int i = 0; i < norm.Length; i++)
            {
                float v = norm[i];
                if (v < HideBelow) continue;
                float a = 0.72f * MathF.Pow(v, 0.8f);                          // 강할수록 진하게
                byte alpha = (byte)(255 * a);
                byte r = 255, g = (byte)(230 * (1 - v)), b = 0;               // 노랑(약) → 빨강(강)
                px[i * 4 + 0] = (byte)(b * a);
                px[i * 4 + 1] = (byte)(g * a);
                px[i * 4 + 2] = (byte)(r * a);
                px[i * 4 + 3] = alpha;
            }
            // 바이트 배열을 비트맵으로 만든다. Freeze()는 불변으로 굳혀 스레드·성능 면에서 안전하게 만든다(WPF 관례).
            var overlay = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
            overlay.Freeze();

            // 2단계: 원본 위에 위 작은 이미지를 원본 크기로 늘려(Linear 보간으로 부드럽게) 겹쳐 그린 뒤, 한 장의 비트맵(RenderTargetBitmap)으로 굳힌다.
            // 화면에서는 Image 하나만 쓰면 되므로 Stretch/Uniform 같은 화면 맞춤 계산과 좌표 맞추기를 따로 할 필요가 없다.
            int pw = bmp.PixelWidth, ph = bmp.PixelHeight;
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.Linear);
            using (var dc = visual.RenderOpen())
            {
                // 먼저 원본 전체를 그린다.
                dc.DrawImage(bmp, new Rect(0, 0, pw, ph));
                // ROI를 썼다면 격자는 ROI 영역에만 해당한다
                dc.DrawImage(overlay, new Rect(heatmap.RegionX * pw, heatmap.RegionY * ph, heatmap.RegionW * pw, heatmap.RegionH * ph));
            }
            var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }
        catch (Exception) { return baseImage; }                                // 합성에 실패해도 원본은 보여 준다
    }
}
