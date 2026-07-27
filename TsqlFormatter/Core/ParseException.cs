using System;

namespace TsqlFormatter.Core
{

/// <summary>
/// Thrown when the parser hits a token it cannot reconcile with the grammar. Callers
/// (FormatterEngine) catch it and fall back to returning the source unchanged — a
/// "couldn't parse reliably, so don't touch it" graceful degradation, rather than
/// emitting desynchronized/broken SQL.
/// </summary>
public sealed class ParseException : Exception
{
    public ParseException(TokenType expectedType, string? expectedValue, Token got)
        : base(Describe(expectedType, expectedValue, got))
    {
    }

    private static string Describe(TokenType expectedType, string? expectedValue, Token got)
    {
        var exp = expectedValue != null ? $"{expectedType} '{expectedValue}'" : expectedType.ToString();
        return $"Parse error at line {got.Line}, col {got.Column}: expected {exp}, got [{got.Type}] '{got.Value}'.";
    }
}

}
