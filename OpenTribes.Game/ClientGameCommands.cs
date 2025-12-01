// Port of src/game/client_game_commands.hpp and src/game/client_game_commands.cpp: client-side game commands that need
// the object model: chat menus (ChatMenu objects exist on a dedicated server). docs/mods.md 5.5.
namespace OpenTribes.Game;

// src/game/client_game_commands.hpp struct ChatMenuNode: menu tree of a ChatMenu object (0x00402230). A menu string is a
// path of items separated by `\`; the first character of an item is its key, the rest its text.
public sealed class ChatMenuNode
{
    public char Key;
    public string Text = "";                     // at most 31 characters
    public bool Leaf;
    public List<string> Commands = new();        // leaf: at most 10 words
    public readonly List<ChatMenuNode> Children = new();
    public ChatMenuNode? Parent;
}

// src/game/client_game_commands.hpp struct ChatMenuTree.
public sealed class ChatMenuTree
{
    public readonly ChatMenuNode Root = new();

    public ChatMenuTree() => mLevel = Root;

    private int mMode; // setCMMode, 0-2
    private ChatMenuNode mLevel; // the node whose children are on display (Root at the top)
    private int mReleaseKey;      // scan code of the key press this menu consumed: its release is consumed too [code 00402068, field +0x6c]

    // setCMMode [code 00401fe0]: modes 0-2 are stored and the display goes back to the top level.
    public int Mode
    {
        get => mMode;
        set
        {
            mMode = value;
            mLevel = Root;
        }
    }

    // The top level has items and the menu is on: what the chat display draws [code 004a633c: mode and level both non-zero].
    public bool Showing => mMode != 0 && mLevel.Children.Count > 0;

    // The node whose children are listed now, null when nothing is shown.
    public ChatMenuNode? Level => Showing ? mLevel : null;

    // The path of headings above the shown level, root first, joined with ':' (empty at the top level, where the menu object's own
    // title is shown) [code 004a633c title loop].
    public string Path()
    {
        var names = new List<string>();
        for (ChatMenuNode? n = Level; n != null && n != Root; n = n.Parent)
            names.Add(n.Text);
        names.Reverse();
        return string.Join(':', names);
    }

    // The item lines of the shown level, "key: text" [code 004a633c].
    public IReadOnlyList<string> Lines() =>
        Level == null ? Array.Empty<string>() : Level.Children.Select(c => c.Key + ": " + c.Text).ToList();

    // One keyboard event offered to the menu (InputEvent handler [code 00402068]). Returns true when the menu consumed it.
    // `scan` is the key code (ObjInst), `action` 1 press and 2 release, `ch` the event's character, `mods` its modifier byte.
    // `run` executes a leaf's command words (function or command name first).
    public bool OnKey(int scan, int action, char ch, byte mods, Action<IReadOnlyList<string>> run)
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
        if (mMode == 0 || action != 1)
            return false;
        if (scan == 1) // Escape [code 00402068]
        {
            mReleaseKey = 1;
            if (mMode == 2)
            {
                Mode = 0;
                return true;
            }
            if (mLevel == Root)
                return false;
            mLevel = Root;
            return true;
        }
        if (mods != 0 || ch == '\0')
            return false;
        // [code 004021cc]: the item with that key runs its command and opens its sub level; no match leaves the key to the game.
        bool hit = false;
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
                if (mMode == 2)
                    Mode = 0;
            }
            else
                mLevel = found;
        }
        if (hit)
            mReleaseKey = scan;
        return hit;
    }

    // src/game/client_game_commands.cpp ChatMenuTree::add: false: empty string, empty item, or the last item's key already
    // exists at its level.
    public bool Add(string menuString, IReadOnlyList<string> commands)
    {
        ChatMenuNode level = Root;
        int p = 0;
        for (;;)
        {
            if (p >= menuString.Length)
                return false;
            char key = menuString[p++];
            int end = menuString.IndexOf('\\', p);
            string text = end < 0 ? menuString[p..] : menuString[p..end];
            if (text.Length == 0)
                return false;
            bool last = end < 0;
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
                node.Commands = commands.Take(Math.Min(commands.Count, 10)).ToList();
            level.Children.Add(node);
            if (last)
                return true;
            level = node;
        }
    }
}

// The chat menus of one console: a tree per ChatMenu object and the menu a script switched on last.
public sealed class ChatMenuRegistry
{
    private readonly Dictionary<SimObject, (int Id, ChatMenuTree Tree)> mTrees = new(ReferenceEqualityComparer.Instance);
    private SimObject? mActive;
    private string mActiveName = "";
    private readonly Dictionary<SimObject, string> mNames = new(ReferenceEqualityComparer.Instance);

    // Keyed by object and id; an entry whose id no longer matches is stale.
    public ChatMenuTree Of(SimObject obj)
    {
        if (!mTrees.TryGetValue(obj, out var slot) || slot.Id != obj.Id())
        {
            slot = (obj.Id(), new ChatMenuTree());
            mTrees[obj] = slot;
        }
        return slot.Tree;
    }

    // setCMMode: a menu switched to a mode above 0 is the one on display; switching it to 0 removes it.
    public void SetMode(SimObject obj, string name, int mode)
    {
        Of(obj).Mode = mode;
        mNames[obj] = name;
        if (mode > 0)
        {
            mActive = obj;
            mActiveName = name;
        }
        else if (ReferenceEquals(mActive, obj))
            mActive = null;
    }

    // A keyboard event offered to every menu that is on (the menus are members of the input consumer set, `sim.md` GS4). The first
    // menu that consumes it wins. `run` executes the command words of a leaf.
    public bool OnKey(GameState game, int scan, int action, char ch, byte mods, Action<IReadOnlyList<string>> run)
    {
        foreach (var (obj, slot) in mTrees.ToArray())
        {
            if (slot.Id != obj.Id() || !mNames.TryGetValue(obj, out string? name) ||
                game.Sim?.Server()?.Find(name) is not SimObject now || !ReferenceEquals(now, obj))
                continue;
            if ((slot.Tree.Mode != 0 || action == 2) && slot.Tree.OnKey(scan, action, ch, mods, run))
                return true;
        }
        return false;
    }

    // The tree of the named menu while it is on and shows items, else null.
    public (string Title, ChatMenuTree Tree)? Showing(GameState game, string name)
    {
        if (game.Sim?.Server()?.Find(name) is not SimObject obj || obj.ClassName() != "ChatMenu")
            return null;
        ChatMenuTree tree = Of(obj);
        return tree.Showing ? (obj is SimClasses.ChatMenu m ? m.Title : "", tree) : null;
    }

    // The menu on display, null when none is on, or its object was deleted.
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
    public readonly ChatMenuRegistry ChatMenus = new();
}

public static class ClientGameCommands
{
    // src/game/client_game_commands.cpp findMenu.
    private static SimObject? FindMenu(GameState game, string name) =>
        game.Sim?.Server()?.Find(name) is SimObject obj && obj.ClassName() == "ChatMenu" ? obj : null;

    // src/game/client_game_commands.cpp registerClientGameCommands.
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
            int mode = Numbers.BorlandAtoi(a[2]);
            if (mode >= 0 && mode < 3)
                menus.SetMode(obj, a[1], mode);
            return null;
        });
    }
}
