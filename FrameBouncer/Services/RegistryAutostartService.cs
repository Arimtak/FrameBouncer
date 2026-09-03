using System;
using Microsoft.Win32;

namespace FrameBouncer.Services;

/// <summary>
/// Echte Autostart-Implementierung über den HKCU-Run-Key.
/// Benötigt keine erhöhten Rechte und gilt pro Benutzer.
/// </summary>
public class RegistryAutostartService : IAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FrameBouncer";

    public bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public void SetAutostart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return;

                // Immer auf die aktuelle EXE zeigen (Pfad kann sich nach Verschieben ändern)
                key.SetValue(ValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Registry nicht beschreibbar – still ignorieren, UI-Zustand bleibt unangetastet
        }
    }
}
