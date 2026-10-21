// ============================================================================
//  TxtBar —— Windows 任务栏式的逐行阅读器
//  分层实现，本文件按层组织：
//    L1 运行环境探测（DPI / 任务栏几何 / 主题）
//    L2 外观与自绘（背景、分隔线、字体度量）
//    L3 正文（载入 txt、编码识别、每行固定字数切行、进度）
//    L4 记忆（行号、字号、开关，写到文件旁与 %LOCALAPPDATA%）
//    L5 操控（按钮命中、键盘钩子、滚轮、自动翻行）
//    L6 集成（谁在前切换 / 剪贴板跟随 / 托盘图标）
//  目标框架：.NET Framework 4.x（C# 5 语法），用系统自带 csc.exe 编译
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
    // ========================================================================
    //  L1a  原生互操作
    // ========================================================================
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string cls, string win);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string win);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr h, out RECT r);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr h);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr val);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindowLongPtr(IntPtr h, int index);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern bool SetLayeredWindowAttributes(IntPtr h, uint colorKey, byte alpha, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc fn, IntPtr hMod, uint threadId);

        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr fn);

        [DllImport("user32.dll")]
        public static extern bool KillTimer(IntPtr h, IntPtr id);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr h);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr h, int index);

        [DllImport("user32.dll")]
        public static extern bool ShowWindowAsync(IntPtr h, int cmd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr h);

        [DllImport("user32.dll")]
        public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDPIAware();

        public delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);

        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;

        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_LAYERED = 0x00080000;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_NOZORDER = 0x0004;

        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    }

    // ========================================================================
    //  L1b  运行环境探测结果
    // ========================================================================
    internal static class Shell
    {
        public static Rectangle ScreenBounds;
        public static Rectangle WorkArea;          // 不含任务栏的可用区
        public static Rectangle TaskbarRect;       // 系统任务栏（物理像素）
        public static int TaskbarHeight = 48;
        public static int TrayWidth = 0;           // 右侧托盘+时钟区宽度
        public static IntPtr TaskbarHwnd = IntPtr.Zero;   // 系统任务栏窗口（用于「剥开托盘」）
        public static IntPtr TrayHwnd = IntPtr.Zero;      // 右下角托盘/时钟那一块
        public static IntPtr ReBarHwnd = IntPtr.Zero;     // 任务栏上的任务按钮区
        public static int Dpi = 96;
        public static bool TaskbarFound = false;
        public static bool LightTheme = true;      // 系统当前是浅色主题
        public static bool TaskbarAlignedLeft = true;

        // 视觉缩放：所有尺寸都乘这个系数，保证在 100%/125%/150% 下观感一致
        public static float Scale = 1f;
        public static int S(int px) { return (int)Math.Round(px * Scale); }

        public static void Detect()
        {
            const int SM_CXSCREEN = 0, SM_CYSCREEN = 1, SM_CXVIRTUALSCREEN = 78;
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            TaskbarHwnd = tray;
            TaskbarFound = tray != IntPtr.Zero;

            if (TaskbarFound)
            {
                Native.RECT r;
                if (Native.GetWindowRect(tray, out r))
                {
                    TaskbarRect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    TaskbarHeight = r.Bottom - r.Top;
                }
                uint dpi = 0;
                try { dpi = Native.GetDpiForWindow(tray); } catch { }
                if (dpi >= 72) Dpi = (int)dpi;

                IntPtr notify = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                TrayHwnd = notify;
                if (notify != IntPtr.Zero)
                {
                    Native.RECT tr;
                    if (Native.GetWindowRect(notify, out tr)) TrayWidth = tr.Right - tr.Left;
                }
                ReBarHwnd = Native.FindWindowEx(tray, IntPtr.Zero, "ReBarWindow32", null);
            }

            ScreenBounds = Screen.PrimaryScreen.Bounds;
            WorkArea = Screen.PrimaryScreen.WorkingArea;
            if (!TaskbarFound || TaskbarHeight <= 0)
            {
                TaskbarHeight = ScreenBounds.Bottom - WorkArea.Bottom;
                if (TaskbarHeight <= 0) TaskbarHeight = 48;
                TaskbarRect = new Rectangle(ScreenBounds.Left, ScreenBounds.Bottom - TaskbarHeight,
                                            ScreenBounds.Width, TaskbarHeight);
            }
            if (TrayWidth <= 0) TrayWidth = 220;

            Scale = Dpi / 96f;

            // 主题：AppsUseLightTheme=1 表示浅色
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("SystemUsesLightTheme");
                        if (v != null) LightTheme = Convert.ToInt32(v) == 1;
                    }
                    else LightTheme = true;
                }
                using (Microsoft.Win32.RegistryKey k2 = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                {
                    if (k2 != null)
                    {
                        object v2 = k2.GetValue("TaskbarAl");
                        if (v2 != null) TaskbarAlignedLeft = Convert.ToInt32(v2) == 0;
                    }
                }
            }
            catch { }
        }

        public static string Describe()
        {
            return string.Format("dpi={0} scale={1:0.00} taskbar={2}x{3} @ ({4},{5}) tray={6} theme={7} aligned={8}",
                Dpi, Scale, TaskbarRect.Width, TaskbarRect.Height,
                TaskbarRect.Left, TaskbarRect.Top, TrayWidth,
                LightTheme ? "light" : "dark", TaskbarAlignedLeft ? "left" : "center");
        }
    }

    // ========================================================================
    //  L2  外观：颜色与字体度量（跟系统任务栏保持一致的观感）
    // ========================================================================
    internal static class Skin
    {
        public static bool Light = true;

        // 浅色主题下的任务栏底色（Win11 是 #F3F3F3 + 轻微透明）
        public static Color Bg { get { return Light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32); } }
        public static Color Fg { get { return Light ? Color.FromArgb(24, 24, 24) : Color.FromArgb(243, 243, 243); } }
        public static Color Dim { get { return Light ? Color.FromArgb(120, 120, 120) : Color.FromArgb(150, 150, 150); } }
        public static Color Hover { get { return Light ? Color.FromArgb(28, 0, 0, 0) : Color.FromArgb(36, 255, 255, 255); } }
        public static Color Pressed { get { return Light ? Color.FromArgb(48, 0, 0, 0) : Color.FromArgb(56, 255, 255, 255); } }
        public static Color Accent { get { return Color.FromArgb(0, 120, 212); } }
        public static Color Line { get { return Light ? Color.FromArgb(220, 220, 220) : Color.FromArgb(60, 60, 60); } }
        public static Color Track { get { return Light ? Color.FromArgb(224, 224, 224) : Color.FromArgb(64, 64, 64); } }

        public static void Init(bool light) { Light = light; }
    }

    // ========================================================================
    //  L3  正文模型：按「每行固定字数」切行 + 原文行号映射
    // ========================================================================
    public sealed class Book
    {
        public string Path = "";
        public string Title = "（未打开文件）";
        public string Text = "";
        public List<string> Lines = new List<string>();     // 显示用行
        public List<int> OrigLine = new List<int>();        // 每行对应的原文行号
        public Encoding Enc = Encoding.UTF8;
        public string EncName = "";
        public int Index = 0;                               // 当前位置
        public int CharsPerLine = 40;
        public bool SkipBlank = true;                       // 空行不显示（默认开）
        public bool BlankFallback = false;                  // 整篇都是空行时的兜底标记
        public string Error = "";

        public bool Loaded { get { return Lines.Count > 0; } }

        public static Encoding DetectEncoding(byte[] data, out string name)
        {
            name = "UTF-8";
            // 带 BOM 的直接按 BOM
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return new UTF8Encoding(true);
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) { name = "UTF-16LE"; return Encoding.Unicode; }
            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) { name = "UTF-16BE"; return Encoding.BigEndianUnicode; }
            // 严格 UTF-8 试解码。
            // 注意：不能直接把前 262144 字节整块丢给严格解码 —— 那个位置很可能正好落在
            // 一个多字节汉字的中间，解码器会当成非法序列而误判成 GB18030（整篇读成乱码）。
            // 这里用 Decoder 增量转换、flush:false，尾部那个残缺字符会被挂起而不是报错。
            try
            {
                UTF8Encoding strict = new UTF8Encoding(false, true);
                int probe = Math.Min(data.Length, 262144);
                int chars = strict.GetMaxCharCount(probe) + 4;
                char[] buf = new char[chars > 0 ? chars : probe + 4];
                int used, made;
                bool done;
                strict.GetDecoder().Convert(data, 0, probe, buf, 0, buf.Length, false, out used, out made, out done);
                return new UTF8Encoding(false);
            }
            catch { }
            // 退回 GB18030（兼容 GBK / GB2312）
            try
            {
                Encoding gb = Encoding.GetEncoding(54936);   // GB18030
                name = "GB18030";
                return gb;
            }
            catch { }
            try { name = "GBK"; return Encoding.GetEncoding(936); }
            catch { }
            name = "UTF-8(强制)";
            return new UTF8Encoding(false);
        }

        public void LoadFile(string path)
        {
            Error = "";
            byte[] raw;
            try { raw = File.ReadAllBytes(path); }
            catch (Exception ex) { Error = "打不开文件：" + ex.Message; return; }

            LoadBytes(raw, path);
        }

        public void LoadBytes(byte[] raw, string path)
        {
            Error = "";
            Path = path == null ? "" : path;
            Title = string.IsNullOrEmpty(path) ? "（剪贴板）" : System.IO.Path.GetFileName(path);

            string n;
            Enc = DetectEncoding(raw, out n);
            EncName = n;
            string text;
            try { text = Enc.GetString(raw); }
            catch (Exception ex) { Enc = new UTF8Encoding(false); EncName = "UTF-8"; text = Enc.GetString(raw); Error = "解码异常：" + ex.Message; }

            SetText(text);
        }

        public void SetText(string text)
        {
            Text = text == null ? "" : text;
            Rebuild();
            Index = 0;
        }

        /// <summary>
        /// 切行：先按原文换行分段，长段再按 CharsPerLine 硬切。
        /// 默认会跳过空行（含只有空白字符的行），这样阅读时不会翻到一片空白。
        /// </summary>
        public void Rebuild()
        {
            BlankFallback = false;
            RebuildCore(SkipBlank);

            // 兜底：整篇都是空行时不能什么都不显示，退回保留空行再切一次
            if (Lines.Count == 0 && SkipBlank && Text.Length > 0)
            {
                RebuildCore(false);
                BlankFallback = true;
            }

            if (Index >= Lines.Count) Index = Math.Max(0, Lines.Count - 1);
            if (Index < 0) Index = 0;
            BuildFindIndex();
        }

        private void RebuildCore(bool skipBlank)
        {
            Lines.Clear();
            OrigLine.Clear();
            string t = Text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] paras = t.Split('\n');
            for (int p = 0; p < paras.Length; p++)
            {
                string s = paras[p];
                if (s.Length == 0)
                {
                    if (!skipBlank) { Lines.Add(""); OrigLine.Add(p); }
                    continue;
                }
                int off = 0;
                while (off < s.Length)
                {
                    int take = Math.Min(CharsPerLine, s.Length - off);
                    string piece = s.Substring(off, take);
                    // 全是空白的片段（例如段落尾巴被切出来的一小截空格）也不显示
                    if (!(skipBlank && piece.Trim().Length == 0))
                    {
                        Lines.Add(piece);
                        OrigLine.Add(p);
                    }
                    off += take;
                }
            }
        }

        // ------------------------------------------------------------------
        //  查找索引：把全文拼成一条长字符串，并记住每个字符属于哪一条显示行。
        //  这样查找是在真正的正文里找，关键词即使跨了切行边界也能命中，
        //  找到后再一次性定位到它所在的那条显示行。
        // ------------------------------------------------------------------
        private string _indexText = "";
        private int[] _indexRow = new int[0];

        private void BuildFindIndex()
        {
            StringBuilder sb = new StringBuilder();
            List<int> rows = new List<int>();
            for (int i = 0; i < Lines.Count; i++)
            {
                sb.Append(Lines[i]);
                for (int k = 0; k < Lines[i].Length; k++) rows.Add(i);
                sb.Append('\n');          // 行间分隔，避免两条不相干的行被粘成假命中
                rows.Add(i);
            }
            _indexText = sb.ToString();
            _indexRow = rows.ToArray();
        }

        /// <summary>某条显示行在全文索引里的起始字符位置</summary>
        private int IndexPosOfRow(int row)
        {
            int pos = 0;
            for (int i = 0; i < row && i < Lines.Count; i++) pos += Lines[i].Length + 1;
            return pos;
        }

        /// <summary>全文索引里的字符位置换算回显示行号</summary>
        private int RowOfIndexPos(int pos)
        {
            if (_indexRow.Length == 0) return 0;
            if (pos < 0) return 0;
            if (pos >= _indexRow.Length) return _indexRow[_indexRow.Length - 1];
            return _indexRow[pos];
        }

        public string Current()
        {
            if (Lines.Count == 0) return "";
            return Lines[Math.Max(0, Math.Min(Index, Lines.Count - 1))];
        }

        // ------------------------------------------------------------------
        //  查找：显示行内逐字匹配，找不到就报「没有」
        // ------------------------------------------------------------------
        public string FindQuery = "";          // 当前查找词
        public string FindError = "";          // 查找失败的原因（找不到 / 没打开文件）
        public bool FindWrapped = false;       // 是否绕回开头找过

        /// <summary>
        /// 从当前位置往后找，找到就把 Index 移到它所在的那条显示行，返回 true。
        /// 先在当前位置之后搜到结尾，再从开头搜回当前位置（绕回）。
        /// </summary>
        public bool FindNext(string query, bool ignoreCase)
        {
            FindError = "";
            FindWrapped = false;
            if (Lines.Count == 0) { FindError = "还没有打开文件"; return false; }
            if (string.IsNullOrEmpty(query)) { FindError = "请先输入要查找的内容"; return false; }
            if (_indexText.Length == 0) BuildFindIndex();

            StringComparison cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            int start = Math.Min(IndexPosOfRow(Index) + 1, _indexText.Length);
            int at = _indexText.IndexOf(query, start, cmp);
            if (at < 0 && start > 0)
            {
                at = _indexText.IndexOf(query, 0, cmp);       // 绕回开头
                if (at >= 0) FindWrapped = true;
            }
            if (at < 0) { FindError = "没有找到「" + query + "」"; return false; }
            Index = RowOfIndexPos(at);
            return true;
        }

        /// <summary>从当前位置往前找（反向查找）</summary>
        public bool FindPrev(string query, bool ignoreCase)
        {
            FindError = "";
            FindWrapped = false;
            if (Lines.Count == 0) { FindError = "还没有打开文件"; return false; }
            if (string.IsNullOrEmpty(query)) { FindError = "请先输入要查找的内容"; return false; }
            if (_indexText.Length == 0) BuildFindIndex();

            StringComparison cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            int end = Math.Min(IndexPosOfRow(Index), _indexText.Length);
            int at = end > 0 ? _indexText.LastIndexOf(query, end - 1, cmp) : -1;
            if (at < 0 && end < _indexText.Length)
            {
                at = _indexText.LastIndexOf(query, cmp);      // 绕回末尾
                if (at >= 0) FindWrapped = true;
            }
            if (at < 0) { FindError = "没有找到「" + query + "」"; return false; }
            Index = RowOfIndexPos(at);
            return true;
        }

        /// <summary>全文里一共出现多少处</summary>
        public int CountHits(string query, bool ignoreCase)
        {
            if (string.IsNullOrEmpty(query) || _indexText.Length == 0) return 0;
            StringComparison cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            int n = 0, from = 0;
            while (from <= _indexText.Length)
            {
                int at = _indexText.IndexOf(query, from, cmp);
                if (at < 0) break;
                n++;
                from = at + Math.Max(1, query.Length);
            }
            return n;
        }

        /// <summary>当前这条显示行里是不是有查找词</summary>
        public bool CurrentHits(string query, bool ignoreCase)
        {
            int from, len;
            return CurrentHitRange(query, out from, out len) && from >= 0;
        }

        /// <summary>当前行的命中位置（给绘制用）</summary>
        public bool CurrentHitRange(string query, out int from, out int len)
        {
            return CurrentHitRangeInner(query, out from, out len);
        }

        /// <summary>命中片段在当前这一行里的起止位置（用于把命中的几个字涂色）</summary>
        private bool CurrentHitRangeInner(string query, out int from, out int len)
        {
            from = -1; len = 0;
            if (string.IsNullOrEmpty(query) || Lines.Count == 0) return false;
            string cur = Current();
            if (cur.Length == 0) return false;

            StringComparison cmp = StringComparison.OrdinalIgnoreCase;
            int direct = cur.IndexOf(query, cmp);
            if (direct >= 0) { from = direct; len = Math.Min(query.Length, cur.Length - direct); return true; }

            // 跨行命中的情况：当前行被某个命中片段盖住了一部分
            if (_indexText.Length == 0) return false;
            int p = IndexPosOfRow(Index);
            for (int back = Math.Min(query.Length - 1, p); back >= 0; back--)
            {
                int at = p - back;
                if (at < 0 || at + query.Length > _indexText.Length) continue;
                bool same = true;
                for (int k = 0; k < query.Length; k++)
                {
                    char a = char.ToLowerInvariant(_indexText[at + k]);
                    char b = char.ToLowerInvariant(query[k]);
                    if (a != b) { same = false; break; }
                }
                if (!same) continue;
                int s = Math.Max(at, p) - p;
                int e = Math.Min(at + query.Length, p + cur.Length) - p;
                if (e > s) { from = s; len = e - s; return true; }
            }
            return false;
        }

        public void SetCharsPerLine(int n)
        {
            n = Math.Max(8, Math.Min(200, n));
            if (n == CharsPerLine) return;
            // 尽量停在同一段、同一字附近
            int orig = OrigLine.Count > 0 ? OrigLine[Math.Min(Index, OrigLine.Count - 1)] : 0;
            int skip = 0;
            for (int i = 0; i < OrigLine.Count && i < Index; i++) if (OrigLine[i] == orig) skip += Lines[i].Length;
            CharsPerLine = n;
            Rebuild();
            Index = 0;
            for (int i = 0; i < OrigLine.Count; i++)
            {
                if (OrigLine[i] == orig) { Index = i; break; }
            }
            int acc = 0;
            for (int i = Index; i < OrigLine.Count && OrigLine[i] == orig; i++)
            {
                if (acc + Lines[i].Length > skip) break;
                acc += Lines[i].Length;
                Index = i;
            }
        }

        public int Percent { get { return Lines.Count <= 1 ? 0 : (int)Math.Round(Index * 100.0 / (Lines.Count - 1)); } }
    }

    // ========================================================================
    //  L4  记忆：设置 + 每本书的行号
    // ========================================================================
    public sealed class Memory
    {
        public int CharsPerLine = 40;
        public bool SkipBlank = true;           // 空行不显示
        public bool FollowSystemTheme = true;   // 外观跟随系统浅/深色
        public bool SwapAtDesktopCorner = true; // 任务栏最右侧那个切换入口摆在「显示桌面」那儿
        public int FontPx = 21;
        public bool AutoFont = true;            // 字号跟着「每行字数」自动算，正好铺满
        public double AutoDelay = 2.0;          // 自动翻行间隔（秒）
        public bool AutoPlay = false;
        public bool ClipWatch = true;
        public bool CoverTaskbar = true;
        public bool KeepTrayVisible = true;
        public bool GlobalKeys = false;         // 是否全局接管按键（默认只在桌面/任务栏上响应）
        public int XOffset = 0;                 // 文本横向偏移（可微调，避免压住托盘）
        public List<string> Recent = new List<string>();
        public Dictionary<string, int> Pos = new Dictionary<string, int>();
        public Dictionary<string, int> PosOffset = new Dictionary<string, int>();
        public Dictionary<string, int> Scroll = new Dictionary<string, int>();

        // 自检时可以把它指到临时目录，避免动到用户真实的记忆
        public static string DirOverride = null;

        public static string Dir
        {
            get
            {
                string d = DirOverride;
                if (string.IsNullOrEmpty(d))
                    d = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TxtBar");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }
        public static string File_ { get { return System.IO.Path.Combine(Dir, "settings.json"); } }

        public static Memory Load()
        {
            Memory m = new Memory();
            try
            {
                if (!File.Exists(File_)) return m;
                string[] lines = File.ReadAllLines(File_, Encoding.UTF8);
                List<string> cur = null;
                foreach (string raw in lines)
                {
                    string s = raw.Trim();
                    if (s.Length == 0 || s.StartsWith("//")) continue;
                    if (s.StartsWith("[") && s.EndsWith("]"))
                    {
                        cur = new List<string>();
                        continue;
                    }
                    int eq = s.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = s.Substring(0, eq).Trim();
                    string v = s.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "CharsPerLine": m.CharsPerLine = ParseInt(v, 40, 8, 200); break;
                        case "SkipBlank": m.SkipBlank = !(v == "0" || v.ToLower() == "false"); break;
                        case "FollowSystemTheme": m.FollowSystemTheme = !(v == "0" || v.ToLower() == "false"); break;
                        case "SwapAtDesktopCorner": m.SwapAtDesktopCorner = !(v == "0" || v.ToLower() == "false"); break;
                        case "FontPx": m.FontPx = ParseInt(v, 21, 9, 48); break;
                        case "AutoFont": m.AutoFont = !(v == "0" || v.ToLower() == "false"); break;
                        case "AutoDelay": m.AutoDelay = ParseDouble(v, 2.0, 0.2, 60); break;
                        case "AutoPlay": m.AutoPlay = v == "1" || v.ToLower() == "true"; break;
                        case "ClipWatch": m.ClipWatch = !(v == "0" || v.ToLower() == "false"); break;
                        case "CoverTaskbar": m.CoverTaskbar = !(v == "0" || v.ToLower() == "false"); break;
                        case "KeepTrayVisible": m.KeepTrayVisible = !(v == "0" || v.ToLower() == "false"); break;
                        case "XOffset": m.XOffset = ParseInt(v, 0, -600, 600); break;
                        case "GlobalKeys": m.GlobalKeys = v == "1" || v.ToLower() == "true"; break;
                        case "Recent": if (v.Length > 0) foreach (string p in v.Split('|')) if (p.Length > 0) m.Recent.Add(p); break;
                        case "Pos":
                        case "PosOffset":
                        case "Scroll":
                            if (v.Length > 0)
                            {
                                int bar = v.IndexOf('|');
                                if (bar > 0)
                                {
                                    string path = v.Substring(0, bar);
                                    int val = ParseInt(v.Substring(bar + 1), 0, 0, 1 << 30);
                                    if (k == "Pos") m.Pos[path] = val;
                                    else if (k == "PosOffset") m.PosOffset[path] = val;
                                    else m.Scroll[path] = val;
                                }
                            }
                            break;
                    }
                }
            }
            catch { }
            return m;
        }

        public void Save(string currentPath, Book book, int scrollOffset)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("// TxtBar 记忆文件（自动生成，可以删）");
            sb.AppendLine("CharsPerLine=" + CharsPerLine);
            sb.AppendLine("SkipBlank=" + (SkipBlank ? 1 : 0));
            sb.AppendLine("FollowSystemTheme=" + (FollowSystemTheme ? 1 : 0));
            sb.AppendLine("SwapAtDesktopCorner=" + (SwapAtDesktopCorner ? 1 : 0));
            sb.AppendLine("FontPx=" + FontPx);
            sb.AppendLine("AutoFont=" + (AutoFont ? 1 : 0));
            sb.AppendLine("AutoDelay=" + AutoDelay.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            sb.AppendLine("AutoPlay=" + (AutoPlay ? 1 : 0));
            sb.AppendLine("ClipWatch=" + (ClipWatch ? 1 : 0));
            sb.AppendLine("CoverTaskbar=" + (CoverTaskbar ? 1 : 0));
            sb.AppendLine("KeepTrayVisible=" + (KeepTrayVisible ? 1 : 0));
            sb.AppendLine("GlobalKeys=" + (GlobalKeys ? 1 : 0));
            sb.AppendLine("XOffset=" + XOffset);
            if (book != null && !string.IsNullOrEmpty(book.Path))
            {
                Pos[book.Path] = book.Index;
                Scroll[book.Path] = scrollOffset;
            }
            if (Recent.Count > 0) sb.AppendLine("Recent=" + string.Join("|", Recent.ToArray()));
            foreach (KeyValuePair<string, int> kv in Pos) sb.AppendLine("Pos=" + kv.Key + "|" + kv.Value);
            foreach (KeyValuePair<string, int> kv in Scroll) sb.AppendLine("Scroll=" + kv.Key + "|" + kv.Value);
            try
            {
                File.WriteAllText(File_, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }

            // 同时在书旁边放一份 .txtbar.json，书放到别的电脑也带着进度
            if (book != null && !string.IsNullOrEmpty(book.Path) && File.Exists(book.Path))
            {
                try
                {
                    string meta = "{\n  \"file\": \"" + EscapeJson(book.Path) + "\",\n" +
                                  "  \"index\": " + book.Index + ",\n" +
                                  "  \"charsPerLine\": " + book.CharsPerLine + ",\n" +
                                  "  \"percent\": " + book.Percent + ",\n" +
                                  "  \"scrollOffset\": " + scrollOffset + ",\n" +
                                  "  \"savedAt\": \"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\"\n}\n";
                    File.WriteAllText(book.Path + ".txtbar.json", meta, new UTF8Encoding(false));
                }
                catch { }
            }
        }

        static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        static int ParseInt(string s, int def, int lo, int hi)
        {
            int v;
            if (int.TryParse(s, out v)) return Math.Max(lo, Math.Min(hi, v));
            return def;
        }
        static double ParseDouble(string s, double def, double lo, double hi)
        {
            double v;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v)) return Math.Max(lo, Math.Min(hi, v));
            return def;
        }
    }
}
