namespace OpenTribes.Script;

/// <summary>The result of a parse.</summary>
public sealed class ParseResult
{
    /// <summary>The complete top-level statements, in order.</summary>
    public readonly List<Node> Statements = [];
    /// <summary>Whether a syntax error occurred.</summary>
    public bool Error;
    /// <summary>The line of the syntax error.</summary>
    public int ErrorLine;
    /// <summary>The token at the syntax error.</summary>
    public Token ErrorToken = new();
}

/// <summary>The script parser. It accepts the same language as the original LALR parser (CMDparse, 005ee998), and top-level statements completed before a syntax error are kept.</summary>
/// <remarks>Port of src/script/parser.cpp.</remarks>
public static class ScriptParser
{
    /// <summary>Parses a script.</summary>
    /// <param name="source">The script text.</param>
    /// <returns>The statements and any error.</returns>
    public static ParseResult Parse(string source) => new Parser(source).Run();

    /// <summary>Builds the message the original prints for a syntax error (005f8010).</summary>
    /// <param name="r">The parse result.</param>
    /// <param name="fileName">The file name, empty for console input.</param>
    /// <returns>The message.</returns>
    public static string SyntaxErrorMessage(ParseResult r, string fileName)
    {
        if (fileName.Length == 0)
            return "Syntax error in input.";
        return fileName + " Line: " + r.ErrorLine.ToString(System.Globalization.CultureInfo.InvariantCulture) + " - Syntax error.";
    }

    // src/script/parser.cpp class Parser. Errors unwind through mFailed checks, as in the C++.
    private sealed class Parser
    {
        private readonly Lexer mLex;
        private Token mTok, mNext;
        private bool mFailed;
        private Token mErrTok = new();

        public Parser(string src)
        {
            mLex = new Lexer(src);
            mTok = mLex.Next();
            mNext = mLex.Next();
        }

        public ParseResult Run()
        {
            var r = new ParseResult();
            while (mTok.Kind != Tok.End)
            {
                Node? s = Statement();
                if (mFailed)
                {
                    r.Error = true;
                    r.ErrorToken = mErrTok;
                    r.ErrorLine = mErrTok.Line;
                    break;
                }
                r.Statements.Add(s!);
            }
            return r;
        }

        // ---- token helpers ----
        private void Advance()
        {
            mTok = mNext;
            mNext = mLex.Next();
        }
        private static bool IsChar(Token t, char c) => t.Kind == Tok.Char && t.Ch == c;
        private bool At(char c) => IsChar(mTok, c);
        private bool At(Tok k) => mTok.Kind == k;
        private Node? Fail()
        {
            if (!mFailed)
            {
                mFailed = true;
                mErrTok = mTok;
            }
            return null;
        }
        private bool Expect(char c)
        {
            if (mFailed)
                return false;
            if (!At(c))
            {
                Fail();
                return false;
            }
            Advance();
            return true;
        }
        private Node MakeNode(NodeKind k) => new() { Kind = k, Line = mTok.Line };

        // ---- statements ----
        private Node? Statement()
        {
            if (mFailed)
                return null;
            switch (mTok.Kind)
            {
                case Tok.If:
                    return IfStatement();
                case Tok.While:
                    return WhileStatement();
                case Tok.For:
                    return ForStatement();
                case Tok.Function:
                    return FunctionDecl();
                case Tok.Instant:
                    return InstantDecl();
                case Tok.Return:
                {
                    Node n = MakeNode(NodeKind.Return);
                    Advance();
                    if (!At(';'))
                        n.A = Expression();
                    Expect(';');
                    return n;
                }
                case Tok.Continue:
                {
                    Node n = MakeNode(NodeKind.Continue);
                    Advance();
                    Expect(';');
                    return n;
                }
                case Tok.Break:
                    if (IsChar(mNext, ';'))
                    {
                        Node n = MakeNode(NodeKind.BreakStmt);
                        Advance();
                        Advance();
                        return n;
                    }
                    break;
                case Tok.Tag:
                    if (IsChar(mNext, '='))
                        return TagDecl();
                    break;
                case Tok.Ident:
                    if (mNext.Kind == Tok.Ident || IsChar(mNext, '{'))
                        return DatablockDecl();
                    break;
                default:
                    break;
            }
            return ExpressionStatement();
        }

