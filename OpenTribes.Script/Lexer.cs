namespace OpenTribes.Script;

using OpenTribes.Core;

/// <summary>The kind of a lexer token.</summary>
/// <remarks>Port of src/script/lexer.cpp.</remarks>
public enum Tok : byte
{
    /// <summary>NUL, end of text, or an invalid \x escape (the original returns -1).</summary>
    End,
    /// <summary>An illegal character (rules 71 and 72).</summary>
    Illegal,
    /// <summary>A string literal.</summary>
    String,
    /// <summary>An integer literal.</summary>
    Int,
    /// <summary>A float literal.</summary>
    Float,
    /// <summary>A variable name.</summary>
    Var,
    /// <summary>A tag name.</summary>
    Tag,
    /// <summary>An identifier.</summary>
    Ident,
    /// <summary>The break keyword.</summary>
    Break,
    /// <summary>The nil keyword.</summary>
    Nil,
    /// <summary>The class keyword.</summary>
    Class,
    /// <summary>The return keyword.</summary>
    Return,
    /// <summary>The else keyword.</summary>
    Else,
    /// <summary>The global keyword.</summary>
    Global,
    /// <summary>The while keyword.</summary>
    While,
    /// <summary>The if keyword.</summary>
    If,
    /// <summary>The for keyword.</summary>
    For,
    /// <summary>The continue keyword.</summary>
    Continue,
    /// <summary>The function keyword.</summary>
    Function,
    /// <summary>The instant keyword.</summary>
    Instant,
    /// <summary>The == operator.</summary>
    Eq,
    /// <summary>The != operator.</summary>
    Ne,
    /// <summary>The &gt;= operator.</summary>
    Ge,
    /// <summary>The &lt;= operator.</summary>
    Le,
    /// <summary>The &amp;&amp; operator.</summary>
    AndAnd,
    /// <summary>The || operator.</summary>
    OrOr,
    /// <summary>The :: operator.</summary>
    ColonColon,
    /// <summary>The -- operator.</summary>
    MinusMinus,
    /// <summary>The ++ operator.</summary>
    PlusPlus,
    /// <summary>The $+ operator.</summary>
    DollarPlus,
    /// <summary>The &lt;&lt; operator.</summary>
    Shl,
    /// <summary>The &gt;&gt; operator.</summary>
    Shr,
    /// <summary>The += operator.</summary>
    PlusAssign,
    /// <summary>The -= operator.</summary>
    MinusAssign,
    /// <summary>The *= operator.</summary>
    MulAssign,
    /// <summary>The /= operator.</summary>
    DivAssign,
    /// <summary>The %= operator.</summary>
    ModAssign,
    /// <summary>The &amp;= operator.</summary>
    AndAssign,
    /// <summary>The ^= operator.</summary>
    XorAssign,
    /// <summary>The |= operator.</summary>
    OrAssign,
    /// <summary>The &lt;&lt;= operator.</summary>
    ShlAssign,
    /// <summary>The &gt;&gt;= operator.</summary>
    ShrAssign,
    /// <summary>A single character: one of @ [ ] ( ) + - * / &lt; &gt; | . ! : ; { } , &amp; % ^ ~ =.</summary>
    Char,
}

/// <summary>A lexer token.</summary>
public sealed class Token
{
    /// <summary>The token kind.</summary>
    public Tok Kind { get; set; } = Tok.End;
    /// <summary>The character of a <c>Tok.Char</c> token.</summary>
    public char Ch { get; set; }
    /// <summary>The identifier, variable or tag name, the string value, or the number text.</summary>
    public string Text { get; set; } = "";
    /// <summary>The source line.</summary>
    public int Line { get; set; } = 1;
}

/// <summary>The console script lexer. Rules and priorities are in docs/script.md section 1 (original CMDlex, 005f7808).</summary>
public sealed class Lexer
{
    private readonly string _src;
    private int _pos;
    private int _line = 1;

    private const int TokenBuffer = 4096;

    private static readonly (string text, Tok tok)[] Keywords =
    [
        ("break", Tok.Break), ("nil", Tok.Nil), ("class", Tok.Class), ("return", Tok.Return),
        ("else", Tok.Else), ("global", Tok.Global), ("while", Tok.While), ("if", Tok.If),
        ("for", Tok.For), ("continue", Tok.Continue), ("function", Tok.Function), ("instant", Tok.Instant),
    ];

