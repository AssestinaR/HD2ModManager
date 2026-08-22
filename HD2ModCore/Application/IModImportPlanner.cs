using HD2ModCore.Domain;

namespace HD2ModCore.Application;

/// <summary>Scans an import source and produces an executable, auditable plan without writing the library.</summary>
public interface IModImportPlanner
{
    ValueTask<ImportPlan> PlanFolderAsync(string folderPath, CancellationToken cancellationToken = default);
    ValueTask<ImportPlan> PlanArchiveAsync(string archivePath, CancellationToken cancellationToken = default);
}
