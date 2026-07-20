using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

public sealed class GoSeparatorRule : IFormatterRule
{
    public bool CanHandle(AstNode node) => node is GoSeparatorNode;

    public string Format(AstNode node, FormatterEngine engine, int indent) => "GO";
}

}
