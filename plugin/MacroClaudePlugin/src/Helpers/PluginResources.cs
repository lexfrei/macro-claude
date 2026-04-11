using System;
using System.IO;
using System.Reflection;

namespace Loupedeck.MacroClaudePlugin;

// Thin static adapter over the plugin assembly's embedded resources.
// Initialized exactly once from the Plugin constructor, after which all
// accessors assume a non-null assembly.
internal static class PluginResources
{
    private static Assembly? _assembly;

    public static void Init(Assembly assembly)
    {
        assembly.CheckNullArgument(nameof(assembly));
        _assembly = assembly;
    }

    private static Assembly RequireAssembly()
        => _assembly ?? throw new InvalidOperationException("PluginResources.Init was not called before use.");

    // Retrieves the names of all the resource files in the specified folder.
    public static String[] GetFilesInFolder(String folderName)
        => RequireAssembly().GetFilesInFolder(folderName);

    // Finds the first resource file with the specified file name.
    public static String FindFile(String fileName)
        => RequireAssembly().FindFileOrThrow(fileName);

    // Finds all the resource files that match the specified regular expression pattern.
    public static String[] FindFiles(String regexPattern)
        => RequireAssembly().FindFiles(regexPattern);

    // Finds the first resource file with the specified file name, and returns the file as a stream.
    public static Stream GetStream(String resourceName)
        => RequireAssembly().GetStream(FindFile(resourceName));

    // Reads content of the specified text file, and returns the file content as a string.
    public static String ReadTextFile(String resourceName)
        => RequireAssembly().ReadTextFile(FindFile(resourceName));

    // Reads content of the specified binary file, and returns the file content as bytes.
    public static Byte[] ReadBinaryFile(String resourceName)
        => RequireAssembly().ReadBinaryFile(FindFile(resourceName));

    // Reads content of the specified image file, and returns the file content as a bitmap image.
    public static BitmapImage ReadImage(String resourceName)
        => RequireAssembly().ReadImage(FindFile(resourceName));

    // Extracts the specified resource file to the given file path in the file system.
    public static void ExtractFile(String resourceName, String filePathName)
        => RequireAssembly().ExtractFile(FindFile(resourceName), filePathName);
}
