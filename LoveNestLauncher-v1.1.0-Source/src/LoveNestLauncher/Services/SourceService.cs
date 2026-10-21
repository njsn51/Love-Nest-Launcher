using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using LoveNestLauncher.Common;
using LoveNestLauncher.Models;

namespace LoveNestLauncher.Services;

public sealed partial class SourceService
{
    private const string EmbeddedResourceName = "LoveNestLauncher.download-sources.txt";

    public bool HasCustomSource => File.Exists(AppPaths.CustomSourceFile);

    public async Task<SourceLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (HasCustomSource)
        {
            try
            {
                var customText = await File.ReadAllTextAsync(AppPaths.CustomSourceFile, cancellationToken);
                var customItems = Parse(customText);
                if (customItems.Count > 0)
                {
                    return new SourceLoadResult(customItems, true, "自定义下载源", AppPaths.CustomSourceFile);
                }

                LogService.Warn("自定义下载源中没有有效条目，已回退到内置下载源。");
            }
            catch (Exception exception)
            {
                LogService.Error("读取自定义下载源失败", exception);
            }
        }

        var embedded = await ReadEmbeddedAsync(cancellationToken);
        var items = Parse(embedded);
        return new SourceLoadResult(items, false, "内置下载源", "随启动器打包");
    }

    public async Task<SourceImportResult> ImportAsync(string sourceFile, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(sourceFile, cancellationToken);
        var items = Parse(text);
        if (items.Count == 0)
        {
            throw new InvalidDataException("没有识别到有效的下载条目。每行格式应为：名称    下载地址");
        }

        Directory.CreateDirectory(AppPaths.DataDirectory);
        var temporaryFile = AppPaths.CustomSourceFile + ".tmp";
        await File.WriteAllTextAsync(temporaryFile, text, new UTF8Encoding(false), cancellationToken);
        File.Move(temporaryFile, AppPaths.CustomSourceFile, overwrite: true);
        LogService.Info($"已导入下载源：{sourceFile}，共 {items.Count} 个条目。");
        return new SourceImportResult { Count = items.Count, FilePath = AppPaths.CustomSourceFile };
    }

    public void ResetToBuiltIn()
    {
        if (File.Exists(AppPaths.CustomSourceFile))
        {
            File.Delete(AppPaths.CustomSourceFile);
        }

        LogService.Info("已恢复内置下载源。");
    }

    public async Task<string> ReadCurrentSourceTextAsync(CancellationToken cancellationToken = default)
    {
        if (HasCustomSource)
        {
            return await File.ReadAllTextAsync(AppPaths.CustomSourceFile, cancellationToken);
        }

        return await ReadEmbeddedAsync(cancellationToken);
    }

    public static IReadOnlyList<DownloadDefinition> Parse(string text)
    {
        var results = new List<DownloadDefinition>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sequence = 0;

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//"))
            {
                continue;
            }

            var urlMatch = UrlAtEndRegex().Match(line);
            if (!urlMatch.Success)
            {
                continue;
            }

            var url = urlMatch.Value.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            if (!seenUrls.Add(url))
            {
                continue;
            }

            var name = line[..urlMatch.Index].Trim();
            if (name.Length == 0)
            {
                name = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            }

            if (name.Length == 0)
            {
                continue;
            }

            sequence++;
            var fileName = BuildFileName(uri, sequence);
            var fileType = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
            if (fileType.Length == 0)
            {
                fileType = "FILE";
            }

            results.Add(new DownloadDefinition(
                sequence,
                name,
                url,
                fileName,
                fileType,
                DetectCategory(name),
                DetectDescription(name)));
        }

        return results;
    }

    private static string BuildFileName(Uri uri, int sequence)
    {
        var decodedPath = Uri.UnescapeDataString(uri.AbsolutePath);
        var fileName = Path.GetFileName(decodedPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"love-nest-{sequence:00}.rar";
        }

        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidCharacter, '_');
        }

        var extension = Path.GetExtension(fileName);
        var namePart = Path.GetFileNameWithoutExtension(fileName);
        if (namePart.Length > 120)
        {
            fileName = namePart[..120] + extension;
        }

        return fileName;
    }

    private static string DetectCategory(string name)
    {
        if (name.Contains("二创", StringComparison.OrdinalIgnoreCase))
        {
            return "二创";
        }

        if (name.Contains("测试", StringComparison.OrdinalIgnoreCase)
            || name.Contains("弹窗", StringComparison.OrdinalIgnoreCase))
        {
            return "测试";
        }

        if (name.Contains("展会", StringComparison.OrdinalIgnoreCase))
        {
            return "展会";
        }

        return "Demo";
    }

    private static string DetectDescription(string name) => DetectCategory(name) switch
    {
        "二创" => "玩家二创 · 非官方版本",
        "测试" => "测试版本 · 内容可能存在变动",
        "展会" => "展会体验版 · 限定内容",
        _ => "爱之巢 Demo 版本 · 下载后即可体验"
    };

    private static async Task<string> ReadEmbeddedAsync(CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly();
        await using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
                                 ?? throw new InvalidOperationException("启动器内置下载源缺失。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    [GeneratedRegex(@"https?://\S+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlAtEndRegex();
}
