// The IRC client's tick [code 005fc340]: $IRC::MsgHistory below 50 or above 500 (an unset variable reads 0) is replaced by the
// client's own buffer size, 50 at start. So the IRC options show 50 before any script or user sets the field: the options script
// (IRCOptions::init) only defaults an empty variable to 500.
using OpenTribes.Core;
using OpenTribes.Script;

namespace OpenTribes.Client.App;

public sealed class IrcHistory
{
    public const string Variable = "$IRC::MsgHistory";
    public int Size { get; private set; } = 50;

    public void Keep(Interpreter interp)
    {
        int v = Numbers.BorlandAtoi(interp.PeekVariable(Variable));
        if (v < 50 || v > 500)
            interp.SetVariable(Variable, Numbers.FormatInt(Size));
        else
            Size = v;
    }
}
