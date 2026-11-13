// Port of src/game/client_game_commands.hpp and src/game/client_game_commands.cpp: client-side game commands that need
// the object model: chat menus (ChatMenu objects exist on a dedicated server). docs/mods.md 5.5.
namespace OpenTribes.Game;

/// <summary>A node of the menu tree of a ChatMenu object (0x00402230).</summary>
/// <remarks>A menu string is a path of items separated by <c>\</c>. The first character of an item is its key and the rest its text.</remarks>
public sealed class ChatMenuNode
{
    /// <summary>The key of the item.</summary>
    public char Key;
    /// <summary>The item text, at most 31 characters.</summary>
    public string Text = "";                     // at most 31 characters
    /// <summary>True for a leaf, which runs commands.</summary>
    public bool Leaf;
    /// <summary>The command words of a leaf, at most 10.</summary>
    public List<string> Commands = [];
    /// <summary>The child items.</summary>
    public readonly List<ChatMenuNode> Children = [];
    /// <summary>The parent node, or null at the root.</summary>
    public ChatMenuNode? Parent;
}

/// <summary>The menu tree of one ChatMenu object.</summary>
public sealed class ChatMenuTree
{
    /// <summary>The root node.</summary>
    public readonly ChatMenuNode Root = new();

    /// <summary>Creates an empty tree.</summary>
    public ChatMenuTree() => mLevel = Root;

    private ChatMenuNode mLevel; // the node whose children are on display (Root at the top)
    private int mReleaseKey;      // scan code of the key press this menu consumed: its release is consumed too [code 00402068, field +0x6c]

    /// <summary>The menu mode, 0 to 2. Setting it stores the mode and returns the display to the top level.</summary>
    /// <remarks>Follows setCMMode (00401fe0).</remarks>
    public int Mode
    {
        get;
        set
        {
            field = value;
            mLevel = Root;
        }
    }

    /// <summary>Gets whether the chat display draws the menu: the mode is not 0 and the top level has items.</summary>
    /// <remarks>Follows 004a633c (mode and level both non-zero).</remarks>
    public bool Showing => Mode != 0 && mLevel.Children.Count > 0;

    /// <summary>The node whose children are listed now, or null when nothing is shown.</summary>
    public ChatMenuNode? Level => Showing ? mLevel : null;

    /// <summary>Returns the path of headings above the shown level.</summary>
    /// <returns>The headings, root first, joined with ':'. Empty at the top level, where the menu object's own title is shown.</returns>
    /// <remarks>Follows the title loop of 004a633c.</remarks>
    public string Path()
    {
        var names = new List<string>();
        for (ChatMenuNode? n = Level; n != null && n != Root; n = n.Parent)
            names.Add(n.Text);
        names.Reverse();
        return string.Join(':', names);
    }

    /// <summary>Returns the item lines of the shown level.</summary>
    /// <returns>The lines, as "key: text".</returns>
    /// <remarks>Follows 004a633c.</remarks>
    public IReadOnlyList<string> Lines() =>
        Level == null ? Array.Empty<string>() : Level.Children.Select(c => c.Key + ": " + c.Text).ToList();

    /// <summary>Offers one keyboard event to the menu.</summary>
    /// <param name="scan">The key code (ObjInst).</param>
    /// <param name="action">1 for press, 2 for release.</param>
    /// <param name="ch">The character of the event.</param>
    /// <param name="mods">The modifier byte of the event.</param>
    /// <param name="run">Runs the command words of a leaf, the function or command name first.</param>
    /// <param name="cancelOrder">The order cursor of the command map for the command menu. Escape resets it and, when it was on, the key is taken even at the top level of the menu.</param>
    /// <returns>True when the menu consumed the event.</returns>
    /// <remarks>Follows the InputEvent handler (00402068).</remarks>
    public bool OnKey(int scan, int action, char ch, byte mods, Action<IReadOnlyList<string>> run, Func<bool>? cancelOrder = null)
    {
        if (action == 2)
        {
            if (mReleaseKey != 0 && scan == mReleaseKey)
            {
                mReleaseKey = 0;
                return true;
            }
            return false;
        }
        if (Mode == 0 || action != 1)
            return false;
        if (scan == 1) // Escape [code 00402068]
        {
            mReleaseKey = 1;
            var wasOrdering = cancelOrder?.Invoke() ?? false;
            if (Mode == 2)
            {
                Mode = 0;
                return true;
            }
            if (mLevel == Root)
                return wasOrdering;
            mLevel = Root;
            return true;
        }
        if (mods != 0 || ch == '\0')
            return false;
        // [code 004021cc]: the item with that key runs its command and opens its sub level; no match leaves the key to the game.
        var hit = false;
        ChatMenuNode? found = mLevel.Children.FirstOrDefault(c => c.Key == ch);
        if (found != null)
        {
            hit = true;
            if (found.Commands.Count > 0)
                run(found.Commands);
            // The command may have switched the menu (setCMMode resets the level); the original reads the item's link afterwards
            // and overwrites the level with it.
            if (found.Leaf)
            {
                mLevel = Root;
                if (Mode == 2)
                    Mode = 0;
            }
            else
                mLevel = found;
        }
        if (hit)
            mReleaseKey = scan;
        return hit;
    }