        private Node? ExpressionStatement()
        {
            Node n = MakeNode(NodeKind.ExprStmt);
            n.A = Expression();
            if (mFailed)
                return null;
            if (!IsStatementExpression(n.A!))
                return Fail();
            Expect(';');
            return n;
        }

        private static bool IsStatementExpression(Node e)
        {
            if (e.Parenthesized)
                return false;
            return e.Kind switch
            {
                NodeKind.Assign or NodeKind.FieldListAssign or NodeKind.CompoundAssign or NodeKind.PostIncDec or NodeKind.Call or NodeKind.MethodCall => true,
                _ => false,
            };
        }

        private void Block(List<Node> output)
        {
            if (At('{'))
            {
                Advance();
                while (!mFailed && !At('}'))
                {
                    if (At(Tok.End))
                    {
                        Fail();
                        return;
                    }
                    output.Add(Statement()!);
                }
                Expect('}');
            }
            else
            {
                output.Add(Statement()!);
            }
        }

        private Node? IfStatement()
        {
            Node n = MakeNode(NodeKind.If);
            Advance();
            Expect('(');
            n.A = Expression();
            Expect(')');
            if (mFailed)
                return null;
            Block(n.Body);
            if (!mFailed && At(Tok.Else))
            {
                Advance();
                n.HasElse = true;
                Block(n.ElseBody);
            }
            return n;
        }

        private Node? WhileStatement()
        {
            Node n = MakeNode(NodeKind.While);
            Advance();
            Expect('(');
            n.A = Expression();
            Expect(')');
            if (mFailed)
                return null;
            Block(n.Body);
            return n;
        }

        private Node? ForStatement()
        {
            Node n = MakeNode(NodeKind.For);
            Advance();
            Expect('(');
            n.A = Expression();
            Expect(';');
            n.B = Expression();
            Expect(';');
            n.C = Expression();
            Expect(')');
            if (mFailed)
                return null;
            Block(n.Body);
            return n;
        }

        private Node? FunctionDecl()
        {
            Node n = MakeNode(NodeKind.Function);
            Advance();
            if (!At(Tok.Ident))
                return Fail();
            n.Name = mTok.Text;
            Advance();
            Expect('(');
            if (!mFailed && At(Tok.Var))
            {
                for (; ; )
                {
                    n.Params.Add(mTok.Text);
                    Advance();
                    if (!At(','))
                        break;
                    Advance();
                    if (!At(Tok.Var))
                        return Fail();
                }
            }
            Expect(')');
            Expect('{');
            while (!mFailed && !At('}'))
            {
                if (At(Tok.End))
                    return Fail();
                n.Body.Add(Statement()!);
            }
            Expect('}');
            return n;
        }

        private Node TagDecl()
        {
            Node n = MakeNode(NodeKind.TagDecl);
            n.Name = mTok.Text;
            Advance(); // TAG
            Advance(); // =
            n.A = Expression();
            if (!mFailed && At(','))
            {
                Advance();
                n.B = Expression();
            }
            Expect(';');
            return n;
        }

        private Node? DatablockDecl()
        {
            Node n = MakeNode(NodeKind.Datablock);
            n.ClassName = mTok.Text;
            Advance();
            if (At(Tok.Ident))
            {
                n.Name = mTok.Text;
                Advance();
                if (At(';'))
                {
                    Advance();
                    return n;
                }
            }
            Expect('{');
            if (mFailed)
                return null;
            do
            {
                n.Slots.Add(Slot()!);
            } while (!mFailed && !At('}'));
            Expect('}');
            Expect(';');
            return n;
        }

