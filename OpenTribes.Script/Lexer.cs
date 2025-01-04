// Port of src/script/lexer.hpp and src/script/lexer.cpp: console script lexer.
// Rules and priorities: docs/script.md section 1 (original CMDlex 0x005f7808).
using OpenTribes.Core;

namespace OpenTribes.Script;

// src/script/lexer.hpp enum class Tok.
public enum Tok : byte
{
    End,      // NUL, end of text, or an invalid \x escape (the original returns -1)
    Illegal,  // rules 71, 72
    String,
    Int,
    Float,
    Var,
    Tag,
    Ident,
    // keywords
    Break, Nil, Class, Return, Else, Global, While, If, For, Continue, Function, Instant,
    // multi-character operators
    Eq, Ne, Ge, Le, AndAnd, OrOr, ColonColon, MinusMinus, PlusPlus, DollarPlus, Shl, Shr,
    PlusAssign, MinusAssign, MulAssign, DivAssign, ModAssign, AndAssign, XorAssign, OrAssign, ShlAssign, ShrAssign,
    // single characters: @ [ ] ( ) + - * / < > | . ! : ; { } , & % ^ ~ =
    Char,
}

// src/script/lexer.hpp struct Token.
public sealed class Token
{
    public Tok Kind = Tok.End;
    public char Ch;         // for Tok.Char
    public string Text = ""; // identifier/variable/tag name, string value, number text
    public int Line = 1;
}

// src/script/lexer.hpp class Lexer.
public sealed class Lexer
{
    private readonly string mSrc;
    private int mPos;
    private int mLine = 1;

    private const int kTokenBuffer = 4096;

    private static readonly (string text, Tok tok)[] kKeywords =
    {
        ("break", Tok.Break), ("nil", Tok.Nil), ("class", Tok.Class), ("return", Tok.Return),
        ("else", Tok.Else), ("global", Tok.Global), ("while", Tok.While), ("if", Tok.If),
        ("for", Tok.For), ("continue", Tok.Continue), ("function", Tok.Function), ("instant", Tok.Instant),
    };

    // Longest first.
    private static readonly (string text, Tok tok)[] kOps =
    {
        ("<<=", Tok.ShlAssign), (">>=", Tok.ShrAssign), ("==", Tok.Eq), ("!=", Tok.Ne),
        (">=", Tok.Ge), ("<=", Tok.Le), ("&&", Tok.AndAnd), ("||", Tok.OrOr),
        ("::", Tok.ColonColon), ("--", Tok.MinusMinus), ("++", Tok.PlusPlus), ("$+", Tok.DollarPlus),
        ("<<", Tok.Shl), (">>", Tok.Shr), ("+=", Tok.PlusAssign), ("-=", Tok.MinusAssign),
        ("*=", Tok.MulAssign), ("/=", Tok.DivAssign), ("%=", Tok.ModAssign), ("&=", Tok.AndAssign),
        ("^=", Tok.XorAssign), ("|=", Tok.OrAssign),
    };
    private const string kSingles = "@[]()+-*/<>|.!:;{},&%^~=";

    public Lexer(string source) => mSrc = source;

    public int Line() => mLine;