    /// <summary>Adds a menu item path to the tree.</summary>
    /// <param name="menuString">The path of items, separated by <c>\</c>.</param>
    /// <param name="commands">The command words of the leaf.</param>
    /// <returns>False for an empty string, an empty item, or when the last item's key already exists at its level.</returns>
    public bool Add(string menuString, IReadOnlyList<string> commands)
    {
        ChatMenuNode level = Root;
        var p = 0;
        for (; ; )
        {
            if (p >= menuString.Length)
                return false;
            var key = menuString[p++];
            var end = menuString.IndexOf('\\', p);
            var text = end < 0 ? menuString[p..] : menuString[p..end];
            if (text.Length == 0)
                return false;
            var last = end < 0;
            p = last ? menuString.Length : end + 1;
            ChatMenuNode? found = null;
            foreach (ChatMenuNode c in level.Children)
                if (c.Key == key)
                {
                    found = c;
                    break;
                }
            if (found != null)
            {
                if (last)
                    return false;
                level = found;
                continue;
            }
            var node = new ChatMenuNode { Key = key, Text = text.Length > 31 ? text[..31] : text, Leaf = last, Parent = level };
            if (last)
                node.Commands = [.. commands.Take(Math.Min(commands.Count, 10))];
            level.Children.Add(node);
            if (last)
                return true;
            level = node;
        }
    }
}

/// <summary>The chat menus of one console: a tree per ChatMenu object and the menu a script switched on last.</summary>
public sealed class ChatMenuRegistry
{
    private readonly Dictionary<SimObject, (int Id, ChatMenuTree Tree)> mTrees = new(ReferenceEqualityComparer.Instance);
    private SimObject? mActive;
    private string mActiveName = "";
    private readonly Dictionary<SimObject, string> mNames = new(ReferenceEqualityComparer.Instance);

    /// <summary>Returns the tree of a ChatMenu object, creating it on first use.</summary>
    /// <param name="obj">The ChatMenu object. An entry is keyed by object and id, and an entry whose id no longer matches is stale.</param>
    /// <returns>The tree.</returns>
    public ChatMenuTree Of(SimObject obj)
    {
        if (!mTrees.TryGetValue(obj, out var slot) || slot.Id != obj.Id())
        {
            slot = (obj.Id(), new ChatMenuTree());
            mTrees[obj] = slot;
        }
        return slot.Tree;
    }

    /// <summary>The name of the command map menu. Switching it off ends the order cursor (00401fe0, name test).</summary>
    public const string CommandMenuName = "CommandChatMenu";
    /// <summary>Called when the command menu is switched off.</summary>
    public Action? CommandMenuOff;
    /// <summary>Cancels the order cursor on Escape in the command menu and tells whether it was on (00402068).</summary>
    public Func<bool>? CancelOrder;

    /// <summary>Sets the mode of a menu. A menu set above 0 is the one on display. Setting it to 0 removes it.</summary>
    /// <param name="obj">The ChatMenu object.</param>
    /// <param name="name">The object name.</param>
    /// <param name="mode">The new mode.</param>
    public void SetMode(SimObject obj, string name, int mode)
    {
        Of(obj).Mode = mode;
        mNames[obj] = name;
        if (mode == 0 && string.Equals(name, CommandMenuName, StringComparison.OrdinalIgnoreCase))
            CommandMenuOff?.Invoke();
        if (mode > 0)
        {
            mActive = obj;
            mActiveName = name;
        }
        else if (ReferenceEquals(mActive, obj))
            mActive = null;
    }

