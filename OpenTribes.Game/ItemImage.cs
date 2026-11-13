// Port of src/game/item_image.cpp: Player item images (docs/player-sim.md 7.2, 7.4, 7.5): mounting, the image state
// machine with its timers, ammo and energy use, trigger handling, the script callbacks. Server side only: sounds and
// animations belong to ghosts.
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

public sealed partial class Player
{
    private static readonly string[] kStateNames =
    {
        "Reset", "Fire", "NoAmmo", "Idle", "Ready", "Reload", "SpinUp", "SpinDown", "Activate", "Deactivate",
    };

    // src/game/item_image.cpp fieldOf.
    private static DataBlockManager.Field? FieldOf(DataBlockManager.Block b, string name)
    {
        DataBlockManager.Field? f = b.Find(name);
        return f != null && f.Elems.Count != 0 ? f : null;
    }

    // src/game/item_image.cpp blockFloat.
    private static float BlockFloat(DataBlockManager.Block b, string name, float def)
    {
        DataBlockManager.Field? f = FieldOf(b, name);
        if (f == null)
            return def;
        if (f.Elems[0].Set)
            return f.Elems[0].F[0];
        if (f.Def.DefaultText.Length != 0)
            return DataBlockManager.CtorFloat(f.Def.DefaultText);
        return def;
    }

    // src/game/item_image.cpp blockInt.
    private static int BlockInt(DataBlockManager.Block b, string name, int def)
    {
        DataBlockManager.Field? f = FieldOf(b, name);
        return f != null && f.Elems[0].Set ? f.Elems[0].I : def;
    }

    /// <summary>Returns the name of an image state.</summary>
    /// <param name="state">The state index, 0 to 9.</param>
    /// <returns>The state name, or "Reset" for an index out of range.</returns>
    public static string ImageStateName(int state) => state >= 0 && state < 10 ? kStateNames[state] : "Reset";

    // ---- script callbacks (7.5, Player_findImageCallback 0x0041a00c) ----

    /// <summary>Calls the script callback of an image event.</summary>
    /// <param name="slot">The image slot.</param>
    /// <param name="ev">The event name.</param>
    public void ImageCallback(int slot, string ev)
    {
        ImageSlot s = Images[slot];
        if (mGame.Console == null || mGame.Sim == null)
            return;
        var names = new List<string>();
        if (s.Info.Name != null)
            names.Add(s.Info.Name + "::" + ev);
        DataBlockManager.Block? item = mGame.Sim.DataBlocks().At(5, s.Item);
        if (item != null && mGame.Sim.DataBlocks().GetField(item, "className", "", out string cls) && cls.Length != 0)
            names.Add(cls + "::" + ev);
        names.Add("ItemImage::" + ev);
        foreach (string fn in names)
            if (mGame.Console.HasFunction(fn))
            {
                mGame.Console.CallFunction(fn, new[] { Numbers.FormatInt(Id()), Numbers.FormatInt(slot) });
                return;
            }
    }

    // ---- mounting (7.2) ----

    /// <summary>Mounts an item image in a slot.</summary>
    /// <param name="type">The item type.</param>
    /// <param name="slot">The image slot.</param>
    /// <param name="team">The team of the player.</param>
    /// <returns>True when the image was mounted.</returns>
    public bool MountItem(int type, int slot, int team)
    {
        if (slot < 0 || slot >= kImageSlots || type < 0 || type >= kItemTypes)
            return false;
        DataBlockManager.Block? item = mGame.Sim?.DataBlocks().At(5, type);
        DataBlockManager.Field? f = item?.Find("imageType");
        if (f == null || f.Elems.Count == 0 || !f.Elems[0].Set || f.Elems[0].I < 0)
            return false; // imageType -1
        if (Images[slot].Item != -1)
        {
            if (Images[slot].Item == type)
            {
                Images[slot].NextItem = -2;
                return true;
            }
            ItemCallback(Images[slot].Item, "onUnmount");
        }
        SlotOf[type] = slot;
        SetMaskBits(0x20000u | (0x100000u << slot));
        SetImage(slot, type, team);
        ItemCallback(type, "onMount");
        return true;
    }

    /// <summary>Unmounts the image of a slot.</summary>
    /// <param name="slot">The image slot.</param>
    /// <returns>True when an image was mounted.</returns>
    public bool UnmountItem(int slot)
    {
        if (slot < 0 || slot >= kImageSlots || Images[slot].Item == -1)
            return false;
        int type = Images[slot].Item;
        if (type >= 0 && type < kItemTypes)
            SlotOf[type] = -1;
        SetMaskBits(0x20000u | (0x100000u << slot));
        SetImage(slot, -1, -1);
        ItemCallback(type, "onUnmount");
        return true;
    }