    // Longest first.
    private static readonly (string text, Tok tok)[] Ops =
    [
        ("<<=", Tok.ShlAssign), (">>=", Tok.ShrAssign), ("==", Tok.Eq), ("!=", Tok.Ne),
        (">=", Tok.Ge), ("<=", Tok.Le), ("&&", Tok.AndAnd), ("||", Tok.OrOr),
        ("::", Tok.ColonColon), ("--", Tok.MinusMinus), ("++", Tok.PlusPlus), ("$+", Tok.DollarPlus),
        ("<<", Tok.Shl), (">>", Tok.Shr), ("+=", Tok.PlusAssign), ("-=", Tok.MinusAssign),
        ("*=", Tok.MulAssign), ("/=", Tok.DivAssign), ("%=", Tok.ModAssign), ("&=", Tok.AndAssign),
        ("^=", Tok.XorAssign), ("|=", Tok.OrAssign),
    ];
    private const string Singles = "@[]()+-*/<>|.!:;{},&%^~=";

    /// <summary>Creates a lexer.</summary>
    /// <param name="source">The script text.</param>
    public Lexer(string source) => _src = source;

    /// <summary>Gets the current source line.</summary>
    /// <returns>The line number.</returns>
    public int Line() => _line;

    private static bool IsAlpha(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_';
    private static bool IsDigit(char c) => c is >= '0' and <= '9';
    private static bool IsIdentChar(char c) => IsAlpha(c) || IsDigit(c) || c == ':';
    private static bool IsHex(char c) => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    private static int HexValue(char c) => IsDigit(c) ? c - '0' : (c | 0x20) - 'a' + 10;

    private char Peek(int ahead = 0) => _pos + ahead < _src.Length ? _src[_pos + ahead] : '\0';

    // src/script/lexer.cpp Lexer::make.
    private Token Make(Tok kind, string text = "")
    {
        var t = new Token { Kind = kind, Text = text, Line = _line };
        if (t.Text.Length >= TokenBuffer)
            t.Kind = Tok.Illegal; // the original exits on "Token buffer overflow" (D11)
        // Identifier, variable and tag text is interned while lexing (0x005f75c0): the first
        // spelling ever lexed, executed or not, is the one every later use prints.
        if (kind is Tok.Ident or Tok.Var or Tok.Tag)
            t.Text = StringTable.Global().Intern(t.Text);
        return t;
    }

    /// <summary>Reads the next token.</summary>
    /// <returns>The token. A token of kind <see cref="Tok.End"/> marks the end.</returns>
    public Token Next()
    {
        for (; ; )
        {
            var ch = Peek();
            if (ch == '\0')
                return Make(Tok.End);
            if (ch is ' ' or '\t' or '\v' or '\f' or '\r')
            {
                ++_pos;
                continue;
            }
            if (ch == '\n')
            {
                ++_pos;
                ++_line;
                continue;
            }
            if ((ch == '/' && Peek(1) == '/') || ch == '#')
            {
                while (Peek() != '\0' && Peek() != '\n' && Peek() != '\r')
                    ++_pos;
                continue;
            }
            break;
        }

        var c = Peek();
        if (c == '"')
            return LexDoubleQuoted();
        if (c == '\'')
            return LexSingleQuoted();
        if (IsDigit(c))
            return LexNumber();

        if (c is '$' or '%')
        {
            if (IsAlpha(Peek(1)))
            {
                var start = _pos++;
                while (IsIdentChar(Peek()))
                    ++_pos;
                return Make(Tok.Var, _src[start.._pos]);
            }
            if (IsDigit(Peek(1)))
            {
                var i = 1;
                while (IsDigit(Peek(i)))
                    ++i;
                if (IsAlpha(Peek(i)))
                { // rule 71
                    var start = _pos;
                    _pos += i;
                    while (IsIdentChar(Peek()))
                        ++_pos;
                    return Make(Tok.Illegal, _src[start.._pos]);
                }
            }
        }

        if (IsAlpha(c))
        {
            var start = _pos;
            while (IsIdentChar(Peek()))
                ++_pos;
            var word = _src[start.._pos];
            foreach (var (text, tok) in Keywords)
                if (word == text)
                    return Make(tok, word);
            if (word.Length > 2 && word[0] == 'I' && word[1] == 'D')
                return Make(Tok.Tag, word);
            return Make(Tok.Ident, word);
        }

        foreach (var (text, tok) in Ops)
        {
            if (string.CompareOrdinal(_src, _pos, text, 0, text.Length) == 0 && _pos + text.Length <= _src.Length)
            {
                _pos += text.Length;
                return Make(tok, text);
            }
        }
        ++_pos;
        if (Singles.Contains(c))
        {
            var t = Make(Tok.Char, c.ToString());
            t.Ch = c;
            return t;
        }
        return Make(Tok.Illegal, c.ToString());
    }

    // src/script/lexer.cpp Lexer::lexNumber.
    private Token LexNumber()
    {
        var start = _pos;
        if (Peek() == '0' && (Peek(1) == 'x' || Peek(1) == 'X') && IsHex(Peek(2)))
        {
            _pos += 2;
            while (IsHex(Peek()))
                ++_pos;
            return Make(Tok.Int, _src[start.._pos]);
        }
        while (IsDigit(Peek()))
            ++_pos;
        int ExponentLength(int at)
        {
            var i = at;
            if (Peek(i) != 'e' && Peek(i) != 'E')
                return 0;
            ++i;
            if (Peek(i) == '+' || Peek(i) == '-')
                ++i;
            if (!IsDigit(Peek(i)))
                return 0;
            while (IsDigit(Peek(i)))
                ++i;
            return i - at;
        }
        var isFloat = false;
        if (Peek() == '.' && IsDigit(Peek(1)))
        {
            ++_pos;
            while (IsDigit(Peek()))
                ++_pos;
            _pos += ExponentLength(0);
            isFloat = true;
        }
        else
        {
            var n = ExponentLength(0);
            if (n != 0)
            {
                _pos += n;
                isFloat = true;
            }
        }
        return Make(isFloat ? Tok.Float : Tok.Int, _src[start.._pos]);
    }

    // src/script/lexer.cpp Lexer::lexDoubleQuoted.
    private Token LexDoubleQuoted()
    {
        // Pattern: "([^"\\\n\r]|\\[^\n])*"
        var i = 1;
        for (; ; )
        {
            var c = Peek(i);
            if (c == '"')
                break;
            if (c is '\0' or '\n' or '\r')
            {
                ++_pos; // no match: the quote alone is an ILLEGAL token
                return Make(Tok.Illegal, "\"");
            }
            if (c == '\\')
            {
                if (Peek(i + 1) == '\n' || Peek(i + 1) == '\0')
                {
                    ++_pos;
                    return Make(Tok.Illegal, "\"");
                }
                i += 2;
                continue;
            }
            ++i;
        }
        var raw = _src.Substring(_pos + 1, i - 1);
        _pos += i + 1;

        // Escapes (0x005f7644).
        var value = new System.Text.StringBuilder();
        for (var k = 0; k < raw.Length; ++k)
        {
            if (raw[k] != '\\')
            {
                value.Append(raw[k]);
                continue;
            }
            var e = raw[++k];
            if (e == 'n')
            {
                value.Append('\n');
            }
            else if (e == 't')
            {
                value.Append('\t');
            }
            else if (e == 'x')
            {
                if (k + 2 >= raw.Length || !IsHex(raw[k + 1]) || !IsHex(raw[k + 2]))
                    return Make(Tok.End); // the original lexer returns -1
                value.Append((char)((HexValue(raw[k + 1]) * 16) + HexValue(raw[k + 2])));
                k += 2;
            }
            else
            {
                value.Append(e);
            }
        }
        return Make(Tok.String, value.ToString());
    }

    // src/script/lexer.cpp Lexer::lexSingleQuoted.
    private Token LexSingleQuoted()
    {
        // Pattern: '([^'\\\n]|\\[^\n])*' with the raw text as value.
        var i = 1;
        for (; ; )
        {
            var c = Peek(i);
            if (c == '\'')
                break;
            if (c == '\0' || c == '\n' || (c == '\\' && (Peek(i + 1) == '\n' || Peek(i + 1) == '\0')))
            {
                ++_pos;
                return Make(Tok.Illegal, "'");
            }
            i += c == '\\' ? 2 : 1;
        }
        var raw = _src.Substring(_pos + 1, i - 1);
        _pos += i + 1;
        return Make(Tok.String, raw);
    }
}