    /// <summary>Offers a keyboard event to every menu that is on, in order. The first menu that consumes it wins.</summary>
    /// <param name="game">The game state.</param>
    /// <param name="scan">The key code.</param>
    /// <param name="action">1 for press, 2 for release.</param>
    /// <param name="ch">The character of the event.</param>
    /// <param name="mods">The modifier byte of the event.</param>
    /// <param name="run">Runs the command words of a leaf.</param>
    /// <returns>True when a menu consumed the event.</returns>
    /// <remarks>The menus are members of the input consumer set (sim.md GS4).</remarks>
    public bool OnKey(GameState game, int scan, int action, char ch, byte mods, Action<IReadOnlyList<string>> run)
    {
        foreach (var (obj, slot) in mTrees.ToArray())
        {
            if (slot.Id != obj.Id() || !mNames.TryGetValue(obj, out var name) ||
                game.Sim?.Server()?.Find(name) is not SimObject now || !ReferenceEquals(now, obj))
                continue;
            if ((slot.Tree.Mode != 0 || action == 2) &&
                slot.Tree.OnKey(scan, action, ch, mods, run, string.Equals(name, CommandMenuName, StringComparison.OrdinalIgnoreCase) ? CancelOrder : null))
                return true;
        }
        return false;
    }

    /// <summary>Returns the tree of a named menu while it is on and shows items.</summary>
    /// <param name="game">The game state.</param>
    /// <param name="name">The menu name.</param>
    /// <returns>The title and tree, or null.</returns>
    public (string Title, ChatMenuTree Tree)? Showing(GameState game, string name)
    {
        if (game.Sim?.Server()?.Find(name) is not SimObject obj || obj.ClassName() != "ChatMenu")
            return null;
        ChatMenuTree tree = Of(obj);
        return tree.Showing ? (obj is SimClasses.ChatMenu m ? m.Title : "", tree) : null;
    }

    /// <summary>Returns the menu on display.</summary>
    /// <param name="game">The game state.</param>
    /// <returns>The title and tree, or null when none is on or its object was deleted.</returns>
    public (string Title, ChatMenuTree Tree)? Active(GameState game)
    {
        if (mActive == null || game.Sim?.Server()?.Find(mActiveName) is not SimObject now || !ReferenceEquals(now, mActive))
            return null;
        ChatMenuTree tree = Of(mActive);
        return tree.Mode == 0 ? null : (mActive is SimClasses.ChatMenu m ? m.Title : "", tree);
    }
}

public sealed partial class GameState
{
    /// <summary>The chat menus of the console.</summary>
    public readonly ChatMenuRegistry ChatMenus = new();
}

/// <summary>The client side game commands.</summary>
public static class ClientGameCommands
{
    // src/game/client_game_commands.cpp findMenu.
    private static SimObject? FindMenu(GameState game, string name) =>
        game.Sim?.Server()?.Find(name) is SimObject obj && obj.ClassName() == "ChatMenu" ? obj : null;

    /// <summary>Registers the client game commands.</summary>
    /// <param name="interp">The script interpreter.</param>
    /// <param name="game">The game state the commands act on.</param>
    public static void Register(Interpreter interp, GameState game)
    {
        ChatMenuRegistry menus = game.ChatMenus;

        // FearPlugin 0x00424798 case 0x14.
        interp.AddCommand("addCMCommand", (inr, a) =>
        {
            if (a.Length < 4)
            {
                inr.Print(1, "addCMCommand(object, menuString, command, ...);");
                return null;
            }
            SimObject? obj = FindMenu(game, a[1]);
            if (obj == null)
            {
                inr.Print(1, "addCMCommand - invalid menu object.");
                return null;
            }
            if (!menus.Of(obj).Add(a[2], a[3..]))
                inr.Print(1, "Error adding menu to " + a[1]);
            return null;
        });

        // Case 0x15: modes 0-2 are stored, other values are ignored.
        interp.AddCommand("setCMMode", (inr, a) =>
        {
            if (a.Length != 3)
            {
                inr.Print(1, "setCMMode(menuObject, mode);");
                return null;
            }
            SimObject? obj = FindMenu(game, a[1]);
            if (obj == null)
            {
                inr.Print(1, "setCMMode - invalid menu object.");
                return null;
            }
            var mode = Numbers.BorlandAtoi(a[2]);
            if (mode is >= 0 and < 3)
                menus.SetMode(obj, a[1], mode);
            return null;
        });
    }
}
