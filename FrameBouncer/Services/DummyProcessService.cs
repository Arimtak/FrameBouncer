using System.Collections.Generic;

namespace FrameBouncer.Services;

/// <summary>
/// Dummy-Implementierung zur Bereitstellung typischer Spiele- und App-Namen.
/// </summary>
public class DummyProcessService : IProcessService
{
    // TODO: PROCESS ENUMERATION
    //
    // Später echte Prozesse mit Process.GetProcesses()
    // erkennen und für die ComboBox bereitstellen.
    //
    // Aktuell nur Dummy-Prozesse verwenden.

    public IReadOnlyList<string> GetRunningProcesses()
    {
        return new List<string>
        {
            "game.exe",
            "example-game.exe",
            "notepad.exe",
            "dummy-app.exe"
        };
    }
}