    /// <summary>Sets the image of a slot and starts its state machine.</summary>
    /// <param name="slot">The image slot.</param>
    /// <param name="item">The item type.</param>
    /// <param name="team">The team of the player.</param>
    /// <remarks>Follows Player::setImage (0x0041a168).</remarks>
    public void SetImage(int slot, int item, int team)
    {
        ImageSlot s = Images[slot];
        if (item == s.Item)
        {
            s.NextItem = -2;
            return;
        }
        if (item != -1 && (s.State == kFire || s.State == kReload))
        {
            s.NextItem = item;
            s.NextTeam = team;
            return;
        }
        ResetImage(slot);
        if (item == -1)
            return;
        ImageSlot n = Images[slot];
        n.Item = item;
        n.Team = team;
        DataBlockManager.Block? it = mGame.Sim?.DataBlocks().At(5, item);
        DataBlockManager.Field? f = it?.Find("imageType");
        DataBlockManager.Block? img = f != null && f.Elems.Count != 0 && f.Elems[0].Set ? mGame.Sim!.DataBlocks().At(6, f.Elems[0].I) : null;
        // The image shape resource: a missing shape keeps the item and clears the image type.
        bool shape = false;
        if (img != null)
            if (FieldOf(img, "shapeFile") is DataBlockManager.Field sf && !string.IsNullOrEmpty(sf.Elems[0].Str))
                shape = mGame.ResourceExists(sf.Elems[0].Str + ".dts");
        if (img == null || !shape)
        {
            n.Image = -1;
            RecomputeMass();
            return;
        }
        n.Image = img.Index;
        ImageInfo i = n.Info;
        i.Reset();
        i.Name = img.Name;
        i.WeaponType = BlockInt(img, "weaponType", -1);
        i.AmmoType = BlockInt(img, "ammoType", -1);
        i.ActivateTime = BlockFloat(img, "activateTime", 0.5f);
        i.FireTime = BlockFloat(img, "fireTime", 0.2f);
        i.ReloadTime = BlockFloat(img, "reloadTime", 1.0f);
        i.SpinUpTime = BlockFloat(img, "spinUpTime", 2.0f);
        i.SpinDownTime = BlockFloat(img, "spinDownTime", 2.0f);
        i.MinEnergy = BlockFloat(img, "minEnergy", 2.0f);
        i.MaxEnergy = BlockFloat(img, "maxEnergy", 10.0f);
        i.AccuFire = BlockInt(img, "accuFire", 0) != 0;
        i.MountPoint = BlockInt(img, "mountPoint", 2);
        if (FieldOf(img, "mountOffset") is DataBlockManager.Field mo)
            for (int k = 0; k < 3 && mo.Elems.Count != 0; ++k)
                i.MountOffset[k] = mo.Elems[0].F[k];
        if (FieldOf(img, "mountRotation") is DataBlockManager.Field mr)
            for (int k = 0; k < 3 && mr.Elems.Count != 0; ++k)
                i.MountRotation[k] = mr.Elems[0].F[k];
        if (FieldOf(img, "shapeFile") is DataBlockManager.Field sf2 && sf2.Elems.Count != 0)
            i.ShapeFile = sf2.Elems[0].Str;
        if (FieldOf(img, "projectileType") is DataBlockManager.Field pf && pf.Elems[0].Set)
        {
            i.ProjGroup = pf.Elems[0].I;
            i.ProjIndex = pf.Elems[0].I2;
        }
        if (Manager() != null)
        {
            SetImageState(slot, kActivate);
            RecomputeMass();
        }
    }

    /// <summary>Resets the image of a slot to its first state.</summary>
    /// <param name="slot">The image slot.</param>
    /// <remarks>Follows Player::resetImage (0x0041a64c).</remarks>
    public void ResetImage(int slot)
    {
        ImageSlot s = Images[slot];
        if (s.Image != -1 && s.Info.WeaponType == 2 && s.State == kFire)
            StopSustained(slot);
        int item = Images[slot].Item;
        Images[slot].Reset();
        if (item != -1)
            RecomputeMass();
    }

    // ---- trigger (7.4) ----

