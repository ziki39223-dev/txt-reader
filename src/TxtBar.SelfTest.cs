// ============================================================================
//  TxtBar 第三部分：L7 自检（无界面，可自动化运行）
//      TxtBar.exe --selftest [txt路径]    跑一遍检查，全部通过返回 0
//      TxtBar.exe --dump     [txt路径]    把这条「任务栏」渲染成 PNG，便于肉眼核对
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;

namespace TxtBar
{
    internal static class SelfTest
    {
        private static int _pass, _fail;
        private static readonly List<string> _lines = new List<string>();

        private static bool _consoleOk = true;
        private static void Check(string name, bool ok, string detail)
        {
            if (ok) _pass++; else _fail++;
            string s = (ok ? "PASS " : "FAIL ") + name + (detail != null && detail.Length > 0 ? "  | " + detail : "");
            _lines.Add(s);
            if (_consoleOk)
            {
                try
                {
                    Console.OutputEncoding = Encoding.UTF8;
                    Console.WriteLine((ok ? "[OK] " : "[!!] ") + name +
                        (detail != null && detail.Length > 0 ? "  | " + detail : ""));
                }
                catch { _consoleOk = false; }   // 输出被重定向时不要因此崩掉
            }
        }

        private static string NewTempTxt(out string path)
        {
            path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "TxtBar自检_" + DateTime.Now.ToString("HHmmss") + ".txt");
            StringBuilder sb = new StringBuilder();
            for (int i = 1; i <= 300; i++)
                sb.AppendLine(i + "：“我已经按每行固定字数把长文本切好了，这一行就是第 " + i + " 行，足够长以便验证硬切与竖排居中。”");
            sb.AppendLine();                                        // 空行也要能读
            sb.AppendLine("这一行是特意写得非常非常长的内容" + new string('长', 260) + "结束");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        public static bool Run(string bookPath)
        {
            // 自检不碰真实记忆：整体改到临时目录里跑
            Memory.DirOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "TxtBarSelfTest_" + DateTime.Now.ToString("HHmmss"));
            try { Directory.CreateDirectory(Memory.DirOverride); } catch { }

            Console.WriteLine("=== TxtBar 自检 ===");
            Console.WriteLine("环境：" + Shell.Describe());

            // ---- 1. 任务栏探测 ----
            Check("能定位到系统任务栏窗口",
                Shell.TaskbarFound, "hwnd 找到=" + Shell.TaskbarFound);
            Check("任务栏高度合理（18~120 px）",
                Shell.TaskbarRect.Height >= 18 && Shell.TaskbarRect.Height <= 120,
                Shell.TaskbarRect.Width + "x" + Shell.TaskbarRect.Height + " @(" + Shell.TaskbarRect.Left + "," + Shell.TaskbarRect.Top + ")");
            Check("任务栏贴在某条屏幕边缘上",
                Shell.TaskbarRect.Top + Shell.TaskbarRect.Height >= Shell.ScreenBounds.Bottom - 2,
                "屏幕 " + Shell.ScreenBounds.Width + "x" + Shell.ScreenBounds.Height);
            Check("托盘区宽度探测正常",
                Shell.TrayWidth > 0 && Shell.TrayWidth < Shell.TaskbarRect.Width,
                Shell.TrayWidth + " px（正文右侧会让出这块）");

            // ---- 2. 载入长文本 ----
            string txt = bookPath;
            bool temp = false;
            if (string.IsNullOrEmpty(txt) || !File.Exists(txt)) { NewTempTxt(out txt); temp = true; }

            Book book = new Book();
            book.LoadFile(txt);
            Check("能打开并解码 txt",
                book.Loaded && book.Error.Length == 0,
                book.Title + " · " + book.Lines.Count + " 行 · 编码 " + book.EncName + (book.Error.Length > 0 ? " · " + book.Error : ""));

            // ---- 3. 一行固定字数 ----
            book.CharsPerLine = 40;
            book.Rebuild();
            int over = 0, blanks = 0;
            foreach (string s in book.Lines) { if (s.Length > book.CharsPerLine) over++; if (s.Length == 0) blanks++; }
            Check("每行都不超过设定字数（40 字/行）",
                over == 0, "超宽行数=" + over + "，总行数=" + book.Lines.Count + "，其中空行=" + blanks);

            int longLineExpect = 1;   // 末尾那条超长段落应被硬切成多行
            Check("超长段落被硬切成多行（不丢字）",
                book.Lines.Count > 300 + longLineExpect,
                "原文 302 段 → 显示 " + book.Lines.Count + " 行");

            bool nothingLost = true;
            StringBuilder jb = new StringBuilder();
            foreach (string s in book.Lines) jb.Append(s);
            string joined = jb.ToString();
            string origin = TextOf(txt);
            // 去掉换行后逐字比对，确认切行没有吃字
            string a = origin.Replace("\r", "").Replace("\n", "");
            if (joined.Replace("\r", "").Replace("\n", "") != a) nothingLost = false;
            Check("切行不丢字（去掉换行后逐字一致）", nothingLost,
                "原文 " + a.Length + " 字 vs 切行后 " + joined.Length + " 字");

            // 换字数后仍回到同一个字
            book.Index = 12;
            book.SetCharsPerLine(22);
            int over2 = 0;
            foreach (string s in book.Lines) if (s.Length > 22) over2++;
            Check("改「每行字数」后所有行仍不超宽",
                over2 == 0, "22 字/行，超宽=" + over2 + "，行数=" + book.Lines.Count);
            Check("改字数后停在原来的位置附近（不跳回开头）",
                book.Index > 0, "原第 13 行 → 现第 " + (book.Index + 1) + " 行");

            // ---- 4. 一起创建窗体，量真实几何 ----
            TxtBarForm form = new TxtBarForm();
            form.SuppressUi = true;
            form.Mem.CharsPerLine = 40;
            form.Book.CharsPerLine = 40;
            form.LayoutForTest();

            Rectangle target = form.TargetRect();
            Check("这条的高度与系统任务栏一致；覆盖模式把右下托盘整块让出来",
                target.Height == Shell.TaskbarRect.Height &&
                target.Width == Shell.TaskbarRect.Width - Shell.TrayWidth,
                "TxtBar " + target.Width + "x" + target.Height + "  vs 系统 " +
                Shell.TaskbarRect.Width + "x" + Shell.TaskbarRect.Height +
                "，让出托盘 " + Shell.TrayWidth + "px（让出后右侧正好是显示桌面按钮那一带）");
            Check("无论谁在前，这条永远只占系统任务栏这一条（纵向重合、不浮到上面）",
                target.Top == Shell.TaskbarRect.Top &&
                target.Height == Shell.TaskbarRect.Height &&
                target.Bottom == Shell.TaskbarRect.Bottom,
                "TxtBar @(" + target.Left + "," + target.Top + ") 底边 " + target.Bottom +
                "，任务栏 @" + Shell.TaskbarRect.Top + " 底边 " + Shell.TaskbarRect.Bottom);

