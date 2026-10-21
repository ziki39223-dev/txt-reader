// ============================================================================
//  TxtBar 第六部分：L8 源文本清洗（Cleaner）
//
//  网上下来的 txt 通常带这些毛病，读起来就会觉得「翻页在跳」：
//    1) 同一章被印了两遍 —— 本书实测重复内容占了 47%，翻着翻着就绕回去
//    2) 一句完整的话被硬换行切断，阅读器按每行字数再切一次，碎成一条 2 个字的尾巴
//    3) 满篇 (本章完) / 网址 / 「记住我们网」 / 分割线 之类的水印
//    4) 章号乱：中文数字和阿拉伯数字混排，还大量倒退
//
//  目录约定（两个文件夹，结构一一对应）：
//      书库\原始\<书名>\*.txt     ← 你下好的 txt，一本书一个子文件夹，可以多个 txt
//      书库\已清洗\<书名>\*.txt    ← 清洗结果，文件名跟原来一样，一个 txt 对一个 txt
//
//  用法：
//      双击 TxtClean.exe           扫整个书库，洗完弹一张报告
//      TxtClean.exe --quiet        命令行清洗，报告打到控制台（便于自动化）
//      TxtClean.exe "书库根目录"    指定书库根目录
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace TxtBar
{
    // ========================================================================
    //  L8a  清洗统计：洗完要能说清楚"到底动了什么"
    // ========================================================================
    public sealed class CleanFileStat
    {
        public string Name = "";
        public string EncName = "";
        public int LinesIn;        // 读进来多少行
        public int Watermarks;     // 删掉多少行水印
        public int Chapters;       // 洗完多少章
        public int CharsIn;        // 清洗前字数
        public int CharsOut;       // 清洗后字数
    }

    public sealed class CleanStat
    {
        public string Book = "";
        public string OutDir = "";
        public string Error = "";
        public List<CleanFileStat> Files = new List<CleanFileStat>();
        public int Joined;         // 接回多少处被切断的句子
        public int Repeats;        // 删掉多少条紧挨着的重复行
        public int NumFixed;       // 修正了多少个倒退/撞号的章号
        public int DupBlocks;      // 删掉多少处整块重复
        public int DupChapters;    // 删掉多少重复章节
        public int EmptyChapters;  // 丢掉多少只有标题没有正文的章节

        public int TotalIn
        {
            get { int n = 0; foreach (CleanFileStat f in Files) n += f.CharsIn; return n; }
        }
        public int TotalOut
        {
            get { int n = 0; foreach (CleanFileStat f in Files) n += f.CharsOut; return n; }
        }
        public int TotalChapters
        {
            get { int n = 0; foreach (CleanFileStat f in Files) n += f.Chapters; return n; }
        }
        public int SavedPercent
        {
            get { return TotalIn <= 0 ? 0 : (int)Math.Round((TotalIn - TotalOut) * 100.0 / TotalIn); }
        }
    }

    // ========================================================================
    //  L8b  清洗引擎
    // ========================================================================
    public static class Cleaner
    {
        public const string RootName = "书库";
        public const string RawName = "原始";
        public const string OutName = "已清洗";

        // ---- 水印：整行就是这些字的，直接删 ----
        private static readonly string[] JunkExact = new string[]
        {
            "本章完", "(本章完)", "（本章完）", "又见本章完",
            "全文完", "(全文完)", "（全文完）",
            "未完待续", "(未完待续)", "（未完待续）",
            "()", "（）"
        };

        // ---- 水印：像这些样子的，也删 ----
        private static readonly Regex[] JunkRx = new Regex[]
        {
            new Regex(@"^\s*(来源|出处|整理|录入|网址|網址|地址|章节范围|更新时间)\s*[:：]"),
            new Regex(@"^\s*https?://", RegexOptions.IgnoreCase),
            new Regex(@"^\s*www\.", RegexOptions.IgnoreCase),
            new Regex(@"记住.{0,12}(网|域名)"),
            new Regex(@"(全网)?最快.{0,12}(小说)?站"),
            new Regex(@"^\s*[—\-=_*·。\.\s]{5,}\s*$"),                       // 分割线
            new Regex(@"^\s*[【\[（(][^】\]）)]{0,60}[】\]）)]\s*$"),          // 整行只有一个【xx】
            new Regex(@"^\s*[（(][^）)]{0,8}(未完|待续|本章|上架)[^）)]{0,8}[）)]\s*$"),
            new Regex(@"^\s*(ps|PS)\s*[:：]\s*$"),                             // 孤零零一个 "PS："
            // 盗版站的域名广告。域名本身常被随机字符糊掉（101??????.??????），
            // 所以不认域名，改认它那几句固定搭配。
            // 注意绝不能只认「首发」：正文里就有「官家今日怒气，首发于骑军」。
            new Regex(@"(全手打|无错站|无乱序章节|无错章节)"),
            new Regex(@"^本书首发"),
            new Regex(@"^更多精彩小说"),
            new Regex(@"^请?记住.{0,20}(网站|域名|网)"),
            new Regex(@"^\s*[\d\?\*\.a-zA-Z①-⑩]{6,}\s*[,，]?\s*$"),          // 糊成一团的网址
        };

        // ---- 章节标题：「第 84 章 xxx」/「第八十四章 xxx」 ----
        private static readonly Regex TitleRx = new Regex(
            @"^\s*第\s*([0-9０-９〇零一二三四五六七八九十百千两]+)\s*[章回节]\s*(.*)$");

        // 句末标点：上一行以这些结尾，就算"说完了"，不与下一行合并
        private const string TerminalChars = "。！？…”』」）)：；—!?\"'…～~";

        /// <summary>单个汉字数字 → 数值。注意「〇」和「零」都是 0，必须显式映射</summary>
        private static int DigitOf(char c)
        {
            switch (c)
            {
                case '零': case '〇': return 0;
                case '一': return 1;
                case '二': case '两': return 2;
                case '三': return 3;
                case '四': return 4;
                case '五': return 5;
                case '六': return 6;
                case '七': return 7;
                case '八': return 8;
                case '九': return 9;
            }
            return -1;
        }

        // ------------------------------------------------------------------
        //  小工具
        // ------------------------------------------------------------------
        /// <summary>中文数字 / 阿拉伯数字 → 整数。解析不了返回 -1</summary>
        public static int Cn2Int(string s)
        {
            if (s == null) return -1;
            s = s.Trim();
            if (s.Length == 0) return -1;
            int v;
            string normalized = s.Replace('０', '0').Replace('１', '1').Replace('２', '2')
                .Replace('３', '3').Replace('４', '4').Replace('５', '5').Replace('６', '6')
                .Replace('７', '7').Replace('８', '8').Replace('９', '9');
            if (int.TryParse(normalized, out v)) return v;

            int total = 0, cur = 0;
            foreach (char ch in normalized)
            {
                int d = DigitOf(ch);
                if (d >= 0) { cur = d; continue; }
                int unit = ch == '十' ? 10 : (ch == '百' ? 100 : (ch == '千' ? 1000 : 0));
                if (unit > 0)
                {
                    total += (cur == 0 ? 1 : cur) * unit;
                    cur = 0;
                }
            }
            return total + cur;
        }

        /// <summary>这一行长什么样才算章节标题（顺便把章号和副标题拆出来）</summary>
        public static bool IsTitle(string s, out int num, out string sub)
        {
            num = -1; sub = "";
            if (s == null) return false;
            if (s.Length > 50) return false;                 // 标题不会太长
            Match m = TitleRx.Match(s);
            if (!m.Success) return false;
            num = Cn2Int(m.Groups[1].Value);
            if (num < 0) return false;
            sub = m.Groups[2].Value.Trim();
            if (sub.Length > 34) return false;                // 副标题太长说明这行是正文
            // 句号只可能藏在副标题的括号注解里（「召见（上）（感谢白银盟大佬『。』）」）。
            // 先把括号注解整段剥掉，再看剩下的部分有没有句号 —— 有的话说明这行其实是正文。
            if (StripParens(sub).IndexOf('。') >= 0) return false;
            return true;
        }

        public static bool IsTitle(string s) { int n; string b; return IsTitle(s, out n, out b); }

        /// <summary>把 () （） 【】 [] 括号里的内容整段剥掉（标题里的致谢、注解都在括号里）</summary>
        public static string StripParens(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            int depth = 0;
            foreach (char c in s)
            {
                if (c == '(' || c == '（' || c == '【' || c == '[') { depth++; continue; }
                if (c == ')' || c == '）' || c == '】' || c == ']') { if (depth > 0) depth--; continue; }
                if (depth == 0) sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>整行就是水印吗</summary>
        public static bool IsWatermark(string s)
        {
            if (s == null) return true;
            string t = s.Trim();
            if (t.Length == 0) return true;
            for (int i = 0; i < JunkExact.Length; i++)
                if (t == JunkExact[i]) return true;
            for (int i = 0; i < JunkRx.Length; i++)
                if (JunkRx[i].IsMatch(t)) return true;
            return false;
        }

        /// <summary>这一行算不算"话说完了"</summary>
        private static bool IsTerminal(string s)
        {
            if (s == null || s.Length == 0) return true;
            if (IsTitle(s)) return true;
            if (s.StartsWith("《") && s.Length < 40) return true;
            char c = s[s.Length - 1];
            return TerminalChars.IndexOf(c) >= 0;
        }

        /// <summary>这一行开头有没有缩进（半角空格 / 全角空格 / 制表符）</summary>
        private static bool HasIndent(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return false;
            char c = raw[0];
            return c == ' ' || c == '\u3000' || c == '\t';
        }

        private static string Hash(string s)
        {
            // FNV-1a 64 位：稳定、不依赖运行时随机种子
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 1099511628211UL;
            }
            return h.ToString("x16");
        }

        // ------------------------------------------------------------------
        //  段落记录：每一段都记住自己来自哪个 txt，洗完才能各自写回各自文件
        // ------------------------------------------------------------------
        private sealed class Para
        {
            public string Text;
            public int File;          // 来自第几个 txt
            public bool IsTitle;      // 这一条是不是章节标题
            public int TitleNum;      // 标题里的章号
            public string TitleSub = "";
            public bool Indented;     // 原文这一行开头有没有缩进（段首有、续行没有）
            public Para(string t, int f) { Text = t; File = f; }
        }

        private sealed class Chapter
        {
            public int File;          // 属于哪个 txt
            public int Num;           // 原始章号（解析出来的）
            public string Sub = "";
            public List<Para> Body = new List<Para>();
            public bool Dead;         // 重复章节，被丢掉
        }

        // ------------------------------------------------------------------
        //  主流程：把若干 txt 的行，各自洗成"每行一个完整段落"的行
        // ------------------------------------------------------------------
        /// <summary>
        /// 纯函数：输入每个 txt 的行列表，输出清洗后每个 txt 的行列表（顺序、个数一一对应）。
        /// 自检直接喂数据进来就能验证，不需要真的建文件。
        /// </summary>
        public static List<List<string>> CleanFiles(List<List<string>> srcByFile, CleanStat st)
        {
            // ---- 1) 去空行 + 去水印；同时就把「这是不是标题」记下来 ----
            //   关键：标题身份在**去重之前**确定。源文件里同一章的标题常常重复印两行
            //   （比如「第八十五章 流光（上）」紧跟着「第84章 流光（上）」），
            //   如果等去重之后再靠 IsTitle 重新认，混进去的注释、致谢会让它认不出来，
            //   那一章的边界就丢了，后面整段章节都会错位。
            List<Para> paras = new List<Para>();
            int IndentedLines = 0;
            for (int f = 0; f < srcByFile.Count; f++)
            {
                CleanFileStat fs = (f < st.Files.Count) ? st.Files[f] : null;
                foreach (string raw in srcByFile[f])
                {
                    string t = raw == null ? "" : raw.Trim();
                    if (t.Length == 0) continue;
                    if (fs != null) { fs.LinesIn++; fs.CharsIn += t.Length; }
                    if (IsWatermark(t)) { if (fs != null) fs.Watermarks++; continue; }

                    Para p = new Para(t, f);
                    p.Indented = HasIndent(raw);
                    if (p.Indented) IndentedLines++;
                    int num; string sub;
                    if (IsTitle(t, out num, out sub))
                    {
                        p.IsTitle = true;
                        p.TitleNum = num;
                        p.TitleSub = sub;
                    }
                    paras.Add(p);
                }
            }

            // ---- 2) 紧挨着一模一样的行（同一行印了两遍）直接删 ----
            List<Para> uniq = new List<Para>();
            foreach (Para p in paras)
            {
                if (uniq.Count > 0)
                {
                    Para prev = uniq[uniq.Count - 1];
                    if (prev.File == p.File && prev.Text == p.Text && p.Text.Length >= 6)
                    {
                        st.Repeats++;
                        continue;
                    }
                }
                uniq.Add(p);
            }

            // ---- 3) 接回被硬换行切断的句子 ----
            //   源文件的排版规律：一段的第一行带缩进，续行不带缩进。
            //   所以「上一段结尾没有句末标点 + 这一段不带缩进」= 它其实是上一段的续行。
            //   三条硬性约束：
            //     · 标题行绝不与前后合并（否则章号会被吞进正文里）
            //     · 上一行是标题时不合并（新章的第一句必须自己起头）
            //     · 不跨文件
            //   顺序上必须放在「去整块重复」**之前**：如果反着来，被删掉的重复块两侧
            //   的段落会被粘在一起，造出原文里根本不存在的句子。
            bool useIndent = IndentedLines * 3 > paras.Count;   // 缩进行够多才拿它当依据
            List<Para> joined = new List<Para>();
            foreach (Para p in uniq)
            {
                if (joined.Count > 0 && !p.IsTitle)
                {
                    Para prev = joined[joined.Count - 1];
                    bool continuation = !prev.IsTitle && prev.File == p.File &&
                                        !IsTerminal(prev.Text) && prev.Text.Length < 300 &&
                                        (!useIndent || !p.Indented);
                    if (continuation)
                    {
                        prev.Text = prev.Text + p.Text;
                        st.Joined++;
                        continue;
                    }
                }
                joined.Add(p);
            }

            // ---- 4) 去「整块重复」：连续 N 条与前面某处完全相同，把后面那份删掉 ----
            List<Para> dedup = DropDuplicateBlocks(joined, st);

            // ---- 5) 切章节：正文挂在它前面「最后一个」标题上 ----
            //   为什么取最后一个：源文件里每章常印两行标题，第二行的号才是与前后章衔接得上的那个。
            List<Chapter> chs = new List<Chapter>();
            Chapter cur = null;
            foreach (Para p in dedup)
            {
                if (p.IsTitle)
                {
                    cur = new Chapter();
                    cur.File = p.File;
                    cur.Num = p.TitleNum;
                    cur.Sub = p.TitleSub;
                    chs.Add(cur);
                }
                else
                {
                    if (cur == null)                  // 第一章之前的卷首（书名、作者）
                    {
                        cur = new Chapter();
                        cur.File = p.File;
                        cur.Num = 0;
                        chs.Add(cur);
                    }
                    cur.Body.Add(p);
                }
            }

            // ---- 6) 整章正文一字不差的两章只留第一份；只有标题没正文的丢掉 ----
            Dictionary<string, int> seen = new Dictionary<string, int>();
            foreach (Chapter c in chs)
            {
                if (c.Body.Count == 0)
                {
                    c.Dead = true;
                    if (c.Sub.Length > 0) st.EmptyChapters++;
                    continue;
                }
                StringBuilder fb = new StringBuilder(c.Body.Count * 18);
                for (int i = 0; i < c.Body.Count; i++) fb.Append(Hash(c.Body[i].Text));
                string fp = fb.ToString();
                if (seen.ContainsKey(fp)) { c.Dead = true; st.DupChapters++; continue; }
                seen[fp] = 1;
            }

            // ---- 7) 章号原样保留 ----
            //   源文件里每章通常印两遍：第一遍是中文数字标题，第二遍又跟一行阿拉伯数字标题。
            //   去重时这一章的正文只留第一份，但**标题取的是它正文前面最后一个标题**
            //   （也就是那个阿拉伯数字），所以留下来的正好是这本书真实的章号。
            //
            //   这里不再重排、也不做「不倒退修正」：
            //     · 重排成 1,2,3… 会让用户搜「305 章」直接搜不到；
            //     · 逢撞号就 +1 会连锁漂移，整本编号越跑越大（实测会从 292 涨到 493）。
            //   重复章节的正文已经被删掉了，所以同一个章号只会剩一条。

            // ---- 8) 输出：标题一行、正文每段一行；每个 txt 各写各的，不合并 ----
            List<List<string>> outByFile = new List<List<string>>();
            for (int f = 0; f < srcByFile.Count; f++) outByFile.Add(new List<string>());
            foreach (Chapter c in chs)
            {
                if (c.Dead) continue;
                List<string> outLines = outByFile[c.File < outByFile.Count ? c.File : 0];
                if (c.Num > 0)
                    outLines.Add(c.Sub.Length > 0 ? ("第" + c.Num + "章 " + c.Sub) : ("第" + c.Num + "章"));
                foreach (Para p in c.Body)
                {
                    outLines.Add(p.Text);
                    if (c.File < st.Files.Count) st.Files[c.File].CharsOut += p.Text.Length;
                }
                if (c.Num > 0 && c.File < st.Files.Count) st.Files[c.File].Chapters++;
            }
            return outByFile;
        }

        /// <summary>兼容旧签名：只有一个输入流时，当成"单文件"清洗</summary>
        public static List<string> CleanLines(List<string> srcLines, CleanStat st)
        {
            List<List<string>> one = new List<List<string>>();
            one.Add(srcLines);
            while (st.Files.Count < 1) st.Files.Add(new CleanFileStat());
            return CleanFiles(one, st)[0];
        }

        private static List<Para> DropDuplicateBlocks(List<Para> paras, CleanStat st)
        {
            const int WINDOW = 8;       // 连续 8 段相同就先怀疑
            const int MIN_BLOCK = 12;   // 确认至少 12 段
            const int MIN_CHARS = 400;  // 且至少 400 字，避免误删「「是。」」这种

            int n = paras.Count;
            string[] sig = new string[n];
            for (int i = 0; i < n; i++) sig[i] = Hash(paras[i].Text);

            Dictionary<string, int> seen = new Dictionary<string, int>();
            bool[] kill = new bool[n];
            int at = 0;
            while (at + WINDOW <= n)
            {
                StringBuilder kb = new StringBuilder(WINDOW * 17);
                for (int k = 0; k < WINDOW; k++) kb.Append(sig[at + k]);
                string key = kb.ToString();

                int prev;
                if (seen.TryGetValue(key, out prev) && prev < at && !AnyKilled(kill, prev, WINDOW))
                {
                    int len = 0;
                    while (at + len < n && paras[at + len].Text == paras[prev + len].Text) len++;
                    int chars = 0;
                    for (int k = at; k < at + len; k++) chars += paras[k].Text.Length;
                    if (len >= MIN_BLOCK && chars >= MIN_CHARS)
                    {
                        for (int k = at; k < at + len; k++) kill[k] = true;
                        st.DupBlocks++;
                        at += len;
                        continue;
                    }
                }
                if (!seen.ContainsKey(key)) seen[key] = at;
                at++;
            }

            List<Para> outList = new List<Para>(n);
            for (int i = 0; i < n; i++) if (!kill[i]) outList.Add(paras[i]);
            return outList;
        }

        private static bool AnyKilled(bool[] kill, int from, int count)
        {
            for (int i = from; i < from + count && i < kill.Length; i++) if (kill[i]) return true;
            return false;
        }

        // ------------------------------------------------------------------
        //  读写
        // ------------------------------------------------------------------
        public static string ReadAllText(string path, out string encName)
        {
            byte[] raw = File.ReadAllBytes(path);
            Encoding e = Book.DetectEncoding(raw, out encName);
            return e.GetString(raw);
        }

        public static List<string> ToLines(string text)
        {
            return new List<string>(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
        }

        /// <summary>
        /// 清洗一本书的整个文件夹（一个文件夹 = 一本书，里面可以有多个 txt）。
        /// 一个 txt 洗成一个同名 txt，放进 已清洗\&lt;书名&gt;\，不再合并成一本。
        /// </summary>
        public static CleanStat CleanBookFolder(string folder, string outRoot)
        {
            CleanStat st = new CleanStat();
            st.Book = Path.GetFileName(folder.TrimEnd('\\', '/'));
            try
            {
                string[] files = Directory.GetFiles(folder, "*.txt");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                if (files.Length == 0) { st.Error = "这个文件夹里没有 txt"; return st; }

                List<List<string>> srcByFile = new List<List<string>>();
                foreach (string f in files)
                {
                    CleanFileStat fs = new CleanFileStat();
                    fs.Name = Path.GetFileName(f);
                    st.Files.Add(fs);
                    string enc;
                    string text;
                    try { text = ReadAllText(f, out enc); }
                    catch (Exception ex) { fs.EncName = "读不了：" + ex.Message; srcByFile.Add(new List<string>()); continue; }
                    fs.EncName = enc;
                    srcByFile.Add(ToLines(text));
                }

                List<List<string>> outByFile = CleanFiles(srcByFile, st);

                string dir = Path.Combine(outRoot, st.Book);
                Directory.CreateDirectory(dir);
                st.OutDir = dir;
                for (int i = 0; i < outByFile.Count; i++)
                {
                    if (i >= st.Files.Count) break;
                    if (st.Files[i].EncName.StartsWith("读不了")) continue;
                    string target = Path.Combine(dir, st.Files[i].Name);
                    File.WriteAllText(target, string.Join("\r\n", outByFile[i].ToArray()) + "\r\n",
                        new UTF8Encoding(false));
                }
            }
            catch (Exception ex) { st.Error = ex.Message; }
            return st;
        }

        // ------------------------------------------------------------------
        //  书库目录
        // ------------------------------------------------------------------
        /// <summary>书库根目录：exe 旁边的「书库」</summary>
        public static string LibRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string p = Path.Combine(dir, RootName);
            if (!Directory.Exists(p)) Directory.CreateDirectory(p);
            return p;
        }

        public static string RawRoot(string libRoot) { return Path.Combine(libRoot, RawName); }
        public static string OutRoot(string libRoot) { return Path.Combine(libRoot, OutName); }

        /// <summary>扫整个书库：原始 下面的每个子文件夹 = 一本书</summary>
        public static List<CleanStat> RunLibrary(string libRoot, Action<string> log)
        {
            if (string.IsNullOrEmpty(libRoot)) libRoot = LibRoot();
            string rawRoot = RawRoot(libRoot);
            string outRoot = OutRoot(libRoot);
            Directory.CreateDirectory(rawRoot);
            Directory.CreateDirectory(outRoot);

            List<CleanStat> list = new List<CleanStat>();
            string[] dirs = Directory.GetDirectories(rawRoot);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            if (dirs.Length == 0)
            {
                if (log != null) log("「" + rawRoot + "」下面还没有书。把 txt 按「一本书一个文件夹」放进去就行。");
                return list;
            }
            foreach (string d in dirs)
            {
                if (log != null) log("清洗《" + Path.GetFileName(d) + "》…");
                CleanStat st = CleanBookFolder(d, outRoot);
                list.Add(st);
                if (log != null)
                {
                    if (st.Error.Length > 0) log("  失败：" + st.Error);
                    else log("  完成：" + st.TotalChapters + " 章，" + st.TotalOut + " 字（砍掉 " + st.SavedPercent + "%）");
                }
            }
            return list;
        }

        /// <summary>把一批统计渲染成一段给人看的报告</summary>
        public static string FormatReport(List<CleanStat> list, string libRoot)
        {
            StringBuilder sb = new StringBuilder();
            int ok = 0, bad = 0, chIn = 0, chOut = 0, ch = 0;
            foreach (CleanStat s in list)
            {
                if (s.Error.Length > 0) bad++; else ok++;
                chIn += s.TotalIn; chOut += s.TotalOut; ch += s.TotalChapters;
            }
            sb.AppendLine("清洗完成：" + ok + " 本成功" + (bad > 0 ? "，" + bad + " 本失败" : ""));
            sb.AppendLine("合计 " + ch + " 章，" + chIn + " 字 → " + chOut + " 字"
                + (chIn > 0 ? "（砍掉 " + (int)Math.Round((chIn - chOut) * 100.0 / chIn) + "% 的重复和水印）" : ""));
            sb.AppendLine("每个 txt 洗成同名的 txt，结构不变、没有合并。");
            sb.AppendLine();
            foreach (CleanStat s in list)
            {
                sb.AppendLine("■ " + s.Book);
                if (s.Error.Length > 0) { sb.AppendLine("   失败：" + s.Error); sb.AppendLine(); continue; }
                foreach (CleanFileStat f in s.Files)
                {
                    if (f.EncName.StartsWith("读不了")) { sb.AppendLine("   " + f.Name + "  " + f.EncName); continue; }
                    sb.AppendLine("   " + f.Name + " · " + f.EncName + " · " + f.LinesIn + " 行");
                    sb.AppendLine("       水印 " + f.Watermarks + " 行 · " + f.Chapters + " 章 · "
                        + f.CharsIn + " 字 → " + f.CharsOut + " 字");
                }
                sb.AppendLine("   合计：接回断句 " + s.Joined + " 处 · 重复行 " + s.Repeats + " 条 · 整块重复 " + s.DupBlocks
                    + " 处 · 重复章节 " + s.DupChapters + " 章 · 空标题 " + s.EmptyChapters + " 个");
                sb.AppendLine("   → " + s.OutDir);
                sb.AppendLine();
            }
            sb.AppendLine("原始：" + RawRoot(libRoot));
            sb.AppendLine("已清洗：" + OutRoot(libRoot));
            return sb.ToString();
        }
    }

    // ========================================================================
    //  L8c  清洗报告窗体：双击 TxtClean.exe 弹这个
    // ========================================================================
    public sealed class CleanReportForm : Form
    {
        public CleanReportForm(string body, string outDir)
        {
            Text = "TxtBar 清洗报告";
            ClientSize = new Size(Shell.S(760), Shell.S(560));
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 13f, GraphicsUnit.Pixel);
            BackColor = Skin.Bg;
            ForeColor = Skin.Fg;

            TextBox box = new TextBox();
            box.Multiline = true;
            box.ReadOnly = true;
            box.ScrollBars = ScrollBars.Both;
            box.WordWrap = false;
            box.Dock = DockStyle.Fill;
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Skin.Bg;
            box.ForeColor = Skin.Fg;
            try { box.Font = new Font("Consolas", 13f, GraphicsUnit.Pixel); } catch { }
            box.Text = body;

            Panel bar = new Panel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Shell.S(50);

            Button open = new Button();
            open.Text = "打开「已清洗」文件夹";
            open.Dock = DockStyle.Left;
            open.Width = Shell.S(210);
            open.Click += delegate
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + outDir + "\""); }
                catch { }
            };

            Button close = new Button();
            close.Text = "关掉";
            close.Dock = DockStyle.Right;
            close.Width = Shell.S(110);
            close.Click += delegate { Close(); };

            bar.Controls.Add(close);
            bar.Controls.Add(open);
            Controls.Add(box);
            Controls.Add(bar);
            AcceptButton = close;
        }
    }

    // ========================================================================
    //  L8d  独立入口：TxtClean.exe
    // ========================================================================
    public static class CleanEntry
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int pid);

        public static void Main(string[] args)
        {
            string libRoot = null;
            bool quiet = false;
            foreach (string a in args)
            {
                if (string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase)) quiet = true;
                else if (!a.StartsWith("--")) libRoot = a;
            }

            // winexe 自己不带控制台：要往命令行/管道里写报告，得挂到父进程的控制台上
            if (quiet) { try { AttachConsole(-1); } catch { } }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Shell.Detect();
                Skin.Init(Shell.LightTheme);
            }
            catch { }

            List<CleanStat> list = Cleaner.RunLibrary(libRoot, null);
            string root = string.IsNullOrEmpty(libRoot) ? Cleaner.LibRoot() : libRoot;
            string body = Cleaner.FormatReport(list, root);

            if (quiet)
            {
                try
                {
                    Console.OutputEncoding = Encoding.UTF8;
                    Console.WriteLine(body);
                }
                catch { }
                foreach (CleanStat s in list) if (s.Error.Length > 0) Environment.ExitCode = 1;
                return;
            }

            Application.Run(new CleanReportForm(body, Cleaner.OutRoot(root)));
        }
    }
}
