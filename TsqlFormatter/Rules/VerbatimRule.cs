using System.Linq;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

/// <summary>
/// Emits a VerbatimNode exactly as written — concatenates the original token text
/// (whitespace and comments included), applying no formatting. Used for programmable
/// objects (functions/procedures/triggers) whose procedural bodies must not be altered.
/// </summary>
public sealed class VerbatimRule : IFormatterRule
{
    public bool CanHandle(AstNode node) => node is VerbatimNode;

    public string Format(AstNode node, FormatterEngine engine, int indent) =>
        string.Concat(((VerbatimNode)node).Tokens.Select(t => t.Value));
}

}
