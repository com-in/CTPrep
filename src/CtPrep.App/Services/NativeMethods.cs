using System.Runtime.InteropServices;

namespace CtPrep.App.Services;

internal static class NativeMethods
{
    private enum FirmwareType : uint
    {
        Unknown = 0,
        Bios = 1,
        Uefi = 2,
        Max = 3,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out FirmwareType firmwareType);

    /// <summary>获取固件类型；失败返回 Unknown。</summary>
    public static Models.FirmwareType GetFirmware()
    {
        try
        {
            if (GetFirmwareType(out var type))
            {
                return type switch
                {
                    FirmwareType.Bios => Models.FirmwareType.Bios,
                    FirmwareType.Uefi => Models.FirmwareType.Uefi,
                    _ => Models.FirmwareType.Unknown,
                };
            }
        }
        catch
        {
            // Win7 及更早没有该 API
        }

        return Models.FirmwareType.Unknown;
    }
}
