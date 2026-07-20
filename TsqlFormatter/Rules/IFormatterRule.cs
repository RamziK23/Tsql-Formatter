using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

namespace TsqlFormatter.Rules
{

public interface IFormatterRule
{
    bool CanHandle(AstNode node);
    string Format(AstNode node, FormatterEngine engine, int indent);
}

}
