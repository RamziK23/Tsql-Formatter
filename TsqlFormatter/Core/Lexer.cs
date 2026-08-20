using System;
using System.Collections.Generic;
using System.Text;

namespace TsqlFormatter.Core
{

public sealed class Lexer
{
    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT","FROM","WHERE","JOIN","INNER","LEFT","RIGHT","FULL","OUTER","CROSS",
        "ON","AND","OR","NOT","IN","LIKE","BETWEEN","IS","NULL","AS","TOP","DISTINCT",
        "GROUP","BY","ORDER","HAVING","UNION","ALL","EXCEPT","INTERSECT",
        "INSERT","INTO","VALUES","UPDATE","SET","DELETE","TRUNCATE",
        "CREATE","ALTER","DROP","TABLE","VIEW","INDEX","PROCEDURE","FUNCTION",
        "BEGIN","END","IF","ELSE","WHILE","RETURN","RETURNS","EXEC","EXECUTE",
        "CASE","WHEN","THEN","ELSE","OPENQUERY","EXISTS","WITH",
        "DECLARE","PRINT","RAISERROR","TRY","CATCH","THROW",
        "ASC","DESC","NULLS","FIRST","LAST","COLLATE","PERCENT","TIES",
        "MIN","MAX","SUM","COUNT","AVG","COALESCE","ISNULL","NULLIF","CONCAT",
        "CAST","CONVERT","DATEDIFF","DATEADD","GETDATE","GETUTCDATE","YEAR","MONTH","DAY",
        "LEN","LTRIM","RTRIM","TRIM","SUBSTRING","CHARINDEX","REPLACE","STUFF","FORMAT",
        "ROW_NUMBER","RANK","DENSE_RANK","NTILE","OVER","PARTITION",
        "CROSS","APPLY","OUTER","PIVOT","UNPIVOT",
        "INT","BIGINT","SMALLINT","TINYINT","BIT","FLOAT","REAL","DECIMAL","NUMERIC",
        "CHAR","VARCHAR","NCHAR","NVARCHAR","TEXT","NTEXT","DATE","DATETIME","DATETIME2",
        "SMALLDATETIME","TIME","UNIQUEIDENTIFIER","XML","IMAGE","VARBINARY","BINARY",
        "NOCOUNT","TRANSACTION","COMMIT","ROLLBACK","SAVEPOINT","LOCK",
        // SET session options — keywords, so "SET QUOTED_IDENTIFIER ON" lowercases like any
        // other keyword. Only the underscored option names, which nobody uses as a column name.
        "QUOTED_IDENTIFIER","ANSI_NULLS","ANSI_PADDING","ANSI_WARNINGS","ARITHABORT",
        "CONCAT_NULL_YIELDS_NULL","NUMERIC_ROUNDABORT","XACT_ABORT","IMPLICIT_TRANSACTIONS",
        "IDENTITY_INSERT",
        "PRIMARY","FOREIGN","KEY","CONSTRAINT","DEFAULT","IDENTITY","CHECK",
        "NONCLUSTERED","CLUSTERED","UNIQUE","INCLUDE","FILLFACTOR","PAD_INDEX",
        "NOLOCK","READPAST","UPDLOCK","TABLOCK","ROWLOCK","XLOCK","HOLDLOCK",
        "OPTION","MAXDOP","RECOMPILE","OPTIMIZE","FOR","UNKNOWN",
        "OUTPUT","MERGE","MATCHED","TARGET","SOURCE","USING",
    };

    private readonly string _source;
    private int _pos;
    private int _line;
    private int _col;

