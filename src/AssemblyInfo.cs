// ============================================================================
//  版本信息（编进 exe 的资源里）
//  为什么要有：安全软件看到「无公司 / 无版本 / 无描述」的陌生 exe 会格外警惕，
//  这类「三无」文件是误报的高发区。把这些信息补齐，能明显降低被直接删掉的概率。
//  只在编译时读这些属性，运行时不占任何东西。
// ============================================================================
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("TxtBar 逐行阅读器")]
[assembly: AssemblyDescription("把 Windows 任务栏的位置变成逐行阅读器：一行固定字数、接着上次读、支持查找")]
[assembly: AssemblyProduct("TxtBar")]
[assembly: AssemblyCompany("TxtBar")]
[assembly: AssemblyCopyright("TxtBar")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

[assembly: ComVisible(false)]
[assembly: Guid("8f3a2c41-6d17-4b8e-9a55-1c0d7e6b2f30")]
