// ============================================================================
//  TxtBar 第三部分：L5d 查找面板 + L6 集成（主窗体）
//  与 TxtBar.cs 一起编译：
//      csc /target:winexe /out:..\TxtBar.exe src\*.cs
//
//  这一层专门放「最近一轮改的东西」：
//    · 查找面板（点条上的放大镜按钮 / 按 F 或 Ctrl+F 弹出）
//    · 正文不再能拖动（把误操作彻底去掉，位置改用菜单里的微调）
//    · 退出路径加固（Esc / ✕ / 托盘三个入口都收干净，不留窗口）
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TxtBar
{
    using Win32 = System.Runtime.InteropServices;


    // ========================================================================
    //  L5d  查找面板：一个贴着任务栏上方弹出的小输入条
    // ========================================================================
    public sealed class FindPanel : Form
    {
        private readonly TextBox _box;
        private readonly Button _prevBtn;
        private readonly Button _nextBtn;
        private readonly Label _hint;
        private readonly TxtBarForm _owner;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr p);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint a, uint b, bool attach);

        /// <summary>
        /// 把前台权抢过来：我们的条是不抢焦点的窗口，弹面板时进程没有前台权限，
        /// 直接 Show + Activate 会被系统拒绝（表现就是「点了放大镜却看不到面板」）。
        /// 这里先把输入线程挂到当前前台线程上，就能合法地切前台。
        /// </summary>
        private static void ForceForeground(IntPtr h)
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
                uint myThread = GetCurrentThreadId();
                if (fgThread != 0 && fgThread != myThread)
                {
                    AttachThreadInput(myThread, fgThread, true);
                    SetForegroundWindow(h);
                    AttachThreadInput(myThread, fgThread, false);
                }
                else SetForegroundWindow(h);
            }
            catch { }
        }

        public FindPanel(TxtBarForm owner)
        {
            _owner = owner;

            FormBorderStyle = FormBorderStyle.None;
            Text = "TxtBarFind";          // 给窗口一个标题，方便外部工具/脚本定位它
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = Skin.Light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(44, 44, 44);
            Font = new Font("Microsoft YaHei UI", 11f, GraphicsUnit.Pixel);

            _box = new TextBox();
            _box.BorderStyle = BorderStyle.FixedSingle;
            _box.Font = new Font("Microsoft YaHei UI", 13f, GraphicsUnit.Pixel);
            _box.BackColor = Skin.Light ? Color.White : Color.FromArgb(30, 30, 30);
            _box.ForeColor = Skin.Fg;
            _box.Width = 260;
            _box.KeyDown += OnBoxKey;

            _prevBtn = new Button { Text = "上一个", FlatStyle = FlatStyle.Flat, Width = 62, Height = 26 };
            _nextBtn = new Button { Text = "下一个", FlatStyle = FlatStyle.Flat, Width = 62, Height = 26 };
            _prevBtn.Click += delegate { DoFind(false); };
            _nextBtn.Click += delegate { DoFind(true); };

            _hint = new Label { AutoSize = false, Height = 26, TextAlign = ContentAlignment.MiddleLeft,
                                ForeColor = Skin.Dim, Text = "输入后回车 = 从当前位置往后找" };

            Controls.Add(_box);
            Controls.Add(_prevBtn);
            Controls.Add(_nextBtn);
            Controls.Add(_hint);

            Size = new Size(520, 40);
            LayoutControls();
        }

        private void LayoutControls()
        {
            int pad = 8, gap = 6;
            _box.Location = new Point(pad, (Height - _box.Height) / 2);
            int x = _box.Right + gap;
            _prevBtn.Location = new Point(x, (Height - _prevBtn.Height) / 2);
            x += _prevBtn.Width + gap;
            _nextBtn.Location = new Point(x, (Height - _nextBtn.Height) / 2);
            x += _nextBtn.Width + gap;
            _hint.Location = new Point(x, (Height - _hint.Height) / 2);
            _hint.Width = Math.Max(40, Width - x - pad);
        }

        /// <summary>贴在 TxtBar 那条任务栏的上面（如果任务栏在最下面）或下面，尽量不挡住正文</summary>
        public void PlaceNearBar()
        {
            Rectangle bar = _owner.TargetRect();
            int x = Math.Min(bar.Left + 120, Math.Max(0, Screen.PrimaryScreen.WorkingArea.Right - Width - 20));
            int y = bar.Top - Height - 6;
            if (y < 0) y = bar.Bottom + 6;
            Location = new Point(x, y);
        }

        /// <summary>打开时把「面板到底有没有显示」记进日志，出问题能查</summary>
        public void OpenWith(string seed)
        {
            Program.Log("查找面板 OpenWith：seed=[" + (seed == null ? "null" : seed) + "] 当前可见=" + Visible);
            _outsideClickAt = 0;
            _box.Text = seed == null ? "" : seed;   // 总是带上上次查找的词，接着找
            _owner.Book.FindError = "";
            PlaceNearBar();
            Show();
            InstallOutsideClickWatcher();
            ForceForeground(Handle);
            try { Activate(); } catch { }
            _box.Focus();
            _box.SelectAll();
            UpdateHint();
            Program.Log("查找面板 OpenWith 结束：Visible=" + Visible + " Bounds=" + Bounds +
                       " IsHandleCreated=" + IsHandleCreated + " Handle=" + Handle.ToInt64());
        }

        public void UpdateHint()
        {
            Book b = _owner.Book;
            if (b.FindError.Length > 0) _hint.Text = b.FindError;
            else
            {
                string q = _box.Text;
                int hits = b.CountHits(q, true);
                _hint.Text = q.Length == 0
                    ? "输入后回车 = 从当前位置往后找"
                    : (hits > 0 ? "共 " + hits + " 处命中" : "还没有命中");
            }
        }

        private void OnBoxKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.F3)
            {
                DoFind(!e.Shift);
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                Hide();
                e.SuppressKeyPress = true;
            }
        }

        private void DoFind(bool forward)
        {
            string q = _box.Text;
            bool hit = forward ? _owner.Book.FindNext(q, true) : _owner.Book.FindPrev(q, true);
            _owner.Book.FindQuery = q;
            UpdateHint();
            _owner.BookRefreshAndFlash(forward ? "查找：下一个" : "查找：上一个");
            _owner.SaveMemory();   // 立刻落盘，位置和查找词都记住
            // 找到了就关掉面板，把注意力还给正文（没找到就留着让人改词）
            if (hit) Hide();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Skin.Light ? Color.FromArgb(200, 200, 200) : Color.FromArgb(70, 70, 70)))
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        // ------------------------------------------------------------------
        //  收起规则：不再看焦点（我们这条任务栏本来就不抢焦点），
        //  改成监听全局左键：点到面板外面就自动收起，点到面板里面则不动。
        // ------------------------------------------------------------------
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint tid);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wp, IntPtr lp);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        private delegate IntPtr HookProc(int code, IntPtr wp, IntPtr lp);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MPOINT { public int X, Y; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MSLL { public MPOINT pt; public uint mouseData, flags, time; public IntPtr extra; }

        private HookProc _mouseProc;          // 必须保住引用
        private IntPtr _mouseHook = IntPtr.Zero;
        private int _outsideClickAt = 0;
        private System.Windows.Forms.Timer _hideTimer;

        private void InstallOutsideClickWatcher()
        {
            if (_mouseHook != IntPtr.Zero) return;
            _mouseProc = delegate (int code, IntPtr wp, IntPtr lp)
            {
                if (code >= 0 && wp.ToInt32() == 0x0201)   // WM_LBUTTONDOWN
                {
                    try
                    {
                        MSLL d = (MSLL)System.Runtime.InteropServices.Marshal.PtrToStructure(lp, typeof(MSLL));
                        if (Visible && !Bounds.Contains(d.pt.X, d.pt.Y)) _outsideClickAt = Environment.TickCount;
                    }
                    catch { }
                }
                return CallNextHookEx(_mouseHook, code, wp, lp);
            };
            _mouseHook = SetWindowsHookEx(14, _mouseProc, GetModuleHandle(null), 0);   // WH_MOUSE_LL

            _hideTimer = new System.Windows.Forms.Timer();
            _hideTimer.Interval = 200;
            _hideTimer.Tick += delegate
            {
                if (!Visible || _outsideClickAt == 0) return;
                if (Environment.TickCount - _outsideClickAt > 250) { _outsideClickAt = 0; Hide(); }
            };
            _hideTimer.Start();
        }

        private void RemoveOutsideClickWatcher()
        {
            if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            RemoveOutsideClickWatcher();
            base.OnFormClosing(e);
        }
    }
}
