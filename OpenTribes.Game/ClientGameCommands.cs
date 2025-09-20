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
}

// src/game/client_game_commands.hpp struct ChatMenuTree.
public sealed class ChatMenuTree
{
    public readonly ChatMenuNode Root = new();
    public int Mode; // setCMMode, 0-2

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
            var node = new ChatMenuNode { Key = key, Text = text.Length > 31 ? text[..31] : text, Leaf = last };
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
        if (mode > 0)
        {
            mActive = obj;
            mActiveName = name;
        }
        else if (ReferenceEquals(mActive, obj))
            mActive = null;
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
