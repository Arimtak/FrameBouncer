using System.Collections.Generic;

namespace FrameBouncer.Services;

/// <summary>
/// Schnittstelle für die Prozesserkennung zur Auswahl des Ziel-Spiels/Programms.
/// </summary>
public interface IProcessService
{
    /// <summary>
    /// Gibt eine Liste relevanter ausführbarer Dateien zurück.
    /// </summary>
    IReadOnlyList<string> GetRunningProcesses();
}