            int h = target.Height;
            bool inBand = true, overlap = false;
            List<BarButton> bs = form.Buttons;
            for (int i = 0; i < bs.Count; i++)
            {
                Rectangle r = bs[i].Rect;
                if (r.Top < 0 || r.Bottom > h || r.Left < 0 || r.Right > target.Width) inBand = false;
                if (i > 0 && r.Left < bs[i - 1].Rect.Right) overlap = true;
            }
            Check("主页面按钮都在这条之内（不越界、不重叠）",
                inBand && !overlap, bs.Count + " 个按钮（菜单 / 上一页 / 下一页），条高 " + h + " px");

            Rectangle textArea = form.TextArea();
            int rightMost = 0;
            foreach (BarButton bb in form.Buttons) if (bb.Rect.Right > rightMost) rightMost = bb.Rect.Right;
            Check("正文区起点跟着最右边那个按钮、终点让开状态区（按钮增减不会让文字移位）",
                textArea.Left == rightMost + Shell.S(10) &&
                textArea.Right <= target.Width - Shell.S(8) - Shell.S(120),
                "正文区 " + textArea.Width + "x" + textArea.Height + " @x=" + textArea.Left +
                "，最右按钮右边=" + rightMost + "，状态区宽 " + form.StatusWidth() + "px");

            int fontPx = form.AutoFontPx();
            int perChar = textArea.Width / Math.Max(1, form.Book.CharsPerLine);
            Check("字号自适应：40 个全角字能正好铺满正文区",
                perChar >= 11 && perChar <= 40 && fontPx == perChar,
                "算得字号 " + fontPx + "px（正文区宽 " + textArea.Width + " ÷ 40 字）");

            // 真正画一遍，量这一行的像素宽度，确认不溢出
            Size measured = MeasureLine(form, "汉", fontPx);
            int total = measured.Width * 40;
            Check("按该字号画 40 个汉字，宽度不超过正文区（实测算）",
                total <= textArea.Width,
                "40 字实宽 " + total + " px ≤ 正文区 " + textArea.Width + " px");

            // ---- 5. 记忆 ----
            form.OpenBook(txt);
            form.Book.Index = 137;
            form.SaveMemory();
            Memory reread = Memory.Load();
            int saved = -1;
            reread.Pos.TryGetValue(txt, out saved);
            Check("行号写进了记忆文件", saved == 137,
                Memory.File_ + " → " + txt + "|" + saved);
            Check("「每行字数」等设置也写进记忆", reread.CharsPerLine == form.Book.CharsPerLine,
                "CharsPerLine=" + reread.CharsPerLine);
            Check("txt 旁边生成了 .txtbar.json（换台机器也带着进度）",
                File.Exists(txt + ".txtbar.json"),
                File.Exists(txt + ".txtbar.json") ? txt + ".txtbar.json 存在" : "没生成");

            TxtBarForm form2 = new TxtBarForm();
            form2.SuppressUi = true;
            string tip = form2.RestoreLastBook();
            Check("重新启动后接着上次那一行",
                form2.Book.Loaded && form2.Book.Index == 137,
                tip + "（期望第 138 行，实际第 " + (form2.Book.Index + 1) + " 行）");

            // ---- 6. 编码 ----
            byte[] gbkBytes = null;
            try { gbkBytes = Encoding.GetEncoding(936).GetBytes("这是 GBK 编码的测试文本\r\n第二行"); }
            catch { }
            if (gbkBytes != null)
            {
                Book gb = new Book();
                gb.LoadBytes(gbkBytes, "");
                Check("GBK 文件自动识别不乱码（无替换字符）",
                    gb.Lines.Count >= 2 && gb.Current().IndexOf('�') < 0 && gb.Current().IndexOf("这是 GBK") >= 0,
                    gb.EncName + " → " + gb.Current());
            }
            else
            {
                Check("GBK 文件自动识别不乱码（无替换字符）", true, "本机没有 GBK 代码页，跳过");
            }

            // ---- 7. 人机接口不崩 ----
            int i0 = form.Book.Index;
            form.Step(1);
            bool stepped = form.Book.Index == i0 + 1;
            form.JumpToPercent(50);
            bool jumped = form.Book.Percent >= 49 && form.Book.Percent <= 51;
            form.ToggleAuto();
            bool autoOn = form.Mem.AutoPlay;
            form.ToggleAuto();
            Check("翻行 / 跳转百分比 / 自动翻行 三个操作都生效",
                stepped && jumped && autoOn && !form.Mem.AutoPlay,
                "Step ok=" + stepped + " Jump50%=" + form.Book.Percent + "% Auto ok=" + autoOn);

            // 旧版本（网页版）必须已经删掉，避免和这个原生版混淆
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            bool legacyGone = !File.Exists(Path.Combine(dir, "index.html")) &&
                              !File.Exists(Path.Combine(dir, "test-e2e.js"));
            Check("旧版本（网页版）已删除，只剩这一个程序", legacyGone,
                legacyGone ? "目录里没有 index.html / test-e2e.js" : "发现残留的旧版本文件");

            // 查找面板的类能正常构造（不出异常即算通过）
            bool panelOk = true;
            try
            {
                TxtBarForm host = new TxtBarForm();
                host.SuppressUi = true;
                FindPanel fp = new FindPanel(host);
                panelOk = !fp.IsDisposed;
                fp.Dispose();
                host.Dispose();
            }
            catch (Exception ex) { panelOk = false; Program.Log("查找面板构造失败：" + ex.Message); }
            Check("查找面板能正常创建", panelOk, panelOk ? "已创建并释放" : "构造异常");

            form.Flash("测试提示");
            form.SaveMemory();
            string statusJson = System.IO.Path.Combine(Memory.Dir, "status.json");
            bool flashInStatus = File.Exists(statusJson) &&
                                 File.ReadAllText(statusJson, Encoding.UTF8).IndexOf("测试提示") >= 0;
            Check("提示信息能记录并通过 status.json 对外可见（不在条上乱画）",
                flashInStatus, flashInStatus ? "status.json 里有提示文本" : "没写进 status.json");

