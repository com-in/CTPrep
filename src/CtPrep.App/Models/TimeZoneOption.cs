namespace CtPrep.App.Models;

/// <summary>
/// 「时区」下拉框的一项。
/// <paramref name="Id"/> 是 Windows 时区标识（如 China Standard Time），
/// 会原样写进应答文件的 TimeZone 节点；<paramref name="Display"/> 只用于界面显示。
/// </summary>
public sealed record TimeZoneOption(string Id, string Display);
