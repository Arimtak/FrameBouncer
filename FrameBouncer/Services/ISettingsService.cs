using FrameBouncer.Models;

namespace FrameBouncer.Services;

public interface ISettingsService
{
    AppSettings Load();
    void Save(AppSettings settings);
}