    private static bool IsAlpha(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
    private static bool IsDigit(char c) => c >= '0' && c <= '9';
    private static bool IsIdentChar(char c) => IsAlpha(c) || IsDigit(c) || c == ':';
    private static bool IsHex(char c) => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    private static int HexValue(char c) => IsDigit(c) ? c - '0' : (c | 0x20) - 'a' + 10;

    private char Peek(int ahead = 0) => mPos + ahead < mSrc.Length ? mSrc[mPos + ahead] : '\0';

    // src/script/lexer.cpp Lexer::make.
    private Token Make(Tok kind, string text = "")
    {
        var t = new Token { Kind = kind, Text = text, Line = mLine };
        if (t.Text.Length >= kTokenBuffer)
            t.Kind = Tok.Illegal; // the original exits on "Token buffer overflow" (D11)
        // Identifier, variable and tag text is interned while lexing (0x005f75c0): the first
        // spelling ever lexed, executed or not, is the one every later use prints.
        if (kind == Tok.Ident || kind == Tok.Var || kind == Tok.Tag)
            t.Text = StringTable.Global().Intern(t.Text);
        return t;
    }

    // src/script/lexer.cpp Lexer::next.
    public Token Next()
    {
        for (;;)
        {
            char ch = Peek();
            if (ch == '\0')
                return Make(Tok.End);
            if (ch == ' ' || ch == '\t' || ch == '\v' || ch == '\f' || ch == '\r')
            {
                ++mPos;
                continue;
            }
            if (ch == '\n')
            {
                ++mPos;
                ++mLine;
                continue;
            }
            if ((ch == '/' && Peek(1) == '/') || ch == '#')
            {
                while (Peek() != '\0' && Peek() != '\n' && Peek() != '\r')
                    ++mPos;
                continue;
            }
            break;
        }

        char c = Peek();
        if (c == '"')
            return LexDoubleQuoted();
        if (c == '\'')
            return LexSingleQuoted();
        if (IsDigit(c))
            return LexNumber();

        if (c == '$' || c == '%')
        {
            if (IsAlpha(Peek(1)))
            {
                int start = mPos++;
                while (IsIdentChar(Peek()))
                    ++mPos;
                return Make(Tok.Var, mSrc.Substring(start, mPos - start));
            }
            if (IsDigit(Peek(1)))
            {
                int i = 1;
                while (IsDigit(Peek(i)))
                    ++i;
                if (IsAlpha(Peek(i)))
                { // rule 71
                    int start = mPos;
                    mPos += i;
                    while (IsIdentChar(Peek()))
                        ++mPos;
                    return Make(Tok.Illegal, mSrc.Substring(start, mPos - start));
                }
            }
        }

        if (IsAlpha(c))
        {
            int start = mPos;
            while (IsIdentChar(Peek()))
                ++mPos;
            string word = mSrc.Substring(start, mPos - start);
            foreach (var (text, tok) in kKeywords)
                if (word == text)
                    return Make(tok, word);
            if (word.Length > 2 && word[0] == 'I' && word[1] == 'D')
                return Make(Tok.Tag, word);
            return Make(Tok.Ident, word);
        }

        foreach (var (text, tok) in kOps)
        {
            if (string.CompareOrdinal(mSrc, mPos, text, 0, text.Length) == 0 && mPos + text.Length <= mSrc.Length)
            {
                mPos += text.Length;
                return Make(tok, text);
            }
        }
        ++mPos;
        if (kSingles.IndexOf(c) >= 0)
        {
            Token t = Make(Tok.Char, c.ToString());
            t.Ch = c;
            return t;
        }
        return Make(Tok.Illegal, c.ToString());
    }

    // src/script/lexer.cpp Lexer::lexNumber.
    private Token LexNumber()
    {
        int start = mPos;
        if (Peek() == '0' && (Peek(1) == 'x' || Peek(1) == 'X') && IsHex(Peek(2)))
        {
            mPos += 2;
            while (IsHex(Peek()))
                ++mPos;
            return Make(Tok.Int, mSrc.Substring(start, mPos - start));
        }
        while (IsDigit(Peek()))
            ++mPos;
        int ExponentLength(int at)
        {
            int i = at;
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
        bool isFloat = false;
        if (Peek() == '.' && IsDigit(Peek(1)))
        {
            ++mPos;
            while (IsDigit(Peek()))
                ++mPos;
            mPos += ExponentLength(0);
            isFloat = true;
        }
        else
        {
            int n = ExponentLength(0);
            if (n != 0)
            {
                mPos += n;
                isFloat = true;
            }
        }
        return Make(isFloat ? Tok.Float : Tok.Int, mSrc.Substring(start, mPos - start));
    }

    // src/script/lexer.cpp Lexer::lexDoubleQuoted.
    private Token LexDoubleQuoted()
    {
        // Pattern: "([^"\\\n\r]|\\[^\n])*"
        int i = 1;
        for (;;)
        {
            char c = Peek(i);
            if (c == '"')
                break;
            if (c == '\0' || c == '\n' || c == '\r')
            {
                ++mPos; // no match: the quote alone is an ILLEGAL token
                return Make(Tok.Illegal, "\"");
            }
            if (c == '\\')
            {
                if (Peek(i + 1) == '\n' || Peek(i + 1) == '\0')
                {
                    ++mPos;
                    return Make(Tok.Illegal, "\"");
                }
                i += 2;
                continue;
            }
            ++i;
        }
        string raw = mSrc.Substring(mPos + 1, i - 1);
        mPos += i + 1;

        // Escapes (0x005f7644).
        var value = new System.Text.StringBuilder();
        for (int k = 0; k < raw.Length; ++k)
        {
            if (raw[k] != '\\')
            {
                value.Append(raw[k]);
                continue;
            }
            char e = raw[++k];
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
                value.Append((char)(HexValue(raw[k + 1]) * 16 + HexValue(raw[k + 2])));
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
        int i = 1;
        for (;;)
        {
            char c = Peek(i);
            if (c == '\'')
                break;
            if (c == '\0' || c == '\n' || (c == '\\' && (Peek(i + 1) == '\n' || Peek(i + 1) == '\0')))
            {
                ++mPos;
                return Make(Tok.Illegal, "'");
            }
            i += c == '\\' ? 2 : 1;
        }
        string raw = mSrc.Substring(mPos + 1, i - 1);
        mPos += i + 1;
        return Make(Tok.String, raw);
    }
}
