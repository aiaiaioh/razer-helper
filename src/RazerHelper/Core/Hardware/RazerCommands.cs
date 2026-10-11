namespace RazerHelper.Core.Hardware;

/// <summary>
/// Every EC command the app sends, in one place. A command id is
/// <c>(command class &lt;&lt; 8) | command</c>; the "get" counterpart of a
/// "set" is the same id with 0x80 set on the low byte.
/// </summary>
/// <remarks>
/// Verified on a Razer Blade 16 (2023), PID 0x029F. The ids come from the
/// community reverse-engineering work in razer-ctl (MIT) and OpenRazer.
/// </remarks>
internal static class RazerCommands
{
    // Class 0x0D: performance and fans.
    public const ushort SetPerformanceMode = 0x0D02;
    public const ushort GetPerformanceMode = 0x0D82;
    public const ushort SetBoost = 0x0D07;
    public const ushort GetBoost = 0x0D87;
    public const ushort GetActualFanRpm = 0x0D88;

    // Manual fan speed: [0, zone, rpm / 100] for zone 1 (CPU) and 2 (GPU). It only
    // takes effect for a zone whose performance mode was written with the fan byte
    // set to 1 (see PerformanceService / FanControlService). The id and layout come
    // from razer-control-win's daemon, which uses them on a Blade 14 (2022); they
    // have not been checked on any other model by this app.
    public const ushort SetFanRpm = 0x0D01;

    // Class 0x0D: temperatures. Found by a read-only scan of a Blade 16 (2023)'s
    // controller; it is in neither razer-ctl nor OpenRazer. The reply is
    // [2, cpu, gpu] in whole degrees Celsius. The CPU byte follows CPU heat but
    // slowly and lower than a die sensor's (it peaked at 55 under a full load),
    // so it looks like a sensor near the CPU, not the CPU die itself.
    public const ushort GetTemperatures = 0x0D85;

    // Class 0x07: max fan speed. Custom performance mode only.
    public const ushort SetMaxFan = 0x070F;
    public const ushort GetMaxFan = 0x078F;

    // Class 0x07: battery.
    public const ushort SetBatteryChargeLimit = 0x0712;

    // Class 0x03: lighting. Brightness is shared by the keyboard (LED 5) and
    // the logo (LED 4); the logo also has a power and a mode register.
    public const ushort SetLogoPower = 0x0300;
    public const ushort GetLogoPower = 0x0380;
    public const ushort SetLogoMode = 0x0302;
    public const ushort GetLogoMode = 0x0382;
    public const ushort SetBrightness = 0x0303;
    public const ushort GetBrightness = 0x0383;

    // Class 0x0F: keyboard backlight effect (extended matrix effect).
    public const ushort SetKeyboardEffect = 0x0F02;
    public const ushort GetKeyboardEffect = 0x0F82;
}
