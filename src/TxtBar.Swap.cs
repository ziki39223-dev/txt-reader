// ============================================================================
//  TxtBar 第五部分：L7 切换入口（SwitchPad）
//  在 Windows 任务栏最右侧、「显示桌面」按钮所在的那块位置，常驻一个小方块，
//  点它就切换「我的条在前 / 系统任务栏在前」。
//  它始终置顶、不抢焦点，点它不会把你在用的窗口顶下去。
// ============================================================================
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TxtBar
{
    public sealed class SwitchPad : Form
    {
        private readonly TxtBarForm _owner;
        private bool _hover = false;
        private bool _pressed = false;
        private System.Windows.Forms.Timer _follow;

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr val);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public SwitchPad(TxtBarForm owner)
        {
            _owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Text = "TxtBarSwap";
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        /// <summary>把自己摆到任务栏最右端（显示桌面按钮那一带），或退回条的最右端</summary>
        public void ApplyPlacement()
        {
            Rectangle tb = Shell.TaskbarRect;
            int w = Math.Max(Shell.S(28), Shell.S(34));
            int x, y;
            if (_owner.Mem.SwapAtDesktopCorner)
            {
                x = tb.Right - w;      // 最右端，也就是「显示桌面」按钮那一块
                y = tb.Top;
            }
            else
            {
                // 退回条自己的最右边
                Rectangle bar = _owner.TargetRect();
                x = bar.Right - w;
                y = bar.Top;
            }
            if (!_owner.SuppressUi)
                SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, w, tb.Height,
                    Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            else if (IsHandleCreated)
                SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, w, tb.Height, Native.SWP_NOACTIVATE);
            else ClientSize = new Size(w, tb.Height);
            Invalidate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyPlacement();
            // 任务栏被改尺寸/位置（比如切换了自动隐藏）时跟着走
            _follow = new System.Windows.Forms.Timer();
            _follow.Interval = 500;
            _follow.Tick += delegate
            {
                Shell.Detect();
                ApplyPlacement();
            };
            _follow.Start();
        }

        // ------------------------------------------------------------------
        //  绘制：两个箭头 + 当前谁在前
        // ------------------------------------------------------------------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // 底：跟任务栏同色，悬停/按下时给一块圆角高亮
            Color bg = Skin.Bg;
            using (Brush b = new SolidBrush(bg)) g.FillRectangle(b, 0, 0, Width, Height);
            if (_hover || _pressed)
            {
                Rectangle r = new Rectangle(Shell.S(2), Shell.S(4), Width - Shell.S(4), Height - Shell.S(8));
                using (GraphicsPath p = RoundRect(r, Shell.S(6)))
                using (Brush b = new SolidBrush(_pressed ? Skin.Pressed : Skin.Hover))
                    g.FillPath(b, p);
            }

            // 图标：两个方向相反的箭头（⇄），谁在前就用强调色标出来
            int size = Math.Max(12, (int)(Height * 0.42));
            using (Font f = new Font("Segoe MDL2 Assets", size, GraphicsUnit.Pixel))
            {
                Color c = _owner.Mem.CoverTaskbar ? Skin.Accent : Skin.Fg;
                using (Brush b = new SolidBrush(c))
                {
                    string glyph = "";     // Switch
                    SizeF sz = g.MeasureString(glyph, f);
                    g.DrawString(glyph, f, b, (Width - sz.Width) / 2f, (Height - sz.Height) / 2f);
                }
            }

            // 极细的左边框，和任务栏里的分隔线同风格
            using (Pen p = new Pen(Skin.Line))
                g.DrawLine(p, 0, Shell.S(12), 0, Height - Shell.S(12));
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            if (d > r.Height) d = r.Height;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ------------------------------------------------------------------
        //  交互
        // ------------------------------------------------------------------
        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            ApplyHover(true);
        }

        private void ApplyHover(bool on)
        {
            _hover = on;
            Invalidate();
            _owner.ShowSwapHint(on);
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false; _pressed = false; Invalidate();
            _owner.ShowSwapHint(false);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right) { _owner.ShowBandMenu(new Point(4, 4)); _pressed = false; Invalidate(); return; }
            if (_pressed) { _pressed = false; Invalidate(); _owner.ToggleCover(); ApplyPlacement(); }
        }

        /// <summary>给自检用：直接走一次「点下去再抬起」</summary>
        public void ClickForTest()
        {
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, Width / 2, Height / 2, 0));
        }
        public void HoverForTest(bool on) { ApplyHover(on); }

        public void StopFollow()
        {
            if (_follow != null) { _follow.Stop(); _follow.Dispose(); _follow = null; }
        }
    }

    // ========================================================================
    //  「谁在前」这个动作的两处入口共用的东西：
    //    · 条上第 4 个按钮（紧挨「下一页」右边）
    //    · 任务栏最右侧那个常驻小方块（SwitchPad）
    //  图标、id、提示文案都从这里取，两处说法永远一致。
    // ========================================================================
    public static class SwapEntry
    {
        /// <summary>按钮图标：Segoe MDL2 Assets 的「Switch」，两个箭头对着转，一眼看出是切换</summary>
        public const string Glyph = "";

        public const string Id = "swap";

        /// <summary>悬停时条上提的那句话：说明点下去会发生什么</summary>
        public static string Hint(bool coverTaskbar)
        {
            return coverTaskbar
                ? "点这里：把系统任务栏显示到前面（我的条原地让位，位置不变）"
                : "点这里：把我的条显示到前面（压在系统任务栏上，托盘照常可用）";
        }

        /// <summary>切换之后条上那句话</summary>
        public static string Done(bool coverTaskbar)
        {
            return coverTaskbar
                ? "已切到「我的条在前」：压在系统任务栏上，右下托盘照常可用"
                : "已切到「系统任务栏在前」：我的条原地让位（还在老位置上，不占额外屏幕）";
        }
    }
}
