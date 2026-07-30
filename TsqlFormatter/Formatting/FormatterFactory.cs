using System.Collections.Generic;
using TsqlFormatter.Rules;

namespace TsqlFormatter.Formatting
{

public static class FormatterFactory
{
    public static FormatterEngine Create()
    {
        var rules = new List<IFormatterRule>
        {
            new DeclareRule(),
            new SelectRule(),
            new SetOperationRule(),
            new InsertRule(),
            new UpdateRule(),
            new DeleteRule(),
            new BeginEndRule(),
            new ProgrammableObjectRule(),
            new ControlFlowRule(),
            new CreateTableRule(),
            new FragmentRule(),
            new GoSeparatorRule(),
            new RawTokensRule(),
        };
        return new FormatterEngine(rules);
    }
}

}
