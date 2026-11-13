namespace OpenTribes.Platform.Sdl;

/// <summary>The joysticks open for the client.</summary>
/// <remarks>The stock input manager enumerates the attached devices once when the input system opens (0050e134 via 0050df34, instance numbers in order from 0 per class, 0050e9e8) and does not follow hot plugging, so the set is read once here. Joystick events reach the client only while the window has focus, since the stock access is foreground (docs/client-app.md 3.6).</remarks>
public sealed class JoystickSet : IDisposable
{
    private readonly List<(uint Id, IntPtr Handle, string Name, int Axes, int Buttons, int Hats)> mDevices = new();

    /// <summary>Gets the number of open devices.</summary>
    public int Count => mDevices.Count;

    /// <summary>Opens every attached joystick.</summary>
    /// <returns>The set. It is empty when SDL or its joystick subsystem is unavailable.</returns>
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

    /// <summary>Creates a set of devices that are not backed by SDL, for tests (no hardware needed).</summary>
    /// <param name="names">The device names.</param>
    /// <returns>The set.</returns>
    public static JoystickSet Simulated(params string[] names)
    {
        var set = new JoystickSet();
        for (int i = 0; i < names.Length; ++i)
            set.mDevices.Add(((uint)(i + 1), IntPtr.Zero, names[i], 6, 12, 1));
        return set;
    }

    /// <summary>Gets the device number (joystick0, joystick1 and so on) of an SDL instance id.</summary>
    /// <param name="id">The SDL instance id.</param>
    /// <returns>The device number, or -1 when it is not one of the open devices.</returns>
    public int InstanceOf(uint id)
    {
        for (int i = 0; i < mDevices.Count; ++i)
            if (mDevices[i].Id == id)
                return i;
        return -1;
    }

    /// <summary>Gets the name of a device.</summary>
    /// <param name="inst">The device number.</param>
    /// <returns>The name.</returns>
    public string NameOf(int inst) => mDevices[inst].Name;
    /// <summary>Gets the number of axes of a device.</summary>
    /// <param name="inst">The device number.</param>
    /// <returns>The count.</returns>
    public int AxesOf(int inst) => mDevices[inst].Axes;
    /// <summary>Gets the number of buttons of a device.</summary>
    /// <param name="inst">The device number.</param>
    /// <returns>The count.</returns>
    public int ButtonsOf(int inst) => mDevices[inst].Buttons;
    /// <summary>Gets the number of hats of a device.</summary>
    /// <param name="inst">The device number.</param>
    /// <returns>The count.</returns>
    public int HatsOf(int inst) => mDevices[inst].Hats;

    /// <summary>Closes every device.</summary>
    public void Dispose()
    {
        foreach (var d in mDevices)
            if (d.Handle != IntPtr.Zero)
                Sdl3.CloseJoystick(d.Handle);
        mDevices.Clear();
    }
}
