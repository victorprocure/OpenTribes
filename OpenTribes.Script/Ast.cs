// Port of src/script/ast.hpp: script syntax tree. Node kinds follow the grammar in docs/script.md section 2.1.
namespace OpenTribes.Script;

// src/script/ast.hpp enum class NodeKind.
public enum NodeKind
{
    // expressions
    Int, Float, String, Ident, BreakWord, TagRef,
    Var,          // name; index (optional)
    Field,        // a = object; name; index (optional); b = second subscript (optional)
    Call,         // name; list = args
    MethodCall,   // a = object; name; list = args
    Unary,        // op; a
    Binary,       // op; a, b
    Assign,       // a = lvalue; b = value
    FieldListAssign,  // a = field; list = values
    CompoundAssign,   // op; a = lvalue; b = value
    PostIncDec,       // op ('+' or '-'); a = lvalue
    // statements
    ExprStmt,     // a
    If,           // a = cond; body; elseBody (hasElse)
    While,        // a = cond; body
    For,          // a, b, c; body
    Function,     // name; params; body
    Return,       // a (optional)
    Continue,
    BreakStmt,
    TagDecl,      // name; a = id; b = text (optional)
    Datablock,    // className; name (optional); slots
    Instant,      // className; a = name expr (optional); slots; children (instants)
    Slot,         // name; index (optional); b = second subscript (optional); a = value or list
}

// src/script/ast.hpp struct Node. The C++ unique_ptr children are plain references; a failed parse
// may leave null entries in the lists of a tree that is then discarded.
public sealed class Node
{
    public NodeKind Kind;
    public int Line;
    public Tok Op = Tok.End; // operator token kind
    public char OpChar;      // operator character for Tok.Char operators
    public string Name = ""; // identifier, variable, field, function or class name; literal text
    public string ClassName = "";
    public bool Parenthesized;
    public bool HasElse;
    public bool IsList;      // Slot with {a, b} value
    public Node? A, B, C;
    public readonly List<Node> Index = new();    // subscript expressions
    public readonly List<Node> List = new();     // arguments, values
    public readonly List<Node> Body = new(), ElseBody = new();
    public readonly List<Node> Slots = new(), Children = new();
    public readonly List<string> Params = new();
}
