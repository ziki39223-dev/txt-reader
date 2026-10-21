// ============================================================================
//  TxtBar 第二部分：L5 操控 + L6 集成（主窗体）
//  与 TxtBar.cs 一起编译：
//      csc /target:winexe /out:..\TxtBar.exe src\*.cs
//  带 /d:SELFTEST 编译时，运行 TxtBar.exe --selftest 可做无界面自检
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TxtBar
{
    // ========================================================================
    //  L5a  鼠标低级钩子（任务栏窗口不抢焦点，滚轮只能在钩子里收）
    // ========================================================================
    internal sealed class MouseWheelHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEWHEEL = 0x020A;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int id, Native.LowLevelProc fn, IntPtr hMod, uint tid);
        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wp, IntPtr lp);

        private Native.LowLevelProc _proc;      // 必须保住引用，否则被 GC 回收会崩
        private IntPtr _hook = IntPtr.Zero;
        public Func<Point, int, bool> OnWheel;  // 返回 true 表示已消费

        public void Install()
        {
            _proc = HookProc;
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
        }

        private IntPtr HookProc(int code, IntPtr wp, IntPtr lp)
        {
            if (code >= 0 && wp.ToInt32() == WM_MOUSEWHEEL)
            {
                try
                {
                    MSLLHOOKSTRUCT d = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lp, typeof(MSLLHOOKSTRUCT));
                    int delta = (short)((d.mouseData >> 16) & 0xFFFF);
                    if (OnWheel != null && OnWheel(new Point(d.pt.X, d.pt.Y), delta)) return new IntPtr(1);
                }
                catch { }
            }
            return CallNextHookEx(_hook, code, wp, lp);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        }
    }

    // ========================================================================
    //  L5b  键盘低级钩子
    //  默认只在「桌面 / 任务栏 / TxtBar 自己」是前台时接管按键，不打扰别的软件；
    //  要全局接管可在托盘菜单里打开。
    // ========================================================================
    internal sealed class KeyHook : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int id, Native.LowLevelProc fn, IntPtr hMod, uint tid);
        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private Native.LowLevelProc _proc;
        private IntPtr _hook = IntPtr.Zero;
        public Func<int, bool> OnKeyDown;
        public Func<bool> ShouldHandle;

        private static readonly int[] OwnedKeys = new int[]
        {
            0x21, 0x22,                         // PageUp / PageDown
            0x25, 0x27, 0x26, 0x28,             // ← → ↑ ↓
            0x24, 0x23,                         // Home / End
            0x20, 0x1B,                         // 空格 / Esc
            0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x30,  // 1..9 0 → 10%..100%
            0x6B, 0x6D, 0x6A,                   // 小键盘 + - *
            0xBB, 0xBD,                         // = / -
            0x76, 0x77, 0x78,                   // F7 覆盖切换 / F8 字号自适应 / F9 临时让路
            0xBF                                // / 打开查找
        };

        private static bool AnyModifierDown()
        {
            return (GetAsyncKeyState(0x10) & 0x8000) != 0
                || (GetAsyncKeyState(0x11) & 0x8000) != 0
                || (GetAsyncKeyState(0x12) & 0x8000) != 0;
        }

        public void Install()
        {
            _proc = HookProc;
            _hook = SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        }

        private IntPtr HookProc(int code, IntPtr wp, IntPtr lp)
        {
            if (code >= 0)
            {
                try
                {
                    if (HandleMessage(wp.ToInt32(), Marshal.ReadInt32(lp))) return new IntPtr(1);
                }
                catch { }
            }
            return CallNextHookEx(_hook, code, wp, lp);
        }

        /// <summary>
        /// 钩子回调的判定核心。
        ///
        /// 关键：低级键盘钩子对「按下」和「抬起」都会回调，必须只认「按下」。
        /// 以前没筛消息类型，敲一下方向键会被当成两次 Step，表现为「一次跳两行」——
        /// 实机量过：敲一下 ↓，位置从 12 直接到 14。
        ///
        /// 抽成独立方法是为了让自检能直接把（消息, 虚拟键）喂进来验证，不必真装钩子。
        /// 按住不放时系统会持续发 WM_KEYDOWN，正好用来连续翻行。
        /// </summary>
        public bool HandleMessage(int msg, int vk)
        {
            if (OnKeyDown == null) return false;
            if (msg != Native.WM_KEYDOWN && msg != Native.WM_SYSKEYDOWN) return false;
            if (ShouldHandle != null)
            {
                bool ok = false;
                try { ok = ShouldHandle(); } catch { }
                if (!ok) return false;
            }
            if (AnyModifierDown()) return false;
            bool owned = false;
            for (int i = 0; i < OwnedKeys.Length; i++) if (OwnedKeys[i] == vk) { owned = true; break; }
            if (!owned) return false;
            bool handled = false;
            try { handled = OnKeyDown(vk); } catch { }
            return handled;
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        }
    }

    // ========================================================================
    //  L5c  按钮
    // ========================================================================
    public sealed class BarButton
    {
        public string Id;
        public string Glyph;      // Segoe MDL2 Assets 图形字符，或纯文字
        public bool Wide;         // 纯文字按钮要窄一点
        public string Tip;
        public Rectangle Rect;
        public BarButton(string id, string glyph, string tip, bool wide)
        { Id = id; Glyph = glyph; Tip = tip; Wide = wide; }
    }

    // ========================================================================
    //  L6  主窗体：一条「任务栏」
    // ========================================================================
    public sealed class TxtBarForm : Form
    {
        private const int CLIP_MAX = 400000;

        private Book _book = new Book();
        private Memory _mem;
        private readonly List<BarButton> _btns = new List<BarButton>();
        private System.Windows.Forms.Timer _timer;
        private NotifyIcon _tray;
        private KeyHook _keyHook;
        private MouseWheelHook _wheelHook;
        private string _lastClip = "";
        private int _hover = -1;
        private int _pressed = -1;
        private string _flash = "";
        private string _flashShown = "";
        private int _flashTick = 0;
        private int _xOffset = 0;             // 正文横向微调（现在只能从菜单/键盘改，不能再拖）
        private FindPanel _find;
        private ContextMenuStrip _openMenu;      // 当前打开的菜单（开着的时候不抢按键）
        private SwitchPad _pad;                  // 任务栏最右侧那个常驻的切换入口

        private Font _fontText, _fontSmall, _fontGlyph;
        public bool SuppressUi = false;      // 自检模式：不显示窗口、不装钩子、不建托盘
        /// <summary>命令行带了 --global 时，启动就全局接管按键（在任何程序上按 Esc 都能退出）</summary>
        public static bool ForceGlobalKeys = false;

        public TxtBarForm()
        {
            _mem = Memory.Load();
            if (ForceGlobalKeys) _mem.GlobalKeys = true;   // --global：无视记忆里上次的设置，直接全局接管
            if (_mem.FollowSystemTheme) Skin.Init(Shell.LightTheme);   // 跟随系统时按当前系统主题上色
            _book.CharsPerLine = _mem.CharsPerLine;
            _xOffset = _mem.XOffset;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Text = "TxtBar";
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            BuildButtons();
            BuildFonts();

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

        public Memory Mem { get { return _mem; } }
        public Book Book { get { return _book; } }
        public List<BarButton> Buttons { get { return _btns; } }
        public int HoverIndex { get { return _hover; } }
        /// <summary>前台窗口是不是「桌面 / 任务栏」这类我们自己管的地盘</summary>
        public bool OnDesktopOrTaskbar
        {
            get
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == Handle) return true;
                StringBuilder sb = new StringBuilder(256);
                if (GetClassName(fg, sb, sb.Capacity) <= 0) return false;
                switch (sb.ToString())
                {
                    case "Progman":
                    case "WorkerW":
                    case "Shell_TrayWnd":
                    case "Shell_SecondaryTrayWnd":
                    case "Windows.UI.Core.CoreWindow":
                    case "XamlExplorerHostIslandWindow":
                    case "MultitaskingViewFrame":
                    case "TaskListThumbnailWnd":
                    case "TxtBarWnd":
                        return true;
                }
                return false;
            }
        }

        /// <summary>Esc：用户在打字/别处时只收面板，只有真的在桌面上才退出程序</summary>
        private void HandleEscape()
        {
            // 查找面板开着就先收面板（这样 Esc 一层一层退，符合直觉）
            if (_find != null && _find.Visible) { _find.Hide(); Flash("已收起查找面板"); return; }
            Quit();
        }

        // ------------------------------------------------------------------
        //  字体
        // ------------------------------------------------------------------
        private void BuildFonts()
        {
            string family = "Microsoft YaHei UI";
            bool ok = false;
            foreach (FontFamily ff in FontFamily.Families)
                if (string.Equals(ff.Name, family, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
            if (!ok) family = "Microsoft YaHei";

            int px = _mem.AutoFont ? AutoFontPx() : _mem.FontPx;
            if (_fontText != null) _fontText.Dispose();
            if (_fontSmall != null) _fontSmall.Dispose();
            if (_fontGlyph != null) _fontGlyph.Dispose();
            _fontText = new Font(family, px, GraphicsUnit.Pixel);
            _fontSmall = new Font(family, Math.Max(11f, px * 0.66f), GraphicsUnit.Pixel);
            _fontGlyph = new Font(GlyphFontName(), Math.Max(12f, GlyphFactor()), GraphicsUnit.Pixel);
        }

        /// <summary>字号自适应：让 CharsPerLine 个全角字正好铺满正文区</summary>
        public int AutoFontPx()
        {
            Rectangle a = TextArea();
            int px = (int)Math.Floor(a.Width / (double)Math.Max(1, _book.CharsPerLine));
            // 上限：不超过这条任务栏高度的 42%，否则一行字会把整条塞满，看着很挤
            int capByHeight = (int)Math.Floor(Math.Max(ClientSize.Height, Shell.TaskbarRect.Height) * 0.42);
            if (capByHeight < 11) capByHeight = 11;
            if (px > capByHeight) px = capByHeight;
            if (px > 40) px = 40;
            if (px < 11) px = 11;
            return px;
        }

        /// <summary>按钮上的图标：优先用 Segoe Fluent Icons（Win11），没有就退回 Segoe MDL2 Assets</summary>
        private string GlyphFontName()
        {
            return HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        }

        private static bool HasFont(string name)
        {
            foreach (FontFamily f in FontFamily.Families)
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private float GlyphFactor()
        {
            // 图标字号跟着按钮大小走，按钮大小又跟着任务栏高度走
            int size = Shell.S(20);
            if (_btns.Count > 0) size = Math.Max(size, _btns[0].Rect.Height);
            int px = (int)Math.Round(size * 0.46);
            if (px < 11) px = 11;
            if (px > 28) px = 28;
            return px;
        }

        private void BuildButtons()
        {
            _btns.Clear();
            // 主页面四个按钮：菜单 / 上一页 / 下一页 / 切换谁在前。
            // 「谁在前」有两个入口：条上这个（跟着条走）+ 任务栏最右侧那个常驻小方块（一直看得见）。
            // 正文区的起点看的是「最右边那个按钮」的右边，以后再加按钮文字也不会移位。
            _btns.Add(new BarButton("menu", "\uE700", "菜单：打开 / 查找 / 显示设置 / 退出（右键任意处同效）", false));
            _btns.Add(new BarButton("prev", "\uE76B", "上一页：往回一行（↑ ← PageUp，滚轮上滚同效）", false));
            _btns.Add(new BarButton("next", "\uE76C", "下一页：往下走一行（↓ → PageDown，滚轮下滚同效）", false));
            // 切换「谁在前」的入口就挨着「下一页」右边（F7 同效）。
            // 图标用强调色 = 我的条压在最前；用普通色 = 系统任务栏在前。
            _btns.Add(new BarButton(SwapEntry.Id, SwapEntry.Glyph,
                "切换「谁在前面」：我的条在前 / 系统任务栏在前（F7 同效）", false));
        }

        // ------------------------------------------------------------------
        //  几何：永远占系统任务栏这一条（切换不再上下搬家）
        // ------------------------------------------------------------------
        /// <summary>
        /// 我的条永远只占系统任务栏这一条：左端对齐、高度相同、右端止于托盘左边，
        /// 右下角的托盘/时钟/「显示桌面」整块留给系统。
        /// 「谁在前面」只改层叠、不改位置 —— 所以切换时这条绝不会浮到任务栏上方去。
        /// </summary>
        public Rectangle TargetRect()
        {
            Rectangle r = Shell.TaskbarRect;
            int w = r.Width - (Shell.TrayHwnd != IntPtr.Zero ? Shell.TrayWidth : Shell.S(180));
            if (w < (int)(r.Width * 0.4)) w = (int)(r.Width * 0.4);
            return new Rectangle(r.Left, r.Top, w, r.Height);
        }

        public void ApplyGeometry()
        {
            Shell.Detect();
            Rectangle t = TargetRect();
            // 谁在前：我的条在前 = 置顶（压住任务栏的按钮区，托盘那截我们没占，照常可用）；
            //         系统任务栏在前 = 原地沉到任务栏后面（位置尺寸一个字都不变，只是看不见我们）。
            if (!SuppressUi && TopMost != _mem.CoverTaskbar) TopMost = _mem.CoverTaskbar;
            IntPtr after = _mem.CoverTaskbar ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST;
            if (!SuppressUi)
            {
                Native.SetWindowPos(Handle, after, t.Left, t.Top, t.Width, t.Height,
                    Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            }
            else if (IsHandleCreated)
            {
                // 自检：句柄存在就按真实位置摆好，但不显示出来（层叠照真实模式来，方便核对 exStyle）
                Native.SetWindowPos(Handle, after, t.Left, t.Top, t.Width, t.Height,
                    Native.SWP_NOACTIVATE);
            }
            else if (ClientSize.Width != t.Width || ClientSize.Height != t.Height)
            {
                ClientSize = new Size(t.Width, t.Height);
            }
            LayoutButtons();
            BuildFonts();
            Invalidate();
            WriteStatus();
        }

        /// <summary>为自检模式准备一个和任务栏一样大的画布</summary>
        public void LayoutForTest()
        {
            Rectangle t = TargetRect();
            ClientSize = new Size(t.Width, t.Height);
            LayoutButtons();
            BuildFonts();
            WriteStatus();
        }

        private void LayoutButtons()
        {
            int h = ClientSize.Height;
            int pad = Shell.S(5), gap = Shell.S(2);
            int size = (int)Math.Round(h * 0.66);
            if (size > h - 2) size = h - 2;
            if (size < Shell.S(20)) size = Shell.S(20);
            int y = (h - size) / 2;

            for (int i = 0; i < _btns.Count; i++)
            {
                BarButton b = _btns[i];
                int w = b.Wide ? (int)(size * 1.15) : size;
                b.Rect = new Rectangle(pad + i * (size + gap), y, w, size);
            }
        }

        /// <summary>最右边那个按钮的右边界：正文就从这里开始（按钮增减都不会让文字移位）</summary>
        private int ButtonsRight()
        {
            int r = Shell.S(5);
            for (int i = 0; i < _btns.Count; i++) if (_btns[i].Rect.Right > r) r = _btns[i].Rect.Right;
            return r;
        }

        /// <summary>状态区的右边界：条的最右边留一点边距</summary>
        private int SwatLeft()
        {
            return ClientSize.Width - Shell.S(8);
        }

        /// <summary>状态区宽度：一行紧凑显示（进度 + 文件 + 每行字数 + 空行状态）</summary>
        public int StatusWidth()
        {
            int h = Math.Max(ClientSize.Height, Shell.TaskbarRect.Height);
            int w = (int)Math.Round(h * 5.6);
            return Math.Max(Shell.S(160), w);
        }

        /// <summary>
        /// 正文区域：起点紧跟在菜单按钮右边（所以「文字显示的地方」不因按钮变化而改变），
        /// 终点让出右侧状态区。正文只占一条，垂直居中。
        /// </summary>
        public Rectangle TextArea()
        {
            int left = ButtonsRight() + Shell.S(10);
            int right = ClientSize.Width - Shell.S(8) - StatusWidth();
            if (right - left < Shell.S(40)) right = left + Shell.S(40);
            return new Rectangle(left, 0, Math.Max(Shell.S(20), right - left), ClientSize.Height);
        }

        // ------------------------------------------------------------------
        //  绘制
        // ------------------------------------------------------------------
        public void Render(Graphics g, Size size)
        {
            if (_peek)
            {
                // 让路期间：只留一条极窄的「回来」提示，其余全部透明，系统任务栏露出来
                using (Brush bg = new SolidBrush(Color.FromArgb(180, Skin.Bg))) g.FillRectangle(bg, 0, 0, size.Width, Shell.S(14));
                using (Brush fg = new SolidBrush(Skin.Fg))
                using (Font f = new Font(_fontSmall.FontFamily, _fontSmall.Size, GraphicsUnit.Pixel))
                    g.DrawString("TxtBar 让路中 · 按 F9 回来", f, fg, Shell.S(10), 0);
                return;
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (Brush bg = new SolidBrush(Skin.Bg)) g.FillRectangle(bg, 0, 0, size.Width, size.Height);
            using (Pen p = new Pen(Skin.Light ? Color.FromArgb(255, 255, 255) : Color.FromArgb(54, 54, 54)))
                g.DrawLine(p, 0, 0, size.Width, 0);

            DrawText(g, size);
            DrawStatus(g, size);
            DrawButtons(g, size);     // 菜单 / 上一页 / 下一页 / 切换
        }

        protected override void OnPaint(PaintEventArgs e) { Render(e.Graphics, ClientSize); }

        public Bitmap Snapshot()
        {
            int w = ClientSize.Width > 0 ? ClientSize.Width : TargetRect().Width;
            int h = ClientSize.Height > 0 ? ClientSize.Height : TargetRect().Height;
            Bitmap bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h));
            using (Graphics g = Graphics.FromImage(bmp)) Render(g, new Size(bmp.Width, bmp.Height));
            return bmp;
        }

        private void DrawText(Graphics g, Size size)
        {
            Rectangle a = TextArea();
            if (!_book.Loaded)
            {
                TextRenderer.DrawText(g, "点最左边按钮 → 打开 txt（或用右键菜单）",
                    _fontSmall, a, Skin.Dim,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
                return;
            }
            string line = _book.Current();
            if (line.Length == 0)
            {
                TextRenderer.DrawText(g, "（本段是空行）", _fontSmall, a, Skin.Dim,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }
            Rectangle a2 = new Rectangle(a.X + _xOffset, a.Y, a.Width, a.Height);

            // 查找命中：整行铺一层淡底 + 把命中的那几个字涂成高亮色，一眼看出命中了什么
            int hitFrom, hitLen;
            bool hit = _book.CurrentHitRange(_book.FindQuery, out hitFrom, out hitLen) && hitFrom >= 0 && hitLen > 0;
            if (hit)
            {
                Rectangle hl = new Rectangle(a.X - Shell.S(6), Shell.S(2),
                                             Math.Min(a.Width + Shell.S(12), size.Width - a.X), size.Height - Shell.S(4));
                using (Brush hb = new SolidBrush(Skin.Light ? Color.FromArgb(255, 246, 196) : Color.FromArgb(74, 66, 22)))
                    g.FillRectangle(hb, hl);
            }

            bool cut = hit && hitFrom + hitLen <= line.Length
                ? DrawLineHighlighted(g, line, _fontText, Skin.Fg, Skin.Accent, a2, hitFrom, hitLen)
                : DrawLine(g, line, _fontText, Skin.Fg, a2);
            if (cut)
            {
                using (Brush b = new SolidBrush(Skin.Accent))
                    g.FillEllipse(b, a.Right - Shell.S(5), size.Height / 2 - Shell.S(2), Shell.S(5), Shell.S(5));
            }
        }

        /// <summary>把 [from, from+len) 这几个字用高亮色画出来，其余正常色；返回是否被截断</summary>
        private bool DrawLineHighlighted(Graphics g, string text, Font font, Color normal, Color hi,
                                         Rectangle area, int from, int len)
        {
            Size need = TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, area.Height),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            bool cut = need.Width > area.Width;
            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
                                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                                    TextFormatFlags.Left | TextFormatFlags.PreserveGraphicsClipping;

            if (cut)
            {
                // 太长就先按普通方式画（带省略号），避免分段测量与截断互相打架
                TextRenderer.DrawText(g, text, font, area, normal, flags | TextFormatFlags.EndEllipsis);
                return true;
            }

            string head = text.Substring(0, Math.Min(from, text.Length));
            string mid = from < text.Length ? text.Substring(from, Math.Min(len, text.Length - from)) : "";
            string tail = (from + len) < text.Length ? text.Substring(from + len) : "";

            int x = area.X;
            int y = area.Y;
            int hgt = area.Height;
            SizeF m;
            if (head.Length > 0)
            {
                m = TextRenderer.MeasureText(g, head, font, new Size(int.MaxValue, hgt),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, head, font, new Rectangle(x, y, (int)Math.Ceiling(m.Width) + 2, hgt), normal, flags);
                x += (int)Math.Round(m.Width);
            }
            if (mid.Length > 0)
            {
                m = TextRenderer.MeasureText(g, mid, font, new Size(int.MaxValue, hgt),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                using (Brush b = new SolidBrush(hi))
                {
                    int mw = (int)Math.Ceiling(m.Width) + 2;
                    g.FillRectangle(b, x, y + hgt / 4, mw, Math.Max(2, hgt / 2));
                }
                TextRenderer.DrawText(g, mid, font, new Rectangle(x, y, (int)Math.Ceiling(m.Width) + 2, hgt),
                    Skin.Light ? Color.White : Color.FromArgb(20, 20, 20), flags);
                x += (int)Math.Round(m.Width);
            }
            if (tail.Length > 0)
                TextRenderer.DrawText(g, tail, font, new Rectangle(x, y, Math.Max(0, area.Right - x), hgt), normal, flags);
            return false;
        }

        private bool DrawLine(Graphics g, string text, Font font, Color color, Rectangle area)
        {
            Size need = TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, area.Height),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            bool cut = need.Width > area.Width;
            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
                                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                                    TextFormatFlags.Left | TextFormatFlags.PreserveGraphicsClipping;
            if (cut) flags |= TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, text, font, area, color, flags);
            return cut;
        }

        /// <summary>
        /// 右侧状态区：一行紧凑显示「进度 · 文件 · 每行字数 · 空行状态」。
        /// 底部那条细线是阅读进度。这里只放信息，不放按钮 —— 所有操作都在菜单里。
        /// </summary>
        private void DrawStatus(Graphics g, Size size)
        {
            int w = StatusWidth();
            int right = Math.Min(size.Width - Shell.S(8), SwatLeft());
            int left = Math.Max(ButtonsRight(), right - w);
            Rectangle box = new Rectangle(left, 0, Math.Max(0, right - left), size.Height);

            using (Pen p = new Pen(Skin.Line))
                g.DrawLine(p, box.Left - Shell.S(8), Shell.S(12), box.Left - Shell.S(8), size.Height - Shell.S(12));

            string prog, rest;
            if (_book.Loaded)
            {
                prog = string.Format("{0}/{1} 行 · {2}%", _book.Index + 1, _book.Lines.Count, _book.Percent);
                if (_book.FindQuery.Length > 0)
                {
                    int hits = _book.CountHits(_book.FindQuery, true);
                    string q = _book.FindQuery.Length > 8 ? _book.FindQuery.Substring(0, 8) + "…" : _book.FindQuery;
                    prog = "找「" + q + "」" + hits + "处 · " + prog;
                }
                string name = _book.Title;
                if (name.Length > 14) name = name.Substring(0, 13) + "…";
                rest = name + " · " + _book.CharsPerLine + "字/行 · " + _book.EncName +
                       (_mem.SkipBlank ? " · 空行已隐藏" : "");
            }
            else
            {
                prog = "尚未打开文件";
                rest = _book.CharsPerLine + " 字/行 · 自动记忆已开启";
            }

            using (Brush b1 = new SolidBrush(Skin.Fg))
            using (Brush b2 = new SolidBrush(Skin.Dim))
            {
                // 进度先画（它是重点），剩余宽度给文件名那一段；空间不够就从左边裁
                SizeF s1 = g.MeasureString(prog, _fontSmall);
                g.DrawString(prog, _fontSmall, b1, box.X, (size.Height - s1.Height) / 2f);
                float x = box.X + s1.Width + Shell.S(10);
                float avail = box.Right - x;
                if (avail > Shell.S(30))
                {
                    string tail = rest;
                    while (tail.Length > 4 && g.MeasureString(tail, _fontSmall).Width > avail) tail = tail.Substring(1);
                    if (tail.Length < rest.Length) tail = "…" + tail;
                    SizeF s2 = g.MeasureString(tail, _fontSmall);
                    g.DrawString(tail, _fontSmall, b2, x, (size.Height - s2.Height) / 2f);
                }
            }

            if (_book.Loaded)
            {
                int y = size.Height - Shell.S(3);
                using (Brush t = new SolidBrush(Skin.Track)) g.FillRectangle(t, box.X, y, box.Width, Shell.S(2));
                using (Brush a = new SolidBrush(Skin.Accent)) g.FillRectangle(a, box.X, y, box.Width * _book.Percent / 100, Shell.S(2));
            }
        }

        private void DrawButtons(Graphics g, Size size)
        {
            for (int i = 0; i < _btns.Count; i++)
            {
                BarButton b = _btns[i];
                bool active = (b.Id == "auto" && _mem.AutoPlay);
                // 「谁在前」这个按钮：我的条在前时用强调色常亮，系统任务栏在前时用普通色。
                bool swapOn = (b.Id == SwapEntry.Id && _mem.CoverTaskbar);
                bool lit = active || swapOn;
                if (i == _hover || i == _pressed || active)
                {
                    using (GraphicsPath path = RoundRect(b.Rect, Shell.S(6)))
                    using (Brush br = new SolidBrush(i == _pressed ? Skin.Pressed : Skin.Hover))
                        g.FillPath(br, path);
                }
                Color fg = lit ? Skin.Accent : Skin.Fg;
                Font f = b.Wide ? _fontSmall : _fontGlyph;
                TextRenderer.DrawText(g, b.Glyph, f, b.Rect, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }

        // 主页面保持干净：不再在条上画提示文字。
        // 提示改由托盘气泡 + 状态区左侧那点小变化承担（见 Flash）。

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
        //  鼠标：点按钮 / 空白处拖动平移正文
        // ------------------------------------------------------------------
        public int HitTest(Point p)
        {
            for (int i = 0; i < _btns.Count; i++) if (_btns[i].Rect.Contains(p)) return i;
            return -1;
        }

        // 诊断计数：鼠标事件到底有没有进来（写进 status.json，出问题一眼能看出）
        public int DbgMove = 0, DbgDown = 0, DbgUp = 0, DbgLastHit = -99, DbgLastPressed = -99;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            DbgMove++;
            int hit = HitTest(e.Location);
            DbgLastHit = hit;
            if (hit != _hover)
            {
                int prev = _hover;
                _hover = hit;
                Invalidate();
                // 只在进出「谁在前」按钮的那一下提一句 / 撤一句，别的按钮不打扰
                bool wasSwap = prev >= 0 && prev < _btns.Count && _btns[prev].Id == SwapEntry.Id;
                bool isSwap = hit >= 0 && _btns[hit].Id == SwapEntry.Id;
                if (isSwap != wasSwap) ShowSwapHint(isSwap);
            }
            // 正文不再能拖动：空白处只是普通指针，避免手一抖就把文字拽走
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            DbgDown++;
            int hit = HitTest(e.Location);
            if (hit < 0) return;              // 空白处按下不做任何事（拖动已取消）
            _pressed = hit;
            DbgLastPressed = hit;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            DbgUp++;
            HandleClickAt(e.Location, e.Button);
        }

        /// <summary>
        /// 一次点击的完整处理（真实鼠标、自检、脚本模拟都走这里，保证测的就是真实路径）。
        /// 注意：这里已经没有拖动逻辑了 —— 正文只能通过按钮/键盘移动。
        /// </summary>
        public void HandleClickAt(Point p, MouseButtons button)
        {
            if (button == MouseButtons.Right)
            {
                if (!SuppressUi) ShowBandMenu(p);
                return;
            }
            int hit = HitTest(p);
            if (hit >= 0 && hit == _pressed) RunCommand(_btns[hit].Id);
            _pressed = -1;
            Invalidate();
        }

        /// <summary>在条上任意位置点右键：出菜单（不抢焦点，托盘窗口也不怕）</summary>
        public void ShowBandMenu(Point at)
        {
            ContextMenuStrip m = BuildMenu();
            _openMenu = m;
            // 菜单关掉后立刻清空引用，并把键盘交还给我们
            m.Closed += delegate { if (_openMenu == m) _openMenu = null; };
            m.Show(new Point(Left + at.X, Top), ToolStripDropDownDirection.AboveRight);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1)
            {
                bool wasSwap = _hover < _btns.Count && _btns[_hover].Id == SwapEntry.Id;
                _hover = -1;
                Invalidate();
                if (wasSwap) ShowSwapHint(false);
            }
        }

        // ------------------------------------------------------------------
        //  命令
        // ------------------------------------------------------------------
        public void RunCommand(string id)
        {
            Program.Log("按钮命令：" + id);     // 排错用：哪个按钮被按了、什么时候按的
            switch (id)
            {
                case "menu": ShowBandMenu(new Point(Buttons[0].Rect.Right, ClientSize.Height / 2)); break;
                case SwapEntry.Id: ToggleCover(); break;
                case "open": DoOpenFile(); break;
                case "find": OpenFind(); break;
                case "clip": ReadClipboard(true); break;
                case "prev": Step(-1); break;
                case "next": Step(1); break;
                case "auto": ToggleAuto(); break;
                case "minus": ChangeChars(-2); break;
                case "plus": ChangeChars(2); break;
                case "fontmd": ChangeFont(-2); break;
                case "fontpl": ChangeFont(2); break;
                case "autoFont": ToggleAutoFont(); break;
                case "mode": ToggleCover(); break;
                case "closer": case "exit": Quit(); break;
            }
        }

        // ------------------------------------------------------------------
        //  查找
        // ------------------------------------------------------------------
        /// <summary>打开查找面板；如果上一次查过词，就直接带着它打开（接着上次查找继续找）</summary>
        public void OpenFind()
        {
            if (SuppressUi) { Flash("自检模式不弹查找面板"); return; }
            if (!_book.Loaded) { Flash("还没有打开文件，先点最左边的文件夹按钮"); return; }
            if (_find == null) { _find = new FindPanel(this); }
            _find.OpenWith(_book.FindQuery);
            WriteStatus();
        }

        /// <summary>不弹面板，直接往下找（F3）</summary>
        public void FindAgain(bool forward)
        {
            if (!_book.Loaded) { Flash("还没有打开文件"); return; }
            if (_book.FindQuery.Length == 0) { OpenFind(); return; }
            bool hit = forward ? _book.FindNext(_book.FindQuery, true) : _book.FindPrev(_book.FindQuery, true);
            if (hit) BookRefreshAndFlash("查找：" + (forward ? "下一个" : "上一个") + "「" + _book.FindQuery + "」");
            else { BookRefreshAndFlash(null); Flash(_book.FindError); }
        }

        /// <summary>查找命中后刷新画面并提示「第几处」</summary>
        public void BookRefreshAndFlash(string what)
        {
            Invalidate();
            if (what == null) return;
            int hits = _book.CountHits(_book.FindQuery, true);
            Flash(what + " → 第 " + (_book.Index + 1) + " 行（共 " + hits + " 处命中）");
        }
        public bool HasFindPanel { get { return _find != null && _find.Visible; } }

        /// <summary>菜单是否开着（开着的时候我们把按键让给菜单处理）</summary>
        public bool MenuOpen
        {
            get
            {
                try { return _openMenu != null && !_openMenu.IsDisposed && _openMenu.Visible; }
                catch { return false; }
            }
        }

        /// <summary>当前正文的横向微调量（自检用）</summary>
        public int CurrentXOffset { get { return _xOffset; } }

        /// <summary>自检用：把鼠标事件直接喂给真实的处理函数</summary>
        public void HandleMouseMoveForTest(Point p) { OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, p.X, p.Y, 0)); }
        public void HandleMouseDownForTest(Point p) { OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0)); }
        public void HandleMouseUpForTest(Point p) { OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0)); }

        private void DoOpenFile()
        {
            if (SuppressUi) { Flash("自检模式不弹文件对话框"); return; }
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择要逐行阅读的 txt";
                dlg.Filter = "文本文件 (*.txt;*.md;*.log)|*.txt;*.md;*.log|所有文件 (*.*)|*.*";
                dlg.CheckFileExists = true;
                if (!string.IsNullOrEmpty(_book.Path) && File.Exists(_book.Path))
                    dlg.InitialDirectory = System.IO.Path.GetDirectoryName(_book.Path);
                if (dlg.ShowDialog() != DialogResult.OK) return;
                OpenBook(dlg.FileName);
            }
        }

        /// <summary>叫出清洗工具（TxtClean.exe），洗完自动把「已清洗」目录带到前面</summary>
        public void RunCleaner()
        {
            if (SuppressUi) { Flash("自检模式不弹清洗工具"); return; }
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string exe = Path.Combine(dir, "TxtClean.exe");
                if (!File.Exists(exe))
                {
                    Flash("没找到 TxtClean.exe（它应该和 TxtBar.exe 放在一起）");
                    return;
                }
                Process.Start(exe);
                Flash("清洗工具已打开；洗完结果在「书库\\已清洗」里");
            }
            catch (Exception ex) { Flash("打不开清洗工具：" + ex.Message); }
        }

        /// <summary>打开「书库\已清洗」文件夹</summary>
        public void OpenCleanFolder()
        {
            if (SuppressUi) { Flash("自检模式不弹文件夹"); return; }
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Cleaner.RootName, Cleaner.OutName);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
                Flash("已打开：" + dir);
            }
            catch (Exception ex) { Flash("打不开文件夹：" + ex.Message); }
        }

        public void OpenBook(string path)
        {
            _book.SkipBlank = _mem.SkipBlank;      // 先套上用户的开关，再切行
            _book.LoadFile(path);
            if (_book.Error.Length > 0) { Flash(_book.Error); return; }
            if (_book.Lines.Count == 0) { Flash("这个文件是空的：" + System.IO.Path.GetFileName(path)); return; }

            if (!_mem.Recent.Contains(path)) _mem.Recent.Insert(0, path);
            while (_mem.Recent.Count > 12) _mem.Recent.RemoveAt(_mem.Recent.Count - 1);

            int saved;
            if (_mem.Pos.TryGetValue(path, out saved)) _book.Index = Math.Max(0, Math.Min(_book.Lines.Count - 1, saved));
            int sc;
            if (_mem.Scroll.TryGetValue(path, out sc)) _xOffset = sc; else _xOffset = _mem.XOffset;

            BuildFonts();
            SaveMemory();
            Flash("已打开《" + _book.Title + "》第 " + (_book.Index + 1) + "/" + _book.Lines.Count +
                  " 行（" + _book.EncName + "），下次打开自动接着这里");
            if (_mem.AutoPlay) UpdateAutoTimer();
        }

        public void ReadClipboard(bool manual)
        {
            string t = null;
            try { if (Clipboard.ContainsText()) t = Clipboard.GetText(); }
            catch { }

            if (string.IsNullOrEmpty(t)) { if (manual) Flash("剪贴板里没有文字"); return; }
            if (t.Length > CLIP_MAX) { if (manual) Flash("剪贴板文本太大，没有载入"); return; }
            if (t == _lastClip) { if (manual) Flash("剪贴板内容没有变化"); return; }

            // 关键：正在读文件时，剪贴板的变化绝不覆盖正文，只提醒一句；
            // 想读剪贴板就点那个按钮（那时才切过去，之后自动跟随继续生效）。
            if (!manual && _book.Loaded && !string.IsNullOrEmpty(_book.Path))
            {
                _lastClip = t;
                Flash("剪贴板有新内容了 —— 想读它，点一下左边的剪贴板按钮");
                return;
            }
            _lastClip = t;

            _book.Path = "";
            _book.SkipBlank = _mem.SkipBlank;
            _book.LoadBytes(new UTF8Encoding(false).GetBytes(t), "");
            _book.Title = "剪贴板内容";
            _book.EncName = "剪贴板";
            if (_book.Lines.Count == 0) { if (manual) Flash("剪贴板内容是空的"); return; }
            BuildFonts();
            SaveMemory();
            if (manual) Flash("已切到剪贴板内容，共 " + _book.Lines.Count + " 行");
        }

        public void Step(int d)
        {
            if (!_book.Loaded) { if (!SuppressUi) DoOpenFile(); else Flash("尚未打开文件"); return; }
            int n = _book.Index + d;
            if (n < 0) { Flash("已经是第一行"); return; }
            if (n >= _book.Lines.Count)
            {
                if (_mem.AutoPlay) { _mem.AutoPlay = false; UpdateAutoTimer(); }
                Flash("已经读到最后一行");
                return;
            }
            _book.Index = n;
            SaveMemory();
            Invalidate();
        }

        public void ToggleAuto()
        {
            if (!_book.Loaded) { if (!SuppressUi) DoOpenFile(); return; }
            _mem.AutoPlay = !_mem.AutoPlay;
            UpdateAutoTimer();
            Flash(_mem.AutoPlay ? ("自动翻行：每 " + _mem.AutoDelay.ToString("0.#") + " 秒一行（空格暂停）") : "自动翻行已暂停");
            SaveMemory();
        }

        /// <summary>把正文横向偏移归零（以前支持拖动时可能留下偏移，这里一键清掉）</summary>
        public void ResetXOffset()
        {
            _xOffset = 0;
            _mem.XOffset = 0;
            SaveMemory();
            Flash("正文已横向归位（左边距恢复默认）");
            Invalidate();
        }

        /// <summary>
        /// 把正文里的空行永久清掉（连原文一起整理，之后无论怎么调字数都不会再出现空行）。
        /// 会先备份一份原稿在旁边，万一想反悔还能拿回来。
        /// </summary>
        public void StripBlankLinesNow()
        {
            if (!_book.Loaded) { Flash("还没有打开文件"); return; }

            string[] raw = _book.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int removed = 0;
            foreach (string line in raw)
            {
                if (line.Trim().Length == 0) { removed++; continue; }
                sb.Append(line).Append('\n');
            }
            if (removed == 0) { Flash("正文里本来就没有空行"); return; }

            string backup = "";
            if (!string.IsNullOrEmpty(_book.Path) && File.Exists(_book.Path))
            {
                backup = _book.Path + ".bak";
                try { if (!File.Exists(backup)) File.Copy(_book.Path, backup, false); }
                catch { backup = ""; }
            }

            int orig = _book.OrigLine.Count > 0 ? _book.OrigLine[Math.Min(_book.Index, _book.OrigLine.Count - 1)] : 0;
            _book.SetText(sb.ToString());
            _book.SkipBlank = true;
            _mem.SkipBlank = true;
            _book.Index = 0;

            SaveMemory();
            Invalidate();
            Flash("已清掉 " + removed + " 个空行" +
                  (backup.Length > 0 ? "，原稿备份在 " + System.IO.Path.GetFileName(backup) : "") +
                  "（共 " + _book.Lines.Count + " 行）");
        }

        /// <summary>空行显示开关：关掉之后正文里不会再出现空行</summary>
        public void ToggleSkipBlank()
        {
            _mem.SkipBlank = !_mem.SkipBlank;
            _book.SkipBlank = _mem.SkipBlank;
            int orig = _book.OrigLine.Count > 0 ? _book.OrigLine[Math.Min(_book.Index, _book.OrigLine.Count - 1)] : 0;
            _book.Rebuild();
            // 尽量停在同一段，别跳回开头
            for (int i = 0; i < _book.OrigLine.Count; i++)
                if (_book.OrigLine[i] >= orig) { _book.Index = i; break; }
            SaveMemory();
            Invalidate();
            Flash(_mem.SkipBlank
                ? "已开启：正文里不再显示空行"
                : "已关闭：空行会照样显示（想再藏起来就再点一次）");
        }

        public void ChangeChars(int d)
        {
            _book.SetCharsPerLine(_book.CharsPerLine + d);
            _mem.CharsPerLine = _book.CharsPerLine;
            BuildFonts();
            SaveMemory();
            Flash("每行 " + _book.CharsPerLine + " 字（停在原处，不跳段）");
            Invalidate();
        }

        public void ChangeFont(int d)
        {
            if (_mem.AutoFont)
            {
                _mem.AutoFont = false;
                _mem.FontPx = AutoFontPx();
                Flash("字号改成手动模式");
            }
            _mem.FontPx = Math.Max(11, Math.Min(40, _mem.FontPx + d));
            BuildFonts();
            SaveMemory();
            Invalidate();
        }

        public void ToggleAutoFont()
        {
            _mem.AutoFont = !_mem.AutoFont;
            if (!_mem.AutoFont) _mem.FontPx = AutoFontPx();
            BuildFonts();
            SaveMemory();
            Flash(_mem.AutoFont ? ("字号自适应已开（" + AutoFontPx() + "px，跟着每行字数走）") : ("字号已固定为 " + _mem.FontPx + "px"));
            Invalidate();
        }

        private void UpdateAutoTimer()
        {
            if (_timer == null)
            {
                _timer = new System.Windows.Forms.Timer();
                _timer.Tick += delegate { if (_mem.AutoPlay) Step(1); };
            }
            _timer.Interval = (int)Math.Max(200, _mem.AutoDelay * 1000);
            if (_mem.AutoPlay) _timer.Start(); else _timer.Stop();
            Invalidate();
        }

        /// <summary>
        /// 反馈提示：主页面不再画文字，提示用托盘气泡弹一下（找不到托盘就只记进状态文件）。
        /// </summary>
        public void Flash(string msg)
        {
            _flash = msg == null ? "" : msg;
            _flashTick = 30;
            Invalidate();
            if (!SuppressUi) { try { WriteStatus(); } catch { } }
            if (!SuppressUi && _tray != null && _tray.Visible && msg != null && msg.Length > 0)
            {
                try
                {
                    _tray.BalloonTipTitle = "TxtBar";
                    _tray.BalloonTipText = msg.Length > 120 ? msg.Substring(0, 120) + "…" : msg;
                    _tray.BalloonTipIcon = ToolTipIcon.None;
                    _tray.ShowBalloonTip(2500);
                }
                catch { }
            }
        }
        public string FlashText { get { return _flashShown.Length > 0 ? _flashShown : _flash; } }

        // ------------------------------------------------------------------
        //  钩子
        // ------------------------------------------------------------------
        public void InstallHooks()
        {
            _keyHook = new KeyHook();
            _keyHook.ShouldHandle = ShouldHandleKeys;
            _keyHook.OnKeyDown = delegate (int vk)
            {
                switch (vk)
                {
                    case 0x21: case 0x26: case 0x25: Step(-1); return true;
                    case 0x22: case 0x28: case 0x27: Step(1); return true;
                    case 0x24: JumpToFirst(); return true;
                    case 0x23: JumpToLast(); return true;
                    case 0x20: ToggleAuto(); return true;
                    case 0x1B: HandleEscape(); return true;
                    case 0x6B: case 0xBB: ChangeChars(2); return true;
                    case 0x6D: case 0xBD: ChangeChars(-2); return true;
                    case 0x6A: ToggleAuto(); return true;
                    case 0x76: ToggleCover(); return true;
                    case 0x77: ToggleAutoFont(); return true;
                    case 0x78: TogglePeek(); return true;
                    case 0xBF: OpenFind(); return true;        // / 打开查找
                    case 0x5D: ShowBandMenu(new Point(ClientSize.Width / 2, ClientSize.Height / 2)); return true;  // 菜单键
                }
                return false;
            };
            _keyHook.Install();

            _wheelHook = new MouseWheelHook();
            _wheelHook.OnWheel = delegate (Point pt, int delta)
            {
                if (SuppressUi || !Visible) return false;
                if (pt.X < Left || pt.X > Right || pt.Y < Top || pt.Y > Bottom) return false;
                bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
                if (ctrl) { ChangeFont(delta < 0 ? -2 : 2); return true; }   // Ctrl+滚轮 = 字号
                Step(delta < 0 ? 1 : -1);
                return true;
            };
            _wheelHook.Install();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>只在「桌面 / 任务栏 / TxtBar 自己」是前台时接管按键</summary>
        public bool ShouldHandleKeys()
        {
            // 自己的菜单开着时，按键交给菜单处理：↑↓ 选条目、Esc 关菜单、回车执行。
            // 否则 Esc 会被我们吃掉直接退出程序，菜单却还挂在屏幕上。
            if (MenuOpen) return false;
            if (_mem.GlobalKeys) return true;
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle) return true;
            StringBuilder sb = new StringBuilder(256);
            int n = GetClassName(fg, sb, sb.Capacity);
            if (n <= 0) return false;
            string cls = sb.ToString();
            switch (cls)
            {
                case "Progman":
                case "WorkerW":
                case "Shell_TrayWnd":
                case "Shell_SecondaryTrayWnd":
                case "Windows.UI.Core.CoreWindow":
                case "XamlExplorerHostIslandWindow":
                case "MultitaskingViewFrame":
                case "TaskListThumbnailWnd":
                case "TxtBarWnd":
                    return true;
            }
            return false;
        }

        public void JumpToFirst() { if (_book.Loaded) { _book.Index = 0; SaveMemory(); Invalidate(); Flash("已到第一行"); } }
        public void JumpToLast() { if (_book.Loaded) { _book.Index = _book.Lines.Count - 1; SaveMemory(); Invalidate(); Flash("已到末行"); } }
        public void JumpToPercent(int pct)
        {
            if (!_book.Loaded) return;
            _book.Index = Math.Max(0, Math.Min(_book.Lines.Count - 1,
                (int)Math.Round((_book.Lines.Count - 1) * pct / 100.0)));
            SaveMemory(); Invalidate();
            Flash("跳到 " + pct + "%（第 " + (_book.Index + 1) + " 行）");
        }

        /// <summary>
        /// 切换「谁在前面」（只改层叠，不改位置）：
        ///   我的条在前 —— 我的条压在任务栏这一条上，托盘/时钟/显示桌面那一截我们没占，照常可用
        ///   系统任务栏在前 —— 我的条原地沉到任务栏后面，位置尺寸完全不动，所以绝不会跳到任务栏上方
        /// 入口有两个：条上「下一页」右边那个 ⇄ 按钮，和任务栏最右侧那个常驻小方块
        /// （F7 也行，条上的菜单里也能切）。
        /// </summary>
        public void ToggleCover()
        {
            _mem.CoverTaskbar = !_mem.CoverTaskbar;
            _mem.KeepTrayVisible = true;      // 不管谁在前，托盘那一段永远留给系统
            ApplyGeometry();
            MoveSwapPad();
            SaveMemory();
            WriteStatus();
            Flash(SwapEntry.Done(_mem.CoverTaskbar));
        }

        /// <summary>临时让路：把正文整体藏起来一小会儿，露出系统任务栏（F9 或点按钮旁边空白处右键）</summary>
        public void TogglePeek()
        {
            _peek = !_peek;
            if (_peek)
            {
                _peekTimer = new System.Windows.Forms.Timer();
                _peekTimer.Interval = 3000;
                _peekTimer.Tick += delegate
                {
                    _peekTimer.Stop(); _peekTimer.Dispose(); _peekTimer = null;
                    _peek = false; Invalidate();
                };
                _peekTimer.Start();
                Flash("让路 3 秒：现在点系统任务栏上的任何东西（再按 F9 可马上回来）");
            }
            else
            {
                if (_peekTimer != null) { _peekTimer.Stop(); _peekTimer.Dispose(); _peekTimer = null; }
            }
            Invalidate();
        }
        private bool _peek = false;
        private System.Windows.Forms.Timer _peekTimer = null;

        // ------------------------------------------------------------------
        //  托盘 + 空闲计时
        // ------------------------------------------------------------------
        /// <summary>菜单只建一份：托盘右键、条上右键、快捷键都用它</summary>
        /// <summary>
        /// 唯一的操作入口：条上那个菜单按钮、条上任意位置右键、托盘图标，弹出的都是这一份。
        /// 分成四组：打开与查找 / 阅读控制 / 显示设置 / 任务栏与退出。
        /// </summary>
        public ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei UI", 13f, GraphicsUnit.Pixel);
            menu.ShowImageMargin = false;

            // ---- 打开与查找 ----
            menu.Items.Add(Head("打开与查找"));
            menu.Items.Add(Item("打开 txt…", "点这里选一本书", delegate { DoOpenFile(); }));
            string lastBook = _mem.Recent.Count > 0 ? _mem.Recent[0] : null;
            if (lastBook != null && File.Exists(lastBook))
                menu.Items.Add(Item("继续读《" + ShortName(lastBook) + "》", lastBook, delegate { OpenBook(lastBook); }));
            if (_mem.Recent.Count > 1)
            {
                ToolStripMenuItem recent = new ToolStripMenuItem("最近读过的书…");
                int n = 0;
                foreach (string path in _mem.Recent)
                {
                    if (n >= 8) break;
                    if (!File.Exists(path)) continue;
                    string p2 = path;
                    recent.DropDownItems.Add(Item(ShortName(p2), p2, delegate { OpenBook(p2); }));
                    n++;
                }
                if (recent.DropDownItems.Count > 0) menu.Items.Add(recent);
            }
            menu.Items.Add(Item("查找…", "在正文里找，找到就从那一行开始读（/ 键）", delegate { OpenFind(); }));
            menu.Items.Add(Item("读剪贴板文本", "把刚复制的一段文字接着读", delegate { ReadClipboard(true); }));
            menu.Items.Add(Head("源文本清洗（书库）"));
            menu.Items.Add(Item("清洗源文本…", "把「书库\\原始」里新下的 txt 洗一遍，结果放进「书库\\已清洗」",
                delegate { RunCleaner(); }));
            menu.Items.Add(Item("打开「已清洗」文件夹", "从这儿挑洗好的书来读", delegate { OpenCleanFolder(); }));

            // ---- 阅读控制 ----
            menu.Items.Add(Head("阅读控制"));
            menu.Items.Add(Item("回到第一行", "Home", delegate { JumpToFirst(); }));
            menu.Items.Add(Item("跳到末行", "End", delegate { JumpToLast(); }));
            ToolStripMenuItem auto = new ToolStripMenuItem("自动翻行");
            auto.CheckOnClick = true;
            auto.Checked = _mem.AutoPlay;
            auto.CheckedChanged += delegate { if (auto.Checked != _mem.AutoPlay) ToggleAuto(); };
            menu.Items.Add(auto);
            ToolStripMenuItem speed = new ToolStripMenuItem("自动翻行速度");
            foreach (double sp in new double[] { 1, 2, 3, 5, 10 })
            {
                double v = sp;
                ToolStripMenuItem one = new ToolStripMenuItem(v.ToString("0.#") + " 秒一行");
                one.Checked = Math.Abs(_mem.AutoDelay - v) < 0.01;
                one.Click += delegate { SetAutoDelay(v); };
                speed.DropDownItems.Add(one);
            }
            menu.Items.Add(speed);

            // ---- 显示设置 ----
            menu.Items.Add(Head("显示设置"));
            ToolStripMenuItem chars = new ToolStripMenuItem("每行字数（当前 " + _book.CharsPerLine + " 字）");
            foreach (int c in new int[] { 18, 24, 28, 32, 36, 40, 48, 56, 64 })
            {
                int v = c;
                ToolStripMenuItem one = new ToolStripMenuItem(v + " 字");
                one.Checked = _book.CharsPerLine == v;
                one.Click += delegate { SetCharsPerLineExact(v); };
                chars.DropDownItems.Add(one);
            }
            chars.DropDownItems.Add(new ToolStripSeparator());
            chars.DropDownItems.Add(Item("少 2 个字", "- 键", delegate { ChangeChars(-2); }));
            chars.DropDownItems.Add(Item("多 2 个字", "= 键", delegate { ChangeChars(2); }));
            menu.Items.Add(chars);

            ToolStripMenuItem font = new ToolStripMenuItem("字号（当前 " + (_mem.AutoFont ? "自适应 " + AutoFontPx() + "px" : _mem.FontPx + "px") + "）");
            ToolStripMenuItem af = new ToolStripMenuItem("自适应（跟着每行字数自动铺满）");
            af.CheckOnClick = true;
            af.Checked = _mem.AutoFont;
            af.CheckedChanged += delegate { if (af.Checked != _mem.AutoFont) ToggleAutoFont(); };
            font.DropDownItems.Add(af);
            font.DropDownItems.Add(new ToolStripSeparator());
            for (int i = 0; i < 5; i++)
            {
                int v = 13 + i * 4;          // 13 / 17 / 21 / 25 / 29 px
                ToolStripMenuItem one = new ToolStripMenuItem(v + " px");
                one.Checked = !_mem.AutoFont && _mem.FontPx == v;
                one.Click += delegate { SetFontPxExact(v); };
                font.DropDownItems.Add(one);
            }
            font.DropDownItems.Add(Item("小一点", "Ctrl+滚轮", delegate { ChangeFont(-2); }));
            font.DropDownItems.Add(Item("大一点", "Ctrl+滚轮", delegate { ChangeFont(2); }));
            menu.Items.Add(font);

            ToolStripMenuItem sbItem = new ToolStripMenuItem("不显示空行（阅读更紧凑）");
            sbItem.CheckOnClick = true;
            sbItem.Checked = _mem.SkipBlank;
            sbItem.CheckedChanged += delegate { if (sbItem.Checked != _mem.SkipBlank) ToggleSkipBlank(); };
            menu.Items.Add(sbItem);
            menu.Items.Add(Item("把所有空行从正文里清掉", "永久整理，会先备份原稿", delegate { StripBlankLinesNow(); }));

            ToolStripMenuItem theme = new ToolStripMenuItem("外观");
            ToolStripMenuItem th = new ToolStripMenuItem(_mem.FollowSystemTheme ? "跟随系统（当前）" : "手动指定");
            th.CheckOnClick = true;
            th.Checked = _mem.FollowSystemTheme;
            th.CheckedChanged += delegate { if (th.Checked != _mem.FollowSystemTheme) ToggleFollowTheme(); };
            theme.DropDownItems.Add(th);
            ToolStripMenuItem light = new ToolStripMenuItem("浅色");
            light.Checked = !Skin.Light;
            light.Click += delegate { SetTheme(true); };
            ToolStripMenuItem dark = new ToolStripMenuItem("深色");
            dark.Checked = Skin.Light;
            dark.Click += delegate { SetTheme(false); };
            theme.DropDownItems.Add(light);
            theme.DropDownItems.Add(dark);
            menu.Items.Add(theme);
            menu.Items.Add(Item("正文横向归位", "清掉以前的偏移，回到默认左边距", delegate { ResetXOffset(); }));

            // ---- 任务栏与退出 ----
            menu.Items.Add(Head("任务栏与退出"));
            ToolStripMenuItem cover = new ToolStripMenuItem("我的条显示在系统任务栏前面（谁在前）");
            cover.CheckOnClick = true;
            cover.Checked = _mem.CoverTaskbar;
            cover.ToolTipText = "勾上=我的条压在任务栏上；取消=我的条原地沉到任务栏后面（位置不变，不会浮到任务栏上方）。条上「下一页」右边那个按钮同效（F7 也行）";
            cover.CheckedChanged += delegate { if (cover.Checked != _mem.CoverTaskbar) ToggleCover(); };
            menu.Items.Add(cover);
            ToolStripMenuItem padPos = new ToolStripMenuItem("最右侧那个切换入口摆在「显示桌面」那儿");
            padPos.CheckOnClick = true;
            padPos.Checked = _mem.SwapAtDesktopCorner;
            padPos.ToolTipText = "勾上=常驻在任务栏最右端；取消=退到我的条最右边（照样能点）";
            padPos.CheckedChanged += delegate
            {
                if (padPos.Checked == _mem.SwapAtDesktopCorner) return;
                _mem.SwapAtDesktopCorner = padPos.Checked;
                MoveSwapPad();
                SaveMemory();
                Flash(padPos.Checked ? "入口已常驻在任务栏最右侧" : "入口已退到我的条最右边");
            };
            menu.Items.Add(padPos);
            menu.Items.Add(Item("让路 3 秒", "临时露出系统任务栏（F9）", delegate { TogglePeek(); }));
            menu.Items.Add(Item("重新贴齐任务栏位置", "任务栏挪过位置时点它", delegate { ApplyGeometry(); Flash("已重新贴齐"); }));
            ToolStripMenuItem gk = new ToolStripMenuItem("全局接管方向键（会占用其他软件的 ↑↓）");
            gk.CheckOnClick = true;
            gk.Checked = _mem.GlobalKeys;
            gk.CheckedChanged += delegate { _mem.GlobalKeys = gk.Checked; SaveMemory(); Flash(gk.Checked ? "已全局接管按键" : "仅在桌面/任务栏上响应按键"); };
            menu.Items.Add(gk);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("退出 TxtBar（系统任务栏立刻回来）", "Esc", delegate { Quit(); }));
            return menu;
        }

        private static ToolStripMenuItem Head(string text)
        {
            ToolStripMenuItem h = new ToolStripMenuItem(text);
            h.Enabled = false;
            h.ForeColor = Skin.Light ? Color.FromArgb(120, 120, 120) : Color.FromArgb(150, 150, 150);
            return h;
        }

        private static ToolStripMenuItem Item(string text, string tip, EventHandler onClick)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            if (tip != null && tip.Length > 0) it.ToolTipText = tip;
            if (onClick != null) it.Click += onClick;
            return it;
        }

        private static string ShortName(string path)
        {
            try { return Path.GetFileName(path); } catch { return path; }
        }

        // ---- 菜单里用到的几个设置入口（保持「改完立刻生效 + 记住」）----
        public void SetAutoDelay(double seconds)
        {
            _mem.AutoDelay = Math.Max(0.2, Math.Min(60, seconds));
            UpdateAutoTimer();
            SaveMemory();
            Flash("自动翻行：每 " + _mem.AutoDelay.ToString("0.#") + " 秒一行");
        }

        public void SetCharsPerLineExact(int n)
        {
            _book.SetCharsPerLine(n);
            _mem.CharsPerLine = _book.CharsPerLine;
            BuildFonts();
            SaveMemory();
            Invalidate();
            Flash("每行 " + _book.CharsPerLine + " 字");
        }

        public void SetFontPxExact(int px)
        {
            _mem.AutoFont = false;
            _mem.FontPx = Math.Max(11, Math.Min(40, px));
            BuildFonts();
            SaveMemory();
            Invalidate();
            Flash("字号固定为 " + _mem.FontPx + "px（已关掉自适应）");
        }

        public void ToggleFollowTheme()
        {
            _mem.FollowSystemTheme = !_mem.FollowSystemTheme;
            SaveMemory();
            if (_mem.FollowSystemTheme)
            {
                Skin.Init(Shell.LightTheme);      // 回到跟系统一致
                BackColor = Skin.Bg;
                Invalidate();
                Flash("外观：跟随系统（" + (Shell.LightTheme ? "浅色" : "深色") + "）");
            }
            else Flash("外观：改成手动指定；用下面的浅色/深色切换");
        }

        public void SetTheme(bool light)
        {
            _mem.FollowSystemTheme = false;       // 手动选了就不再跟系统
            Skin.Init(light);
            BackColor = Skin.Bg;
            SaveMemory();
            Invalidate();
            Flash("外观：" + (light ? "浅色" : "深色") + "（已改为手动指定）");
        }

        public void InstallTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = MakeIcon();
            _tray.Text = "TxtBar 逐行阅读器（双击 = 自动翻行 开/关）";
            _tray.Visible = true;
            _tray.ContextMenuStrip = BuildMenu();
            _tray.DoubleClick += delegate { ToggleAuto(); };

            System.Windows.Forms.Timer idle = new System.Windows.Forms.Timer();
            idle.Interval = 200;
            idle.Tick += delegate
            {
                if (_flashTick > 0)
                {
                    _flashTick--;
                    if (_flashTick == 0) { _flash = ""; _flashShown = ""; Invalidate(); }
                }
                Rectangle want = TargetRect();
                if (want.Height != Height || want.Width != Width || want.Top != Top || want.Left != Left)
                    ApplyGeometry();
                if (_mem.ClipWatch) ReadClipboard(false);
            };
            idle.Start();
        }

        private static Icon MakeIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (Brush b = new SolidBrush(Skin.Accent)) g.FillRectangle(b, 2, 6, 28, 20);
                    using (Pen p = new Pen(Color.White, 2))
                    {
                        g.DrawLine(p, 7, 12, 25, 12);
                        g.DrawLine(p, 7, 17, 22, 17);
                        g.DrawLine(p, 7, 22, 18, 22);
                    }
                }
                try { return Icon.FromHandle(bmp.GetHicon()); }
                catch { return SystemIcons.Application; }
            }
        }

        /// <summary>启动时：接着上次读的那个文件的那一行</summary>
        public string RestoreLastBook()
        {
            string last = _mem.Recent.Count > 0 ? _mem.Recent[0] : null;
            if (last != null && File.Exists(last))
            {
                _book.SkipBlank = _mem.SkipBlank;
                _book.LoadFile(last);
                if (!_book.Loaded) return "上次的文件打不开了：" + _book.Error;
                int saved = 0;
                _mem.Pos.TryGetValue(last, out saved);
                _book.Index = Math.Max(0, Math.Min(_book.Lines.Count - 1, saved));
                int sc;
                if (_mem.Scroll.TryGetValue(last, out sc)) _xOffset = sc;
                return "接着上次：第 " + (_book.Index + 1) + "/" + _book.Lines.Count + " 行";
            }
            return "TxtBar 已就位：点最左边的文件夹按钮打开 txt";
        }

        public void SaveMemory()
        {
            try { _mem.Save(_book.Path, _book, _xOffset); } catch { }
            WriteStatus();
        }

        /// <summary>
        /// 把「当前读到哪、按钮在哪」实时写一份 status.json。
        /// 一是方便外部工具/脚本核对，二是自检与自动化测试可以直接点真实窗口上的按钮来验证功能。
        /// </summary>
        public void WriteStatus()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                Rectangle t = TargetRect();
                sb.Append("{\n");
                sb.Append("  \"title\": \"").Append(JsonEsc(_book.Title)).Append("\",\n");
                sb.Append("  \"path\": \"").Append(JsonEsc(_book.Path)).Append("\",\n");
                sb.Append("  \"index\": ").Append(_book.Index).Append(",\n");
                sb.Append("  \"totalLines\": ").Append(_book.Lines.Count).Append(",\n");
                sb.Append("  \"lineNumber\": ").Append(_book.Loaded ? _book.Index + 1 : 0).Append(",\n");
                sb.Append("  \"percent\": ").Append(_book.Percent).Append(",\n");
                sb.Append("  \"charsPerLine\": ").Append(_book.CharsPerLine).Append(",\n");
                sb.Append("  \"currentLineText\": \"").Append(JsonEsc(_book.Current())).Append("\",\n");
                sb.Append("  \"autoPlay\": ").Append(_mem.AutoPlay ? "true" : "false").Append(",\n");
                sb.Append("  \"xOffset\": ").Append(_xOffset).Append(",\n");
                sb.Append("  \"findQuery\": \"").Append(JsonEsc(_book.FindQuery)).Append("\",\n");
                sb.Append("  \"findError\": \"").Append(JsonEsc(_book.FindError)).Append("\",\n");
                sb.Append("  \"findPanelOpen\": ").Append(HasFindPanel ? "true" : "false").Append(",\n");
                sb.Append("  \"buttonsCount\": ").Append(_btns.Count).Append(",\n");
                sb.Append("  \"menuOpen\": ").Append(MenuOpen ? "true" : "false").Append(",\n");
                sb.Append("  \"globalKeys\": ").Append(_mem.GlobalKeys ? "true" : "false").Append(",\n");
                sb.Append("  \"trayWidth\": ").Append(Shell.TrayWidth).Append(",\n");
                sb.Append("  \"padVisible\": ").Append(_pad != null && _pad.Visible ? "true" : "false").Append(",\n");
                sb.Append("  \"padRect\": [");
                if (_pad != null)
                    sb.Append(_pad.Left).Append(", ").Append(_pad.Top).Append(", ")
                      .Append(_pad.Width).Append(", ").Append(_pad.Height);
                sb.Append("],\n");
                sb.Append("  \"flashText\": \"").Append(JsonEsc(_flash)).Append("\",\n");
                sb.Append("  \"hoverIndex\": ").Append(_hover).Append(",\n");
                sb.Append("  \"coverTaskbar\": ").Append(_mem.CoverTaskbar ? "true" : "false").Append(",\n");
                sb.Append("  \"windowRect\": [").Append(t.Left).Append(", ").Append(t.Top).Append(", ")
                  .Append(t.Width).Append(", ").Append(t.Height).Append("],\n");
                sb.Append("  \"taskbarRect\": [").Append(Shell.TaskbarRect.Left).Append(", ").Append(Shell.TaskbarRect.Top)
                  .Append(", ").Append(Shell.TaskbarRect.Width).Append(", ").Append(Shell.TaskbarRect.Height).Append("],\n");
                sb.Append("  \"clientSize\": [").Append(ClientSize.Width).Append(", ").Append(ClientSize.Height).Append("],\n");
                sb.Append("  \"textArea\": [").Append(TextArea().Left).Append(", ").Append(TextArea().Top).Append(", ")
                  .Append(TextArea().Width).Append(", ").Append(TextArea().Height).Append("],\n");
                sb.Append("  \"buttons\": [");
                for (int i = 0; i < _btns.Count; i++)
                {
                    BarButton b = _btns[i];
                    if (i > 0) sb.Append(", ");
                    sb.Append("{\"id\": \"").Append(b.Id).Append("\", \"x\": ").Append(b.Rect.X)
                      .Append(", \"y\": ").Append(b.Rect.Y).Append(", \"w\": ").Append(b.Rect.Width)
                      .Append(", \"h\": ").Append(b.Rect.Height).Append(", \"tip\": \"").Append(JsonEsc(b.Tip)).Append("\"}");
                }
                sb.Append("],\n");
                sb.Append("  \"updated\": \"").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\"\n");
                sb.Append("}\n");
                File.WriteAllText(System.IO.Path.Combine(Memory.Dir, "status.json"), sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        private static string JsonEsc(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 32) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public void Quit()
        {
            SaveMemory();
            if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
            if (_keyHook != null) { _keyHook.Dispose(); _keyHook = null; }
            if (_wheelHook != null) { _wheelHook.Dispose(); _wheelHook = null; }
            if (_pad != null) { _pad.StopFollow(); _pad.Hide(); _pad.Dispose(); _pad = null; }
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            try { if (!SuppressUi) Close(); } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveMemory();
            base.OnFormClosing(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (SuppressUi) return;
            // 启动时永远先把条露出来。
            // 上次退出可能停在「系统任务栏在前」，那种状态条是沉在任务栏后面的 ——
            // 一开机什么都看不见，很容易以为「程序没起来」。所以层叠这一项不跨启动记忆：
            // 读到的行号、每行字数、字号全照旧恢复，只有「谁在前」每次都从「我的条在前」开始。
            if (!_mem.CoverTaskbar)
            {
                _mem.CoverTaskbar = true;
                _mem.KeepTrayVisible = true;
                try { _mem.Save(_book.Path, _book, _xOffset); } catch { }   // 顺手把纠正后的状态落盘
            }
            Flash(RestoreLastBook());
            ApplyGeometry();
            InstallSwapPad();
            UpdateAutoTimer();
            InstallTray();
            InstallHooks();
        }

        /// <summary>建出任务栏最右侧那个常驻的切换入口（条上「下一页」右边那个按钮同效）</summary>
        private void InstallSwapPad()
        {
            if (_pad != null) return;
            _pad = new SwitchPad(this);
            _pad.Show();
            _pad.ApplyPlacement();
            WriteStatus();       // 建完再刷一次，status.json 里的 padVisible/padRect 才是真的
        }

        /// <summary>切换 / 挪位置之后，让最右侧那个入口跟上</summary>
        public void MoveSwapPad()
        {
            if (_pad != null) _pad.ApplyPlacement();
        }

        /// <summary>鼠标停在切换入口上时，在条上提一句「点它会发生什么」；移开就撤掉</summary>
        public void ShowSwapHint(bool on)
        {
            Flash(on ? SwapEntry.Hint(_mem.CoverTaskbar) : "");
        }
    }

    // ========================================================================
    //  入口
    // ========================================================================
    internal static class Program
    {
        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        private static extern IntPtr SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")]
        private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr ctx);

        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = new IntPtr(-3);
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = new IntPtr(-2);

        private static int _dpiAwareness = -1;

        /// <summary>
        /// 打开 DPI 感知。必须真的成功，否则系统会替我们做坐标/字号虚拟化，
        /// 结果是「窗口尺寸看着对，字号和坐标全是错的」——实机验证过这个坑。
        /// </summary>
        private static string EnableDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) != IntPtr.Zero)
                    _dpiAwareness = 2;
            }
            catch { }
            if (_dpiAwareness < 0)
            {
                try { if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE) != IntPtr.Zero) _dpiAwareness = 2; }
                catch { }
            }
            if (_dpiAwareness < 0)
            {
                try { SetProcessDpiAwareness(2); _dpiAwareness = 2; } catch { }
            }
            if (_dpiAwareness < 0)
            {
                try { SetProcessDpiAwareness(1); _dpiAwareness = 1; } catch { }
            }
            if (_dpiAwareness < 0)
            {
                try { if (SetProcessDPIAware()) _dpiAwareness = 1; } catch { }
            }
            int real = -1;
            try { real = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()); } catch { }
            string s = "请求=" + _dpiAwareness + " 实际=" + real +
                       "（0=非感知 1=系统感知 2=每屏感知）";
            if (real < 1) s += "  ← 未生效，界面会偏小";
            return s;
        }

        /// <summary>
        /// 把结果同时写到 stdout 和控制台能用的地方；输出被重定向时只写日志文件，不抛异常。
        /// </summary>
        private static void Say(string s)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            try { Console.WriteLine(s); } catch { }
            try
            {
                File.AppendAllText(System.IO.Path.Combine(Memory.Dir, "selftest.log"),
                    DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            // 崩溃也要留痕，方便排查（写到 %LOCALAPPDATA%\TxtBar\startup.log）
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Log("UnhandledException: " + (e.ExceptionObject != null ? e.ExceptionObject.ToString() : "?"));
            };
            Application.ThreadException += delegate (object s, System.Threading.ThreadExceptionEventArgs e)
            {
                Log("ThreadException: " + e.Exception);
            };

            string dpiInfo = EnableDpiAwareness();
            Log("启动，参数 " + (args.Length > 0 ? string.Join(" ", args) : "(无)") + "；DPI " + dpiInfo);

            if (args.Length > 0 && string.Equals(args[0], "--dpi", StringComparison.OrdinalIgnoreCase))
            {
                Shell.Detect();
                Say("DPI 感知：" + dpiInfo);
                Say("环境：" + Shell.Describe());
                Say("屏幕 " + Shell.ScreenBounds.Width + "x" + Shell.ScreenBounds.Height +
                    "，任务栏 " + Shell.TaskbarRect.Width + "x" + Shell.TaskbarRect.Height +
                    " @(" + Shell.TaskbarRect.Left + "," + Shell.TaskbarRect.Top + ")");
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Shell.Detect();
            Skin.Init(Shell.LightTheme);

            bool anyGlobal = false;
            foreach (string a in args)
                if (string.Equals(a, "--global", StringComparison.OrdinalIgnoreCase)) anyGlobal = true;
            TxtBarForm.ForceGlobalKeys = anyGlobal;

            if (args.Length > 0 && string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = SelfTest.Run(args.Length > 1 ? args[1] : null) ? 0 : 1;
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "--dump", StringComparison.OrdinalIgnoreCase))
            {
                SelfTest.Dump(args.Length > 1 ? args[1] : null,
                              args.Length > 2 ? args[2] : null);
                return;
            }

            string openPath = null;
            foreach (string a in args)
                if (!a.StartsWith("--") && File.Exists(a)) { openPath = a; break; }
            if (openPath != null)
            {
                // 指定了文件就以它为准：提到「最近阅读」最前面，启动时才会读它
                Memory m = Memory.Load();
                m.Recent.Remove(openPath);
                m.Recent.Insert(0, openPath);
                while (m.Recent.Count > 12) m.Recent.RemoveAt(m.Recent.Count - 1);
                m.Save(null, null, 0);
            }

            Application.Run(new TxtBarForm());
        }

        /// <summary>启动/崩溃日志，写在 %LOCALAPPDATA%\TxtBar\startup.log</summary>
        public static void Log(string msg)
        {
            try
            {
                File.AppendAllText(System.IO.Path.Combine(Memory.Dir, "startup.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