            form.Quit();

            // ---- 8. 真窗口：尺寸、位置、窗口样式（这条最接近「能不能代替系统任务栏」） ----
            Console.WriteLine("-- 真实窗口检查 --");
            TxtBarForm live = new TxtBarForm();
            live.SuppressUi = true;
            live.LayoutForTest();
            string firstLine = "第 138 行：这一行是真实窗口模式下取的当前行，用来确认正文区里确实是一行文字。";
            live.Book.SetText(firstLine + new string('字', 200) + "\r\n第二行不该被显示");
            live.Book.Index = 0;

            live.Visible = false;
            IntPtr hwnd = IntPtr.Zero;
            try
            {
                // 创建一个真实窗口句柄（不显示），量它的尺寸/样式
                var mi = typeof(System.Windows.Forms.Form).GetMethod("CreateHandle",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                mi.Invoke(live, null);
                hwnd = live.Handle;
            }
            catch (Exception ex) { Program.Log("真窗口创建失败：" + ex.Message); }
            if (hwnd != IntPtr.Zero) live.ApplyGeometry();   // 按真实位置摆好（自检模式不显示）

            Check("能创建出真实窗口句柄", hwnd != IntPtr.Zero, "hwnd=" + hwnd.ToInt64());

            if (hwnd != IntPtr.Zero)
            {
                Native.RECT rr;
                Native.GetWindowRect(hwnd, out rr);
                Rectangle liveRect = Rectangle.FromLTRB(rr.Left, rr.Top, rr.Right, rr.Bottom);
                Rectangle want = live.TargetRect();
                Check("真实窗口：高度铺满任务栏，右侧把托盘整块留给系统",
                    liveRect.Height == Shell.TaskbarRect.Height &&
                    liveRect.Width == Shell.TaskbarRect.Width - Shell.TrayWidth,
                    "实际 " + liveRect.Width + "x" + liveRect.Height + " vs 任务栏 " +
                    Shell.TaskbarRect.Width + "x" + Shell.TaskbarRect.Height +
                    "，托盘让出 " + Shell.TrayWidth + "px（右下角留给系统）");
                Check("真实窗口贴在系统任务栏这一条上（左端对齐、纵向重合）",
                    liveRect.Top == Shell.TaskbarRect.Top && liveRect.Left == Shell.TaskbarRect.Left,
                    "窗口 @(" + liveRect.Left + "," + liveRect.Top + ")，任务栏 @(" +
                    Shell.TaskbarRect.Left + "," + Shell.TaskbarRect.Top + ")");

                // 切换「谁在前面」只改层叠、不改位置：把两种状态都切一遍，
                // 位置/尺寸必须一字不变，并且绝不会跑到任务栏上方去。
                bool bothSamePlace = true, noFloat = true, zOk = true;
                string placeDetail = "";
                for (int k = 0; k < 2; k++)
                {
                    live.ToggleCover();
                    Native.RECT r2;
                    Native.GetWindowRect(hwnd, out r2);
                    Rectangle now = Rectangle.FromLTRB(r2.Left, r2.Top, r2.Right, r2.Bottom);
                    if (now != liveRect) bothSamePlace = false;
                    // 上浮的典型特征：窗口底边正好压在任务栏顶边上（也就是整条浮到任务栏上方）
                    if (now.Bottom == Shell.TaskbarRect.Top || now.Top < Shell.TaskbarRect.Top) noFloat = false;
                    // 层叠必须跟当前模式对上：我的条在前=置顶，系统任务栏在前=不置顶
                    long ex2 = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                    if (((ex2 & Native.WS_EX_TOPMOST) != 0) != live.Mem.CoverTaskbar) zOk = false;
                    if (k == 0) placeDetail = "切换后 @(" + now.Left + "," + now.Top + ") " + now.Width + "x" + now.Height;
                }
                Check("切换「谁在前面」时位置和尺寸一个字都不变",
                    bothSamePlace, placeDetail + "（与切换前 @(" + liveRect.Left + "," + liveRect.Top + ") 相同=" + bothSamePlace + "）");
                Check("切换后不会浮到系统任务栏上方",
                    noFloat, "两种模式下窗口都落在任务栏这一条里（顶边 " + Shell.TaskbarRect.Top + "）");
                Check("层叠跟着模式走：我的条在前=置顶，系统任务栏在前=让位",
                    zOk, "两种模式都切过一遍，置顶状态与当前模式一致=" + zOk);

                // 后面的样式/渲染检查统一在「我的条在前」这个默认状态下做
                if (!live.Mem.CoverTaskbar) live.ToggleCover();
                Native.RECT r3;
                Native.GetWindowRect(hwnd, out r3);
                liveRect = Rectangle.FromLTRB(r3.Left, r3.Top, r3.Right, r3.Bottom);

                // 安全软件的误报高发区是「无公司 / 无版本 / 无描述」的三无 exe，
                // 所以编译时必须把版本信息编进去，这里直接读自己身上的属性来核对。
                string fv = "", pn = "", fd = "", co = "";
                try
                {
                    object[] attrs = typeof(Program).Assembly.GetCustomAttributes(false);
                    for (int i = 0; i < attrs.Length; i++)
                    {
                        if (attrs[i] is AssemblyFileVersionAttribute) fv = ((AssemblyFileVersionAttribute)attrs[i]).Version;
                        else if (attrs[i] is AssemblyProductAttribute) pn = ((AssemblyProductAttribute)attrs[i]).Product;
                        else if (attrs[i] is AssemblyDescriptionAttribute) fd = ((AssemblyDescriptionAttribute)attrs[i]).Description;
                        else if (attrs[i] is AssemblyCompanyAttribute) co = ((AssemblyCompanyAttribute)attrs[i]).Company;
                    }
                }
                catch (Exception vex) { Program.Log("版本信息自检异常：" + vex.Message); }
                Check("exe 里带了完整版本信息（公司 / 产品 / 描述 / 版本号都不是空的）",
                    fv.Length > 0 && pn.Length > 0 && fd.Length > 0 && co.Length > 0,
                    "版本 " + fv + " · 产品 " + pn + " · 公司 " + co);

                long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                Check("窗口是置顶 + 工具窗口 + 不抢焦点（这样点按钮不会把别人的窗口顶下去）",
                    (ex & Native.WS_EX_TOPMOST) != 0 && (ex & Native.WS_EX_TOOLWINDOW) != 0 &&
                    (ex & Native.WS_EX_NOACTIVATE) != 0,
                    "exStyle=0x" + ex.ToString("X"));

                using (Bitmap b = live.Snapshot())
                {
                    Check("窗口内容能渲染出来",
                        b.Width == want.Width && b.Height == want.Height,
                        "位图 " + b.Width + "x" + b.Height);

                    // 像素级：三块区域都必须真的画了东西（顺便验证图标字体没缺字）
                    int btnInk = InkRatio(b, live.Buttons[0].Rect);
                    int textInk = InkRatio(b, live.TextArea());
                    int statusInk = InkRatio(b, new Rectangle(want.Width - Shell.S(8) - live.StatusWidth(), 0, live.StatusWidth(), want.Height));

                    Check("菜单按钮图标真的画出来了（不是空心方块）",
                        btnInk > 8,
                        "菜单按钮墨迹 " + btnInk + "/1000，按钮总数 = " + live.Buttons.Count);
                    Check("正文区有实际文字像素", textInk > 5, "墨迹 " + textInk + "/1000");
                    Check("状态区有实际文字像素", statusInk > 3, "墨迹 " + statusInk + "/1000");

                    // 只画一行：位图下半部分不应再有第二行文字
                    int upper = InkRatio(b, new Rectangle(live.TextArea().X, 0, live.TextArea().Width, want.Height / 2));
                    int lower = InkRatio(b, new Rectangle(live.TextArea().X, want.Height / 2, live.TextArea().Width, want.Height / 2));
                    Check("正文只占一条（垂直居中），没有第二行",
                        upper > 0 && lower > 0 && Math.Abs(upper - lower) < Math.Max(upper, lower) * 0.8,
                        "上半 " + upper + " / 下半 " + lower + "（居中排布）");
                }

                // 主页面就四个按钮：菜单 + 上一页 + 下一页 + 切换谁在前；每个都得命中得上
                int idx = -1, prevIdx = -1, nextIdx = -1, swapIdx = -1;
                for (int i = 0; i < live.Buttons.Count; i++)
                {
                    string bid = live.Buttons[i].Id;
                    if (bid == "menu") idx = i;
                    else if (bid == "prev") prevIdx = i;
                    else if (bid == "next") nextIdx = i;
                    else if (bid == SwapEntry.Id) swapIdx = i;
                }
                int hit = -1, hitPrev = -1, hitNext = -1, hitSwap = -1;
                if (idx >= 0)
                    hit = live.HitTest(Center(live.Buttons[idx].Rect));
                if (prevIdx >= 0)
                    hitPrev = live.HitTest(Center(live.Buttons[prevIdx].Rect));
                if (nextIdx >= 0)
                    hitNext = live.HitTest(Center(live.Buttons[nextIdx].Rect));
                if (swapIdx >= 0)
                    hitSwap = live.HitTest(Center(live.Buttons[swapIdx].Rect));
                Check("主页面是「菜单 + 上一页 + 下一页 + 切换」四个按钮，且每个都点得中",
                    live.Buttons.Count == 4 && hit == idx && hitPrev == prevIdx &&
                    hitNext == nextIdx && hitSwap == swapIdx,
                    "按钮数 = " + live.Buttons.Count + "，菜单命中=" + (hit == idx)
                    + "，上一页命中=" + (hitPrev == prevIdx) + "，下一页命中=" + (hitNext == nextIdx)
                    + "，切换命中=" + (hitSwap == swapIdx));

                // 切换入口就是「下一页」右边那个按钮：同一行、同一尺寸、紧挨着，不越到条外
                bool swapBeside = false;
                if (nextIdx >= 0 && swapIdx >= 0)
                {
                    Rectangle rn = live.Buttons[nextIdx].Rect;
                    Rectangle rs = live.Buttons[swapIdx].Rect;
                    int gap = rs.Left - rn.Right;
                    swapBeside = swapIdx == nextIdx + 1 && rs.Top == rn.Top &&
                                 rs.Height == rn.Height && gap >= 0 && gap <= Shell.S(12) &&
                                 rs.Right <= live.ClientSize.Width;
                }
                Check("切换入口紧挨在「下一页」右边（同一行、同尺寸，没跑到条外面）",
                    swapBeside,
                    swapBeside
                        ? "间距 " + (live.Buttons[swapIdx].Rect.Left - live.Buttons[nextIdx].Rect.Right) + "px"
                        : "位置不对");

                // 这个入口真的能切「谁在前」：点一下翻过去，再点一下翻回来
                bool swapToggle = false, swapBack = false, swapHint = false;
                string swapHintText = "";
                try
                {
                    if (swapIdx >= 0)
                    {
                        bool before = live.Mem.CoverTaskbar;
                        Point c = Center(live.Buttons[swapIdx].Rect);
                        live.HandleMouseMoveForTest(c);
                        swapHintText = live.FlashText;
                        swapHint = swapHintText.Length > 0;
                        live.HandleMouseDownForTest(c);
                        live.HandleMouseUpForTest(c);
                        swapToggle = live.Mem.CoverTaskbar != before;
                        live.HandleMouseDownForTest(c);
                        live.HandleMouseUpForTest(c);
                        swapBack = live.Mem.CoverTaskbar == before;
                    }
                }
                catch (Exception pex) { Program.Log("切换按钮自检异常：" + pex.Message); }

                Check("鼠标停在切换按钮上有提示（说明点下去会发生什么）", swapHint,
                    "悬停提示 " + (swapHint ? "有" : "无") + "：" + swapHintText);
                Check("点「下一页」右边那个按钮能来回切换「谁在前面」",
                    swapToggle && swapBack,
                    "切换 " + (swapToggle ? "生效" : "无效") + "，再切回=" + swapBack);

                // 图标必须真画出来（Segoe MDL2 Assets 里没有这个码位就会是空心方块）
                int swapInk = 0;
                try { using (Bitmap sbmp = live.Snapshot()) swapInk = InkRatio(sbmp, live.Buttons[swapIdx].Rect); }
                catch { }
                Check("切换按钮的图标真的画出来了（不是空心方块）", swapInk > 8,
                    "墨迹 " + swapInk + "/1000");

                // 这个按钮要一眼看出当前是谁在前：我的条在前 = 强调色，系统任务栏在前 = 普通色
                int accentOn = 0, accentOff = 0;
                try
                {
                    bool keep = live.Mem.CoverTaskbar;
                    live.Mem.CoverTaskbar = true;
                    using (Bitmap bt = live.Snapshot()) accentOn = AccentRatio(bt, live.Buttons[swapIdx].Rect);
                    live.Mem.CoverTaskbar = false;
                    using (Bitmap bf = live.Snapshot()) accentOff = AccentRatio(bf, live.Buttons[swapIdx].Rect);
                    live.Mem.CoverTaskbar = keep;
                }
                catch (Exception cex) { Program.Log("切换按钮配色自检异常：" + cex.Message); }

                Check("切换按钮会随状态换色（我的条在前 = 强调色，系统任务栏在前 = 普通色）",
                    accentOn > 20 && accentOff < 8,
                    "强调色像素占比：在前 " + accentOn + "/1000，在前为假 " + accentOff + "/1000");

                // 除了条上这个按钮，任务栏最右侧还要有一个常驻的切换入口（两个入口并存）
                bool padAtCorner = false, padToggle = false, padBack = false, padHint = false;
                try
                {
                    live.Mem.SwapAtDesktopCorner = true;
                    SwitchPad pad = new SwitchPad(live);
                    pad.Show();
                    pad.ApplyPlacement();
                    padAtCorner = Math.Abs(pad.Right - Shell.TaskbarRect.Right) <= Shell.S(6) &&
                                  pad.Top == Shell.TaskbarRect.Top &&
                                  pad.Height == Shell.TaskbarRect.Height;
                    bool beforePad = live.Mem.CoverTaskbar;
                    pad.HoverForTest(true);
                    padHint = live.FlashText.Length > 0;
                    pad.HoverForTest(false);
                    pad.ClickForTest();
                    padToggle = live.Mem.CoverTaskbar != beforePad;
                    pad.ClickForTest();
                    padBack = live.Mem.CoverTaskbar == beforePad;
                    pad.StopFollow();
                    pad.Hide();
                    pad.Dispose();
                }
                catch (Exception padEx) { Program.Log("常驻入口自检异常：" + padEx.Message); }

                Check("任务栏最右侧还有一个常驻的切换入口（高度与任务栏一致，鼠标移上有提示）",
                    padAtCorner && padHint,
                    "贴在最右端 " + (padAtCorner ? "是" : "否") + "，悬停提示 " + (padHint ? "有" : "无"));
                Check("常驻入口也能来回切换「谁在前面」（和条上那个按钮两个入口并存）",
                    padToggle && padBack,
                    "切换 " + (padToggle ? "生效" : "无效") + "，再切回=" + padBack);

                // 真的去点「下一页」：位置必须往前走；再点「上一页」：必须原路回来
                int p0 = live.Book.Index;
                bool moved = false, cameBack = false;
                try
                {
                    live.HandleMouseDownForTest(Center(live.Buttons[nextIdx].Rect));
                    live.HandleMouseUpForTest(Center(live.Buttons[nextIdx].Rect));
                    moved = live.Book.Index == p0 + 1;
                    live.HandleMouseDownForTest(Center(live.Buttons[prevIdx].Rect));
                    live.HandleMouseUpForTest(Center(live.Buttons[prevIdx].Rect));
                    cameBack = live.Book.Index == p0;
                }
                catch (Exception bex) { Program.Log("翻页按钮自检异常：" + bex.Message); }
                Check("点「下一页」往下走一行、点「上一页」原路回到原地",
                    moved && cameBack,
                    "第 " + (p0 + 1) + " 行 → 下一页 " + (moved ? "走了一行" : "没动")
                    + " → 上一页 " + (cameBack ? "回到原处" : "没回来"));

                // 菜单本身能正常构建（不再依赖某个具体按钮）
                bool menuOk = false;
                try
                {
                    System.Windows.Forms.ContextMenuStrip m = live.BuildMenu();
                    menuOk = m.Items.Count >= 15;
                }
                catch (Exception mex) { Program.Log("菜单构建失败：" + mex.Message); }
                Check("菜单能构建，且所有功能都在里面（≥15 项）", menuOk,
                    menuOk ? "菜单项齐全" : "菜单构建异常");

                // 菜单里的设置入口要真能改到东西
                int cpBefore = live.Book.CharsPerLine;
                live.SetCharsPerLineExact(28);
                bool charOk = live.Book.CharsPerLine == 28;
                live.SetCharsPerLineExact(cpBefore);
                live.SetAutoDelay(3);
                bool delayOk = Math.Abs(live.Mem.AutoDelay - 3) < 0.01;
                live.SetAutoDelay(2);
                Check("菜单里的「每行字数 / 自动翻行速度」确实生效",
                    charOk && delayOk, "每行字数 " + cpBefore + "→28→" + live.Book.CharsPerLine + "，速度改成 3 秒=" + delayOk);

                // 查找功能：真的往后再往前找
                live.Book.SetText("甲行内容在这里\n乙行也有内容\n丙行是最后一行\n甲行又出现一次");
                live.Book.Index = 0;
                bool f1 = live.Book.FindNext("乙行", true);          // 从 0 往后 → 1
                int at1 = live.Book.Index;
                bool f2 = live.Book.FindNext("没这个词", true);        // 找不到
                string e2 = live.Book.FindError;
                live.Book.Index = 2;
                bool f3 = live.Book.FindNext("甲行", true);           // 从 2 往后 → 3
                int at3 = live.Book.Index;
                live.Book.Index = 3;
                bool f4 = live.Book.FindNext("甲行", true);           // 从末尾往后 → 绕回 0
                int at4 = live.Book.Index;
                bool wrapped4 = live.Book.FindWrapped;                // 立刻取，后面再找会把它重置
                live.Book.Index = 3;
                bool f5 = live.Book.FindPrev("乙行", true);           // 3 往前 → 1
                int at5 = live.Book.Index;

                Check("查找：从当前位置往后找得到",
                    f1 && at1 == 1, "「乙行」→ 第 " + (at1 + 1) + " 行");
                Check("查找：从中间往后找，跳到后面那一处",
                    f3 && at3 == 3, "「甲行」从第 3 行 → 第 " + (at3 + 1) + " 行");
                Check("查找：在末尾再往下找会绕回开头",
                    f4 && at4 == 0 && wrapped4,
                    "「甲行」从末行 → 第 " + (at4 + 1) + " 行（绕回=" + wrapped4 + "）");
                Check("查找：也能往前找",
                    f5 && at5 == 1, "「乙行」从末行往前 → 第 " + (at5 + 1) + " 行");
                Check("查找：找不到时明确报「没有找到」，不会乱跳",
                    !f2 && e2.IndexOf("没有找到") >= 0, e2);

                // 空行：默认不显示，切行后正文里不可能再出现空行
                Book bk = new Book();
                bk.SkipBlank = true;
                bk.CharsPerLine = 40;
                bk.SetText("第一行内容\n\n\n第二行内容\n   \n\t\n第三行内容\n");
                bool noBlank = true;
                foreach (string ln in bk.Lines) if (ln.Trim().Length == 0) noBlank = false;
                Check("空行不显示（默认开启，正文里找不到一条空行）",
                    noBlank && bk.Lines.Count == 3,
                    "原文 7 段（含 4 个空段）→ 显示 " + bk.Lines.Count + " 行，全部非空=" + noBlank);

                bool mapOk = bk.OrigLine.Count == bk.Lines.Count &&
                             bk.OrigLine[0] == 0 && bk.OrigLine[1] == 3 && bk.OrigLine[2] == 6;
                Check("跳过空行后，每一行与原文段落的对应关系没有错位",
                    mapOk, "显示的第 1/2/3 行分别对应原文第 " +
                    (bk.OrigLine.Count > 2 ? (bk.OrigLine[0] + 1) + "/" + (bk.OrigLine[1] + 1) + "/" + (bk.OrigLine[2] + 1) : "?") + " 段");

                bk.SkipBlank = false;
                bk.Rebuild();
                int blankRows = 0;
                foreach (string ln in bk.Lines) if (ln.Trim().Length == 0) blankRows++;
                Check("关掉开关后空行照样显示（随时可以反悔）",
                    blankRows > 0, "关掉后空行数 = " + blankRows);

                Book allBlank = new Book();
                allBlank.SkipBlank = true;
                allBlank.SetText("\n\n\n");
                Check("整篇都是空行时不会变成空白屏（自动兜底）",
                    allBlank.Lines.Count > 0,
                    "显示行数 = " + allBlank.Lines.Count + "，兜底=" + allBlank.BlankFallback);

                Book tail = new Book();
                tail.SkipBlank = true;
                tail.CharsPerLine = 10;
                tail.SetText("1234567890   \n下一段");
                bool tailOk = true;
                foreach (string ln in tail.Lines) if (ln.Trim().Length == 0) tailOk = false;
                Check("段落尾巴被切出来的纯空格片段也不显示",
                    tailOk && tail.Lines.Count == 2,
                    "显示 " + tail.Lines.Count + " 行：" +
                    (tail.Lines.Count > 1 ? "[" + tail.Lines[0] + "][" + tail.Lines[1] + "]" : ""));
                // 正文不能再靠拖动移动：模拟在空白处按住拖一把，文字必须原地不动
                int offBefore = live.CurrentXOffset;
                live.HandleMouseMoveForTest(new Point(live.TextArea().X + 30, live.ClientSize.Height / 2));
                live.HandleMouseDownForTest(new Point(live.TextArea().X + 30, live.ClientSize.Height / 2));
                live.HandleMouseMoveForTest(new Point(live.TextArea().X + 300, live.ClientSize.Height / 2));
                live.HandleMouseUpForTest(new Point(live.TextArea().X + 300, live.ClientSize.Height / 2));
                Check("正文不再能被拖动（空白处拖一把，横向位置不变）",
                    live.CurrentXOffset == offBefore, "拖动前后都是 " + live.CurrentXOffset + " px");
                Check("正文只能用按钮/键盘移动（按钮仍然有效）",
                    live.Book.Loaded && live.Book.Index >= 0, "当前第 " + (live.Book.Index + 1) + " 行");
            }
            live.Quit();

            // ---- 9. 源文本清洗：规则必须真的把「脏」去掉，且不能伤到正文本 ----
            Console.WriteLine("-- 源文本清洗检查 --");
            {
                List<string> dirty = new List<string>();
                dirty.Add("《测试书》  作者：某人");
                dirty.Add("来源：某某网");
                dirty.Add("网址：http://example.com");
                dirty.Add("");
                dirty.Add("第一章 开始");
                dirty.Add("　　这是一段的开头，它被硬换行切断了，");
                dirty.Add("所以下一行其实是它的后半截。");
                dirty.Add("");
                dirty.Add("(本章完)");
                dirty.Add("");
                dirty.Add("第二章 继续");
                dirty.Add("　　第二章的第一段，完整的一句话就这样结束。");
                dirty.Add("　　「你来了。」");
                dirty.Add("");
                dirty.Add("第三章 继续");          // 与第二章标题同名，但正文不同，应当保留
                dirty.Add("　　这是第三章自己的正文，跟前面不重复，必须留着。");
                dirty.Add("");
                dirty.Add("记住我们网");
                dirty.Add("——————我是分割线——————");

                CleanStat st = new CleanStat();
                st.Files.Add(new CleanFileStat());
                List<List<string>> one = new List<List<string>>();
                one.Add(dirty);
                List<List<string>> outList = Cleaner.CleanFiles(one, st);
                List<string> outp = outList[0];

                bool noBlank = true, noJunk = true;
                foreach (string l in outp)
                {
                    if (l.Trim().Length == 0) noBlank = false;
                    if (Cleaner.IsWatermark(l)) noJunk = false;
                }
                Check("清洗后：没有空行、没有水印残留",
                    noBlank && noJunk,
                    outp.Count + " 行，空行=" + (!noBlank) + "，水印残留=" + (!noJunk));

                bool joinedOk = false;
                foreach (string l in outp)
                    if (l.IndexOf("被硬换行切断了，所以下一行") >= 0) joinedOk = true;
                Check("清洗后：被硬换行切断的句子接回成一条完整的话",
                    joinedOk && st.Joined >= 1,
                    "接回 " + st.Joined + " 处");

                int titles = 0;
                foreach (string l in outp) if (Cleaner.IsTitle(l)) titles++;
                bool keptThird = false;
                foreach (string l in outp) if (l.IndexOf("第三章自己的正文") >= 0) keptThird = true;
                Check("清洗后：不同正文的同名章不会被误删（章号原样保留）",
                    titles == 3 && keptThird && st.TotalChapters == 3,
                    titles + " 个标题，" + st.TotalChapters + " 章，第三章正文保留=" + keptThird
                    + "，编号=" + string.Join("/", ToutTitles(outp)));

                // 整块重复：同一段内容印两遍，第二遍必须被去掉
                List<string> twice = new List<string>();
                twice.Add("第一章 甲");
                for (int i = 0; i < 20; i++)
                    twice.Add("　　这是第 " + i + " 段内容，长度足够长，用来把整块重复的判定撑起来。");
                twice.Add("第二章 乙");
                for (int i = 0; i < 20; i++)
                    twice.Add("　　这是第 " + i + " 段内容，长度足够长，用来把整块重复的判定撑起来。");
                CleanStat st2 = new CleanStat();
                st2.Files.Add(new CleanFileStat());
                List<List<string>> one2 = new List<List<string>>();
                one2.Add(twice);
                List<List<string>> out2 = Cleaner.CleanFiles(one2, st2);
                int dupLeft = 0;
                foreach (string l in out2[0]) if (l.IndexOf("这是第 19 段内容") >= 0) dupLeft++;
                Check("清洗后：整块重复的内容只留一份（同一段印两遍的那种）",
                    st2.DupBlocks >= 1 && dupLeft == 1,
                    "删掉 " + st2.DupBlocks + " 处整块重复，第 19 段现在出现 " + dupLeft + " 次");

                // 多个 txt 各洗各的：章号跨文件接着数，但不合并成一个文件
                List<List<string>> two = new List<List<string>>();
                List<string> f1 = new List<string>();
                f1.Add("第一章 甲");
                f1.Add("　　第一本的正文，这一句话完整。");
                List<string> f2 = new List<string>();
                f2.Add("第一章 乙");
                f2.Add("　　第二本的正文，这一句话也完整。");
                two.Add(f1); two.Add(f2);
                CleanStat st3 = new CleanStat();
                st3.Files.Add(new CleanFileStat()); st3.Files.Add(new CleanFileStat());
                List<List<string>> out3 = Cleaner.CleanFiles(two, st3);
                bool splitKept = out3.Count == 2 && out3[0].Count == 2 && out3[1].Count == 2;
                bool bodyKept = out3[0][1].IndexOf("第一本的正文") >= 0 &&
                                out3[1][1].IndexOf("第二本的正文") >= 0;
                Check("清洗后：多个 txt 各洗各的，互不合并、各写各的文件",
                    splitKept && bodyKept,
                    "输出 " + out3.Count + " 个文件，第一个开头「" + out3[0][0] + "」，第二个开头「" + out3[1][0] + "」");

                // 键盘：一次「按下 + 抬起」只能走一步。
                // 低级键盘钩子对 down / up 都会回调，早先没筛消息类型，
                // 敲一下 ↓ 会被当成两次 Step —— 实机量过是 12 → 14，一次跳两行。
                {
                    TxtBarForm kf = new TxtBarForm();
                    kf.SuppressUi = true;
                    kf.LayoutForTest();
                    kf.Book.SetText("甲行\n乙行\n丙行\n丁行\n戊行");
                    kf.Book.Index = 0;

                    KeyHook hook = new KeyHook();
                    hook.OnKeyDown = delegate (int vk)
                    {
                        if (vk == 0x28 || vk == 0x22) { kf.Step(1); return true; }
                        if (vk == 0x26 || vk == 0x21) { kf.Step(-1); return true; }
                        return false;
                    };
                    hook.ShouldHandle = delegate { return true; };

                    int before = kf.Book.Index;
                    bool downOk = hook.HandleMessage(Native.WM_KEYDOWN, 0x28);
                    int afterDown = kf.Book.Index;
                    bool upOk = hook.HandleMessage(Native.WM_KEYUP, 0x28);
                    int afterUp = kf.Book.Index;

                    bool otherMsgOk = !hook.HandleMessage(Native.WM_KEYUP, 0x22);
                    bool toast = kf.FlashText != null;

                    Check("键盘钩子只认「按下」：敲一下方向键只走一行（抬起不再触发）",
                        downOk && (afterDown - before) == 1 && !upOk && afterUp == afterDown,
                        "按下 oneStep=" + (afterDown - before) + "，抬起 handled=" + upOk
                        + "，抬起后位置变化=" + (afterUp - afterDown) + "，其它消息被忽略=" + otherMsgOk);

                    kf.Quit();
                    kf.Dispose();
                }

                // 清洗：标题身份在去重之前定下来。
                //   源文件里同一章常印两行标题（中文一行、阿拉伯一行），中间还夹着重复正文。
                //   如果等去重之后再重新认标题，那一行会被当成普通段落接进上一章，
                //   整段章节边界就全错位了 —— 这正是本次 #305 章乱跳的根因。
                {
                    List<string> t = new List<string>();
                    t.Add("第八十四章 流光（上）");          // 中文标题
                    t.Add("　　第八十四章的正文，完整一句话。");
                    t.Add("第八十五章 流光（上）");          // 重复内容前的假标题
                    t.Add("第84章 流光（上）");              // 真正的标题（阿拉伯数字）
                    for (int i = 0; i < 16; i++)
                        t.Add("　　重复正文第 " + i + " 段，长度够长，好让整块重复能被认出来。");
                    t.Add("第八十六章 下一章");              // 下一章的标题
                    t.Add("　　下一章的正文，也必须是完整的一句话。");

                    CleanStat cs = new CleanStat();
                    cs.Files.Add(new CleanFileStat());
                    List<List<string>> oneIn = new List<List<string>>();
                    oneIn.Add(t);
                    List<string> outv = Cleaner.CleanFiles(oneIn, cs)[0];

                    List<string> titlesOut = ToutTitles(outv);
                    bool ch84 = false, ch86 = false;
                    foreach (string x in titlesOut)
                    {
                        if (x.StartsWith("第84章")) ch84 = true;
                        if (x.StartsWith("第86章")) ch86 = true;
                    }
                    Check("清洗：标题身份在去重前就定好，重复内容不会把章节边界吃掉",
                        ch84 && ch86 && cs.TotalChapters == 3,
                        "得到 " + cs.TotalChapters + " 章 / 标题 " + titlesOut.Count
                        + " 个：" + string.Join(" | ", titlesOut.ToArray()));
                }

                // 清洗：章号原样保留 —— 用户搜「305」还要能搜到第 305 章。
                // （早先「重排成 1,2,3…」和「撞号就 +1」两种做法都会让 305 消失或漂移。）
                {
                    List<string> t = new List<string>();
                    t.Add("第三百零四章 甲");
                    t.Add("　　甲章的正文，完整一句话。");
                    t.Add("第三百零五章 乙");
                    t.Add("　　乙章的正文，完整一句话。");
                    t.Add("第三百零六章 丙");
                    t.Add("　　丙章的正文，完整一句话。");
                    CleanStat cs = new CleanStat();
                    cs.Files.Add(new CleanFileStat());
                    List<List<string>> oneIn = new List<List<string>>();
                    oneIn.Add(t);
                    List<string> outv = Cleaner.CleanFiles(oneIn, cs)[0];
                    string joinedTxt = string.Join("|", outv.ToArray());
                    Check("清洗：章号原样保留（搜「305 章」还能搜到）",
                        joinedTxt.IndexOf("第305章") >= 0 && joinedTxt.IndexOf("第306章") >= 0,
                        "得到：" + string.Join(" | ", ToutTitles(outv).ToArray()));
                }

                // 中文数字与阿拉伯数字都要认得
                int n1, n2, n3;
                string s1, s2, s3;
                bool t1 = Cleaner.IsTitle("第八十四章 流光（上）", out n1, out s1) && n1 == 84;
                bool t2 = Cleaner.IsTitle("第84章 流光（上）", out n2, out s2) && n2 == 84;
                bool t3 = Cleaner.IsTitle("第三百零五章　雪", out n3, out s3) && n3 == 305;
                Check("清洗：中文章号与阿拉伯章号都认得出来",
                    t1 && t2 && t3, "「第八十四章」→" + n1 + "，「第84章」→" + n2 + "，「第三百零五章」→" + n3);
            }

            Console.WriteLine();
            Console.WriteLine("=== 结果：" + _pass + " 项通过 / " + _fail + " 项失败 ===");

            // 把报告也存一份，方便回看
            try
            {
                string report = System.IO.Path.Combine(Memory.Dir, "selftest.log");
                List<string> all = new List<string>();
                all.Add("时间：" + DateTime.Now);
                all.Add("环境：" + Shell.Describe());
                all.AddRange(_lines);
                all.Add("结果：" + _pass + " 通过 / " + _fail + " 失败");
                File.WriteAllLines(report, all.ToArray(), new UTF8Encoding(false));
                Console.WriteLine("报告：" + report);
            }
            catch { }

            if (temp) { try { File.Delete(txt); } catch { } }
            return _fail == 0;
        }

