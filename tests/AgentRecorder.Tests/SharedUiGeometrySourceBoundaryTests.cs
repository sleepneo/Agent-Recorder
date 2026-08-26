using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Source/project boundary guard for the shared geometry project. This
/// complements the assembly-reference guard in SharedUiGeometryTests: a
/// platform API can enter source without creating a forbidden project
/// reference, so the source itself must be checked too.
/// </summary>
public sealed class SharedUiGeometrySourceBoundaryTests
{
    private static string SharedProjectDirectory =>
        Path.Combine(TestHelper.ProjectRoot, "src", "AgentRecorder.UI.Geometry");

    [Fact]
    public void SharedGeometryProject_HasPortableProjectShape()
    {
        var projectPath = Path.Combine(SharedProjectDirectory, "AgentRecorder.UI.Geometry.csproj");
        var document = XDocument.Load(projectPath);
        var elements = document.Descendants().ToArray();

        var targetFramework = elements
            .Single(element => element.Name.LocalName == "TargetFramework")
            .Value
            .Trim();

        Assert.Equal("net8.0", targetFramework);
        Assert.DoesNotContain(elements, element => element.Name.LocalName is "UseWindowsForms" or "UseWPF");
        Assert.DoesNotContain(elements, element => element.Name.LocalName == "ProjectReference");
    }

    [Fact]
    public void SharedGeometrySource_HasNoPlatformOrProjectBoundaryTokens()
    {
        var sourceFiles = Directory
            .EnumerateFiles(SharedProjectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(sourceFiles);

        var forbiddenTokens = new (string Name, string Pattern)[]
        {
            ("System.Windows.Forms", @"\bSystem\.Windows\.Forms\b"),
            ("System.Windows", @"\bSystem\.Windows(?:\.|\b)"),
            ("SystemInformation", @"\bSystemInformation\b"),
            ("SystemQuery", @"\bSystemQuery\b"),
            ("Screen", @"\bScreen\b"),
            ("Control", @"\bControl\b"),
            ("HWND", @"\bHWND\b"),
            ("IntPtr", @"\bIntPtr\b"),
            ("DllImport", @"\bDllImport\b"),
            ("LibraryImport", @"\bLibraryImport\b"),
            ("COM/Interop/Marshal", @"\b(?:ComImport|ComVisible|CoCreateInstance|IUnknown|IDispatch|COMException|Interop|Marshal)\b|\bActivator\.CreateInstance\b"),
            ("AgentRecorder.App", @"\bAgentRecorder\.App\b"),
            ("AgentRecorder.Windows", @"\bAgentRecorder\.Windows\b"),
            ("AgentRecorder.Capture", @"\bAgentRecorder\.Capture\b"),
            ("AgentRecorder.Api", @"\bAgentRecorder\.Api\b"),
            ("AgentRecorder.Core", @"\bAgentRecorder\.Core\b")
        };

        foreach (var sourcePath in sourceFiles)
        {
            var source = File.ReadAllText(sourcePath);
            var code = RemoveCommentsAndStringLiterals(source);
            foreach (var (name, pattern) in forbiddenTokens)
            {
                Assert.False(
                    Regex.IsMatch(code, pattern, RegexOptions.CultureInvariant),
                    $"{name} boundary token found in {sourcePath}");
            }
        }
    }

    private static string RemoveCommentsAndStringLiterals(string source)
    {
        var output = new StringBuilder(source.Length);
        var state = LexState.Code;

        for (int i = 0; i < source.Length; i++)
        {
            char current = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (state == LexState.Code)
            {
                if (current == '/' && next == '/')
                {
                    output.Append("  ");
                    i++;
                    state = LexState.LineComment;
                }
                else if (current == '/' && next == '*')
                {
                    output.Append("  ");
                    i++;
                    state = LexState.BlockComment;
                }
                else if (current == '"' || current == '\'')
                {
                    output.Append(' ');
                    state = current == '"' ? LexState.StringLiteral : LexState.CharLiteral;
                }
                else
                {
                    output.Append(current);
                }
            }
            else if (state == LexState.LineComment)
            {
                if (current == '\n')
                {
                    output.Append('\n');
                    state = LexState.Code;
                }
                else if (current == '\r')
                {
                    output.Append('\r');
                }
                else
                {
                    output.Append(' ');
                }
            }
            else if (state == LexState.BlockComment)
            {
                if (current == '*' && next == '/')
                {
                    output.Append("  ");
                    i++;
                    state = LexState.Code;
                }
                else if (current is '\n' or '\r')
                {
                    output.Append(current);
                }
                else
                {
                    output.Append(' ');
                }
            }
            else
            {
                if (current == '\\' && i + 1 < source.Length)
                {
                    output.Append("  ");
                    i++;
                }
                else if ((state == LexState.StringLiteral && current == '"') ||
                         (state == LexState.CharLiteral && current == '\''))
                {
                    output.Append(' ');
                    state = LexState.Code;
                }
                else
                {
                    output.Append(current is '\n' or '\r' ? current : ' ');
                }
            }
        }

        return output.ToString();
    }

    private enum LexState
    {
        Code,
        LineComment,
        BlockComment,
        StringLiteral,
        CharLiteral
    }
}
