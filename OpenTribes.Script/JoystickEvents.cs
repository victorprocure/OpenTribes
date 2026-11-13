namespace OpenTribes.Script;

/// <summary>Converts joystick device state to input event values (docs/client-app.md 3.2, 3.3, 3.6).</summary>
/// <remarks>Pure logic: the platform layer reads SDL and calls these. Follows the stock device event maker (0050f658) and the object walk (0050ed5c).</remarks>
public static class JoystickEvents
{
    /// <summary>The device class of a joystick. 0050e9e8 counts instances per class and 0050ed5c names it "joystick%d".</summary>
    public const byte DeviceType = 4;
    /// <summary>The object types of the event record: buttons, POV and hat directions, axes and slider (0050ed5c).</summary>
    public const byte ObjButton = 2, ObjPov = 3, ObjXPov = 4, ObjYPov = 5, ObjUPov = 6, ObjDPov = 7, ObjLPov = 8, ObjRPov = 9,
        ObjXAxis = 11, ObjYAxis = 12, ObjZAxis = 13, ObjRxAxis = 14, ObjRyAxis = 15, ObjRzAxis = 16, ObjSlider = 17;

    /// <summary>The range the axis value is normalised against.</summary>
    /// <remarks>0050ed5c stores the minimum and maximum the device reports for each axis and never sets a range itself. 0050f658 divides by that span. SDL reports every axis as a signed 16-bit value, so these are the range of the SDL device.</remarks>
    public const int AxisMin = -32768, AxisMax = 32767;

    /// <summary>Normalises a raw axis value of a device that is not a mouse (0050f658).</summary>
    /// <param name="raw">The raw SDL axis value.</param>
    /// <returns>(raw - min) / (max - min) as floats, 0 to 1.</returns>
    public static float AxisValue(int raw) => (float)(raw - AxisMin) / (float)(AxisMax - AxisMin);

    /// <summary>Maps an SDL axis index to an event object type and instance.</summary>
    /// <param name="sdlAxis">The SDL axis index.</param>
    /// <returns>The object type and instance.</returns>
    /// <remarks>0050ed5c walks the device objects in order and numbers each type from 0 (x, y, z, rx, ry, rz, slider). SDL gives plain axis indexes: the first six are taken as x, y, z, rx, ry, rz and every one after as the next slider.</remarks>
    public static (byte ObjType, byte ObjInst) AxisObject(int sdlAxis) => sdlAxis switch
    {
        0 => (ObjXAxis, (byte)0),
        1 => (ObjYAxis, (byte)0),
        2 => (ObjZAxis, (byte)0),
        3 => (ObjRxAxis, (byte)0),
        4 => (ObjRyAxis, (byte)0),
        5 => (ObjRzAxis, (byte)0),
        _ => (ObjSlider, (byte)(sdlAxis - 6)),
    };

    /// <summary>The POV data of a centred hat: low word 0xffff (0050f658).</summary>
    public const int PovCentered = 0xffff;

    /// <summary>Converts SDL hat bits (up 1, right 2, down 4, left 8) to a POV angle.</summary>
    /// <param name="sdlHat">The SDL hat bits.</param>
    /// <returns>The angle in hundredths of a degree clockwise from up, <see cref="PovCentered"/> when centred (0050f658).</returns>
    public static int HatAngle(int sdlHat) => (sdlHat & 15) switch
    {
        1 => 0,
        3 => 4500,
        2 => 9000,
        6 => 13500,
        4 => 18000,
        12 => 22500,
        8 => 27000,
        9 => 31500,
        _ => PovCentered,
    };

    /// <summary>Gets the direction sector of a POV angle (0050f658).</summary>
    /// <param name="angle">The POV angle.</param>
    /// <returns>0 up, 1 right, 2 down, 3 left, or 5 when centred.</returns>
    public static int PovSector(int angle) =>
        (angle & 0xffff) == 0xffff ? 5 : (((angle & 0xffff) + 0x7b0c) / 9000 - 3) & 3;

    /// <summary>Makes the events 0050f658 makes for one POV object.</summary>
    /// <param name="angle">The new POV angle.</param>
    /// <param name="oldAngle">The previous POV angle.</param>
    /// <returns>The events as (object type, action, value). The two move events come first (x, then y), then when the sector changed the break of the old direction and the make of the new one.</returns>
    /// <remarks>A device that is read as buffered data passes the same angle as the old one, so it makes no direction events.</remarks>
    public static List<(byte ObjType, byte Action, float Value)> PovEvents(int angle, int oldAngle)
    {
        var list = new List<(byte, byte, float)>();
        if ((angle & 0xffff) == 0xffff)
        {
            list.Add((ObjXPov, 3, 0.5f));
            list.Add((ObjYPov, 3, 0.5f));
        }
        else
        {
            float a = (float)angle * 2.7777778e-05f * 6.2831855f;
            list.Add((ObjXPov, 3, (MathF.Sin(a) + 1f) * 0.5f));
            list.Add((ObjYPov, 3, (-MathF.Cos(a) + 1f) * 0.5f));
        }
        int now = PovSector(angle), old = PovSector(oldAngle);
        if (now != old)
        {
            if (old != 5)
                list.Add((DirectionObject(old), 2, 0f));
            if (now != 5)
                list.Add((DirectionObject(now), 1, 1f));
        }
        return list;
    }

    private static byte DirectionObject(int sector) => sector switch { 0 => ObjUPov, 1 => ObjRPov, 2 => ObjDPov, _ => ObjLPov };

    /// <summary>Makes the event of a button object (0050f658).</summary>
    /// <param name="device">The device instance.</param>
    /// <param name="index">The button index.</param>
    /// <param name="down">Whether the button is down.</param>
    /// <returns>A make event with 1.0 when down, else a break event with 0.0.</returns>
    public static InputEvent Button(int device, int index, bool down) => new()
    {
        DeviceType = DeviceType, DeviceInst = (byte)device, ObjType = ObjButton, ObjInst = (byte)index, Action = (byte)(down ? 1 : 2),
    };
}