        private Node? Slot()
        {
            if (!At(Tok.Ident))
                return Fail();
            Node n = MakeNode(NodeKind.Slot);
            n.Name = mTok.Text;
            Advance();
            if (At('['))
            {
                Advance();
                IndexList(n.Index);
                Expect(']');
                if (!mFailed && At('['))
                {
                    Advance();
                    n.B = Expression();
                    Expect(']');
                    Expect('=');
                    n.A = Expression();
                    Expect(';');
                    return n;
                }
            }
            Expect('=');
            if (!mFailed && At('{'))
            {
                Advance();
                n.IsList = true;
                ExprList(n.List);
                Expect('}');
            }
            else if (!mFailed)
            {
                n.A = Expression();
            }
            Expect(';');
            return n;
        }

        private Node? InstantDecl()
        {
            Node n = MakeNode(NodeKind.Instant);
            Advance();
            if (!At(Tok.Ident))
                return Fail();
            n.ClassName = mTok.Text;
            Advance();
            if (!At('{') && !At(';'))
                n.A = Expression();
            if (!mFailed && At('{'))
            {
                Advance();
                while (!mFailed && At(Tok.Ident))
                    n.Slots.Add(Slot()!);
                while (!mFailed && At(Tok.Instant))
                    n.Children.Add(InstantDecl()!);
                if (!mFailed && n.Slots.Count == 0 && n.Children.Count == 0)
                    return Fail();
                Expect('}');
            }
            Expect(';');
            return n;
        }

        // ---- expressions ----
        private void IndexList(List<Node> output)
        {
            output.Add(Expression()!);
            while (!mFailed && At(','))
            {
                Advance();
                output.Add(Expression()!);
            }
        }
        private void ExprList(List<Node> output) => IndexList(output);

        private static int Precedence(Token t) => t.Kind switch
        {
            Tok.OrOr => 1,
            Tok.AndAnd => 2,
            Tok.Eq or Tok.Ne => 6,
            Tok.Le or Tok.Ge => 7,
            Tok.DollarPlus => 8,
            Tok.Shl or Tok.Shr => 9,
            Tok.Char => t.Ch switch
            {
                '|' => 3,
                '^' => 4,
                '&' => 5,
                '<' or '>' => 7,
                '@' => 8,
                '+' or '-' => 10,
                '*' or '/' or '%' => 11,
                _ => 0,
            },
            _ => 0,
        };

        private Node? Expression(int minPrec = 1)
        {
            Node? left = Unary();
            while (!mFailed)
            {
                var prec = Precedence(mTok);
                if (prec < minPrec || prec == 0)
                    break;
                Node n = MakeNode(NodeKind.Binary);
                n.Op = mTok.Kind;
                n.OpChar = mTok.Ch;
                Advance();
                n.A = left;
                n.B = Expression(prec + 1); // left-associative
                left = n;
            }
            return mFailed ? null : left;
        }

        private Node? Unary()
        {
            if (mFailed)
                return null;
            if (At('-') || At('!') || At('~') || At('*'))
            {
                Node n = MakeNode(NodeKind.Unary);
                n.Op = Tok.Char;
                n.OpChar = mTok.Ch;
                Advance();
                n.A = Unary();
                return mFailed ? null : n;
            }
            return Postfix(Primary());
        }

