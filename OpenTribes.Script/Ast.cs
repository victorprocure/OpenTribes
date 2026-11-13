namespace OpenTribes.Script;

/// <summary>The kind of a syntax tree node, following the grammar in docs/script.md section 2.1.</summary>
/// <remarks>Port of src/script/ast.hpp. Each member lists the <see cref="Node"/> fields it uses.</remarks>
public enum NodeKind
{
    /// <summary>An integer literal.</summary>
    IntLiteral,
    /// <summary>A float literal.</summary>
    FloatLiteral,
    /// <summary>A string literal.</summary>
    StringLiteral,
    /// <summary>An identifier.</summary>
    Ident,
    /// <summary>A break word.</summary>
    BreakWord,
    /// <summary>A tag reference.</summary>
    TagRef,
    /// <summary>A variable: name and optional index.</summary>
    Var,
    /// <summary>A field: A is the object, plus name, optional index and optional second subscript in B.</summary>
    Field,
    /// <summary>A function call: name and the arguments in the list.</summary>
    Call,
    /// <summary>A method call: A is the object, plus name and the arguments in the list.</summary>
    MethodCall,
    /// <summary>A unary operation: operator and A.</summary>
    Unary,
    /// <summary>A binary operation: operator, A and B.</summary>
    Binary,
    /// <summary>An assignment: A is the lvalue and B the value.</summary>
    Assign,
    /// <summary>A field assigned a list: A is the field and the list holds the values.</summary>
    FieldListAssign,
    /// <summary>A compound assignment: operator, A is the lvalue and B the value.</summary>
    CompoundAssign,
    /// <summary>A post increment or decrement: operator '+' or '-' and A is the lvalue.</summary>
    PostIncDec,
    /// <summary>An expression statement: A.</summary>
    ExprStmt,
    /// <summary>An if statement: A is the condition, plus body and the else body when HasElse is set.</summary>
    If,
    /// <summary>A while loop: A is the condition, plus body.</summary>
    While,
    /// <summary>A for loop: A, B and C, plus body.</summary>
    For,
    /// <summary>A function definition: name, parameters and body.</summary>
    Function,
    /// <summary>A return statement with an optional A.</summary>
    Return,
    /// <summary>A continue statement.</summary>
    Continue,
    /// <summary>A break statement.</summary>
    BreakStmt,
    /// <summary>A tag declaration: name, A is the id and B the optional text.</summary>
    TagDecl,
    /// <summary>A datablock: class name, optional name and slots.</summary>
    Datablock,
    /// <summary>An instant: class name, optional name expression in A, slots and child instants.</summary>
    Instant,
    /// <summary>A slot: name, optional index, optional second subscript in B and A as the value or list.</summary>
    Slot,
}

/// <summary>A syntax tree node.</summary>
/// <remarks>The C++ unique_ptr children are plain references. A failed parse may leave null entries in the lists of a tree that is then discarded.</remarks>
public sealed class Node
{
    /// <summary>The node kind.</summary>
    public NodeKind Kind { get; set; }
    /// <summary>The source line.</summary>
    public int Line { get; set; }
    /// <summary>The operator token kind.</summary>
    public Tok Op { get; set; } = Tok.End;
    /// <summary>The operator character for <c>Tok.Char</c> operators.</summary>
    public char OpChar { get; set; }
    /// <summary>The identifier, variable, field, function or class name, or the literal text.</summary>
    public string Name { get; set; } = "";
    /// <summary>The class name of a datablock or instant.</summary>
    public string ClassName { get; set; } = "";
    /// <summary>Whether the expression was written in parentheses.</summary>
    public bool Parenthesized { get; set; }
    /// <summary>Whether an if statement has an else branch.</summary>
    public bool HasElse { get; set; }
    /// <summary>Whether a slot has a <c>{a, b}</c> value.</summary>
    public bool IsList { get; set; }
    /// <summary>The first child.</summary>
    public Node? A { get; set; }
    /// <summary>The second child.</summary>
    public Node? B { get; set; }
    /// <summary>The third child.</summary>
    public Node? C { get; set; }
    /// <summary>The subscript expressions.</summary>
    public List<Node> Index { get; } = [];
    /// <summary>The arguments or values.</summary>
    public List<Node> List { get; } = [];
    /// <summary>The statements of the body.</summary>
    public List<Node> Body { get; } = [];
    /// <summary>The statements of the else body.</summary>
    public List<Node> ElseBody { get; } = [];
    /// <summary>The slots of a datablock or instant.</summary>
    public List<Node> Slots { get; } = [];
    /// <summary>The child instants of an instant.</summary>
    public List<Node> Children { get; } = [];
    /// <summary>The parameter names of a function.</summary>
    public List<string> Params { get; } = [];
}