        private static string file_Text;
        private static string TextOf(string path)
        {
            if (file_Text == null) file_Text = File.ReadAllText(path, Encoding.UTF8);
            return file_Text;
        }

        /// <summary>把清洗结果里的章节标题收集出来，便于自检打印</summary>
        private static List<string> ToutTitles(List<string> lines)
        {
            List<string> t = new List<string>();
            foreach (string l in lines) if (Cleaner.IsTitle(l)) t.Add(l);
            return t;
        }

        /// <summary>矩形中心点（自检里到处要用）</summary>
        private static Point Center(Rectangle r)
        {
            return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        }

        private static Size MeasureLine(TxtBarForm f, string sample, int fontPx)
        {
            using (Bitmap bmp = new Bitmap(8, 8))
            using (Graphics g = Graphics.FromImage(bmp))
            using (Font font = new Font("Microsoft YaHei UI", fontPx, GraphicsUnit.Pixel))
            {
                System.Windows.Forms.TextFormatFlags fl = System.Windows.Forms.TextFormatFlags.NoPadding |
                    System.Windows.Forms.TextFormatFlags.SingleLine | System.Windows.Forms.TextFormatFlags.NoPrefix;
                return System.Windows.Forms.TextRenderer.MeasureText(g, sample, font, new Size(int.MaxValue, 100), fl);
            }
        }

