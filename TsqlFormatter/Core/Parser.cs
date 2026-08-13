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
                    { hadSemicolon = true; AdvanceRaw(); continue; }
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
                // A -- comment on the SAME line as the statement's end is a trailing comment
                // for THIS statement — keep it attached here rather than letting it migrate
                // to the following statement as a standalone comment.
                var trailing = TryTakeSameLineComment();
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

    private AstNode ParseIf()
    {
        Advance(); // IF
        var conds = ParseConditionList(isJoinOn: false);
        var then = ParseStatement();
        AstNode? elseStmt = null;
        if (Peek().IsKeyword("ELSE")) { Advance(); elseStmt = ParseStatement(); }
        var n = new IfNode { Then = then, Else = elseStmt };
        n.Conditions.AddRange(conds);
        return n;
    }

    private AstNode ParseWhile()
    {
        Advance(); // WHILE
        var conds = ParseConditionList(isJoinOn: false);
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
        TryConsume(TokenType.Semicolon);
        return new ReturnNode { Value = val };
    }

    private AstNode ParseSet()
    {
        Advance(); // SET
        var target = ParsePrimary();
        string op = "=";
        if (Peek().Type == TokenType.CompoundAssign) op = Advance().Value;
        else if (PeekIs(TokenType.Equals)) Advance();
        var val = ParseExpression();
        TryConsume(TokenType.Semicolon);
        return new SetNode { Target = target, Op = op, Value = val };
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
        // The trailing ';' is left for the statement loop: it both separates statements and
        // marks a following WITH as needing its ";with" prefix.
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
        node.Columns.AddRange(ParseSelectColumns());
        // SELECT ... INTO #tbl / ##global / schema.table / [quoted]
        if (Peek().IsKeyword("INTO"))
        {
            Advance();
            var nameSb = new System.Text.StringBuilder();
            nameSb.Append(Advance().Value);
            while (PeekPastComments().Type == TokenType.Dot)
            { ParkComment(); nameSb.Append(Advance().Value); ParkComment(); nameSb.Append(Advance().Value); }
            node.IntoTable = nameSb.ToString();
        }
        if (Peek().IsKeyword("FROM"))
        {
            Advance();
            var fromTable = ParseTableRef();
            node.FromClauses.Add(fromTable);
            // A -- comment on the SAME line as the FROM table stays attached to that line
            // (rather than migrating to a PostFromComment or the following statement).
            var fromTrailing = TryTakeSameLineComment();
            if (fromTrailing != null) fromTable.TrailingComment = fromTrailing;
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
        do
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
        } while (PeekIs(TokenType.Comma) && AdvanceAndTrue());
        return valuesNode;
    }

    private bool AdvanceAndTrue() { Advance(); return true; }

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
        Expect(TokenType.Keyword, "SET");

        var assignments = new List<AssignmentNode>();
        do
        {
            var target = ParsePrimary();
            Expect(TokenType.Equals);
            var value = ParseExpression();
            assignments.Add(new AssignmentNode { Target = target, Value = value });
        } while (PeekIs(TokenType.Comma) && AdvanceAndTrue());

        var fromClauses = new List<AstNode>();
        if (Peek().IsKeyword("FROM"))
        {
            Advance();
            fromClauses.Add(ParseTableRef());
            while (IsJoinKeyword()) fromClauses.Add(ParseJoin());
        }

        var whereConds = new List<AstNode>();
        if (Peek().IsKeyword("WHERE")) { Advance(); whereConds.AddRange(ParseConditionList(isJoinOn: false)); }

        return new UpdateNode { Table = table }.Tap(n =>
        {
            n.Assignments.AddRange(assignments);
            n.FromClauses.AddRange(fromClauses);
            n.WhereConditions.AddRange(whereConds);
        });
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

        if (Peek().IsKeyword("FROM"))
        {
            // DELETE FROM tbl ...
            Advance();
            table = ParseTableRef();
            while (IsJoinKeyword()) fromClauses.Add(ParseJoin());
        }
        else
        {
            // DELETE alias FROM tbl ...
            targetAlias = ParseTableRef();
            if (Peek().IsKeyword("FROM"))
            {
                Advance();
                fromClauses.Add(ParseTableRef());
                while (IsJoinKeyword()) fromClauses.Add(ParseJoin());
            }
        }

        var whereConds = new List<AstNode>();
        if (Peek().IsKeyword("WHERE")) { Advance(); whereConds.AddRange(ParseConditionList(isJoinOn: false)); }

        return new DeleteNode { TargetAlias = targetAlias, Table = table }.Tap(n =>
        {
            n.FromClauses.AddRange(fromClauses);
            n.WhereConditions.AddRange(whereConds);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SELECT column list
    // ═══════════════════════════════════════════════════════════════════════════

    private List<SelectColumnNode> ParseSelectColumns()
    {
        var cols = new List<SelectColumnNode>();
        // Comments that stood before the separating comma wait here for the column they annotate.
        var carriedComments = new List<string>();
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
            var leadingComments = new List<string>(carriedComments);
            carriedComments.Clear();
            while (PeekIs(TokenType.BlockComment) || PeekIs(TokenType.LineComment))
                leadingComments.Add(Advance().Value);

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
            if (alias == null)
            {
                if (Peek().IsKeyword("AS")) { Advance(); alias = Advance(); }
                else if (Peek().Type is TokenType.Identifier or TokenType.QuotedIdentifier
                         && !IsClauseKeyword(Peek()) && !IsGoKeyword())
                    alias = Advance();
                // Non-reserved words lex as Keyword but are legal bare aliases (day, year,
                // key, ...): "select getdate() day" must not split into two columns.
                else if (Peek().Type == TokenType.Keyword
                         && !IsSelectClauseKeyword() && !IsClauseKeyword(Peek()))
                    alias = Advance();
            }
            // Only a comment on the SAME line is a trailing comment for this column.
            if (comment == null) comment = TryTakeSameLineComment();
            bool sawComma = PeekIs(TokenType.Comma);
            if (sawComma) Advance();
            // A trailing comment may also sit AFTER the comma on the same line: "a, --note"
            // or "a, /* note */". A block comment there is transparent — glued to this column.
            if (comment == null) comment = TryTakeSameLineInlineComment();
            cols.Add(new SelectColumnNode { Expression = expr, Alias = alias, TrailingComment = comment }
                .Tap(c => c.LeadingComments.AddRange(leadingComments)));
            // Columns are comma-separated: with no comma, the next token can't start another
            // column — it's the next statement ("select 1 \n use db"), which must not be
            // swallowed into the list with an invented comma. Standalone comments may stand
            // between the column and that comma, though, and the list goes on after them.
            if (!sawComma && PeekPastComments().Type != TokenType.Comma) break;
        }
        // Comments carried past a comma with no column behind them are hoisted, never dropped.
        _pendingComments.AddRange(carriedComments);
        return cols;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  FROM / JOIN
    // ═══════════════════════════════════════════════════════════════════════════

    private TableRefNode ParseTableRef(bool allowFuncArgs = true)
    {
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
                Alias = sqAlias };
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
        if (Peek().IsKeyword("WITH") && PeekAt(1).Type == TokenType.LeftParen)
        {
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
        }

        bool isOpenQuery = nameParts.Count == 1
            && nameParts[0].Value.Equals("openquery", System.StringComparison.OrdinalIgnoreCase)
            && funcArgs != null;
        return new TableRefNode { Alias = alias, FuncArgs = funcArgs, IsOpenQuery = isOpenQuery, HintNolock = hint }
            .Tap(n => n.Name.AddRange(nameParts));
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
        var conditions = new List<AstNode>();
        if (Peek().IsKeyword("ON")) { Advance(); conditions.AddRange(ParseConditionList(isJoinOn: true)); }
        return new JoinNode { JoinType = parts.ToString(), Table = table }.Tap(n => n.Conditions.AddRange(conditions));
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
            if (next.IsKeyword("LIKE")) { Advance(); Advance(); return new LikeExprNode { Left = left, Pattern = ParseAdditive() }; }
            if (next.IsKeyword("BETWEEN")) { Advance(); Advance(); var lo = ParseAdditive(); Expect(TokenType.Keyword, "AND"); var hi = ParseAdditive(); return new BetweenExprNode { Left = left, Low = lo, High = hi }; }
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
            var op = Peek();
            if (op.Type is not (TokenType.Plus or TokenType.Minus or TokenType.BitwiseOp)) break;
            // Don't consume Minus that could start a negative number literal in a list context
            Advance();
            var right = ParseMultiplicative();
            left = new BinaryExprNode { Left = left, Op = op, Right = right };
        }
        return left;
    }

    private AstNode ParseMultiplicative()
    {
        var left = ParsePrimary();
        left = ApplyCollate(left);
        while (true)
        {
            var op = Peek();
            if (op.Type is not (TokenType.Multiply or TokenType.Divide or TokenType.Percent)) break;
            Advance();
            var right = ApplyCollate(ParsePrimary());
            left = new BinaryExprNode { Left = left, Op = op, Right = right };
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
                Expect(TokenType.RightParen);
                return group;
            }
            Expect(TokenType.RightParen);
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

        // Unary minus: -1 → negative literal; -(expr) → BinaryExpr
        if (tok.Type == TokenType.Minus)
        {
            Advance();
            var next = Peek();
            if (next.Type == TokenType.NumberLiteral)
                return new LiteralNode { Token = new Token(TokenType.NumberLiteral, "-" + Advance().Value) };
            var operand = ParsePrimary();
            return new BinaryExprNode
            {
                Left  = new LiteralNode { Token = new Token(TokenType.NumberLiteral, "0") },
                Op    = tok,
                Right = operand
            };
        }

        // Literals
        if (tok.Type is TokenType.StringLiteral or TokenType.NumberLiteral)
        { var lit = new LiteralNode { Token = Advance() }; lit.TrailingComment = TryTakeSameLineComment(); return lit; }
        // A comment is never an operand: a -- comment turned into a "value" would swallow the rest
        // of the line (real columns/conditions). Callers that can place a comment nicely capture it
        // before reaching here; anything left over is parked and lifted to the statement, so an
        // annotation in an odd spot moves a line up instead of costing the whole script its
        // formatting. Parsing then continues with the operand that follows.
        if (tok.Type is TokenType.LineComment or TokenType.BlockComment)
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
        bool isConvert = nameToken.Value.Equals("CONVERT", StringComparison.OrdinalIgnoreCase)
                      || nameToken.Value.Equals("TRY_CONVERT", StringComparison.OrdinalIgnoreCase);
        if (isConvert)
        {
            args.Add(ParseDataType());
            if (PeekIs(TokenType.Comma)) Advance();
            while (!IsAtEnd() && !PeekIs(TokenType.RightParen)) { args.Add(ParseExpression()); if (PeekIs(TokenType.Comma)) Advance(); else break; }
        }
        else
        {
            args.Add(ParseExpression());
            if (Peek().IsKeyword("AS")) Advance();
            args.Add(ParseDataType());
        }
        Expect(TokenType.RightParen);
        return new FunctionCallNode { Name = nameToken.Value, IsKeywordFunction = nameToken.Type == TokenType.Keyword }.Tap(n => n.Arguments.AddRange(args));
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
            if (t == TokenType.Comma) { _pos++; continue; }
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

            // A /* */ block comment glued right after the value stays attached to it, inline.
            while (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.Whitespace) _pos++;
            while (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.BlockComment)
            {
                AttachGluedBlockComment(val, _tokens[_pos++].Value);
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

    /// <summary>Glues a transparent /* */ block comment onto the last parsed IN value.</summary>
    private static void GlueInListBlockComment(List<AstNode> list, string blockComment)
    {
        if (list.Count == 0) return;   // comment inside an empty list — nothing to attach it to
        AttachGluedBlockComment(list[^1], blockComment);
    }

    /// <summary>Glues any /* */ block comment(s) on the SAME line (whitespace-only before them,
    /// no newline) onto the given expression node — transparent, kept where they were.</summary>
    private void GlueSameLineBlockComments(AstNode expr)
    {
        while (true)
        {
            int j = _pos;
            while (j < _tokens.Count && _tokens[j].Type == TokenType.Whitespace) j++;
            if (j < _tokens.Count && _tokens[j].Type == TokenType.BlockComment)
            {
                AttachGluedBlockComment(expr, _tokens[j].Value);
                _pos = j + 1;
            }
            else break;
        }
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
        AstNode? inputExpr = null;
        if (!Peek().IsKeyword("WHEN")) inputExpr = ParsePrimary();
        var whens = new List<WhenClauseNode>();
        AstNode? elseExpr = null; string? elseComment = null;
        while (Peek().IsKeyword("WHEN"))
        {
            Advance();
            // Conditions chain with and/or; each keeps its operator so the emitter can
            // render "or" continuations too (previously OR failed the whole parse).
            var conds = new List<AstNode> { new ConditionNode { Expression = ParseExpression() } };
            while (Peek().IsKeyword("AND") || Peek().IsKeyword("OR"))
            {
                string op = Peek().IsKeyword("AND") ? "and" : "or";
                Advance();
                conds.Add(new ConditionNode { LogicalOp = op, Expression = ParseExpression() });
            }
            Expect(TokenType.Keyword, "THEN");
            var then = ParseExpression(); string? tc = null;
            if (PeekIs(TokenType.LineComment)) tc = Advance().Value;
            whens.Add(new WhenClauseNode { Then = then, ThenComment = tc }.Tap(w => w.Conditions.AddRange(conds)));
        }
        if (Peek().IsKeyword("ELSE")) { Advance(); elseExpr = ParseExpression(); if (PeekIs(TokenType.LineComment)) elseComment = Advance().Value; }
        Expect(TokenType.Keyword, "END");
        return new CaseExprNode { InputExpr = inputExpr, ElseExpr = elseExpr, ElseComment = elseComment }.Tap(n => n.WhenClauses.AddRange(whens));
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
        while (!IsAtEnd() && !PeekIs(TokenType.RightParen))
        {
            // Handle OVER (...) for window functions
            if (Peek().IsKeyword("OVER")) { Advance(); args.Add(ParseWindowSpec()); continue; }
            args.Add(ParseExpression());
            if (PeekIs(TokenType.Comma)) Advance(); else break;
        }
        Expect(TokenType.RightParen);
        // Window function: OVER clause may follow the closing paren
        AstNode? overClause = null;
        if (Peek().IsKeyword("OVER")) { Advance(); overClause = ParseWindowSpec(); }
        var fn = new FunctionCallNode { Name = name, IsKeywordFunction = isKeyword, OverClause = overClause, SetQuantifier = setQuantifier };
        fn.Arguments.AddRange(args);
        return fn;
    }

    private AstNode ParseWindowSpec()
    {
        // OVER (PARTITION BY ... ORDER BY ...)  — emit as raw tokens for now
        Expect(TokenType.LeftParen);
        var raw = new RawTokensNode();
        int depth = 1;
        while (!IsAtEnd() && depth > 0)
        {
            var t = PeekRaw();
            if (t.Type == TokenType.LeftParen)  depth++;
            if (t.Type == TokenType.RightParen) { depth--; if (depth == 0) { AdvanceRaw(); break; } }
            raw.Tokens.Add(AdvanceRaw());
        }
        return raw;
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
            GlueSameLineBlockComments(expr);
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
            list.Add(WithLeadingComments(new OrderByItemNode { Expression = expr, Direction = dir }, leading));
            if (PeekIs(TokenType.Comma)) { Advance(); continue; } break;
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
                // Keep a same-line trailing comment attached to this statement's last line.
                var trailing = TryTakeSameLineComment();
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
        while (!IsAtEnd() && !PeekIs(TokenType.LeftParen) && !IsGoKeyword()
               && Peek().Type != TokenType.EndOfFile)
        {
            nameTokens.Append(Advance().Value);
        }
        var tableName = nameTokens.ToString().Trim();
        var node = new CreateTableNode { TableName = tableName };

        if (!PeekIs(TokenType.LeftParen)) return node;
        Advance(); // outer (

        while (!IsAtEnd())
        {
            // Skip whitespace/newlines between column definitions
            while (!IsAtEnd() && _tokens[_pos].Type is TokenType.Whitespace or TokenType.Newline)
                _pos++;
            // Raw check: stop at outer )
            if (_pos >= _tokens.Count || _tokens[_pos].Type == TokenType.RightParen) break;

            // Column name (next non-whitespace token)
            var colName = Advance().Value;

            // Collect definition: everything until a DEPTH-0 comma or closing )
            // Track paren depth so varchar(255) and decimal(18,2) are parsed whole.
            var defTokens = new System.Text.StringBuilder();
            int depth = 0;
            while (!IsAtEnd())
            {
                var raw = _tokens[_pos];
                // Whitespace/newline: add a space only before non-paren, non-comma content
                if (raw.Type is TokenType.Whitespace or TokenType.Newline)
                {
                    if (depth == 0)
                    {
                        // peek what follows
                        int la = _pos + 1;
                        while (la < _tokens.Count && _tokens[la].Type is TokenType.Whitespace or TokenType.Newline) la++;
                        if (la < _tokens.Count && defTokens.Length > 0)
                        {
                            var nxt = _tokens[la].Type;
                            if (nxt != TokenType.LeftParen && nxt != TokenType.Comma
                                && nxt != TokenType.RightParen)
                                defTokens.Append(' ');
                        }
                    }
                    _pos++;
                    continue;
                }
                if (raw.Type == TokenType.LeftParen)  { depth++; defTokens.Append('('); _pos++; continue; }
                if (raw.Type == TokenType.RightParen)
                {
                    if (depth == 0) break;          // outer ) of CREATE TABLE
                    depth--;
                    defTokens.Append(')');
                    _pos++;
                    continue;
                }
                if (raw.Type == TokenType.Comma && depth == 0) break; // column separator
                // Regular token — no extra space before/after ( or )
                if (defTokens.Length > 0 && raw.Type == TokenType.Comma && depth > 0)
                    defTokens.Append(", ");         // "decimal(18, 2)" — space after comma in type
                else
                    // Type/constraint keywords (int, varchar, not, null, default ...) lowercased.
                    defTokens.Append(raw.Type == TokenType.Keyword ? raw.Value.ToLowerInvariant() : raw.Value);
                _pos++;
            }

            node.Columns.Add(new ColumnDefNode
            {
                Name       = colName,
                Definition = defTokens.ToString().Trim()
            });

            // Consume depth-0 comma separator between columns
            if (!IsAtEnd() && _tokens[_pos].Type == TokenType.Comma) _pos++;
        }
        // Consume outer )
        if (!IsAtEnd() && _tokens[_pos].Type == TokenType.RightParen) _pos++;
        return node;
    }

    private AstNode ParseDrop()
    {
        Advance(); // DROP
        if (Peek().IsKeyword("TABLE"))
        {
            Advance(); // TABLE
            bool ifExists = false;
            if (Peek().IsKeyword("IF")) { Advance(); Expect(TokenType.Keyword, "EXISTS"); ifExists = true; }
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
            return new DropTableNode { IfExists = ifExists, TableName = nameTokens.ToString().Trim() };
        }
        return ParseRawStatement();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Raw fallback
    // ═══════════════════════════════════════════════════════════════════════════

    private RawTokensNode ParseRawStatement()
    {
        var raw = new RawTokensNode();
        int parenDepth = 0;
        int caseDepth  = 0;
        while (!IsAtEnd())
        {
            var t = Peek();
            if (t.IsKeyword("SELECT") || t.IsKeyword("WITH") || t.IsKeyword("INSERT")
                || t.IsKeyword("UPDATE") || t.IsKeyword("DELETE")
                || t.IsKeyword("DROP")  || t.IsKeyword("CREATE") || t.IsKeyword("BEGIN")
                || t.IsKeyword("IF")    || t.IsKeyword("WHILE")  || t.IsKeyword("RETURN")
                || t.IsKeyword("COMMIT")|| t.IsKeyword("ROLLBACK")
                || t.Type == TokenType.DeclareKeyword
                || t.Type == TokenType.EndOfFile || IsGoKeyword()) break;
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
        }
        return raw;
    }

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
    private string? TryTakeSameLineInlineComment()
    {
        int i = _pos;
        while (i < _tokens.Count && _tokens[i].Type == TokenType.Whitespace) i++;
        if (i < _tokens.Count &&
            (_tokens[i].Type == TokenType.LineComment || _tokens[i].Type == TokenType.BlockComment))
        {
            var val = _tokens[i].Value;
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

    private bool TryConsume(TokenType type) { if (Peek().Type != type) return false; Advance(); return true; }
    private bool IsAtEnd() => Peek().Type == TokenType.EndOfFile;
}

internal static class NodeExtensions
{
    public static T Tap<T>(this T node, Action<T> action) where T : AstNode { action(node); return node; }
}

}
