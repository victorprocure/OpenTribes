namespace OpenTribes.Client.App;

using OpenTribes.Script;

/// <summary>The size of the IRC message history.</summary>
/// <remarks>Follows the IRC client tick (005fc340): $IRC::MsgHistory below 50 or above 500 (an unset variable reads 0) is replaced by the client's own buffer size, 50 at start. So the IRC options show 50 before any script or user sets the field: the options script (IRCOptions::init) only defaults an empty variable to 500.</remarks>
public sealed class IrcHistory
{
    /// <summary>The script variable that holds the history size.</summary>
    public const string Variable = "$IRC::MsgHistory";
    /// <summary>The current history size, 50 at start.</summary>
    public int Size { get; private set; } = 50;

    /// <summary>Takes the variable as the new size, or writes the current size back when it is out of range.</summary>
    /// <param name="interp">The script interpreter that owns the variable.</param>
    public void Keep(Interpreter interp)
    {
        var v = Numbers.BorlandAtoi(interp.PeekVariable(Variable));
        if (v is < 50 or > 500)
            interp.SetVariable(Variable, Numbers.FormatInt(Size));
        else
            Size = v;
    }
}
