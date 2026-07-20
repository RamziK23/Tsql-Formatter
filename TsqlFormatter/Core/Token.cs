using System;

namespace TsqlFormatter.Core
{

public sealed class Token
{
    public TokenType Type { get; }
    public string Value { get; }
    public int Line { get; }
    public int Column { get; }
    public string? TrailingComment { get; set; }

    public Token(TokenType type, string value, int line = 0, int column = 0)
    {
        Type = type;
        Value = value;
        Line = line;
        Column = column;
    }

    public bool IsKeyword(string keyword) =>
        Type == TokenType.Keyword && Value.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"[{Type}] {Value}";
}

}
