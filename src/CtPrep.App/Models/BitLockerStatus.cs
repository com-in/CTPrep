namespace CtPrep.App.Models;

/// <summary>
/// 某个卷的 BitLocker 状态。
/// </summary>
/// <param name="Available">
/// 本机是否具备 BitLocker 组件（家庭版或未安装该功能时为 false，此时一律按「未加密」处理，不阻断部署）。
/// </param>
/// <param name="Protected">保护是否开启（ProtectionStatus=On）。暂停保护后会变成 Off。</param>
/// <param name="EncryptionPercentage">加密进度百分比；未加密时为 0。</param>
public sealed record BitLockerStatus(bool Available, bool Protected, int EncryptionPercentage)
{
    /// <summary>组件不可用或未加密时的统一取值。</summary>
    public static BitLockerStatus Unknown { get; } = new(false, false, 0);
}
