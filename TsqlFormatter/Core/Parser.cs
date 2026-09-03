using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TsqlFormatter.Core
{

public sealed class Parser
{
    private readonly List<Token> _tokens;
    private int _pos;
    // Comments skipped by Expect() while re-synchronizing; drained onto the next node
    // (e.g. a subquery SELECT) so they are never silently lost.
    private readonly List<string> _pendingComments = new();

    private static readonly HashSet<TokenType> Skippable = new()
    {
        TokenType.Whitespace, TokenType.Newline
    };

    /// <param name="hoistComments">
    /// Safe mode: never attach a comment to the end of a line — park every comment for hoisting
    /// above the statement instead. Used as a retry when the normal layout would have lost a
    /// comment or hidden code behind one; the placement is coarser, but the script still formats.
    /// </param>
    public Parser(List<Token> tokens, bool hoistComments = false)
    { _tokens = tokens; _hoistComments = hoistComments; }

    private readonly bool _hoistComments;

    // ═══════════════════════════════════════════════════════════════════════════
    //  Script
    // ═══════════════════════════════════════════════════════════════════════════

    /// <param name="recoverPartialStatement">
    /// When true (the default) a statement that runs off the end of the input keeps the longest
    /// prefix of itself that still parses; the caller can re-parse with false to leave such a
    /// statement whole and verbatim instead.
    /// </param>
    public ScriptNode Parse(bool recoverPartialStatement = true)
    {
        var script = new ScriptNode();
        bool isFirstStatement = true;

        while (!IsAtEnd())
        {
            bool hadSemicolon = false;
            int newlinesBefore = 0;
            // Consume leading separators. Each GO keyword emits its OWN separator node,
            // so consecutive GO batch separators are preserved rather than collapsed to one.
            while (!IsAtEnd())
            {
                var raw = PeekRaw();
                if (raw.Type == TokenType.Semicolon)
                {
                    hadSemicolon = true;
                    AdvanceRaw();
                    // A ';' the author put on a line of its own is a statement of its own and
                    // keeps that line. (The ';' that opens ";with …" is handled by hadSemicolon,
                    // and one that ends a statement was taken by that statement already.)
                    if (newlinesBefore > 0 && script.Statements.Count > 0
                        && !PeekPastComments().IsKeyword("WITH"))
                        script.Statements.Add(new SemicolonNode { BlankLineBefore = newlinesBefore >= 2 });
                    continue;
                }
                if (raw.Type == TokenType.Newline)
                    { newlinesBefore++; AdvanceRaw(); continue; }
                if (raw.Type == TokenType.Whitespace)
                    { AdvanceRaw(); continue; }
                if (IsGoKeyword())
                {
                    AdvanceRaw();
                    // "GO 5" repeats the batch 5 times — the count must be preserved.
                    string? goCount = null;
                    var cnt = PeekSameLine();
                    if (cnt != null && cnt.Type == TokenType.NumberLiteral)
                    { goCount = cnt.Value; Advance(); }
                    // A GO before any statement is meaningless — drop it.
                    if (script.Statements.Count > 0)
                        script.Statements.Add(new GoSeparatorNode { Count = goCount });
                    continue;
                }
                break;
            }

            // Only trailing separators remained — stop.
            if (IsAtEnd())
                break;

            // Leading semicolon before first statement (e.g. ;with ...)
            if (isFirstStatement && hadSemicolon)
                script.HasLeadingSemicolon = true;
            isFirstStatement = false;

            int beforeStmt = _pos;
            // A ';' before WITH is mandatory in T-SQL (the previous statement must be
            // terminated) — remember it so FormatScript can re-emit ";with ...".
            bool nextIsWith = Peek().IsKeyword("WITH");
            AstNode? stmt;
            try
            {
                stmt = ParseStatement();
            }
            catch (ParseException) when (Peek().Type == TokenType.EndOfFile)
            {
                // The input ends in the middle of this construct — a selection cut short, e.g.
                // "… from openquery(" with the rest missing. Keep as much as still parses: the
                // longest prefix of the statement is formatted, and only the unfinished remainder
                // is emitted exactly as written. If nothing parses, the whole statement is.
                int tailStart = beforeStmt;
                AstNode? recovered = null;
                if (recoverPartialStatement) recovered = TryParseLongestPrefix(beforeStmt, out tailStart);
                if (recovered != null)
                {
                    if (newlinesBefore >= 2 && script.Statements.Count > 0)
                        recovered.BlankLineBefore = true;
                    script.Statements.Add(recovered);
                    script.UnparsedTail = RawTextFrom(tailStart);
                    script.UnparsedTailGlued = true;
                }
                else
                {
                    script.UnparsedTail = RawTextFrom(beforeStmt);
                    script.UnparsedTailBlankBefore = newlinesBefore >= 2 && script.Statements.Count > 0;
                }
                break;
            }
            // Forward-progress guarantee: never allow a zero-consumption iteration to loop
            // forever. If a statement was somehow not advanced, consume one token and move on.
            if (_pos == beforeStmt) { AdvanceRaw(); continue; }
            if (stmt != null)
            {
                // Comments parked while parsing an expression ride above this statement.
                stmt.HoistedComments.AddRange(DrainPendingComments());
                if (hadSemicolon && nextIsWith && script.Statements.Count > 0)
                    stmt.LeadingSemicolon = true;
                // Preserve a blank line that existed before this statement in the source.
                if (newlinesBefore >= 2 && script.Statements.Count > 0)
                    stmt.BlankLineBefore = true;
                // A ';' on the statement's own last line terminates it and stays with it, so the
                // next statement starts on its own line instead of being run together with this
                // one. A ';' on a later line is left to the separator loop above.
                if (stmt is not RawTokensNode) stmt.TrailingSemicolon |= TryTakeSameLineSemicolon();
                // A comment on the SAME line as the statement's end is a trailing comment for
                // THIS statement — keep it attached here rather than letting it migrate to the
                // following statement as a standalone comment.
                var trailing = TryTakeSameLineInlineComment();
                if (trailing != null) stmt.StatementTrailingComment = trailing;
                script.Statements.Add(stmt);
            }
        }
        return script;
    }

    /// <summary>
    /// Parses a partial SQL selection (fragment). Returns a fragment node when the input
    /// is a bare WHERE clause, column list, or JOIN chain; otherwise returns null so the
    /// caller can fall back to full-statement parsing.
    /// </summary>
    public AstNode? ParseFragment()
    {
        var first = Peek();

        // A leading standalone comment means this is (at least) a full statement with a
        // preceding comment, not a bare clause fragment. Let full parsing handle it.
        if (first.Type is TokenType.LineComment or TokenType.BlockComment)
            return null;

        // Bare WHERE clause: "where a = 1 and b = 2"
        if (first.IsKeyword("WHERE"))
        {
            Advance();
            var node = new WhereFragmentNode();
            node.Conditions.AddRange(ParseConditionList(isJoinOn: false));
            return AtFragmentEnd() ? node : null;
        }

        // Bare GROUP BY: "group by a, b, c"
        if (first.IsKeyword("GROUP"))
        {
            Advance();
            if (Peek().IsKeyword("BY")) Advance();
            var node = new GroupByFragmentNode();
            node.Columns.AddRange(ParseExpressionList());
            return AtFragmentEnd() ? node : null;
        }

        // Bare ORDER BY: "order by a desc, b asc"
        if (first.IsKeyword("ORDER"))
        {
            Advance();
            if (Peek().IsKeyword("BY")) Advance();
            var node = new OrderByFragmentNode();
            node.Items.AddRange(ParseOrderByList());
            return AtFragmentEnd() ? node : null;
        }

        // Bare JOIN chain: "inner join t on ... [join ...]"
        if (IsJoinKeyword())
        {
            var node = new JoinFragmentNode();
            while (IsJoinKeyword()) node.Joins.Add(ParseJoin());
            return AtFragmentEnd() ? node : null;
        }

        // If it starts with a statement keyword, this isn't a fragment.
        if (first.IsKeyword("SELECT") || first.IsKeyword("INSERT") || first.IsKeyword("UPDATE")
            || first.IsKeyword("DELETE") || first.IsKeyword("WITH") || first.IsKeyword("BEGIN")
            || first.IsKeyword("CREATE") || first.IsKeyword("DROP") || first.IsKeyword("DECLARE")
            || first.Type == TokenType.DeclareKeyword)
            return null;

        // Bare condition list (no leading WHERE): "a = 1 and b = 2".
        int save = _pos;
        try
        {
            var conds = ParseConditionList(isJoinOn: false);
            if (conds.Count > 0 && AtFragmentEnd() && LooksLikeConditions(conds))
            {
                var node = new WhereFragmentNode();
                node.Conditions.AddRange(conds);
                return node;
            }
        }
        catch { /* fall through */ }
        _pos = save;

        // Bare column list: "a, b, count(*) as cnt". At least TWO comma-separated columns
        // are required — otherwise any unrecognized two-word statement ("use mydb",
        // "waitfor delay '…'") would be "formatted" into a bogus "x as y" column.
        save = _pos;
        try
        {
            var cols = ParseSelectColumns();
            if (cols.Count >= 2 && AtFragmentEnd())
            {
                var node = new ColumnListFragmentNode();
                node.Columns.AddRange(cols);
                return node;
            }
        }
        catch { /* fall through */ }
        _pos = save;

        return null;
    }

    /// <summary>True if only EOF remains (skippables are transparent to Peek).</summary>
    private bool AtFragmentEnd() => Peek().Type == TokenType.EndOfFile;

    /// <summary>Heuristic: a condition list is "real" if at least one item is a comparison/logical expr.</summary>
    private static bool LooksLikeConditions(List<AstNode> conds)
    {
        foreach (var c in conds)
        {
            if (c is ConditionNode cn)
            {
                var e = cn.Expression;
                if (e is BinaryExprNode || e is InExprNode || e is BetweenExprNode || e is LikeExprNode)
                    return true;
            }
        }
        return false;
    }

    private bool IsGoKeyword()
    {
        var t = Peek();
        return t.Type == TokenType.Identifier && t.Value.Equals("GO", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True at CREATE/ALTER [OR ALTER] FUNCTION|PROCEDURE|PROC|TRIGGER — a programmable
    /// object whose procedural body we won't try to reformat.</summary>
    private bool IsProceduralObjectStart()
    {
        var head = Peek();
        if (!(head.IsKeyword("CREATE") || head.IsKeyword("ALTER"))) return false;
        int k = 1;
        if (PeekAt(1).IsKeyword("OR") && PeekAt(2).IsKeyword("ALTER")) k = 3;
        var kind = PeekAt(k).Value;
        return kind.Equals("FUNCTION",  StringComparison.OrdinalIgnoreCase)
            || kind.Equals("PROCEDURE", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("PROC",      StringComparison.OrdinalIgnoreCase)
            || kind.Equals("TRIGGER",   StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Programmable objects (CREATE/ALTER FUNCTION | PROCEDURE) + control flow
    // ═══════════════════════════════════════════════════════════════════════════

    private AstNode ParseCreateProgrammable()
    {
        // Prefix: CREATE [OR ALTER] {FUNCTION|PROCEDURE|PROC}
        var prefix = new System.Text.StringBuilder();
        prefix.Append(Advance().Value.ToLowerInvariant());       // create / alter
        if (Peek().IsKeyword("OR"))
        {
            prefix.Append(' ').Append(Advance().Value.ToLowerInvariant());   // or
            prefix.Append(' ').Append(Advance().Value.ToLowerInvariant());   // alter
        }
        prefix.Append(' ').Append(Advance().Value.ToLowerInvariant());       // function / procedure / proc

        // Object name (dotted, may be [quoted]).
        var name = new System.Text.StringBuilder();
        name.Append(Advance().Value);
        while (PeekPastComments().Type == TokenType.Dot)
        { ParkComment(); name.Append(Advance().Value); ParkComment(); name.Append(Advance().Value); }

        var pars = new List<ParamNode>();
        bool hasParens = false;
        if (PeekIs(TokenType.LeftParen))
        {
            hasParens = true;
            Advance(); // (
            pars.AddRange(ParseParamList(untilRightParen: true));
            Expect(TokenType.RightParen);
        }
        else if (Peek().Type == TokenType.Variable)
        {
            pars.AddRange(ParseParamList(untilRightParen: false)); // procedure params without parens
        }

        // Returns clause / WITH options: everything up to AS (or the body).
        var returns = new List<Token>();
        while (!IsAtEnd() && !Peek().IsKeyword("AS") && !Peek().IsKeyword("BEGIN") && !IsGoKeyword())
            returns.Add(Advance());
        if (Peek().IsKeyword("AS")) Advance();

        AstNode? body = null;
        if (!IsAtEnd() && !IsGoKeyword()) body = ParseStatement();

        var obj = new ProgrammableObjectNode { Prefix = prefix.ToString(), Name = name.ToString(), HasParens = hasParens, Body = body };
        obj.Params.AddRange(pars);
        obj.ReturnsClause.AddRange(returns);
        return obj;
    }

    /// <summary>Parses a comma-separated parameter list: @name type [= default] [output].</summary>
    private List<ParamNode> ParseParamList(bool untilRightParen)
    {
        var list = new List<ParamNode>();
        while (!IsAtEnd())
        {
            if (untilRightParen && PeekIs(TokenType.RightParen)) break;
            if (!untilRightParen && (Peek().IsKeyword("AS") || IsGoKeyword())) break;
            if (PeekIs(TokenType.Comma)) { Advance(); continue; }
            if (Peek().Type != TokenType.Variable) break;   // not a parameter — stop

            var variable = Advance();
            var dataType = new List<Token>();
            int depth = 0;
            while (!IsAtEnd())
            {
                var t = Peek();
                if (depth == 0 && (PeekIs(TokenType.Equals) || PeekIs(TokenType.Comma) || PeekIs(TokenType.RightParen))) break;
                if (depth == 0 && (t.IsKeyword("AS") || t.IsKeyword("OUTPUT") || IsGoKeyword() || t.Type == TokenType.EndOfFile)) break;
                if (t.Type == TokenType.LeftParen)  depth++;
                if (t.Type == TokenType.RightParen) depth--;
                dataType.Add(Advance());
            }
            AstNode? def = null;
            if (TryConsume(TokenType.Equals)) def = ParseExpression();
            bool output = false;
            if (Peek().IsKeyword("OUTPUT")) { output = true; Advance(); }

            string? tc = TryTakeSameLineComment();
            if (PeekIs(TokenType.Comma)) Advance();
            if (tc == null) tc = TryTakeSameLineInlineComment();

            var p = new ParamNode { Variable = variable, Default = def, Output = output, TrailingComment = tc };
            p.DataType.AddRange(dataType);
            list.Add(p);
        }
        return list;
    }

    /// <summary>
    /// When the WHOLE condition of an IF / WHILE is one parenthesised expression, it is laid out
    /// as a condition group: "(" stays on the keyword's line, the condition sits one tab in and
    /// the ")" takes its own line. Conditions the author did not wrap keep the inline layout.
    /// </summary>
    private static List<AstNode> AsGroupIfFullyParenthesised(List<AstNode> conds)
    {
        if (conds.Count != 1 || conds[0] is not ConditionNode c || c.Expression is not ParenExprNode p)
            return conds;
        var group = new ConditionGroupNode();
        group.Conditions.Add(new ConditionNode { Expression = p.Inner });
        return new List<AstNode> { new ConditionNode { Expression = group, TrailingComment = c.TrailingComment } };
    }

    private AstNode ParseIf()
    {
        Advance(); // IF
        var conds = AsGroupIfFullyParenthesised(ParseConditionList(isJoinOn: false));
        var then = ParseStatement();
        AstNode? elseStmt = null; string? elseComment = null;
        if (Peek().IsKeyword("ELSE"))
        {
            Advance();
            // "else -- режим записи" — the comment stays on the else line instead of drifting
            // down onto the branch body below it.
            elseComment = TryTakeSameLineInlineComment();
            elseStmt = ParseStatement();
        }
        var n = new IfNode { Then = then, Else = elseStmt, ElseComment = elseComment };
        n.Conditions.AddRange(conds);
        return n;
    }

    private AstNode ParseWhile()
    {
        Advance(); // WHILE
        var conds = AsGroupIfFullyParenthesised(ParseConditionList(isJoinOn: false));
        var body = ParseStatement();
        var n = new WhileNode { Body = body };
        n.Conditions.AddRange(conds);
        return n;
    }

    private AstNode ParseReturn()
    {
        Advance(); // RETURN
        AstNode? val = null;
        var t = Peek();
        bool ends = t.Type is TokenType.Semicolon or TokenType.EndOfFile
            || t.IsKeyword("END") || t.IsKeyword("ELSE") || IsGoKeyword();
        if (!ends) val = ParseExpression();
        // Keep the ';' the author wrote (rule `semi`) rather than swallowing it.
        var node = new ReturnNode { Value = val };
        node.TrailingSemicolon = TryConsume(TokenType.Semicolon);
        return node;
    }

    private AstNode ParseSet()
    {
        Advance(); // SET
        var target = ParsePrimary();
        string op = "=";
        if (Peek().Type == TokenType.CompoundAssign) op = Advance().Value;
        else if (PeekIs(TokenType.Equals)) Advance();
        var val = ParseExpression();
        // The ';' the author wrote is the statement's own (rule `semi`); swallowing it silently
        // dropped it from the output.
        var node = new SetNode { Target = target, Op = op, Value = val };
        node.TrailingSemicolon = TryConsume(TokenType.Semicolon);
        return node;
    }

    /// <summary>True at TRAN / TRANSACTION / DISTRIBUTED (the words that make a BEGIN a
    /// transaction statement rather than a block).</summary>
    private static bool IsTransactionWord(Token t) =>
        t.IsKeyword("TRANSACTION")
        || t.Value.Equals("TRAN", StringComparison.OrdinalIgnoreCase)
        || t.Value.Equals("DISTRIBUTED", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// BEGIN [DISTRIBUTED] TRAN[SACTION] [name] | COMMIT [TRAN[SACTION]|WORK] [name]
    /// | ROLLBACK [TRAN[SACTION]|WORK] [name]. Emitted as a raw statement; the transaction
    /// keywords are lowercased, a name keeps its case. The optional name/qualifier is taken
    /// only from the SAME line, so the next statement is never swallowed as a "name".
    /// </summary>
    private AstNode ParseTransactionStatement()
    {
        var raw = new RawTokensNode();
        raw.Tokens.Add(Lowered(Advance()));                   // begin / commit / rollback
        // Qualifiers: [distributed] tran|transaction|work. Taken even from the next line — none
        // of these words can start a statement, and "commit \n transaction" is one statement that
        // belongs on one line, not two ("commit" alone followed by a stray "transaction").
        while (true)
        {
            var q = Peek();
            if (IsTransactionWord(q) || q.Value.Equals("WORK", StringComparison.OrdinalIgnoreCase))
                raw.Tokens.Add(Lowered(Advance()));
            else break;
        }
        // Optional same-line transaction/savepoint name.
        var name = PeekSameLine();
        if (name != null
            && name.Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Variable
            && !IsGoKeyword())
            raw.Tokens.Add(Advance());
        // A ';' on this line ends the statement and belongs to it (rule `semi`); one on a later
        // line is left to the statement loop, where it both separates statements and marks a
        // following WITH as needing its ";with" prefix.
        int semi = _pos;
        while (semi < _tokens.Count && _tokens[semi].Type == TokenType.Whitespace) semi++;
        if (semi < _tokens.Count && _tokens[semi].Type == TokenType.Semicolon)
        { raw.Tokens.Add(_tokens[semi]); _pos = semi + 1; }
        return raw;
    }

    /// <summary>Lowercases keyword and transaction-word tokens for uniform "begin tran" output.</summary>
    private static Token Lowered(Token t) => new Token(t.Type, t.Value.ToLowerInvariant(), t.Line, t.Column);

    /// <summary>Peeks the next meaningful token only if it sits on the SAME line
    /// (no newline between here and it); returns null otherwise.</summary>
    private Token? PeekSameLine()
    {
        int i = _pos;
        while (i < _tokens.Count && _tokens[i].Type == TokenType.Whitespace) i++;
        if (i < _tokens.Count && _tokens[i].Type is not (TokenType.Newline or TokenType.EndOfFile))
            return _tokens[i];
        return null;
    }

    private AstNode? ParseStatement()
    {
        // Programmable objects (CREATE/ALTER FUNCTION|PROCEDURE|PROC|TRIGGER): parse header +
        // procedural body. If parsing fails, FormatSource returns the source unchanged.
        if (IsProceduralObjectStart()) return ParseCreateProgrammable();

        var tok = Peek();
        if (tok.Type == TokenType.DeclareKeyword)  return ParseDeclare();
        if (tok.IsKeyword("WITH"))                 return ParseWithCte();
        if (tok.IsKeyword("SELECT"))               return ParseSelectOrSet(null);
        if (tok.IsKeyword("INSERT"))               return ParseInsert();
        if (tok.IsKeyword("UPDATE"))               return ParseUpdate();
        if (tok.IsKeyword("DELETE"))               return ParseDelete();
        if (tok.IsKeyword("MERGE") && !PeekAt(1).IsKeyword("JOIN")) return ParseMerge();
        // BEGIN TRAN[SACTION] is a transaction statement, NOT a BEGIN...END block:
        // parsing it as a block would swallow following statements and fabricate an "end".
        if (tok.IsKeyword("BEGIN") && IsTransactionWord(PeekAt(1)))
            return ParseTransactionStatement();
        if (tok.IsKeyword("COMMIT") || tok.IsKeyword("ROLLBACK"))
            return ParseTransactionStatement();
        // BEGIN TRY / BEGIN CATCH are blocks of their own, closed by END TRY / END CATCH.
        if (tok.IsKeyword("BEGIN") && (PeekAt(1).IsKeyword("TRY") || PeekAt(1).IsKeyword("CATCH")))
            return ParseBeginEnd(PeekAt(1).Value.ToLowerInvariant());
        if (tok.IsKeyword("BEGIN"))                return ParseBeginEnd();
        if (tok.IsKeyword("CREATE"))               return ParseCreate();
        if (tok.IsKeyword("DROP"))                 return ParseDrop();
        if (tok.IsKeyword("IF"))                   return ParseIf();
        if (tok.IsKeyword("WHILE"))                return ParseWhile();
        if (tok.IsKeyword("RETURN"))               return ParseReturn();
        if (tok.IsKeyword("SET") && PeekAt(1).Type == TokenType.Variable) return ParseSet();
        if (tok.Type == TokenType.Semicolon)        { Advance(); return null; }
        // Standalone comment: consume as single-token raw node (prevent bleeding into next statement)
        if (tok.Type is TokenType.LineComment or TokenType.BlockComment)
        {
            bool blankBefore = CountNewlinesBackFromCurrent() >= 2;
            var cmt = new RawTokensNode(); cmt.Tokens.Add(Advance());
            bool blankAfter = CountNewlinesForwardFromCurrent() >= 2;
            cmt.BlankBefore = blankBefore;
            cmt.BlankAfter = blankAfter;
            return cmt;
        }
        return ParseRawStatement();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DECLARE
    // ═══════════════════════════════════════════════════════════════════════════

    private DeclareNode ParseDeclare()
    {
        Advance(); // DECLARE keyword
        var node = new DeclareNode();
        do
        {
            var variable = Expect(TokenType.Variable);
            var dataType = new List<Token>();
            // A table variable's type is a column list, not a run of type tokens: read it like
            // CREATE TABLE's body so each column gets its own line and keeps its comment.
            if (Peek().IsKeyword("TABLE") && PeekAt(1).Type == TokenType.LeftParen)
            {
                Advance(); // table
                Advance(); // (
                var tableColumns = new List<ColumnDefNode>();
                ParseColumnDefs(tableColumns);
                var tv = new DeclareVarNode { Variable = variable, TableColumns = tableColumns };
                tv.TrailingComment = TryTakeSameLineInlineComment();
                node.Variables.Add(tv);
                // The do-while below consumes the separating comma; nothing more to do here.
                continue;
            }
            int typeDepth = 0;
            while (!IsAtEnd())
            {
                var t = Peek();
                // A comma, '=' or ';' ends the type only at paren depth 0. Stopping at ';'
                // is critical: an initializer-less last variable ("@x float;") must not
                // swallow the ';' and every following statement up to the next SELECT/GO.
                if (typeDepth == 0 && (PeekIs(TokenType.Equals) || PeekIs(TokenType.Comma) || PeekIs(TokenType.Semicolon))) break;
                // A comment never belongs to the data type: swallowing it would render as
                // "@b float --note" glued into the type. Leave it for the trailing-comment
                // check below (same line) or for the next statement (standalone).
                if (t.Type is TokenType.LineComment or TokenType.BlockComment) break;
                if (t.IsKeyword("SELECT") || t.Type == TokenType.DeclareKeyword
                    || IsGoKeyword() || t.Type == TokenType.EndOfFile) break;
                // A statement-boundary keyword also ends the type (defensive when there is no
                // ';', e.g. "declare @x float begin ..." inside a function body).
                if (typeDepth == 0 && (t.IsKeyword("BEGIN") || t.IsKeyword("END") || t.IsKeyword("IF")
                    || t.IsKeyword("WHILE") || t.IsKeyword("RETURN") || t.IsKeyword("SET")
                    || t.IsKeyword("INSERT") || t.IsKeyword("UPDATE") || t.IsKeyword("DELETE")
                    || t.IsKeyword("CREATE") || t.IsKeyword("DROP") || t.IsKeyword("WITH"))) break;
                if (t.Type == TokenType.LeftParen)  typeDepth++;
                if (t.Type == TokenType.RightParen) typeDepth--;
                dataType.Add(Advance());
            }
            AstNode? init = null;
            if (TryConsume(TokenType.Equals)) init = ParseExpression();
            // Trailing comment on the SAME line as the variable: @a int = 1 --note.
            // A comment on the NEXT line is standalone and must not be pulled in here.
            string? varComment = TryTakeSameLineComment();
            node.Variables.Add(new DeclareVarNode { Variable = variable, Initializer = init, TrailingComment = varComment }
                .Tap(v => v.DataType.AddRange(dataType)));
        } while (PeekIs(TokenType.Comma) && ConsumeCommaThenMaybeComment(node));
        return node;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  WITH / CTE
    // ═══════════════════════════════════════════════════════════════════════════

    private AstNode ParseWithCte()
    {
        Advance(); // WITH
        var ctes = new List<AstNode>();
        while (true)
        {
            var cteName = Advance();
            Expect(TokenType.Keyword, "AS");
            Expect(TokenType.LeftParen);
            var cteBody = ParseSelectOrSet(null);
            Expect(TokenType.RightParen);
            ctes.Add(new CteDefinitionNode { Name = cteName, Body = cteBody });
            if (!PeekIs(TokenType.Comma)) break;
            Advance();
        }
        // A CTE list can head an INSERT / UPDATE / DELETE just as well as a SELECT. Insisting on
        // SELECT here is what made "with … insert into … select …" fail to parse, leaving the
        // whole script unformatted.
        if (Peek().IsKeyword("INSERT")) return ParseInsert().Tap(n => n.CteDefinitions.AddRange(ctes));
        if (Peek().IsKeyword("UPDATE")) return ParseUpdate().Tap(n => n.CteDefinitions.AddRange(ctes));
        if (Peek().IsKeyword("DELETE")) return ParseDelete().Tap(n => n.CteDefinitions.AddRange(ctes));
        return ParseSelectOrSet(ctes);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SELECT  (+ UNION / EXCEPT / INTERSECT chaining)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a single SELECT then checks for UNION / EXCEPT / INTERSECT.
    /// Returns either a SelectStatementNode or a SetOperationNode tree.
    /// CTEs are attached only to the outermost left node.
    /// </summary>
    private AstNode ParseSelectOrSet(List<AstNode>? ctes)
    {
        AstNode left = ParseSelectStatement(ctes);

        while (IsSetOperator())
        {
            string op = BuildSetOperator(); // "UNION" / "UNION ALL" / "EXCEPT" / "INTERSECT"
            AstNode right = ParseSelectStatement(null);
            left = new SetOperationNode { Operator = op, Left = left, Right = right };
        }

        return left;
    }

    private bool IsSetOperator()
    {
        var t = Peek();
        return t.IsKeyword("UNION") || t.IsKeyword("EXCEPT") || t.IsKeyword("INTERSECT");
    }

    private string BuildSetOperator()
    {
        var t = Advance(); // UNION / EXCEPT / INTERSECT
        string op = t.Value.ToLowerInvariant();
        if (op == "union" && Peek().IsKeyword("ALL")) { Advance(); op = "union all"; }
        return op;
    }

    private SelectStatementNode ParseSelectStatement(List<AstNode>? ctes)
    {
        Expect(TokenType.Keyword, "SELECT");
        // A comment sitting just before SELECT (e.g. inside "( --note\n select ...)") was
        // skipped by Expect; keep it as a leading comment rendered above the select.
        var leadingComments = DrainPendingComments();
        // SELECT [DISTINCT] [TOP ...] — DISTINCT precedes TOP syntactically.
        // A comment may sit between SELECT and DISTINCT/TOP, e.g. "select /*top 10*/ top (@top)".
        // Peek() does NOT skip comments, so we must look PAST them: otherwise the TOP/DISTINCT
        // clause is missed and "top (@top)" is misparsed as a column, derailing the whole
        // statement. When the clause does follow, the intervening comment(s) are consumed and
        // kept as leading comments of the select.
        bool distinct = false;
        if (PeekPastComments().IsKeyword("DISTINCT"))
        { leadingComments.AddRange(CollectStandaloneComments()); distinct = true; Advance(); }
        string? topExpr = null;
        if (PeekPastComments().IsKeyword("TOP"))
        {
            leadingComments.AddRange(CollectStandaloneComments());
            Advance();
            var tb = new System.Text.StringBuilder();
            if (PeekIs(TokenType.LeftParen))
            {
                Advance();
                int depth = 1; tb.Append('(');
                while (!IsAtEnd() && depth > 0)
                {
                    var t = Peek();
                    if (t.Type == TokenType.LeftParen) depth++;
                    if (t.Type == TokenType.RightParen) { depth--; }
                    tb.Append(t.Value);
                    Advance();
                }
            }
            else
            {
                tb.Append(Advance().Value); // TOP n
            }
            if (Peek().Value.Equals("PERCENT", System.StringComparison.OrdinalIgnoreCase)
                && Peek().Type != TokenType.StringLiteral) { Advance(); tb.Append(" percent"); }
            // WITH TIES — TIES may lex as an identifier, so match by value.
            if (Peek().IsKeyword("WITH")
                && PeekAt(1).Value.Equals("TIES", System.StringComparison.OrdinalIgnoreCase))
            { Advance(); Advance(); tb.Append(" with ties"); }
            topExpr = tb.ToString();
        }
        var node = new SelectStatementNode { IsDistinct = distinct, TopExpr = topExpr };
        node.LeadingComments.AddRange(leadingComments);
        if (ctes != null) node.CteDefinitions.AddRange(ctes);
        // A comment on the SELECT line annotates the statement, not the first column: it stays on
        // the select line instead of moving down onto the column's line. Columns always start on
        // their own line, so what followed the comment on that line moves down as usual.
        node.HeaderComment = TryTakeSameLineInlineComment();
        node.Columns.AddRange(ParseSelectColumns());
        // SELECT ... INTO #tbl / ##global / schema.table / [quoted]
        // A comment between the column list and INTO/FROM (a commented-out clause, typically)
        // must not hide the clause behind it: peeked at directly, the FROM went unseen and the
        // statement fell apart. Kept to be rendered on its own line where it was written.
        if (PeekPastComments().IsKeyword("INTO") || PeekPastComments().IsKeyword("FROM"))
            node.PreFromComments.AddRange(CollectStandaloneComments());

        if (Peek().IsKeyword("INTO"))
        {
            Advance();
            var nameSb = new System.Text.StringBuilder();
            nameSb.Append(Advance().Value);
            while (PeekPastComments().Type == TokenType.Dot)
            { ParkComment(); nameSb.Append(Advance().Value); ParkComment(); nameSb.Append(Advance().Value); }
            node.IntoTable = nameSb.ToString();
            // A comment on the INTO line stays on it ("into #wru/*note*/"). Left in the stream it
            // hid the FROM behind it and the rest of the statement fell out into raw text.
            node.IntoComment = TryTakeSameLineInlineComment(out bool intoGlued);
            node.IntoCommentGlued = intoGlued;
        }
        if (PeekClause("FROM", node.PreFromComments))
        {
            Advance();
            // FROM may list several sources separated by commas (the old-style cross join):
            // "from a as t1, b as t2, #c as t3". Each source can be followed by its own joins.
            do
            {
            var fromTable = ParseTableRef();
            node.FromClauses.Add(fromTable);
            // A comment on the SAME line as the FROM table stays attached to that line, a
            // /* */ one as much as a -- one (rather than migrating to a PostFromComment or the
            // following statement, which is where a block comment used to end up).
            var fromTrailing = TryTakeSameLineInlineComment(out bool fromGlued);
            if (fromTrailing != null)
            {
                fromTable.TrailingComment = fromTrailing;
                fromTable.TrailingCommentGlued = fromGlued;
            }
            // Collect joins, tolerating standalone comments between FROM and each JOIN.
            while (true)
            {
                int beforeComments = _pos;
                var pending = CollectStandaloneComments();
                if (IsJoinKeyword())
                {
                    var join = ParseJoin();
                    join.LeadingComments.AddRange(pending);
                    node.FromClauses.Add(join);
                }
                else
                {
                    // Comments after the FROM/JOIN block belong to this SELECT only when the
                    // statement actually continues (WHERE, GROUP BY, …). Otherwise they annotate
                    // whatever comes AFTER the statement: give them back untouched, so the script
                    // level attaches them to the next statement together with the blank line the
                    // source had around them.
                    if (pending.Count > 0 && !IsSelectContinuation(Peek()))
                        _pos = beforeComments;
                    else
                        node.PostFromComments.AddRange(pending);
                    break;
                }
            }
            } while (PeekPastComments().Type == TokenType.Comma && AdvanceCommaKeepingTrailingComment(node));
        }
        if (Peek().IsKeyword("WHERE"))  { Advance(); node.WhereConditions.AddRange(ParseConditionList(isJoinOn: false)); }
        if (Peek().IsKeyword("GROUP"))  { Advance(); Expect(TokenType.Keyword, "BY"); node.GroupByColumns.AddRange(ParseExpressionList()); }
        if (Peek().IsKeyword("HAVING")) { Advance(); node.HavingConditions.AddRange(ParseConditionList(isJoinOn: false)); }
        if (Peek().IsKeyword("ORDER"))  { Advance(); Expect(TokenType.Keyword, "BY"); node.OrderByColumns.AddRange(ParseOrderByList()); }
        // OPTION (...) query hint — a trailing clause, not a WHERE condition.
        if (Peek().IsKeyword("OPTION"))
        {
            Advance(); // OPTION
            Expect(TokenType.LeftParen);
            var optTokens = new List<Token>();
            int depth = 1;
            while (!IsAtEnd() && depth > 0)
            {
                var t = PeekRaw();
                if (t.Type == TokenType.LeftParen)  depth++;
                if (t.Type == TokenType.RightParen) { depth--; if (depth == 0) { AdvanceRaw(); break; } }
                optTokens.Add(AdvanceRaw());
            }
            node.OptionTokens = optTokens;
        }
        return node;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  INSERT
    // ═══════════════════════════════════════════════════════════════════════════

    private InsertNode ParseInsert()
    {
        Advance(); // INSERT
        if (Peek().IsKeyword("INTO")) Advance(); // optional INTO

        // INSERT target: parse the table name only. A following '(' is the column list,
        // never table-valued-function arguments — so suppress func-arg consumption.
        var table = ParseTableRef(allowFuncArgs: false);

        // Optional column list: INSERT INTO t (col1, col2)
        var columns = new List<AstNode>();
        if (PeekIs(TokenType.LeftParen))
        {
            // Peek ahead: if first non-paren token is SELECT → this is not a column list
            int savedPos = _pos;
            Advance(); // (
            if (!Peek().IsKeyword("SELECT"))
            {
                while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
                {
                    columns.Add(ParsePrimary());
                    if (PeekIs(TokenType.Comma)) Advance();
                }
                Expect(TokenType.RightParen);
            }
            else
            {
                // Restore: the ( belongs to the subquery handled below
                _pos = savedPos;
            }
        }

        // A comment right after the column list ("… )  -- note") must not hide the VALUES/SELECT
        // that follows, or the source becomes a separate statement and the blank line the source
        // had between them is preserved as if they were two. The same-line comment stays on the
        // ')' line; comments on their own line are left for the SELECT to carry.
        string? columnsComment = null;
        if (PeekPastComments().IsKeyword("VALUES") || PeekPastComments().IsKeyword("SELECT"))
            columnsComment = TryTakeSameLineComment();

        AstNode? source = null;
        if (Peek().IsKeyword("VALUES"))
        {
            source = ParseValues();
        }
        else if (Peek().IsKeyword("SELECT"))
        {
            source = ParseSelectOrSet(null);
        }
        else if (PeekIs(TokenType.LeftParen))
        {
            // (SELECT ...)
            Advance();
            source = ParseSelectOrSet(null);
            Expect(TokenType.RightParen);
        }

        return new InsertNode { Table = table, Source = source, ColumnsComment = columnsComment }
            .Tap(n => n.Columns.AddRange(columns));
    }

    private ValuesNode ParseValues()
    {
        Advance(); // VALUES
        var valuesNode = new ValuesNode();
        while (true)
        {
            Expect(TokenType.LeftParen);
            var row = new List<AstNode>();
            while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
            {
                row.Add(ParseExpression());
                if (PeekIs(TokenType.Comma)) Advance();
            }
            Expect(TokenType.RightParen);
            valuesNode.Rows.Add(row);
            // A comment annotating the row stays with the row; the comma is emitted before it.
            bool more = PeekIs(TokenType.Comma);
            if (more) Advance();
            valuesNode.RowComments.Add(TryTakeSameLineInlineComment());
            if (!more) break;
        }
        return valuesNode;
    }

    private bool AdvanceAndTrue() { Advance(); return true; }

    /// <summary>
    /// Consumes the comma between FROM sources and keeps a comment that follows it on the same
    /// line with the source BEFORE it — that is where the formatter itself puts such a comment
    /// ("from t1 as a,   --note"), so re-formatting its own output has to read it back the same
    /// way instead of handing it to the next source.
    /// </summary>
    private bool AdvanceCommaKeepingTrailingComment(SelectStatementNode node)
    {
        // A comment written between the source and its comma belongs to the source before it —
        // the same place the formatter puts one itself ("from t1 as a,   --note").
        var beforeComma = CollectStandaloneComments();
        Advance(); // ,
        var trailing = TryTakeSameLineInlineComment()
                       ?? (beforeComma.Count > 0 ? string.Join(" ", beforeComma) : null);
        if (trailing != null)
        {
            var last = node.FromClauses.Count > 0 ? node.FromClauses[^1] : null;
            if (last is TableRefNode tref && tref.TrailingComment == null) tref.TrailingComment = trailing;
            else if (last is JoinNode join && join.TrailingComment == null) join.TrailingComment = trailing;
            else _pendingComments.Add(trailing);
        }
        return true;
    }

    /// <summary>Consumes the comma between DECLARE variables; a comment right after the comma
    /// is attached to the variable just parsed (if it doesn't already carry one).</summary>
    private bool ConsumeCommaThenMaybeComment(DeclareNode node)
    {
        Advance(); // comma
        // Only a comment on the SAME line as the comma trails the just-parsed variable;
        // a comment on the next line is standalone and left for the next statement.
        var c = TryTakeSameLineComment();
        if (c != null && node.Variables.Count > 0 && node.Variables[^1].TrailingComment == null)
            node.Variables[^1].TrailingComment = c;
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  UPDATE
    // ═══════════════════════════════════════════════════════════════════════════

    private UpdateNode ParseUpdate()
    {
        Advance(); // UPDATE
        var table = ParseTableRef();
        // A comment on the "update tbl" line stays on it; one written on its own line before SET
        // keeps its own line. Either way it must not hide the SET behind it.
        var targetComment = TryTakeSameLineInlineComment();
        var preSet   = new List<string>();
        var preFrom  = new List<string>();
        var preWhere = new List<string>();
        PeekClause("SET", preSet);
        Expect(TokenType.Keyword, "SET");

        var assignments = ParseAssignmentList();

        var fromClauses = new List<AstNode>();
        if (PeekClause("FROM", preFrom))
        {
            Advance();
            fromClauses.Add(ParseTableRef());
            fromClauses.AddRange(ParseTrailingJoins(preWhere));
        }

        var whereConds = new List<AstNode>();
        if (PeekClause("WHERE", preWhere)) { Advance(); whereConds.AddRange(ParseConditionList(isJoinOn: false)); }

        return new UpdateNode { Table = table, TargetComment = targetComment }.Tap(n =>
        {
            n.Assignments.AddRange(assignments);
            n.FromClauses.AddRange(fromClauses);
            n.WhereConditions.AddRange(whereConds);
            n.PreSetComments.AddRange(preSet);
            n.PreFromComments.AddRange(preFrom);
            n.PreWhereComments.AddRange(preWhere);
        });
    }

    /// <summary>
    /// The comma-separated "col = value" list of a SET clause — UPDATE's and the one in a MERGE's
    /// WHEN … THEN UPDATE branch.
    /// </summary>
    private List<AssignmentNode> ParseAssignmentList()
    {
        var assignments = new List<AssignmentNode>();
        while (true)
        {
            var target = ParsePrimary();
            Expect(TokenType.Equals);
            var value = ParseExpression();
            // The comment belongs to the assignment, not to its value: left inside the value, the
            // separating comma was emitted AFTER the -- comment and ended up commented out —
            // which cost the script its formatting entirely.
            var assignComment = TakeLineCommentFrom(value) ?? TryTakeSameLineInlineComment();
            // Leading-comma style puts the separator at the start of the next line:
            //   set a = 1 -- note
            //     , b = 2
            bool more = PeekPastComments().Type == TokenType.Comma;
            if (more)
            {
                // Anything else standing between the assignment and its comma is a standalone
                // comment: park it above the statement rather than let the comma swallow it.
                _pendingComments.AddRange(CollectStandaloneComments());
                Advance(); // ,
                assignComment ??= TryTakeSameLineInlineComment();
            }
            assignments.Add(new AssignmentNode { Target = target, Value = value, TrailingComment = assignComment });
            if (!more) break;
        }
        return assignments;
    }

    /// <summary>
    /// Reads the JOINs that follow a FROM source in an UPDATE/DELETE, tolerating standalone
    /// comments in front of each one (they render above their join). A trailing run of comments
    /// belongs to this statement only when it actually continues with WHERE; otherwise it is given
    /// back untouched, so the script level attaches it to the next statement together with the
    /// blank lines the source had around it.
    /// </summary>
    private List<AstNode> ParseTrailingJoins(List<string> trailingSink)
    {
        var joins = new List<AstNode>();
        while (true)
        {
            int beforeComments = _pos;
            var pending = CollectStandaloneComments();
            if (IsJoinKeyword())
            {
                var join = ParseJoin();
                join.LeadingComments.AddRange(pending);
                joins.Add(join);
                continue;
            }
            if (pending.Count > 0)
            {
                if (Peek().IsKeyword("WHERE")) trailingSink.AddRange(pending);
                else _pos = beforeComments;
            }
            return joins;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MERGE
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// MERGE [INTO] target [AS alias] USING source [AS alias] ON &lt;conditions&gt;
    /// WHEN [NOT] MATCHED [BY TARGET|SOURCE] [AND &lt;conditions&gt;] THEN &lt;action&gt; …
    /// [OUTPUT … [INTO …]]. The action is UPDATE SET …, INSERT (…) VALUES (…) or DELETE — all
    /// in their abbreviated MERGE form, without a table of their own.
    /// </summary>
    private MergeNode ParseMerge()
    {
        Advance(); // MERGE
        bool hasInto = Peek().IsKeyword("INTO");
        if (hasInto) Advance();
        var target = ParseTableRef();
        var targetComment = TryTakeSameLineInlineComment();

        Expect(TokenType.Keyword, "USING");
        var source = ParseTableRef();
        var sourceComment = TryTakeSameLineInlineComment();

        var node = new MergeNode { Target = target, HasInto = hasInto, TargetComment = targetComment,
                                   Source = source, SourceComment = sourceComment };

        Expect(TokenType.Keyword, "ON");
        node.OnConditions.AddRange(ParseConditionList(isJoinOn: true));

        while (Peek().IsKeyword("WHEN"))
        {
            Advance();
            var kind = new System.Text.StringBuilder();
            if (Peek().IsKeyword("NOT")) { Advance(); kind.Append("not "); }
            Expect(TokenType.Keyword, "MATCHED");
            kind.Append("matched");
            if (Peek().Value.Equals("BY", StringComparison.OrdinalIgnoreCase))
            { Advance(); kind.Append(" by ").Append(Advance().Value.ToLowerInvariant()); }

            var when = new MergeWhenNode { Kind = kind.ToString() };
            while (Peek().IsKeyword("AND") || Peek().IsKeyword("OR"))
            {
                string op = Peek().IsKeyword("AND") ? "and" : "or";
                Advance();
                when.ExtraConditions.Add(new ConditionNode { LogicalOp = op, Expression = ParseExpression() });
            }
            when.ConditionComment = TryTakeSameLineInlineComment();
            Expect(TokenType.Keyword, "THEN");
            when.ThenComment = TryTakeSameLineInlineComment();

            if (Peek().IsKeyword("UPDATE"))
            {
                Advance();
                Expect(TokenType.Keyword, "SET");
                when.Action = "update";
                when.Assignments.AddRange(ParseAssignmentList());
            }
            else if (Peek().IsKeyword("INSERT"))
            {
                Advance();
                when.Action = "insert";
                if (PeekIs(TokenType.LeftParen))
                {
                    Advance();
                    while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
                    {
                        when.InsertColumns.Add(ParseExpression());
                        if (PeekIs(TokenType.Comma)) Advance(); else break;
                    }
                    Expect(TokenType.RightParen);
                }
                if (Peek().IsKeyword("DEFAULT")) { Advance(); Advance(); when.DefaultValues = true; }
                else if (Peek().IsKeyword("VALUES")) when.InsertValues = ParseValues();
            }
            else
            {
                Expect(TokenType.Keyword, "DELETE");
                when.Action = "delete";
            }
            node.Whens.Add(when);
        }

        // OUTPUT … [INTO …] is kept verbatim: it is a list of its own with no layout rules here.
        if (Peek().Value.Equals("OUTPUT", StringComparison.OrdinalIgnoreCase))
        {
            node.OutputTokens = TakeClauseTokens();
            node.OutputComment = TryTakeSameLineInlineComment();
            // "output … into #log" — the INTO target keeps its own line.
            if (PeekPastComments().IsKeyword("INTO"))
            {
                _pendingComments.AddRange(CollectStandaloneComments());
                node.OutputIntoTokens = TakeClauseTokens();
                node.OutputIntoComment = TryTakeSameLineInlineComment();
            }
        }
        return node;
    }

    /// <summary>
    /// Takes a clause verbatim — its keyword and everything up to the end of the line, a ';', a
    /// comment or a following clause keyword. Used for MERGE's OUTPUT / INTO, which have no
    /// layout rules of their own; keywords are lowercased like everywhere else.
    /// </summary>
    private List<Token> TakeClauseTokens()
    {
        var tokens = new List<Token> { Lowered(Advance()) };
        while (!IsAtEnd() && !PeekIs(TokenType.Semicolon)
               && Peek().Type is not (TokenType.LineComment or TokenType.BlockComment)
               && !Peek().IsKeyword("INTO") && !IsGoKeyword())
            tokens.Add(Peek().Type == TokenType.Keyword ? Lowered(Advance()) : Advance());
        return tokens;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DELETE
    // ═══════════════════════════════════════════════════════════════════════════

    private DeleteNode ParseDelete()
    {
        Advance(); // DELETE

        // Two forms:
        //   DELETE alias FROM tbl ...
        //   DELETE FROM tbl ...
        TableRefNode? targetAlias = null;
        TableRefNode? table = null;
        var fromClauses = new List<AstNode>();
        var preFrom  = new List<string>();
        var preWhere = new List<string>();
        string? targetComment = null;

        if (PeekClause("FROM", preFrom))
        {
            // DELETE FROM tbl ...
            Advance();
            table = ParseTableRef();
            targetComment = TryTakeSameLineInlineComment();
            fromClauses.AddRange(ParseTrailingJoins(preWhere));
        }
        else
        {
            // DELETE alias FROM tbl ...
            targetAlias = ParseTableRef();
            // A comment on the "delete d" line stays on it instead of hiding the FROM that
            // follows — the reason such a script came back completely unformatted.
            targetComment = TryTakeSameLineInlineComment();
            if (PeekClause("FROM", preFrom))
            {
                Advance();
                fromClauses.Add(ParseTableRef());
                fromClauses.AddRange(ParseTrailingJoins(preWhere));
            }
        }

        var whereConds = new List<AstNode>();
        if (PeekClause("WHERE", preWhere)) { Advance(); whereConds.AddRange(ParseConditionList(isJoinOn: false)); }

        return new DeleteNode { TargetAlias = targetAlias, Table = table, TargetComment = targetComment }.Tap(n =>
        {
            n.FromClauses.AddRange(fromClauses);
            n.WhereConditions.AddRange(whereConds);
            n.PreFromComments.AddRange(preFrom);
            n.PreWhereComments.AddRange(preWhere);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SELECT column list
    // ═══════════════════════════════════════════════════════════════════════════

    private List<SelectColumnNode> ParseSelectColumns()
    {
        var cols = new List<SelectColumnNode>();
        // Comments that stood before the separating comma wait here for the column they annotate.
        var carriedComments = new List<LeadingComment>();
        while (!IsAtEnd() && !IsSelectClauseKeyword())
        {
            if (PeekIs(TokenType.Comma)) { Advance(); continue; }

            // If a run of comments is followed by a statement boundary (DECLARE, another
            // statement, GO, ;, EOF), those comments are standalone — NOT column-leading.
            // Leave them for the outer parser and end the column list, so an assignment SELECT
            // with no FROM doesn't swallow a following DECLARE/statement as bogus columns.
            if ((PeekIs(TokenType.BlockComment) || PeekIs(TokenType.LineComment))
                && IsStatementBoundary(PeekPastComments()))
                break;

            // Leading comments before a column: block comments (/* */, kept inline) and line
            // comments standing on their own line (-- , rendered above the column). Capturing
            // line comments here keeps them out of ParsePrimary, where they would otherwise be
            // mis-parsed as an expression/alias.
            var leadingComments = new List<LeadingComment>(carriedComments);
            carriedComments.Clear();
            while (PeekIs(TokenType.BlockComment) || PeekIs(TokenType.LineComment))
            {
                var text = Advance().Value;
                // Whether the author put a line break between the comment and what follows
                // decides whether the column goes on the comment's line or the next one.
                leadingComments.Add(new LeadingComment { Text = text, BreakAfter = NewlineFollows() });
            }

            // Leading-comma style puts the separator after the comment:
            //   col1
            //   --note
            //   , col2
            // The comment belongs to col2, so carry it over and take the comma now.
            if (PeekIs(TokenType.Comma))
            {
                Advance();
                carriedComments.AddRange(leadingComments);
                continue;
            }

            // Rule: detect T-SQL assignment alias: [Alias] = expression
            // Pattern: simple identifier/quoted-identifier immediately followed by =
            Token? assignAlias = null;
            if (Peek().Type is TokenType.Identifier or TokenType.QuotedIdentifier
                && PeekAt(1).Type == TokenType.Equals)
            {
                assignAlias = Advance(); // consume alias token
                Advance();              // consume =
            }

            var expr = ParseExpression();
            // A -- comment captured by the expression's root (a ColumnRef/Literal) belongs to
            // the whole column and must render AFTER the comma, not inside the expression — pull
            // it up to the column level so the comma never lands inside the comment (bug 5).
            string? comment = TakeLineCommentFrom(expr);
            Token? alias = assignAlias;
            string? preAliasComment = null, postAliasComment = null;
            if (alias == null)
            {
                // A /* */ comment written around the AS ("1 /* before */ as /* after */ one")
                // belongs to the alias. Read as the column's trailing comment instead, it ended
                // the column list and left "as … one" behind as raw text.
                int beforeAliasComment = _pos, parkedBeforeAlias = _pendingComments.Count;
                var pending = TryTakeInlineBlockComment();
                if (Peek().IsKeyword("AS"))
                {
                    preAliasComment = pending;
                    Advance();
                    postAliasComment = TryTakeInlineBlockComment();
                    alias = Advance();
                }
                // A bare alias can also be written in single quotes ("p.eid 'суб ктв'"), the old
                // T-SQL spelling of "[суб ктв]". Not accepted here, the column list ended at the
                // expression and everything after it fell out of the statement as raw text.
                else if (Peek().Type is TokenType.Identifier or TokenType.QuotedIdentifier
                                     or TokenType.StringLiteral
                         && !IsClauseKeyword(Peek()) && !IsGoKeyword())
                { preAliasComment = pending; alias = Advance(); }
                // Non-reserved words lex as Keyword but are legal bare aliases (day, year,
                // key, ...): "select getdate() day" must not split into two columns.
                else if (Peek().Type == TokenType.Keyword
                         && !IsSelectClauseKeyword() && !IsClauseKeyword(Peek()))
                { preAliasComment = pending; alias = Advance(); }
                // No alias after all — the comment is the column's own, give it back.
                else Rewind(beforeAliasComment, parkedBeforeAlias);
            }
            // Only a comment on the SAME line is a trailing comment for this column.
            if (comment == null) comment = TryTakeSameLineComment();
            bool sawComma = PeekIs(TokenType.Comma);
            if (sawComma) Advance();
            // A trailing comment may also sit AFTER the comma on the same line: "a, --note"
            // or "a, /* note */". A block comment there is transparent — glued to this column.
            if (comment == null) comment = TryTakeSameLineInlineComment();
            // Every column starts on a line of its own, so the list's line break has to land
            // somewhere around a trailing /* */ comment. It goes after the comment — unless the
            // source already broke the line there, or the comment itself spans lines, in which
            // case the next column is on a fresh line anyway and keeps the author's layout.
            bool breakAfter = comment == null || comment.StartsWith("--")
                              || NewlineFollows() || !comment.Contains('\n');
            cols.Add(new SelectColumnNode { Expression = expr, Alias = alias, TrailingComment = comment,
                                            TrailingBreakAfter = breakAfter,
                                            PreAliasComment = preAliasComment,
                                            PostAliasComment = postAliasComment }
                .Tap(c => c.LeadingComments.AddRange(leadingComments)));
            // Columns are comma-separated: with no comma, the next token can't start another
            // column — it's the next statement ("select 1 \n use db"), which must not be
            // swallowed into the list with an invented comma. Standalone comments may stand
            // between the column and that comma, though, and the list goes on after them.
            if (!sawComma && PeekPastComments().Type != TokenType.Comma) break;
        }
        // Comments carried past a comma with no column behind them are hoisted, never dropped.
        _pendingComments.AddRange(carriedComments.Select(c => c.Text));
        return cols;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  FROM / JOIN
    // ═══════════════════════════════════════════════════════════════════════════

    private TableRefNode ParseTableRef(bool allowFuncArgs = true)
    {
        // A comment between the clause keyword and the table ("left join -- тип\n dbo.Orders")
        // belongs in front of the name. Read as the name itself, it turned the real name into an
        // alias and split the script ("from /*c*/ as dbo" + ".Orders" on the next line).
        var leadComments = CollectStandaloneComments();
        string? leadComment = leadComments.Count > 0
            ? string.Join(" ", leadComments.Select(CommentText.AsInline)) : null;

        // Subquery as table source: (SELECT ...) AS alias
        if (PeekIs(TokenType.LeftParen))
        {
            Advance(); // (
            var sub = ParseSelectOrSet(null);
            var closingComments = CollectStandaloneComments();
            Expect(TokenType.RightParen);
            Token? sqAlias = null;
            if (Peek().IsKeyword("AS")) { Advance(); sqAlias = Advance(); }
            else if (Peek().Type is TokenType.Identifier or TokenType.QuotedIdentifier && !IsJoinKeyword() && !IsSelectClauseKeyword())
                sqAlias = Advance();
            return new TableRefNode {
                SubQuery = new SubQueryNode { Select = sub }.Tap(q => q.CloseComments.AddRange(closingComments)),
                Alias = sqAlias, LeadingComment = leadComment }.Tap(n => n.Pivot = TryParsePivot());
        }

        var nameParts = new List<Token>();
        nameParts.Add(Advance());
        while (PeekPastComments().Type == TokenType.Dot)
        { ParkComment(); nameParts.Add(Advance()); ParkComment(); nameParts.Add(Advance()); }

        // Function-valued table source: name(arg1, arg2, ...) — e.g. openjson(col) or STRING_SPLIT(col, ',').
        // Suppressed for INSERT targets, where a following '(' is always the column list.
        List<AstNode>? funcArgs = null;
        if (allowFuncArgs && PeekIs(TokenType.LeftParen))
        {
            Advance(); // (
            funcArgs = new List<AstNode>();
            while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
            {
                funcArgs.Add(ParseExpression());
                if (PeekIs(TokenType.Comma)) Advance();
                else break;
            }
            Expect(TokenType.RightParen);
        }

        Token? alias = null;
        if (Peek().IsKeyword("AS")) { Advance(); alias = Advance(); }
        else if (Peek().Type is TokenType.Identifier or TokenType.QuotedIdentifier
                 && !IsJoinKeyword() && !IsSelectClauseKeyword() && !IsGoKeyword())
            alias = Advance();

        // Table hint: WITH (NOLOCK), WITH (INDEX(..)), etc. — capture raw hint text.
        string? hint = null;
        var hintComments = new List<string>();
        if (PeekPastComments().IsKeyword("WITH") && PeekPastComments(1).Type == TokenType.LeftParen)
        {
            // A comment before the hint must not hide it: read past the keyword, the whole
            // statement stopped parsing and the script came back unformatted.
            hintComments.AddRange(CollectStandaloneComments());
            Advance(); // WITH
            Advance(); // (
            var hb = new System.Text.StringBuilder();
            int depth = 1;
            while (!IsAtEnd() && depth > 0)
            {
                var t = Peek();
                if (t.Type == TokenType.LeftParen)  depth++;
                if (t.Type == TokenType.RightParen) { depth--; if (depth == 0) { Advance(); break; } }
                if (hb.Length > 0 && t.Type != TokenType.Comma
                    && !(hb.Length > 0 && hb[hb.Length-1] == '(')
                    && t.Type != TokenType.RightParen && t.Type != TokenType.LeftParen)
                    hb.Append(' ');
                if (t.Type == TokenType.Comma) hb.Append(", ");
                else hb.Append(t.Value);
                Advance();
            }
            hint = hb.ToString().Trim();
            // A comment written before the hint has no place on the line: park it above the
            // statement rather than lose it.
            _pendingComments.AddRange(hintComments);
        }

        bool isOpenQuery = nameParts.Count == 1
            && nameParts[0].Value.Equals("openquery", System.StringComparison.OrdinalIgnoreCase)
            && funcArgs != null;
        return new TableRefNode { Alias = alias, FuncArgs = funcArgs, IsOpenQuery = isOpenQuery,
                                 HintNolock = hint, LeadingComment = leadComment }
            .Tap(n => { n.Name.AddRange(nameParts); n.Pivot = TryParsePivot(); });
    }

    /// <summary>
    /// PIVOT (count(x) FOR col IN ([a], [b])) AS pvt — and UNPIVOT, which has the same shape with
    /// a plain column where PIVOT has its aggregate. Returns null when neither follows.
    /// </summary>
    private PivotNode? TryParsePivot()
    {
        var kw = PeekPastComments();
        if (!kw.IsKeyword("PIVOT") && !kw.IsKeyword("UNPIVOT")) return null;
        // Comments between the source and PIVOT keep their own lines above the block; peeked at
        // directly, they hid the keyword and the whole clause came out as raw text.
        var leading = CollectStandaloneComments();
        Advance();
        Expect(TokenType.LeftParen);
        // ParsePrimary, not ParseExpression: the column after FOR is followed by IN, which the
        // expression parser would happily swallow as an "x in (…)" test.
        var head = ParsePrimary();
        // Comments inside the pivot block stay in it, instead of being hoisted above the query.
        var headComment = TakeLineCommentFrom(head) ?? TryTakeSameLineInlineComment();
        Expect(TokenType.Keyword, "FOR");
        var forColumn = ParsePrimary();
        Expect(TokenType.Keyword, "IN");
        Expect(TokenType.LeftParen);

        var node = new PivotNode { Kind = kw.Value.ToLowerInvariant(), Head = head, ForColumn = forColumn,
                                   HeadComment = headComment };
        node.LeadingComments.AddRange(leading);
        while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
        {
            node.InValues.Add(ParseExpression());
            if (PeekIs(TokenType.Comma)) Advance();
            else break;
        }
        Expect(TokenType.RightParen);   // closes IN (
        node.InComment = TryTakeSameLineInlineComment();
        Expect(TokenType.RightParen);   // closes PIVOT (

        if (Peek().IsKeyword("AS")) { Advance(); node.Alias = Advance(); }
        else if (Peek().Type is TokenType.Identifier or TokenType.QuotedIdentifier
                 && !IsJoinKeyword() && !IsSelectClauseKeyword() && !IsGoKeyword())
            node.Alias = Advance();
        return node;
    }

    private bool IsJoinKeyword()
    {
        var t = Peek();
        return t.IsKeyword("INNER") || t.IsKeyword("LEFT") || t.IsKeyword("RIGHT")
            || t.IsKeyword("FULL") || t.IsKeyword("CROSS") || t.IsKeyword("JOIN")
            || t.IsKeyword("OUTER") // removed for LEFT/RIGHT/FULL OUTER JOIN
            || t.IsKeyword("APPLY"); // CROSS APPLY / OUTER APPLY
    }

    private JoinNode ParseJoin()
    {
        var parts = new System.Text.StringBuilder();
        while (IsJoinKeyword()) { if (parts.Length > 0) parts.Append(' '); parts.Append(Advance().Value.ToLowerInvariant()); }
        // Rule: remove OUTER only when it precedes JOIN (not APPLY)
        var jt = System.Text.RegularExpressions.Regex.Replace(parts.ToString(), @"\bouter\s+join\b", "join");
        jt = System.Text.RegularExpressions.Regex.Replace(jt, @"\s+", " ").Trim();
        if (jt == "join") jt = "inner join";
        parts.Clear(); parts.Append(jt);
        var table = ParseTableRef();
        // A comment on the join's line ("left join … as ug  --создал процесс") stays on that line.
        // It must not hide the ON that follows: looked at with a plain Peek, the join came out
        // condition-less and the rest of the statement fell apart.
        var trailing = TryTakeSameLineInlineComment();
        var conditions = new List<AstNode>();
        if (PeekPastComments().IsKeyword("ON"))
        {
            var beforeOn = CollectStandaloneComments();
            Advance(); // on
            conditions.AddRange(ParseConditionList(isJoinOn: true));
            // Comments between the join line and ON belong above the join's conditions.
            if (beforeOn.Count > 0 && conditions.Count > 0 && conditions[0] is ConditionNode first)
                first.LeadingComments.InsertRange(0, beforeOn);
        }
        return new JoinNode { JoinType = parts.ToString(), Table = table, TrailingComment = trailing }
            .Tap(n => n.Conditions.AddRange(conditions));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  WHERE / condition lists
    // ═══════════════════════════════════════════════════════════════════════════

    private List<AstNode> ParseConditionList(bool isJoinOn)
    {
        var list = new List<AstNode>();
        while (!IsAtEnd())
        {
            int save = _pos;
            // Standalone comments (own-line -- / block) before the next condition. Captured
            // here so they never reach ParsePrimary (which would treat them as an operand).
            var leading = new List<string>();
            while (PeekIs(TokenType.LineComment) || PeekIs(TokenType.BlockComment))
                leading.Add(Advance().Value);

            string? logicalOp = null;
            if (Peek().IsKeyword("AND")) { logicalOp = "and"; Advance(); }
            else if (Peek().IsKeyword("OR")) { logicalOp = "or"; Advance(); }
            // Comments between the operator and the condition also lead the condition.
            while (PeekIs(TokenType.LineComment) || PeekIs(TokenType.BlockComment))
                leading.Add(Advance().Value);

            // No actual condition follows these comments/operator — rewind so the caller
            // (or the graceful fallback) handles the leftover rather than us mis-parsing it.
            if (IsAtEnd() || IsConditionTerminator(isJoinOn)) { _pos = save; break; }

            // Conditions chain with and/or. A follow-up token without an operator is not a
            // condition — it's the next statement ("where x = 1 \n open cur") and must not be
            // swallowed into the WHERE list.
            if (list.Count > 0 && logicalOp == null) { _pos = save; break; }

            var expr = ParseExpression();
            // Inline comment on the same line (block or line) trails this condition.
            string? condComment = TryTakeSameLineInlineComment();
            list.Add(new ConditionNode { LogicalOp = logicalOp, Expression = expr, TrailingComment = condComment }
                .Tap(c => c.LeadingComments.AddRange(leading)));
        }
        return list;
    }

    private bool IsConditionTerminator(bool isJoinOn)
    {
        var t = Peek();
        bool common = t.IsKeyword("GROUP") || t.IsKeyword("ORDER") || t.IsKeyword("HAVING")
            || t.IsKeyword("UNION") || t.IsKeyword("EXCEPT") || t.IsKeyword("INTERSECT")
            || t.IsKeyword("OPTION") // OPTION (...) query hint ends the WHERE list
            || t.IsKeyword("END")    // terminates inside BEGIN blocks
            || t.IsKeyword("SELECT") || t.IsKeyword("INSERT") || t.IsKeyword("UPDATE")
            || t.IsKeyword("DELETE") || t.IsKeyword("CREATE") || t.IsKeyword("DROP")
            || t.IsKeyword("BEGIN")  || t.Type == TokenType.DeclareKeyword
            // control-flow keywords end an IF/WHILE condition (the body follows)
            || t.IsKeyword("RETURN") || t.IsKeyword("SET")  || t.IsKeyword("WHILE")
            || t.IsKeyword("IF")     || t.IsKeyword("ELSE") || t.IsKeyword("PRINT")
            || t.IsKeyword("EXEC")   || t.IsKeyword("EXECUTE")
            || t.IsKeyword("COMMIT") || t.IsKeyword("ROLLBACK")
            || t.Type == TokenType.EndOfFile || t.Type == TokenType.RightParen
            || t.Type == TokenType.Semicolon || IsGoKeyword();
        if (isJoinOn) return common || t.IsKeyword("WHERE") || IsJoinKeyword();
        return common;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Expressions
    // ═══════════════════════════════════════════════════════════════════════════

    private AstNode ParseExpression() => ParseComparison();

    private AstNode ParseComparison()
    {
        // Prefix boolean NOT: "not exists (...)" and "not (condition group)" must each parse
        // as a SINGLE node, otherwise a condition list splits "not" off from its operand.
        if (Peek().IsKeyword("NOT"))
        {
            var nxt = PeekAt(1);
            if (nxt.IsKeyword("EXISTS"))
            {
                Advance(); // not
                Advance(); // exists
                Expect(TokenType.LeftParen);
                var openC = TryTakeSameLineInlineComment();
                var sub = ParseSelectOrSet(null);
                var closing = CollectStandaloneComments();
                Expect(TokenType.RightParen);
                return new FunctionCallNode { Name = "EXISTS", IsKeywordFunction = true, Negated = true }
                    .Tap(n => n.Arguments.Add(new SubQueryNode { Select = sub, OpenComment = openC }
                        .Tap(q => q.CloseComments.AddRange(closing))));
            }
            if (nxt.Type == TokenType.LeftParen)
            {
                Advance(); // not
                return new NotExprNode { Inner = ParsePrimary() };
            }
        }

        var left = ParseAdditive();
        var op = Peek();
        if (op.Type is TokenType.Equals or TokenType.NotEquals or TokenType.LessThan
            or TokenType.GreaterThan or TokenType.LessOrEqual or TokenType.GreaterOrEqual)
        { Advance(); return new BinaryExprNode { Left = left, Op = op, Right = ParseAdditive() }; }
        if (op.IsKeyword("IS"))
        {
            Advance(); bool notNull = false;
            if (Peek().IsKeyword("NOT")) { notNull = true; Advance(); }
            Expect(TokenType.Keyword, "NULL");
            return new IsNullExprNode { Left = left, IsNotNull = notNull };
        }
        if (op.IsKeyword("NOT"))
        {
            var next = PeekAt(1);
            if (next.IsKeyword("IN"))   { Advance(); Advance(); Expect(TokenType.LeftParen);
                if (PeekPastComments().IsKeyword("SELECT")) { var openC = TryTakeSameLineInlineComment(); var sub = ParseSelectOrSet(null); Expect(TokenType.RightParen); return new InExprNode { Left = left, Negated = true, SubQuery = new SubQueryNode { Select = sub, OpenComment = openC } }; }
                var v = ParseInList(); Expect(TokenType.RightParen); return new InExprNode { Left = left, Negated = true }.Tap(n => n.Values.AddRange(v)); }
            // The NOT belongs to the operator — losing it inverts the condition.
            if (next.IsKeyword("LIKE")) { Advance(); Advance(); return new LikeExprNode { Left = left, Pattern = ParseAdditive(), Negated = true }; }
            if (next.IsKeyword("BETWEEN")) { Advance(); Advance(); var lo = ParseAdditive(); Expect(TokenType.Keyword, "AND"); var hi = ParseAdditive(); return new BetweenExprNode { Left = left, Low = lo, High = hi, Negated = true }; }
        }
        if (op.IsKeyword("IN"))     { Advance(); Expect(TokenType.LeftParen);
            if (PeekPastComments().IsKeyword("SELECT")) { var openC = TryTakeSameLineInlineComment(); var sub = ParseSelectOrSet(null); Expect(TokenType.RightParen); return new InExprNode { Left = left, SubQuery = new SubQueryNode { Select = sub, OpenComment = openC } }; }
            var v = ParseInList(); Expect(TokenType.RightParen); return new InExprNode { Left = left }.Tap(n => n.Values.AddRange(v)); }
        if (op.IsKeyword("LIKE"))   { Advance(); return new LikeExprNode { Left = left, Pattern = ParseAdditive() }; }
        if (op.IsKeyword("BETWEEN")){ Advance(); var lo = ParseAdditive(); Expect(TokenType.Keyword, "AND"); var hi = ParseAdditive(); return new BetweenExprNode { Left = left, Low = lo, High = hi }; }
        return left;
    }

    /// <summary>Handles +, -, string concatenation, and bitwise &amp; | ^ (same precedence in T-SQL).</summary>
    private AstNode ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (true)
        {
            int beforeComments = _pos, parked = _pendingComments.Count;
            var comment = TryTakeInlineBlockComment();
            var op = Peek();
            if (op.Type is not (TokenType.Plus or TokenType.Minus or TokenType.BitwiseOp))
            { Rewind(beforeComments, parked); break; }
            // Don't consume Minus that could start a negative number literal in a list context
            Advance();
            var right = ParseMultiplicative();
            left = new BinaryExprNode { Left = left, Op = op, Right = right, OpLeadingComment = comment };
        }
        return left;
    }

    private AstNode ParseMultiplicative()
    {
        var left = AttachGluedBlockComment(ParsePrimary());
        left = ApplyCollate(left);
        while (true)
        {
            int beforeComments = _pos, parked = _pendingComments.Count;
            var comment = TryTakeInlineBlockComment();
            var op = Peek();
            if (op.Type is not (TokenType.Multiply or TokenType.Divide or TokenType.Percent))
            { Rewind(beforeComments, parked); break; }
            Advance();
            var right = ApplyCollate(AttachGluedBlockComment(ParsePrimary()));
            left = new BinaryExprNode { Left = left, Op = op, Right = right, OpLeadingComment = comment };
        }
        return left;
    }

    /// <summary>Applies a trailing COLLATE clause to an expression: expr COLLATE Collation_Name.</summary>
    private AstNode ApplyCollate(AstNode expr)
    {
        if (Peek().IsKeyword("COLLATE"))
        {
            var op = Advance();                 // COLLATE
            var collation = Advance();           // collation name (identifier)
            return new BinaryExprNode { Left = expr, Op = op, Right = new ColumnRefNode().Tap(c => c.Parts.Add(collation)) };
        }
        return expr;
    }

    /// <summary>
    /// Consumes the ')' closing a parenthesised condition. When it is missing and what follows
    /// can only start a new statement (or the input ends), the group is closed here rather than
    /// failing the parse: "while (a <= b select 20" is a forgotten paren, and closing it before
    /// the body is the only reading that makes the script valid. The paren is then printed, so
    /// the result is valid T-SQL instead of the unformatted broken original.
    /// </summary>
    private void ExpectGroupClose()
    {
        if (PeekIs(TokenType.RightParen)) { Advance(); return; }
        // Only when a real statement follows. At the end of the input the text is not broken,
        // it is merely CUT SHORT — a partial selection whose rest lives outside it — and
        // inventing a paren there would change a script the user can still see the whole of.
        var next = Peek();
        if (next.Type is not (TokenType.EndOfFile or TokenType.Semicolon) && IsStatementBoundary(next))
            return;
        Expect(TokenType.RightParen);
    }

    private AstNode ParsePrimary()
    {
        var tok = Peek();

        // Parenthesised expression or subquery
        if (tok.Type == TokenType.LeftParen)
        {
            Advance();
            // Comments right after '(' — "not (  --note" — are not operands: the same-line one
            // stays on the paren's line, the ones below lead the first condition. Without this
            // they reached ParsePrimary and aborted the parse of the whole script.
            string? openComment = TryTakeSameLineInlineComment();
            if (PeekPastComments().IsKeyword("SELECT"))
            {
                var sub = ParseSelectOrSet(null);
                var closing = CollectStandaloneComments();
                Expect(TokenType.RightParen);
                return new SubQueryNode { Select = sub, OpenComment = openComment }
                    .Tap(n => n.CloseComments.AddRange(closing));
            }
            var leading = CollectStandaloneComments();
            var inner = ParseExpression();
            var innerComment = TryTakeSameLineInlineComment();
            // A boolean operator makes this a condition group: ( a = 1 or b = 2 ). Comments do
            // too — the group's layout is the only one that can give them their own line.
            if (Peek().IsKeyword("AND") || Peek().IsKeyword("OR")
                || openComment != null || leading.Count > 0 || innerComment != null)
            {
                var group = new ConditionGroupNode { OpenComment = openComment };
                group.Conditions.Add(new ConditionNode { Expression = inner, TrailingComment = innerComment }
                    .Tap(c => c.LeadingComments.AddRange(leading)));
                while (Peek().IsKeyword("AND") || Peek().IsKeyword("OR"))
                {
                    string op = Peek().IsKeyword("AND") ? "and" : "or";
                    Advance();
                    var nextLeading = CollectStandaloneComments();
                    var next = ParseExpression();
                    var nextComment = TryTakeSameLineInlineComment();
                    group.Conditions.Add(new ConditionNode { LogicalOp = op, Expression = next, TrailingComment = nextComment }
                        .Tap(c => c.LeadingComments.AddRange(nextLeading)));
                }
                ExpectGroupClose();
                return group;
            }
            ExpectGroupClose();
            return new ParenExprNode { Inner = inner };
        }

        if (tok.IsKeyword("CASE")) return ParseCase();
        if (IsCastFunction(tok))   return ParseCastFunction();

        // EXISTS (subquery) — must come before general function-call detection
        // because exists( would otherwise be intercepted by ParseFunctionCall()
        if (tok.IsKeyword("EXISTS"))
        {
            Advance(); // exists
            Expect(TokenType.LeftParen);
            var openC = TryTakeSameLineInlineComment();
            var sub = ParseSelectOrSet(null);
            var closingComments = CollectStandaloneComments();
            Expect(TokenType.RightParen);
            return new FunctionCallNode { Name = "EXISTS", IsKeywordFunction = true }
                .Tap(n => n.Arguments.Add(new SubQueryNode { Select = sub, OpenComment = openC }
                    .Tap(q => q.CloseComments.AddRange(closingComments))));
        }

        // Function call: identifier/keyword followed by (
        if (tok.Type is TokenType.Identifier or TokenType.Keyword)
            if (PeekAt(1).Type == TokenType.LeftParen) return ParseFunctionCall();

        // Column reference / identifier chain
        if (tok.Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Variable or TokenType.Keyword)
        {
            var col = new ColumnRefNode();
            col.Parts.Add(Advance());
            while (PeekPastComments().Type == TokenType.Dot)
            { ParkComment(); col.Parts.Add(Advance()); ParkComment(); col.Parts.Add(Advance()); }
            // Dotted / quoted function call: schema.fn(args), [db].[schema].[fn](args).
            // (A single-part name followed by '(' was already handled as a function above.)
            if (PeekIs(TokenType.LeftParen) && tok.Type != TokenType.Variable)
            {
                var fnName = string.Join("", col.Parts.Select(p => p.Value));
                return ParseFunctionCallBody(fnName, isKeyword: false);
            }
            col.TrailingComment = TryTakeSameLineComment();
            return col;
        }

        // A sign in front of an operand: "-1" folds into a negative literal, everything else
        // ("+14" in dateadd(day, +14, …), "-(a + b)") keeps the sign in front of the operand.
        // A leading '+' used to reach none of this and derailed the whole statement; the old
        // "-(expr) → 0 - expr" shape fabricated a zero the source never had, which the
        // faithfulness check then rejected, throwing the formatting away.
        if (tok.Type is TokenType.Minus or TokenType.Plus)
        {
            Advance();
            if (tok.Type == TokenType.Minus && PeekIs(TokenType.NumberLiteral))
                return new LiteralNode { Token = new Token(TokenType.NumberLiteral, "-" + Advance().Value) };
            return new UnaryExprNode { Op = tok, Operand = ParsePrimary() };
        }

        // Literals
        if (tok.Type is TokenType.StringLiteral or TokenType.NumberLiteral)
        { var lit = new LiteralNode { Token = Advance() }; lit.TrailingComment = TryTakeSameLineComment(); return lit; }
        // A comment is never an operand: a -- comment turned into a "value" would swallow the rest
        // of the line (real columns/conditions). Callers that can place a comment nicely capture it
        // before reaching here; anything left over is parked and lifted to the statement, so an
        // annotation in an odd spot moves a line up instead of costing the whole script its
        // formatting. Parsing then continues with the operand that follows.
        if (tok.Type == TokenType.BlockComment)
        {
            // A /* */ comment CAN stand in front of a value — commented-out code around an
            // operand is a common idiom ("between /*dateadd(month,-2,*/@from/*)*/ and @to").
            // It stays glued to the operand instead of being lifted above the statement.
            var before = Advance().Value;
            var operand = ParsePrimary();
            if (_hoistComments) { _pendingComments.Add(before); return operand; }
            return new InlineCommentedNode { Before = before, Inner = operand };
        }
        if (tok.Type == TokenType.LineComment)
        {
            _pendingComments.Add(Advance().Value);
            return ParsePrimary();
        }

        // Wildcard SELECT *
        if (tok.Type == TokenType.Multiply)
        { return new LiteralNode { Token = Advance() }; }

        return new LiteralNode { Token = Advance() };
    }

    private bool IsCastFunction(Token t) =>
        t.Value.Equals("CAST", StringComparison.OrdinalIgnoreCase) ||
        t.Value.Equals("TRY_CAST", StringComparison.OrdinalIgnoreCase) ||
        t.Value.Equals("CONVERT", StringComparison.OrdinalIgnoreCase) ||
        t.Value.Equals("TRY_CONVERT", StringComparison.OrdinalIgnoreCase);

    private FunctionCallNode ParseCastFunction()
    {
        var nameToken = Advance();
        Expect(TokenType.LeftParen);
        var args = new List<AstNode>();
        var argComments = new List<string?>();
        bool isConvert = nameToken.Value.Equals("CONVERT", StringComparison.OrdinalIgnoreCase)
                      || nameToken.Value.Equals("TRY_CONVERT", StringComparison.OrdinalIgnoreCase);
        if (isConvert)
        {
            args.Add(ParseDataType());
            argComments.Add(TryTakeInlineBlockComment());
            if (PeekIs(TokenType.Comma)) Advance();
            while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
            {
                var a = ParseExpression();
                args.Add(a);
                argComments.Add(TakeLineCommentFrom(a) ?? TryTakeInlineBlockComment());
                if (PeekIs(TokenType.Comma)) Advance(); else break;
            }
        }
        else
        {
            var value = ParseExpression();
            args.Add(value);
            argComments.Add(TakeLineCommentFrom(value) ?? TryTakeInlineBlockComment());
            if (Peek().IsKeyword("AS")) Advance();
            args.Add(ParseDataType());
            // "cast(x as decimal(18, 2) /* округление */)" — a comment before the closing paren
            // belongs to the type, not to whatever comes after the cast.
            argComments.Add(TryTakeInlineBlockComment());
        }
        Expect(TokenType.RightParen);
        return new FunctionCallNode { Name = nameToken.Value, IsKeywordFunction = nameToken.Type == TokenType.Keyword }
            .Tap(n => { n.Arguments.AddRange(args); n.ArgumentComments.AddRange(argComments); });
    }

    private AstNode ParseDataType()
    {
        // Keep every token, INCLUDING the parens and length/precision, so the type renders
        // as "varchar(10)" / "decimal(18, 2)" instead of losing its parens ("varchar10").
        var raw = new RawTokensNode();
        raw.Tokens.Add(Advance());                 // type name
        if (PeekIs(TokenType.LeftParen))
        {
            raw.Tokens.Add(Advance());             // (
            while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
            {
                raw.Tokens.Add(Advance());
                if (PeekIs(TokenType.Comma)) raw.Tokens.Add(Advance());
            }
            if (PeekIs(TokenType.RightParen)) raw.Tokens.Add(Advance());   // )
        }
        return raw;
    }

    /// <summary>
    /// Parses an IN value list. /* */ block comments are transparent and keep the list inline:
    /// a comment written BEFORE a value stays in front of that value ("in (/*11,*/54, …)"), one
    /// written right after a value stays glued to it. A -- line comment attaches to the preceding
    /// value as a CommentedValueNode, which breaks the list onto separate lines (rule 2.3.2).
    /// </summary>
    private List<AstNode> ParseInList()
    {
        var list = new List<AstNode>();
        string? pendingLead = null;   // /* */ comment(s) seen before the next value
        while (!IsAtEnd())
        {
            while (_pos < _tokens.Count && _tokens[_pos].Type is TokenType.Whitespace or TokenType.Newline)
                _pos++;
            if (_pos >= _tokens.Count) break;
            var t = _tokens[_pos].Type;
            if (t == TokenType.RightParen) break;
            if (t == TokenType.Comma)
            {
                _pos++;
                // A /* */ comment written after the comma and CLOSING its line annotates the value
                // just listed — it stays on that value's line. One with the next value still
                // behind it on the same line leads that value instead (pendingLead, below).
                TakeCommentAfterInComma(list);
                continue;
            }
            // A /* */ block comment before a value belongs in front of THAT value — including the
            // very first one, which used to have no preceding value to glue to and was dropped.
            if (t == TokenType.BlockComment)
            {
                pendingLead = (pendingLead ?? "") + _tokens[_pos++].Value;
                continue;
            }
            // A -- line comment between values attaches to the preceding value (breaks the list).
            if (t == TokenType.LineComment) { PromoteLastToCommented(list, _tokens[_pos++].Value); continue; }

            // Parse a value.
            var val = ParsePrimary();
            // ParsePrimary may have captured a same-line -- comment on the value itself.
            var lineComment = TakeLineCommentFrom(val);

            // A /* */ block comment after the value stays attached to it, inline — glued when the
            // author wrote it glued, a space out when he wrote a space.
            bool spaced = _pos < _tokens.Count && _tokens[_pos].Type == TokenType.Whitespace;
            while (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.Whitespace) _pos++;
            while (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.BlockComment)
            {
                val = WithTrailingBlockComment(val, _tokens[_pos++].Value, spaced);
                spaced = _pos < _tokens.Count && _tokens[_pos].Type == TokenType.Whitespace;
                while (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.Whitespace) _pos++;
            }

            AstNode item = val;
            if (pendingLead != null)
            {
                item = new InValueGroupNode { LeadingBlockComment = pendingLead }.Tap(g => g.Values.Add(val));
                pendingLead = null;
            }
            if (lineComment != null)
                item = new CommentedValueNode { Value = item, TrailingComment = lineComment };
            list.Add(item);
        }
        // A comment right before the closing paren has no value to lead — keep it on the last one,
        // or as the only content when the list holds nothing else.
        if (pendingLead != null)
        {
            if (list.Count > 0) GlueInListBlockComment(list, pendingLead);
            else                list.Add(new InValueGroupNode { LeadingBlockComment = pendingLead });
        }
        return list;
    }

    /// <summary>
    /// Takes the /* */ comment that stands right after an IN-list comma and ends its line, and
    /// keeps it with the value the comma closed. Without this it drifted down and glued itself in
    /// front of the NEXT value, taking that value's line break with it.
    /// </summary>
    private void TakeCommentAfterInComma(List<AstNode> list)
    {
        if (list.Count == 0) return;
        int i = _pos;
        while (i < _tokens.Count && _tokens[i].Type == TokenType.Whitespace) i++;
        if (i >= _tokens.Count || _tokens[i].Type != TokenType.BlockComment) return;
        int j = i + 1;
        while (j < _tokens.Count && _tokens[j].Type == TokenType.Whitespace) j++;
        if (j < _tokens.Count && _tokens[j].Type is not (TokenType.Newline or TokenType.EndOfFile)) return;

        var text = _tokens[i].Value;
        _pos = i + 1;
        if (_hoistComments) { _pendingComments.Add(text); return; }
        list[^1] = list[^1] is CommentedValueNode cv
            ? new CommentedValueNode { Value = cv.Value, TrailingComment = (cv.TrailingComment ?? "") + " " + text }
            : new CommentedValueNode { Value = list[^1], TrailingComment = text };
    }

    /// <summary>Glues a transparent /* */ block comment onto the last parsed IN value.</summary>
    private static void GlueInListBlockComment(List<AstNode> list, string blockComment)
    {
        if (list.Count == 0) return;   // comment inside an empty list — nothing to attach it to
        AttachGluedBlockComment(list[^1], blockComment);
    }

    /// <summary>Glues any /* */ block comment(s) on the SAME line (whitespace-only before them,
    /// no newline) onto the given expression node — transparent, kept where they were.</summary>
    private AstNode GlueSameLineBlockComments(AstNode expr)
    {
        while (true)
        {
            int j = _pos;
            while (j < _tokens.Count && _tokens[j].Type == TokenType.Whitespace) j++;
            if (j < _tokens.Count && _tokens[j].Type == TokenType.BlockComment)
            {
                expr = WithTrailingBlockComment(expr, _tokens[j].Value, spaced: j > _pos);
                _pos = j + 1;
            }
            else break;
        }
        return expr;
    }

    /// <summary>
    /// Attaches a /* */ comment written after a value to that value: glued when the author wrote
    /// no space in front of it, one space otherwise — the same rule the emitters apply everywhere
    /// else. Returns the node to use in place of <paramref name="val"/>.
    /// </summary>
    private static AstNode WithTrailingBlockComment(AstNode val, string comment, bool spaced)
    {
        if (!spaced) { AttachGluedBlockComment(val, comment); return val; }
        if (val is InlineCommentedNode ic && ic.After == null) { ic.After = " " + comment; return ic; }
        return new InlineCommentedNode { Inner = val, After = " " + comment };
    }

    /// <summary>Appends a glued /* */ block comment to a value node that can carry a trailing comment.</summary>
    private static void AttachGluedBlockComment(AstNode val, string blockComment)
    {
        switch (val)
        {
            case LiteralNode l:   l.TrailingComment = (l.TrailingComment ?? "") + blockComment; break;
            case ColumnRefNode c: c.TrailingComment = (c.TrailingComment ?? "") + blockComment; break;
            // Wrappers carry the comment down to the value they wrap.
            case CommentedValueNode cv:                       AttachGluedBlockComment(cv.Value,     blockComment); break;
            case InValueGroupNode g when g.Values.Count > 0:   AttachGluedBlockComment(g.Values[^1], blockComment); break;
            // other value kinds have no comment slot — the transparent comment is dropped (rare)
        }
    }

    /// <summary>Extracts and clears a -- line comment captured on a value node (block comments stay).</summary>
    private static string? TakeLineCommentFrom(AstNode val)
    {
        if (val is LiteralNode l && l.TrailingComment != null && l.TrailingComment.StartsWith("--"))
        { var s = l.TrailingComment; l.TrailingComment = null; return s; }
        if (val is ColumnRefNode c && c.TrailingComment != null && c.TrailingComment.StartsWith("--"))
        { var s = c.TrailingComment; c.TrailingComment = null; return s; }
        // The comment sits on the LAST operand of a compound expression ("ol.qty + 1 -- note"):
        // it still closes the whole line, so it belongs to the item, not to that operand.
        if (val is BinaryExprNode b)   return TakeLineCommentFrom(b.Right);
        if (val is UnaryExprNode u)    return TakeLineCommentFrom(u.Operand);
        return null;
    }

    /// <summary>Attaches a -- line comment to the last IN value, wrapping it in a CommentedValueNode.</summary>
    private static void PromoteLastToCommented(List<AstNode> list, string lineComment)
    {
        if (list.Count == 0) return;
        list[^1] = list[^1] is CommentedValueNode cv
            ? new CommentedValueNode { Value = cv.Value, TrailingComment = (cv.TrailingComment ?? "") + " " + lineComment }
            : new CommentedValueNode { Value = list[^1], TrailingComment = lineComment };
    }

    private CaseExprNode ParseCase()
    {
        Expect(TokenType.Keyword, "CASE");
        // "case -- начало CASE" — a comment on the case line is the case's own. Read as an input
        // expression (the "case x when …" form), it broke the parse and the script went
        // unformatted.
        var headerComment = TryTakeSameLineInlineComment();
        AstNode? inputExpr = null;
        if (!PeekPastComments().IsKeyword("WHEN")) inputExpr = ParsePrimary();
        var whens = new List<WhenClauseNode>();
        AstNode? elseExpr = null; string? elseComment = null;
        var tailComments = new List<string>();
        while (true)
        {
            // Standalone comments between branches stay on their own lines above their WHEN.
            var leading = CollectStandaloneComments();
            if (!Peek().IsKeyword("WHEN")) { tailComments.AddRange(leading); break; }
            Advance();
            // Conditions chain with and/or; each keeps its operator so the emitter can
            // render "or" continuations too (previously OR failed the whole parse).
            var conds = new List<AstNode> { new ConditionNode { Expression = ParseExpression() } };
            while (PeekPastComments().IsKeyword("AND") || PeekPastComments().IsKeyword("OR"))
            {
                string op = PeekPastComments().IsKeyword("AND") ? "and" : "or";
                CollectStandaloneComments();
                Advance();
                conds.Add(new ConditionNode { LogicalOp = op, Expression = ParseExpression() });
            }
            // A comment between the condition and THEN closes the when line — THEN goes on the
            // next line anyway, so even a -- comment is safe there.
            var conditionComment = TryTakeSameLineInlineComment();
            Expect(TokenType.Keyword, "THEN");
            // Only a /* */ comment can stand in front of the value: a -- one would comment it out.
            var thenLeading = TryTakeInlineBlockComment();
            var then = ParseExpression();
            // Only a comment on the SAME line belongs to this branch. Taking whatever line comment
            // came next swallowed the standalone comment that annotated the FOLLOWING branch.
            string? tc = TakeLineCommentFrom(then) ?? TryTakeSameLineComment();
            whens.Add(new WhenClauseNode { Then = then, ThenComment = tc,
                                           ConditionComment = conditionComment,
                                           ThenLeadingComment = thenLeading }
                .Tap(w => { w.Conditions.AddRange(conds); w.LeadingComments.AddRange(leading); }));
        }
        var elseLeading = new List<string>(tailComments);
        tailComments.Clear();
        if (Peek().IsKeyword("ELSE"))
        {
            Advance(); elseExpr = ParseExpression();
            elseComment = TakeLineCommentFrom(elseExpr) ?? TryTakeSameLineComment();
            tailComments.AddRange(CollectStandaloneComments());
        }
        else { tailComments.AddRange(elseLeading); elseLeading.Clear(); }
        Expect(TokenType.Keyword, "END");
        return new CaseExprNode { InputExpr = inputExpr, ElseExpr = elseExpr, ElseComment = elseComment,
                                  HeaderComment = headerComment }
            .Tap(n => { n.WhenClauses.AddRange(whens);
                        n.ElseLeadingComments.AddRange(elseLeading);
                        n.EndLeadingComments.AddRange(tailComments); });
    }

    private FunctionCallNode ParseFunctionCall()
    {
        var nameToken = Advance(); // name (single token; '(' is next)
        return ParseFunctionCallBody(nameToken.Value, nameToken.Type == TokenType.Keyword);
    }

    /// <summary>
    /// Parses a function call's '(' argument list ')' and optional OVER clause. The name may
    /// be multi-part (e.g. schema.fn) and is supplied by the caller; the current token is '('.
    /// </summary>
    private FunctionCallNode ParseFunctionCallBody(string name, bool isKeyword)
    {
        Advance(); // (
        // Optional set quantifier in aggregates: count(distinct x), sum(all y)
        string? setQuantifier = null;
        if (Peek().IsKeyword("DISTINCT")) { Advance(); setQuantifier = "distinct"; }
        else if (Peek().IsKeyword("ALL")) { Advance(); setQuantifier = "all"; }
        var args = new List<AstNode>();
        var argComments = new List<string?>();
        while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
        {
            // Handle OVER (...) for window functions
            if (Peek().IsKeyword("OVER")) { Advance(); args.Add(ParseWindowSpec()); argComments.Add(null); continue; }
            var arg = ParseBooleanArgument();
            // An argument may carry a comment of its own ("o.note,   -- запасной"). Lifted to the
            // argument list, it renders after the comma instead of being hoisted above the whole
            // statement, and the call breaks across lines so nothing hides behind a -- comment.
            string? argComment = TakeLineCommentFrom(arg);
            // The comment can stand on either side of the separating comma:
            // "iif(x > 1 /* порог */, 'a', 'b')" and "coalesce(a,   -- основной".
            argComment ??= TryTakeInlineBlockComment();
            bool sawComma = PeekIs(TokenType.Comma);
            if (sawComma) Advance();
            argComment ??= TryTakeSameLineInlineComment();
            args.Add(arg);
            argComments.Add(argComment);
            if (!sawComma) break;
        }
        Expect(TokenType.RightParen);
        // Window function: OVER clause may follow the closing paren
        AstNode? overClause = null;
        if (Peek().IsKeyword("OVER")) { Advance(); overClause = ParseWindowSpec(); }
        var fn = new FunctionCallNode { Name = name, IsKeywordFunction = isKeyword, OverClause = overClause, SetQuantifier = setQuantifier };
        fn.Arguments.AddRange(args);
        fn.ArgumentComments.AddRange(argComments);
        return fn;
    }

    /// <summary>
    /// Parses one function argument. An argument can be a whole condition — "iif(a = 1 and b, x, y)",
    /// "choose(…)", "isnull((… or …) and …, 0)" — while the expression parser stops before AND/OR,
    /// which normally belong to a condition list. The chain is folded into the expression here, so
    /// the argument list is not cut short at the first "and" (which used to abort the whole parse).
    /// </summary>
    private AstNode ParseBooleanArgument()
    {
        var expr = ParseExpression();
        while (Peek().IsKeyword("AND") || Peek().IsKeyword("OR"))
        {
            var op = Advance();
            expr = new BinaryExprNode { Left = expr, Op = op, Right = ParseExpression() };
        }
        return expr;
    }

    /// <summary>
    /// OVER (PARTITION BY … ORDER BY … [frame]). The two lists are parsed like any other
    /// expression / order-by list so each item can be given its own line; a frame clause
    /// (ROWS/RANGE …) has no structure worth breaking up and is kept as raw tokens.
    /// </summary>
    private AstNode ParseWindowSpec()
    {
        Expect(TokenType.LeftParen);
        var spec = new WindowSpecNode();

        // A comment written on its own line after "over (" stays on its own line; peeked past,
        // it hid the PARTITION/ORDER behind it and the whole clause fell into the raw frame.
        if (PeekPastComments().IsKeyword("PARTITION") || PeekPastComments().IsKeyword("ORDER"))
            spec.LeadingComments.AddRange(CollectStandaloneComments());

        if (Peek().IsKeyword("PARTITION"))
        {
            Advance();
            Expect(TokenType.Keyword, "BY");
            spec.PartitionBy.AddRange(ParseExpressionList());
        }
        if (Peek().IsKeyword("ORDER"))
        {
            Advance();
            Expect(TokenType.Keyword, "BY");
            spec.OrderBy.AddRange(ParseOrderByList());
        }

        int depth = 1;
        while (!IsAtEnd() && depth > 0)
        {
            var t = PeekRaw();
            if (t.Type == TokenType.LeftParen)  depth++;
            if (t.Type == TokenType.RightParen) { depth--; if (depth == 0) { AdvanceRaw(); break; } }
            spec.Frame.Add(AdvanceRaw());
        }
        return spec;
    }

    private List<AstNode> ParseExpressionList()
    {
        var list = new List<AstNode>();
        while (!IsAtEnd() && !IsSelectClauseKeyword())
        {
            if (!TryTakeItemLeadingComments(out var leading)) break;
            var expr = ParseExpression();
            if (Peek().IsKeyword("ASC") || Peek().IsKeyword("DESC")) Advance();
            // A /* */ block comment on the same line stays glued to the item (transparent),
            // instead of migrating to its own line as a standalone comment.
            expr = GlueSameLineBlockComments(expr);
            list.Add(WithLeadingComments(expr, leading));
            if (PeekIs(TokenType.Comma)) { Advance(); continue; } break;
        }
        return list;
    }

    /// <summary>Parses ORDER BY items, preserving optional ASC/DESC direction.</summary>
    private List<AstNode> ParseOrderByList()
    {
        var list = new List<AstNode>();
        while (!IsAtEnd() && !IsSelectClauseKeyword())
        {
            if (!TryTakeItemLeadingComments(out var leading)) break;
            var expr = ParseExpression();
            string? dir = null;
            if (Peek().IsKeyword("ASC"))  { Advance(); dir = "asc";  }
            else if (Peek().IsKeyword("DESC")) { Advance(); dir = "desc"; }
            var item = new OrderByItemNode { Expression = expr, Direction = dir };
            // A comment on the item's own line stays with it. Inside an OVER (…) it used to fall
            // through into the frame clause and come out on a line of its own.
            item.TrailingComment = TakeLineCommentFrom(expr);
            list.Add(WithLeadingComments(item, leading));
            if (PeekIs(TokenType.Comma)) { Advance(); item.TrailingComment ??= TryTakeSameLineInlineComment(); continue; }
            item.TrailingComment ??= TryTakeSameLineInlineComment();
            break;
        }
        return list;
    }

    /// <summary>
    /// Consumes the standalone comments that precede the next GROUP BY / ORDER BY item. Returns
    /// false (having consumed nothing) when the comments are not followed by an item at all — they
    /// belong to whatever ends the list, not to this one.
    /// </summary>
    private bool TryTakeItemLeadingComments(out List<string> leading)
    {
        int before = _pos;
        leading = CollectStandaloneComments();
        if (leading.Count > 0 && (IsAtEnd() || IsSelectClauseKeyword()))
        {
            _pos = before;
            return false;
        }
        return true;
    }

    /// <summary>Wraps a list item in a ListItemNode when it carries comments above it.</summary>
    private static AstNode WithLeadingComments(AstNode item, List<string> leading) =>
        leading.Count == 0
            ? item
            : new ListItemNode { Expression = item }.Tap(n => n.LeadingComments.AddRange(leading));


    // ═══════════════════════════════════════════════════════════════════════════
    //  BEGIN / END  (rule 2.8)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <param name="label">"try" or "catch" for BEGIN TRY / BEGIN CATCH, which are closed by
    /// END TRY / END CATCH; null for a plain BEGIN … END block.</param>
    private BeginEndNode ParseBeginEnd(string? label = null)
    {
        Advance(); // BEGIN
        if (label != null) Advance(); // TRY / CATCH
        var node = new BeginEndNode { Label = label };
        while (!IsAtEnd() && !Peek().IsKeyword("END"))
        {
            int before = _pos;
            var stmt = ParseStatement();
            if (stmt != null)
            {
                stmt.HoistedComments.AddRange(DrainPendingComments());
                // A ';' ending the statement stays with it, inside a block exactly as at script
                // level; then the same-line trailing comment, after the ';'.
                if (stmt is not RawTokensNode) stmt.TrailingSemicolon |= TryTakeSameLineSemicolon();
                // Keep a same-line trailing comment attached to this statement's last line —
                // a /* */ one just as much as a -- one.
                var trailing = TryTakeSameLineInlineComment();
                if (trailing != null) stmt.StatementTrailingComment = trailing;
                node.Body.Add(stmt);
            }
            // Forward-progress guarantee: if ParseStatement consumed nothing (e.g. a stray GO
            // inside the block that no sub-parser will take), stop instead of spinning forever.
            if (_pos == before) break;
        }
        // A BEGIN with no matching END must NOT fabricate one — an invented "end" token
        // breaks the script. Bail out so the source is returned unchanged.
        if (!Peek().IsKeyword("END"))
            throw new ParseException($"BEGIN block has no matching END (unexpected [{Peek().Type}] '{Peek().Value}').");
        Advance(); // END
        if (label != null)
        {
            if (!Peek().IsKeyword(label))
                throw new ParseException($"BEGIN {label} block is closed by END without '{label}'.");
            Advance(); // TRY / CATCH
        }
        return node;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CREATE TABLE  (rule 2.14)
    // ═══════════════════════════════════════════════════════════════════════════

    private AstNode ParseCreate()
    {
        Advance(); // CREATE
        if (Peek().IsKeyword("TABLE")) return ParseCreateTable();
        // Unknown CREATE → raw fallback
        return ParseRawStatement();
    }

    private CreateTableNode ParseCreateTable()
    {
        Advance(); // TABLE
        // Collect table name (might be schema.table or #temp)
        var nameTokens = new System.Text.StringBuilder();
        var nameComments = new List<string>();
        while (!IsAtEnd() && !PeekIs(TokenType.LeftParen) && !IsGoKeyword()
               && Peek().Type != TokenType.EndOfFile)
        {
            // A comment between the name and the column list ("create table t -- note\n(") is not
            // part of the name — appended to it, it made the whole statement unparsable.
            if (PeekIs(TokenType.LineComment) || PeekIs(TokenType.BlockComment))
            { nameComments.Add(Advance().Value); continue; }
            nameTokens.Append(Advance().Value);
        }
        var tableName = nameTokens.ToString().Trim();
        var node = new CreateTableNode { TableName = tableName };
        if (nameComments.Count > 0)
            node.NameComment = string.Join(" ", nameComments.Select(CommentText.AsInline));

        if (!PeekIs(TokenType.LeftParen)) return node;
        Advance(); // outer (
        ParseColumnDefs(node.Columns, node.CloseComments);
        return node;
    }

    /// <summary>
    /// Reads a parenthesised column definition list — the body of CREATE TABLE and of a table
    /// variable's type — from just after the '(' through the matching ')'. Comments are kept with
    /// the column they annotate instead of being glued into the next column's definition, which
    /// left such a script unformatted.
    /// </summary>
    /// <summary>
    /// True when nothing but the separating comma or the list's closing paren stands behind the
    /// comment(s) at the current position: the comment then belongs AFTER the column definition,
    /// not inside it. Inside a nested paren ("decimal(18, /*x*/ 2)") it is never the end.
    /// </summary>
    private bool ColumnDefEndsAfterComments(int depth)
    {
        if (depth != 0) return false;
        int j = _pos;
        while (j < _tokens.Count && (Skippable.Contains(_tokens[j].Type)
               || _tokens[j].Type is TokenType.LineComment or TokenType.BlockComment)) j++;
        return j >= _tokens.Count
            || _tokens[j].Type is TokenType.Comma or TokenType.RightParen or TokenType.EndOfFile;
    }

    private void ParseColumnDefs(List<ColumnDefNode> columns, List<string>? closeComments = null)
    {
        while (!IsAtEnd())
        {
            // Comments standing on their own line(s) before a column belong above it.
            var leading = CollectStandaloneComments();
            // Skip whitespace/newlines between column definitions
            while (!IsAtEnd() && _tokens[_pos].Type is TokenType.Whitespace or TokenType.Newline)
                _pos++;
            // Raw check: stop at outer )
            if (_pos >= _tokens.Count || _tokens[_pos].Type == TokenType.RightParen)
            {
                // A comment left hanging after the last column keeps its own line above the
                // closing paren; with nowhere to render it, it is parked above the statement.
                if (closeComments != null) closeComments.AddRange(leading);
                else _pendingComments.AddRange(leading);
                break;
            }

            // Column name (next non-whitespace token). A keyword in that slot is not a name but
            // the start of a table constraint ("constraint PK_… primary key …"), so it is
            // lowercased like any other keyword; real names keep their case.
            var nameToken = Advance();
            var colName = nameToken.Type == TokenType.Keyword
                ? nameToken.Value.ToLowerInvariant() : nameToken.Value;

            // Collect definition: everything until a DEPTH-0 comma or closing )
            // Track paren depth so varchar(255) and decimal(18,2) are parsed whole.
            var defTokens = new System.Text.StringBuilder();
            int depth = 0;
            // Whether the source had whitespace before the token about to be appended. It only
            // decides the space before a '(' — "decimal(18, 2)" glues, "default (0)" does not.
            bool sawSpace = false;
            // Whether a line break stands between the definition and whatever ends it: a comment
            // on the NEXT line is not this column's trailing comment, it annotates what follows.
            bool sawNewline = false;
            while (!IsAtEnd())
            {
                var raw = _tokens[_pos];
                if (raw.Type is TokenType.Whitespace or TokenType.Newline)
                { sawSpace = true; sawNewline |= raw.Type == TokenType.Newline; _pos++; continue; }

                if (raw.Type == TokenType.Comma && depth == 0) break; // column separator
                // A comment with nothing but the comma / the closing paren behind it TRAILS the
                // column: it is taken below, so the comma can be emitted in front of it. One
                // written INSIDE the definition ("id int /*x*/ not null") stays inside it —
                // ending the definition there split the column in two and invented a comma
                // between the halves, which changed what the script means.
                bool isComment = raw.Type is TokenType.LineComment or TokenType.BlockComment;
                if (isComment && ColumnDefEndsAfterComments(depth)) break;
                if (raw.Type == TokenType.RightParen && depth == 0) break; // outer ) of the list

                // Separator: none before ',' or ')' and none right after '(', a space before '('
                // only where the author wrote one, a single space between everything else.
                bool first = defTokens.Length == 0;
                char last  = first ? ' ' : defTokens[defTokens.Length - 1];
                bool noSpace = first
                    || raw.Type is TokenType.Comma or TokenType.RightParen
                    || last == '('
                    // A dotted name is one word: "references dbo.Accounts(id)", never "dbo . Accounts".
                    || raw.Type == TokenType.Dot || last == '.'
                    || (raw.Type == TokenType.LeftParen && !sawSpace);
                if (!noSpace) defTokens.Append(' ');
                sawSpace = false;

                if (raw.Type == TokenType.LeftParen)  depth++;
                if (raw.Type == TokenType.RightParen) depth--;
                // Type/constraint keywords (int, varchar, not, null, default ...) lowercased.
                // A comment inside the definition renders as a /* */ one: code follows it on the
                // same line, and a -- comment would swallow the rest of the column.
                defTokens.Append(isComment ? CommentText.AsInline(raw.Value)
                                 : raw.Type == TokenType.Keyword ? raw.Value.ToLowerInvariant() : raw.Value);
                sawNewline = false;
                _pos++;
            }

            // The comment can stand on either side of the separating comma — but only on the
            // column's own line.
            var comment = sawNewline ? null : TryTakeSameLineInlineComment();
            // Consume depth-0 comma separator between columns; a comment may follow it. The
            // comma can stand on the line BELOW the comment ("id int --note⏎, b int"); left
            // unconsumed there it opened the next column with a stray "," of its own.
            int afterDef = _pos;
            while (afterDef < _tokens.Count && Skippable.Contains(_tokens[afterDef].Type)) afterDef++;
            if (afterDef < _tokens.Count && _tokens[afterDef].Type == TokenType.Comma)
            { _pos = afterDef + 1; comment ??= TryTakeSameLineInlineComment(); }

            columns.Add(new ColumnDefNode
            {
                Name       = colName,
                Definition = defTokens.ToString().Trim(),
                TrailingComment = comment
            }.Tap(c => c.LeadingComments.AddRange(leading)));
        }
        // Consume outer )
        if (!IsAtEnd() && _tokens[_pos].Type == TokenType.RightParen) _pos++;
    }

    private AstNode ParseDrop()
    {
        var dropToken = Advance(); // DROP
        // A comment between DROP and TABLE must not hide the TABLE: read with Peek() alone it
        // sent "drop /*x*/ table #t" down the raw path and split the statement over three lines.
        var kindComments = new List<string>();
        if (PeekClause("TABLE", kindComments))
        {
            Advance(); // TABLE
            bool ifExists = false;
            // The same for a comment in front of IF EXISTS: hidden behind it, the IF read as a
            // statement of its own and "exists #t" failed to parse as its condition.
            var existsComments = new List<string>();
            if (PeekClause("IF", existsComments))
            {
                Advance();
                existsComments.AddRange(CollectStandaloneComments());
                Expect(TokenType.Keyword, "EXISTS");
                ifExists = true;
            }
            // Comments between TABLE / IF EXISTS and the name keep their place too.
            var nameComments = CollectStandaloneComments();
            var nameTokens = new System.Text.StringBuilder();
            // The name ends at the next statement — every boundary keyword, not just a few:
            // "drop table #t \n update t set …" used to glue the whole next statement into the
            // name ("#tupdatetseta=1"), and an "end" closing the enclosing block was swallowed
            // the same way, leaving the BEGIN without its END.
            while (!IsAtEnd() && !IsStatementBoundary(Peek())
                   // A comment is never part of the table name; stop so a same-line -- comment
                   // is picked up as the statement's trailing comment instead of glued in.
                   && Peek().Type != TokenType.LineComment && Peek().Type != TokenType.BlockComment)
                nameTokens.Append(Advance().Value);
            return new DropTableNode { IfExists = ifExists, TableName = nameTokens.ToString().Trim() }
                .Tap(d => { d.KindComments.AddRange(kindComments);
                            d.ExistsComments.AddRange(existsComments);
                            d.NameComments.AddRange(nameComments); });
        }
        // Any other DROP (function, procedure, view, index, trigger …) goes through the raw
        // emitter, which lowercases the keywords and leaves the name alone. The head of the
        // statement is handed to it as tokens already read: left out, the raw statement began at
        // "function", the word "drop" was lost, and the faithfulness gate handed the whole script
        // back unformatted.
        var seed = new List<Token> { dropToken };
        // The kind of object being dropped ("function", "procedure", "index", …).
        if (!IsAtEnd() && !IsStatementBoundary(Peek())
            && Peek().Type is TokenType.Keyword or TokenType.Identifier)
            seed.Add(Advance());
        // "drop function if exists dbo.f": the IF belongs to the DROP. Left in the stream it
        // started an IF statement and "exists dbo.f" failed to parse as its condition, which
        // cost the whole script its formatting.
        if (Peek().IsKeyword("IF"))
        {
            seed.Add(Advance());
            if (Peek().IsKeyword("EXISTS")) seed.Add(Advance());
        }
        return ParseRawStatement(seed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Raw fallback
    // ═══════════════════════════════════════════════════════════════════════════

    /// <param name="seed">Tokens already consumed that start this statement (the "drop function"
    /// of a "drop function …", say); they become the raw statement's first tokens.</param>
    private RawTokensNode ParseRawStatement(List<Token>? seed = null)
    {
        var raw = new RawTokensNode();
        if (seed != null) raw.Tokens.AddRange(seed);
        int parenDepth = 0;
        int caseDepth  = 0;
        while (!IsAtEnd())
        {
            var t = Peek();
            if (t.IsKeyword("SELECT") || t.IsKeyword("WITH") || t.IsKeyword("INSERT")
                || t.IsKeyword("UPDATE") || t.IsKeyword("DELETE") || t.IsKeyword("MERGE")
                || t.IsKeyword("DROP")  || t.IsKeyword("CREATE") || t.IsKeyword("BEGIN")
                || t.IsKeyword("IF")    || t.IsKeyword("WHILE")  || t.IsKeyword("RETURN")
                || t.IsKeyword("COMMIT")|| t.IsKeyword("ROLLBACK")
                || t.Type == TokenType.DeclareKeyword
                || t.Type == TokenType.EndOfFile || IsGoKeyword()) break;
            // A statement of its own also starts at SET / PRINT / EXEC and friends. Reading past
            // them ran two statements together on one line: "exec dbo.p @m = @mm set @mm = …".
            // Only outside parens — "exec dbo.p (…)" and a string argument keep their contents.
            if (parenDepth == 0 && raw.Tokens.Count > 0 && IsStatementStartKeyword(t)) break;
            // END closes the enclosing BEGIN block and ELSE opens the alternative branch of an
            // IF — a raw statement ("print 'x'", "exec …") must stop before them, otherwise the
            // block/branch structure is swallowed and the BEGIN loses its END. An END that
            // closes a CASE inside the statement is not a boundary, so CASE nesting is tracked.
            if (parenDepth == 0 && caseDepth == 0 && (t.IsKeyword("END") || t.IsKeyword("ELSE")))
                break;
            // A comment on a LATER line is standalone — it belongs to whatever follows, not to
            // this statement (otherwise "print 'x'" drags the next statement's comment into the
            // IF branch it sits in). A same-line comment stays with the statement.
            if (raw.Tokens.Count > 0 && t.Type is TokenType.LineComment or TokenType.BlockComment
                && PeekSameLine() == null)
                break;
            if (t.IsKeyword("CASE"))                        caseDepth++;
            else if (t.IsKeyword("END") && caseDepth > 0)    caseDepth--;
            else if (t.Type == TokenType.LeftParen)          parenDepth++;
            else if (t.Type == TokenType.RightParen)         parenDepth--;
            raw.Tokens.Add(Advance());
            // A ';' ends the statement and belongs to it. Reading past it ran a whole run of
            // "print 'a'; print 'b'; …" together into one statement on one line.
            if (parenDepth == 0 && t.Type == TokenType.Semicolon) break;
        }
        return raw;
    }

    /// <summary>
    /// Keywords that can only START a statement, so a raw statement ("exec …", "print …") has to
    /// stop in front of them. SET is the one that matters most: "exec dbo.p @m = @mm" followed by
    /// "set @mm = @mm + 1" is two statements, however the author spaced them.
    /// </summary>
    private static bool IsStatementStartKeyword(Token t) =>
        t.IsKeyword("SET") || t.IsKeyword("PRINT") || t.IsKeyword("EXEC") || t.IsKeyword("EXECUTE")
        || t.IsKeyword("RAISERROR") || t.IsKeyword("THROW") || t.IsKeyword("TRUNCATE")
        || t.IsKeyword("WAITFOR") || t.IsKeyword("GOTO") || t.IsKeyword("BREAK")
        || t.IsKeyword("CONTINUE");

    // ═══════════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private bool IsSelectClauseKeyword()
    {
        var t = Peek();
        return t.IsKeyword("FROM") || t.IsKeyword("WHERE") || t.IsKeyword("GROUP")
            || t.IsKeyword("ORDER") || t.IsKeyword("HAVING") || t.IsKeyword("UNION")
            || t.IsKeyword("EXCEPT") || t.IsKeyword("INTERSECT") || t.IsKeyword("SELECT")
            || t.IsKeyword("INTO")   // SELECT ... INTO #tbl — ends the column list
            || t.IsKeyword("OPTION") // OPTION (...) query hint — ends list clauses
            || t.IsKeyword("END")  // terminates a column list inside BEGIN/END
            // A new statement keyword ends the column list of an assignment SELECT that has
            // no FROM, e.g. "select @x = 1 \n declare ..." or "... \n exec (...)".
            || t.IsKeyword("INSERT") || t.IsKeyword("UPDATE") || t.IsKeyword("DELETE")
            || t.IsKeyword("CREATE") || t.IsKeyword("DROP")   || t.IsKeyword("BEGIN")
            || t.IsKeyword("EXEC")   || t.IsKeyword("EXECUTE")|| t.IsKeyword("IF")
            || t.IsKeyword("WHILE")  || t.IsKeyword("RETURN") || t.IsKeyword("PRINT")
            || t.IsKeyword("MERGE")  || t.IsKeyword("TRUNCATE")
            // ELSE starts the alternative branch of an IF: "if … select 1 else select 0" must
            // not take "else" as the alias of the last column.
            || t.IsKeyword("ELSE")
            || t.IsKeyword("SET")    || t.IsKeyword("COMMIT") || t.IsKeyword("ROLLBACK")
            || t.Type == TokenType.DeclareKeyword
            || t.Type == TokenType.EndOfFile || t.Type == TokenType.RightParen
            || t.Type == TokenType.Semicolon || IsGoKeyword();
    }

    /// <summary>True if the token starts a new statement (or ends the batch/subquery) rather
    /// than continuing the current SELECT — used to stop a column list at a real boundary.</summary>
    private bool IsStatementBoundary(Token t) =>
        t.Type == TokenType.DeclareKeyword
        || t.IsKeyword("SELECT") || t.IsKeyword("INSERT") || t.IsKeyword("UPDATE")
        || t.IsKeyword("DELETE") || t.IsKeyword("CREATE") || t.IsKeyword("DROP")
        || t.IsKeyword("BEGIN")  || t.IsKeyword("END")    || t.IsKeyword("EXEC")
        || t.IsKeyword("ELSE")
        || t.IsKeyword("EXECUTE")|| t.IsKeyword("IF")     || t.IsKeyword("WHILE")
        || t.IsKeyword("RETURN") || t.IsKeyword("PRINT")  || t.IsKeyword("MERGE")
        || t.IsKeyword("TRUNCATE") || t.IsKeyword("SET")
        || t.IsKeyword("COMMIT") || t.IsKeyword("ROLLBACK")
        || t.Type == TokenType.EndOfFile || t.Type == TokenType.Semicolon
        || (t.Type == TokenType.Identifier && t.Value.Equals("GO", StringComparison.OrdinalIgnoreCase));

    /// <summary>True if the token continues the current SELECT after its FROM/JOIN block
    /// (WHERE, GROUP BY, HAVING, ORDER BY, OPTION, UNION/EXCEPT/INTERSECT).</summary>
    private static bool IsSelectContinuation(Token t) =>
        t.IsKeyword("WHERE")  || t.IsKeyword("GROUP")  || t.IsKeyword("HAVING")
        || t.IsKeyword("ORDER") || t.IsKeyword("OPTION")
        || t.IsKeyword("UNION") || t.IsKeyword("EXCEPT") || t.IsKeyword("INTERSECT");

    private bool IsClauseKeyword(Token t) =>
        t.IsKeyword("FROM") || t.IsKeyword("WHERE") || t.IsKeyword("GROUP") || t.IsKeyword("ORDER")
        || t.IsKeyword("HAVING") || t.IsKeyword("UNION") || t.IsKeyword("INNER") || t.IsKeyword("LEFT")
        || t.IsKeyword("RIGHT") || t.IsKeyword("JOIN") || t.IsKeyword("ON") || t.IsKeyword("SELECT")
        || t.IsKeyword("WITH") || t.IsKeyword("EXCEPT") || t.IsKeyword("INTERSECT") || t.IsKeyword("END")
        || t.IsKeyword("INTO");

    private Token Peek()
    {
        int i = _pos;
        while (i < _tokens.Count && Skippable.Contains(_tokens[i].Type)) i++;
        return i < _tokens.Count ? _tokens[i] : new Token(TokenType.EndOfFile, string.Empty);
    }

    /// <summary>
    /// Like Peek() but also skips standalone comment tokens, so callers can detect a
    /// clause keyword (JOIN, WHERE, ...) that follows one or more comment lines.
    /// </summary>
    /// <summary>
    /// True when the next meaningful token is <paramref name="keyword"/>, looking past any
    /// comments in front of it; the comments are then moved into <paramref name="sink"/> so the
    /// clause itself can be consumed normally. Peeking at the raw token instead is exactly how a
    /// comment written before a clause ("delete d /*note*/" or "-- into #tmp") used to hide the
    /// clause behind it and drop the rest of the statement into unformatted raw text.
    /// </summary>
    private bool PeekClause(string keyword, List<string> sink)
    {
        if (!PeekPastComments().IsKeyword(keyword)) return false;
        sink.AddRange(CollectStandaloneComments());
        return true;
    }

    /// <summary>Like <see cref="PeekPastComments()"/>, but skipping <paramref name="skip"/> more
    /// meaningful tokens after the first — used to check "WITH (" past a comment.</summary>
    private Token PeekPastComments(int skip)
    {
        int i = _pos, seen = 0;
        while (i < _tokens.Count)
        {
            var t = _tokens[i];
            if (Skippable.Contains(t.Type) || t.Type is TokenType.LineComment or TokenType.BlockComment)
            { i++; continue; }
            if (seen == skip) return t;
            seen++; i++;
        }
        return new Token(TokenType.EndOfFile, string.Empty);
    }

    private Token PeekPastComments()
    {
        int i = _pos;
        while (i < _tokens.Count &&
               (Skippable.Contains(_tokens[i].Type)
                || _tokens[i].Type == TokenType.LineComment
                || _tokens[i].Type == TokenType.BlockComment)) i++;
        return i < _tokens.Count ? _tokens[i] : new Token(TokenType.EndOfFile, string.Empty);
    }

    /// <summary>Consumes and returns any standalone comments at the current position.</summary>
    private List<string> CollectStandaloneComments()
    {
        var comments = new List<string>();
        while (true)
        {
            // Look past whitespace/newlines WITHOUT consuming them, so a run with no comment
            // leaves the separators intact (blank-line detection upstream depends on it).
            int i = _pos;
            while (i < _tokens.Count && Skippable.Contains(_tokens[i].Type)) i++;
            if (i < _tokens.Count &&
                (_tokens[i].Type == TokenType.LineComment || _tokens[i].Type == TokenType.BlockComment))
            {
                comments.Add(_tokens[i].Value);
                _pos = i + 1;
            }
            else break;
        }
        return comments;
    }

    private Token PeekAt(int offset)
    {
        int count = 0, i = _pos;
        while (i < _tokens.Count)
        {
            if (!Skippable.Contains(_tokens[i].Type)) { if (count == offset) return _tokens[i]; count++; }
            i++;
        }
        return new Token(TokenType.EndOfFile, string.Empty);
    }

    /// <summary>
    /// Counts consecutive Newline tokens immediately before the next meaningful token — the one
    /// Peek() would return. The run is measured from that token backwards rather than from _pos,
    /// so a lookahead that already stepped over part of the separator run cannot make a blank
    /// line "disappear" for the caller.
    /// </summary>
    private int CountNewlinesBackFromCurrent()
    {
        int next = _pos;
        while (next < _tokens.Count && Skippable.Contains(_tokens[next].Type)) next++;

        int i = next - 1, count = 0;
        while (i >= 0 && (_tokens[i].Type == TokenType.Newline || _tokens[i].Type == TokenType.Whitespace))
        {
            if (_tokens[i].Type == TokenType.Newline) count++;
            i--;
        }
        return count;
    }

    /// <summary>Counts consecutive Newline tokens immediately after the current position.</summary>
    private int CountNewlinesForwardFromCurrent()
    {
        int i = _pos, count = 0;
        while (i < _tokens.Count && (_tokens[i].Type == TokenType.Newline || _tokens[i].Type == TokenType.Whitespace))
        {
            if (_tokens[i].Type == TokenType.Newline) count++;
            i++;
        }
        return count;
    }

    private Token PeekRaw() => _pos < _tokens.Count ? _tokens[_pos] : new Token(TokenType.EndOfFile, string.Empty);

    /// <summary>
    /// After a statement ran off the end of the input, finds the longest prefix of it that still
    /// parses as a whole statement: trailing tokens are dropped one at a time and the prefix is
    /// re-parsed. Returns that statement and sets <paramref name="tailStart"/> to the token where
    /// the leftover begins — the original whitespace between the two included, so the remainder can
    /// be appended exactly where it stood. Returns null when no prefix parses.
    /// </summary>
    private AstNode? TryParseLongestPrefix(int start, out int tailStart)
    {
        tailStart = start;

        var meaningful = new List<int>();
        for (int i = start; i < _tokens.Count; i++)
            if (!Skippable.Contains(_tokens[i].Type) && _tokens[i].Type != TokenType.EndOfFile)
                meaningful.Add(i);

        // A cut-short tail is short: try the longest prefixes first and give up after a while
        // rather than rescanning a huge statement token by token.
        const int maxAttempts = 200;
        int attempts = 0;
        for (int last = meaningful.Count - 1; last >= 1 && attempts < maxAttempts; last--, attempts++)
        {
            var slice = _tokens.GetRange(start, meaningful[last] - start);
            slice.Add(new Token(TokenType.EndOfFile, string.Empty));
            var sub = new Parser(slice);
            try
            {
                var stmt = sub.ParseStatement();
                // The prefix must be consumed whole — leftover tokens would be silently lost.
                if (stmt == null || !sub.IsAtEnd()) continue;
                tailStart = meaningful[last - 1] + 1;
                return stmt;
            }
            catch (ParseException) { }
        }
        return null;
    }

    /// <summary>
    /// Rebuilds the original source text from token <paramref name="start"/> to the end. Every
    /// token (including whitespace, newlines, comments and literals) keeps its raw text, so the
    /// result is the input verbatim — used to pass an unparsable tail through untouched.
    /// </summary>
    private string RawTextFrom(int start)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = start; i < _tokens.Count; i++)
            if (_tokens[i].Type != TokenType.EndOfFile) sb.Append(_tokens[i].Value);
        return sb.ToString();
    }

    /// <summary>
    /// If a line comment follows on the SAME line (only whitespace between here and it,
    /// no newline), consumes and returns it. Otherwise returns null and consumes nothing.
    /// Used so a comment on its own line is treated as standalone, not as a trailing comment.
    /// </summary>
    private string? TryTakeSameLineComment()
    {
        int i = _pos;
        while (i < _tokens.Count && _tokens[i].Type == TokenType.Whitespace) i++;
        if (i < _tokens.Count && _tokens[i].Type == TokenType.LineComment)
        {
            var val = _tokens[i].Value;
            _pos = i + 1;
            if (_hoistComments) { _pendingComments.Add(val); return null; }
            return val;
        }
        return null;
    }

    /// <summary>
    /// Like TryTakeSameLineComment but also catches inline block comments (/* ... */) that
    /// sit on the same line, e.g. "expr /* note */ and ...". Returns null if none.
    /// </summary>
    /// <summary>
    /// Takes the comment that CLOSES the current line, if there is one: a -- comment always (it
    /// closes its line by definition), a /* */ comment only when the author wrote a line break
    /// after it. A block comment with code still behind it on the same line belongs to that code
    /// and stays glued to it, the way it was written.
    /// </summary>
    private string? TryTakeLineClosingComment()
    {
        int i = _pos;
        while (i < _tokens.Count && _tokens[i].Type == TokenType.Whitespace) i++;
        if (i >= _tokens.Count) return null;
        if (_tokens[i].Type == TokenType.BlockComment)
        {
            int j = i + 1;
            while (j < _tokens.Count && _tokens[j].Type == TokenType.Whitespace) j++;
            if (j < _tokens.Count && _tokens[j].Type is not (TokenType.Newline or TokenType.EndOfFile))
                return null;
        }
        else if (_tokens[i].Type != TokenType.LineComment) return null;

        var val = _tokens[i].Value;
        _pos = i + 1;
        if (_hoistComments) { _pendingComments.Add(val); return null; }
        return val;
    }

    private string? TryTakeSameLineInlineComment() => TryTakeSameLineInlineComment(out _);

    /// <summary>
    /// Same, but also reports whether the author wrote the comment glued to the code before it
    /// ("as u/*note*/" — no space, block comment). A glued comment stays glued on output.
    /// </summary>
    private string? TryTakeSameLineInlineComment(out bool glued)
    {
        glued = false;
        int i = _pos;
        while (i < _tokens.Count && _tokens[i].Type == TokenType.Whitespace) i++;
        if (i < _tokens.Count &&
            (_tokens[i].Type == TokenType.LineComment || _tokens[i].Type == TokenType.BlockComment))
        {
            var val = _tokens[i].Value;
            glued = i == _pos && _tokens[i].Type == TokenType.BlockComment;
            _pos = i + 1;
            if (_hoistComments) { _pendingComments.Add(val); return null; }
            return val;
        }
        return null;
    }
    private bool PeekIs(TokenType type) => Peek().Type == type;

    private Token Advance()
    {
        while (_pos < _tokens.Count && Skippable.Contains(_tokens[_pos].Type)) _pos++;
        return _pos < _tokens.Count ? _tokens[_pos++] : new Token(TokenType.EndOfFile, string.Empty);
    }

    private Token AdvanceRaw() => _pos < _tokens.Count ? _tokens[_pos++] : new Token(TokenType.EndOfFile, string.Empty);

    private Token Expect(TokenType type, string? value = null)
    {
        var t = Peek();
        if (Matches(t, type, value)) return Advance();
        // Tolerate comments sitting before the expected token: skip them (remembering their
        // text in the pending buffer) and retry, so a comment can't desync the stream
        // (e.g. "exists ( --note\n select ...)"). Only a genuine token mismatch is an error.
        if (SkipLeadingComments())
        {
            t = Peek();
            if (Matches(t, type, value)) return Advance();
        }
        throw new ParseException(type, value, t);
    }

    private static bool Matches(Token t, TokenType type, string? value) =>
        t.Type == type && (value == null || t.Value.Equals(value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Advances past leading whitespace/newlines and comment tokens, collecting the
    /// comment text into the pending buffer. Returns true if at least one comment was skipped.</summary>
    private bool SkipLeadingComments()
    {
        bool any = false;
        while (true)
        {
            int i = _pos;
            while (i < _tokens.Count && Skippable.Contains(_tokens[i].Type)) i++;
            if (i < _tokens.Count &&
                (_tokens[i].Type == TokenType.LineComment || _tokens[i].Type == TokenType.BlockComment))
            {
                _pendingComments.Add(_tokens[i].Value);
                _pos = i + 1;
                any = true;
            }
            else break;
        }
        return any;
    }

    /// <summary>Returns and clears any comments collected by Expr's comment-skipping.</summary>
    private List<string> DrainPendingComments()
    {
        if (_pendingComments.Count == 0) return new List<string>();
        var copy = new List<string>(_pendingComments);
        _pendingComments.Clear();
        return copy;
    }

    /// <summary>
    /// Consumes comments standing inside a dotted name ("dbo. --note \n Users") and parks them for
    /// hoisting above the statement. A comment is never part of a name: taken as one it both
    /// mangled the name and commented out whatever followed it on that line.
    /// </summary>
    private void ParkComment()
    {
        while (PeekIs(TokenType.LineComment) || PeekIs(TokenType.BlockComment))
            _pendingComments.Add(Advance().Value);
    }

    /// <summary>True when a newline (or the end of input) comes before the next meaningful token,
    /// i.e. the author broke the line here.</summary>
    /// <summary>
    /// Glues a /* */ comment written with NO space in front of it to the operand it follows
    /// ("@from/*)*/"): commented-out code wrapped around a value. With a space in front it is an
    /// ordinary trailing comment and belongs to whatever clause the operand sits in.
    /// </summary>
    private AstNode AttachGluedBlockComment(AstNode operand)
    {
        if (_pos >= _tokens.Count || _tokens[_pos].Type != TokenType.BlockComment) return operand;
        var text = _tokens[_pos].Value;
        _pos++;
        if (_hoistComments) { _pendingComments.Add(text); return operand; }
        if (operand is InlineCommentedNode ic && ic.After == null) { ic.After = text; return ic; }
        return new InlineCommentedNode { Inner = operand, After = text };
    }

    /// <summary>
    /// Takes a /* */ comment written where an operator is expected ("10 /* note */ / 2"), so the
    /// comment does not end the expression and leave "/ 2" behind as raw text. Only block
    /// comments: a -- comment really does end its line, and pulling code up onto it would
    /// comment that code out.
    /// </summary>
    private string? TryTakeInlineBlockComment()
    {
        int i = _pos;
        while (i < _tokens.Count && Skippable.Contains(_tokens[i].Type)) i++;
        if (i >= _tokens.Count || _tokens[i].Type != TokenType.BlockComment) return null;
        var val = _tokens[i].Value;
        _pos = i + 1;
        if (_hoistComments) { _pendingComments.Add(val); return null; }
        return val;
    }

    /// <summary>
    /// Undoes a speculative read: back to <paramref name="position"/>, and any comment parked
    /// along the way is un-parked. Without the second half, a comment read speculatively and then
    /// given back was hoisted once per attempt and came out duplicated.
    /// </summary>
    private void Rewind(int position, int parkedCount)
    {
        _pos = position;
        if (_pendingComments.Count > parkedCount)
            _pendingComments.RemoveRange(parkedCount, _pendingComments.Count - parkedCount);
    }

    /// <summary>Consumes a ';' standing on the current line, and reports whether there was one.</summary>
    private bool TryTakeSameLineSemicolon()
    {
        int i = _pos;
        while (i < _tokens.Count && _tokens[i].Type == TokenType.Whitespace) i++;
        if (i >= _tokens.Count || _tokens[i].Type != TokenType.Semicolon) return false;
        _pos = i + 1;
        return true;
    }

    private bool NewlineFollows()
    {
        int i = _pos;
        while (i < _tokens.Count && _tokens[i].Type == TokenType.Whitespace) i++;
        return i >= _tokens.Count || _tokens[i].Type is TokenType.Newline or TokenType.EndOfFile;
    }

    private bool TryConsume(TokenType type) { if (Peek().Type != type) return false; Advance(); return true; }
    private bool IsAtEnd() => Peek().Type == TokenType.EndOfFile;
}

internal static class NodeExtensions
{
    public static T Tap<T>(this T node, Action<T> action) where T : AstNode { action(node); return node; }
}

}
