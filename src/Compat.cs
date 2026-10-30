using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MapVote;

/// <summary>
/// Game updates keep appending optional parameters to methods we call (MenuEffectClick gained <c>rumble</c>,
/// GetLocalizedString gained <c>_locale</c>). C# fills optional arguments in at compile time, so a direct call
/// built against older GameLibs throws MissingMethodException on a newer game, and Mono throws it when the
/// calling method is compiled, so a try/catch inside that method never sees it. These calls bind at runtime
/// instead and pad any trailing parameters the game added with their declared defaults.
/// </summary>
internal static class Compat
{
    private static readonly Dictionary<(Type, string, int), MethodInfo?> Methods = new();

    public static object? Call(object target, string name, params object?[] args)
    {
        var type = target.GetType();
        var key = (type, name, args.Length);
        if (!Methods.TryGetValue(key, out var method)) Methods[key] = method = Find(type, name, args.Length);
        if (method == null) throw new MissingMethodException(type.Name, name);

        var parameters = method.GetParameters();
        var full = new object?[parameters.Length];
        Array.Copy(args, full, args.Length);
        for (int i = args.Length; i < parameters.Length; i++) full[i] = parameters[i].DefaultValue;
        return method.Invoke(target, full);
    }

    /// <summary>The overload taking exactly <paramref name="given"/> arguments plus only defaulted extras; fewest extras wins.</summary>
    private static MethodInfo? Find(Type type, string name, int given) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.Name == name)
            .Select(m => (Method: m, Parameters: m.GetParameters()))
            .Where(x => x.Parameters.Length >= given && x.Parameters.Skip(given).All(p => p.HasDefaultValue))
            .OrderBy(x => x.Parameters.Length)
            .Select(x => x.Method)
            .FirstOrDefault();
}
