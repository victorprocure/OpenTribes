// Port of src/game/object_files.hpp and src/game/object_files.cpp: loadObject / storeObject (SimConsolePlugin ids 0x66,
// 0x67; docs/sim.md 8, docs/script.md 6.8).
namespace OpenTribes.Game;

public static class ObjectFiles
{
    // src/game/object_files.hpp enum class PersistentFileKind.
    public enum PersistentFileKind
    {
        Invalid,      // not a persistent stream, or an unknown class: "Error reading file"
        NotSimObject, // a known persistent class that is not a SimObject
    }

    // src/game/object_files.cpp classifyPersistentFile: classifies the head of a persistent stream (PERS tag, size, class
    // name).
    public static PersistentFileKind ClassifyPersistentFile(byte[] d)
    {
        // Block streams with their own tag: ITRShape (.dis), GridFile (.dtf), GridBlock (.dtb). They are persistent classes
        // but not SimObjects; PL98 (.ppl) is not a persistent stream.
        static bool Tag(byte[] d, string t) => d.Length >= 4 && d[0] == t[0] && d[1] == t[1] && d[2] == t[2] && d[3] == t[3];
        foreach (string tag in new[] { "ITRs", "GFIL", "GBLK" })
            if (d.Length >= 8 && Tag(d, tag))
                return PersistentFileKind.NotSimObject;
        // PERS, u32 size (bit 31: padding flag), u16 name length, the class name.
        if (d.Length < 10 || !Tag(d, "PERS"))
            return PersistentFileKind.Invalid;
        ushort nameLen = BitConverter.ToUInt16(d, 8);
        if (nameLen == 0 || 10 + nameLen > d.Length)
            return PersistentFileKind.Invalid;
        string name = ByteString.FromBytes(d.AsSpan(10, nameLen));
        foreach (string known in new[] { "TS::Shape", "TS::MaterialList", "ITRGeometry", "ITRLighting", "ITRMissionLighting" })
            if (GameUtil.EqualsNoCase(name, known))
                return PersistentFileKind.NotSimObject;
        return PersistentFileKind.Invalid;
    }

    // src/game/object_files.cpp registerObjectFileCommands.
    public static void Register(Interpreter interp, GameState game)
    {
        // loadObject(name, file) 0x0050b815: the file (direct path, then the resource manager) must hold a persistent
        // SimObject. Only the failure paths are reproduced: no SimObject class stores itself in a file on a dedicated
        // server, so there is nothing valid to load.
        interp.AddCommand("loadObject", (inr, a) =>
        {
            if (a.Length != 3)
            {
                inr.Print(1, "loadObject: objectName fileName");
                return "0";
            }
            byte[] data = Array.Empty<byte>();
            bool ok = false;
            try
            {
                data = File.ReadAllBytes(ByteString.ToHost(a[2]));
                ok = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                ok = false;
            }
            if (!ok && game.Resources != null)
                ok = game.Resources.Read(a[2], out data);
            if (ok && ClassifyPersistentFile(data) == PersistentFileKind.NotSimObject)
                inr.Print(1, "loadObject: Persistent object not a SimObject");
            else
                inr.Print(1, "loadObject: Error reading file");
            return "0";
        });

        // storeObject(object, file [, userField]) 0x0050b97b. The write of a found object never returns on the reference
        // (DECISIONS D11, docs/script.md 6.8); here it fails with the write error text.
        interp.AddCommand("storeObject", (inr, a) =>
        {
            if (a.Length != 3 && a.Length != 4)
            {
                inr.Print(1, "storeObject: objectName fileName [userField]");
                return "False";
            }
            if (game.Sim?.Server()?.Find(a[1]) == null)
            {
                inr.Print(1, "storeObject: Could not find object");
                return "False";
            }
            inr.Print(1, "storeObject: Error writing object");
            return "False";
        });
    }
}
