using System.Text;

namespace MofuCore.Launch;

/// <summary>
/// 命令行文本与参数列表之间的转换。启动器 UI、包装命令、旧版 minecraftArguments 共用同一套规则：
/// 引号（单/双）内的空白不切分，反斜杠可转义引号与反斜杠，引号外的空白分隔参数。
/// </summary>
/// <remarks>
/// 以前每个入口各写一套分词：UI 直接按空格 Split（带空格的 -D 参数会被拆成裸 token，
/// 进而被 Java 当成主类），包装命令只认双引号，旧版参数只认 \"。
/// 统一到这里后「文本框怎么显示」和「真正传给进程的 argv」始终能原样互转。
/// </remarks>
public static class CommandLineTokenizer
{
    /// <summary>把一段命令行文本拆成参数列表（保留空参数，例如 <c>""</c>）。</summary>
    /// <exception cref="FormatException">引号未闭合。</exception>
    public static List<string> Split(string? commandLine)
    {
        if (!TrySplit(commandLine, out List<string> tokens, out string? error))
        {
            throw new FormatException(error);
        }

        return tokens;
    }

    /// <summary>拆分命令行文本；引号未闭合时返回 false 并给出可展示的原因。</summary>
    public static bool TrySplit(string? commandLine, out List<string> tokens, out string? error)
    {
        tokens = [];
        error = null;
        if (string.IsNullOrEmpty(commandLine))
        {
            return true;
        }

        StringBuilder current = new();
        bool hasToken = false;
        char quote = '\0';
        for (int index = 0; index < commandLine.Length; index++)
        {
            char character = commandLine[index];

            if (character == '\\' && index + 1 < commandLine.Length)
            {
                char next = commandLine[index + 1];
                if (next is '"' or '\'' or '\\')
                {
                    current.Append(next);
                    hasToken = true;
                    index++;
                    continue;
                }
            }

            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(character);
                }

                // 空引号 "" 也是一个真实参数
                hasToken = true;
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                hasToken = true;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(character);
            hasToken = true;
        }

        if (quote != '\0')
        {
            error = $"命令行末尾有未闭合的 {quote} 引号。";
            return false;
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return true;
    }

    /// <summary>把单个参数格式化成 <see cref="Split"/> 能原样还原的文本片段。</summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character is '"' or '\''))
        {
            return argument;
        }

        StringBuilder builder = new("\"");
        foreach (char character in argument)
        {
            if (character is '"' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>把参数列表拼成一行可读、可再次 <see cref="Split"/> 的命令行文本。</summary>
    public static string Join(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return string.Join(' ', arguments.Select(Quote));
    }
}
