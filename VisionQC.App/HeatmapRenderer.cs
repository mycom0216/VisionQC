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
    public static ViewOptions Instance { get; } = new();
    [ObservableProperty] private bool _showHeatmap = true;
}

/// <summary>
/// 원본 이미지 위에 불량 위치 히트맵을 반투명으로 겹친 이미지를 만든다(한 장의 비트맵으로 합성해 화면 맞춤 계산이 필요 없다).
/// 색은 양의 증거(불량 쪽)만: 약하면 투명한 노랑 → 강하면 불투명한 빨강. 7×7 격자를 부드럽게 확대하므로 윤곽이 아니라 "대략 이 부근"이다.
/// </summary>
internal static class HeatmapRenderer
{
    private const float HideBelow = 0.15f;      // 이보다 약한 증거는 그리지 않는다(잡음 억제)

    public static ImageSource? Compose(string imagePath, Heatmap? heatmap, bool show)
    {
        var baseImage = ImageLoader.Load(imagePath);
        if (!show || heatmap is null || baseImage is not BitmapSource bmp) return baseImage;

        var norm = HeatmapMath.Normalize(heatmap);
        if (norm.All(v => v < HideBelow)) return baseImage;                  // 양의 증거가 없으면 원본 그대로

        try
        {
            int w = heatmap.Width, h = heatmap.Height;
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
            var overlay = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
            overlay.Freeze();

            int pw = bmp.PixelWidth, ph = bmp.PixelHeight;
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.Linear);
            using (var dc = visual.RenderOpen())
            {
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
