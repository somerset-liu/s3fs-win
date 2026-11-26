using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace S3fsWin;

/// <summary>托盘图标的三态。</summary>
internal enum TrayState
{
    NotMounted,
    Mounting,
    Mounted,
}

/// <summary>
/// 托盘图标现画，省得往仓库里塞 .ico：三态用三种颜色的圆点区分。
/// 生成的 Icon 在进程生命周期内常驻，只占 3 个 GDI 句柄。
/// </summary>
internal static class TrayIconFactory
{
    private const int Size = 32;

    public static Icon Create(TrayState state)
    {
        Color fill = state switch
        {
            TrayState.Mounted => Color.FromArgb(46, 160, 67),   // 绿：已挂载
            TrayState.Mounting => Color.FromArgb(241, 165, 37), // 黄：挂载中
            _ => Color.FromArgb(130, 138, 145),                 // 灰：未挂载
        };

        using var bmp = new Bitmap(Size, Size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var back = new SolidBrush(Color.FromArgb(28, 32, 38)))
            {
                g.FillEllipse(back, 1, 1, Size - 3, Size - 3);
            }

            using (var dot = new SolidBrush(fill))
            {
                g.FillEllipse(dot, 7, 7, Size - 15, Size - 15);
            }

            // 已挂载时多画一圈环，色盲或灰度主题下也能分出来。
            if (state == TrayState.Mounted)
            {
                using var ring = new Pen(Color.White, 2.5f);
                g.DrawEllipse(ring, 5, 5, Size - 11, Size - 11);
            }
        }

        // Icon.FromHandle 不接管句柄，这里也不 DestroyIcon：程序退出时系统一并回收。
        return Icon.FromHandle(bmp.GetHicon());
    }
}
