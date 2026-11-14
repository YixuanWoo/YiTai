// ============================================================
// RefDocSpec.cs —— 对照栏里的"这一栏放什么"
// ============================================================
//
// 同屏对照要放的东西只有四种可能：
//   跟随当前打开的那份资料文件 / 项目里某个源文件 / 项目里某份译文 / 项目外的随意一份文本。
//
// 它**不写进 project.json**。project.json 说的是"这本书怎么翻"；
// 栏怎么摆、摆几栏，是"这个软件上次什么样"，属于 settings.json。
//
// Path 的写法随 Kind 变：
//   FollowSource 留空
//   Source       源文件名（相对 sources\）
//   Translation  相对项目根的路径（项目外另存的译文允许记绝对路径）
//   External     绝对路径
// ============================================================

namespace YiTai.Core;

public enum RefKind
{
    /// <summary>跟着译文栏当前那一章走，切章自动换。</summary>
    FollowSource = 0,

    /// <summary>项目 sources\ 里的某一份。</summary>
    Source = 1,

    /// <summary>项目里某一份译文（可以不是当前章的，用来看旧稿 / 另一种语言的稿）。</summary>
    Translation = 2,

    /// <summary>项目外的任意文本，只读，不进项目。</summary>
    External = 3,
}

public sealed class RefDocSpec
{
    public RefKind Kind { get; set; } = RefKind.FollowSource;

    public string Path { get; set; } = "";

    /// <summary>给人看的备注，程序不解释。</summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// 这一栏的显示比例，1 = 100%。只影响这一栏怎么画，不碰文件本身。
    /// 放在这里是为了跟栏位一起被记住：同一份资料，你上次放大到 150% 看，下次还是 150%。
    /// </summary>
    public double Zoom { get; set; } = 1.0;
}
