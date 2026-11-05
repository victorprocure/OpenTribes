// Joystick device state to input event values (docs/client-app.md 3.2, 3.3, 3.6). Pure logic: the platform layer reads
// SDL and calls these. Follows the stock device event maker 0050f658 and the object walk 0050ed5c.
namespace OpenTribes.Script;

public static class JoystickEvents
{
    // Device class of a joystick (0050e9e8 counts instances per class, 0050ed5c names it "joystick%d").
    public const byte DeviceType = 4;

    // Object types of the event record (0050ed5c).
    public const byte ObjButton = 2, ObjPov = 3, ObjXPov = 4, ObjYPov = 5, ObjUPov = 6, ObjDPov = 7, ObjLPov = 8, ObjRPov = 9,
        ObjXAxis = 11, ObjYAxis = 12, ObjZAxis = 13, ObjRxAxis = 14, ObjRyAxis = 15, ObjRzAxis = 16, ObjSlider = 17;

    // The range the axis value is normalised against. 0050ed5c stores the minimum and maximum the device reports for each
    // axis (it never sets a range itself) and 0050f658 divides by that span. SDL reports every axis as a signed 16 bit
    // value, so these are the range of the SDL device.
    public const int AxisMin = -32768, AxisMax = 32767;

    // 0050f658, axis objects of a device that is not a mouse: (raw - min) / (max - min) as floats, 0 to 1.
    public static float AxisValue(int raw) => (float)(raw - AxisMin) / (float)(AxisMax - AxisMin);

    // 0050ed5c walks the device objects in order and numbers each type from 0 (x, y, z, rx, ry, rz, slider). SDL gives
    // plain axis indexes: the first six are taken as x, y, z, rx, ry, rz and every one after as the next slider.
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

    // 0050f658, POV objects: the data is the angle in hundredths of a degree clockwise from up, low word 0xffff centred.
    public const int PovCentered = 0xffff;

    // SDL hat bits (up 1, right 2, down 4, left 8) to that angle.
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

    // 0050f658: the direction sector 0 up, 1 right, 2 down, 3 left, or 5 when centred.
    public static int PovSector(int angle) =>
        (angle & 0xffff) == 0xffff ? 5 : (((angle & 0xffff) + 0x7b0c) / 9000 - 3) & 3;

    // The events 0050f658 makes for one POV object: (object type, action, value). The two move events come first (x, then y), then
    // when the sector changed the break of the old direction and the make of the new one. A device that is read as buffered data
    // passes the same angle as the old one, so it makes no direction events.
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

    // 0050f658, button objects: make with 1.0 when the data has bit 7, else break with 0.0.
    public static InputEvent Button(int device, int index, bool down) => new()
    {
        DeviceType = DeviceType, DeviceInst = (byte)device, ObjType = ObjButton, ObjInst = (byte)index, Action = (byte)(down ? 1 : 2),
    };
}
