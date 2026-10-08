namespace CtPrep.App.Models;

/// <summary>部署进度事件。</summary>
public sealed record DeployProgress(
    DeployStage Stage,
    int Percent,
    string Message,
    bool IsError = false);
