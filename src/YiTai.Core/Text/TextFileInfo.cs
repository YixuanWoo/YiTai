// ============================================================
// TextFileInfo.cs —— 一次读取的结果（正文 + 它的书写习惯）
// ============================================================
//
// 从 TextFile.cs 里拆出来，是因为它会被好几个模块用到：
// 读资料（只看 Text / 字数）、读译文（还要 Eol / HasBom / Sha 才能安全回写）。
// 放在单独文件里，谁需要谁引用，不必知道别的东西。
//
// ★ 为什么要有 State：文件读不了的原因不止"不存在"一种 ——
//   被别的程序独占、没有权限、路径是个文件夹、磁盘出错，都会抛异常。
//   把原因收进数据里（而不是抛出去），界面才能显示"人话"而不是崩掉。
// ============================================================

namespace YiTai.Core;

/// <summary>一次读取的结局。除 Ok 以外，Text 一律为空串，Problem 里是一句人话。</summary>
public enum TextReadState
{
    /// <summary>读到了。</summary>
    Ok = 0,

    /// <summary>文件不在了（被移走、改名、删除）。</summary>
    Missing = 1,

    /// <summary>文件在，但没有读取权限。</summary>
    NoAccess = 2,

    /// <summary>文件在，但被别的程序独占（OneDrive 同步、另一个编辑器正在保存……）。</summary>
    Locked = 3,

    /// <summary>路径本身不成立（超长、含非法字符、指向一个目录……）。</summary>
    InvalidPath = 4,

    /// <summary>其它读取失败，具体原因在 Problem 里。</summary>
    Failed = 5,
}

public sealed class TextFileInfo
{
    public bool Exists { get; set; }

    /// <summary>这次读取的结局。Ok 以外的值表示 Text 不可用。</summary>
    public TextReadState State { get; set; } = TextReadState.Ok;

    /// <summary>读不了的原因（一句人话）。空 = 没有问题。</summary>
    public string Problem { get; set; } = "";

    /// <summary>正文，换行统一成 \n。</summary>
    public string Text { get; set; } = "";

    /// <summary>源文件的换行（"\r\n" 或 "\n"），回写时照它。</summary>
    public string Eol { get; set; } = "\n";

    public bool HasBom { get; set; }

    public bool EndsWithNewline { get; set; } = true;

    /// <summary>内容指纹（sha256 前 16 位），用来发现"被别人改过"。</summary>
    public string Sha { get; set; } = "";
}