    /// <summary>Sets the trigger state of the image in a slot.</summary>
    /// <param name="slot">The image slot.</param>
    /// <param name="down">True when the trigger is down.</param>
    public void TriggerImage(int slot, bool down)
    {
        if (slot < 0 || slot >= kImageSlots)
            return;
        ImageSlot s = Images[slot];
        if (s.Image == -1 || s.Triggered == down)
            return;
        if (down)
        {
            if (s.Info.WeaponType == -1)
                return;
            s.Triggered = true;
            SetMaskBits(0x20000u | (0x100000u << slot));
            if (s.State == kFire && (s.Info.WeaponType == 1 || s.Info.WeaponType == 2))
                s.FireCount = (s.FireCount + 1) & 7;
        }
        else
        {
            s.Triggered = false;
            SetMaskBits(0x20000u | (0x100000u << slot));
        }
        UpdateImageState(slot, 0.0f);
    }

    // ---- state machine (7.5) ----

    /// <summary>Advances the image state machine of a slot.</summary>
    /// <param name="slot">The image slot.</param>
    /// <param name="dt">The step length in seconds.</param>
    /// <remarks>Follows Player::updateImageState (0x0041af68).</remarks>
    public void UpdateImageState(int slot, float dt)
    {
        using var chop = X87.Chop();
        if (slot < 0 || slot >= kImageSlots || Images[slot].Image == -1)
            return;
        int self = Id();
        SimManager? mgr = Manager();
        ImageSlot s = Images[slot];
        s.Timer = X87.F32(Sub(s.Timer, dt));
        // The ammo flag (server).
        bool ammo = s.Info.AmmoType == -1 ? Energy > s.Info.MinEnergy : ItemCountOf(s.Info.AmmoType) > 0;
        if (ammo != s.Ammo)
        {
            SetMaskBits(0x20000u | (0x100000u << slot));
            s.Ammo = ammo;
        }
        int wt = s.Info.WeaponType;
        bool T = s.Triggered, A = s.Ammo;
        bool expired = !(s.Timer > 0.0f);
        switch (s.State)
        {
            case kReset:
            case kActivate:
                if (expired)
                    SetImageState(slot, wt == 1 ? kIdle : !A ? kNoAmmo : wt == 3 ? kReload : kReady);
                break;
            case kFire:
            {
                if (wt == 2)
                {
                    UpdateSustained(slot);
                    if (mgr == null || mgr.FindById(self) != this)
                        return;
                    if (s.Info.AmmoType == -1)
                    {
                        double e = Sub(Energy, Mul(s.Info.MaxEnergy, dt));
                        Energy = X87.F32(e);
                        if (e < 0.0)
                            Energy = 0.0f;
                        else if (Energy > PData.MaxEnergy)
                            Energy = PData.MaxEnergy;
                    }
                }
                if (expired)
                {
                    if (wt == 1)
                    {
                        if (!T || !A)
                        {
                            SetImageState(slot, kSpinDown);
                        }
                        else
                        {
                            s.Timer = s.Info.FireTime;
                            FireImage(slot);
                        }
                    }
                    else if (wt == 2)
                    {
                        if (!T || !A)
                            SetImageState(slot, kReload);
                    }
                    else
                    {
                        SetImageState(slot, kReload);
                    }
                }
                break;
            }
            case kNoAmmo:
                if (A)
                    SetImageState(slot, wt == 1 ? (T ? kSpinUp : kIdle) : kReload);
                break;
            case kIdle:
                if (!A)
                    SetImageState(slot, kNoAmmo);
                else if (T)
                    SetImageState(slot, wt == 1 ? kSpinUp : kReady);
                break;
            case kReady:
                if (wt == 1)
                {
                    if (!T || !A)
                        SetImageState(slot, kSpinDown);
                }
                else if (T)
                {
                    SetImageState(slot, kFire);
                }
                break;
            case kReload:
                if (expired)
                    SetImageState(slot, wt == 3 ? kSpinUp : kReady);
                break;
            case kSpinUp:
                if (wt == 3 && !A)
                    SetImageState(slot, kNoAmmo);
                else if (wt != 3 && (!T || !A))
                    SetImageState(slot, kSpinDown);
                else if (expired)
                    SetImageState(slot, kReady);
                break;
            case kSpinDown:
                if (T && A)
                    SetImageState(slot, kSpinUp);
                else if (expired)
                    SetImageState(slot, kIdle);
                break;
            default:
                break;
        }
    }