    public Lexer(string source) { _source = source; _pos = 0; _line = 1; _col = 1; }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (_pos < _source.Length)
        {
            var token = ReadNext();
            if (token != null) tokens.Add(token);
        }
        tokens.Add(new Token(TokenType.EndOfFile, string.Empty, _line, _col));
        return tokens;
    }

    private Token? ReadNext()
    {
        char c = _source[_pos];
        if (c == '\r' || c == '\n') { int sl = _line, sc = _col; string nl = ReadNewline(); return new Token(TokenType.Newline, nl, sl, sc); }
        if (char.IsWhiteSpace(c)) { int sl = _line, sc = _col; string ws = ReadWhile(ch => char.IsWhiteSpace(ch) && ch != '\r' && ch != '\n'); return new Token(TokenType.Whitespace, ws, sl, sc); }
        if (c == '-' && Peek(1) == '-') return ReadLineComment();
        if (c == '/' && Peek(1) == '*') return ReadBlockComment();
        if (c == '\'') return ReadStringLiteral();
        // Unicode string literal prefix: N'...' (the N belongs to the string, not an identifier)
        if ((c == 'N' || c == 'n') && Peek(1) == '\'') return ReadStringLiteral(unicodePrefix: true);
        if (c == '[') return ReadQuotedIdentifier();
        // ANSI double-quoted identifier: "name" (equivalent to [name] in T-SQL)
        if (c == '"') return ReadDoubleQuotedIdentifier();
        // Hex/binary literal 0x1F: must be one token, or "0x1F" splits into number 0
        // and identifier x1F (which the parser would then treat as an alias).
        if (c == '0' && (Peek(1) == 'x' || Peek(1) == 'X')) return ReadHexLiteral();
        if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek(1)))) return ReadNumber();
        if (c == '@') return ReadVariable();
        if (char.IsLetter(c) || c == '_' || c == '#') return ReadWord();
        // Compound assignment operators must stay a single token: += -= *= /= %= &= |= ^=
        // (checked before the '/*' comment case above already handled '/*').
        if ((c is '+' or '-' or '*' or '/' or '%' or '&' or '|' or '^') && Peek(1) == '=')
            return ReadOperator(TokenType.CompoundAssign, 2);
        if (c == '<' && Peek(1) == '>') return ReadOperator(TokenType.NotEquals, 2);
        if (c == '!' && Peek(1) == '=') return ReadOperator(TokenType.NotEquals, 2);
        if (c == '<' && Peek(1) == '=') return ReadOperator(TokenType.LessOrEqual, 2);
        if (c == '>' && Peek(1) == '=') return ReadOperator(TokenType.GreaterOrEqual, 2);
        return c switch
        {
            ',' => ReadSingle(TokenType.Comma),
            '.' => ReadSingle(TokenType.Dot),
            ';' => ReadSingle(TokenType.Semicolon),
            '(' => ReadSingle(TokenType.LeftParen),
            ')' => ReadSingle(TokenType.RightParen),
            '=' => ReadSingle(TokenType.Equals),
            '<' => ReadSingle(TokenType.LessThan),
            '>' => ReadSingle(TokenType.GreaterThan),
            '+' => ReadSingle(TokenType.Plus),
            '-' => ReadSingle(TokenType.Minus),
            '*' => ReadSingle(TokenType.Multiply),
            '/' => ReadSingle(TokenType.Divide),
            '%' => ReadSingle(TokenType.Percent),
            '&' => ReadSingle(TokenType.BitwiseOp),
            '|' => ReadSingle(TokenType.BitwiseOp),
            '^' => ReadSingle(TokenType.BitwiseOp),
            _ => ReadSingle(TokenType.Unknown)
        };
    }

    private Token ReadWord()
    {
        int sl = _line, sc = _col;
        string word = ReadWhile(c => char.IsLetterOrDigit(c) || c == '_' || c == '#' || c == '$');
        if (word.Equals("DECLARE", StringComparison.OrdinalIgnoreCase))
            return new Token(TokenType.DeclareKeyword, word, sl, sc);
        if (SqlKeywords.Contains(word))
            return new Token(TokenType.Keyword, word, sl, sc);  // preserve original case
        return new Token(TokenType.Identifier, word, sl, sc);
    }

    private Token ReadVariable()
    {
        int sl = _line, sc = _col;
        Advance();
        // A second '@' makes it a built-in global: @@TRANCOUNT, @@ROWCOUNT, @@IDENTITY. It is one
        // token — split in two, "@@TRANCOUNT" came out as "@" plus a separate variable.
        string prefix = Peek(0) == '@' ? "@" + Advance() : "@";
        string name = ReadWhile(c => char.IsLetterOrDigit(c) || c == '_');
        return new Token(TokenType.Variable, prefix + name, sl, sc);
    }

    private Token ReadStringLiteral(bool unicodePrefix = false)
    {
        int sl = _line, sc = _col;
        var sb = new System.Text.StringBuilder();
        if (unicodePrefix) sb.Append(Advance()); // consume the N prefix
        sb.Append(Advance());
        while (_pos < _source.Length)
        {
            char c = _source[_pos];
            if (c == '\'') { sb.Append(Advance()); if (_pos < _source.Length && _source[_pos] == '\'') sb.Append(Advance()); else break; }
            else sb.Append(Advance());
        }
        return new Token(TokenType.StringLiteral, sb.ToString(), sl, sc);
    }

    /// <summary>
    /// Bracketed identifier: [name]. A doubled ]] inside escapes a literal ] — stopping at the
    /// first ] instead cut "[col]]--umn]" into three tokens and turned the rest of the line into
    /// a comment.
    /// </summary>
    private Token ReadQuotedIdentifier()
    {
        int sl = _line, sc = _col;
        var sb = new System.Text.StringBuilder();
        sb.Append(Advance()); // opening [
        while (_pos < _source.Length)
        {
            char c = _source[_pos];
            if (c == ']')
            {
                sb.Append(Advance());
                if (_pos < _source.Length && _source[_pos] == ']') sb.Append(Advance());
                else break;
            }
            else sb.Append(Advance());
        }
        return new Token(TokenType.QuotedIdentifier, sb.ToString(), sl, sc);
    }

    /// <summary>
    /// ANSI double-quoted identifier: "name". Doubled quotes ("") inside escape a literal quote.
    /// Preserved verbatim, like [bracketed] identifiers.
    /// </summary>
    private Token ReadDoubleQuotedIdentifier()
    {
        int sl = _line, sc = _col;
        var sb = new System.Text.StringBuilder();
        sb.Append(Advance()); // opening "
        while (_pos < _source.Length)
        {
            char c = _source[_pos];
            if (c == '"')
            {
                sb.Append(Advance());
                // "" is an escaped quote inside the identifier
                if (_pos < _source.Length && _source[_pos] == '"') sb.Append(Advance());
                else break;
            }
            else sb.Append(Advance());
        }
        return new Token(TokenType.QuotedIdentifier, sb.ToString(), sl, sc);
    }

    private Token ReadHexLiteral()
    {
        int sl = _line, sc = _col;
        var sb = new System.Text.StringBuilder();
        sb.Append(Advance()); // 0
        sb.Append(Advance()); // x / X
        while (_pos < _source.Length && Uri.IsHexDigit(_source[_pos])) sb.Append(Advance());
        return new Token(TokenType.NumberLiteral, sb.ToString(), sl, sc);
    }

    private Token ReadNumber()
    {
        int sl = _line, sc = _col;
        var sb = new System.Text.StringBuilder();
        char prev = '\0';
        while (_pos < _source.Length)
        {
            char c = _source[_pos];
            bool ok = char.IsDigit(c) || c == '.' || c == 'e' || c == 'E'
                      // a minus is part of the number only as an exponent sign: 1e-5
                      || (c == '-' && (prev == 'e' || prev == 'E'));
            if (!ok) break;
            sb.Append(c);
            prev = c;
            _pos++; _col++;
        }
        return new Token(TokenType.NumberLiteral, sb.ToString(), sl, sc);
    }

    private Token ReadLineComment()
    {
        int sl = _line, sc = _col;
        string text = ReadWhile(c => c != '\r' && c != '\n');
        return new Token(TokenType.LineComment, text, sl, sc);
    }

    private Token ReadBlockComment()
    {
        int sl = _line, sc = _col;
        var sb = new System.Text.StringBuilder();
        sb.Append(Advance()); sb.Append(Advance());
        // T-SQL block comments nest: /* outer /* inner */ still comment */ is ONE comment.
        int depth = 1;
        while (_pos < _source.Length && depth > 0)
        {
            if (_source[_pos] == '/' && Peek(1) == '*') { depth++; sb.Append(Advance()); sb.Append(Advance()); continue; }
            if (_source[_pos] == '*' && Peek(1) == '/') { depth--; sb.Append(Advance()); sb.Append(Advance()); continue; }
            sb.Append(Advance());
        }
        return new Token(TokenType.BlockComment, sb.ToString(), sl, sc);
    }

    private Token ReadOperator(TokenType type, int length)
    {
        int sl = _line, sc = _col;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < length; i++) sb.Append(Advance());
        return new Token(type, sb.ToString(), sl, sc);
    }

    private Token ReadSingle(TokenType type) { int sl = _line, sc = _col; return new Token(type, Advance().ToString(), sl, sc); }

    private string ReadNewline()
    {
        char c = _source[_pos];
        if (c == '\r' && Peek(1) == '\n') { Advance(); Advance(); return "\r\n"; }
        Advance();
        return c == '\r' ? "\r" : "\n";
    }

    private string ReadWhile(Func<char, bool> predicate)
    {
        var sb = new System.Text.StringBuilder();
        while (_pos < _source.Length && predicate(_source[_pos])) sb.Append(Advance());
        return sb.ToString();
    }

    private char Advance() { char c = _source[_pos++]; if (c == '\n') { _line++; _col = 1; } else _col++; return c; }
    private char Peek(int offset = 1) => _pos + offset < _source.Length ? _source[_pos + offset] : '\0';
}

}
