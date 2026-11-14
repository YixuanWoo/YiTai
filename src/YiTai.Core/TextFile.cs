// ============================================================
// TextFile.cs —— 文本文件的安全读写（编码、换行、原子替换、每日备份）
// ============================================================
//
// 为什么不能直接 File.WriteAllText：
//
// 【换行】
//   实测：`04-译稿\第01章.md` 是 CRLF，`第02章.md` 是 LF。
//   写回时若统一成一种，整章会被编辑器/git 认为"每一行都改过"，
//   以后做 diff 就再也看不清真实改动了。
//   → 读到什么换行，就原样还回去。
//
// 【BOM】
//   你现有译稿是无 BOM 的 UTF-8。带 BOM 会让某些解析器把首行读成 \uFEFF第三章。
//   → 沿用源文件状态。
//
// 【原子性】
//   直接覆盖写到一半断电 = 半个文件。先写同目录的 .tmp，再整体替换。
//
// 【备份】
//   每章每天第一次落盘前，把上一版复制到备份目录。不是每次保存都备
//   （那样一天会攒出几十个文件）。
// ============================================================

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace YiTai.Core;

public static class TextFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // ---------------------------------------------------------------- 读

    /// <summary>
    /// 读一份文本，并记下它的编码/换行习惯，供回写时照原样还原。
    ///
    /// ★ 这个方法**永远不抛异常**：文件不在、没权限、被独占、路径不成立，
    ///   一律把原因写进 <see cref="TextFileInfo.State"/> / <see cref="TextFileInfo.Problem"/> 返回。
    ///   界面靠这份"人话"提示用户，而不是让一次 IO 失败把整个窗口带下去。
    /// </summary>
    public static TextFileInfo Read(string path)
    {
        var info = new TextFileInfo();

        if (string.IsNullOrWhiteSpace(path))
        {
            info.State = TextReadState.InvalidPath;
            info.Problem = "这一栏没有指向任何文件。";
            return info;
        }

        try
        {
            if (!File.Exists(path))
            {
                info.Exists = false;
                info.State = TextReadState.Missing;
                info.Problem = "文件不在了（可能被移走、改名或删除）。";
                return info;
            }

            if (Directory.Exists(path))
            {
                info.Exists = true;
                info.State = TextReadState.InvalidPath;
                info.Problem = "这个路径是一个文件夹，不是文件。";
                return info;
            }

            byte[] bytes = File.ReadAllBytes(path);
            info.Exists = true;

            // BOM：EF BB BF
            bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            info.HasBom = hasBom;

            string text = hasBom
                ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
                : Encoding.UTF8.GetString(bytes);

            // 换行风格：以 CRLF 出现次数为准（混排文件按多数派走）
            int crlf = 0;
            int lf = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    if (i > 0 && text[i - 1] == '\r') crlf++;
                    else lf++;
                }
            }
            info.Eol = crlf > lf ? "\r\n" : "\n";

            info.EndsWithNewline = text.EndsWith("\n", StringComparison.Ordinal);

            // 内部一律用 \n，省得每个下游都要考虑两种换行
            info.Text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            info.Sha = Sha16(info.Text);
            info.State = TextReadState.Ok;
            return info;
        }
        catch (UnauthorizedAccessException)
        {
            info.Exists = true;
            info.State = TextReadState.NoAccess;
            info.Problem = "文件在，但没有读取权限。";
            return info;
        }
        catch (FileNotFoundException)
        {
            info.Exists = false;
            info.State = TextReadState.Missing;
            info.Problem = "文件不在了（可能被移走、改名或删除）。";
            return info;
        }
        catch (DirectoryNotFoundException)
        {
            info.State = TextReadState.InvalidPath;
            info.Problem = "这个文件所在的文件夹不存在了。";
            return info;
        }
        catch (PathTooLongException)
        {
            info.State = TextReadState.InvalidPath;
            info.Problem = "路径太长，系统读不了。";
            return info;
        }
        catch (IOException)
        {
            info.Exists = true;
            info.State = TextReadState.Locked;
            info.Problem = "文件被别的程序占着（同步盘、另一个编辑器正在保存……），暂时读不了。";
            return info;
        }
        catch (ArgumentException)
        {
            info.State = TextReadState.InvalidPath;
            info.Problem = "路径里有系统不认的字符。";
            return info;
        }
        catch (Exception ex)
        {
            info.Exists = File.Exists(path);
            info.State = TextReadState.Failed;
            info.Problem = "读不了：" + ex.Message;
            return info;
        }
    }

    // ---------------------------------------------------------------- 写

    /// <summary>
    /// 原子写。沿用 <paramref name="eol"/> 与 <paramref name="withBom"/>，
    /// 先在同一个目录写临时文件再整体替换（同一个卷上替换是原子的）。
    /// </summary>
    public static void WriteAtomic(string path, string textLf, string eol, bool withBom, bool endsWithNewline = true)
    {
        string body = textLf.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!endsWithNewline && body.EndsWith("\n", StringComparison.Ordinal))
            body = body.Substring(0, body.Length - 1);

        if (eol != "\n") body = body.Replace("\n", eol);

        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) throw new InvalidOperationException("目标路径没有目录：" + path);
        Directory.CreateDirectory(dir);

        string tmp = Path.Combine(dir, "." + Path.GetFileName(path) + ".yitai.tmp");

        try
        {
            byte[] bytes = withBom
                ? Encoding.UTF8.GetBytes("\uFEFF" + body)   // 极少见，但保留能力
                : Utf8NoBom.GetBytes(body);

            // 目标被 MarkText 之类占着时，替换会失败 —— 重试几次再放弃
            Retry(() =>
            {
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, path, overwrite: true);
            });
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理失败不影响主流程 */ }
        }
    }

    private static void Retry(Action action)
    {
        const int attempts = 5;
        for (int i = 0; i < attempts; i++)
        {
            try { action(); return; }
            catch (IOException) when (i < attempts - 1) { Thread.Sleep(120); }
            catch (UnauthorizedAccessException) when (i < attempts - 1) { Thread.Sleep(120); }
        }
    }

    // ---------------------------------------------------------------- 备份

    /// <summary>
    /// 每天首次保存前备份一份上一版，命名 `源文件名-工作台-yyyyMMdd.md`。
    /// 已经有今天这份就不再重复备份。
    /// </summary>
    public static string? BackupOncePerDay(string path, string backupDir, DateTime now)
    {
        if (!File.Exists(path)) return null;

        Directory.CreateDirectory(backupDir);

        string stem = Path.GetFileNameWithoutExtension(path);
        string stamp = now.ToString("yyyyMMdd");
        string dest = Path.Combine(backupDir, $"{stem}-工作台-{stamp}.md");

        if (File.Exists(dest)) return null;

        try
        {
            File.Copy(path, dest, overwrite: false);
            return dest;
        }
        catch (IOException)
        {
            return null;   // 备份失败不该拦住保存
        }
    }

    // ---------------------------------------------------------------- 杂项

    /// <summary>内容指纹，取 sha256 前 16 位。够区分"是不是同一份"。</summary>
    public static string Sha16(string text)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Utf8NoBom.GetBytes(text));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }
}
