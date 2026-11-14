// ============================================================
// TextFileInfo.cs —— 一次读取的结果（正文 + 它的书写习惯）
// ============================================================
//
// 从 TextFile.cs 里拆出来，是因为它会被好几个模块用到：
// 读资料（只看 Text / 字数）、读译文（还要 Eol / HasBom / Sha 才能安全回写）。
// 放在单独文件里，谁需要谁引用，不必知道别的东西。
// ============================================================

namespace YiTai.Core;

public sealed class TextFileInfo
{
    public bool Exists { get; set; }

    /// <summary>正文，换行统一成 \n。</summary>
    public string Text { get; set; } = "";

    /// <summary>源文件的换行（"\r\n" 或 "\n"），回写时照它。</summary>
    public string Eol { get; set; } = "\n";

    public bool HasBom { get; set; }

    public bool EndsWithNewline { get; set; } = true;

    /// <summary>内容指纹（sha256 前 16 位），用来发现"被别人改过"。</summary>
    public string Sha { get; set; } = "";
}
