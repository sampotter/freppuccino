using System.Text.RegularExpressions;
using Freppuccino.UserFn;
using Xunit;

namespace UserFn.Tests;

/// Code shipped in docs/commands must compile (and translate to MSL/HLSL), so agents aren't taught broken syntax.
public class DocSampleTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Freppuccino.slnx"))) d = d.Parent;
        return d!.FullName;
    }

    public static IEnumerable<object[]> Samples()
    {
        var skill = File.ReadAllText(Path.Combine(Root(), ".claude/skills/freppuccino-ffunction/SKILL.md"));
        int i = 0;
        foreach (Match m in Regex.Matches(skill, "```glsl\\n(.*?)```", RegexOptions.Singleline))
            yield return [$"SKILL.md#{i++}", m.Groups[1].Value];
        var cmd = File.ReadAllText(Path.Combine(Root(), "src/Freppuccino/Commands/FFunctionCommand.cs"));
        var dc = Regex.Match(cmd, "DefaultCode = \"\"\"\\n(.*?)\"\"\";", RegexOptions.Singleline);
        Assert.True(dc.Success, "FFunctionCommand.DefaultCode not found");
        yield return ["FFunctionCommand.DefaultCode", dc.Groups[1].Value];
    }

    [Theory, MemberData(nameof(Samples))]
    public void Compiles(string name, string code)
    {
        var r = UserFnCompiler.Compile(code);
        Assert.True(r.Ok, name + ": " + string.Join("; ", r.Diagnostics));
        Assert.True(double.IsFinite(r.Program!.Cpu(0.3, 0.2, 0.1, [])), name);
    }
}
