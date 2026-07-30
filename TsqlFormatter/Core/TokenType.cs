namespace TsqlFormatter.Core
{

public enum TokenType
{
    Keyword,
    DeclareKeyword,
    Identifier,
    QuotedIdentifier,
    StringLiteral,
    NumberLiteral,
    Variable,
    Comma,
    Dot,
    Semicolon,
    LeftParen,
    RightParen,
    Equals,
    NotEquals,
    LessThan,
    GreaterThan,
    LessOrEqual,
    GreaterOrEqual,
    Plus,
    Minus,
    Multiply,
    Divide,
    Percent,
    CompoundAssign,   // += -= *= /= %= &= |= ^=
    LineComment,
    BlockComment,
    Whitespace,
    Newline,
    EndOfFile,
    Unknown
}

}