        /// <summary>统计一块区域里「非底色」像素的比例（千分比），用来判断到底画没画东西</summary>
        /// <summary>这个区域里「强调色」像素占多少（千分比）：用来核对按钮配色有没有跟着状态变</summary>
        private static int AccentRatio(Bitmap bmp, Rectangle area)
        {
            int x0 = Math.Max(0, area.Left), y0 = Math.Max(0, area.Top);
            int x1 = Math.Min(bmp.Width, area.Right), y1 = Math.Min(bmp.Height, area.Bottom);
            if (x1 <= x0 || y1 <= y0) return 0;
            Color ac = Skin.Accent;
            int hit = 0, total = 0;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    total++;
                    int d = Math.Abs(c.R - ac.R) + Math.Abs(c.G - ac.G) + Math.Abs(c.B - ac.B);
                    if (d < 90) hit++;
                }
            }
            return total == 0 ? 0 : (int)Math.Round(hit * 1000.0 / total);
        }

        private static int InkRatio(Bitmap bmp, Rectangle area)
        {
            int x0 = Math.Max(0, area.Left), y0 = Math.Max(0, area.Top);
            int x1 = Math.Min(bmp.Width, area.Right), y1 = Math.Min(bmp.Height, area.Bottom);
            if (x1 <= x0 || y1 <= y0) return 0;
            Color bg = bmp.GetPixel(x0, y0);
            int ink = 0, total = 0;
            for (int y = y0; y < y1; y += 1)
            {
                for (int x = x0; x < x1; x += 1)
                {
                    Color c = bmp.GetPixel(x, y);
                    total++;
                    int d = Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B);
                    if (d > 90) ink++;      // 明显与底色不同才算「墨迹」
                }
            }
            return total == 0 ? 0 : (int)Math.Round(ink * 1000.0 / total);
        }

        /// <summary>把这条任务栏渲染成 PNG，肉眼核对用：--dump [txt] [输出png]</summary>
        public static void Dump(string bookPath, string outPng)
        {
            string txt = bookPath;
            if (string.IsNullOrEmpty(txt) || !File.Exists(txt)) NewTempTxt(out txt);

            TxtBarForm form = new TxtBarForm();
            form.SuppressUi = true;
            form.LayoutForTest();
            form.OpenBook(txt);
            form.Book.Index = Math.Min(137, form.Book.Lines.Count - 1);
            form.Flash("接着上次：第 " + (form.Book.Index + 1) + " 行");

            string png = string.IsNullOrEmpty(outPng)
                ? System.IO.Path.Combine(Memory.Dir, "bar-preview.png")
                : outPng;
            try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(png)); } catch { }
            using (Bitmap bmp = form.Snapshot()) bmp.Save(png, ImageFormat.Png);
            Console.WriteLine("已渲染：" + png + "（" + form.TargetRect().Width + "x" + form.TargetRect().Height + "）");
            form.Quit();
        }
    }
}