        private Node? Primary()
        {
            if (mFailed)
                return null;
            Node n;
            switch (mTok.Kind)
            {
                case Tok.Int:
                    n = MakeNode(NodeKind.Int);
                    n.Name = mTok.Text;
                    Advance();
                    return n;
                case Tok.Float:
                    n = MakeNode(NodeKind.Float);
                    n.Name = mTok.Text;
                    Advance();
                    return n;
                case Tok.String:
                    n = MakeNode(NodeKind.String);
                    n.Name = mTok.Text;
                    Advance();
                    return n;
                case Tok.Tag:
                    n = MakeNode(NodeKind.TagRef);
                    n.Name = mTok.Text;
                    Advance();
                    return n;
                case Tok.Break:
                    n = MakeNode(NodeKind.BreakWord);
                    n.Name = "break";
                    Advance();
                    return n;
                case Tok.Ident:
                    if (IsChar(mNext, '('))
                    {
                        n = MakeNode(NodeKind.Call);
                        n.Name = mTok.Text;
                        Advance();
                        Advance();
                        if (!At(')'))
                            ExprList(n.List);
                        Expect(')');
                        return mFailed ? null : n;
                    }
                    n = MakeNode(NodeKind.Ident);
                    n.Name = mTok.Text;
                    Advance();
                    return n;
                case Tok.Var:
                    n = MakeNode(NodeKind.Var);
                    n.Name = mTok.Text;
                    Advance();
                    if (At('['))
                    {
                        Advance();
                        IndexList(n.Index);
                        Expect(']');
                    }
                    return mFailed ? null : n;
                case Tok.Char:
                    if (mTok.Ch == '(')
                    {
                        Advance();
                        Node? e = Expression();
                        Expect(')');
                        if (mFailed)
                            return null;
                        e!.Parenthesized = true;
                        return e;
                    }
                    return Fail();
                default:
                    return Fail();
            }
        }

        private static bool IsLvalue(Node n) => !n.Parenthesized && (n.Kind == NodeKind.Var || n.Kind == NodeKind.Field);

        private static bool IsCompound(Tok k) => k switch
        {
            Tok.PlusAssign or Tok.MinusAssign or Tok.MulAssign or Tok.DivAssign or Tok.ModAssign or Tok.AndAssign or Tok.XorAssign or Tok.OrAssign or Tok.ShlAssign or Tok.ShrAssign => true,
            _ => false,
        };

        private Node? Postfix(Node? e)
        {
            while (e != null && !mFailed)
            {
                if (At('.'))
                {
                    Advance();
                    if (!At(Tok.Ident))
                        return Fail();
                    var name = mTok.Text;
                    var line = mTok.Line;
                    Advance();
                    if (At('('))
                    {
                        Node m = MakeNode(NodeKind.MethodCall);
                        m.Line = line;
                        m.Name = name;
                        m.A = e;
                        Advance();
                        if (!At(')'))
                            ExprList(m.List);
                        Expect(')');
                        e = m;
                        continue;
                    }
                    Node f = MakeNode(NodeKind.Field);
                    f.Line = line;
                    f.Name = name;
                    f.A = e;
                    if (At('['))
                    {
                        Advance();
                        IndexList(f.Index);
                        Expect(']');
                        if (!mFailed && At('['))
                        {
                            Advance();
                            f.B = Expression();
                            Expect(']');
                        }
                    }
                    e = f;
                    continue;
                }
                if (IsLvalue(e))
                {
                    if (At('='))
                    {
                        Advance();
                        if (e.Kind == NodeKind.Field && At('{'))
                        {
                            Node l = MakeNode(NodeKind.FieldListAssign);
                            Advance();
                            l.A = e;
                            ExprList(l.List);
                            Expect('}');
                            e = l;
                            continue;
                        }
                        Node s = MakeNode(NodeKind.Assign);
                        s.A = e;
                        s.B = Expression();
                        e = s;
                        continue;
                    }
                    if (IsCompound(mTok.Kind))
                    {
                        Node s = MakeNode(NodeKind.CompoundAssign);
                        s.Op = mTok.Kind;
                        Advance();
                        s.A = e;
                        s.B = Expression();
                        e = s;
                        continue;
                    }
                    if (At(Tok.PlusPlus) || At(Tok.MinusMinus))
                    {
                        Node s = MakeNode(NodeKind.PostIncDec);
                        s.OpChar = At(Tok.PlusPlus) ? '+' : '-';
                        Advance();
                        s.A = e;
                        e = s;
                        continue;
                    }
                }
                break;
            }
            return mFailed ? null : e;
        }
    }
}
