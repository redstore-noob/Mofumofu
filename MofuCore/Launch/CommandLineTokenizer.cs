using System.Text;

namespace MofuCore.Launch;

// UI、包装命令、旧版 minecraftArguments 必须共用同一套规则，否则「文本框显示」与真正传给进程的 argv 无法原样互转。
// 引号（单/双）内的空白不切分，反斜杠可转义引号与反斜杠，引号外的空白分隔参数。
public static class CommandLineTokenizer
{
    /// <summary>保留空参数（例如 <c>""</c>）。</summary>
    public static List<string> Split(string? commandLine)
    {
        if (!TrySplit(commandLine, out List<string> tokens, out string? error))
        {
            throw new FormatException(error);
        }

        return tokens;
    }

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

    /// <summary>结果必须能被 <see cref="Split"/> 原样还原。</summary>
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

    /// <summary>结果必须能被 <see cref="Split"/> 原样还原。</summary>
    public static string Join(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return string.Join(' ', arguments.Select(Quote));
    }
}
