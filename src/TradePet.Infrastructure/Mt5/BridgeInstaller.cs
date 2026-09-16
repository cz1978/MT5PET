namespace TradePet.Infrastructure.Mt5;

public sealed record BridgeInstallResult(string ExpertDirectory, string CompiledPath, string? SourcePath);

public sealed class BridgeInstaller
{
    public BridgeInstallResult Install(
        Mt5TerminalInstallation terminal,
        string compiledBridgePath,
        string? sourceBridgePath = null)
    {
        if (string.IsNullOrWhiteSpace(terminal.DataDirectory))
        {
            throw new InvalidOperationException("The MT5 data directory could not be resolved.");
        }

        if (!File.Exists(compiledBridgePath))
        {
            throw new FileNotFoundException("The compiled TradePet Bridge was not found.", compiledBridgePath);
        }

        var expertDirectory = Path.GetFullPath(Path.Combine(terminal.DataDirectory, "MQL5", "Experts", "TradePet"));
        Directory.CreateDirectory(expertDirectory);
        var targetCompiled = Path.Combine(expertDirectory, "TradePetBridge.ex5");
        File.Copy(compiledBridgePath, targetCompiled, overwrite: true);

        string? targetSource = null;
        if (!string.IsNullOrWhiteSpace(sourceBridgePath) && File.Exists(sourceBridgePath))
        {
            targetSource = Path.Combine(expertDirectory, "TradePetBridge.mq5");
            File.Copy(sourceBridgePath, targetSource, overwrite: true);
        }

        return new BridgeInstallResult(expertDirectory, targetCompiled, targetSource);
    }
}