    /// <summary>Enters an image state.</summary>
    /// <param name="slot">The image slot.</param>
    /// <param name="state">The new state index.</param>
    /// <remarks>Follows Player::setImageState (0x0041aa40).</remarks>
    public void SetImageState(int slot, int state)
    {
        using var chop = X87.Chop();
        ImageSlot s = Images[slot];
        if (s.Image == -1)
            return;
        int old = s.State;
        float oldTimer = s.Timer;
        s.State = state;
        if (old == kFire && s.Info.WeaponType == 2)
            StopSustained(slot);
        if (Images[slot].Image == -1)
            return;
        // A pending swap completes when Fire or Reload is left.
        if (s.NextItem >= -1 && (old == kFire || old == kReload))
        {
            SetImage(slot, s.NextItem, s.NextTeam);
            return;
        }
        int wt = s.Info.WeaponType;
        switch (state)
        {
            case kFire:
                s.Timer = s.Info.FireTime;
                SetMaskBits(0x20000u | (0x100000u << slot));
                s.FireCount = (s.FireCount + 1) & 7;
                if (wt == 2)
                    StartSustained(slot);
                else
                    FireImage(slot);
                break;
            case kNoAmmo:
                if (s.Info.AmmoType != -1 && mGame.Console != null)
                {
                    string fn = mGame.VirtualFunction(this, "onNoAmmo");
                    DataBlockManager.Block? item = mGame.Sim?.DataBlocks().At(5, s.Item);
                    if (fn.Length != 0 && item != null)
                        mGame.Console.CallFunction(fn, new[] { Numbers.FormatInt(Id()), Numbers.FormatInt(slot), item.Name });
                }
                break;
            case kReady:
                if (s.Triggered)
                    SetImageState(slot, kFire);
                break;
            case kReload:
                if (s.Info.AmmoType == -1 || s.Ammo)
                {
                    s.Timer = s.Info.ReloadTime;
                    if (s.Info.ReloadTime == 0.0f)
                        SetImageState(slot, kReady);
                }
                else
                {
                    SetImageState(slot, kNoAmmo);
                }
                break;
            case kSpinUp:
                s.Timer = old == kSpinDown ? X87.F32(Mul(s.Info.SpinUpTime, X87.F32(Sub(1.0, Div(oldTimer, s.Info.SpinDownTime)))))
                                           : s.Info.SpinUpTime;
                break;
            case kSpinDown:
                s.Timer = old == kSpinUp ? X87.F32(Mul(s.Info.SpinDownTime, X87.F32(Sub(1.0, Div(oldTimer, s.Info.SpinUpTime)))))
                                         : s.Info.SpinDownTime;
                break;
            case kActivate:
                s.Timer = s.Info.ActivateTime;
                break;
            default:
                break;
        }
    }

    // ---- fire (7.5 Player_fireImage, sustained fire) ----

    /// <summary>Fires the image of a slot.</summary>
    /// <param name="slot">The image slot.</param>
    /// <remarks>Follows Player::fireImage (0x0041b8cc).</remarks>
    public void FireImage(int slot)
    {
        ImageSlot s = Images[slot];
        if (s.Info.ProjGroup < 0 || s.Info.ProjIndex < 0)
        {
            ImageCallback(slot, "onFire");
            return;
        }
        ProjectileFire.ProjectileFromImage(this, slot, false);
    }

    /// <summary>Starts the sustained effect of the image in a slot.</summary>
    /// <param name="slot">The image slot.</param>
    /// <remarks>Follows Player::startSustained (0x0041b520).</remarks>
    public void StartSustained(int slot)
    {
        ImageSlot s = Images[slot];
        if (s.Info.ProjGroup < 0 || s.Info.ProjIndex < 0)
        {
            ImageCallback(slot, "onActivate");
            return;
        }
        ProjectileFire.ProjectileFromImage(this, slot, true);
    }

    /// <summary>Updates the sustained effect of the image in a slot.</summary>
    /// <param name="slot">The image slot.</param>
    /// <remarks>Follows Player::updateSustained (0x0041b728).</remarks>
    public void UpdateSustained(int slot)
    {
        ImageSlot s = Images[slot];
        if (s.Info.ProjGroup < 0 || s.Info.ProjIndex < 0)
        {
            ImageCallback(slot, "onUpdateFire");
            return;
        }
        if (s.Projectile != 0)
            ProjectileFire.ProjectileUpdateFromImage(this, slot);
    }

    /// <summary>Stops the sustained effect of the image in a slot.</summary>
    /// <param name="slot">The image slot.</param>
    /// <remarks>Follows Player::stopSustained (0x0041b828).</remarks>
    public void StopSustained(int slot)
    {
        ImageSlot s = Images[slot];
        if (s.Info.ProjGroup < 0 || s.Info.ProjIndex < 0)
        {
            ImageCallback(slot, "onDeactivate");
            return;
        }
        ProjectileFire.ProjectileShutOffFromImage(this, slot);
        Images[slot].Projectile = 0;
    }
}
