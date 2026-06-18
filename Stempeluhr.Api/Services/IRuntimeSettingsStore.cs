using Stempeluhr.Api.Models;

namespace Stempeluhr.Api.Services;

public interface IRuntimeSettingsStore
{
    RuntimeSettings Load();

    Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default);
}
