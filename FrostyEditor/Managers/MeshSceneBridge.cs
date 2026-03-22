using System;
using System.IO;
using System.Linq;
using Assimp;

namespace FrostyEditor.Managers;

public static class MeshSceneBridge
{
    public static MeshOperationResult ConvertObjToSceneFormat(string sourceObjPath, string destinationPath)
    {
        try
        {
            string extension = Path.GetExtension(destinationPath).ToLowerInvariant();
            if (extension != ".fbx")
            {
                return new MeshOperationResult(false, $"Unsupported mesh export format \"{extension}\".");
            }

            using AssimpContext context = new();
            string formatId = ResolveExportFormat(context, extension);
            context.ConvertFromFileToFile(
                sourceObjPath,
                destinationPath,
                formatId,
                PostProcessSteps.Triangulate | PostProcessSteps.ValidateDataStructure);

            return new MeshOperationResult(true, $"Exported mesh to {destinationPath}.");
        }
        catch (Exception ex)
        {
            return new MeshOperationResult(false, $"Failed to export scene mesh: {ex.Message}");
        }
    }

    public static MeshOperationResult ConvertSceneFormatToObj(string sourcePath, string destinationObjPath)
    {
        try
        {
            string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (extension != ".fbx")
            {
                return new MeshOperationResult(false, $"Unsupported mesh import format \"{extension}\".");
            }

            using AssimpContext context = new();
            string formatId = ResolveExportFormat(context, ".obj");
            context.ConvertFromFileToFile(
                sourcePath,
                destinationObjPath,
                formatId,
                PostProcessSteps.Triangulate | PostProcessSteps.ValidateDataStructure);

            return new MeshOperationResult(true, $"Converted mesh to temporary OBJ {destinationObjPath}.");
        }
        catch (Exception ex)
        {
            return new MeshOperationResult(false, $"Failed to convert scene mesh: {ex.Message}");
        }
    }

    private static string ResolveExportFormat(AssimpContext context, string extension)
    {
        string normalizedExtension = extension.TrimStart('.').ToLowerInvariant();
        ExportFormatDescription? format = context.GetSupportedExportFormats()
            .FirstOrDefault(candidate => string.Equals(candidate.FileExtension, normalizedExtension, StringComparison.OrdinalIgnoreCase));

        if (format is null)
        {
            throw new InvalidOperationException($"Assimp does not expose an exporter for .{normalizedExtension}.");
        }

        return format.FormatId;
    }
}
