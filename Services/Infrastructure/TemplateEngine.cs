using System.Text.RegularExpressions;

namespace YealinkAdmin.Services;

public class TemplateEngine
{
    public string ProcessTemplate(string template, Dictionary<string, string> variables)
    {
        var result = template;
        foreach (var (key, value) in variables)
        {
            result = result.Replace($"{{{key}}}", value);
        }
        return result;
    }

    public Dictionary<string, string> ExtractVariables(string template)
    {
        var matches = Regex.Matches(template, @"\{(\w+)\}");
        return matches.Select(m => m.Groups[1].Value).Distinct().ToDictionary(
            v => v,
            v => string.Empty);
    }
}