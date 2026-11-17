using AgentRecorder.Mcp.Tests;

// No default production binding or automatic discovery. All inputs are explicit.
if (args.Length != 6 || args[0] != "--old-plugin" || args[2] != "--current-plugin" || args[4] != "--evidence-directory")
{
    Console.Error.WriteLine("explicit_old_current_plugin_and_private_evidence_required");
    return 2;
}
try { return await HistoricalUpgradeProbe.RunAsync(args[1], args[3], args[5]); }
catch { Console.Error.WriteLine("historical_upgrade_diagnostic_unavailable"); return 1; }
