namespace OpenTribes.Platform.Sdl;

// The joysticks open for the client. The stock input manager enumerates the attached devices once when the input system opens
// (0050e134 via 0050df34, instance numbers in order from 0 per class, 0050e9e8) and does not follow hot plugging, so the set is
// read once here. Joystick events reach the client only while the window has focus (the stock access is foreground, 3.6).
public sealed class JoystickSet : IDisposable
{
    private readonly List<(uint Id, IntPtr Handle, string Name, int Axes, int Buttons, int Hats)> mDevices = new();

    public int Count => mDevices.Count;

    public static JoystickSet Open()
    {
        var set = new JoystickSet();
        try
        {
            Sdl3.EnsureLoaded();
            if (!Sdl3.InitSubSystem(Sdl3.InitJoystick))
                return set;
            foreach (uint id in Sdl3.GetJoystickIds())
            {
                IntPtr h = Sdl3.OpenJoystick(id);
                if (h != IntPtr.Zero)
                    set.mDevices.Add((id, h, Sdl3.JoystickName(h), Sdl3.GetNumJoystickAxes(h), Sdl3.GetNumJoystickButtons(h), Sdl3.GetNumJoystickHats(h)));
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return set;
    }

    // A set of devices that are not backed by SDL, for tests (no hardware needed).
    public static JoystickSet Simulated(params string[] names)
    {
        var set = new JoystickSet();
        for (int i = 0; i < names.Length; ++i)
            set.mDevices.Add(((uint)(i + 1), IntPtr.Zero, names[i], 6, 12, 1));
        return set;
    }

    // The device number (joystick0, joystick1...) of an SDL instance id, or -1 when it is not one of the open devices.
    public int InstanceOf(uint id)
    {
        for (int i = 0; i < mDevices.Count; ++i)
            if (mDevices[i].Id == id)
                return i;
        return -1;
    }

    public string NameOf(int inst) => mDevices[inst].Name;
    public int AxesOf(int inst) => mDevices[inst].Axes;
    public int ButtonsOf(int inst) => mDevices[inst].Buttons;
    public int HatsOf(int inst) => mDevices[inst].Hats;

    public void Dispose()
    {
        foreach (var d in mDevices)
            if (d.Handle != IntPtr.Zero)
                Sdl3.CloseJoystick(d.Handle);
        mDevices.Clear();
    }
}
