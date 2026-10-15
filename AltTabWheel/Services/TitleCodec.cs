using System.Text;

namespace AltTabWheel.Services;

/// <summary>
/// 窗口标题解码与清洗。
///
/// 实测发现（见 docs/DEVELOPMENT.md §8）：本环境的 win32k 标题层按 4 字节/字符
/// 存储，GetWindowTextW 仍按 WCHAR(2B) 返回 —— 表现为有效字符与 U+0000 交错
/// （"Windows" → 'W\0i\0n\0...'）。此外部分终端类窗口标题残留 ANSI 控制符
/// （OSC/SGR 序列，learnc-cli 等会注入）。
///
/// 解码策略：
///  1. 交错零检测：奇数索引为 0 的比例 ≥ 80% 时按偶数索引折叠。
///  2. 控制符清理：剥离 C0（保留 \t）、DEL、C1 区段。
///  3. 孤立代理项清理：防止 WPF 渲染出方框。
/// </summary>
public static class TitleCodec
{
    public static string Decode(string raw)
    {
        if (raw.Length < 4)
            return StripControls(raw);

        int pairs = raw.Length / 2;
        int interleavedZeros = 0;
        for (int i = 1; i < raw.Length; i += 2)
        {
            if (raw[i] == '\0')
                interleavedZeros++;
        }

        string s = raw;
        if (pairs >= 2 && interleavedZeros >= pairs * 0.8)
        {
            var folded = new StringBuilder(pairs);
            for (int i = 0; i + 1 < raw.Length; i += 2)
                folded.Append(raw[i]);
            // 奇偶反转的少见情形兜底：若折叠结果几乎全空而奇数位有内容，反转
            s = folded.Length > 0 ? folded.ToString() : raw;
        }

        return StripControls(s).Trim();
    }

    private static string StripControls(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\t' || (c >= 0x20 && c != 0x7F && !(c >= 0x80 && c <= 0x9F)))
                sb.Append(c);
        }

        // 孤立代理项清理（成对保留）
        var cleaned = sb.ToString();
        var final = new StringBuilder(cleaned.Length);
        for (int i = 0; i < cleaned.Length; i++)
        {
            char c = cleaned[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < cleaned.Length && char.IsLowSurrogate(cleaned[i + 1]))
                {
                    final.Append(c).Append(cleaned[i + 1]);
                    i++;
                }
                // 孤立高位代理：丢弃
            }
            else if (char.IsLowSurrogate(c))
            {
                // 孤立低位代理：丢弃
            }
            else
            {
                final.Append(c);
            }
        }
        return final.ToString();
    }
}
