using System.Text;

namespace PMCL.Services;

/// <summary>
/// Splits user-entered JVM/game arguments into individual process arguments.
/// Supports single and double quotes plus backslash-escaped quotes.
/// </summary>
public static class CommandLineSplitter
{
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return Array.Empty<string>();

        var arguments = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var hasContent = false;

        for (var index = 0; index < commandLine.Length; index++)
        {
            var character = commandLine[index];

            if (character == '\\'
                && index + 1 < commandLine.Length
                && commandLine[index + 1] is '"' or '\\')
            {
                current.Append(commandLine[++index]);
                hasContent = true;
                continue;
            }

            if (quote == '\0' && character is '"' or '\'')
            {
                quote = character;
                hasContent = true;
                continue;
            }

            if (quote != '\0' && character == quote)
            {
                quote = '\0';
                continue;
            }

            if (char.IsWhiteSpace(character) && quote == '\0')
            {
                if (hasContent)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    hasContent = false;
                }

                continue;
            }

            current.Append(character);
            hasContent = true;
        }

        if (hasContent)
            arguments.Add(current.ToString());

        return arguments;
    }
}
